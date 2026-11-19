using System;
using System.Collections.Generic;
using System.Reflection;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Diagnostics;
using BALLxPITLocalCoop.Input;
using BALLxPITLocalCoop.PlayerTwo;
using BALLxPITLocalCoop.UI;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Shapes;
using UnityEngine;
using UnityEngine.Rendering;

namespace BALLxPITLocalCoop.Gameplay
{
    /// <summary>
    /// Routes enemy melee targeting, fair P1/P2 projectile duplication and boss alternation,
    /// dual-body-aware targeting/markers, gamepad vibration isolation, and battle state guards.
    /// </summary>
    public static class CoopCombat
    {
        private struct CreationScope
        {
            public bool Outer;
            public bool Closed;

            public void Close()
            {
                if (!Closed)
                {
                    Closed = true;
                    _creationDepth = Math.Max(0, _creationDepth - 1);
                }
            }
        }

        private struct ArrowStep
        {
            public CoopContext.ScopeToken Context;
            public bool Scoped;

            public void Restore()
            {
                if (Scoped)
                {
                    Scoped = false;
                    CoopContext.Exit(Context);
                }
            }
        }

        private struct PlayerScope
        {
            public int Previous;
        }

        private struct MeleeScope
        {
            public CoopContext.ScopeToken Context;
            public bool Scoped;
        }

        private static bool _creating;
        private static int _creationDepth;
        private static readonly Dictionary<IntPtr, bool> BossTargets = new Dictionary<IntPtr, bool>(16);
        private static readonly HashSet<IntPtr> SecondHoming = new HashSet<IntPtr>(64);
        private static readonly HashSet<IntPtr> FirstOnlyArrows = new HashSet<IntPtr>(64);
        private static readonly HashSet<IntPtr> SecondOnlyArrows = new HashSet<IntPtr>(64);

        private static readonly Disc[] PlayerRings = new Disc[2];
        private static readonly Disc[] PlayerInnerRings = new Disc[2];
        private static readonly Disc[] RescueFieldDiscs = new Disc[2];
        private static readonly Disc[] RescueOuterRings = new Disc[2];
        private static readonly Disc[] RescueProgressRings = new Disc[2];
        private static readonly Disc[] RescueBeaconRings = new Disc[2];

        private static readonly IntPtr[] CachedShadowOwners = new IntPtr[2];
        private static readonly GridPieceShadow[] CachedShadows = new GridPieceShadow[2];
        private static readonly bool[] ShadowResolved = new bool[2];
        private static float _cachedFloorZOffset = float.NaN;
        private static bool _installed;

        public static int SimulationOwner { get; set; } = -1;

        public static Player PlayerAt(int slot) => slot == 1 ? PlayerTwoRuntime.PlayerTwo : PlayerTwoRuntime.PlayerOne;

        public static Vector3 Pos(Player player) => CoopContext.GetPlayerPosition(player);

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GridPieceObj), nameof(GridPieceObj.OnAttackPlayer)),
                prefix: new HarmonyMethod(typeof(CoopCombat), nameof(BeginMelee)),
                finalizer: new HarmonyMethod(typeof(CoopCombat), nameof(EndMelee)),
                label: "GridPieceObj.OnAttackPlayer");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(Player), nameof(Player.MyUpdate)),
                prefix: new HarmonyMethod(typeof(CoopCombat), nameof(BeginSimulation)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(CoopCombat), nameof(EndSimulation)),
                label: "Player.MyUpdate (CoopCombat)");

            foreach (MethodInfo m in typeof(PoolMgr).GetMethods())
            {
                if (m.Name == nameof(PoolMgr.CreateArrow))
                {
                    HarmonyHelper.TryPatch(
                        harmony,
                        m,
                        prefix: new HarmonyMethod(typeof(CoopCombat), nameof(BeginCreation)),
                        postfix: new HarmonyMethod(typeof(CoopCombat), nameof(OnArrowCreated)),
                        finalizer: new HarmonyMethod(typeof(CoopCombat), nameof(EndCreation)),
                        label: "PoolMgr.CreateArrow");
                }
            }

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(ArrowObj), nameof(ArrowObj.MyFixedUpdate)),
                prefix: new HarmonyMethod(typeof(CoopCombat), nameof(OnArrowBefore)),
                finalizer: new HarmonyMethod(typeof(CoopCombat), nameof(OnArrowFinished)),
                label: "ArrowObj.MyFixedUpdate");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(ArrowObj), nameof(ArrowObj.OnHitPlayer), new[] { typeof(PlayerCharController) }),
                prefix: new HarmonyMethod(typeof(CoopCombat), nameof(AllowArrowHitPlayer)),
                label: "ArrowObj.OnHitPlayer");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(ArrowObj), nameof(ArrowObj.Remove)),
                postfix: new HarmonyMethod(typeof(CoopCombat), nameof(OnArrowCleared)),
                label: "ArrowObj.Remove");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(ArrowObj), nameof(ArrowObj.Reset)),
                postfix: new HarmonyMethod(typeof(CoopCombat), nameof(OnArrowCleared)),
                label: "ArrowObj.Reset");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GridPieceObj), nameof(GridPieceObj.Init), new[] { typeof(GridPieceInst) }),
                postfix: new HarmonyMethod(typeof(CoopCombat), nameof(OnGridPieceInitialized)),
                label: "GridPieceObj.Init");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GameMgr), nameof(GameMgr.Start)),
                postfix: new HarmonyMethod(typeof(CoopCombat), nameof(OnNewBattle)),
                label: "GameMgr.Start (CoopCombat)");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GameMgr), nameof(GameMgr.SetState)),
                prefix: new HarmonyMethod(typeof(CoopCombat), nameof(AllowState)),
                postfix: new HarmonyMethod(typeof(CoopCombat), nameof(OnStateChanged)),
                label: "GameMgr.SetState (CoopCombat)");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(InputMgr), nameof(InputMgr.Vibrate), new[] { typeof(float), typeof(float) }),
                prefix: new HarmonyMethod(typeof(CoopCombat), nameof(OnVibrate)),
                label: "InputMgr.Vibrate");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(BallMgr), nameof(BallMgr.RecallBalls)),
                prefix: new HarmonyMethod(typeof(CoopCombat), nameof(CanRecallBalls)),
                label: "BallMgr.RecallBalls (CoopCombat)");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GameOverUI), nameof(GameOverUI.Activate)),
                prefix: new HarmonyMethod(typeof(CoopCombat), nameof(AllowGameOverActivate)),
                label: "GameOverUI.Activate");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(BaseMgr), nameof(BaseMgr.OnReturnToBaseComplete)),
                postfix: new HarmonyMethod(typeof(CoopCombat), nameof(OnReturnToBaseCompletePostfix)),
                label: "BaseMgr.OnReturnToBaseComplete");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(BaseMgr), nameof(BaseMgr.Start)),
                postfix: new HarmonyMethod(typeof(CoopCombat), nameof(OnBaseStartPostfix)),
                label: "BaseMgr.Start (CoopCombat)");
        }

        public static void ResetRound()
        {
            BossTargets.Clear();
            SecondHoming.Clear();
            FirstOnlyArrows.Clear();
            SecondOnlyArrows.Clear();
            CachedShadowOwners[0] = CachedShadowOwners[1] = IntPtr.Zero;
            CachedShadows[0] = CachedShadows[1] = null;
            ShadowResolved[0] = ShadowResolved[1] = false;
            _cachedFloorZOffset = float.NaN;
            SimulationOwner = -1;
            _creating = false;
            _creationDepth = 0;
        }

        public static void Shutdown()
        {
            ResetRound();
            DestroyDiscs(PlayerRings);
            DestroyDiscs(PlayerInnerRings);
            DestroyDiscs(RescueFieldDiscs);
            DestroyDiscs(RescueOuterRings);
            DestroyDiscs(RescueProgressRings);
            DestroyDiscs(RescueBeaconRings);
            _installed = false;
        }

        public static Player NearestLiving(Vector3 origin)
        {
            Player p1 = PlayerAt(0);
            Player p2 = PlayerAt(1);
            if (p1 == null || CoopVitals.Down(0))
            {
                return (p2 != null && !CoopVitals.Down(1)) ? p2 : null;
            }
            if (p2 == null || CoopVitals.Down(1))
            {
                return p1;
            }
            Vector3 p1Pos = CoopContext.GetClosestBodyPosition(p1, origin);
            Vector3 p2Pos = CoopContext.GetClosestBodyPosition(p2, origin);
            float d1 = (p1Pos.x - origin.x) * (p1Pos.x - origin.x) + (p1Pos.y - origin.y) * (p1Pos.y - origin.y);
            float d2 = (p2Pos.x - origin.x) * (p2Pos.x - origin.x) + (p2Pos.y - origin.y) * (p2Pos.y - origin.y);
            return d1 <= d2 ? p1 : p2;
        }

        private static bool TryCheckPlayerInRange(GridPieceObj piece, Player candidate)
        {
            if (piece == null || candidate == null)
            {
                return false;
            }
            bool isSecond = CoopContext.IsPlayerTwo(candidate);
            bool prevSuppress = PlayerTwoPatches.SuppressGridPieceTarget;
            PlayerTwoPatches.SuppressGridPieceTarget = true;
            var scope = isSecond
                ? CoopContext.EnterPlayerTwo(candidate, CoopContext.ScopeKind.PositionOnly)
                : CoopContext.EnterPlayerOne(CoopContext.ScopeKind.PositionOnly);
            try
            {
                return piece.IsPlayerInRange();
            }
            catch
            {
                return false;
            }
            finally
            {
                CoopContext.Exit(scope);
                PlayerTwoPatches.SuppressGridPieceTarget = prevSuppress;
            }
        }

        private static bool BeginMelee(GridPieceObj __instance, ref EnemyAttackResult __result, out MeleeScope __state)
        {
            __state = default;
            if (!CoopVitals.Active || __instance == null || __instance.Inst == null)
            {
                return true;
            }
            Vector3 center = __instance.GetWorldCenterPos();
            Player target = NearestLiving(center);
            if (target == null)
            {
                __result = EnemyAttackResult.kMissed;
                return false;
            }

            Player chosen = TryCheckPlayerInRange(__instance, target) ? target : null;
            if (chosen == null)
            {
                Player p1 = PlayerAt(0);
                Player p2 = PlayerAt(1);
                Player other = HarmonyHelper.SameNative(target, p2)
                    ? (!CoopVitals.Down(0) ? p1 : null)
                    : (!CoopVitals.Down(1) ? p2 : null);
                if (other != null && TryCheckPlayerInRange(__instance, other))
                {
                    chosen = other;
                }
            }
            if (chosen == null)
            {
                __result = EnemyAttackResult.kMissed;
                return false;
            }

            __state = new MeleeScope
            {
                Context = CoopContext.IsPlayerTwo(chosen)
                    ? CoopContext.EnterPlayerTwo(chosen, CoopContext.ScopeKind.Full)
                    : CoopContext.EnterPlayerOne(CoopContext.ScopeKind.Full),
                Scoped = true
            };
            return true;
        }

        private static void EndMelee(ref MeleeScope __state)
        {
            if (__state.Scoped)
            {
                __state.Scoped = false;
                CoopContext.Exit(__state.Context);
            }
        }

        private static void BeginSimulation(Player __instance, out PlayerScope __state)
        {
            __state = new PlayerScope { Previous = SimulationOwner };
            if (CoopVitals.Active)
            {
                SimulationOwner = CoopVitals.Owner(__instance);
            }
        }

        private static void EndSimulation(ref PlayerScope __state)
        {
            SimulationOwner = __state.Previous;
        }

        private static void BeginCreation(out CreationScope __state) =>
            __state = new CreationScope { Outer = _creationDepth++ == 0 };

        private static void EndCreation(ref CreationScope __state) => __state.Close();

        private static void OnArrowCreated(PoolMgr __instance, ArrowObj __result, ArrowType __0, GridPieceInst __1, Vector2 __2, ref CreationScope __state)
        {
            __state.Close();
            IntPtr arrowPtr = HarmonyHelper.Ptr(__result);
            if (arrowPtr != IntPtr.Zero)
            {
                SecondHoming.Remove(arrowPtr);
                FirstOnlyArrows.Remove(arrowPtr);
                SecondOnlyArrows.Remove(arrowPtr);
            }

            if (!__state.Outer || _creating || !CoopMode.Enabled || __result == null || __1 == null || __result.IsFriendly()
                || !CoopCharacterRuntime.IndependentReady || CoopBalance.IsAllyPiece(__1.Type)
                || __1.Type == GridPieceType.kGemspring || __1.Type == GridPieceType.kDefault)
            {
                return;
            }

            Player p1 = PlayerAt(0);
            Player p2 = PlayerAt(1);
            if (p1 == null || p2 == null)
            {
                return;
            }

            Vector3 spawnPos = __result.transform.position;
            Vector2 toP1 = ((Vector2)(CoopContext.GetClosestBodyPosition(p1, spawnPos) - spawnPos)).normalized;
            Vector2 toP2 = ((Vector2)(CoopContext.GetClosestBodyPosition(p2, spawnPos) - spawnPos)).normalized;
            if (toP1.sqrMagnitude < 0.01f || toP2.sqrMagnitude < 0.01f)
            {
                return;
            }

            if (CoopVitals.Down(0))
            {
                if (!CoopVitals.Down(1))
                {
                    __result.SetAimDir(toP2);
                    TrackSecond(__result, exclusiveToSecond: false);
                }
                return;
            }
            if (CoopVitals.Down(1))
            {
                return;
            }

            // Determine which player the original arrow was aimed at (works even when P2 was the closer enemy target).
            Vector2 aimDir = __2.sqrMagnitude > 0.0001f ? __2.normalized : toP1;
            float offsetP1 = Vector2.SignedAngle(toP1, aimDir);
            float offsetP2 = Vector2.SignedAngle(toP2, aimDir);
            bool aimedAtP2 = Mathf.Abs(offsetP2) < Mathf.Abs(offsetP1);

            if (__1.Type.IsBoss())
            {
                float minOffset = aimedAtP2 ? Mathf.Abs(offsetP2) : Mathf.Abs(offsetP1);
                if (minOffset <= 15f)
                {
                    IntPtr bossPtr = HarmonyHelper.Ptr(__1);
                    BossTargets.TryGetValue(bossPtr, out bool aimSecond);
                    BossTargets[bossPtr] = !aimSecond;
                    if (aimSecond)
                    {
                        __result.SetAimDir(toP2);
                        TrackSecond(__result, exclusiveToSecond: false);
                    }
                    else
                    {
                        __result.SetAimDir(toP1);
                    }
                }
                return;
            }

            // Regular enemies: duplicate aimed volley fairly — original is exclusive to its target, clone is exclusive to the other player.
            Vector2 otherTargetDir = aimedAtP2 ? toP1 : toP2;
            float relativeOffset = aimedAtP2 ? offsetP2 : offsetP1;

            if (aimedAtP2)
            {
                TrackSecond(__result, exclusiveToSecond: true);
            }
            else if (arrowPtr != IntPtr.Zero)
            {
                FirstOnlyArrows.Add(arrowPtr);
            }

            float rad = (Mathf.Atan2(otherTargetDir.y, otherTargetDir.x) * Mathf.Rad2Deg + relativeOffset) * Mathf.Deg2Rad;
            _creating = true;
            try
            {
                ArrowObj dup = __instance.CreateArrow(__0, __1, new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)), spawnPos);
                if (dup != null)
                {
                    if (aimedAtP2)
                    {
                        IntPtr dupPtr = HarmonyHelper.Ptr(dup);
                        if (dupPtr != IntPtr.Zero) FirstOnlyArrows.Add(dupPtr);
                    }
                    else
                    {
                        TrackSecond(dup, exclusiveToSecond: true);
                    }
                }
            }
            finally
            {
                _creating = false;
            }
        }

        private static void TrackSecond(ArrowObj arrow, bool exclusiveToSecond)
        {
            IntPtr ptr = HarmonyHelper.Ptr(arrow);
            if (ptr == IntPtr.Zero)
            {
                return;
            }
            if (exclusiveToSecond)
            {
                SecondOnlyArrows.Add(ptr);
            }
            if (arrow.Type == ArrowType.kHoming || arrow.Type == ArrowType.kHomingRock)
            {
                SecondHoming.Add(ptr);
            }
        }

        private static void OnArrowCleared(ArrowObj __instance)
        {
            IntPtr ptr = HarmonyHelper.Ptr(__instance);
            if (ptr != IntPtr.Zero)
            {
                FirstOnlyArrows.Remove(ptr);
                SecondOnlyArrows.Remove(ptr);
                SecondHoming.Remove(ptr);
            }
        }

        private static void OnGridPieceInitialized(GridPieceObj __instance)
        {
            IntPtr instPtr = HarmonyHelper.Ptr(__instance?.Inst);
            if (instPtr != IntPtr.Zero)
            {
                BossTargets.Remove(instPtr);
            }
        }

        private static bool IsPlayerTwoController(PlayerCharController ctrl)
        {
            IntPtr ptr = HarmonyHelper.Ptr(ctrl);
            if (ptr == IntPtr.Zero)
            {
                return false;
            }
            if (HarmonyHelper.Ptr(PlayerTwoRuntime.PlayerTwoCharController) == ptr)
            {
                return true;
            }
            Player p2 = PlayerAt(1);
            var ctrls = p2?.CharControllers;
            if (ctrls != null)
            {
                for (int i = 0; i < ctrls.Length; i++)
                {
                    if (HarmonyHelper.Ptr(ctrls[i]) == ptr)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool AllowArrowHitPlayer(ArrowObj __instance, PlayerCharController __0)
        {
            if (!CoopMode.Enabled || __instance == null)
            {
                return true;
            }
            IntPtr ptr = HarmonyHelper.Ptr(__instance);
            if (SecondOnlyArrows.Contains(ptr))
            {
                return IsPlayerTwoController(__0);
            }
            if (FirstOnlyArrows.Contains(ptr))
            {
                return !IsPlayerTwoController(__0);
            }
            return true;
        }

        private static void OnNewBattle()
        {
            ResetRound();
            CoopMode.ClearReturningToBase();
            CoopProgression.ResetRunRewards();
            if (CoopMode.Enabled)
            {
                GamepadRouter.EnsureDefaultAutoShoot();
            }
        }

        private static void OnArrowBefore(ArrowObj __instance, out ArrowStep __state)
        {
            __state = default;
            if (__instance == null)
            {
                return;
            }
            IntPtr ptr = HarmonyHelper.Ptr(__instance);
            if (CoopVitals.Down(1))
            {
                SecondHoming.Remove(ptr);
            }
            if (CoopMode.Enabled && CoopCharacterRuntime.IndependentReady
                && (SecondHoming.Contains(ptr) || (CoopVitals.Down(0) && !CoopVitals.Down(1)))
                && !__instance.IsFriendly())
            {
                Player p2 = PlayerAt(1);
                if (p2 != null)
                {
                    __state = new ArrowStep
                    {
                        Context = CoopContext.EnterPlayerTwo(p2, CoopContext.ScopeKind.PositionOnly),
                        Scoped = true
                    };
                }
            }
        }

        private static void OnArrowFinished(ref ArrowStep __state) => __state.Restore();

        private static void OnStateChanged(GameState __0)
        {
            if (!CoopMode.Enabled || __0 != GameState.kPlaying)
            {
                return;
            }
            for (int i = 0; i < 2; i++)
            {
                Player p = PlayerAt(i);
                if (p != null)
                {
                    p.IsRunningLevelUp = false;
                }
            }
        }

        private static bool AllowState(GameMgr __instance, GameState __0)
        {
            bool guardScene = CoopMode.Enabled || CoopMode.ReturningToBase;
            if (guardScene && (__instance == null || !HarmonyHelper.IsInGameScene()))
            {
                return false;
            }
            if (CoopMode.ReturningToBase
                && (__0 == GameState.kRevive || __0 == GameState.kGameOver || __0 == GameState.kLevelUp
                    || __0 == GameState.kPickTreasure || __0 == GameState.kFoundBlueprint || __0 == GameState.kFoundEgg
                    || __0 == GameState.kBonusBall || __0 == GameState.kBonusPassive))
            {
                return false;
            }
            if (CoopMode.Enabled && CoopVitals.Active && !CoopVitals.Life.Defeated
                && (__0 == GameState.kRevive || __0 == GameState.kGameOver))
            {
                return false;
            }
            if (CoopMode.Enabled && BattleSaveData.I != null && BattleSaveData.I.CompletedLevel)
            {
                return __0 != GameState.kLevelUp
                    && __0 != GameState.kPickTreasure
                    && __0 != GameState.kBonusBall
                    && __0 != GameState.kBonusPassive;
            }
            return true;
        }

        private static bool AllowGameOverActivate(GameOverUI __instance)
        {
            if (CoopMode.Enabled || CoopMode.ReturningToBase || !HarmonyHelper.IsInGameScene())
            {
                return false;
            }
            return HarmonyHelper.IsUnityAlive(__instance);
        }

        private static void OnReturnToBaseCompletePostfix() => CoopMode.EnsurePostCoopBaseClean();
        private static void OnBaseStartPostfix() => CoopMode.EnsurePostCoopBaseClean();

        private static bool CanRecallBalls(BallMgr __instance) =>
            !CoopMode.Enabled || (__instance != null && HarmonyHelper.IsInGameScene());

        private static bool OnVibrate(float __0, float __1)
        {
            if (!CoopMode.Enabled || !CoopCharacterRuntime.IndependentReady)
            {
                return true;
            }
            int slot = CoopContext.IsPlayerTwoContext ? 1 : 0;
            // Route explicitly to that slot's assigned gamepad (so P1 hits in KB+M+Pad mode never rumble P2's controller!).
            if (GamepadRouter.UsesGamepadForSlot(slot))
            {
                GamepadRouter.VibratePad(slot, __0, __1);
            }
            return false;
        }

        // --- Visual Floor Rings & Rescue Aura ---

        private static void DestroyDiscs(Disc[] discs)
        {
            for (int i = 0; i < discs.Length; i++)
            {
                if (HarmonyHelper.IsUnityAlive(discs[i]))
                {
                    UnityEngine.Object.Destroy(discs[i].gameObject);
                }
                discs[i] = null;
            }
        }

        private static Disc CreateGroundDisc(
            string name,
            DiscType type,
            float radius,
            float thickness,
            Color color,
            int sortingOrder,
            ShapesBlendMode blendMode = ShapesBlendMode.Transparent)
        {
            GameObject go = new GameObject(name);
            Disc disc = go.AddComponent(Il2CppType.Of<Disc>()).Cast<Disc>();
            disc.Type = type;
            disc.Geometry = DiscGeometry.Flat2D;
            disc.BlendMode = blendMode;
            disc.ZTest = CompareFunction.LessEqual;
            disc.SortingOrder = sortingOrder;
            disc.RenderQueue = 2950;
            disc.Radius = radius;
            disc.Thickness = thickness;
            disc.Color = color;
            return disc;
        }

        private static Vector3 ResolveGroundCenter(Player player, Vector3 fallbackPos)
        {
            if (player != null)
            {
                try
                {
                    int slot = CoopContext.IsPlayerTwo(player) ? 1 : 0;
                    IntPtr ptr = HarmonyHelper.Ptr(player);
                    if (CachedShadowOwners[slot] != ptr || !ShadowResolved[slot])
                    {
                        CachedShadowOwners[slot] = ptr;
                        CachedShadows[slot] = player.GetComponentInChildren<GridPieceShadow>(true);
                        ShadowResolved[slot] = true;
                    }
                    GridPieceShadow shadow = CachedShadows[slot];
                    if (HarmonyHelper.IsUnityAlive(shadow))
                    {
                        Vector3 sp = shadow.transform.position;
                        _cachedFloorZOffset = sp.z - fallbackPos.z;
                        return new Vector3(sp.x, sp.y, sp.z - 0.008f);
                    }
                }
                catch { }
            }
            float z = float.IsNaN(_cachedFloorZOffset)
                ? fallbackPos.z - 0.008f
                : fallbackPos.z + _cachedFloorZOffset - 0.008f;
            return new Vector3(fallbackPos.x, fallbackPos.y - 0.12f, z);
        }

        private static void SetRingGroupActive(int slot, bool active)
        {
            if (HarmonyHelper.IsUnityAlive(PlayerRings[slot])) PlayerRings[slot].gameObject.SetActive(active);
            if (HarmonyHelper.IsUnityAlive(PlayerInnerRings[slot])) PlayerInnerRings[slot].gameObject.SetActive(active);
        }

        private static void SetRescueGroupActive(int slot, bool active)
        {
            if (HarmonyHelper.IsUnityAlive(RescueFieldDiscs[slot])) RescueFieldDiscs[slot].gameObject.SetActive(active);
            if (HarmonyHelper.IsUnityAlive(RescueOuterRings[slot])) RescueOuterRings[slot].gameObject.SetActive(active);
            if (HarmonyHelper.IsUnityAlive(RescueProgressRings[slot])) RescueProgressRings[slot].gameObject.SetActive(active);
            if (HarmonyHelper.IsUnityAlive(RescueBeaconRings[slot])) RescueBeaconRings[slot].gameObject.SetActive(active);
        }

        public static void Tick()
        {
            using (HangWatchdog.Enter(HangWatchdog.PhaseId.CombatTick))
            {
                bool inGame = CoopMode.Enabled && HarmonyHelper.IsInGameScene() && !CoopRoundStats.Complete;
                if (!inGame)
                {
                    _cachedFloorZOffset = float.NaN;
                    for (int i = 0; i < 2; i++)
                    {
                        SetRingGroupActive(i, false);
                        SetRescueGroupActive(i, false);
                    }
                    return;
                }

                Color p1C = PlayerPalette.GetPlayerColor(0);
                Color p2C = PlayerPalette.GetPlayerColor(1);
                float now = Time.unscaledTime;

                for (int i = 0; i < 2; i++)
                {
                    Color baseCol = i == 0 ? p1C : p2C;
                    Color slotColor = new Color(baseCol.r, baseCol.g, baseCol.b, 0.85f);

                    Player p = PlayerAt(i);
                    bool alive = p != null && p.gameObject.activeInHierarchy && !CoopVitals.Down(i);
                    if (alive && PlayerAt(1 - i) != null)
                    {
                        if (!HarmonyHelper.IsUnityAlive(PlayerRings[i]))
                        {
                            PlayerRings[i] = CreateGroundDisc($"CoopP{i + 1}Ring", DiscType.Ring, 0.62f, 0.055f, slotColor, -2);
                        }
                        if (!HarmonyHelper.IsUnityAlive(PlayerInnerRings[i]))
                        {
                            Color inner = new Color(slotColor.r, slotColor.g, slotColor.b, 0.34f);
                            PlayerInnerRings[i] = CreateGroundDisc($"CoopP{i + 1}InnerRing", DiscType.Ring, 0.50f, 0.025f, inner, -3);
                        }

                        float pulse = 1f + 0.04f * Mathf.Sin(now * 3.2f + i * 1.6f);
                        Vector3 center = ResolveGroundCenter(p, Pos(p));
                        PlayerRings[i].Radius = 0.62f * pulse;
                        PlayerRings[i].Color = slotColor;
                        PlayerRings[i].transform.position = center;
                        PlayerRings[i].gameObject.SetActive(true);

                        PlayerInnerRings[i].Radius = 0.50f * (2f - pulse);
                        PlayerInnerRings[i].Color = new Color(slotColor.r, slotColor.g, slotColor.b, 0.34f);
                        PlayerInnerRings[i].transform.position = center;
                        PlayerInnerRings[i].gameObject.SetActive(true);
                    }
                    else
                    {
                        SetRingGroupActive(i, false);
                    }

                    bool showRescue = CoopVitals.Active
                        && CoopVitals.Life.CanManualRevive(i)
                        && CoopVitals.HasDownedPos[i];
                    if (showRescue)
                    {
                        float radius = CoopVitals.ManualRescueRadius;
                        if (!HarmonyHelper.IsUnityAlive(RescueFieldDiscs[i]))
                        {
                            Disc field = CreateGroundDisc($"CoopRescueField{i}", DiscType.Disc, radius, 0f, slotColor, -6, ShapesBlendMode.Additive);
                            field.ColorMode = Disc.DiscColorMode.Radial;
                            RescueFieldDiscs[i] = field;
                        }
                        if (!HarmonyHelper.IsUnityAlive(RescueOuterRings[i]))
                        {
                            Disc outer = CreateGroundDisc($"CoopRescueOuter{i}", DiscType.Ring, radius, 0.065f, slotColor, -4, ShapesBlendMode.Additive);
                            outer.Dashed = true;
                            outer.DashSpace = DashSpace.FixedCount;
                            outer.DashType = DashType.Rounded;
                            outer.DashSnap = DashSnapping.Tiling;
                            outer.DashSize = 14f;
                            outer.DashSpacing = 0.42f;
                            RescueOuterRings[i] = outer;
                        }
                        if (!HarmonyHelper.IsUnityAlive(RescueProgressRings[i]))
                        {
                            Disc arc = CreateGroundDisc($"CoopRescueFill{i}", DiscType.Arc, radius - 0.11f, 0.085f, new Color(0.42f, 1f, 0.72f, 0.95f), -3, ShapesBlendMode.Additive);
                            arc.ArcEndCaps = ArcEndCap.Round;
                            RescueProgressRings[i] = arc;
                        }
                        if (!HarmonyHelper.IsUnityAlive(RescueBeaconRings[i]))
                        {
                            RescueBeaconRings[i] = CreateGroundDisc($"CoopRescueBeacon{i}", DiscType.Ring, 0.44f, 0.045f, slotColor, -5, ShapesBlendMode.Additive);
                        }

                        Vector3 downedPos = ResolveGroundCenter(null, CoopVitals.DownedPos[i]);
                        bool inRing = CoopVitals.ManualRescueInRing[i];
                        float progress = Mathf.Clamp01(CoopVitals.Life.RescueProgress[i]);
                        float beat = 0.5f + 0.5f * Mathf.Sin(now * (inRing ? 6.8f : 3.4f));

                        Color goldTone = new Color(1f, 0.84f, 0.34f, 1f);
                        Color emeraldTone = new Color(0.38f, 1f, 0.68f, 1f);
                        Color activeTone = inRing
                            ? Color.Lerp(goldTone, emeraldTone, progress)
                            : Color.Lerp(slotColor, goldTone, 0.35f + 0.25f * beat);

                        float auraAlpha = inRing ? (0.14f + 0.12f * progress + 0.05f * beat) : (0.07f + 0.06f * beat);
                        RescueFieldDiscs[i].Radius = radius - 0.03f;
                        RescueFieldDiscs[i].ColorInner = new Color(activeTone.r, activeTone.g, activeTone.b, auraAlpha * 0.45f);
                        RescueFieldDiscs[i].ColorOuter = new Color(activeTone.r, activeTone.g, activeTone.b, auraAlpha);
                        RescueFieldDiscs[i].transform.position = downedPos;
                        RescueFieldDiscs[i].gameObject.SetActive(true);

                        RescueOuterRings[i].Radius = radius;
                        RescueOuterRings[i].Thickness = inRing ? 0.072f : 0.052f;
                        RescueOuterRings[i].DashOffset = now * (inRing ? 1.85f : 0.65f);
                        RescueOuterRings[i].Color = new Color(activeTone.r, activeTone.g, activeTone.b, inRing ? 0.88f : 0.55f + 0.20f * beat);
                        RescueOuterRings[i].transform.position = downedPos;
                        RescueOuterRings[i].gameObject.SetActive(true);

                        RescueBeaconRings[i].Radius = Mathf.Lerp(0.38f, 0.52f, beat);
                        RescueBeaconRings[i].Thickness = inRing ? 0.055f : 0.04f;
                        RescueBeaconRings[i].Color = new Color(slotColor.r, slotColor.g, slotColor.b, inRing ? 0.82f : 0.45f + 0.30f * (1f - beat));
                        RescueBeaconRings[i].transform.position = downedPos;
                        RescueBeaconRings[i].gameObject.SetActive(true);

                        if (progress > 0.01f)
                        {
                            float startAngle = MathF.PI * 0.5f;
                            float endAngle = startAngle - progress * MathF.PI * 2f;
                            RescueProgressRings[i].Radius = radius - 0.105f;
                            RescueProgressRings[i].Thickness = 0.08f + 0.015f * beat;
                            RescueProgressRings[i].AngRadiansStart = startAngle;
                            RescueProgressRings[i].AngRadiansEnd = endAngle;
                            Color sweepColor = Color.Lerp(goldTone, new Color(0.55f, 1f, 0.78f, 1f), progress);
                            RescueProgressRings[i].Color = new Color(sweepColor.r, sweepColor.g, sweepColor.b, 0.94f);
                            RescueProgressRings[i].transform.position = downedPos;
                            RescueProgressRings[i].gameObject.SetActive(true);
                        }
                        else
                        {
                            RescueProgressRings[i].gameObject.SetActive(false);
                        }
                    }
                    else
                    {
                        SetRescueGroupActive(i, false);
                    }
                }
            }
        }
    }
}
