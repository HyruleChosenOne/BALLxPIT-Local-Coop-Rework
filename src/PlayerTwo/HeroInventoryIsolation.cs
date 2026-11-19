using System;
using System.Collections.Generic;
using BALLxPITLocalCoop.Core;

namespace BALLxPITLocalCoop.PlayerTwo
{
    /// <summary>
    /// Maintains independent P1 and P2 HeroInst lists with O(1) IntPtr ownership lookup
    /// and direct interop copy construction (zero reflection).
    /// </summary>
    internal static class HeroInventoryIsolation
    {
        private static readonly HashSet<IntPtr> _p2HeroPointers = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> _p1HeroPointers = new HashSet<IntPtr>();
        private static Il2CppSystem.Collections.Generic.List<HeroInst> _playerOneHeroes;
        private static Il2CppSystem.Collections.Generic.List<HeroInst> _playerTwoHeroes;
        private static int _activeSlot;
        private static int _lastP2Count = -1;
        private static int _lastP1Count = -1;

        public static Il2CppSystem.Collections.Generic.List<HeroInst> PlayerOneHeroes => _playerOneHeroes;
        public static Il2CppSystem.Collections.Generic.List<HeroInst> PlayerTwoHeroes => _playerTwoHeroes;
        public static bool IsPlayerTwoActive => _activeSlot == 1;

        public static void Initialize()
        {
            _activeSlot = 0;
            _playerOneHeroes = null;
            _playerTwoHeroes = new Il2CppSystem.Collections.Generic.List<HeroInst>();
            _p2HeroPointers.Clear();
            _p1HeroPointers.Clear();
            _lastP2Count = -1;
            _lastP1Count = -1;
        }

        public static void BindIndependentLists(
            Il2CppSystem.Collections.Generic.List<HeroInst> playerOneHeroes,
            Il2CppSystem.Collections.Generic.List<HeroInst> playerTwoHeroes)
        {
            if (playerOneHeroes != null)
            {
                _playerOneHeroes = playerOneHeroes;
            }
            _playerTwoHeroes = playerTwoHeroes ?? _playerTwoHeroes ?? new Il2CppSystem.Collections.Generic.List<HeroInst>();
            RebuildPointerCaches();
        }

        public static void SetActiveSlot(int slot)
        {
            BattleSaveData data = BattleSaveData.I;
            if (data == null)
            {
                _activeSlot = slot;
                return;
            }

            if (slot == 1)
            {
                IntPtr dataPtr = HarmonyHelper.Ptr(data.Heroes);
                IntPtr p2Ptr = HarmonyHelper.Ptr(_playerTwoHeroes);
                if (dataPtr != IntPtr.Zero && dataPtr != p2Ptr)
                {
                    _playerOneHeroes = data.Heroes;
                }
                _playerTwoHeroes ??= new Il2CppSystem.Collections.Generic.List<HeroInst>();
                p2Ptr = HarmonyHelper.Ptr(_playerTwoHeroes);
                if (HarmonyHelper.Ptr(data.Heroes) != p2Ptr)
                {
                    data.Heroes = _playerTwoHeroes;
                }
                _activeSlot = 1;
            }
            else
            {
                IntPtr p1Ptr = HarmonyHelper.Ptr(_playerOneHeroes);
                if (_playerOneHeroes != null && HarmonyHelper.Ptr(data.Heroes) != p1Ptr)
                {
                    data.Heroes = _playerOneHeroes;
                }
                _activeSlot = 0;
            }
        }

        public static void RebuildPointerCaches()
        {
            _p1HeroPointers.Clear();
            _p2HeroPointers.Clear();

            if (_playerOneHeroes != null)
            {
                int c = _playerOneHeroes.Count;
                for (int i = 0; i < c; i++)
                {
                    IntPtr ptr = HarmonyHelper.Ptr(_playerOneHeroes[i]);
                    if (ptr != IntPtr.Zero) _p1HeroPointers.Add(ptr);
                }
                _lastP1Count = c;
            }
            else
            {
                _lastP1Count = -1;
            }

            if (_playerTwoHeroes != null)
            {
                int c = _playerTwoHeroes.Count;
                for (int i = 0; i < c; i++)
                {
                    IntPtr ptr = HarmonyHelper.Ptr(_playerTwoHeroes[i]);
                    if (ptr != IntPtr.Zero) _p2HeroPointers.Add(ptr);
                }
                _lastP2Count = c;
            }
            else
            {
                _lastP2Count = -1;
            }
        }

        public static bool IsPlayerTwoHero(HeroInst hero)
        {
            IntPtr ptr = HarmonyHelper.Ptr(hero);
            if (ptr == IntPtr.Zero)
            {
                return false;
            }
            int p2Count = _playerTwoHeroes?.Count ?? 0;
            if (p2Count != _lastP2Count)
            {
                RebuildPointerCaches();
            }
            if (_p2HeroPointers.Contains(ptr))
            {
                return true;
            }
            if (_p1HeroPointers.Contains(ptr))
            {
                return false;
            }
            // Fallback linear scan in case a HeroInst was replaced in-place without changing Count (e.g., evolution).
            if (_playerTwoHeroes != null)
            {
                for (int i = 0; i < _playerTwoHeroes.Count; i++)
                {
                    if (HarmonyHelper.Ptr(_playerTwoHeroes[i]) == ptr)
                    {
                        RebuildPointerCaches();
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool IsPlayerOneHero(HeroInst hero)
        {
            IntPtr ptr = HarmonyHelper.Ptr(hero);
            if (ptr == IntPtr.Zero)
            {
                return false;
            }
            int p1Count = _playerOneHeroes?.Count ?? 0;
            if (p1Count != _lastP1Count)
            {
                RebuildPointerCaches();
            }
            if (_p1HeroPointers.Contains(ptr))
            {
                return true;
            }
            if (_p2HeroPointers.Contains(ptr))
            {
                return false;
            }
            if (_playerOneHeroes != null)
            {
                for (int i = 0; i < _playerOneHeroes.Count; i++)
                {
                    if (HarmonyHelper.Ptr(_playerOneHeroes[i]) == ptr)
                    {
                        RebuildPointerCaches();
                        return true;
                    }
                }
            }
            return false;
        }

        public static HeroInst CloneHero(HeroInst original)
        {
            if (original == null)
            {
                return null;
            }
            HeroInst clone = new HeroInst(original, true);
            clone.Obj = null;
            return clone;
        }

        public static void EnsureCurrentBallIndexValid(BallMgr manager)
        {
            if (manager == null || _playerTwoHeroes == null || _playerTwoHeroes.Count == 0)
            {
                return;
            }
            int cur = manager.GetCurBallIdx();
            if (cur >= 0 && cur < _playerTwoHeroes.Count)
            {
                return;
            }
            int fallback = 0;
            for (int i = 0; i < _playerTwoHeroes.Count; i++)
            {
                if (_playerTwoHeroes[i] != null && _playerTwoHeroes[i].IsReadyToShoot())
                {
                    fallback = i;
                    break;
                }
            }
            manager.SetCurBallIdx(fallback);
        }

        public static void RepairRuntimeAgainstWorld(BallMgr manager)
        {
            if (manager == null || _playerTwoHeroes == null || manager.GetBallState() == BallState.kLaunching)
            {
                return;
            }
            BallObj allInOne = manager.AllInOneBall;
            IntPtr allInOnePtr = HarmonyHelper.Ptr(allInOne);
            for (int i = 0; i < _playerTwoHeroes.Count; i++)
            {
                HeroInst hero = _playerTwoHeroes[i];
                if (hero == null || hero.Obj == null)
                {
                    continue;
                }
                BallObj obj = hero.Obj;
                IntPtr objPtr = HarmonyHelper.Ptr(obj);
                if (objPtr != IntPtr.Zero && objPtr == allInOnePtr)
                {
                    continue;
                }
                if (obj.IsActive && (BallMgrVirtualizer.ContainsBall(manager.ActiveBalls, obj)
                    || BallMgrVirtualizer.ContainsBall(manager.SideBalls, obj)
                    || BallMgrVirtualizer.ContainsBall(manager.AdultSideBalls, obj)
                    || BallMgrVirtualizer.ContainsBall(manager.EarlyReturnedBalls, obj)))
                {
                    continue;
                }
                hero.Obj = null;
                hero.LastReturnTime = BattleSaveData.I != null ? BattleSaveData.I.ElapsedTime : 0f;
            }
        }

        public static void Reset()
        {
            if (_activeSlot == 1)
            {
                try
                {
                    if (BattleSaveData.I != null && _playerOneHeroes != null)
                    {
                        BattleSaveData.I.Heroes = _playerOneHeroes;
                    }
                }
                catch { }
            }
            _activeSlot = 0;
            _playerOneHeroes = null;
            _playerTwoHeroes = null;
            _p1HeroPointers.Clear();
            _p2HeroPointers.Clear();
            _lastP1Count = -1;
            _lastP2Count = -1;
        }
    }
}
