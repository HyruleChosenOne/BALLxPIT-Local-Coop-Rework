using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Diagnostics;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.UI;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MEC;
using UnityEngine;

namespace BALLxPITLocalCoop.PlayerTwo
{
    /// <summary>
    /// Focused Harmony patches for P2 Player lifecycle, InputMgr P2 suppression, BallMgr/MoveBall/RemoveBall/RunBalls
    /// isolation, recall safety, and enemy-death passive scoping.
    /// Eliminates reflection on public interop methods, O(N^2) scans, IntPtr leaks, and static SpeedMult clobbering.
    /// </summary>
    internal static class PlayerTwoPatches
    {
        private struct PlayerScopedState
        {
            public CoopContext.ScopeToken Context;
            public Player SpeedScaledPlayer;
            public float SavedSpeedMult;
            public float AppliedSpeedMult;
            public bool Closed;
        }

        private struct RunBallsStepState
        {
            public int Owner;
            public int PrevOwner;
            public CoopContext.ScopeToken Context;
            public BallMgrVirtualizer.WorldBallScope WorldScope;
            public bool Active;
            public bool Closed;
        }

        private struct GridPieceDeathState
        {
            public IntPtr PiecePtr;
            public CoopContext.ScopeToken Context;
            public bool Active;
            public bool Closed;
        }

        private struct RemoveBallScopeState
        {
            public CoopContext.ScopeToken Context;
            public BallMgrVirtualizer.WorldBallScope WorldScope;
            public bool Closed;
        }

        private static bool _installed;
        private static bool _globalRecallActive;
        private static bool _p1GameOverActive;
        private static bool _runningP2Maintenance;
        private static bool _releasingP2Launch;
        private static int _activeRunBallsOwner = -1;
        private static int _lastNoLaunchFrame = -1;
        private static int _p2CreatedBallCount;
        private static float _p2NextLaunchAllowedTime;

        // O(1) ball ownership caches (pruned on BallObj.Init, InitWorker, and final retirement).
        private static readonly HashSet<IntPtr> _p2Balls = new HashSet<IntPtr>(256);
        private static readonly HashSet<IntPtr> _p1Balls = new HashSet<IntPtr>(256);
        private static readonly Dictionary<IntPtr, int> _p2RemoveCounts = new Dictionary<IntPtr, int>(64);
        private static readonly HashSet<IntPtr> _suppressedRemovePostfixes = new HashSet<IntPtr>(32);
        private static int _removeCountFrame = -1;

        // Active P2 _RunBalls tracking.
        private static readonly HashSet<IntPtr> _p2RunBallsIterators = new HashSet<IntPtr>(4);
        private static CoroutineHandle _p2CoroutineHandle;
        private static bool _hasP2CoroutineHandle;

        // Reusable pre-step snapshot buffer for detecting balls spawned during a P2 _RunBalls step.
        private static readonly HashSet<IntPtr> _preStepBallScratch = new HashSet<IntPtr>(128);

        // Enemy death passive scoping (capped/pruned to prevent unbounded growth).
        private static readonly HashSet<IntPtr> _p2KilledPieces = new HashSet<IntPtr>(64);
        private static Func<object, GridPieceObj> _deathIteratorPieceGetter;

        internal static bool SuppressGridPieceTarget;

        public static bool IsGlobalRecallActive => _globalRecallActive;
        public static bool IsPlayerOneGameOverActive => _p1GameOverActive;
        public static bool HasActivePlayerTwoBallRun => IsP2CoroutineAlive();

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;
            ResetRound();

            InstallPlayerLifecyclePatches(harmony);
            InstallP2InputSuppressionPatches(harmony);
            InstallBallMgrAndRecallPatches(harmony);
            InstallPreviewIsolationPatches(harmony);
            InstallBallOwnershipAndDeathPatches(harmony);
            InstallRunBallsAndTimingPatches(harmony);
            FalconerIsolation.Install(harmony);

            Plugin.Logger?.LogInfo("PlayerTwoPatches installed cleanly.");
        }

        public static void Shutdown()
        {
            ResetRound();
            FalconerIsolation.Shutdown();
            _deathIteratorPieceGetter = null;
            _installed = false;
        }

        public static void ResetRound()
        {
            CancelActivePlayerTwoCoroutine();
            _p2RunBallsIterators.Clear();
            _p2Balls.Clear();
            _p1Balls.Clear();
            _p2RemoveCounts.Clear();
            _suppressedRemovePostfixes.Clear();
            _p2KilledPieces.Clear();
            _preStepBallScratch.Clear();
            _globalRecallActive = false;
            _p1GameOverActive = false;
            _runningP2Maintenance = false;
            _releasingP2Launch = false;
            _activeRunBallsOwner = -1;
            _lastNoLaunchFrame = -1;
            _p2CreatedBallCount = 0;
            _p2NextLaunchAllowedTime = 0f;
            _removeCountFrame = -1;
            SuppressGridPieceTarget = false;
        }

        private static void InstallPlayerLifecyclePatches(Harmony harmony)
        {
            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(Player), "Awake", Type.EmptyTypes),
                postfix: Method(nameof(PlayerAwakePostfix)),
                label: "Player.Awake");

            (string name, Type[] args)[] playerMethods =
            {
                ("Start", Type.EmptyTypes),
                ("Init", Type.EmptyTypes),
                ("MyUpdate", Type.EmptyTypes),
                ("FixedUpdate", Type.EmptyTypes),
                ("Damage", new[] { typeof(float), typeof(PieceDmgType) }),
                ("RunBallKnockback", new[] { typeof(Vector2) }),
                ("Heal", new[] { typeof(float) }),
                ("ApplyStatusEffect", new[] { typeof(PlayerStatusEffect) })
            };

            MethodInfo pre = Method(nameof(PlayerScopedPrefix));
            MethodInfo post = Method(nameof(PlayerScopedPostfix));
            MethodInfo fin = Method(nameof(PlayerScopedFinalizer));
            foreach (var (name, args) in playerMethods)
            {
                HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(Player), name, args),
                    prefix: pre, postfix: post, finalizer: fin, label: $"Player.{name}");
            }

            // Lightweight PositionOnly nearest-player targeting for enemies.
            MethodInfo gridPre = Method(nameof(GridPieceTargetPrefix));
            MethodInfo gridFin = Method(nameof(SimpleContextFinalizer));
            foreach (string name in new[] { "UpdateWalk", "IsPlayerInRange", "AttackPlayer" })
            {
                HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(GridPieceObj), name, Type.EmptyTypes),
                    prefix: gridPre, finalizer: gridFin, label: $"GridPieceObj.{name}");
            }

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(Player), "RunGameOver", Type.EmptyTypes),
                prefix: Method(nameof(P2RunGameOverPrefix)),
                label: "Player.RunGameOver");
        }

        private static void InstallP2InputSuppressionPatches(Harmony harmony)
        {
            MethodInfo noArg = Method(nameof(P2InputBoolFalsePrefix));
            foreach (string name in new[] { "IsCancelBtnPressed", "IsAnyBtnDown", "IsAnyBtnHeld", "IsAnyBtnUp" })
            {
                HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(InputMgr), name, Type.EmptyTypes),
                    prefix: noArg, label: $"InputMgr.{name}");
            }

            foreach (string name in new[] { "IsBtnDown", "IsBtnHeld", "IsBtnUp" })
            {
                HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(InputMgr), name, new[] { typeof(GameActionType) }),
                    prefix: noArg, label: $"InputMgr.{name}(GameActionType)");
            }

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(InputMgr), "GetAxis", new[] { typeof(GameActionType) }),
                prefix: Method(nameof(P2InputAxisZeroPrefix)), label: "InputMgr.GetAxis(GameActionType)");

            foreach (var (name, args) in new (string, Type[])[]
            {
                ("IsBtnDown", new[] { typeof(ControllerBtn) }),
                ("IsBtnHeld", new[] { typeof(ControllerBtn) }),
                ("IsBtnUp", new[] { typeof(ControllerBtn), typeof(bool) }),
                ("IsFaceBtnDown", new[] { typeof(CardinalDir) }),
                ("IsFaceBtnUp", new[] { typeof(CardinalDir) }),
                ("IsBtnComboDown", new[] { typeof(ControllerBtn), typeof(ControllerBtn) })
            })
            {
                HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(InputMgr), name, args),
                    prefix: noArg, label: $"InputMgr.{name}(ControllerBtn)");
            }

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(InputMgr), "GetAxis", new[] { typeof(ControllerBtn) }),
                prefix: Method(nameof(P2InputAxisZeroPrefix)), label: "InputMgr.GetAxis(ControllerBtn)");
        }

        private static void InstallBallMgrAndRecallPatches(Harmony harmony)
        {
            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "MoveBalls", Type.EmptyTypes),
                prefix: Method(nameof(SkipMoveBallsDuringP2Maintenance)), label: "BallMgr.MoveBalls");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "MyUpdate", Type.EmptyTypes),
                prefix: Method(nameof(P1BallMgrMyUpdatePrefix)),
                finalizer: Method(nameof(P1BallMgrMyUpdateFinalizer)),
                label: "BallMgr.MyUpdate");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), nameof(BallMgr.IsReadyToShoot), Type.EmptyTypes),
                prefix: Method(nameof(BallMgrIsReadyToShootPrefix)),
                finalizer: Method(nameof(BallMgrIsReadyToShootFinalizer)),
                prefixPriority: Priority.First,
                label: "BallMgr.IsReadyToShoot");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "RunBalls", Type.EmptyTypes),
                prefix: Method(nameof(BallMgrRunBallsPrefix)),
                finalizer: Method(nameof(BallMgrRunBallsFinalizer)),
                label: "BallMgr.RunBalls");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(InGameUI), nameof(InGameUI.RefreshAmmoDisplay), Type.EmptyTypes),
                prefix: Method(nameof(SuppressP2AmmoDisplayPrefix)),
                label: "InGameUI.RefreshAmmoDisplay");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "RecallBalls", Type.EmptyTypes),
                prefix: Method(nameof(GlobalRecallPrefix)), label: "BallMgr.RecallBalls");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "SetBallState", new[] { typeof(BallState) }),
                prefix: Method(nameof(GlobalBallStatePrefix)), label: "BallMgr.SetBallState");

            Type recallIter = HarmonyHelper.FindNestedCoroutineType(typeof(BallMgr), "RecallBalls", "BallMgr+__RecallBalls_d__62");
            if (recallIter != null)
            {
                HarmonyHelper.TryPatch(harmony, AccessTools.Method(recallIter, "MoveNext", Type.EmptyTypes),
                    prefix: Method(nameof(RecallBallsMoveNextPrefix)),
                    finalizer: Method(nameof(RecallBallsMoveNextFinalizer)),
                    label: "BallMgr._RecallBalls.MoveNext");
            }
        }

        private static void InstallPreviewIsolationPatches(Harmony harmony)
        {
            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "RefreshBallPreview", Type.EmptyTypes),
                prefix: Method(nameof(P2BallPreviewIsolationPrefix)), label: "BallMgr.RefreshBallPreview");

            MethodInfo previewPre = Method(nameof(P2PreviewCloneMyUpdatePrefix));
            MethodInfo gameUpdate = AccessTools.Method(typeof(GameBallPreview), "MyUpdate", Type.EmptyTypes);
            MethodInfo baseUpdate = AccessTools.Method(typeof(BallPreview), "MyUpdate", Type.EmptyTypes);
            HarmonyHelper.TryPatch(harmony, gameUpdate, prefix: previewPre, label: "GameBallPreview.MyUpdate");
            if (baseUpdate != null && baseUpdate != gameUpdate)
            {
                HarmonyHelper.TryPatch(harmony, baseUpdate, prefix: previewPre, label: "BallPreview.MyUpdate");
            }

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "MousePosToAimDir", new[] { typeof(Vector3), typeof(int) }),
                prefix: Method(nameof(P2MousePosToAimDirPrefix)), label: "BallMgr.MousePosToAimDir");
        }

        private static void InstallBallOwnershipAndDeathPatches(Harmony harmony)
        {
            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "CreateBall", new[] { typeof(HeroInst), typeof(bool), typeof(Vector2), typeof(BallSourceType) }),
                postfix: Method(nameof(CreateBallPostfix)), label: "BallMgr.CreateBall");

            MethodInfo auxPost = Method(nameof(CreatedAuxBallPostfix));
            MethodInfo simpleFin = Method(nameof(SimpleContextFinalizer));

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "CreateSideBall"),
                prefix: Method(nameof(SideBallParentHeroPrefix)), postfix: auxPost, finalizer: simpleFin, label: "BallMgr.CreateSideBall");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "CreateAdultSideBall"),
                prefix: Method(nameof(AdultSideBallParentHeroPrefix)), postfix: auxPost, finalizer: simpleFin, label: "BallMgr.CreateAdultSideBall");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "CreatePassiveSideBall"),
                prefix: Method(nameof(PassiveInstPrefix)), postfix: auxPost, finalizer: simpleFin, label: "BallMgr.CreatePassiveSideBall");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "LaunchRandomBabiesPassive", new[] { typeof(PassiveInst), typeof(Vector3), typeof(int), typeof(int), typeof(float), typeof(float) }),
                prefix: Method(nameof(PassiveInstPrefix)), finalizer: simpleFin, label: "BallMgr.LaunchRandomBabiesPassive");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "LaunchRandomBabies", new[] { typeof(Vector3), typeof(int), typeof(int), typeof(BallSourceType), typeof(HeroInst), typeof(float), typeof(float) }),
                prefix: Method(nameof(LaunchRandomBabiesHeroPrefix)), finalizer: simpleFin, label: "BallMgr.LaunchRandomBabies");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallObj), nameof(BallObj.Init), new[] { typeof(HeroInst), typeof(bool), typeof(bool), typeof(Vector2), typeof(BallSourceType) }),
                postfix: Method(nameof(OnBallInitPostfix)), label: "BallObj.Init");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallObj), nameof(BallObj.InitWorker), new[] { typeof(CharMetaInst), typeof(Vector2) }),
                postfix: Method(nameof(OnBallInitPostfix)), label: "BallObj.InitWorker");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallObj), nameof(BallObj.MyUpdate), Type.EmptyTypes),
                prefix: Method(nameof(BallObjOwnedPrefix)), finalizer: simpleFin, label: "BallObj.MyUpdate");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallObj), nameof(BallObj.MarkLastBounceOnFrontWall), Type.EmptyTypes),
                prefix: Method(nameof(BallObjOwnedPrefix)), finalizer: simpleFin, label: "BallObj.MarkLastBounceOnFrontWall");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "MoveBall", new[] { typeof(BallObj), typeof(bool), typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(Il2CppStructArray<bool>) }),
                prefix: Method(nameof(MoveBallPrefix)), finalizer: simpleFin, label: "BallMgr.MoveBall");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "RemoveBall", new[] { typeof(BallObj), typeof(bool), typeof(BallReturnType) }),
                prefix: Method(nameof(RemoveBallPrefix)), postfix: Method(nameof(RemoveBallPostfix)), finalizer: Method(nameof(RemoveBallFinalizer)), label: "BallMgr.RemoveBall");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), "OnHeroesChanged", Type.EmptyTypes),
                postfix: Method(nameof(OnHeroesChangedPostfix)), label: "BallMgr.OnHeroesChanged");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(GridPieceInst), nameof(GridPieceInst.OnKilled), Type.EmptyTypes),
                prefix: Method(nameof(OnGridPieceKilledPrefix)), label: "GridPieceInst.OnKilled");

            Type deathIter = HarmonyHelper.FindNestedCoroutineType(typeof(GridPieceObj), "RunDeath", "GridPieceObj+__RunDeath_d__127");
            if (deathIter != null)
            {
                _deathIteratorPieceGetter = BuildDeathPieceGetter(deathIter);
                HarmonyHelper.TryPatch(harmony, AccessTools.Method(deathIter, "MoveNext", Type.EmptyTypes),
                    prefix: Method(nameof(GridPieceDeathMoveNextPrefix)),
                    postfix: Method(nameof(GridPieceDeathMoveNextPostfix)),
                    finalizer: Method(nameof(GridPieceDeathMoveNextFinalizer)),
                    label: "GridPieceObj._RunDeath.MoveNext");
            }
        }

        private static void InstallRunBallsAndTimingPatches(Harmony harmony)
        {
            Type runBallsIter = HarmonyHelper.FindNestedCoroutineType(typeof(BallMgr), "RunBalls", "BallMgr+__RunBalls_d__42");
            if (runBallsIter != null)
            {
                HarmonyHelper.TryPatch(harmony, AccessTools.Method(runBallsIter, "MoveNext", Type.EmptyTypes),
                    prefix: Method(nameof(RunBallsMoveNextPrefix)),
                    postfix: Method(nameof(RunBallsMoveNextPostfix)),
                    finalizer: Method(nameof(RunBallsMoveNextFinalizer)),
                    label: "BallMgr._RunBalls.MoveNext");
            }

            // Patch ALL MEC.Timing KillCoroutines* overloads so P2 tracking never goes stale.
            Type timingType = typeof(Timing);
            MethodInfo killAllPost = Method(nameof(OnTimingKillAllPostfix));
            MethodInfo killHandlePre = Method(nameof(OnTimingKillHandlePrefix));

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(timingType, "KillCoroutines", Type.EmptyTypes),
                postfix: killAllPost, label: "Timing.KillCoroutines()");
            HarmonyHelper.TryPatch(harmony, AccessTools.Method(timingType, "KillCoroutinesOnInstance", Type.EmptyTypes),
                postfix: killAllPost, label: "Timing.KillCoroutinesOnInstance()");
            HarmonyHelper.TryPatch(harmony, AccessTools.Method(timingType, "KillCoroutines", new[] { typeof(CoroutineHandle) }),
                prefix: killHandlePre, label: "Timing.KillCoroutines(CoroutineHandle)");
            HarmonyHelper.TryPatch(harmony, AccessTools.Method(timingType, "KillCoroutinesOnInstance", new[] { typeof(CoroutineHandle) }),
                prefix: killHandlePre, label: "Timing.KillCoroutinesOnInstance(CoroutineHandle)");
        }

        private static MethodInfo Method(string name) =>
            typeof(PlayerTwoPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        // --- Ball Ownership (O(1) fast paths for both P1 and P2) ---

        public static bool IsPlayerTwoBall(BallObj ball)
        {
            IntPtr ptr = HarmonyHelper.Ptr(ball);
            if (ptr == IntPtr.Zero) return false;
            if (_p2Balls.Contains(ptr)) return true;
            if (_p1Balls.Contains(ptr)) return false;

            bool isP2 = HasP2OwnerRef(ball);
            if (isP2) _p2Balls.Add(ptr);
            else _p1Balls.Add(ptr);
            return isP2;
        }

        public static bool MatchesBallOwner(BallObj ball, int owner)
        {
            if (ball == null) return false;
            bool isP2 = IsPlayerTwoBall(ball);
            return owner == 1 ? isP2 : !isP2;
        }

        private static bool HasP2OwnerRef(BallObj ball)
        {
            try
            {
                return (ball.Inst != null && HeroInventoryIsolation.IsPlayerTwoHero(ball.Inst))
                    || (ball.ParentInst != null && HeroInventoryIsolation.IsPlayerTwoHero(ball.ParentInst))
                    || (ball.PassiveSrc != null && CoopCharacterRuntime.IsPassiveInSlot(1, ball.PassiveSrc));
            }
            catch
            {
                return false;
            }
        }

        private static void RegisterBallOwnership(BallObj ball, bool countAsP2Launch)
        {
            IntPtr ptr = HarmonyHelper.Ptr(ball);
            if (ptr == IntPtr.Zero) return;

            _p2RemoveCounts.Remove(ptr);
            _suppressedRemovePostfixes.Remove(ptr);

            bool belongsToP2 = CoopContext.IsPlayerTwoContext || HasP2OwnerRef(ball);
            if (belongsToP2)
            {
                _p1Balls.Remove(ptr);
                _p2Balls.Add(ptr);
                CoopRoundStats.NotifyBallOwnership(ptr, 1);
                if (countAsP2Launch && CoopContext.IsPlayerTwoContext)
                {
                    _p2CreatedBallCount++;
                    AdvancePlayerTwoLaunchCooldown();
                }
            }
            else
            {
                _p2Balls.Remove(ptr);
                _p1Balls.Add(ptr);
                CoopRoundStats.NotifyBallOwnership(ptr, 0);
                BallMgrVirtualizer.RemoveBallFromAllScopesAndEarlyLists(ball);
            }
        }

        private static void AdvancePlayerTwoLaunchCooldown()
        {
            try
            {
                float fireRate = UpgradeMgr.I != null ? UpgradeMgr.I.FireRate : 0f;
                if (fireRate > 0.05f && BattleSaveData.I != null)
                {
                    _p2NextLaunchAllowedTime = BattleSaveData.I.ElapsedTime + (1f / fireRate);
                }
            }
            catch { }
        }

        private static void UnregisterBallPointer(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return;
            _p2Balls.Remove(ptr);
            _p1Balls.Remove(ptr);
            _p2RemoveCounts.Remove(ptr);
            _suppressedRemovePostfixes.Remove(ptr);
            CoopRoundStats.ForgetBall(ptr);
        }

        // --- Player & Enemy Context Scopes ---

        private static void PlayerAwakePostfix(Player __instance)
        {
            if (CoopContext.IsCreatingPlayerTwo && !HarmonyHelper.SameNative(__instance, CoopContext.OriginalPlayer))
            {
                CoopContext.ForceRestorePlayerOne();
            }
        }

        private static void PlayerScopedPrefix(Player __instance, MethodBase __originalMethod, out PlayerScopedState __state)
        {
            __state = default;
            if (__instance == null) return;

            string methodName = __originalMethod?.Name;
            if (CoopVitals.Active && (methodName == "MyUpdate" || methodName == "FixedUpdate") && CoopVitals.Down(CoopVitals.Owner(__instance)))
            {
                return;
            }

            if (CoopContext.IsPlayerTwo(__instance))
            {
                bool isMyUpdate = methodName == "MyUpdate";
                if (isMyUpdate)
                {
                    PlayerTwoRuntime.ReapplyMovementOverrides();
                }

                __state.Context = CoopContext.EnterPlayerTwo(__instance, CoopContext.ScopeKind.Full);

                // Mirror vanilla MoveSpeedWhileShooting slowdown inside P2.MyUpdate, stored per-scope (never clobbered by nested calls!).
                if (isMyUpdate && UpgradeMgr.I != null && BallMgr.I != null && BallMgr.I.DidShootRecently())
                {
                    float moveSpeed = UpgradeMgr.I.MoveSpeed;
                    float shootSpeed = UpgradeMgr.I.MoveSpeedWhileShooting;
                    if (moveSpeed > 0.0001f && shootSpeed >= 0f && shootSpeed < moveSpeed - 0.0001f)
                    {
                        float ratio = Mathf.Clamp(shootSpeed / moveSpeed, 0.05f, 1f);
                        __state.SpeedScaledPlayer = __instance;
                        __state.SavedSpeedMult = __instance.SpeedMult;
                        __state.AppliedSpeedMult = __state.SavedSpeedMult * ratio;
                        __instance.SpeedMult = __state.AppliedSpeedMult;
                    }
                }
            }
            else if (HarmonyHelper.SameNative(__instance, CoopContext.OriginalPlayer) && CoopContext.IsPlayerTwoContext)
            {
                // Properly push a nested P1 override instead of nuking P2's context stack!
                __state.Context = CoopContext.EnterPlayerOne(CoopContext.ScopeKind.Full);
            }
        }

        private static void PlayerScopedPostfix(ref PlayerScopedState __state) => ClosePlayerScoped(ref __state);

        private static Exception PlayerScopedFinalizer(ref PlayerScopedState __state, Exception __exception)
        {
            ClosePlayerScoped(ref __state);
            return __exception;
        }

        private static void ClosePlayerScoped(ref PlayerScopedState state)
        {
            if (state.Closed) return;
            state.Closed = true;
            if (state.SpeedScaledPlayer != null)
            {
                try
                {
                    if (Mathf.Abs(state.SpeedScaledPlayer.SpeedMult - state.AppliedSpeedMult) < 0.0001f)
                    {
                        state.SpeedScaledPlayer.SpeedMult = state.SavedSpeedMult;
                    }
                }
                catch { }
            }
            CoopContext.Exit(state.Context);
        }

        private static void GridPieceTargetPrefix(GridPieceObj __instance, out CoopContext.ScopeToken __state)
        {
            __state = default;
            if (SuppressGridPieceTarget || __instance == null || !CoopMode.Enabled || CoopContext.PlayerTwo == null)
            {
                return;
            }
            // Lightweight PositionOnly scope: zero BallMgr/UpgradeMgr/Falconer overhead!
            __state = CoopContext.EnterNearestPlayer(__instance.transform.position, CoopContext.ScopeKind.PositionOnly);
        }

        private static void SimpleContextFinalizer(CoopContext.ScopeToken __state) => CoopContext.Exit(__state);

        private static bool P2RunGameOverPrefix(Player __instance)
        {
            if (CoopVitals.Active)
            {
                return false;
            }
            if (CoopContext.IsPlayerTwo(__instance))
            {
                return false;
            }
            if (__instance != null && HarmonyHelper.SameNative(__instance, CoopContext.OriginalPlayer) && CoopContext.PlayerTwo != null)
            {
                _p1GameOverActive = true;
                BeginGlobalRecall(BallMgr.I);
            }
            return true;
        }

        // --- InputMgr Suppression While Player.I == P2 ---

        private static bool P2InputBoolFalsePrefix(ref bool __result)
        {
            if (!PlayerTwoRuntime.IsRuntimeEnabled || !CoopContext.IsPlayerTwoContext)
            {
                return true;
            }
            __result = false;
            return false;
        }

        private static bool P2InputAxisZeroPrefix(ref float __result)
        {
            if (!PlayerTwoRuntime.IsRuntimeEnabled || !CoopContext.IsPlayerTwoContext)
            {
                return true;
            }
            __result = 0f;
            return false;
        }

        // --- Preview Isolation ---

        private static bool P2BallPreviewIsolationPrefix() => !CoopContext.IsPlayerTwoContext || PreviewIsolation.IsRefreshing;

        private static bool P2PreviewCloneMyUpdatePrefix(object __instance)
        {
            if (__instance is BallPreview preview && PreviewIsolation.IsPlayerTwoPreview(preview))
            {
                return PreviewIsolation.IsRefreshing;
            }
            return true;
        }

        private static bool P2MousePosToAimDirPrefix(ref Vector2 __result)
        {
            if (!PlayerTwoRuntime.IsRuntimeEnabled || !CoopContext.IsPlayerTwoContext || PlayerTwoRuntime.IsEvaluatingMouseAim)
            {
                return true;
            }
            Vector2 dir = PlayerTwoRuntime.AimDir;
            if (dir.sqrMagnitude <= 0.0001f) return true;
            __result = dir.normalized;
            return false;
        }

        // --- Ball Ownership, Creation, MoveBall & RemoveBall ---

        private static void OnBallInitPostfix(BallObj __instance) => RegisterBallOwnership(__instance, countAsP2Launch: false);
        private static void CreateBallPostfix(BallObj __result) => RegisterBallOwnership(__result, countAsP2Launch: true);
        private static void CreatedAuxBallPostfix(BallObj __result) => RegisterBallOwnership(__result, countAsP2Launch: false);

        private static void SideBallParentHeroPrefix(HeroInst __0, out CoopContext.ScopeToken __state)
        {
            __state = default;
            if (CoopMode.Enabled && CoopCharacterRuntime.IndependentReady && !CoopContext.IsFullPlayerTwoContext
                && __0 != null && HeroInventoryIsolation.IsPlayerTwoHero(__0))
            {
                __state = CoopContext.EnterPlayerTwo(CoopContext.PlayerTwo, CoopContext.ScopeKind.Full);
            }
        }

        private static void AdultSideBallParentHeroPrefix(HeroInst __0, HeroInst __1, out CoopContext.ScopeToken __state)
        {
            __state = default;
            if (CoopMode.Enabled && CoopCharacterRuntime.IndependentReady && !CoopContext.IsFullPlayerTwoContext
                && ((__0 != null && HeroInventoryIsolation.IsPlayerTwoHero(__0))
                    || (__1 != null && HeroInventoryIsolation.IsPlayerTwoHero(__1))))
            {
                __state = CoopContext.EnterPlayerTwo(CoopContext.PlayerTwo, CoopContext.ScopeKind.Full);
            }
        }

        private static void LaunchRandomBabiesHeroPrefix(HeroInst __4, out CoopContext.ScopeToken __state)
        {
            __state = default;
            if (CoopMode.Enabled && CoopCharacterRuntime.IndependentReady && !CoopContext.IsFullPlayerTwoContext
                && __4 != null && HeroInventoryIsolation.IsPlayerTwoHero(__4))
            {
                __state = CoopContext.EnterPlayerTwo(CoopContext.PlayerTwo, CoopContext.ScopeKind.Full);
            }
        }

        private static void PassiveInstPrefix(PassiveInst __0, out CoopContext.ScopeToken __state)
        {
            __state = default;
            if (CoopMode.Enabled && CoopCharacterRuntime.IndependentReady && !CoopContext.IsFullPlayerTwoContext
                && __0 != null && CoopCharacterRuntime.IsPassiveInSlot(1, __0))
            {
                __state = CoopContext.EnterPlayerTwo(CoopContext.PlayerTwo, CoopContext.ScopeKind.Full);
            }
        }

        private static void BallObjOwnedPrefix(BallObj __instance, out CoopContext.ScopeToken __state)
        {
            __state = default;
            if (CoopMode.Enabled && CoopCharacterRuntime.IndependentReady && !CoopContext.IsFullPlayerTwoContext
                && __instance != null && IsPlayerTwoBall(__instance))
            {
                __state = CoopContext.EnterPlayerTwo(CoopContext.PlayerTwo, CoopContext.ScopeKind.Full);
            }
        }

        private static void MoveBallPrefix(BallObj __0, ref Vector3 __2, ref Vector3 __3, ref Vector3 __4, ref Il2CppStructArray<bool> __5, out CoopContext.ScopeToken __state)
        {
            __state = default;
            if (__0 == null || CoopContext.PlayerTwo == null || !IsPlayerTwoBall(__0))
            {
                return;
            }
            try
            {
                Player p2 = CoopContext.PlayerTwo;
                Vector3 rootPos = p2.transform.position;
                Vector3 char1Pos = rootPos;
                Vector3 char2Pos = rootPos;
                var controllers = p2.CharControllers;
                if (controllers != null && controllers.Length > 0 && controllers[0] != null)
                {
                    char1Pos = controllers[0].transform.position;
                    char2Pos = (controllers.Length > 1 && controllers[1] != null)
                        ? controllers[1].transform.position
                        : char1Pos;
                }
                __2 = rootPos;
                __3 = char1Pos;
                __4 = char2Pos;
                __state = CoopContext.EnterPlayerTwo(p2, CoopContext.ScopeKind.Full);
                Il2CppStructArray<bool> p2Props = CharBattleInst.I?.GetPropArray();
                if (p2Props != null) __5 = p2Props;
            }
            catch { }
        }

        private static bool RemoveBallPrefix(BallObj __0, bool __1, out RemoveBallScopeState __state)
        {
            __state = default;
            if (__0 == null || CoopContext.PlayerTwo == null)
            {
                return true;
            }

            int frame = Time.frameCount;
            if (_removeCountFrame != frame)
            {
                _removeCountFrame = frame;
                _p2RemoveCounts.Clear();
            }

            bool isP2Ball = IsPlayerTwoBall(__0);
            if (!isP2Ball)
            {
                if (CoopContext.IsPlayerTwoContext)
                {
                    __state.Context = CoopContext.EnterPlayerOne(CoopContext.ScopeKind.Full);
                }
                if (BallMgr.I != null)
                {
                    __state.WorldScope = BallMgrVirtualizer.EnterWorldBallScope(BallMgr.I, 0);
                }
                return true;
            }

            IntPtr ptr = HarmonyHelper.Ptr(__0);
            if (_globalRecallActive)
            {
                // Keep P2's RemoveBall in P2 context during recall so P2 balls never decrement P1's _numActiveFollowers!
                __state.Context = CoopContext.EnterPlayerTwo(CoopContext.PlayerTwo, CoopContext.ScopeKind.Full);
                __state.WorldScope = BallMgrVirtualizer.EnterWorldBallScope(BallMgr.I, 1);
                if (ptr != IntPtr.Zero) _p2RemoveCounts.Remove(ptr);
                return true;
            }

            __state.Context = CoopContext.EnterPlayerTwo(CoopContext.PlayerTwo, CoopContext.ScopeKind.Full);
            BallMgr mgr = BallMgr.I;
            __state.WorldScope = BallMgrVirtualizer.EnterWorldBallScope(mgr, 1);

            if (!__1 && mgr != null && mgr.GetBallState() == BallState.kLaunching && BallMgrVirtualizer.ContainsBall(mgr.EarlyReturnedBalls, __0))
            {
                if (ptr != IntPtr.Zero) _suppressedRemovePostfixes.Add(ptr);
                DeactivateBall(__0);
                return false;
            }

            if (ptr != IntPtr.Zero)
            {
                _p2RemoveCounts.TryGetValue(ptr, out int count);
                if (count >= 8)
                {
                    _suppressedRemovePostfixes.Add(ptr);
                    ForceRetireP2Ball(mgr, __0, ptr);
                    return false;
                }
                _p2RemoveCounts[ptr] = count + 1;
            }

            return true;
        }

        private static void RemoveBallPostfix(BallObj __0, bool __1)
        {
            if (__0 == null) return;
            IntPtr ptr = HarmonyHelper.Ptr(__0);
            if (ptr != IntPtr.Zero && _suppressedRemovePostfixes.Remove(ptr))
            {
                return;
            }
            if (!__0.IsActive)
            {
                // Ball retired (not deferred into EarlyReturnedBalls): prune ownership cache if not waiting in EarlyReturnedBalls.
                BallMgr mgr = BallMgr.I;
                if (mgr == null || !BallMgrVirtualizer.ContainsBall(mgr.EarlyReturnedBalls, __0))
                {
                    BallMgrVirtualizer.RemoveBallFromAllScopesAndEarlyLists(__0);
                    UnregisterBallPointer(ptr);
                }
            }
        }

        private static Exception RemoveBallFinalizer(ref RemoveBallScopeState __state, Exception __exception)
        {
            if (!__state.Closed)
            {
                __state.Closed = true;
                try { BallMgrVirtualizer.ExitWorldBallScope(BallMgr.I, __state.WorldScope, reconcileChanges: true); }
                finally { CoopContext.Exit(__state.Context); }
            }
            return __exception;
        }

        private static void ForceRetireP2Ball(BallMgr mgr, BallObj ball, IntPtr ptr)
        {
            if (ball == null) return;
            bool isBaby = false;
            try { isBaby = ball.IsBaby(); } catch { }
            bool wasInEarly = mgr != null && BallMgrVirtualizer.ContainsBall(mgr.EarlyReturnedBalls, ball);
            bool wasInActive = mgr != null && BallMgrVirtualizer.ContainsBall(mgr.ActiveBalls, ball);

            try { ball.OnAboutToRemove(); } catch { }
            try
            {
                if (ball.Inst != null && HarmonyHelper.SameNative(ball.Inst.Obj, ball))
                {
                    ball.Inst.Obj = null;
                    ball.Inst.LastReturnTime = BattleSaveData.I != null ? BattleSaveData.I.ElapsedTime : 0f;
                }
            }
            catch { }

            BallMgrVirtualizer.RemoveBallFromAllScopesAndEarlyLists(ball);
            if (mgr != null)
            {
                BallMgrVirtualizer.RemoveBallByPointer(mgr.ActiveBalls, ptr);
                BallMgrVirtualizer.RemoveBallByPointer(mgr.SideBalls, ptr);
                BallMgrVirtualizer.RemoveBallByPointer(mgr.AdultSideBalls, ptr);
                BallMgrVirtualizer.RemoveBallByPointer(mgr._justLaunchedBalls, ptr);
                BallMgrVirtualizer.RemoveBallByPointer(mgr._justLaunchedAltBalls, ptr);
            }
            DeactivateBall(ball);

            if (isBaby && mgr != null)
            {
                if (wasInEarly && mgr._earlyReturnedFollowers > 0)
                {
                    mgr._earlyReturnedFollowers--;
                }
                if (wasInActive && !wasInEarly)
                {
                    mgr._numActiveFollowers = Math.Max(0, mgr._numActiveFollowers - 1);
                    if (!_globalRecallActive)
                    {
                        try { mgr.RechargingFollowers?.Add(0f); } catch { }
                    }
                }
            }

            UnregisterBallPointer(ptr);
        }

        private static void DeactivateBall(BallObj ball)
        {
            if (ball == null) return;
            try { ball.IsActive = false; } catch { }
            try { if (ball.gameObject != null) ball.gameObject.SetActive(false); } catch { }
        }

        private static void OnHeroesChangedPostfix()
        {
            HeroInventoryIsolation.RebuildPointerCaches();
            CoopRoundStats.InvalidateHeroCache();
        }

        // --- Enemy Death Passive Context ---

        private static void OnGridPieceKilledPrefix(GridPieceInst __instance)
        {
            if (!CoopMode.Enabled || __instance == null) return;
            IntPtr ptr = HarmonyHelper.Ptr(__instance);
            if (ptr == IntPtr.Zero) return;

            if (CoopContext.IsPlayerTwoContext)
            {
                if (_p2KilledPieces.Count > 256) _p2KilledPieces.Clear();
                _p2KilledPieces.Add(ptr);
            }
            else
            {
                _p2KilledPieces.Remove(ptr);
            }
        }

        private static void GridPieceDeathMoveNextPrefix(object __instance, out GridPieceDeathState __state)
        {
            __state = default;
            if (!CoopMode.Enabled || !CoopCharacterRuntime.IndependentReady || CoopContext.PlayerTwo == null
                || _p2KilledPieces.Count == 0 || _deathIteratorPieceGetter == null)
            {
                return;
            }
            GridPieceObj piece = null;
            try { piece = _deathIteratorPieceGetter(__instance); } catch { }
            IntPtr instPtr = HarmonyHelper.Ptr(piece?.Inst);
            if (instPtr != IntPtr.Zero && _p2KilledPieces.Contains(instPtr))
            {
                __state.PiecePtr = instPtr;
                __state.Active = true;
                if (!CoopContext.IsFullPlayerTwoContext)
                {
                    __state.Context = CoopContext.EnterPlayerTwo(CoopContext.PlayerTwo, CoopContext.ScopeKind.Full);
                }
            }
        }

        private static void GridPieceDeathMoveNextPostfix(ref GridPieceDeathState __state, bool __result)
        {
            if (!__state.Active || __state.Closed) return;
            __state.Closed = true;
            CoopContext.Exit(__state.Context);
            if (!__result && __state.PiecePtr != IntPtr.Zero)
            {
                _p2KilledPieces.Remove(__state.PiecePtr);
            }
        }

        private static Exception GridPieceDeathMoveNextFinalizer(ref GridPieceDeathState __state, Exception __exception)
        {
            if (__state.Active && !__state.Closed)
            {
                __state.Closed = true;
                CoopContext.Exit(__state.Context);
                if (__state.PiecePtr != IntPtr.Zero)
                {
                    _p2KilledPieces.Remove(__state.PiecePtr);
                }
            }
            return __exception;
        }

        private static Func<object, GridPieceObj> BuildDeathPieceGetter(Type iterType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo prop = null;
            foreach (PropertyInfo p in iterType.GetProperties(flags))
            {
                if (p.Name.IndexOf("4__this", StringComparison.Ordinal) >= 0 && typeof(GridPieceObj).IsAssignableFrom(p.PropertyType))
                {
                    prop = p;
                    break;
                }
            }
            if (prop == null) return null;
            try
            {
                ParameterExpression param = Expression.Parameter(typeof(object), "iter");
                UnaryExpression cast = Expression.Convert(param, iterType);
                MemberExpression access = Expression.Property(cast, prop);
                return Expression.Lambda<Func<object, GridPieceObj>>(access, param).Compile();
            }
            catch
            {
                return iter => prop.GetValue(iter) as GridPieceObj;
            }
        }

        // --- BallMgr.MyUpdate, IsReadyToShoot, RunBalls & Maintenance ---

        private static bool SkipMoveBallsDuringP2Maintenance() => !_runningP2Maintenance;
        private static bool SuppressP2AmmoDisplayPrefix() => !CoopMode.Enabled || !CoopCharacterRuntime.IndependentReady || !CoopContext.IsPlayerTwoContext;

        private static void P1BallMgrMyUpdatePrefix(BallMgr __instance, out BallMgrVirtualizer.WorldBallScope __state)
        {
            __state = null;
            if (!PlayerTwoRuntime.IsRuntimeEnabled || __instance == null || CoopContext.PlayerTwo == null
                || _runningP2Maintenance || CoopContext.IsPlayerTwoContext)
            {
                return;
            }
            __state = BallMgrVirtualizer.EnterWorldBallScope(__instance, 0);
        }

        private static void P1BallMgrMyUpdateFinalizer(BallMgr __instance, BallMgrVirtualizer.WorldBallScope __state)
        {
            BallMgrVirtualizer.ExitWorldBallScope(__instance, __state, reconcileChanges: true);
        }

        private static void BallMgrIsReadyToShootPrefix(BallMgr __instance, out BallMgrVirtualizer.WorldBallScope __state)
        {
            __state = null;
            if (!CoopMode.Enabled || !CoopCharacterRuntime.IndependentReady || __instance == null || CoopContext.PlayerTwo == null)
            {
                return;
            }
            int owner = CoopContext.IsPlayerTwoContext ? 1 : 0;
            __state = BallMgrVirtualizer.EnterWorldBallScope(__instance, owner);
            if (__instance._numActiveFollowers < 0)
            {
                __instance._numActiveFollowers = 0;
            }
        }

        private static void BallMgrIsReadyToShootFinalizer(BallMgr __instance, BallMgrVirtualizer.WorldBallScope __state)
        {
            BallMgrVirtualizer.ExitWorldBallScope(__instance, __state, reconcileChanges: false);
        }

        private static bool BallMgrRunBallsPrefix(BallMgr __instance, out BallMgrVirtualizer.WorldBallScope __state)
        {
            __state = null;
            if (!PlayerTwoRuntime.IsRuntimeEnabled) return true;
            if (CoopContext.IsPlayerTwoContext) return false;
            if (__instance != null && CoopContext.PlayerTwo != null)
            {
                __state = BallMgrVirtualizer.EnterWorldBallScope(__instance, 0);
            }
            return true;
        }

        private static void BallMgrRunBallsFinalizer(BallMgr __instance, BallMgrVirtualizer.WorldBallScope __state)
        {
            BallMgrVirtualizer.ExitWorldBallScope(__instance, __state, reconcileChanges: true);
        }

        public static void RunPlayerTwoBallMgrMaintenance(BallMgr manager, Player playerTwo)
        {
            if (_runningP2Maintenance || _globalRecallActive || _p1GameOverActive || manager == null || playerTwo == null)
            {
                return;
            }

            using (HangWatchdog.Enter(HangWatchdog.PhaseId.P2Maintenance))
            {
                var ctx = CoopContext.EnterPlayerTwo(playerTwo, CoopContext.ScopeKind.Full);
                var worldScope = BallMgrVirtualizer.EnterWorldBallScope(manager, 1);
                _runningP2Maintenance = true;
                try
                {
                    SyncEarlyReturnedFollowerCount(manager);
                    manager.MyUpdate();
                    SyncEarlyReturnedFollowerCount(manager);
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogDebug($"P2 BallMgr maintenance failed: {ex.Message}");
                }
                finally
                {
                    _runningP2Maintenance = false;
                    BallMgrVirtualizer.ExitWorldBallScope(manager, worldScope, reconcileChanges: true);
                    CoopContext.Exit(ctx);
                }
            }
        }

        private static void SyncEarlyReturnedFollowerCount(BallMgr manager)
        {
            if (manager == null || manager.GetBallState() == BallState.kLaunching) return;
            int listCount = manager.EarlyReturnedBalls?.Count ?? 0;
            if (listCount == 0 && manager._earlyReturnedFollowers != 0)
            {
                manager._earlyReturnedFollowers = 0;
            }
        }

        // --- Global Recall ---

        public static void RefreshGlobalRecallState(BallMgr manager)
        {
            if (manager == null || CoopContext.PlayerTwo == null || CoopContext.IsPlayerTwoContext) return;
            try
            {
                if (manager.GetBallState() == BallState.kRecalling) BeginGlobalRecall(manager);
                else if (_globalRecallActive) FinishGlobalRecall(manager);
            }
            catch { }
        }

        private static void GlobalRecallPrefix(BallMgr __instance) => BeginGlobalRecall(__instance);

        private static void GlobalBallStatePrefix(BallMgr __instance, BallState __0)
        {
            if (!CoopContext.IsPlayerTwoContext && __0 == BallState.kRecalling)
            {
                BeginGlobalRecall(__instance);
            }
        }

        private static void RecallBallsMoveNextPrefix()
        {
            if (CoopContext.PlayerTwo != null) BeginGlobalRecall(BallMgr.I);
        }

        private static void RecallBallsMoveNextFinalizer(bool __result, Exception __exception)
        {
            if ((!__result || __exception != null) && _globalRecallActive)
            {
                FinishGlobalRecall(BallMgr.I);
            }
        }

        private static void BeginGlobalRecall(BallMgr manager)
        {
            if (_globalRecallActive || manager == null || CoopContext.PlayerTwo == null) return;
            _globalRecallActive = true;
            CancelActivePlayerTwoCoroutine();
            var scope = CoopContext.EnterPlayerTwo(CoopContext.PlayerTwo, CoopContext.ScopeKind.Full);
            try { manager.CancelShoot(); } catch { }
            finally { CoopContext.Exit(scope); }
        }

        private static void FinishGlobalRecall(BallMgr manager)
        {
            _globalRecallActive = false;
            _p2RunBallsIterators.Clear();
            _hasP2CoroutineHandle = false;
            _p2CoroutineHandle = default;
            _p2RemoveCounts.Clear();
            _suppressedRemovePostfixes.Clear();
            if (manager == null || CoopContext.PlayerTwo == null) return;

            CoopContext.ForceRestorePlayerOne();
            BallMgrVirtualizer.RebasePlayerTwoFromPlayerOne();
            var scope = CoopContext.EnterPlayerTwo(CoopContext.PlayerTwo, CoopContext.ScopeKind.Full);
            try { HeroInventoryIsolation.RepairRuntimeAgainstWorld(manager); } catch { }
            finally { CoopContext.Exit(scope); }
        }

        // --- P2 _RunBalls Coroutine Scoping & Launch ---

        private static void RunBallsMoveNextPrefix(object __instance, out RunBallsStepState __state)
        {
            __state = default;
            if (__instance == null) return;
            IntPtr ptr = HarmonyHelper.Ptr(__instance);
            if (ptr == IntPtr.Zero) return;

            BallMgr mgr = BallMgr.I;
            bool isP2Iter = _p2RunBallsIterators.Contains(ptr);
            if (isP2Iter)
            {
                if (CoopContext.PlayerTwo == null)
                {
                    _p2RunBallsIterators.Remove(ptr);
                    return;
                }
                __state.Active = true;
                __state.Owner = 1;
                __state.PrevOwner = _activeRunBallsOwner;

                CaptureBallPointers(mgr, _preStepBallScratch);
                __state.Context = CoopContext.EnterPlayerTwo(CoopContext.PlayerTwo, CoopContext.ScopeKind.Full);
                _activeRunBallsOwner = 1;
                CoopAmmoRecovery.ScrubEarlyReturnedBalls(mgr, 1);
                __state.WorldScope = BallMgrVirtualizer.EnterWorldBallScope(mgr, 1);
                PlayerTwoRuntime.ReapplyAim();
                return;
            }

            if (PlayerTwoRuntime.IsRuntimeEnabled && CoopContext.PlayerTwo != null && !CoopContext.IsPlayerTwoContext)
            {
                __state.Active = true;
                __state.Owner = 0;
                __state.PrevOwner = _activeRunBallsOwner;
                _activeRunBallsOwner = 0;
                CoopAmmoRecovery.ScrubEarlyReturnedBalls(mgr, 0);
                __state.WorldScope = BallMgrVirtualizer.EnterWorldBallScope(mgr, 0);
            }
        }

        private static void RunBallsMoveNextPostfix(object __instance, ref RunBallsStepState __state, bool __result)
        {
            if (!__state.Active || __state.Closed) return;
            BallMgr mgr = BallMgr.I;
            IntPtr ptr = HarmonyHelper.Ptr(__instance);

            if (__state.Owner == 0)
            {
                try
                {
                    if (!__result && mgr != null && mgr.GetBallState() == BallState.kLaunching)
                    {
                        CoopAmmoRecovery.ReleaseLaunch(mgr, 0);
                    }
                }
                finally
                {
                    _activeRunBallsOwner = __state.PrevOwner;
                    BallMgrVirtualizer.ExitWorldBallScope(mgr, __state.WorldScope, reconcileChanges: true);
                    __state.Closed = true;
                }
                return;
            }

            try
            {
                TagNewlyCreatedP2Balls(mgr, _preStepBallScratch);
                if (!__result && mgr != null && mgr.GetBallState() == BallState.kLaunching)
                {
                    CoopAmmoRecovery.ReleaseLaunch(mgr, 1);
                }
            }
            finally
            {
                _activeRunBallsOwner = __state.PrevOwner;
                try { BallMgrVirtualizer.ExitWorldBallScope(mgr, __state.WorldScope, reconcileChanges: true); }
                finally
                {
                    CoopContext.Exit(__state.Context);
                    __state.Closed = true;
                }
            }

            if (!__result && ptr != IntPtr.Zero && _p2RunBallsIterators.Remove(ptr))
            {
                _hasP2CoroutineHandle = false;
                _p2CoroutineHandle = default;
            }
        }

        private static Exception RunBallsMoveNextFinalizer(object __instance, ref RunBallsStepState __state, Exception __exception)
        {
            if (!__state.Active || __state.Closed) return __exception;
            BallMgr mgr = BallMgr.I;
            _activeRunBallsOwner = __state.PrevOwner;
            try
            {
                CoopAmmoRecovery.ReleaseLaunch(mgr, __state.Owner);
                BallMgrVirtualizer.ExitWorldBallScope(mgr, __state.WorldScope, reconcileChanges: true);
            }
            catch { }
            finally
            {
                if (__state.Owner == 1)
                {
                    CoopContext.Exit(__state.Context);
                    IntPtr ptr = HarmonyHelper.Ptr(__instance);
                    if (ptr != IntPtr.Zero && _p2RunBallsIterators.Remove(ptr))
                    {
                        _hasP2CoroutineHandle = false;
                        _p2CoroutineHandle = default;
                    }
                }
                __state.Closed = true;
            }
            return __exception;
        }

        public static bool StartPlayerTwoRunBalls(BallMgr manager, Player playerTwo)
        {
            if (manager == null || playerTwo == null || _p1GameOverActive || _globalRecallActive
                || manager.GetBallState() == BallState.kRecalling || HasActivePlayerTwoBallRun
                || _lastNoLaunchFrame == Time.frameCount)
            {
                return false;
            }
            if (BattleSaveData.I != null)
            {
                float now = BattleSaveData.I.ElapsedTime;
                if (now < _p2NextLaunchAllowedTime && now + 10f >= _p2NextLaunchAllowedTime)
                {
                    return false;
                }
            }

            using (HangWatchdog.Enter(HangWatchdog.PhaseId.P2StartRunBalls))
            {
                var scope = CoopContext.EnterPlayerTwo(playerTwo, CoopContext.ScopeKind.Full);
                IntPtr iterPtr = IntPtr.Zero;
                try
                {
                    if (GameMgr.I == null || GameMgr.I.CurState != GameState.kPlaying || CoopVitals.Down(1))
                    {
                        return false;
                    }
                    CoopAmmoRecovery.PrepareSecondLaunch(manager);
                    HeroInventoryIsolation.RepairRuntimeAgainstWorld(manager);
                    HeroInventoryIsolation.EnsureCurrentBallIndexValid(manager);

                    if (manager.GetBallState() != BallState.kIdle)
                    {
                        return false;
                    }
                    if (!manager.IsReadyToShoot())
                    {
                        return false;
                    }

                    // Ensure stats are valid without overwriting P1's UpgradeMgr outside P2 scope.
                    if (CoopCharacterRuntime.IndependentReady)
                    {
                        CoopCharacterRuntime.SyncCharacter();
                    }

                    if (manager._ballAnim.IsValid)
                    {
                        try { Timing.KillCoroutines(manager._ballAnim); } catch { }
                    }
                    manager._allowAutoChange = true;
                    manager._didAnyBallStartToReturn = false;
                    manager._didAnyBallReturn = false;
                    manager._earlyReturnedFollowers = 0;
                    manager._isShootInterrupted = false;
                    manager._justLaunchedBalls?.Clear();
                    manager._justLaunchedAltBalls?.Clear();

                    CharBattleInst p2Char = CoopCharacterRuntime.SecondChar ?? BattleSaveData.I?.CurChar;
                    if (p2Char != null && p2Char.GetTimeMode() == TimeMode.kTurnBased && playerTwo.CharControllers != null)
                    {
                        for (int i = 0; i < playerTwo.CharControllers.Length; i++)
                        {
                            playerTwo.CharControllers[i]?.SetMoveDir(Vector2.zero);
                        }
                    }

                    var enumerator = manager._RunBalls();
                    iterPtr = HarmonyHelper.Ptr(enumerator);
                    if (iterPtr == IntPtr.Zero) return false;

                    _p2RunBallsIterators.Add(iterPtr);
                    int beforeCount = _p2CreatedBallCount;
                    CoroutineHandle ch = Timing.RunCoroutine(enumerator);
                    bool stillTracked = _p2RunBallsIterators.Contains(iterPtr);

                    if (stillTracked)
                    {
                        if (IsTimingHandleDead(ch))
                        {
                            _p2RunBallsIterators.Remove(iterPtr);
                            _hasP2CoroutineHandle = false;
                            _p2CoroutineHandle = default;
                            stillTracked = false;
                        }
                        else
                        {
                            _p2CoroutineHandle = ch;
                            _hasP2CoroutineHandle = true;
                            manager._ballAnim = ch;
                        }
                    }

                    if (!stillTracked && manager.GetBallState() == BallState.kLaunching)
                    {
                        CoopAmmoRecovery.ReleaseLaunch(manager, 1);
                    }
                    if (!stillTracked && _p2CreatedBallCount == beforeCount)
                    {
                        _lastNoLaunchFrame = Time.frameCount;
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    if (iterPtr != IntPtr.Zero) _p2RunBallsIterators.Remove(iterPtr);
                    if (_p2RunBallsIterators.Count == 0 && manager.GetBallState() == BallState.kLaunching)
                    {
                        CoopAmmoRecovery.ReleaseLaunch(manager, 1);
                    }
                    Plugin.Logger?.LogDebug($"StartPlayerTwoRunBalls failed: {ex.Message}");
                    return false;
                }
                finally
                {
                    CoopContext.Exit(scope);
                }
            }
        }

        public static void CancelActivePlayerTwoCoroutine()
        {
            if (_hasP2CoroutineHandle && _p2CoroutineHandle.IsValid)
            {
                try { Timing.KillCoroutines(_p2CoroutineHandle); } catch { }
            }
            _hasP2CoroutineHandle = false;
            _p2CoroutineHandle = default;
            _p2RunBallsIterators.Clear();
        }

        internal static bool IsTimingHandleAlive(CoroutineHandle ch) => ch.IsValid && !IsTimingHandleDead(ch);

        private static bool IsTimingHandleDead(CoroutineHandle ch)
        {
            if (!ch.IsValid) return true;
            try
            {
                Timing inst = Timing.Instance;
                if (inst == null) return true;
                var map = inst._handleToIndex;
                if (map == null || !map.TryGetValue(ch, out Timing.ProcessIndex procIdx)) return true;
                return inst.CoindexIsNull(procIdx);
            }
            catch
            {
                return _p2RunBallsIterators.Count == 0;
            }
        }

        private static bool IsP2CoroutineAlive()
        {
            if (_p2RunBallsIterators.Count == 0) return false;
            if (_hasP2CoroutineHandle && IsTimingHandleDead(_p2CoroutineHandle))
            {
                _p2RunBallsIterators.Clear();
                _hasP2CoroutineHandle = false;
                _p2CoroutineHandle = default;
                ReleaseP2LaunchIfNeeded();
                return false;
            }
            return _p2RunBallsIterators.Count > 0;
        }

        private static void ReleaseP2LaunchIfNeeded()
        {
            if (CoopContext.PlayerTwo == null || BallMgr.I == null || _releasingP2Launch) return;
            _releasingP2Launch = true;
            var scope = CoopContext.EnterPlayerTwo(CoopContext.PlayerTwo, CoopContext.ScopeKind.Full);
            try
            {
                if (BallMgr.I != null && BallMgr.I.GetBallState() == BallState.kLaunching)
                {
                    CoopAmmoRecovery.ReleaseLaunch(BallMgr.I, 1);
                }
            }
            finally
            {
                CoopContext.Exit(scope);
                _releasingP2Launch = false;
            }
        }

        private static void OnTimingKillAllPostfix()
        {
            if (_p2RunBallsIterators.Count == 0) return;
            _p2RunBallsIterators.Clear();
            _hasP2CoroutineHandle = false;
            _p2CoroutineHandle = default;
            ReleaseP2LaunchIfNeeded();
        }

        private static void OnTimingKillHandlePrefix(CoroutineHandle __0)
        {
            if (_p2RunBallsIterators.Count == 0 || !_hasP2CoroutineHandle || !__0.IsValid) return;
            if (_p2CoroutineHandle == __0)
            {
                _p2RunBallsIterators.Clear();
                _hasP2CoroutineHandle = false;
                _p2CoroutineHandle = default;
                ReleaseP2LaunchIfNeeded();
            }
        }

        private static void CaptureBallPointers(BallMgr mgr, HashSet<IntPtr> dest)
        {
            dest.Clear();
            if (mgr == null) return;
            AddListPointers(mgr.ActiveBalls, dest);
            AddListPointers(mgr.SideBalls, dest);
            AddListPointers(mgr.AdultSideBalls, dest);
        }

        private static void AddListPointers(Il2CppSystem.Collections.Generic.List<BallObj> list, HashSet<IntPtr> dest)
        {
            if (list == null) return;
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                IntPtr ptr = HarmonyHelper.Ptr(list[i]);
                if (ptr != IntPtr.Zero) dest.Add(ptr);
            }
        }

        private static void TagNewlyCreatedP2Balls(BallMgr mgr, HashSet<IntPtr> preExisting)
        {
            if (mgr == null) return;
            TagListNewBalls(mgr.ActiveBalls, preExisting);
            TagListNewBalls(mgr.SideBalls, preExisting);
            TagListNewBalls(mgr.AdultSideBalls, preExisting);
        }

        private static void TagListNewBalls(Il2CppSystem.Collections.Generic.List<BallObj> list, HashSet<IntPtr> preExisting)
        {
            if (list == null) return;
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                IntPtr ptr = HarmonyHelper.Ptr(list[i]);
                if (ptr == IntPtr.Zero || preExisting.Contains(ptr)) continue;
                _p1Balls.Remove(ptr);
                if (_p2Balls.Add(ptr)) _p2CreatedBallCount++;
                CoopRoundStats.NotifyBallOwnership(ptr, 1);
                _p2RemoveCounts.Remove(ptr);
            }
        }
    }
}
