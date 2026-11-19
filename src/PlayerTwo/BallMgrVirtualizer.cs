using System;
using System.Collections.Generic;
using BALLxPITLocalCoop.Core;
using UnityEngine;

namespace BALLxPITLocalCoop.PlayerTwo
{
    /// <summary>
    /// Virtualizes the shared BallMgr singleton across P1 and P2 with guaranteed independent native Lists,
    /// O(N) pooled-HashSet world ball list scoping, and safe nested opposite-owner scoping.
    /// </summary>
    internal static class BallMgrVirtualizer
    {
        private sealed class Snapshot
        {
            public Il2CppSystem.Collections.Generic.List<BallObj> EarlyReturnedBalls;
            public int CurBallIdx;
            public int NumActiveFollowers;
            public int EarlyReturnedFollowers;
            public Il2CppSystem.Collections.Generic.List<float> RechargingFollowers;
            public bool DidAnyBallStartToReturn;
            public bool DidAnyBallReturn;
            public float LastShootCompleteUnityTime;
            public BallState CurState;
            public bool AllowAutoChange;
            public MEC.CoroutineHandle BallAnim;
            public float LastShootCompleteTime;
            public bool IsShootInterrupted;
            public Il2CppSystem.Collections.Generic.List<BallObj> JustLaunchedBalls;
            public Il2CppSystem.Collections.Generic.List<BallObj> JustLaunchedAltBalls;
            public Vector3 LobTgtPos;
            public Vector2 AltAimDir;
            public BallObj AllInOneBall;
        }

        internal sealed class WorldBallScope
        {
            public WorldBallScope Parent;
            public Il2CppSystem.Collections.Generic.List<BallObj> SharedActive;
            public Il2CppSystem.Collections.Generic.List<BallObj> ScopedActive;
            public Il2CppSystem.Collections.Generic.List<BallObj> SharedSide;
            public Il2CppSystem.Collections.Generic.List<BallObj> ScopedSide;
            public Il2CppSystem.Collections.Generic.List<BallObj> SharedAdultSide;
            public Il2CppSystem.Collections.Generic.List<BallObj> ScopedAdultSide;
            public int Owner;
            public bool ReconcileParentFirst;
            public bool Closed;
        }

        private static BallMgr _manager;
        private static readonly Snapshot _p1 = new Snapshot();
        private static readonly Snapshot _p2 = new Snapshot();
        private static int _activeSlot;
        private static bool _initialized;

        // Reusable per-owner views + O(N) scratch sets (zero per-frame heap allocations).
        private static Il2CppSystem.Collections.Generic.List<BallObj>[] _activeViews;
        private static Il2CppSystem.Collections.Generic.List<BallObj>[] _sideViews;
        private static Il2CppSystem.Collections.Generic.List<BallObj>[] _adultSideViews;
        private static readonly HashSet<IntPtr> _scopedPtrScratch = new HashSet<IntPtr>(128);
        private static readonly HashSet<IntPtr> _sharedPtrScratch = new HashSet<IntPtr>(128);
        private static WorldBallScope _currentScope;

        public static bool IsInitialized => _initialized && _manager != null;
        public static int ActiveSlot => _activeSlot;
        public static WorldBallScope CurrentWorldScope => _currentScope;

        public static void Initialize(BallMgr manager)
        {
            Reset();
            if (manager == null)
            {
                return;
            }
            _manager = manager;
            CaptureInto(manager, _p1);
            InitFreshPlayerTwoSnapshot(_p2);
            EnsureViewsAllocated();
            _initialized = true;
            _activeSlot = 0;
            Plugin.Logger?.LogInfo("BallMgrVirtualizer initialized (18 per-player fields + O(N) scoped views).");
        }

        public static bool RebasePlayerTwoFromPlayerOne()
        {
            if (!IsInitialized || _manager == null || _activeSlot != 0 || !HarmonyHelper.SameNative(BallMgr.I, _manager))
            {
                return false;
            }
            CaptureInto(_manager, _p1);
            InitFreshPlayerTwoSnapshot(_p2);
            return true;
        }

        public static void SetActiveSlot(int slot)
        {
            if (!IsInitialized || _manager == null || !HarmonyHelper.SameNative(BallMgr.I, _manager))
            {
                return;
            }
            if (slot == _activeSlot)
            {
                return;
            }
            if (slot == 1)
            {
                CaptureInto(_manager, _p1);
                Apply(_manager, _p2);
                _activeSlot = 1;
            }
            else
            {
                CaptureInto(_manager, _p2);
                Apply(_manager, _p1);
                _activeSlot = 0;
            }
        }

        public static void Reset()
        {
            if (_initialized && _manager != null && _activeSlot == 1)
            {
                try { Apply(_manager, _p1); } catch { }
            }
            _manager = null;
            _activeSlot = 0;
            _initialized = false;
            _currentScope = null;
            ClearSnapshot(_p1);
            ClearSnapshot(_p2);
            _activeViews = null;
            _sideViews = null;
            _adultSideViews = null;
            _scopedPtrScratch.Clear();
            _sharedPtrScratch.Clear();
        }

        private static void EnsureViewsAllocated()
        {
            if (_activeViews == null)
            {
                _activeViews = new[]
                {
                    new Il2CppSystem.Collections.Generic.List<BallObj>(),
                    new Il2CppSystem.Collections.Generic.List<BallObj>()
                };
                _sideViews = new[]
                {
                    new Il2CppSystem.Collections.Generic.List<BallObj>(),
                    new Il2CppSystem.Collections.Generic.List<BallObj>()
                };
                _adultSideViews = new[]
                {
                    new Il2CppSystem.Collections.Generic.List<BallObj>(),
                    new Il2CppSystem.Collections.Generic.List<BallObj>()
                };
            }
        }

        /// <summary>
        /// Enters an isolated ActiveBalls/SideBalls/AdultSideBalls view for `owner` (0 or 1).
        /// Properly handles nested opposite-owner scopes by reconciling the parent view back into
        /// the true shared lists before constructing the inner view!
        /// </summary>
        public static WorldBallScope EnterWorldBallScope(BallMgr manager, int owner)
        {
            if (manager == null || !CoopMode.Enabled || CoopContext.PlayerTwo == null)
            {
                return null;
            }
            EnsureViewsAllocated();

            WorldBallScope parent = _currentScope;
            if (parent != null && parent.Owner == owner)
            {
                // Already scoped for the exact same owner.
                return null;
            }

            Il2CppSystem.Collections.Generic.List<BallObj> trueSharedActive;
            Il2CppSystem.Collections.Generic.List<BallObj> trueSharedSide;
            Il2CppSystem.Collections.Generic.List<BallObj> trueSharedAdult;

            if (parent != null)
            {
                // Nested opposite-owner scope: flush parent's live changes back to true shared lists first!
                ReconcileScopedListON(parent.SharedActive, parent.ScopedActive, parent.Owner);
                ReconcileScopedListON(parent.SharedSide, parent.ScopedSide, parent.Owner);
                ReconcileScopedListON(parent.SharedAdultSide, parent.ScopedAdultSide, parent.Owner);

                trueSharedActive = parent.SharedActive;
                trueSharedSide = parent.SharedSide;
                trueSharedAdult = parent.SharedAdultSide;
            }
            else
            {
                trueSharedActive = manager.ActiveBalls;
                trueSharedSide = manager.SideBalls;
                trueSharedAdult = manager.AdultSideBalls;
            }

            if (trueSharedActive == null)
            {
                return null;
            }

            var scopedActive = _activeViews[owner];
            var scopedSide = _sideViews[owner];
            var scopedAdult = _adultSideViews[owner];

            PopulateForOwner(trueSharedActive, scopedActive, owner);
            PopulateForOwner(trueSharedSide, scopedSide, owner);
            PopulateForOwner(trueSharedAdult, scopedAdult, owner);

            manager.ActiveBalls = scopedActive;
            if (trueSharedSide != null) manager.SideBalls = scopedSide;
            if (trueSharedAdult != null) manager.AdultSideBalls = scopedAdult;

            var scope = new WorldBallScope
            {
                Parent = parent,
                SharedActive = trueSharedActive,
                ScopedActive = scopedActive,
                SharedSide = trueSharedSide,
                ScopedSide = scopedSide,
                SharedAdultSide = trueSharedAdult,
                ScopedAdultSide = scopedAdult,
                Owner = owner,
                ReconcileParentFirst = parent != null
            };
            _currentScope = scope;
            return scope;
        }

        public static void ExitWorldBallScope(BallMgr manager, WorldBallScope scope, bool reconcileChanges)
        {
            if (manager == null || scope == null || scope.Closed)
            {
                return;
            }

            try
            {
                if (reconcileChanges)
                {
                    ReconcileScopedListON(scope.SharedActive, scope.ScopedActive, scope.Owner);
                    ReconcileScopedListON(scope.SharedSide, scope.ScopedSide, scope.Owner);
                    ReconcileScopedListON(scope.SharedAdultSide, scope.ScopedAdultSide, scope.Owner);
                }
            }
            finally
            {
                scope.Closed = true;
                if (ReferenceEquals(_currentScope, scope))
                {
                    _currentScope = scope.Parent;
                }

                if (scope.Parent != null && !scope.Parent.Closed)
                {
                    // Refresh parent's view from updated shared lists and restore parent's lists.
                    PopulateForOwner(scope.Parent.SharedActive, scope.Parent.ScopedActive, scope.Parent.Owner);
                    PopulateForOwner(scope.Parent.SharedSide, scope.Parent.ScopedSide, scope.Parent.Owner);
                    PopulateForOwner(scope.Parent.SharedAdultSide, scope.Parent.ScopedAdultSide, scope.Parent.Owner);
                    manager.ActiveBalls = scope.Parent.ScopedActive;
                    if (scope.Parent.SharedSide != null) manager.SideBalls = scope.Parent.ScopedSide;
                    if (scope.Parent.SharedAdultSide != null) manager.AdultSideBalls = scope.Parent.ScopedAdultSide;
                }
                else
                {
                    manager.ActiveBalls = scope.SharedActive;
                    if (scope.SharedSide != null) manager.SideBalls = scope.SharedSide;
                    if (scope.SharedAdultSide != null) manager.AdultSideBalls = scope.SharedAdultSide;
                }
            }
        }

        public static void RemoveFromAllEarlyReturnedLists(BallObj ball)
        {
            IntPtr ptr = HarmonyHelper.Ptr(ball);
            if (ptr == IntPtr.Zero) return;

            RemoveBallByPointer(_manager?.EarlyReturnedBalls, ptr);
            RemoveBallByPointer(_p1.EarlyReturnedBalls, ptr);
            RemoveBallByPointer(_p2.EarlyReturnedBalls, ptr);
        }

        public static void RemoveBallFromAllScopesAndEarlyLists(BallObj ball)
        {
            IntPtr ptr = HarmonyHelper.Ptr(ball);
            if (ptr == IntPtr.Zero) return;

            RemoveBallByPointer(_manager?.EarlyReturnedBalls, ptr);
            RemoveBallByPointer(_p1.EarlyReturnedBalls, ptr);
            RemoveBallByPointer(_p2.EarlyReturnedBalls, ptr);

            WorldBallScope s = _currentScope;
            while (s != null)
            {
                RemoveBallByPointer(s.SharedActive, ptr);
                RemoveBallByPointer(s.ScopedActive, ptr);
                RemoveBallByPointer(s.SharedSide, ptr);
                RemoveBallByPointer(s.ScopedSide, ptr);
                RemoveBallByPointer(s.SharedAdultSide, ptr);
                RemoveBallByPointer(s.ScopedAdultSide, ptr);
                s = s.Parent;
            }
        }

        public static bool ContainsBall(Il2CppSystem.Collections.Generic.List<BallObj> list, BallObj target)
        {
            if (list == null || target == null) return false;
            IntPtr targetPtr = HarmonyHelper.Ptr(target);
            if (targetPtr == IntPtr.Zero) return false;
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                if (HarmonyHelper.Ptr(list[i]) == targetPtr) return true;
            }
            return false;
        }

        public static bool RemoveBallByPointer(Il2CppSystem.Collections.Generic.List<BallObj> list, IntPtr targetPtr)
        {
            if (list == null || targetPtr == IntPtr.Zero) return false;
            bool removed = false;
            try
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    BallObj item = list[i];
                    IntPtr p = HarmonyHelper.Ptr(item);
                    if (p == targetPtr)
                    {
                        list.RemoveAt(i);
                        removed = true;
                    }
                }
            }
            catch { }
            return removed;
        }

        private static void PopulateForOwner(
            Il2CppSystem.Collections.Generic.List<BallObj> source,
            Il2CppSystem.Collections.Generic.List<BallObj> view,
            int owner)
        {
            view.Clear();
            if (source == null) return;
            int count = source.Count;
            for (int i = 0; i < count; i++)
            {
                BallObj ball = source[i];
                if (ball != null && ball.Pointer != IntPtr.Zero && PlayerTwoPatches.MatchesBallOwner(ball, owner))
                {
                    view.Add(ball);
                }
            }
        }

        /// <summary>
        /// O(N) reconciliation using reusable HashSet<IntPtr> buffers instead of O(N^2) linear scans.
        /// </summary>
        private static void ReconcileScopedListON(
            Il2CppSystem.Collections.Generic.List<BallObj> shared,
            Il2CppSystem.Collections.Generic.List<BallObj> scoped,
            int owner)
        {
            if (shared == null || scoped == null)
            {
                return;
            }

            _scopedPtrScratch.Clear();
            int scopedCount = scoped.Count;
            for (int i = 0; i < scopedCount; i++)
            {
                IntPtr ptr = HarmonyHelper.Ptr(scoped[i]);
                if (ptr != IntPtr.Zero)
                {
                    _scopedPtrScratch.Add(ptr);
                }
            }

            _sharedPtrScratch.Clear();
            for (int i = shared.Count - 1; i >= 0; i--)
            {
                BallObj ball = shared[i];
                IntPtr ptr = HarmonyHelper.Ptr(ball);
                if (ptr == IntPtr.Zero) continue;

                if (PlayerTwoPatches.MatchesBallOwner(ball, owner) && !_scopedPtrScratch.Contains(ptr))
                {
                    shared.RemoveAt(i);
                }
                else
                {
                    _sharedPtrScratch.Add(ptr);
                }
            }

            for (int i = 0; i < scopedCount; i++)
            {
                BallObj ball = scoped[i];
                IntPtr ptr = HarmonyHelper.Ptr(ball);
                if (ptr != IntPtr.Zero && _sharedPtrScratch.Add(ptr))
                {
                    shared.Add(ball);
                }
            }
        }

        private static void InitFreshPlayerTwoSnapshot(Snapshot snap)
        {
            // Always allocate brand-new independent native Lists for P2 (never rely on managed ReferenceEquals).
            snap.EarlyReturnedBalls = new Il2CppSystem.Collections.Generic.List<BallObj>();
            snap.RechargingFollowers = new Il2CppSystem.Collections.Generic.List<float>();
            snap.JustLaunchedBalls = new Il2CppSystem.Collections.Generic.List<BallObj>();
            snap.JustLaunchedAltBalls = new Il2CppSystem.Collections.Generic.List<BallObj>();
            snap.CurState = BallState.kIdle;
            snap.CurBallIdx = 0;
            snap.NumActiveFollowers = 0;
            snap.EarlyReturnedFollowers = 0;
            snap.BallAnim = default;
            snap.IsShootInterrupted = false;
            snap.AllowAutoChange = true;
            snap.DidAnyBallStartToReturn = false;
            snap.DidAnyBallReturn = false;
            // Initialize to -999f so DidShootRecently() doesn't false-trigger at t ≈ 0.
            snap.LastShootCompleteUnityTime = -999f;
            snap.LastShootCompleteTime = -999f;
            snap.LobTgtPos = Vector3.zero;
            snap.AltAimDir = Vector2.up;
            snap.AllInOneBall = null;
        }

        private static void ClearSnapshot(Snapshot snap)
        {
            snap.EarlyReturnedBalls = null;
            snap.RechargingFollowers = null;
            snap.JustLaunchedBalls = null;
            snap.JustLaunchedAltBalls = null;
            snap.AllInOneBall = null;
            snap.BallAnim = default;
            snap.CurState = BallState.kIdle;
            snap.CurBallIdx = 0;
            snap.NumActiveFollowers = 0;
            snap.EarlyReturnedFollowers = 0;
            snap.DidAnyBallStartToReturn = false;
            snap.DidAnyBallReturn = false;
            snap.LastShootCompleteUnityTime = -999f;
            snap.LastShootCompleteTime = -999f;
            snap.AllowAutoChange = true;
            snap.IsShootInterrupted = false;
            snap.LobTgtPos = Vector3.zero;
            snap.AltAimDir = Vector2.up;
        }

        private static void CaptureInto(BallMgr manager, Snapshot snap)
        {
            snap.EarlyReturnedBalls = manager.EarlyReturnedBalls;
            snap.CurBallIdx = manager._curBallIdx;
            snap.NumActiveFollowers = manager._numActiveFollowers;
            snap.EarlyReturnedFollowers = manager._earlyReturnedFollowers;
            snap.RechargingFollowers = manager.RechargingFollowers;
            snap.DidAnyBallStartToReturn = manager._didAnyBallStartToReturn;
            snap.DidAnyBallReturn = manager._didAnyBallReturn;
            snap.LastShootCompleteUnityTime = manager._lastShootCompleteUnityTime;
            snap.CurState = manager._curState;
            snap.AllowAutoChange = manager._allowAutoChange;
            snap.BallAnim = manager._ballAnim;
            snap.LastShootCompleteTime = manager.LastShootCompleteTime;
            snap.IsShootInterrupted = manager._isShootInterrupted;
            snap.JustLaunchedBalls = manager._justLaunchedBalls;
            snap.JustLaunchedAltBalls = manager._justLaunchedAltBalls;
            snap.LobTgtPos = manager._lobTgtPos;
            snap.AltAimDir = manager._altAimDir;
            snap.AllInOneBall = manager.AllInOneBall;
        }

        private static void Apply(BallMgr manager, Snapshot snap)
        {
            manager.EarlyReturnedBalls = snap.EarlyReturnedBalls;
            manager._curBallIdx = snap.CurBallIdx;
            manager._numActiveFollowers = snap.NumActiveFollowers;
            manager._earlyReturnedFollowers = snap.EarlyReturnedFollowers;
            manager.RechargingFollowers = snap.RechargingFollowers;
            manager._didAnyBallStartToReturn = snap.DidAnyBallStartToReturn;
            manager._didAnyBallReturn = snap.DidAnyBallReturn;
            manager._lastShootCompleteUnityTime = snap.LastShootCompleteUnityTime;
            manager._curState = snap.CurState;
            manager._allowAutoChange = snap.AllowAutoChange;
            manager._ballAnim = snap.BallAnim;
            manager.LastShootCompleteTime = snap.LastShootCompleteTime;
            manager._isShootInterrupted = snap.IsShootInterrupted;
            manager._justLaunchedBalls = snap.JustLaunchedBalls;
            manager._justLaunchedAltBalls = snap.JustLaunchedAltBalls;
            manager._lobTgtPos = snap.LobTgtPos;
            manager._altAimDir = snap.AltAimDir;
            manager.AllInOneBall = snap.AllInOneBall;
        }
    }
}
