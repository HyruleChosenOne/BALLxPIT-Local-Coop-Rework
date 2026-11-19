using System;
using System.Collections.Generic;
using System.Reflection;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.PlayerTwo;
using HarmonyLib;
using UnityEngine;

namespace BALLxPITLocalCoop.Gameplay
{
    /// <summary>
    /// Keeps per-player BallMgr early-return queues, active follower counts, launch states,
    /// and HeroInst.Obj references healthy across both co-op players.
    /// Consolidates the former CoopAmmoReload and CoopShootingRecovery classes.
    /// </summary>
    public static class CoopAmmoRecovery
    {
        private static bool _installed;
        private static readonly HashSet<IntPtr> _seenEarlyPointers = new HashSet<IntPtr>(64);

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(BallMgr), nameof(BallMgr.CreateBall), new[] { typeof(HeroInst), typeof(bool), typeof(Vector2), typeof(BallSourceType) }),
                postfix: new HarmonyMethod(typeof(CoopAmmoRecovery), nameof(OnBallCreated)),
                label: "BallMgr.CreateBall (CoopAmmoRecovery)");

            foreach (string auxName in new[] { "CreateSideBall", "CreateAdultSideBall", "CreatePassiveSideBall" })
            {
                HarmonyHelper.TryPatch(
                    harmony,
                    AccessTools.Method(typeof(BallMgr), auxName),
                    postfix: new HarmonyMethod(typeof(CoopAmmoRecovery), nameof(OnBallCreated)),
                    label: $"BallMgr.{auxName} (CoopAmmoRecovery)");
            }

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(BallMgr), nameof(BallMgr.MoveBalls)),
                prefix: new HarmonyMethod(typeof(CoopAmmoRecovery), nameof(ScrubDeadBallsBeforeMove)) { priority = Priority.First },
                label: "BallMgr.MoveBalls (CoopAmmoRecovery)");
        }

        public static void ResetRound()
        {
            _seenEarlyPointers.Clear();
        }

        public static void Shutdown()
        {
            ResetRound();
            _installed = false;
        }

        public static void Tick()
        {
            if (!CoopMode.Enabled || !HarmonyHelper.IsInGameScene() || BallMgr.I == null || GameMgr.I == null)
            {
                return;
            }
            if (GameMgr.I.CurState != GameState.kPlaying)
            {
                return;
            }

            BallMgr mgr = BallMgr.I;
            NormalizeSlotRuntime(mgr, 0);

            Player p2 = PlayerTwoRuntime.PlayerTwo;
            if (p2 != null)
            {
                var scope = CoopContext.EnterPlayerTwo(p2, CoopContext.ScopeKind.Full);
                try
                {
                    NormalizeSlotRuntime(mgr, 1);
                }
                finally
                {
                    CoopContext.Exit(scope);
                }
            }
        }

        private static void OnBallCreated(BallObj __result)
        {
            if (__result == null || !CoopMode.Enabled)
            {
                return;
            }
            BallMgrVirtualizer.RemoveFromAllEarlyReturnedLists(__result);
        }

        internal static void PrepareSecondLaunch(BallMgr mgr)
        {
            if (!CoopMode.Enabled || mgr == null || PlayerTwoPatches.HasActivePlayerTwoBallRun)
            {
                return;
            }

            BallState state = mgr.GetBallState();
            if (state != BallState.kLaunching && state != BallState.kRecalling)
            {
                mgr._allowAutoChange = true;
                mgr._isShootInterrupted = false;
            }
        }

        internal static void ReleaseLaunch(BallMgr mgr, int owner)
        {
            if (mgr == null)
            {
                return;
            }

            BallState state = mgr.GetBallState();
            if (state != BallState.kLaunching)
            {
                return;
            }

            bool turnBased = false;
            try
            {
                CharBattleInst ch = CoopCharacterRuntime.CharacterFor(owner) ?? BattleSaveData.I?.CurChar;
                turnBased = ch != null && ch.GetTimeMode() == TimeMode.kTurnBased;
            }
            catch { }

            mgr._allowAutoChange = true;
            mgr._isShootInterrupted = false;
            mgr.SetBallState(BallState.kBouncing);
            DrainEarlyReturnedBallsSafely(mgr, owner);

            if (mgr.GetBallState() == BallState.kBouncing)
            {
                // For real-time always return to kIdle; for turn-based also return to kIdle if no owner balls remain active (prevents softlock!).
                if (!turnBased || !HasAnyOwnedActiveBall(mgr, owner))
                {
                    mgr.SetBallState(BallState.kIdle);
                }
            }
        }

        internal static void ScrubEarlyReturnedBalls(BallMgr mgr, int owner)
        {
            var early = mgr?.EarlyReturnedBalls;
            if (early == null)
            {
                return;
            }
            _seenEarlyPointers.Clear();
            for (int i = early.Count - 1; i >= 0; i--)
            {
                BallObj b = early[i];
                IntPtr ptr = HarmonyHelper.Ptr(b);
                if (ptr == IntPtr.Zero || b.gameObject == null
                    || !PlayerTwoPatches.MatchesBallOwner(b, owner)
                    || !_seenEarlyPointers.Add(ptr))
                {
                    early.RemoveAt(i);
                }
            }
            _seenEarlyPointers.Clear();
        }

        internal static void DrainEarlyReturnedBallsSafely(BallMgr mgr, int owner)
        {
            var early = mgr?.EarlyReturnedBalls;
            if (mgr == null || early == null || early.Count == 0)
            {
                if (mgr != null) mgr._earlyReturnedFollowers = 0;
                return;
            }

            ScrubEarlyReturnedBalls(mgr, owner);
            if (early.Count == 0)
            {
                mgr._earlyReturnedFollowers = 0;
                return;
            }

            // Use a local snapshot list so re-entrant RemoveBall calls can never corrupt the loop!
            var toDrain = new List<BallObj>(early.Count);
            for (int i = 0; i < early.Count; i++)
            {
                BallObj b = early[i];
                if (HarmonyHelper.Ptr(b) != IntPtr.Zero)
                {
                    toDrain.Add(b);
                }
            }

            early.Clear();
            mgr._earlyReturnedFollowers = 0;

            for (int i = 0; i < toDrain.Count; i++)
            {
                BallObj b = toDrain[i];
                try
                {
                    BallReturnType returnType = BallReturnType.kCatch;
                    bool isSide = false;
                    try
                    {
                        isSide = b.IsSide;
                        if (!b.IsBaby() && b.Inst != null)
                        {
                            returnType = b.Inst.LastReturnType;
                        }
                    }
                    catch { }
                    mgr.RemoveBall(b, isSide, returnType);
                    BallMgrVirtualizer.RemoveBallFromAllScopesAndEarlyLists(b);
                }
                catch
                {
                    try
                    {
                        if (b.Inst != null && HarmonyHelper.SameNative(b.Inst.Obj, b))
                        {
                            b.Inst.Obj = null;
                        }
                        BallMgrVirtualizer.RemoveBallFromAllScopesAndEarlyLists(b);
                        mgr.ActiveBalls?.Remove(b);
                        b.IsActive = false;
                        b.gameObject?.SetActive(false);
                    }
                    catch { }
                }
            }
            early.Clear();
            mgr._earlyReturnedFollowers = 0;
        }

        private static bool HasAnyOwnedActiveBall(BallMgr mgr, int owner)
        {
            if (mgr == null) return false;
            if (mgr.AllInOneBall != null && mgr.AllInOneBall.IsActive && PlayerTwoPatches.MatchesBallOwner(mgr.AllInOneBall, owner))
            {
                return true;
            }
            var active = mgr.ActiveBalls;
            if (active != null)
            {
                for (int i = 0; i < active.Count; i++)
                {
                    BallObj b = active[i];
                    if (HarmonyHelper.IsUnityAlive(b) && b.IsActive && PlayerTwoPatches.MatchesBallOwner(b, owner))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static void NormalizeSlotRuntime(BallMgr mgr, int owner)
        {
            if (mgr == null)
            {
                return;
            }

            ScrubEarlyReturnedBalls(mgr, owner);

            BallState state = mgr.GetBallState();
            bool coroutineActive = owner == 1
                ? PlayerTwoPatches.HasActivePlayerTwoBallRun
                : PlayerTwoPatches.IsTimingHandleAlive(mgr._ballAnim);

            if (!coroutineActive && state == BallState.kLaunching)
            {
                ReleaseLaunch(mgr, owner);
                state = mgr.GetBallState();
            }

            if (state != BallState.kLaunching && mgr.EarlyReturnedBalls != null && mgr.EarlyReturnedBalls.Count > 0)
            {
                DrainEarlyReturnedBallsSafely(mgr, owner);
            }

            if (state != BallState.kLaunching && (mgr.EarlyReturnedBalls?.Count ?? 0) == 0)
            {
                mgr._earlyReturnedFollowers = 0;
                ReconcileActiveFollowerCount(mgr, owner);
            }
            else if (mgr._earlyReturnedFollowers < 0)
            {
                mgr._earlyReturnedFollowers = 0;
            }

            ClearStaleHeroObjects(mgr, owner);
        }

        private static bool IsPlayerFollowerBaby(BallObj b)
        {
            try
            {
                return b.IsBaby()
                    && !b.IsSide
                    && b.SrcType == BallSourceType.kPlayer
                    && b.PassiveSrc == null;
            }
            catch
            {
                return false;
            }
        }

        private static void ReconcileActiveFollowerCount(BallMgr mgr, int owner)
        {
            var active = mgr?.ActiveBalls;
            if (active == null)
            {
                return;
            }
            try
            {
                CharBattleInst ch = CoopCharacterRuntime.CharacterFor(owner) ?? BattleSaveData.I?.CurChar;
                if (ch != null && (ch.HasProp(CharProp.kAllInOne) || ch.HasProp(CharProp.kMultiHero)))
                {
                    if (mgr._numActiveFollowers < 0) mgr._numActiveFollowers = 0;
                    return;
                }
            }
            catch { }

            BallState mgrState = mgr.GetBallState();
            bool canScrubInactive = mgrState != BallState.kLaunching && (mgr.EarlyReturnedBalls == null || mgr.EarlyReturnedBalls.Count == 0);
            int counted = 0;
            int scrubbedInactiveBabies = 0;

            for (int i = active.Count - 1; i >= 0; i--)
            {
                BallObj b = active[i];
                if (b == null)
                {
                    if (canScrubInactive) active.RemoveAt(i);
                    continue;
                }
                if (!PlayerTwoPatches.MatchesBallOwner(b, owner))
                {
                    continue;
                }
                try
                {
                    bool isAlive = HarmonyHelper.IsUnityAlive(b) && b.IsActive && b.gameObject != null && b.gameObject.activeInHierarchy;
                    bool isFollowerBaby = HarmonyHelper.Ptr(b) != IntPtr.Zero && IsPlayerFollowerBaby(b);
                    if (!isAlive)
                    {
                        if (canScrubInactive)
                        {
                            active.RemoveAt(i);
                            if (isFollowerBaby) scrubbedInactiveBabies++;
                        }
                        continue;
                    }
                    if (isFollowerBaby)
                    {
                        counted++;
                    }
                }
                catch { }
            }

            int prevActive = mgr._numActiveFollowers;
            if (prevActive != counted)
            {
                mgr._numActiveFollowers = counted;
                if (!PlayerTwoPatches.IsGlobalRecallActive && scrubbedInactiveBabies > 0 && prevActive > counted && mgr.RechargingFollowers != null)
                {
                    int missing = Math.Min(scrubbedInactiveBabies, prevActive - counted);
                    int maxFollowers = UpgradeMgr.I != null ? Math.Max(0, UpgradeMgr.I.NumFollowers) : int.MaxValue;
                    while (missing > 0 && counted + mgr.RechargingFollowers.Count < maxFollowers)
                    {
                        mgr.RechargingFollowers.Add(0f);
                        missing--;
                    }
                }
            }
        }

        private static void ClearStaleHeroObjects(BallMgr mgr, int owner)
        {
            var heroes = owner == 1 ? CoopCharacterRuntime.HeroesFor(1) : (CoopCharacterRuntime.HeroesFor(0) ?? BattleSaveData.I?.Heroes);
            if (heroes == null)
            {
                return;
            }
            for (int i = 0; i < heroes.Count; i++)
            {
                HeroInst hero = heroes[i];
                BallObj ball = hero?.Obj;
                if (ball == null)
                {
                    continue;
                }
                bool aliveInWorld = HarmonyHelper.IsUnityAlive(ball)
                    && ball.gameObject != null
                    && ball.IsActive
                    && (HarmonyHelper.SameNative(mgr.AllInOneBall, ball)
                        || BallMgrVirtualizer.ContainsBall(mgr.ActiveBalls, ball)
                        || BallMgrVirtualizer.ContainsBall(mgr.SideBalls, ball)
                        || BallMgrVirtualizer.ContainsBall(mgr.AdultSideBalls, ball)
                        || BallMgrVirtualizer.ContainsBall(mgr.EarlyReturnedBalls, ball));
                if (!aliveInWorld)
                {
                    hero.Obj = null;
                    try
                    {
                        hero.LastReturnTime = BattleSaveData.I != null ? BattleSaveData.I.ElapsedTime : 0f;
                    }
                    catch { }
                }
            }
        }

        private static void ScrubDeadBallsBeforeMove(BallMgr __instance)
        {
            if (!CoopMode.Enabled || __instance == null)
            {
                return;
            }
            ScrubDeadList(__instance.ActiveBalls);
            ScrubDeadList(__instance.SideBalls);
            ScrubDeadList(__instance.AdultSideBalls);
        }

        private static void ScrubDeadList(Il2CppSystem.Collections.Generic.List<BallObj> list)
        {
            if (list == null)
            {
                return;
            }
            for (int i = list.Count - 1; i >= 0; i--)
            {
                BallObj b = list[i];
                if (!HarmonyHelper.IsUnityAlive(b) || b.gameObject == null)
                {
                    list.RemoveAt(i);
                }
            }
        }
    }
}
