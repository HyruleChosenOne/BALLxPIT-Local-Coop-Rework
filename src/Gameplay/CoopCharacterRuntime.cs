using System;
using System.Collections.Generic;
using System.Reflection;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.PlayerTwo;
using BALLxPITLocalCoop.UI;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BALLxPITLocalCoop.Gameplay
{
    /// <summary>
    /// Manages independent P1 and P2 character instances, HeroInst / PassiveInst lists,
    /// per-slot UpgradeMgr stat snapshots, and P2 character controller spawning.
    /// UI menu logic has been cleanly extracted to <see cref="CoopMenuUI"/>.
    /// </summary>
    public static class CoopCharacterRuntime
    {
        private sealed class SlotStatSnapshot
        {
            public int MaxHealth;
            public float[] FloatValues;
            public int[] IntValues;
            public bool[] BoolValues;
            public int[] MiscInts;
            public float[] MiscFloats;
            public CharInfo CurChar;
            public Il2CppSystem.Collections.Generic.List<CharInfo> ComboChars;
            public PassiveInst BabyRattlePassive;
            public PassiveInst UpturnedHatchetPassive;
            public PassiveInst SpikedCollarPassive;
            public PassiveInst MagicStaffPassive;
            public PassiveInst BoulderPassive;
        }

        private struct PropAccessor<TValue>
        {
            public Func<UpgradeMgr, TValue> Get;
            public Action<UpgradeMgr, TValue> Set;
        }

        private static readonly PropAccessor<float>[] FloatStatAccessors = BuildTypedAccessors<float>(excludeTgtXP: true);
        private static readonly PropAccessor<int>[] IntStatAccessors = BuildTypedAccessors<int>(excludeTgtXP: true);
        private static readonly PropAccessor<bool>[] BoolStatAccessors = BuildTypedAccessors<bool>(excludeTgtXP: false);

        private static readonly SlotStatSnapshot[] SlotStats = new SlotStatSnapshot[2];
        private static readonly bool[] SlotStatsValid = new bool[2];

        private static bool _installed;
        private static bool _roundInitialized;
        private static bool _calculatingSlotStats;
        private static int _lastAppliedStatsSlot = -1;
        private static HeroInst _starterClone;
        private static CharBattleInst _firstChar;
        private static CharBattleInst _secondChar;
        private static CharInfo _firstCharInfo;
        private static CharInfo _secondCharInfo;
        private static Il2CppSystem.Collections.Generic.List<CharInfo> _firstComboChars;
        private static Il2CppSystem.Collections.Generic.List<CharInfo> _secondComboChars;
        private static Il2CppSystem.Collections.Generic.List<PassiveInst> _firstPassives;
        private static Il2CppSystem.Collections.Generic.List<PassiveInst> _secondPassives;
        private static Il2CppSystem.Collections.Generic.List<HeroInst> _firstHeroes;
        private static Il2CppSystem.Collections.Generic.List<HeroInst> _secondHeroes;

        public static CharType FirstType { get; set; } = CharType.kDefault;
        public static CharType SecondType { get; set; } = CharType.kRecaller;

        public static bool IndependentReady => _roundInitialized && _firstChar != null && _secondChar != null;
        public static CharBattleInst FirstChar => _firstChar;
        public static CharBattleInst SecondChar => _secondChar;
        public static Il2CppSystem.Collections.Generic.List<PassiveInst> FirstPassives => _firstPassives;
        public static Il2CppSystem.Collections.Generic.List<PassiveInst> SecondPassives => _secondPassives;
        public static Il2CppSystem.Collections.Generic.List<HeroInst> SecondHeroes => HeroInventoryIsolation.PlayerTwoHeroes ?? _secondHeroes;
        public static Player FirstPlayer => PlayerTwoRuntime.PlayerOne ?? Player.I;
        public static Player SecondPlayer => PlayerTwoRuntime.PlayerTwo;

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GameMgr), nameof(GameMgr.Start)),
                prefix: new HarmonyMethod(typeof(CoopCharacterRuntime), nameof(OnGameStart)),
                label: "GameMgr.Start (CoopCharacterRuntime)");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(UpgradeMgr), nameof(UpgradeMgr.CalculateStats)),
                prefix: new HarmonyMethod(typeof(CoopCharacterRuntime), nameof(OnCalculateStatsPrefix)),
                postfix: new HarmonyMethod(typeof(CoopCharacterRuntime), nameof(OnCalculateStatsPostfix)),
                label: "UpgradeMgr.CalculateStats (CoopCharacterRuntime)");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(BallMgr), nameof(BallMgr.CreateBall), new[] { typeof(HeroInst), typeof(bool), typeof(Vector2), typeof(BallSourceType) }),
                prefix: new HarmonyMethod(typeof(CoopCharacterRuntime), nameof(OnCreateBallPrefix)),
                postfix: new HarmonyMethod(typeof(CoopCharacterRuntime), nameof(OnCreateBallPostfix)),
                label: "BallMgr.CreateBall (CoopCharacterRuntime)");
        }

        public static void Shutdown()
        {
            ResetRound();
            _installed = false;
        }

        public static void ResetRound()
        {
            _roundInitialized = false;
            _calculatingSlotStats = false;
            _lastAppliedStatsSlot = -1;
            _starterClone = null;
            _firstChar = null;
            _secondChar = null;
            _firstCharInfo = null;
            _secondCharInfo = null;
            _firstComboChars = null;
            _secondComboChars = null;
            _firstPassives = null;
            _secondPassives = null;
            _firstHeroes = null;
            _secondHeroes = null;
            SlotStats[0] = SlotStats[1] = null;
            SlotStatsValid[0] = SlotStatsValid[1] = false;
        }

        public static CharInfo GetCharInfo(CharType type)
        {
            try
            {
                if (InfoDB.I?.Chars != null)
                {
                    foreach (CharInfo info in InfoDB.I.Chars)
                    {
                        if (info != null && info.Type == type) return info;
                    }
                }
                if (MetaSaveData.I?.Chars != null)
                {
                    foreach (CharMetaInst c in MetaSaveData.I.Chars)
                    {
                        CharInfo info = c?.GetInfo();
                        if (info != null && info.Type == type) return info;
                    }
                }
            }
            catch { }
            return null;
        }

        internal static bool IsPassiveInSlot(int slot, PassiveInst passive)
        {
            if (passive == null)
            {
                return false;
            }
            IntPtr ptr = HarmonyHelper.Ptr(passive);
            if (ptr == IntPtr.Zero)
            {
                return false;
            }
            var list = slot == 1 ? _secondPassives : _firstPassives;
            if (list == null)
            {
                return false;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (HarmonyHelper.Ptr(list[i]) == ptr)
                {
                    return true;
                }
            }
            return false;
        }

        private static void OnGameStart()
        {
            ResetRound();

            if (!CoopMode.Enabled || BattleSaveData.I == null)
            {
                return;
            }

            if (BattleSaveData.I.UpgradeLvl == 0 && BattleSaveData.I.CurTurn <= 1)
            {
                BattleSaveData.I.Seed = unchecked((int)((uint)BattleSaveData.I.Seed ^ ((uint)Environment.TickCount * 2654435761u) ^ (uint)UnityEngine.Random.Range(1, int.MaxValue)));
                if (BattleSaveData.I.Seed == 0)
                {
                    BattleSaveData.I.Seed = 1;
                }
            }

            _firstChar = CoopMode.Character(0);
            _secondChar = CoopMode.Character(1);
            BattleSaveData.I.CurChar = _firstChar;
            CharBattleInst.I = _firstChar;
        }

        internal static void OnPlayerTwoSpawned(Player playerTwo)
        {
            if (!CoopMode.Enabled || BattleSaveData.I == null || playerTwo == null)
            {
                return;
            }

            EnsureRoundInitialized();

            CharBattleInst secondChar = _secondChar ?? CoopMode.Character(1);
            if (secondChar == null)
            {
                return;
            }

            var oldControllers = playerTwo.CharControllers;
            var scope = CoopContext.EnterPlayerTwo(playerTwo, CoopContext.ScopeKind.Full);
            var createdList = new List<PlayerCharController>(2);
            try
            {
                CharInfo info = secondChar.GetInfo();
                bool isPair = secondChar.HasProp(CharProp.kMirror)
                    || (secondChar.CombinedTypes != null && secondChar.CombinedTypes.Count > 0)
                    || (info != null && info.PrefabCharSecond != null);
                int count = isPair ? 2 : 1;

                var newControllers = new Il2CppReferenceArray<PlayerCharController>(count);
                Vector2 aimDir = PlayerTwoRuntime.AimDir;
                for (int i = 0; i < count; i++)
                {
                    PlayerCharController ctrl = secondChar.CreateCharController(playerTwo, i);
                    if (ctrl == null)
                    {
                        // Clean up any partially created controllers before aborting.
                        for (int k = 0; k < createdList.Count; k++)
                        {
                            if (createdList[k] != null && createdList[k].gameObject != null)
                            {
                                UnityEngine.Object.Destroy(createdList[k].gameObject);
                            }
                        }
                        return;
                    }
                    createdList.Add(ctrl);

                    if (count == 1)
                    {
                        ctrl.transform.localPosition = Vector3.zero;
                    }
                    else
                    {
                        float offsetX = i == 0 ? -0.45f : 0.45f;
                        ctrl.transform.localPosition = new Vector3(offsetX, 0f, 0f);
                    }

                    ctrl.Init();
                    ctrl.SetMoveDir(Vector2.zero);
                    ctrl.SetAimDir(aimDir);
                    newControllers[i] = ctrl;
                }

                playerTwo.CharControllers = newControllers;
                PlayerTwoRuntime.PlayerTwoCharController = newControllers[0];
                FalconerIsolation.RefreshControllers(FirstPlayer, playerTwo);

                if (oldControllers != null)
                {
                    for (int i = 0; i < oldControllers.Length; i++)
                    {
                        PlayerCharController oldCtrl = oldControllers[i];
                        if (oldCtrl != null && oldCtrl.gameObject != null)
                        {
                            oldCtrl.gameObject.SetActive(false);
                            UnityEngine.Object.Destroy(oldCtrl.gameObject);
                        }
                    }
                }
            }
            finally
            {
                CoopContext.Exit(scope);
            }
        }

        private static void EnsureRoundInitialized()
        {
            if (_roundInitialized || BattleSaveData.I == null)
            {
                return;
            }

            // Always initialize from P1 context so BattleSaveData.I.Heroes/Passives belong to P1!
            var p1Scope = CoopContext.EnterPlayerOne(CoopContext.ScopeKind.Full);
            try
            {
                if (_roundInitialized || BattleSaveData.I == null)
                {
                    return;
                }

                _firstChar ??= BattleSaveData.I.CurChar ?? CoopMode.Character(0);
                _secondChar ??= CoopMode.Character(1);
                _firstCharInfo ??= _firstChar?.GetInfo() ?? UpgradeMgr.I?.CurChar;
                _firstComboChars ??= UpgradeMgr.I?.ComboChars;
                _firstPassives = BattleSaveData.I.Passives ?? new Il2CppSystem.Collections.Generic.List<PassiveInst>();
                _secondPassives = new Il2CppSystem.Collections.Generic.List<PassiveInst>();
                _firstHeroes = BattleSaveData.I.Heroes ?? HeroInventoryIsolation.PlayerOneHeroes ?? new Il2CppSystem.Collections.Generic.List<HeroInst>();

                CharInfo secondInfo = GetCharInfo(SecondType);
                _secondCharInfo = secondInfo ?? _secondChar?.GetInfo();
                _secondComboChars = new Il2CppSystem.Collections.Generic.List<CharInfo>();
                if (_secondChar?.CombinedTypes != null && _secondChar.CombinedTypes.Count > 0)
                {
                    for (int c = 0; c < _secondChar.CombinedTypes.Count; c++)
                    {
                        CharInfo ci = GetCharInfo(_secondChar.CombinedTypes[c]);
                        if (ci != null) _secondComboChars.Add(ci);
                    }
                }

                HeroInst p1Starter = _firstHeroes.Count > 0 ? _firstHeroes[0] : null;
                _starterClone = CreateStarterHeroClone(p1Starter);
                int starterBallLvl = Math.Max(0, BuildingMgr.I != null ? BuildingMgr.I.BallStarterLevel : 0);
                if (_starterClone != null)
                {
                    _starterClone.Lvl = Math.Max(_starterClone.Lvl, starterBallLvl);
                }

                _secondHeroes = HeroInventoryIsolation.PlayerTwoHeroes ?? new Il2CppSystem.Collections.Generic.List<HeroInst>();
                if (_secondHeroes.Count == 0 && _starterClone != null)
                {
                    _secondHeroes.Add(_starterClone);
                }
                else if (_secondHeroes.Count > 0 && _secondHeroes[0] != null)
                {
                    _secondHeroes[0].Lvl = Math.Max(_secondHeroes[0].Lvl, starterBallLvl);
                }

                if (secondInfo?.StarterUpgrade != null && secondInfo.StarterUpgrade.TryCast<PassiveInfo>() is PassiveInfo passiveStarter)
                {
                    int starterPassiveLvl = Math.Max(0, BuildingMgr.I != null ? BuildingMgr.I.PassiveStarterLevel : 0);
                    bool alreadyHas = false;
                    for (int i = 0; i < _secondPassives.Count; i++)
                    {
                        if (_secondPassives[i] != null && _secondPassives[i].Type == passiveStarter.Type)
                        {
                            alreadyHas = true;
                            break;
                        }
                    }
                    if (!alreadyHas)
                    {
                        var pInst = new PassiveInst(passiveStarter.Type);
                        pInst.Lvl = Math.Max(pInst.Lvl, starterPassiveLvl);
                        _secondPassives.Add(pInst);
                    }
                }

                HeroInventoryIsolation.BindIndependentLists(_firstHeroes, _secondHeroes);
                _roundInitialized = true;
                InvalidateStats();
                SyncCharacter();

                LogTownBonusesSnapshot();
            }
            finally
            {
                CoopContext.Exit(p1Scope);
            }
        }

        private static void LogTownBonusesSnapshot()
        {
            try
            {
                float p1Fire = 0f, p1Reload = 0f, p1Move = 0f, p2Fire = 0f, p2Reload = 0f, p2Move = 0f;
                int p1Follow = 0, p2Follow = 0;
                if (UpgradeMgr.I != null)
                {
                    ForPrimary(() =>
                    {
                        p1Fire = UpgradeMgr.I.FireRate;
                        p1Reload = UpgradeMgr.I.ReloadTime;
                        p1Move = UpgradeMgr.I.MoveSpeed;
                        p1Follow = UpgradeMgr.I.NumFollowers;
                    });
                    ForSecond(() =>
                    {
                        p2Fire = UpgradeMgr.I.FireRate;
                        p2Reload = UpgradeMgr.I.ReloadTime;
                        p2Move = UpgradeMgr.I.MoveSpeed;
                        p2Follow = UpgradeMgr.I.NumFollowers;
                    });
                }
                Plugin.Logger?.LogInfo(
                    $"Co-op town bonuses: BallStarter={BuildingMgr.I?.BallStarterLevel ?? 0}, PassiveStarter={BuildingMgr.I?.PassiveStarterLevel ?? 0}, " +
                    $"Revives={BuildingMgr.I?.NumRevives ?? 0}, LvlChoices={BuildingMgr.I?.NumLvlUpChoices ?? 3}, " +
                    $"P1(StarterLv={(_firstHeroes != null && _firstHeroes.Count > 0 ? _firstHeroes[0].Lvl : 0)}, MaxHP={GetMaxHealth(0)}, FireRate={p1Fire:0.00}, Reload={p1Reload:0.00}, Followers={p1Follow}, Move={p1Move:0.00}), " +
                    $"P2(StarterLv={(_secondHeroes != null && _secondHeroes.Count > 0 ? _secondHeroes[0].Lvl : 0)}, MaxHP={GetMaxHealth(1)}, FireRate={p2Fire:0.00}, Reload={p2Reload:0.00}, Followers={p2Follow}, Move={p2Move:0.00}), " +
                    $"Diff={BattleSaveData.I.CurDifficulty}, GameSpd={(TimeMgr.I != null ? TimeMgr.I.GetGameSpeed() : 1f):0.00}, Seed={BattleSaveData.I.Seed}");
            }
            catch { }
        }

        internal static HeroInst CreateStarterHeroClone(HeroInst p1Starter)
        {
            int starterLvl = Math.Max(0, BuildingMgr.I != null ? BuildingMgr.I.BallStarterLevel : 0);
            HeroInst clone = null;
            CharInfo secondInfo = GetCharInfo(SecondType);

            if (secondInfo?.StarterUpgrade != null && secondInfo.StarterUpgrade.TryCast<HeroInfo>() is HeroInfo heroInfo)
            {
                clone = new HeroInst(heroInfo.Type);
            }
            else if (p1Starter != null && p1Starter.GetInfo() != null)
            {
                clone = new HeroInst(p1Starter.GetInfo().Type);
            }
            else
            {
                // Guaranteed non-empty fallback when both players use passive-starter characters.
                clone = new HeroInst(HeroType.kBleed);
            }

            if (clone != null)
            {
                clone.Lvl = Math.Max(clone.Lvl, starterLvl);
                try
                {
                    if (BattleSaveData.I != null && clone.Id <= 0)
                    {
                        clone.Id = BattleSaveData.I.NumHeroesGotten++;
                    }
                }
                catch { }
                try { FXMgr.I?.InitBallVFXPool(clone.Type); } catch { }
            }
            return clone;
        }

        /// <summary>
        /// Executes an action under P1's full context while strictly preserving the CoopContext frame stack.
        /// </summary>
        public static void ForPrimary(Action action)
        {
            if (action == null)
            {
                return;
            }
            if (!CoopMode.Enabled)
            {
                action();
                return;
            }
            EnsureRoundInitialized();
            var scope = CoopContext.EnterPlayerOne(CoopContext.ScopeKind.Full);
            try
            {
                SyncCharacter();
                action();
            }
            finally
            {
                CoopContext.Exit(scope);
                SyncCharacter();
            }
        }

        /// <summary>
        /// Executes an action under P2's full context while strictly preserving the CoopContext frame stack.
        /// </summary>
        public static void ForSecond(Action action)
        {
            if (action == null)
            {
                return;
            }
            if (!CoopMode.Enabled)
            {
                action();
                return;
            }
            EnsureRoundInitialized();
            var scope = CoopContext.EnterPlayerTwo(PlayerTwoRuntime.PlayerTwo, CoopContext.ScopeKind.Full);
            try
            {
                SyncCharacter();
                action();
            }
            finally
            {
                CoopContext.Exit(scope);
                SyncCharacter();
            }
        }

        public static void ForSlot(int slot, Action action)
        {
            if (slot == 1) ForSecond(action);
            else ForPrimary(action);
        }

        public static void SyncCharacter()
        {
            if (!CoopMode.Enabled || BattleSaveData.I == null || !_roundInitialized)
            {
                return;
            }

            bool isP2 = CoopContext.IsFullPlayerTwoContext || HeroInventoryIsolation.IsPlayerTwoActive;
            CharBattleInst targetChar = isP2 ? _secondChar : _firstChar;
            var targetPassives = isP2 ? _secondPassives : _firstPassives;
            var targetHeroes = isP2 ? SecondHeroes : (_firstHeroes ?? HeroInventoryIsolation.PlayerOneHeroes);

            if (targetChar != null)
            {
                if (!HarmonyHelper.SameNative(BattleSaveData.I.CurChar, targetChar))
                {
                    BattleSaveData.I.CurChar = targetChar;
                }
                if (!HarmonyHelper.SameNative(CharBattleInst.I, targetChar))
                {
                    CharBattleInst.I = targetChar;
                }
            }
            if (targetPassives != null && !HarmonyHelper.SameNative(BattleSaveData.I.Passives, targetPassives))
            {
                BattleSaveData.I.Passives = targetPassives;
            }
            if (targetHeroes != null && !HarmonyHelper.SameNative(BattleSaveData.I.Heroes, targetHeroes))
            {
                BattleSaveData.I.Heroes = targetHeroes;
            }

            int slotIdx = isP2 ? 1 : 0;
            if (UpgradeMgr.I != null)
            {
                CharInfo desiredInfo = slotIdx == 1 ? _secondCharInfo : (_firstCharInfo ?? _firstChar?.GetInfo());
                if (desiredInfo != null && !HarmonyHelper.SameNative(UpgradeMgr.I.CurChar, desiredInfo))
                {
                    UpgradeMgr.I.CurChar = desiredInfo;
                }

                // Always assign ComboChars (even when null/empty) so P2's combo characters never bleed into P1 or vice versa.
                var desiredCombo = slotIdx == 1 ? _secondComboChars : _firstComboChars;
                if (!HarmonyHelper.SameNative(UpgradeMgr.I.ComboChars, desiredCombo))
                {
                    UpgradeMgr.I.ComboChars = desiredCombo;
                }

                if (!_calculatingSlotStats && _lastAppliedStatsSlot != slotIdx && SlotStatsValid[slotIdx] && SlotStats[slotIdx] != null)
                {
                    ApplyStatsSnapshot(UpgradeMgr.I, SlotStats[slotIdx]);
                    _lastAppliedStatsSlot = slotIdx;
                }
            }
        }

        public static int GetMaxHealth(int slot)
        {
            int clamped = slot == 1 ? 1 : 0;
            if (SlotStatsValid[clamped] && SlotStats[clamped] != null && SlotStats[clamped].MaxHealth > 0)
            {
                return SlotStats[clamped].MaxHealth;
            }
            if (UpgradeMgr.I == null)
            {
                return 100;
            }
            if (_roundInitialized)
            {
                InvalidateStats();
                if (SlotStatsValid[clamped] && SlotStats[clamped] != null && SlotStats[clamped].MaxHealth > 0)
                {
                    return SlotStats[clamped].MaxHealth;
                }
            }
            return Math.Max(1, UpgradeMgr.I.MaxHealth);
        }

        public static void InvalidateStats()
        {
            if (!_roundInitialized || UpgradeMgr.I == null)
            {
                return;
            }
            _calculatingSlotStats = true;
            _lastAppliedStatsSlot = -1;
            try
            {
                ForSecond(() =>
                {
                    UpgradeMgr.I.CalculateStats();
                    try { UpgradeMgr.I.RefreshFollowerOrder(); } catch { }
                    SlotStats[1] = CaptureStatsSnapshot(UpgradeMgr.I, SlotStats[1]);
                    SlotStatsValid[1] = true;
                });
                ForPrimary(() =>
                {
                    UpgradeMgr.I.CalculateStats();
                    try { UpgradeMgr.I.RefreshFollowerOrder(); } catch { }
                    SlotStats[0] = CaptureStatsSnapshot(UpgradeMgr.I, SlotStats[0]);
                    SlotStatsValid[0] = true;
                    _lastAppliedStatsSlot = 0;
                });
            }
            finally
            {
                _calculatingSlotStats = false;
            }
            SyncCharacter();
        }

        public static int SlotOf(Player player)
        {
            if (player == null) return 0;
            return CoopContext.IsPlayerTwo(player) ? 1 : 0;
        }

        public static Player PlayerFor(int slot) => slot == 1 ? PlayerTwoRuntime.PlayerTwo : (PlayerTwoRuntime.PlayerOne ?? Player.I);
        public static CharBattleInst CharacterFor(int slot) => slot == 1 ? _secondChar : (_firstChar ?? BattleSaveData.I?.CurChar);
        public static Il2CppSystem.Collections.Generic.List<HeroInst> HeroesFor(int slot) =>
            slot == 1 ? SecondHeroes : (_firstHeroes ?? BattleSaveData.I?.Heroes);
        public static Il2CppSystem.Collections.Generic.List<PassiveInst> PassivesFor(int slot) =>
            slot == 1 ? _secondPassives : (_firstPassives ?? BattleSaveData.I?.Passives);

        private static bool OnCalculateStatsPrefix()
        {
            if (!CoopMode.Enabled)
            {
                return true;
            }
            if (_roundInitialized)
            {
                SyncCharacter();
            }
            return true;
        }

        private static void OnCalculateStatsPostfix(UpgradeMgr __instance)
        {
            if (!CoopMode.Enabled || !_roundInitialized || _calculatingSlotStats || __instance == null)
            {
                return;
            }
            int slot = (CoopContext.IsFullPlayerTwoContext || HeroInventoryIsolation.IsPlayerTwoActive) ? 1 : 0;
            try { __instance.RefreshFollowerOrder(); } catch { }
            SlotStats[slot] = CaptureStatsSnapshot(__instance, SlotStats[slot]);
            SlotStatsValid[slot] = true;
            _lastAppliedStatsSlot = slot;
        }

        private static void OnCreateBallPrefix(ref HeroInst b)
        {
            if (!CoopMode.Enabled || b == null)
            {
                return;
            }
            if (CoopContext.IsPlayerTwoContext)
            {
                EnsureRoundInitialized();
            }
        }

        private static void OnCreateBallPostfix()
        {
            SyncCharacter();
        }

        private static PropAccessor<TValue>[] BuildTypedAccessors<TValue>(bool excludeTgtXP)
        {
            var list = new List<PropAccessor<TValue>>();
            try
            {
                PropertyInfo[] props = typeof(UpgradeMgr).GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public);
                foreach (PropertyInfo p in props)
                {
                    if (!p.CanRead || !p.CanWrite || p.PropertyType != typeof(TValue))
                    {
                        continue;
                    }
                    if (excludeTgtXP && p.Name == nameof(UpgradeMgr.TgtXP))
                    {
                        continue;
                    }
                    MethodInfo getter = p.GetGetMethod(true);
                    MethodInfo setter = p.GetSetMethod(true);
                    if (getter == null || setter == null)
                    {
                        continue;
                    }
                    try
                    {
                        list.Add(new PropAccessor<TValue>
                        {
                            Get = (Func<UpgradeMgr, TValue>)Delegate.CreateDelegate(typeof(Func<UpgradeMgr, TValue>), getter),
                            Set = (Action<UpgradeMgr, TValue>)Delegate.CreateDelegate(typeof(Action<UpgradeMgr, TValue>), setter)
                        });
                    }
                    catch
                    {
                        PropertyInfo propRef = p;
                        list.Add(new PropAccessor<TValue>
                        {
                            Get = mgr => (TValue)propRef.GetValue(mgr),
                            Set = (mgr, val) => propRef.SetValue(mgr, val)
                        });
                    }
                }
            }
            catch { }
            return list.ToArray();
        }

        private static SlotStatSnapshot CaptureStatsSnapshot(UpgradeMgr mgr, SlotStatSnapshot reuse = null)
        {
            SlotStatSnapshot snap = reuse ?? new SlotStatSnapshot();
            float[] floats = snap.FloatValues != null && snap.FloatValues.Length == FloatStatAccessors.Length
                ? snap.FloatValues
                : new float[FloatStatAccessors.Length];
            for (int i = 0; i < floats.Length; i++)
            {
                try { floats[i] = FloatStatAccessors[i].Get(mgr); } catch { floats[i] = 0f; }
            }

            int[] ints = snap.IntValues != null && snap.IntValues.Length == IntStatAccessors.Length
                ? snap.IntValues
                : new int[IntStatAccessors.Length];
            for (int i = 0; i < ints.Length; i++)
            {
                try { ints[i] = IntStatAccessors[i].Get(mgr); } catch { ints[i] = 0; }
            }

            bool[] bools = snap.BoolValues != null && snap.BoolValues.Length == BoolStatAccessors.Length
                ? snap.BoolValues
                : new bool[BoolStatAccessors.Length];
            for (int i = 0; i < bools.Length; i++)
            {
                try { bools[i] = BoolStatAccessors[i].Get(mgr); } catch { bools[i] = false; }
            }

            int[] mInts = null;
            if (mgr.MiscBonusesInt != null)
            {
                int len = mgr.MiscBonusesInt.Length;
                mInts = snap.MiscInts != null && snap.MiscInts.Length == len ? snap.MiscInts : new int[len];
                for (int i = 0; i < len; i++) mInts[i] = mgr.MiscBonusesInt[i];
            }

            float[] mFloats = null;
            if (mgr.MiscBonusesFloat != null)
            {
                int len = mgr.MiscBonusesFloat.Length;
                mFloats = snap.MiscFloats != null && snap.MiscFloats.Length == len ? snap.MiscFloats : new float[len];
                for (int i = 0; i < len; i++) mFloats[i] = mgr.MiscBonusesFloat[i];
            }

            snap.MaxHealth = mgr.MaxHealth;
            snap.FloatValues = floats;
            snap.IntValues = ints;
            snap.BoolValues = bools;
            snap.MiscInts = mInts;
            snap.MiscFloats = mFloats;
            snap.CurChar = mgr.CurChar;
            snap.ComboChars = mgr.ComboChars;
            snap.BabyRattlePassive = mgr.BabyRattlePassive;
            snap.UpturnedHatchetPassive = mgr.UpturnedHatchetPassive;
            snap.SpikedCollarPassive = mgr.SpikedCollarPassive;
            snap.MagicStaffPassive = mgr.MagicStaffPassive;
            snap.BoulderPassive = mgr.BoulderPassive;
            return snap;
        }

        private static void ApplyStatsSnapshot(UpgradeMgr mgr, SlotStatSnapshot snap)
        {
            if (snap == null)
            {
                return;
            }
            if (snap.FloatValues != null)
            {
                int n = Math.Min(FloatStatAccessors.Length, snap.FloatValues.Length);
                for (int i = 0; i < n; i++)
                {
                    try { FloatStatAccessors[i].Set(mgr, snap.FloatValues[i]); } catch { }
                }
            }
            if (snap.IntValues != null)
            {
                int n = Math.Min(IntStatAccessors.Length, snap.IntValues.Length);
                for (int i = 0; i < n; i++)
                {
                    try { IntStatAccessors[i].Set(mgr, snap.IntValues[i]); } catch { }
                }
            }
            if (snap.BoolValues != null)
            {
                int n = Math.Min(BoolStatAccessors.Length, snap.BoolValues.Length);
                for (int i = 0; i < n; i++)
                {
                    try { BoolStatAccessors[i].Set(mgr, snap.BoolValues[i]); } catch { }
                }
            }
            if (snap.CurChar != null)
            {
                mgr.CurChar = snap.CurChar;
            }
            mgr.ComboChars = snap.ComboChars;
            mgr.BabyRattlePassive = snap.BabyRattlePassive;
            mgr.UpturnedHatchetPassive = snap.UpturnedHatchetPassive;
            mgr.SpikedCollarPassive = snap.SpikedCollarPassive;
            mgr.MagicStaffPassive = snap.MagicStaffPassive;
            mgr.BoulderPassive = snap.BoulderPassive;

            if (snap.MiscInts != null && mgr.MiscBonusesInt != null)
            {
                int n = Math.Min(snap.MiscInts.Length, mgr.MiscBonusesInt.Length);
                for (int i = 0; i < n; i++) mgr.MiscBonusesInt[i] = snap.MiscInts[i];
            }
            if (snap.MiscFloats != null && mgr.MiscBonusesFloat != null)
            {
                int n = Math.Min(snap.MiscFloats.Length, mgr.MiscBonusesFloat.Length);
                for (int i = 0; i < n; i++) mgr.MiscBonusesFloat[i] = snap.MiscFloats[i];
            }
        }
    }
}
