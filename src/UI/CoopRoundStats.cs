using System;
using System.Collections.Generic;
using System.Reflection;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.PlayerTwo;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace BALLxPITLocalCoop.UI
{
    /// <summary>
    /// Zero-allocation per-player combat statistics (64-bit damage, boss damage, kills, pickups,
    /// monotonic live DPS buckets, fusion-safe per-HeroInst native reconciliation, and status effect attribution).
    /// </summary>
    public static class CoopRoundStats
    {
        private struct AttributionScope
        {
            public int OldSource;
            public bool Status;
            public bool Active;
            public CoopContext.ScopeToken Context;

            public void Close()
            {
                if (!Active) return;
                Active = false;
                CoopContext.Exit(Context);
                _source = OldSource;
                if (Status)
                {
                    _statusDepth = Math.Max(0, _statusDepth - 1);
                }
            }
        }

        private struct HitSnapshot
        {
            public long BeforeHealth;
            public int Owner;
            public int OldSource;
            public bool Active;
        }

        public static readonly long[] Damage = new long[2];
        public static readonly long[] BossDamage = new long[2];
        public static readonly int[] Kills = new int[2];
        public static readonly int[] PickupsCollected = new int[2];

        public static long TeamDamage { get; private set; }
        public static int TeamKills { get; private set; }
        public static bool Complete { get; set; }
        public static bool Victory { get; private set; }

        private const int DpsBucketCount = 8;
        private const float DpsBucketSpan = 0.5f;
        private static readonly long[,] DpsBuckets = new long[2, DpsBucketCount];
        private static readonly int[] DpsLastBucket = { -1, -1 };
        private static float _monotonicDpsClock;

        private static bool _installed;
        private static int _accountedKills;
        private static int _source = -1;
        private static int _statusDepth;
        private static float _nextNativeReconcileAt;

        // Per-HeroInst delta tracking so fusing/evolving heroes never freezes cumulative slot damage/kills!
        private static readonly Dictionary<IntPtr, long> LastHeroNativeDmg = new Dictionary<IntPtr, long>(32);
        private static readonly Dictionary<IntPtr, int> LastHeroNativeKills = new Dictionary<IntPtr, int>(32);

        private static readonly List<IntPtr> DeadPiecesToPrune = new List<IntPtr>(32);
        private static readonly Dictionary<IntPtr, int> LastDamager = new Dictionary<IntPtr, int>(128);
        private static readonly Dictionary<IntPtr, int> Balls = new Dictionary<IntPtr, int>(256);
        private static readonly Dictionary<IntPtr, int> Heroes = new Dictionary<IntPtr, int>(32);

        private static Type _damageRoutineNestedType;
        private static PropertyInfo _routineCtxProp;
        private static PropertyInfo _routineBallProp;
        private static PropertyInfo _routineHeroProp;

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            foreach (MethodInfo m in typeof(GridMgr).GetMethods())
            {
                if (!m.Name.StartsWith("Damage", StringComparison.Ordinal) && m.Name != nameof(GridMgr.ApplyHitEffects))
                {
                    continue;
                }
                ParameterInfo[] ps = m.GetParameters();
                string prefixName = null;
                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    if (pt == typeof(DamageContext) && i == 1) { prefixName = nameof(OnDamageCtxArg1); break; }
                    if (pt == typeof(BallObj))
                    {
                        if (i == 1) prefixName = nameof(OnBallArg1);
                        else if (i == 3) prefixName = nameof(OnBallArg3);
                        else if (i == 5) prefixName = nameof(OnBallArg5);
                        if (prefixName != null) break;
                    }
                    if (pt == typeof(HeroInst))
                    {
                        if (i == 1) prefixName = nameof(OnHeroArg1);
                        else if (i == 3) prefixName = nameof(OnHeroArg3);
                        else if (i == 5) prefixName = nameof(OnHeroArg5);
                        if (prefixName != null) break;
                    }
                }
                if (prefixName != null)
                {
                    HarmonyHelper.TryPatch(
                        harmony,
                        m,
                        prefix: new HarmonyMethod(typeof(CoopRoundStats), prefixName),
                        finalizer: new HarmonyMethod(typeof(CoopRoundStats), nameof(EndScope)),
                        label: $"GridMgr.{m.Name} (CoopRoundStats)");
                }
            }

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GridMgr), nameof(GridMgr._DamagePieceRoutineNew)),
                prefix: new HarmonyMethod(typeof(CoopRoundStats), nameof(OnDamageCtxArg1)),
                finalizer: new HarmonyMethod(typeof(CoopRoundStats), nameof(EndScope)),
                label: "GridMgr._DamagePieceRoutineNew");

            Type nested = HarmonyHelper.FindNestedCoroutineType(typeof(GridMgr), "DamagePieceRoutine", "GridMgr+__DamagePieceRoutineNew_d__72");
            if (nested != null)
            {
                MethodInfo moveNext = AccessTools.Method(nested, "MoveNext");
                if (moveNext != null)
                {
                    _damageRoutineNestedType = nested;
                    CacheDamageRoutineMembers(nested);
                    HarmonyHelper.TryPatch(
                        harmony,
                        moveNext,
                        prefix: new HarmonyMethod(typeof(CoopRoundStats), nameof(OnDamageRoutine)),
                        finalizer: new HarmonyMethod(typeof(CoopRoundStats), nameof(EndScope)),
                        label: "GridMgr.DamagePieceRoutine.MoveNext");
                }
            }

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GridPieceInst), nameof(GridPieceInst.OnKilled)),
                prefix: new HarmonyMethod(typeof(CoopRoundStats), nameof(CaptureKillOwner)),
                postfix: new HarmonyMethod(typeof(CoopRoundStats), nameof(FinishKillOwner)),
                label: "GridPieceInst.OnKilled (CoopRoundStats)");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GridPieceObj), nameof(GridPieceObj.DropDeathStuff)),
                prefix: new HarmonyMethod(typeof(CoopRoundStats), nameof(CaptureDeathDropOwner)),
                postfix: new HarmonyMethod(typeof(CoopRoundStats), nameof(FinishKillOwner)),
                label: "GridPieceObj.DropDeathStuff");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(BallMgr), nameof(BallMgr.CreateBall), new[] { typeof(HeroInst), typeof(bool), typeof(Vector2), typeof(BallSourceType) }),
                postfix: new HarmonyMethod(typeof(CoopRoundStats), nameof(OnBallCreated)),
                label: "BallMgr.CreateBall (CoopRoundStats)");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(BallMgr), nameof(BallMgr.MoveBall)),
                prefix: new HarmonyMethod(typeof(CoopRoundStats), nameof(OnMovingBall)),
                finalizer: new HarmonyMethod(typeof(CoopRoundStats), nameof(EndScope)),
                label: "BallMgr.MoveBall (CoopRoundStats)");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GridMgr), nameof(GridMgr.UpdatePieceStatusEffects)),
                prefix: new HarmonyMethod(typeof(CoopRoundStats), nameof(OnStatusTick)),
                finalizer: new HarmonyMethod(typeof(CoopRoundStats), nameof(EndScope)),
                label: "GridMgr.UpdatePieceStatusEffects");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GridPieceInst), nameof(GridPieceInst.ApplyStatusEffect)),
                prefix: new HarmonyMethod(typeof(CoopRoundStats), nameof(OnApplyStatusEffect)),
                label: "GridPieceInst.ApplyStatusEffect");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GridPieceInst), nameof(GridPieceInst.Damage), new[] { typeof(int), typeof(DamageType), typeof(HitType) }),
                prefix: new HarmonyMethod(typeof(CoopRoundStats), nameof(BeforePieceDamage)),
                postfix: new HarmonyMethod(typeof(CoopRoundStats), nameof(AfterPieceDamage)),
                finalizer: new HarmonyMethod(typeof(CoopRoundStats), nameof(FinalizePieceDamage)),
                label: "GridPieceInst.Damage (CoopRoundStats)");
        }

        public static void Shutdown()
        {
            Reset();
            _damageRoutineNestedType = null;
            _routineCtxProp = null;
            _routineBallProp = null;
            _routineHeroProp = null;
            _installed = false;
        }

        public static void ResetRound() => Reset();

        public static void InvalidateHeroCache()
        {
            Heroes.Clear();
        }

        public static void Reset()
        {
            Array.Clear(Damage, 0, 2);
            Array.Clear(BossDamage, 0, 2);
            Array.Clear(Kills, 0, 2);
            Array.Clear(PickupsCollected, 0, 2);
            Array.Clear(DpsBuckets, 0, DpsBuckets.Length);
            DpsLastBucket[0] = DpsLastBucket[1] = -1;
            _monotonicDpsClock = 0f;
            TeamDamage = 0L;
            TeamKills = 0;
            Complete = false;
            Victory = false;
            Heroes.Clear();
            Balls.Clear();
            LastDamager.Clear();
            DeadPiecesToPrune.Clear();
            LastHeroNativeDmg.Clear();
            LastHeroNativeKills.Clear();
            _nextNativeReconcileAt = 0f;
            _accountedKills = 0;
            _source = -1;
            _statusDepth = 0;
        }

        private static float CurrentBattleClock()
        {
            float elapsed = BattleSaveData.I != null ? BattleSaveData.I.ElapsedTime : 0f;
            if (elapsed > _monotonicDpsClock)
            {
                _monotonicDpsClock = elapsed;
            }
            return _monotonicDpsClock;
        }

        private static void AdvanceDpsBuckets(int slot, int absBucket)
        {
            int prev = DpsLastBucket[slot];
            if (prev < 0)
            {
                DpsLastBucket[slot] = absBucket;
                return;
            }
            int delta = absBucket - prev;
            if (delta <= 0)
            {
                return;
            }
            if (delta >= DpsBucketCount)
            {
                for (int b = 0; b < DpsBucketCount; b++)
                {
                    DpsBuckets[slot, b] = 0L;
                }
            }
            else
            {
                for (int step = 1; step <= delta; step++)
                {
                    DpsBuckets[slot, (prev + step) % DpsBucketCount] = 0L;
                }
            }
            DpsLastBucket[slot] = absBucket;
        }

        private static void AddDpsSample(int slot, long dmg)
        {
            if (slot < 0 || slot > 1 || dmg <= 0L) return;
            float now = CurrentBattleClock();
            int absBucket = Math.Max(0, (int)Math.Floor(now / DpsBucketSpan));
            AdvanceDpsBuckets(slot, absBucket);
            DpsBuckets[slot, absBucket % DpsBucketCount] += dmg;
        }

        public static long GetLiveDps(int slot)
        {
            if (slot < 0 || slot > 1) return 0L;
            float now = CurrentBattleClock();
            int absBucket = Math.Max(0, (int)Math.Floor(now / DpsBucketSpan));
            AdvanceDpsBuckets(slot, absBucket);
            long windowSum = 0L;
            for (int b = 0; b < DpsBucketCount; b++)
            {
                windowSum += DpsBuckets[slot, b];
            }
            float windowSeconds = Math.Clamp(now > 0.5f ? now : DpsBucketCount * DpsBucketSpan, 1f, DpsBucketCount * DpsBucketSpan);
            return (long)Math.Round(windowSum / windowSeconds);
        }

        public static string FormatDps(long dps)
        {
            if (dps >= 1_000_000L) return (dps / 1_000_000.0).ToString("0.0") + "M/s";
            if (dps >= 10_000L) return (dps / 1_000.0).ToString("0") + "k/s";
            if (dps >= 1_000L) return (dps / 1_000.0).ToString("0.0") + "k/s";
            return dps + "/s";
        }

        public static int HeroOwner(HeroInst hero)
        {
            IntPtr ptr = HarmonyHelper.Ptr(hero);
            if (ptr == IntPtr.Zero) return -1;
            if (Heroes.TryGetValue(ptr, out int cached)) return cached;

            if (HeroInventoryIsolation.IsPlayerTwoHero(hero))
            {
                Heroes[ptr] = 1;
                return 1;
            }
            if (HeroInventoryIsolation.IsPlayerOneHero(hero))
            {
                Heroes[ptr] = 0;
                return 0;
            }

            for (int slot = 0; slot < 2; slot++)
            {
                var list = CoopCharacterRuntime.HeroesFor(slot);
                if (list == null) continue;
                for (int j = 0; j < list.Count; j++)
                {
                    if (HarmonyHelper.Ptr(list[j]) == ptr)
                    {
                        Heroes[ptr] = slot;
                        return slot;
                    }
                }
            }
            return -1;
        }

        private static void OnBallCreated(HeroInst __0, BallObj __result)
        {
            if (!CoopMode.Enabled) return;

            int owner = CoopContext.IsPlayerTwoContext ? 1 : 0;
            if (__0 != null)
            {
                int heroOwner = HeroOwner(__0);
                if (heroOwner >= 0) owner = heroOwner;
                IntPtr hPtr = HarmonyHelper.Ptr(__0);
                if (hPtr != IntPtr.Zero) Heroes[hPtr] = owner;
            }
            IntPtr bPtr = HarmonyHelper.Ptr(__result);
            if (bPtr != IntPtr.Zero)
            {
                if (CoopCharacterRuntime.IndependentReady && PlayerTwoPatches.IsPlayerTwoBall(__result))
                {
                    owner = 1;
                }
                if (Balls.Count > 1024) Balls.Clear();
                Balls[bPtr] = owner;
            }
        }

        public static void NotifyBallOwnership(IntPtr ptr, int owner)
        {
            if (ptr == IntPtr.Zero) return;
            if (Balls.Count > 1024) Balls.Clear();
            Balls[ptr] = owner == 1 ? 1 : 0;
        }

        public static void ForgetBall(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return;
            Balls.Remove(ptr);
        }

        public static int BallOwner(BallObj ball)
        {
            IntPtr ptr = HarmonyHelper.Ptr(ball);
            if (ptr == IntPtr.Zero) return -1;

            if (CoopCharacterRuntime.IndependentReady)
            {
                int activeOwner = PlayerTwoPatches.IsPlayerTwoBall(ball) ? 1 : 0;
                if (Balls.Count > 1024) Balls.Clear();
                Balls[ptr] = activeOwner;
                return activeOwner;
            }
            if (Balls.TryGetValue(ptr, out int owner) && owner >= 0)
            {
                return owner;
            }
            owner = HeroOwner(ball.Inst);
            if (owner < 0) owner = HeroOwner(ball.ParentInst);
            if (owner >= 0)
            {
                if (Balls.Count > 1024) Balls.Clear();
                Balls[ptr] = owner;
            }
            return owner;
        }

        private static int ContextOwner(DamageContext ctx)
        {
            if (ctx == null) return -1;
            int owner = BallOwner(ctx.TgtBall);
            if (owner >= 0) return owner;
            owner = HeroOwner(ctx.TgtHero);
            if (owner >= 0) return owner;
            return HeroOwner(ctx.BallParent);
        }

        private static void BeginOwnerScope(int owner, out AttributionScope state)
        {
            if (!CoopMode.Enabled)
            {
                state = default;
                return;
            }
            state = new AttributionScope { OldSource = _source, Active = true };
            if (owner >= 0)
            {
                _source = owner;
                if (owner == 1 && CoopCharacterRuntime.IndependentReady && !CoopContext.IsFullPlayerTwoContext)
                {
                    Player p2 = CoopCombat.PlayerAt(1);
                    if (p2 != null)
                    {
                        state.Context = CoopContext.EnterPlayerTwo(p2, CoopContext.ScopeKind.Full);
                    }
                }
            }
        }

        private static void CacheDamageRoutineMembers(Type nested)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo[] props = nested.GetProperties(flags);
            for (int i = 0; i < props.Length; i++)
            {
                PropertyInfo p = props[i];
                if (_routineCtxProp == null && (p.Name == "context" || typeof(DamageContext).IsAssignableFrom(p.PropertyType)))
                    _routineCtxProp = p;
                if (_routineBallProp == null && (p.Name == "_b_5__4" || typeof(BallObj).IsAssignableFrom(p.PropertyType)))
                    _routineBallProp = p;
                if (_routineHeroProp == null && (p.Name == "_h_5__3" || typeof(HeroInst).IsAssignableFrom(p.PropertyType)))
                    _routineHeroProp = p;
            }
        }

        private static void OnDamageCtxArg1(DamageContext __1, out AttributionScope __state)
        {
            if (!CoopMode.Enabled) { __state = default; return; }
            BeginOwnerScope(ContextOwner(__1), out __state);
        }

        private static void OnBallArg1(BallObj __1, out AttributionScope __state)
        {
            if (!CoopMode.Enabled) { __state = default; return; }
            BeginOwnerScope(BallOwner(__1), out __state);
        }

        private static void OnBallArg3(BallObj __3, out AttributionScope __state)
        {
            if (!CoopMode.Enabled) { __state = default; return; }
            BeginOwnerScope(BallOwner(__3), out __state);
        }

        private static void OnBallArg5(BallObj __5, out AttributionScope __state)
        {
            if (!CoopMode.Enabled) { __state = default; return; }
            BeginOwnerScope(BallOwner(__5), out __state);
        }

        private static void OnHeroArg1(HeroInst __1, out AttributionScope __state)
        {
            if (!CoopMode.Enabled) { __state = default; return; }
            BeginOwnerScope(HeroOwner(__1), out __state);
        }

        private static void OnHeroArg3(HeroInst __3, out AttributionScope __state)
        {
            if (!CoopMode.Enabled) { __state = default; return; }
            BeginOwnerScope(HeroOwner(__3), out __state);
        }

        private static void OnHeroArg5(HeroInst __5, out AttributionScope __state)
        {
            if (!CoopMode.Enabled) { __state = default; return; }
            BeginOwnerScope(HeroOwner(__5), out __state);
        }

        private static void OnDamageRoutine(Il2CppObjectBase __instance, out AttributionScope __state)
        {
            if (!CoopMode.Enabled || __instance == null)
            {
                __state = default;
                return;
            }

            int owner = -1;
            var routine = __instance.TryCast<GridMgr.__DamagePieceRoutineNew_d__72>();
            if (routine != null)
            {
                owner = ContextOwner(routine.context);
                if (owner < 0) owner = BallOwner(routine._b_5__4);
                if (owner < 0) owner = HeroOwner(routine._h_5__3);
            }
            else if (_damageRoutineNestedType != null && __instance.Pointer != IntPtr.Zero)
            {
                try
                {
                    object dynamicRoutine = Activator.CreateInstance(_damageRoutineNestedType, __instance.Pointer);
                    if (dynamicRoutine != null)
                    {
                        if (_routineCtxProp != null) owner = ContextOwner(_routineCtxProp.GetValue(dynamicRoutine) as DamageContext);
                        if (owner < 0 && _routineBallProp != null) owner = BallOwner(_routineBallProp.GetValue(dynamicRoutine) as BallObj);
                        if (owner < 0 && _routineHeroProp != null) owner = HeroOwner(_routineHeroProp.GetValue(dynamicRoutine) as HeroInst);
                    }
                }
                catch { }
            }

            BeginOwnerScope(owner, out __state);
        }

        private static void CaptureKillOwner(GridPieceInst __instance, out int __state)
        {
            if (!CoopMode.Enabled)
            {
                __state = -1;
                return;
            }
            __state = _source;
            IntPtr ptr = HarmonyHelper.Ptr(__instance);
            if (ptr != IntPtr.Zero)
            {
                if (__state < 0 && LastDamager.TryGetValue(ptr, out int last))
                {
                    __state = last;
                }
                DeadPiecesToPrune.Add(ptr);
            }
        }

        private static void CaptureDeathDropOwner(GridPieceObj __instance, out int __state)
        {
            if (!CoopMode.Enabled)
            {
                __state = -1;
                return;
            }
            __state = _source;
            IntPtr ptr = HarmonyHelper.Ptr(__instance?.Inst);
            if (__state < 0 && ptr != IntPtr.Zero && LastDamager.TryGetValue(ptr, out int last))
            {
                __state = last;
            }
        }

        private static void FinishKillOwner(int __state)
        {
            if (CoopMode.Enabled)
            {
                ReconcileKills(__state, forceNative: true);
            }
        }

        private static void ReconcileKills(int owner, bool forceNative = false)
        {
            if (!CoopMode.Enabled || Complete || BattleSaveData.I == null) return;

            if (forceNative || Time.unscaledTime >= _nextNativeReconcileAt)
            {
                _nextNativeReconcileAt = Time.unscaledTime + 0.1f;
                ReconcileNativeStats();
            }

            int totalKills = BattleSaveData.I.NumKills;
            int delta = totalKills - _accountedKills;
            if (delta > 0)
            {
                _accountedKills = totalKills;
                if (owner >= 0 && owner < 2)
                {
                    Kills[owner] += delta;
                }
                else
                {
                    TeamKills += delta;
                }
            }
        }

        private static void ReconcileNativeStats()
        {
            for (int slot = 0; slot < 2; slot++)
            {
                var heroes = CoopCharacterRuntime.HeroesFor(slot);
                if (heroes == null) continue;

                long slotDmgDelta = 0L;
                int slotKillDelta = 0;

                for (int i = 0; i < heroes.Count; i++)
                {
                    HeroInst hero = heroes[i];
                    IntPtr hPtr = HarmonyHelper.Ptr(hero);
                    if (hPtr == IntPtr.Zero || hero.StatData == null) continue;

                    long heroDmg = 0L;
                    int heroKills = 0;
                    for (int j = 0; j < hero.StatData.Count; j++)
                    {
                        HeroStatData st = hero.StatData[j];
                        if (st == null) continue;
                        heroDmg += Math.Max(0L, st.BounceDmgDealt)
                            + Math.Max(0L, st.StatusEffectDmgDealt)
                            + Math.Max(0L, st.AltDmgDealt)
                            + Math.Max(0L, st.AOEDmgDealt);
                        heroKills += Math.Max(0, st.NumKills);
                    }

                    LastHeroNativeDmg.TryGetValue(hPtr, out long prevHeroDmg);
                    if (heroDmg > prevHeroDmg)
                    {
                        slotDmgDelta += (heroDmg - prevHeroDmg);
                        LastHeroNativeDmg[hPtr] = heroDmg;
                    }

                    LastHeroNativeKills.TryGetValue(hPtr, out int prevHeroKills);
                    if (heroKills > prevHeroKills)
                    {
                        slotKillDelta += (heroKills - prevHeroKills);
                        LastHeroNativeKills[hPtr] = heroKills;
                    }
                }

                // Only fold in native deltas that weren't already counted by Before/AfterPieceDamage (reconciles unattributed TeamDamage/TeamKills).
                if (slotDmgDelta > 0L && TeamDamage > 0L)
                {
                    long transfer = Math.Min(TeamDamage, slotDmgDelta);
                    Damage[slot] += transfer;
                    AddDpsSample(slot, transfer);
                    TeamDamage -= transfer;
                }
                if (slotKillDelta > 0 && TeamKills > 0)
                {
                    int transfer = Math.Min(TeamKills, slotKillDelta);
                    Kills[slot] += transfer;
                    TeamKills -= transfer;
                    _accountedKills = Math.Max(_accountedKills, Kills[0] + Kills[1] + TeamKills);
                }
            }
        }

        public static void Tick()
        {
            if (!CoopMode.Enabled) return;

            if (DeadPiecesToPrune.Count > 0)
            {
                for (int i = 0; i < DeadPiecesToPrune.Count; i++)
                {
                    LastDamager.Remove(DeadPiecesToPrune[i]);
                }
                DeadPiecesToPrune.Clear();
            }
            ReconcileKills(-1, forceNative: false);
        }

        private static void OnMovingBall(BallObj __0, out AttributionScope __state)
        {
            if (!CoopMode.Enabled || __0 == null)
            {
                __state = default;
                return;
            }
            __state = new AttributionScope { OldSource = _source, Active = true };
            _source = BallOwner(__0);
        }

        private static void OnStatusTick(out AttributionScope __state)
        {
            if (!CoopMode.Enabled)
            {
                __state = default;
                return;
            }
            __state = new AttributionScope { OldSource = _source, Status = true, Active = true };
            _statusDepth++;
            _source = -1;
        }

        private static void EndScope(ref AttributionScope __state) => __state.Close();

        private static void OnApplyStatusEffect(HeroInst __1)
        {
            if (!CoopMode.Enabled || __1 == null) return;
            int owner = HeroOwner(__1);
            IntPtr ptr = HarmonyHelper.Ptr(__1);
            if (owner >= 0 && ptr != IntPtr.Zero)
            {
                Heroes[ptr] = owner;
            }
        }

        private static void BeforePieceDamage(GridPieceInst __instance, DamageType __1, out HitSnapshot __state)
        {
            __state = default;
            if (!CoopMode.Enabled || Complete || __instance == null || __instance.CurHealth <= 0L || CoopBalance.IsAllyPiece(__instance.Type))
            {
                return;
            }

            int owner = _source;
            if (_statusDepth > 0 && __instance.StatusEffects != null)
            {
                var e = __instance.StatusEffects.GetEnumerator();
                while (e.MoveNext())
                {
                    StatusEffect fx = e.Current;
                    if ((__1 == DamageType.kFire && (fx.Type == StatusEffectType.kBurn || fx.Type == StatusEffectType.kFrostburn))
                        || (__1 == DamageType.kPoison && fx.Type == StatusEffectType.kPoison)
                        || (__1 == DamageType.kBleed && fx.Type == StatusEffectType.kBleed)
                        || (__1 == DamageType.kDisease && fx.Type == StatusEffectType.kDisease))
                    {
                        owner = HeroOwner(fx.LastSrc);
                        break;
                    }
                }
            }

            IntPtr ptr = HarmonyHelper.Ptr(__instance);
            if (owner < 0 && ptr != IntPtr.Zero && LastDamager.TryGetValue(ptr, out int lastOwner))
            {
                owner = lastOwner;
            }
            if (owner >= 0 && ptr != IntPtr.Zero)
            {
                LastDamager[ptr] = owner;
            }

            __state = new HitSnapshot
            {
                BeforeHealth = __instance.CurHealth,
                Owner = owner,
                OldSource = _source,
                Active = true
            };
            _source = owner;
        }

        private static void AfterPieceDamage(GridPieceInst __instance, ref HitSnapshot __state)
        {
            if (!__state.Active) return;
            __state.Active = false;
            _source = __state.OldSource;

            if (__instance == null) return;
            long dmg = Math.Max(0L, __state.BeforeHealth - Math.Max(0L, __instance.CurHealth));
            if (dmg > 0L)
            {
                if (__state.Owner >= 0 && __state.Owner < 2)
                {
                    Damage[__state.Owner] += dmg;
                    AddDpsSample(__state.Owner, dmg);
                    if (__instance.Type.IsBoss())
                    {
                        BossDamage[__state.Owner] += dmg;
                    }
                    IntPtr ptr = HarmonyHelper.Ptr(__instance);
                    if (ptr != IntPtr.Zero) LastDamager[ptr] = __state.Owner;
                }
                else
                {
                    TeamDamage += dmg;
                }
                ReconcileKills(__state.Owner);
            }
        }

        private static void FinalizePieceDamage(ref HitSnapshot __state)
        {
            if (__state.Active)
            {
                __state.Active = false;
                _source = __state.OldSource;
            }
        }

        public static void RecordPickup(int owner)
        {
            if (owner >= 0 && owner < 2)
            {
                PickupsCollected[owner]++;
            }
        }

        public static int MvpSlot()
        {
            double s0 = Damage[0] + BossDamage[0] * 0.5 + Kills[0] * 120.0 + PickupsCollected[0] * 25.0;
            double s1 = Damage[1] + BossDamage[1] * 0.5 + Kills[1] * 120.0 + PickupsCollected[1] * 25.0;
            if (s0 <= 0.0 && s1 <= 0.0) return -1;
            return s0 >= s1 ? 0 : 1;
        }

        public static bool GetTopBallDps(int slot, out string ballName, out long dps, out bool isEvolved)
        {
            ballName = "-";
            dps = 0L;
            isEvolved = false;
            var heroes = CoopCharacterRuntime.HeroesFor(slot);
            if (heroes == null || heroes.Count == 0)
            {
                return false;
            }

            float elapsed = BattleSaveData.I != null ? Math.Max(1f, BattleSaveData.I.ElapsedTime) : 1f;
            long bestDps = -1L;
            string bestName = null;
            bool bestEvolved = false;

            for (int i = 0; i < heroes.Count; i++)
            {
                HeroInst hero = heroes[i];
                if (hero == null || hero.StatData == null || hero.StatData.Count == 0) continue;

                HeroInfo info = hero.GetInfo();
                string rawSlug = info != null ? CoopLocalization.Translate(info.GetNameSlug()) : string.Empty;
                string name = CoopLocalization.IsValidTranslation(rawSlug)
                    ? rawSlug
                    : (!string.IsNullOrWhiteSpace(info?.Name) ? info.Name : CoopLocalization.PrettifyEnumName(hero.Type.ToString()));

                long total = 0L;
                float earliest = elapsed;
                for (int j = 0; j < hero.StatData.Count; j++)
                {
                    HeroStatData st = hero.StatData[j];
                    if (st == null) continue;
                    total += Math.Max(0L, st.BounceDmgDealt)
                        + Math.Max(0L, st.StatusEffectDmgDealt)
                        + Math.Max(0L, st.AltDmgDealt)
                        + Math.Max(0L, st.AOEDmgDealt);
                    if (st.ObtainTime >= 0f && st.ObtainTime < earliest)
                    {
                        earliest = st.ObtainTime;
                    }
                }

                float activeTime = Math.Max(1f, elapsed - earliest);
                long curDps = (long)Math.Round(total / activeTime);
                bool merged = info != null && info.IsMerged();

                if (curDps > bestDps || (curDps == bestDps && merged && !bestEvolved))
                {
                    bestDps = curDps;
                    bestName = name;
                    bestEvolved = merged;
                }
            }

            if (bestDps >= 0L && !string.IsNullOrEmpty(bestName))
            {
                ballName = bestName;
                dps = bestDps;
                isEvolved = bestEvolved;
                return true;
            }
            return false;
        }

        public static void SyncBeforeCapture()
        {
            ReconcileNativeStats();
            ReconcileKills(-1);
        }

        public static void Finish(bool victory)
        {
            if (!Complete)
            {
                ReconcileNativeStats();
                ReconcileKills(-1);
                Complete = true;
                Victory = victory;
                CoopHudAndResults.ResultReady();
            }
        }
    }
}
