using System;
using System.Collections.Generic;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Gameplay;
using UnityEngine;

namespace BALLxPITLocalCoop.PlayerTwo
{
    /// <summary>
    /// Deterministic, stack-based P1/P2 execution context manager.
    /// Separates lightweight PositionOnly scopes (Player.I only — zero BallMgr/UpgradeMgr churn)
    /// from Full scopes (Player.I + BallMgr + HeroInventory + Previews + Falconer + Character/UpgradeMgr + Vitals).
    /// Eliminates nested depth corruption, unbalanced pops, and P1/P2 state desyncs.
    /// </summary>
    internal static class CoopContext
    {
        internal enum ScopeKind : byte
        {
            PositionOnly = 0,
            Full = 1
        }

        private struct ContextFrame
        {
            public int PrevSlot;
            public bool PrevFull;
            public int TargetSlot;
            public bool TargetFull;
        }

        internal readonly struct ScopeToken
        {
            internal readonly int StackDepthAfterPush;
            internal readonly bool Active;

            internal ScopeToken(int depthAfterPush)
            {
                StackDepthAfterPush = depthAfterPush;
                Active = true;
            }
        }

        private static readonly Stack<ContextFrame> Frames = new Stack<ContextFrame>(16);

        private static Player _originalPlayer;
        private static Player _playerTwo;
        private static bool _isCreatingPlayerTwo;
        private static int _currentSlot;
        private static bool _currentFull;

        public static Player OriginalPlayer => _originalPlayer;
        public static Player PlayerTwo => _playerTwo;
        public static bool IsCreatingPlayerTwo => _isCreatingPlayerTwo;
        public static int CurrentSlot => _currentSlot;
        public static bool IsPlayerTwoContext => _playerTwo != null && (_currentSlot == 1 || HarmonyHelper.SameNative(Player.I, _playerTwo));
        public static bool IsFullPlayerTwoContext => _currentSlot == 1 && _currentFull;

        public static void SetOriginalPlayer(Player player) => _originalPlayer = player;

        public static bool IsPlayerTwo(Player player) =>
            player != null && _playerTwo != null && HarmonyHelper.SameNative(player, _playerTwo);

        public static void BeginPlayerTwoCreation(Player originalPlayer)
        {
            Frames.Clear();
            _currentSlot = 0;
            _currentFull = false;
            _originalPlayer = originalPlayer;
            PreviewIsolation.BeginCreation();
            FalconerIsolation.BeforeClone(originalPlayer);
            _isCreatingPlayerTwo = true;
        }

        public static void CompletePlayerTwoCreation(Player playerTwo)
        {
            _playerTwo = playerTwo;
            FalconerIsolation.AfterClone(_originalPlayer, playerTwo);
            PreviewIsolation.CompleteCreation(playerTwo);
            _isCreatingPlayerTwo = false;
            ForceRestorePlayerOne();
        }

        public static void CancelPlayerTwoCreation()
        {
            Frames.Clear();
            _currentSlot = 0;
            _currentFull = false;
            _isCreatingPlayerTwo = false;

            // Restore Player.I and singletons BEFORE nulling _originalPlayer!
            Player orig = _originalPlayer;
            PreviewIsolation.RestorePlayerOneSingletons();
            PreviewIsolation.DestroyOwnedClones();
            PlayerTwoPatches.ResetRound();
            BallMgrVirtualizer.Reset();
            HeroInventoryIsolation.Reset();
            FalconerIsolation.Reset();

            _playerTwo = null;
            if (orig != null && !HarmonyHelper.SameNative(Player.I, orig))
            {
                Player.I = orig;
            }
            _originalPlayer = null;
        }

        public static void ClearPlayerTwo(Player playerTwo)
        {
            Frames.Clear();
            _currentSlot = 0;
            _currentFull = false;
            Player orig = _originalPlayer;

            PlayerTwoPatches.ResetRound();
            BallMgrVirtualizer.Reset();
            HeroInventoryIsolation.Reset();
            FalconerIsolation.Reset();
            PreviewIsolation.RestorePlayerOneSingletons();
            PreviewIsolation.DestroyOwnedClones();

            if (HarmonyHelper.SameNative(_playerTwo, playerTwo) || _playerTwo == null)
            {
                _playerTwo = null;
            }
            if (orig != null && !HarmonyHelper.SameNative(Player.I, orig))
            {
                Player.I = orig;
            }
            CoopCharacterRuntime.SyncCharacter();
            CoopVitals.SyncBackingHealth();

            _originalPlayer = null;
            PreviewIsolation.ResetRefs();
        }

        public static ScopeToken EnterPlayerTwo(Player playerTwo, ScopeKind kind = ScopeKind.Full)
        {
            Player p2 = playerTwo ?? _playerTwo;
            if (p2 == null)
            {
                return default;
            }
            return PushFrame(targetSlot: 1, targetFull: kind == ScopeKind.Full, targetPlayer: p2);
        }

        public static ScopeToken EnterPlayerOne(ScopeKind kind = ScopeKind.Full)
        {
            if (_originalPlayer == null)
            {
                return default;
            }
            // No-op fast path if already in P1 context (with no active P2 Full state).
            if (_currentSlot == 0 && !_currentFull && HarmonyHelper.SameNative(Player.I, _originalPlayer))
            {
                return default;
            }
            return PushFrame(targetSlot: 0, targetFull: false, targetPlayer: _originalPlayer);
        }

        public static ScopeToken EnterSlot(int slot, ScopeKind kind = ScopeKind.Full)
        {
            return slot == 1 ? EnterPlayerTwo(_playerTwo, kind) : EnterPlayerOne(kind);
        }

        public static ScopeToken EnterNearestPlayer(Vector3 worldPos, ScopeKind kind = ScopeKind.PositionOnly)
        {
            if (_playerTwo == null || _originalPlayer == null)
            {
                return default;
            }
            Player chosen = CoopMode.Enabled
                ? CoopCombat.NearestLiving(worldPos)
                : PickCloser(worldPos);
            if (chosen == null)
            {
                return default;
            }
            return IsPlayerTwo(chosen)
                ? EnterPlayerTwo(_playerTwo, kind)
                : EnterPlayerOne(kind);
        }

        public static void Exit(ScopeToken token)
        {
            if (!token.Active)
            {
                return;
            }
            // Unwind cleanly even if an inner frame was somehow abandoned.
            if (Frames.Count == 0)
            {
                ForceRestorePlayerOne();
                return;
            }

            ContextFrame popped = default;
            while (Frames.Count >= token.StackDepthAfterPush)
            {
                popped = Frames.Pop();
            }

            ApplyState(popped.PrevSlot, popped.PrevFull);
        }

        public static void ForceRestorePlayerOne()
        {
            Frames.Clear();
            ApplyState(slot: 0, full: false);
            PreviewIsolation.CaptureOriginalSingletonsIfNeeded();
            if (_isCreatingPlayerTwo || _playerTwo != null)
            {
                PreviewIsolation.CaptureAndDisableClonedSingletons();
                PreviewIsolation.RestorePlayerOneSingletons();
            }
            CoopVitals.SyncBackingHealth();
        }

        private static ScopeToken PushFrame(int targetSlot, bool targetFull, Player targetPlayer)
        {
            int prevSlot = _currentSlot;
            bool prevFull = _currentFull;

            // Preserve Full mode when entering a nested PositionOnly scope on the SAME slot.
            bool effectiveFull = targetSlot == 1 && (targetFull || (prevSlot == 1 && prevFull));

            Frames.Push(new ContextFrame
            {
                PrevSlot = prevSlot,
                PrevFull = prevFull,
                TargetSlot = targetSlot,
                TargetFull = effectiveFull
            });

            ApplyState(targetSlot, effectiveFull, targetPlayer);
            return new ScopeToken(Frames.Count);
        }

        private static void ApplyState(int slot, bool full, Player explicitTarget = null)
        {
            int prevSlot = _currentSlot;
            bool prevFull = _currentFull;
            _currentSlot = slot;
            _currentFull = slot == 1 && full;

            Player desiredPlayer = slot == 1 ? (explicitTarget ?? _playerTwo) : _originalPlayer;
            if (desiredPlayer != null && !HarmonyHelper.SameNative(Player.I, desiredPlayer))
            {
                Player.I = desiredPlayer;
            }

            // Transition full subsystems only when entering or leaving Full P2 mode.
            if (_currentFull && !prevFull)
            {
                BallMgrVirtualizer.SetActiveSlot(1);
                HeroInventoryIsolation.SetActiveSlot(1);
                PreviewIsolation.UsePlayerTwoSingletons();
                FalconerIsolation.ApplyForSlot(1);
                CoopCharacterRuntime.SyncCharacter();
                CoopVitals.SyncBackingHealth();
            }
            else if (!_currentFull && prevFull)
            {
                PreviewIsolation.RestorePlayerOneSingletons();
                BallMgrVirtualizer.SetActiveSlot(0);
                HeroInventoryIsolation.SetActiveSlot(0);
                FalconerIsolation.ApplyForSlot(0);
                CoopCharacterRuntime.SyncCharacter();
                CoopVitals.SyncBackingHealth();
            }
            else if (prevSlot != slot && !_currentFull)
            {
                // Lightweight PositionOnly transition: do NOT swap BallMgr, UpgradeMgr, Previews, or Falconer!
                // Only Player.I was updated above.
            }
        }

        private static Player PickCloser(Vector3 worldPos)
        {
            Vector3 p1Pos = GetClosestBodyPosition(_originalPlayer, worldPos);
            Vector3 p2Pos = GetClosestBodyPosition(_playerTwo, worldPos);
            float d1 = (p1Pos.x - worldPos.x) * (p1Pos.x - worldPos.x) + (p1Pos.y - worldPos.y) * (p1Pos.y - worldPos.y);
            float d2 = (p2Pos.x - worldPos.x) * (p2Pos.x - worldPos.x) + (p2Pos.y - worldPos.y) * (p2Pos.y - worldPos.y);
            return d2 < d1 ? _playerTwo : _originalPlayer;
        }

        public static Vector3 GetPlayerPosition(Player player)
        {
            if (player == null)
            {
                return Vector3.zero;
            }
            try
            {
                var controllers = player.CharControllers;
                if (controllers != null && controllers.Length > 0 && controllers[0] != null)
                {
                    return controllers[0].transform.position;
                }
                return player.transform.position;
            }
            catch
            {
                return Vector3.zero;
            }
        }

        /// <summary>
        /// Supports dual-body characters (e.g. Cohabitants / Mirror) by returning whichever active body is closest to referencePos.
        /// </summary>
        public static Vector3 GetClosestBodyPosition(Player player, Vector3 referencePos)
        {
            if (player == null)
            {
                return Vector3.zero;
            }
            try
            {
                var controllers = player.CharControllers;
                if (controllers != null && controllers.Length > 0)
                {
                    Vector3 best = Vector3.zero;
                    float bestSq = float.MaxValue;
                    bool found = false;
                    for (int i = 0; i < controllers.Length; i++)
                    {
                        PlayerCharController ctrl = controllers[i];
                        if (ctrl == null || !ctrl.gameObject.activeInHierarchy) continue;
                        Vector3 pos = ctrl.transform.position;
                        float dx = pos.x - referencePos.x;
                        float dy = pos.y - referencePos.y;
                        float sq = dx * dx + dy * dy;
                        if (sq < bestSq)
                        {
                            bestSq = sq;
                            best = pos;
                            found = true;
                        }
                    }
                    if (found) return best;
                }
                return player.transform.position;
            }
            catch
            {
                return Vector3.zero;
            }
        }
    }
}
