#pragma warning disable CA1416
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.PlayerTwo;
using BepInEx;
using UnityEngine;

namespace BALLxPITLocalCoop.Diagnostics
{
    /// <summary>
    /// Opt-in, zero-Harmony-overhead background stall detector.
    /// Pre-allocates all native CONTEXT and stack buffers BEFORE SuspendThread so it can never deadlock
    /// against the Windows heap lock or CoreCLR GC safepoints.
    /// </summary>
    internal static class HangWatchdog
    {
        private const int StallThresholdInGameMs = 3500;
        private const int StallThresholdMenuMs = 15000;
        private const int PollIntervalMs = 250;
        private const int MaxReportsPerSession = 3;
        private const int RingCapacity = 64;
        private const int MaxStackWords = 512;

        internal enum PhaseId : short
        {
            None = 0,
            HostUpdate = 1,
            HostLateUpdate = 2,
            GamepadPoll = 3,
            CoopModeUpdate = 4,
            P2Update = 5,
            P2LateUpdate = 6,
            P2Maintenance = 7,
            P2StartRunBalls = 8,
            CoopVitalsTick = 9,
            VitalsTick = 9,
            CoopCombatTick = 10,
            CombatTick = 10,
            CoopPresentationTick = 11,
            CoopRoundStatsTick = 12,
            CoopPauseTick = 13,
            CoopDecisionsTick = 14
        }

        private struct PhaseEvent
        {
            public long Ticks;
            public int Frame;
            public short Phase;
            public sbyte Kind;
        }

        internal readonly struct PhaseScope : IDisposable
        {
            private readonly PhaseId _phase;
            private readonly bool _active;

            public PhaseScope(PhaseId phase)
            {
                _phase = phase;
                _active = _running;
                if (_active)
                {
                    Record(phase, 1);
                }
            }

            public void Dispose()
            {
                if (_active)
                {
                    Record(_phase, -1);
                }
            }
        }

        private static readonly string[] PhaseNames =
        {
            "None",
            "CoopHost.Update",
            "CoopHost.LateUpdate",
            "GamepadRouter.Poll",
            "CoopMode.Update",
            "PlayerTwoRuntime.Update",
            "PlayerTwoRuntime.LateUpdate",
            "PlayerTwoPatches.RunP2Maintenance",
            "PlayerTwoPatches.StartP2RunBalls",
            "CoopVitals.Tick",
            "CoopCombat.Tick",
            "CoopHudAndResults.Tick",
            "CoopRoundStats.Tick",
            "CoopPause.Tick",
            "CoopDecisions.Tick"
        };

        private static readonly PhaseEvent[] Ring = new PhaseEvent[RingCapacity];
        private static readonly PhaseEvent[] RingSnapshot = new PhaseEvent[RingCapacity];
        private static readonly ulong[] StackBuffer = new ulong[MaxStackWords];
        private static readonly short[] PhaseStack = new short[32];
        private static readonly object ReportLock = new object();

        private static volatile bool _running;
        private static volatile bool _stopRequested;
        private static Thread _thread;
        private static ManualResetEventSlim _stopEvent;

        private static int _ringCursor;
        private static int _depth;
        private static volatile int _currentPhase;
        private static volatile int _lastExitedPhase;

        private static volatile uint _mainThreadId;
        private static long _lastBeatTicks;
        private static volatile int _lastBeatFrame;
        private static volatile bool _lastInGame;
        private static volatile bool _lastFocused;
        private static volatile int _lastGameState = -1;
        private static volatile bool _lastP2Context;
        private static volatile bool _lastP2Active;

        private static volatile bool _reportedThisStall;
        private static int _reportsWritten;

        public static bool IsRunning => _running;

        public static PhaseScope Enter(PhaseId phase) => new PhaseScope(phase);

        public static void Install(HarmonyLib.Harmony harmony = null, BepInEx.Logging.ManualLogSource log = null) => Start();

        public static void Start()
        {
            if (_running || !(ModConfig.HangWatchdogEnabled?.Value ?? false))
            {
                return;
            }
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                Plugin.Logger?.LogWarning("HangWatchdog skipped: x64 architecture required.");
                return;
            }

            _stopRequested = false;
            _stopEvent?.Dispose();
            _stopEvent = new ManualResetEventSlim(false);
            _reportsWritten = 0;
            _reportedThisStall = false;
            Volatile.Write(ref _lastBeatTicks, Stopwatch.GetTimestamp());
            _running = true;

            _thread = new Thread(MonitorLoop)
            {
                Name = "LocalCoopHangWatchdog",
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.AboveNormal
            };
            _thread.Start();
            Plugin.Logger?.LogInfo("HangWatchdog armed (opt-in, 3.5s focused in-game stall detection).");
        }

        public static void Shutdown()
        {
            _running = false;
            _stopRequested = true;
            ManualResetEventSlim ev = _stopEvent;
            try { ev?.Set(); } catch { }

            Thread thread = _thread;
            _thread = null;
            if (thread != null && thread.IsAlive)
            {
                try { thread.Join(1500); } catch { }
            }

            if (thread == null || !thread.IsAlive)
            {
                _stopEvent = null;
                try { ev?.Dispose(); } catch { }
            }
        }

        public static void Heartbeat(bool fullSnapshot = false)
        {
            if (!_running)
            {
                return;
            }
            if (_mainThreadId == 0)
            {
                _mainThreadId = GetCurrentThreadId();
            }

            Volatile.Write(ref _lastBeatTicks, Stopwatch.GetTimestamp());
            _reportedThisStall = false;

            try
            {
                _lastBeatFrame = Time.frameCount;
                _lastFocused = Application.isFocused;
                bool inGame = HarmonyHelper.IsInGameScene();
                _lastInGame = inGame;

                if (fullSnapshot)
                {
                    _lastGameState = GameMgr.I != null ? (int)GameMgr.I.CurState : -1;
                    _lastP2Context = CoopContext.IsPlayerTwoContext;
                    _lastP2Active = PlayerTwoRuntime.IsPlayerTwoActive();
                }
            }
            catch { }
        }

        private static void Record(PhaseId phase, sbyte kind)
        {
            int nextCursor = _ringCursor + 1;
            int idx = nextCursor & (RingCapacity - 1);
            Ring[idx].Ticks = Stopwatch.GetTimestamp();
            Ring[idx].Frame = _lastBeatFrame;
            Ring[idx].Phase = (short)phase;
            Ring[idx].Kind = kind;
            Volatile.Write(ref _ringCursor, nextCursor);

            if (kind > 0)
            {
                int d = _depth;
                if (d >= 0 && d < PhaseStack.Length)
                {
                    PhaseStack[d] = (short)phase;
                }
                _depth = d + 1;
                _currentPhase = (int)phase;
            }
            else
            {
                int d = Math.Max(0, _depth - 1);
                _depth = d;
                _currentPhase = d > 0 && d <= PhaseStack.Length ? PhaseStack[d - 1] : 0;
                _lastExitedPhase = (int)phase;
            }
        }

        private static void MonitorLoop()
        {
            while (!_stopRequested)
            {
                try
                {
                    ManualResetEventSlim ev = _stopEvent;
                    if (ev != null)
                    {
                        if (ev.Wait(PollIntervalMs))
                        {
                            break;
                        }
                    }
                    else
                    {
                        Thread.Sleep(PollIntervalMs);
                    }

                    if (_stopRequested)
                    {
                        break;
                    }

                    uint mainTid = _mainThreadId;
                    if (mainTid == 0 || _reportedThisStall || _reportsWritten >= MaxReportsPerSession)
                    {
                        continue;
                    }

                    // Do not trigger false-positive hang captures while window is unfocused / alt-tabbed.
                    if (!_lastFocused)
                    {
                        Volatile.Write(ref _lastBeatTicks, Stopwatch.GetTimestamp());
                        continue;
                    }

                    long lastBeat = Volatile.Read(ref _lastBeatTicks);
                    long now = Stopwatch.GetTimestamp();
                    long elapsedMs = (now - lastBeat) * 1000L / Stopwatch.Frequency;
                    int threshold = _lastInGame ? StallThresholdInGameMs : StallThresholdMenuMs;
                    if (elapsedMs < threshold)
                    {
                        continue;
                    }

                    _reportedThisStall = true;
                    _reportsWritten++;
                    CaptureStallReport(mainTid, elapsedMs, now);
                }
                catch { }
            }
        }

        private static void CaptureStallReport(uint mainTid, long elapsedMs, long nowTicks)
        {
            lock (ReportLock)
            {
                string bepinDir = !string.IsNullOrEmpty(Paths.BepInExRootPath)
                    ? Paths.BepInExRootPath
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx");
                string reportPath = Path.Combine(bepinDir, "hang_report.txt");
                string dumpPath = Path.Combine(bepinDir, $"hang_dump_{_reportsWritten}.dmp");

                bool gotCtx = false;
                uint ctxErr = 0;
                ulong rip = 0, rsp = 0, rbp = 0, rax = 0, rcx = 0, rdx = 0, rbx = 0;
                int stackCount = 0;
                int snapshotCursor = 0;
                int snapshotPhase = _currentPhase;
                int snapshotDepth = _depth;
                int snapshotExited = _lastExitedPhase;

                // Pre-allocate native CONTEXT buffer BEFORE SuspendThread to eliminate heap/GC deadlock risk.
                IntPtr rawBuf = Marshal.AllocHGlobal(1280);
                IntPtr aligned = new IntPtr((rawBuf.ToInt64() + 15L) & ~15L);
                for (int i = 0; i < 1232; i += 8)
                {
                    Marshal.WriteInt64(aligned, i, 0);
                }
                Marshal.WriteInt32(aligned, 0x30, 0x00100003); // CONTEXT_AMD64 | CONTROL | INTEGER

                IntPtr hThread = OpenThread(0x004A, false, mainTid);
                if (hThread != IntPtr.Zero)
                {
                    bool suspended = false;
                    try
                    {
                        if (SuspendThread(hThread) != 0xFFFFFFFFu)
                        {
                            suspended = true;
                            // Zero managed or native heap allocations permitted inside suspended block!
                            if (GetThreadContext(hThread, aligned))
                            {
                                gotCtx = true;
                                rax = (ulong)Marshal.ReadInt64(aligned, 0x78);
                                rcx = (ulong)Marshal.ReadInt64(aligned, 0x80);
                                rdx = (ulong)Marshal.ReadInt64(aligned, 0x88);
                                rbx = (ulong)Marshal.ReadInt64(aligned, 0x90);
                                rsp = (ulong)Marshal.ReadInt64(aligned, 0x98);
                                rbp = (ulong)Marshal.ReadInt64(aligned, 0xA0);
                                rip = (ulong)Marshal.ReadInt64(aligned, 0xF8);
                                stackCount = SampleStackIntoBuffer(rsp, StackBuffer);
                            }
                            else
                            {
                                ctxErr = (uint)Marshal.GetLastWin32Error();
                            }

                            snapshotCursor = Volatile.Read(ref _ringCursor);
                            snapshotPhase = _currentPhase;
                            snapshotDepth = _depth;
                            snapshotExited = _lastExitedPhase;
                            for (int i = 0; i < RingCapacity; i++)
                            {
                                RingSnapshot[i] = Ring[i];
                            }
                        }
                        else
                        {
                            ctxErr = (uint)Marshal.GetLastWin32Error();
                        }
                    }
                    finally
                    {
                        if (suspended)
                        {
                            ResumeThread(hThread);
                        }
                        CloseHandle(hThread);
                    }
                }
                else
                {
                    ctxErr = (uint)Marshal.GetLastWin32Error();
                }

                Marshal.FreeHGlobal(rawBuf);

                var sb = new StringBuilder(4096);
                sb.AppendLine($"=== HangWatchdog Capture #{_reportsWritten} @ {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
                sb.AppendLine($"StalledMs={elapsedMs}, MainTID={mainTid}, Frame={_lastBeatFrame}, InGame={_lastInGame}, Focused={_lastFocused}, GameState={_lastGameState}");
                sb.AppendLine($"IsP2Ctx={_lastP2Context}, P2Active={_lastP2Active}, ActivePhase={PhaseName(snapshotPhase)} (depth={snapshotDepth}), LastExited={PhaseName(snapshotExited)}");

                using Process proc = Process.GetCurrentProcess();
                ProcessModuleCollection modules = null;
                try { modules = proc.Modules; } catch { }

                if (gotCtx)
                {
                    sb.AppendLine($"Context: RIP=0x{rip:X16} ({FormatAddr(rip, modules) ?? "JIT/unknown"})");
                    sb.AppendLine($"         RSP=0x{rsp:X16}, RBP=0x{rbp:X16}, RAX=0x{rax:X16}, RCX=0x{rcx:X16}, RDX=0x{rdx:X16}, RBX=0x{rbx:X16}");
                    sb.AppendLine("Stack Module Hits:");
                    int hits = 0;
                    for (int i = 0; i < stackCount; i++)
                    {
                        string sym = FormatAddr(StackBuffer[i], modules);
                        if (sym != null)
                        {
                            sb.AppendLine($"  [RSP+0x{i * 8:X3}] 0x{StackBuffer[i]:X16} -> {sym}");
                            if (++hits >= 64) break;
                        }
                    }
                }
                else
                {
                    sb.AppendLine($"Context capture failed: Win32Error={ctxErr}");
                }

                sb.AppendLine("Recent Phase Ring (oldest -> newest):");
                for (int offset = RingCapacity - 1; offset >= 0; offset--)
                {
                    int idx = (snapshotCursor - offset) & (RingCapacity - 1);
                    PhaseEvent ev = RingSnapshot[idx];
                    if (ev.Ticks == 0) continue;
                    long agoMs = (nowTicks - ev.Ticks) * 1000L / Stopwatch.Frequency;
                    sb.AppendLine($"  [-{agoMs,5}ms] frame={ev.Frame} {(ev.Kind > 0 ? "ENTER" : "EXIT ")} {PhaseName(ev.Phase)}");
                }

                if (modules != null)
                {
                    sb.AppendLine("Key Modules:");
                    foreach (ProcessModule m in modules)
                    {
                        try
                        {
                            string n = m.ModuleName;
                            if (n.Contains("GameAssembly", StringComparison.OrdinalIgnoreCase)
                                || n.Contains("UnityPlayer", StringComparison.OrdinalIgnoreCase)
                                || n.Contains("coreclr", StringComparison.OrdinalIgnoreCase)
                                || n.Contains("Balls", StringComparison.OrdinalIgnoreCase))
                            {
                                sb.AppendLine($"  {n}: Base=0x{m.BaseAddress.ToInt64():X16}, Size=0x{m.ModuleMemorySize:X}");
                            }
                        }
                        catch { }
                    }
                }

                sb.AppendLine();
                try
                {
                    // Cap report file size so it doesn't grow unboundedly across sessions.
                    if (File.Exists(reportPath) && new FileInfo(reportPath).Length > 512 * 1024)
                    {
                        File.WriteAllText(reportPath, sb.ToString());
                    }
                    else
                    {
                        File.AppendAllText(reportPath, sb.ToString());
                    }
                }
                catch { }

                if (ModConfig.HangWatchdogWriteDump?.Value ?? false)
                {
                    try
                    {
                        using var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), fs.SafeFileHandle.DangerousGetHandle(), 0x00001000u, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch { }
                }
            }
        }

        private static int SampleStackIntoBuffer(ulong rsp, ulong[] destination)
        {
            if (rsp < 0x10000)
            {
                return 0;
            }
            int written = 0;
            ulong verifiedPage = ulong.MaxValue;
            for (int i = 0; i < destination.Length; i++)
            {
                ulong addr = rsp + (ulong)(i * 8);
                ulong firstPage = addr >> 12;
                ulong lastPage = (addr + 7) >> 12;
                if (firstPage != verifiedPage)
                {
                    if (!IsReadableAddress(new IntPtr((long)addr))) break;
                    verifiedPage = firstPage;
                }
                if (lastPage != verifiedPage)
                {
                    if (!IsReadableAddress(new IntPtr((long)(addr + 7)))) break;
                    verifiedPage = lastPage;
                }
                destination[written++] = (ulong)Marshal.ReadInt64(new IntPtr((long)addr));
            }
            return written;
        }

        private static bool IsReadableAddress(IntPtr addr)
        {
            if (VirtualQuery(addr, out MEMORY_BASIC_INFORMATION mbi, (UIntPtr)48) == UIntPtr.Zero)
            {
                return false;
            }
            if (mbi.State != 0x1000u) return false;
            uint p = mbi.Protect & 0xFFu;
            bool readable = p == 0x02u || p == 0x04u || p == 0x08u || p == 0x20u || p == 0x40u || p == 0x80u;
            bool guard = (mbi.Protect & 0x100u) != 0;
            return readable && !guard;
        }

        private static string FormatAddr(ulong addr, ProcessModuleCollection modules)
        {
            if (modules == null || addr < 0x10000) return null;
            foreach (ProcessModule m in modules)
            {
                try
                {
                    ulong b = (ulong)m.BaseAddress.ToInt64();
                    ulong e = b + (ulong)(uint)m.ModuleMemorySize;
                    if (addr >= b && addr < e)
                    {
                        return $"{m.ModuleName}+0x{addr - b:X}";
                    }
                }
                catch { }
            }
            return null;
        }

        private static string PhaseName(int phase) =>
            phase >= 0 && phase < PhaseNames.Length ? PhaseNames[phase] : $"Unknown({phase})";

        [StructLayout(LayoutKind.Explicit, Size = 48)]
        private struct MEMORY_BASIC_INFORMATION
        {
            [FieldOffset(0)] public IntPtr BaseAddress;
            [FieldOffset(8)] public IntPtr AllocationBase;
            [FieldOffset(16)] public uint AllocationProtect;
            [FieldOffset(20)] public ushort PartitionId;
            [FieldOffset(24)] public IntPtr RegionSize;
            [FieldOffset(32)] public uint State;
            [FieldOffset(36)] public uint Protect;
            [FieldOffset(40)] public uint Type;
        }

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(IntPtr hThread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr hThread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(IntPtr hThread, IntPtr lpContext);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll")] private static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);
        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, IntPtr hFile, uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
    }
}
