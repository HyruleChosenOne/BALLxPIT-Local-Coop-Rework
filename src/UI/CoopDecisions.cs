using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.Input;
using HarmonyLib;
using I2.Loc;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Rewired;
using UnityEngine;

namespace BALLxPITLocalCoop.UI
{
    /// <summary>
    /// Synchronized dual-player Level-Up & Fuser decision controller.
    /// Driven once per frame from Update() with pre-computed Option view-models (eliminating per-frame Regex/I2 churn),
    /// unified synchronous P1/P2 rerolls (eliminating _AnimateReroll race and 350ms P2 freeze),
    /// pre-commit Fission Summary snapshots, single-click Fission undo, and softlock-proof Commit.
    /// </summary>
    public static class CoopDecisions
    {
        private sealed class Option
        {
            public string Name;
            public UpgradeChoice Upgrade;
            public HeroCombo? Combo;
            public bool IsFreeFuserUpgrade;
            public bool IsCoinBagFallback;
            public bool IsHealFallback;
            public int FallbackAmount;

            // Pre-computed view-model fields (built once per BuildSlot, zero per-frame Regex/I2 overhead!)
            public string DisplayRowTitle;
            public string RichDescription;
            public string FuserShortName;
            public string FuserPlaque;
            public Sprite MainIcon;
            public Sprite FusionSecondIcon;
            public Sprite RecipeIng1;
            public Sprite RecipeIng2;
        }

        private enum FuserCategory
        {
            Evolution = 0,
            Fusion = 1,
            Fission = 2
        }

        private sealed class FissionGrant
        {
            public Sprite Icon;
            public string Name;
            public int OldLvl;
            public int NewLvl;
            public int Gold;
        }

        private sealed class PreCommitFuserSnapshot
        {
            public bool IsFission;
            public bool HasFusedItem;
            public string ShortName;
            public string Plaque;
            public Sprite ResultIcon;
            public Sprite Ing1;
            public Sprite Ing2;
        }

        private sealed class ListOps
        {
            public PropertyInfo CountProp;
            public MethodInfo GetItemMethod;
            public MethodInfo ClearMethod;
            public MethodInfo AddMethod;
            public IntPtr NativeAddRangePtr;
        }

        private sealed class ChoiceListBackup
        {
            public object ListRef;
            public object NativeClone;
            public object[] Elements;
        }

        private static readonly Regex ParamTokenRegex = new Regex(@"\{\[([^\]]+)\]\}", RegexOptions.Compiled);

        private static readonly List<Option>[] Options = { new List<Option>(8), new List<Option>(8) };
        private static readonly List<Option>[] AllFuserEvos = { new List<Option>(8), new List<Option>(8) };
        private static readonly List<Option>[] AllFuserCombos = { new List<Option>(16), new List<Option>(16) };
        private static readonly Option[] FissionOptions = new Option[2];
        private static readonly bool[] SlotHasFreeUpgrades = new bool[2];
        private static readonly bool[] InFuserCategoryMenu = { true, true };
        private static readonly int[] FuserCategoryIndex = { 0, 0 };
        private static readonly FuserCategory[] ActiveFuserCategory = { FuserCategory.Fission, FuserCategory.Fission };
        private static readonly List<FissionGrant>[] LastFissionGrants = { new List<FissionGrant>(8), new List<FissionGrant>(8) };
        private static readonly PreCommitFuserSnapshot[] PreCommitSnapshots = { new PreCommitFuserSnapshot(), new PreCommitFuserSnapshot() };

        private static bool _showingFissionSummary;
        private static float _fissionSummaryArmedAt;

        private static readonly DecisionGate Gate = new DecisionGate();
        private static readonly float[] NextInput = new float[2];
        private static readonly bool[] Banishing = new bool[2];
        private static readonly int[] SlotRerolls = new int[2];
        private static readonly int[] SlotFreeRerolls = new int[2];
        private static readonly int[] SlotRerollsInRow = new int[2];

        private static readonly List<PropertyInfo> CachedChoiceLists = new List<PropertyInfo>(12);
        private static readonly Dictionary<Type, ListOps> CachedListOps = new Dictionary<Type, ListOps>(8);
        private static readonly Dictionary<HeroType, HeroInfo> HeroInfoCache = new Dictionary<HeroType, HeroInfo>(32);
        private static IntPtr _nativeUpgradeChoiceListAddPtr;

        private static LevelUpUI UI;
        private static bool _installed;
        private static bool _pending;
        private static bool _runRerollsInitialized;
        private static int _lastObservedBattleFreeRerolls;
        private static bool _applying;
        private static bool _started;
        private static bool _awaitResume;
        private static float _armedAt;

        private static LocalizationParamsManager _descriptionParams;
        private static bool _descriptionWarningLogged;

        public static bool IsPending => _pending;

        public static void Install(Harmony harmony)
        {
            if (harmony == null || _installed)
            {
                return;
            }
            _installed = true;
            CachedChoiceLists.Clear();

            PropertyInfo[] props = typeof(LevelUpUI).GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public);
            foreach (PropertyInfo prop in props)
            {
                if (prop.CanRead && prop.CanWrite && (prop.Name.StartsWith("_avail", StringComparison.Ordinal)
                    || prop.Name == "_choices" || prop.Name == "_fusionChoices" || prop.Name == "_prevChoices"))
                {
                    CachedChoiceLists.Add(prop);
                }
            }

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(LevelUpUI), nameof(LevelUpUI.Activate), new[] { typeof(LevelUpType) }),
                prefix: new HarmonyMethod(typeof(CoopDecisions), nameof(CanOpen)),
                postfix: new HarmonyMethod(typeof(CoopDecisions), nameof(Open)),
                label: "LevelUpUI.Activate");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(LevelUpUI), nameof(LevelUpUI.MyUpdate)),
                prefix: new HarmonyMethod(typeof(CoopDecisions), nameof(OnMyUpdate)),
                label: "LevelUpUI.MyUpdate");

            string[] guarded =
            {
                nameof(LevelUpUI.SelectBtn),
                nameof(LevelUpUI.HoverBtn),
                nameof(LevelUpUI.SelectEvo),
                nameof(LevelUpUI.SelectFuserOption),
                nameof(LevelUpUI.SelectComboComponent),
                nameof(LevelUpUI.OnRerollClicked),
                nameof(LevelUpUI.OnBanishClicked),
                nameof(LevelUpUI.OnSelectUpgradeClicked),
                nameof(LevelUpUI.OnContinueClicked),
                nameof(LevelUpUI.ApplyCombo),
                nameof(LevelUpUI.CloseLevelUp),
                nameof(LevelUpUI.OnCloseClicked),
                nameof(LevelUpUI.SkipFreeUpgrades),
                nameof(LevelUpUI.ConfirmIgnoreFuser),
                nameof(LevelUpUI.ConfirmIgnoreBonus)
            };
            foreach (string method in guarded)
            {
                HarmonyHelper.TryPatch(
                    harmony,
                    AccessTools.Method(typeof(LevelUpUI), method),
                    prefix: new HarmonyMethod(typeof(CoopDecisions), nameof(GuardNativeClick)),
                    label: $"LevelUpUI.{method}");
            }

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(LevelUpUI), nameof(LevelUpUI.ShouldAutopick)),
                prefix: new HarmonyMethod(typeof(CoopDecisions), nameof(OnShouldAutopick)),
                label: "LevelUpUI.ShouldAutopick");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(LevelUpUI), nameof(LevelUpUI.Deactivate)),
                postfix: new HarmonyMethod(typeof(CoopDecisions), nameof(OnDeactivate)),
                label: "LevelUpUI.Deactivate");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(LevelUpUI), nameof(LevelUpUI.OnExitComplete)),
                postfix: new HarmonyMethod(typeof(CoopDecisions), nameof(OnExitComplete)),
                label: "LevelUpUI.OnExitComplete");
        }

        public static void Shutdown()
        {
            AbortForSceneChange();
            if (HarmonyHelper.IsUnityAlive(_descriptionParams))
            {
                UnityEngine.Object.Destroy(_descriptionParams.gameObject);
            }
            _descriptionParams = null;
            CachedChoiceLists.Clear();
            CachedListOps.Clear();
            HeroInfoCache.Clear();
            _nativeUpgradeChoiceListAddPtr = IntPtr.Zero;
            _installed = false;
        }

        public static void ResetRound()
        {
            _runRerollsInitialized = false;
            _lastObservedBattleFreeRerolls = 0;
            SlotFreeRerolls[0] = SlotFreeRerolls[1] = 0;
            SlotRerollsInRow[0] = SlotRerollsInRow[1] = 0;
            SlotRerolls[0] = SlotRerolls[1] = 0;
            HeroInfoCache.Clear();
            AbortForSceneChange();
        }

        public static void AbortForSceneChange()
        {
            _pending = false;
            _started = false;
            _awaitResume = false;
            ResetFuserState();
            UI = null;
            ModalOverlayState.ClearIf(ActiveModalOverlay.LevelUp);
            ChoiceOverlay.Hide();
        }

        private static bool CanOpen() =>
            !CoopMode.Enabled || BattleSaveData.I == null || !BattleSaveData.I.CompletedLevel;

        private static void Open(LevelUpUI __instance)
        {
            if (!CanOpen() || __instance == null)
            {
                return;
            }
            if (!CoopMode.Enabled || !CoopCharacterRuntime.IndependentReady)
            {
                _pending = false;
                return;
            }
            // Verify ChoiceOverlay actually exists before suppressing vanilla LevelUpUI!
            if (!ChoiceOverlay.EnsureCreated(__instance))
            {
                Plugin.Logger?.LogWarning("ChoiceOverlay unavailable; falling back to vanilla LevelUpUI.");
                _pending = false;
                return;
            }

            UI = __instance;
            _started = false;
            SlotRerolls[0] = SlotRerolls[1] = 0;
            SlotRerollsInRow[0] = SlotRerollsInRow[1] = 0;
            InitializeOrSyncSlotFreeRerolls(__instance);
            Banishing[0] = Banishing[1] = false;
            ResetFuserState();
            Gate.Reset();
            NextInput[0] = NextInput[1] = 0f;

            _pending = true;
            ModalOverlayState.Set(ActiveModalOverlay.LevelUp);
            ChoiceOverlay.ClearChoices();
            GamepadRouter.VibratePad(1, 0.3f, 0.15f);
            _armedAt = Time.unscaledTime + 0.25f;
        }

        private static bool GuardNativeClick() => !_pending || _applying;

        private static bool OnShouldAutopick(ref bool __result)
        {
            if (!_pending) return true;
            __result = false;
            return false;
        }

        private static bool OnMyUpdate()
        {
            // Suppress vanilla LevelUpUI.MyUpdate while our Co-op decision overlay owns the menu.
            return !_pending;
        }

        private static void ResetFuserState()
        {
            _showingFissionSummary = false;
            _fissionSummaryArmedAt = 0f;
            for (int i = 0; i < 2; i++)
            {
                InFuserCategoryMenu[i] = true;
                FuserCategoryIndex[i] = 0;
                ActiveFuserCategory[i] = FuserCategory.Fission;
                SlotHasFreeUpgrades[i] = false;
                AllFuserEvos[i].Clear();
                AllFuserCombos[i].Clear();
                FissionOptions[i] = null;
                LastFissionGrants[i].Clear();
                PreCommitSnapshots[i] = new PreCommitFuserSnapshot();
            }
        }

        private static void OnDeactivate()
        {
            _pending = false;
            _started = false;
            ResetFuserState();
            UI = null;
            ModalOverlayState.ClearIf(ActiveModalOverlay.LevelUp);
            ChoiceOverlay.Hide();
        }

        private static void OnExitComplete()
        {
            if (_awaitResume && !_pending)
            {
                _awaitResume = false;
                if (GameMgr.I != null
                    && (GameMgr.I.CurState == GameState.kLevelUp
                        || GameMgr.I.CurState == GameState.kPickTreasure
                        || GameMgr.I.CurState == GameState.kBonusBall
                        || GameMgr.I.CurState == GameState.kBonusPassive))
                {
                    GameMgr.I.SetState(GameState.kPlaying);
                }
            }
        }

        // --- Reroll & Banish Economy ---

        private static void InitializeOrSyncSlotFreeRerolls(LevelUpUI ui)
        {
            if (BattleSaveData.I == null) return;

            if (!_runRerollsInitialized)
            {
                _runRerollsInitialized = true;
                int baseFree = Math.Max(BattleSaveData.I.NumFreeRerolls, BuildingMgr.I != null ? BuildingMgr.I.NumFreeRerolls : 0);
                SlotFreeRerolls[0] = baseFree;
                SlotFreeRerolls[1] = baseFree;
                _lastObservedBattleFreeRerolls = baseFree;
            }
            else if (BattleSaveData.I.NumFreeRerolls > _lastObservedBattleFreeRerolls)
            {
                int gained = BattleSaveData.I.NumFreeRerolls - _lastObservedBattleFreeRerolls;
                SlotFreeRerolls[0] += gained;
                SlotFreeRerolls[1] += gained;
            }

            BattleSaveData.I.NumFreeRerolls = SlotFreeRerolls[0];
            BattleSaveData.I.NumRerollsInARow = SlotRerollsInRow[0];
            _lastObservedBattleFreeRerolls = BattleSaveData.I.NumFreeRerolls;
            try { ui?.RefreshRerolls(); } catch { }
        }

        private static int SlotRerollCost(int slot)
        {
            if (slot < 0 || slot >= 2 || SlotFreeRerolls[slot] > 0) return 0;
            float baseReroll = BuildingMgr.kBaseRerollCost > 0 ? BuildingMgr.kBaseRerollCost : 50f;
            float mult = BuildingMgr.I != null && BuildingMgr.I.RerollCostMult > 0f ? BuildingMgr.I.RerollCostMult : 1f;
            int baseCost = Mathf.Max(1, Mathf.FloorToInt(baseReroll * mult));
            int n = Math.Max(0, SlotRerollsInRow[slot]);
            return ((n + 1) * (n + 2) / 2) * baseCost;
        }

        private static bool CanReroll(int slot)
        {
            if (UI == null || BattleSaveData.I == null || UI.Type == LevelUpType.kFuser)
            {
                return false;
            }
            if (slot < 0 || slot >= 2 || Gate.Ready[slot] || (Options[slot].Count == 1 && IsNoOpOption(Options[slot][0])))
            {
                return false;
            }
            if (SlotFreeRerolls[slot] > 0)
            {
                return true;
            }
            int gold = BattleSaveData.I.GetNumResources(ResourceType.kGold);
            int cost = SlotRerollCost(slot);
            // Do NOT gate P2 on UI.BtnReroll.gameObject.activeInHierarchy (which reflects P1's cost!).
            return cost >= 0 && gold >= cost;
        }

        private static bool CanBanish() =>
            UI != null && BattleSaveData.I != null && UI.Type != LevelUpType.kFuser && BattleSaveData.I.NumBanishes > 0;

        private static bool IsNoOpOption(Option opt) =>
            opt == null || (opt.Upgrade == null && !opt.Combo.HasValue && !opt.IsFreeFuserUpgrade && !opt.IsCoinBagFallback && !opt.IsHealFallback);

        private static bool IsFallbackResourceOption(Option opt) =>
            opt != null && (opt.IsCoinBagFallback || opt.IsHealFallback);

        private static bool SlotIsMaxedFallbackOnly(int slot)
        {
            if (slot < 0 || slot > 1 || CoopVitals.Down(slot) || Options[slot].Count == 0) return false;
            for (int i = 0; i < Options[slot].Count; i++)
            {
                if (!IsFallbackResourceOption(Options[slot][i])) return false;
            }
            return true;
        }

        // --- Option Formatting & Pre-Computed ViewModels ---

        public static string SafeUpgradeName(UpgradeInfo info, string fallback = "")
        {
            if (info == null) return fallback;
            try
            {
                string translated = CoopLocalization.Translate(info.GetNameSlug());
                if (CoopLocalization.IsValidTranslation(translated)) return translated;
            }
            catch { }
            return !string.IsNullOrEmpty(info.Name) ? info.Name : fallback;
        }

        private static string UpgradeName(UpgradeChoice choice)
        {
            if (choice == null || choice.Info == null) return "Continue";
            return SafeUpgradeName(choice.Info, CoopLocalization.PrettifyEnumName(choice.Type.ToString()));
        }

        private static HeroInfo FindHeroInfo(HeroType type)
        {
            if (HeroInfoCache.TryGetValue(type, out HeroInfo cached) && HarmonyHelper.IsUnityAlive(cached))
            {
                return cached;
            }
            var heroes = InfoDB.I?.Heroes;
            if (heroes != null)
            {
                for (int i = 0; i < heroes.Length; i++)
                {
                    HeroInfo heroInfo = heroes[i];
                    if (heroInfo != null) HeroInfoCache[heroInfo.Type] = heroInfo;
                }
                if (HeroInfoCache.TryGetValue(type, out cached) && HarmonyHelper.IsUnityAlive(cached))
                {
                    return cached;
                }
            }
            HeroInfo fallback = new HeroInst(type).GetInfo();
            if (fallback != null) HeroInfoCache[type] = fallback;
            return fallback;
        }

        private static string HeroName(HeroType type)
        {
            HeroInfo info = FindHeroInfo(type);
            try
            {
                string translated = CoopLocalization.Translate(info.GetNameSlug());
                if (CoopLocalization.IsValidTranslation(translated)) return translated;
            }
            catch { }
            return info != null && !string.IsNullOrEmpty(info.Name) ? info.Name : CoopLocalization.PrettifyEnumName(type.ToString());
        }

        private static string Description(UpgradeInfo info, int level = 1)
        {
            if (info == null) return string.Empty;
            string translation = info.Desc ?? string.Empty;
            try
            {
                string slugged = CoopLocalization.Translate(info.GetDescSlug());
                if (CoopLocalization.IsValidTranslation(slugged)) translation = slugged;

                if (!HarmonyHelper.IsUnityAlive(_descriptionParams))
                {
                    GameObject holder = new GameObject("CoopDescriptionParameters");
                    holder.SetActive(true);
                    UnityEngine.Object.DontDestroyOnLoad(holder);
                    _descriptionParams = holder.AddComponent<LocalizationParamsManager>();
                    _descriptionParams._IsGlobalManager = false;
                }
                if (!_descriptionParams.gameObject.activeSelf)
                {
                    _descriptionParams.gameObject.SetActive(true);
                }

                _descriptionParams._Params.Clear();
                info.ApplyPropertyParams(_descriptionParams);
                info.ApplyUpgradeParams(level, _descriptionParams, colorize: true);
                LocalizationManager.ApplyLocalizationParams(ref translation, _descriptionParams.gameObject, true);

                if (!string.IsNullOrEmpty(translation) && translation.Contains("{["))
                {
                    MatchCollection matches = ParamTokenRegex.Matches(translation);
                    for (int m = 0; m < matches.Count; m++)
                    {
                        Match match = matches[m];
                        string key = match.Groups[1].Value;
                        string val = null;
                        try { val = _descriptionParams.GetParameterValue(key); } catch { }
                        if (string.IsNullOrEmpty(val))
                        {
                            try { val = LocalizationManager.GetLocalizationParam(key, _descriptionParams.gameObject); } catch { }
                        }
                        if (string.IsNullOrEmpty(val))
                        {
                            try
                            {
                                var pList = _descriptionParams._Params;
                                if (pList != null)
                                {
                                    for (int i = 0; i < pList.Count; i++)
                                    {
                                        if (string.Equals(pList[i].Name, key, StringComparison.OrdinalIgnoreCase))
                                        {
                                            val = pList[i].Value;
                                            break;
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                        if (!string.IsNullOrEmpty(val))
                        {
                            translation = translation.Replace(match.Value, val);
                        }
                    }
                }
                return translation;
            }
            catch (Exception ex)
            {
                if (!_descriptionWarningLogged)
                {
                    _descriptionWarningLogged = true;
                    Plugin.Logger?.LogWarning("Description parameter resolution failed: " + ex.Message);
                }
                return translation;
            }
        }

        private static int CurrentItemLevelInActiveContext(UpgradeChoice choice)
        {
            if (choice == null || choice.IsNew || BattleSaveData.I == null) return 0;
            int idx = choice.EquipmentIdx;
            if (choice.Type == UpgradeType.kHero && BattleSaveData.I.Heroes != null && idx >= 0 && idx < BattleSaveData.I.Heroes.Count)
            {
                return BattleSaveData.I.Heroes[idx].Lvl;
            }
            if (choice.Type == UpgradeType.kPassive && BattleSaveData.I.Passives != null && idx >= 0 && idx < BattleSaveData.I.Passives.Count)
            {
                return BattleSaveData.I.Passives[idx].Lvl;
            }
            LevelUpCurEquipItem item = UI != null ? UI.GetCurUpgradeItem(choice) : null;
            return item != null ? item.DisplayedLvl : 0;
        }

        private static bool TryGetMergeRecipe(UpgradeChoice choice, out UpgradeInfo comp1, out UpgradeInfo comp2)
        {
            comp1 = null;
            comp2 = null;
            UpgradeInfo info = choice?.Info;
            if (info == null) return false;
            try
            {
                if (!info.IsMerged() || info.MergeComponents == null || info.MergeComponents.Count == 0) return false;
                int idx = (choice.EvoIdx >= 0 && choice.EvoIdx < info.MergeComponents.Count) ? choice.EvoIdx : 0;
                var pair = info.MergeComponents[idx];
                if (pair == null || pair.Count < 2) pair = info.MergeComponents[0];
                if (pair != null && pair.Count >= 2)
                {
                    comp1 = pair[0];
                    comp2 = pair[1];
                    return comp1 != null && comp2 != null;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Pre-computes all localized labels, icons, and descriptions for an Option while ALREADY inside that slot's CoopCharacterRuntime context!
        /// </summary>
        private static void PopulateOptionViewModelInSlotContext(Option opt)
        {
            if (opt == null) return;

            if (opt.Upgrade != null)
            {
                UpgradeChoice choice = opt.Upgrade;
                UpgradeInfo info = choice.Info;
                string title = UpgradeName(choice);
                int rawCur = choice.IsNew ? 0 : CurrentItemLevelInActiveContext(choice);
                int curLvl = choice.IsNew ? 0 : Math.Max(1, rawCur + 1);
                int nextLvl = choice.IsNew ? 1 : curLvl + 1;
                int nextParamLvl = choice.IsNew ? 0 : Mathf.Clamp(rawCur + 1, 0, 4);

                bool isPassive = choice.Type == UpgradeType.kPassive;
                bool isMerged = false;
                try { isMerged = info != null && info.IsMerged(); } catch { }

                if (isMerged)
                {
                    string evoTag = CoopLocalization.Text("EVOLUTION", "ÉVOLUTION");
                    opt.DisplayRowTitle = (choice.IsNew || curLvl <= 0)
                        ? $"<b>{title}</b>  <color=#C996FF>[{evoTag}]</color>"
                        : $"<b>{title}</b>  <color=#C996FF>[{evoTag} {curLvl}->{nextLvl}]</color>";
                }
                else if (choice.IsNew || curLvl <= 0)
                {
                    opt.DisplayRowTitle = $"<b>{title}</b>  <color=#78E69C>[{CoopLocalization.Text("NEW!", "NOUVEAU !")}]</color>";
                }
                else
                {
                    string lv = CoopLocalization.Text("LV", "NV");
                    opt.DisplayRowTitle = $"<b>{title}</b>  <color=#F4C86A>[{lv} {curLvl}->{nextLvl}]</color>";
                }

                string recipe = null;
                if (TryGetMergeRecipe(choice, out UpgradeInfo m1, out UpgradeInfo m2))
                {
                    opt.RecipeIng1 = m1.Icon;
                    opt.RecipeIng2 = m2.Icon;
                    string c1 = SafeUpgradeName(m1);
                    string c2 = SafeUpgradeName(m2);
                    if (!string.IsNullOrEmpty(c1) && !string.IsNullOrEmpty(c2)) recipe = c1 + " x " + c2;
                }

                string kindText = isPassive
                    ? CoopLocalization.Text("Passive", "Passif")
                    : (isMerged ? CoopLocalization.Text("Evolution", "Évolution") : CoopLocalization.Text("Ball", "Balle"));
                string headerColor = isMerged ? "#C996FF" : (choice.IsNew || curLvl <= 0 ? "#78E69C" : "#F4C86A");
                string progressText = (choice.IsNew || curLvl <= 0)
                    ? CoopLocalization.Text($"New {kindText}!", isPassive ? "Nouveau passif !" : (isMerged ? "Nouvelle évolution !" : "Nouvelle balle !"))
                    : CoopLocalization.Text($"{kindText}  |  LV {curLvl} -> {nextLvl}", $"{kindText}  |  NV {curLvl} -> {nextLvl}");

                string header = $"<color={headerColor}><b>{progressText}</b></color>";
                if (!string.IsNullOrEmpty(recipe)) header += $"   <color=#D6C4B0>({recipe})</color>";

                string body = Description(info, nextParamLvl);
                opt.RichDescription = string.IsNullOrEmpty(body) ? header : $"{header}\n{body}";
                opt.FuserShortName = title;
                opt.FuserPlaque = $"<size=122%><b><color=#F4E6D5>{title}</color></b></size>\n{opt.RichDescription}";
                opt.MainIcon = info?.Icon;
                if (opt.MainIcon == null)
                {
                    try { opt.MainIcon = UI?.BtnFuserEvolution?.ImgIcon?.sprite; } catch { }
                }
                return;
            }

            if (opt.Combo.HasValue)
            {
                HeroCombo combo = opt.Combo.Value;
                HeroInfo h1 = FindHeroInfo(combo.H1);
                HeroInfo h2 = FindHeroInfo(combo.H2);
                string n1 = HeroName(combo.H1);
                string n2 = HeroName(combo.H2);
                string pairTitle = $"{n1} x {n2}";

                opt.DisplayRowTitle = $"<b>{pairTitle}</b>  <color=#5CD6FF>[FUSION]</color>";
                opt.FuserShortName = pairTitle;
                opt.MainIcon = h1?.GetIcon();
                opt.FusionSecondIcon = h2?.GetIcon();
                opt.RecipeIng1 = opt.MainIcon;
                opt.RecipeIng2 = opt.FusionSecondIcon;
                if (opt.MainIcon == null)
                {
                    try { opt.MainIcon = UI?.BtnFuserCombo?.ImgIcon?.sprite; } catch { }
                }

                string header = $"<color=#5CD6FF><b>{CoopLocalization.Text("Pair Fusion", "Fusion de paire")}</b></color>   <color=#F4C86A>{pairTitle}</color>";
                string d1 = Description(h1, 2);
                string d2 = Description(h2, 2);
                string body = $"<color=#F4C86A>{n1}:</color> {d1}\n<color=#5CD6FF>{n2}:</color> {d2}";
                opt.RichDescription = $"{header}\n{body}";
                opt.FuserPlaque = $"<size=122%><b><color=#F4E6D5>{pairTitle}</color></b></size>\n{opt.RichDescription}";
                return;
            }

            if (opt.IsFreeFuserUpgrade)
            {
                GetFissionRange(out _, out _, out string rangeLabel);
                string title = CoopLocalization.Text("Free Upgrades", "Améliorations gratuites");
                opt.DisplayRowTitle = $"<b>{title}</b>  <color=#FF9F43>[FUSER]</color>";
                opt.FuserShortName = opt.Name ?? CoopLocalization.Text($"Fission (+{rangeLabel} Lvls)", $"Fission (+{rangeLabel} Nv.)");
                try { opt.MainIcon = UI?.BtnFuserFreeUpgrades?.ImgIcon?.sprite ?? LevelUpUI.I?.BtnFuserFreeUpgrades?.ImgIcon?.sprite; } catch { }

                string header = $"<color=#FF9F43><b>{CoopLocalization.Text("Fission - Random Upgrades", "Fission - Améliorations aléatoires")}</b></color>";
                string body = CoopLocalization.Text(
                    $"Vanilla Fission: Randomly upgrades {rangeLabel} equipped non-maxed Balls or Passives (up to Lv.3), or grants bonus Gold if all equipped items are maxed.",
                    $"Fission : Améliore aléatoirement {rangeLabel} boules ou artefacts équipés (jusqu'au Nv.3), ou octroie de l'Or bonus si tout est au niveau max.");
                opt.RichDescription = $"{header}\n{body}";
                opt.FuserPlaque = $"<size=122%><b><color=#F4E6D5>{title}</color></b></size>\n{opt.RichDescription}";
                return;
            }

            if (opt.IsCoinBagFallback)
            {
                opt.DisplayRowTitle = opt.Name;
                opt.FuserShortName = opt.Name;
                string header = $"<color=#F4C86A><b>{CoopLocalization.Text("Slots Maxed - Coin Bag", "Emplacements pleins - Sac d'Or")}</b></color>";
                string body = CoopLocalization.Text(
                    $"All ball and passive slots are full. Claim +{opt.FallbackAmount} Gold instead.",
                    $"Tous les emplacements sont pleins. Récupérez +{opt.FallbackAmount} Or à la place.");
                opt.RichDescription = $"{header}\n{body}";
                opt.FuserPlaque = opt.RichDescription;
                return;
            }

            if (opt.IsHealFallback)
            {
                opt.DisplayRowTitle = opt.Name;
                opt.FuserShortName = opt.Name;
                string header = $"<color=#78E69C><b>{CoopLocalization.Text("Slots Maxed - Field Heal", "Emplacements pleins - Soin")}</b></color>";
                string body = CoopLocalization.Text(
                    $"All ball and passive slots are full. Restore +{opt.FallbackAmount} HP to this player.",
                    $"Tous les emplacements sont pleins. Restaure +{opt.FallbackAmount} PV à ce joueur.");
                opt.RichDescription = $"{header}\n{body}";
                opt.FuserPlaque = opt.RichDescription;
                return;
            }

            opt.DisplayRowTitle = opt.Name ?? CoopLocalization.Text("Continue", "Continuer");
            opt.FuserShortName = opt.DisplayRowTitle;
            opt.RichDescription = string.Empty;
            opt.FuserPlaque = opt.DisplayRowTitle;
        }

        // --- Option Pool Building ---

        private static bool HasEligibleFissionUpgrades()
        {
            if (BattleSaveData.I == null) return false;
            var heroes = BattleSaveData.I.Heroes;
            if (heroes != null)
            {
                for (int i = 0; i < heroes.Count; i++)
                {
                    HeroInst h = heroes[i];
                    if (h != null && h.GetInfo() != null && h.CanUpgrade()) return true;
                }
            }
            var passives = BattleSaveData.I.Passives;
            if (passives != null)
            {
                for (int i = 0; i < passives.Count; i++)
                {
                    PassiveInst p = passives[i];
                    if (p != null && p.GetInfo() != null && p.CanUpgrade()) return true;
                }
            }
            return false;
        }

        private static bool IsBanished(UpgradeInfo info)
        {
            if (info == null || BattleSaveData.I?.BanishedItems == null) return false;
            for (int i = 0; i < BattleSaveData.I.BanishedItems.Count; i++)
            {
                if (BattleSaveData.I.BanishedItems[i] == info) return true;
            }
            return false;
        }

        private static bool IsValidChoiceForCurrentPlayer(
            UpgradeChoice c,
            HashSet<IntPtr> seenInfos,
            HashSet<string> seenKeys)
        {
            if (c == null || c.Info == null || IsBanished(c.Info)) return false;
            if (c.Info.DLCVersion > 0 && !CoopDlcAndLevels.SafeCanAccessBallbylonDLC()) return false;
            IntPtr infoPtr = HarmonyHelper.Ptr(c.Info);
            if (infoPtr != IntPtr.Zero && seenInfos.Contains(infoPtr)) return false;

            HeroInfo hi = c.Info.TryCast<HeroInfo>();
            if (hi != null)
            {
                string key = "H:" + hi.Type + ":" + (hi.IsMerged() ? 1 : 0);
                if (seenKeys.Contains(key)) return false;
                if (UI != null && UI.Type != LevelUpType.kFuser && !hi.IsMerged())
                {
                    var heroes = BattleSaveData.I?.Heroes;
                    HeroInst existingSolo = null;
                    if (heroes != null)
                    {
                        for (int i = 0; i < heroes.Count; i++)
                        {
                            HeroInst h = heroes[i];
                            if (h != null && h.GetNumInfo() == 1 && h.Type == hi.Type)
                            {
                                existingSolo = h;
                                break;
                            }
                        }
                    }
                    int cap = BuildingMgr.I != null && BuildingMgr.I.NumBallSlots > 0 ? BuildingMgr.I.NumBallSlots : 4;
                    int owned = heroes != null ? heroes.Count : 0;
                    if (c.IsNew)
                    {
                        if (existingSolo != null || owned >= cap) return false;
                    }
                    else
                    {
                        if (existingSolo == null || !existingSolo.CanUpgrade()) return false;
                    }
                }
                if (infoPtr != IntPtr.Zero) seenInfos.Add(infoPtr);
                seenKeys.Add(key);
                return true;
            }

            PassiveInfo pi = c.Info.TryCast<PassiveInfo>();
            if (pi != null)
            {
                string key = "P:" + pi.Type + ":" + (pi.IsMerged() ? 1 : 0);
                if (seenKeys.Contains(key)) return false;
                if (UI != null && UI.Type != LevelUpType.kFuser && !pi.IsMerged())
                {
                    var passives = BattleSaveData.I?.Passives;
                    PassiveInst existingP = null;
                    if (passives != null)
                    {
                        for (int i = 0; i < passives.Count; i++)
                        {
                            PassiveInst p = passives[i];
                            if (p != null && p.Type == pi.Type)
                            {
                                existingP = p;
                                break;
                            }
                        }
                    }
                    int cap = BuildingMgr.I != null && BuildingMgr.I.NumPassiveSlots > 0 ? BuildingMgr.I.NumPassiveSlots : 4;
                    int owned = passives != null ? passives.Count : 0;
                    if (c.IsNew)
                    {
                        if (existingP != null || owned >= cap) return false;
                    }
                    else
                    {
                        if (existingP == null || !existingP.CanUpgrade()) return false;
                    }
                }
                if (infoPtr != IntPtr.Zero) seenInfos.Add(infoPtr);
                seenKeys.Add(key);
                return true;
            }

            if (infoPtr != IntPtr.Zero) seenInfos.Add(infoPtr);
            return true;
        }

        private static void Append(
            List<Option> target,
            Il2CppSystem.Collections.Generic.List<UpgradeChoice> choices,
            HashSet<IntPtr> seenInfos,
            HashSet<string> seenKeys,
            HashSet<IntPtr> excludeInfos = null,
            int maxTotal = int.MaxValue)
        {
            if (choices == null || target.Count >= maxTotal) return;
            for (int i = 0; i < choices.Count && target.Count < maxTotal; i++)
            {
                UpgradeChoice c = choices[i];
                if (c == null || c.Info == null) continue;
                IntPtr infoPtr = HarmonyHelper.Ptr(c.Info);
                if (excludeInfos != null && infoPtr != IntPtr.Zero && excludeInfos.Contains(infoPtr)) continue;
                if (IsValidChoiceForCurrentPlayer(c, seenInfos, seenKeys))
                {
                    var opt = new Option { Name = UpgradeName(c), Upgrade = c };
                    PopulateOptionViewModelInSlotContext(opt);
                    target.Add(opt);
                }
            }
        }

        private static void BuildInSlotContext(List<Option> target, int slot, HashSet<IntPtr> excludeInfos = null)
        {
            target.Clear();
            if (UI == null || BattleSaveData.I == null) return;

            var seenInfos = new HashSet<IntPtr>();
            var seenKeys = new HashSet<string>();

            if (UI.Type == LevelUpType.kFuser)
            {
                AllFuserEvos[slot].Clear();
                AllFuserCombos[slot].Clear();

                Append(AllFuserEvos[slot], UI._availMerges, seenInfos, seenKeys);

                var heroes = BattleSaveData.I.Heroes;
                if (heroes != null)
                {
                    for (int i = 0; i < heroes.Count; i++)
                    {
                        for (int j = i + 1; j < heroes.Count; j++)
                        {
                            HeroInst h1 = heroes[i];
                            HeroInst h2 = heroes[j];
                            if (h1 != null && h2 != null && h1.IsAtMaxSolo() && h2.IsAtMaxSolo() && !h1.IsBadCombo(h2))
                            {
                                var comboOpt = new Option
                                {
                                    Name = HeroName(h1.Type) + " x " + HeroName(h2.Type),
                                    Combo = new HeroCombo(i, h1.Type, j, h2.Type)
                                };
                                PopulateOptionViewModelInSlotContext(comboOpt);
                                AllFuserCombos[slot].Add(comboOpt);
                            }
                        }
                    }
                }

                // Evaluate and cache SlotHasFreeUpgrades[slot] while this player's lists are active on UI!
                GetFissionRange(out _, out _, out string rangeLabel);
                bool hasFree = HasEligibleFissionUpgrades() || UI.HasFreeUpgrades();
                SlotHasFreeUpgrades[slot] = hasFree;

                var fissOpt = new Option
                {
                    Name = hasFree
                        ? CoopLocalization.Text($"Fission (+{rangeLabel} Lvls)", $"Fission (+{rangeLabel} Nv.)")
                        : CoopLocalization.Text("Fission (Bonus Gold)", "Fission (Or bonus)"),
                    IsFreeFuserUpgrade = true
                };
                PopulateOptionViewModelInSlotContext(fissOpt);
                FissionOptions[slot] = fissOpt;

                InFuserCategoryMenu[slot] = true;
                FuserCategoryIndex[slot] = AllFuserEvos[slot].Count > 0
                    ? (int)FuserCategory.Evolution
                    : (AllFuserCombos[slot].Count > 0 ? (int)FuserCategory.Fusion : (int)FuserCategory.Fission);
                ActiveFuserCategory[slot] = FuserCategory.Fission;
                target.Add(fissOpt);
                return;
            }

            if (UI._choices != null && UI._choices.Count > 0)
            {
                Append(target, UI._choices, seenInfos, seenKeys, excludeInfos);
            }

            int desiredCount = DesiredChoiceCount();
            if (target.Count < desiredCount)
            {
                var pool = new List<Option>(16);
                if (UI.Type != LevelUpType.kBonusPassive)
                {
                    Append(pool, UI._availHeroUpgrades, seenInfos, seenKeys, excludeInfos);
                    Append(pool, UI._availNewHeroes, seenInfos, seenKeys, excludeInfos);
                }
                if (UI.Type != LevelUpType.kBonusBall)
                {
                    Append(pool, UI._availPassiveUpgrades, seenInfos, seenKeys, excludeInfos);
                    Append(pool, UI._availNewPassives, seenInfos, seenKeys, excludeInfos);
                    Append(pool, UI._availPetUpgrades, seenInfos, seenKeys, excludeInfos);
                    Append(pool, UI._availNewPetUpgrades, seenInfos, seenKeys, excludeInfos);
                }
                if (pool.Count == 0 && excludeInfos != null && excludeInfos.Count > 0)
                {
                    if (UI._choices != null) Append(pool, UI._choices, seenInfos, seenKeys, null);
                    if (UI.Type != LevelUpType.kBonusPassive)
                    {
                        Append(pool, UI._availHeroUpgrades, seenInfos, seenKeys, null);
                        Append(pool, UI._availNewHeroes, seenInfos, seenKeys, null);
                    }
                    if (UI.Type != LevelUpType.kBonusBall)
                    {
                        Append(pool, UI._availPassiveUpgrades, seenInfos, seenKeys, null);
                        Append(pool, UI._availNewPassives, seenInfos, seenKeys, null);
                        Append(pool, UI._availPetUpgrades, seenInfos, seenKeys, null);
                        Append(pool, UI._availNewPetUpgrades, seenInfos, seenKeys, null);
                    }
                }

                if (pool.Count > 0)
                {
                    int mixedSeed = ComputeSlotLevelSeed(BattleSaveData.I.GetCurLvlSeed(), BattleSaveData.I.UpgradeLvl, slot, SlotRerolls[slot], UI.Type);
                    var rng = new System.Random(mixedSeed);
                    for (int n = pool.Count - 1; n > 0; n--)
                    {
                        int idx = rng.Next(n + 1);
                        Option tmp = pool[n];
                        pool[n] = pool[idx];
                        pool[idx] = tmp;
                    }
                    for (int i = 0; i < pool.Count && target.Count < desiredCount; i++)
                    {
                        target.Add(pool[i]);
                    }
                }
            }

            if (target.Count == 0)
            {
                int goldAmt = Math.Max(15, 25 + (BattleSaveData.I != null ? BattleSaveData.I.UpgradeLvl * 3 : 0));
                int healAmt = Mathf.Max(15, Mathf.CeilToInt(CoopVitals.Life.Maximum[slot] * 0.30f));

                var coinOpt = new Option
                {
                    Name = CoopLocalization.Text($"Coin Bag (+{goldAmt}g)", $"Sac d'Or (+{goldAmt}g)"),
                    IsCoinBagFallback = true,
                    FallbackAmount = goldAmt
                };
                PopulateOptionViewModelInSlotContext(coinOpt);
                target.Add(coinOpt);

                var healOpt = new Option
                {
                    Name = CoopLocalization.Text($"Heal (+{healAmt} HP)", $"Soin (+{healAmt} PV)"),
                    IsHealFallback = true,
                    FallbackAmount = healAmt
                };
                PopulateOptionViewModelInSlotContext(healOpt);
                target.Add(healOpt);
            }
        }

        private static HashSet<IntPtr> CaptureOptionInfoPointers(int slot)
        {
            var set = new HashSet<IntPtr>();
            for (int i = 0; i < Options[slot].Count; i++)
            {
                IntPtr ptr = HarmonyHelper.Ptr(Options[slot][i]?.Upgrade?.Info);
                if (ptr != IntPtr.Zero) set.Add(ptr);
            }
            return set;
        }

        private static bool SlotContainsInfo(int slot, UpgradeInfo info)
        {
            IntPtr targetPtr = HarmonyHelper.Ptr(info);
            if (targetPtr == IntPtr.Zero) return false;
            for (int i = 0; i < Options[slot].Count; i++)
            {
                if (HarmonyHelper.Ptr(Options[slot][i]?.Upgrade?.Info) == targetPtr) return true;
            }
            return false;
        }

        private static void NormalizeSlotNoOp(int slot)
        {
            if (CoopVitals.Down(slot))
            {
                Options[slot].Clear();
                var downOpt = new Option
                {
                    Name = CoopLocalization.Text("Downed - Waiting for partner", "KO - En attente du partenaire")
                };
                PopulateOptionViewModelInSlotContext(downOpt);
                Options[slot].Add(downOpt);
            }
            if (Options[slot].Count == 1 && IsNoOpOption(Options[slot][0]))
            {
                Gate.Confirm(slot, connected: true);
            }
        }

        private static int DesiredChoiceCount()
        {
            int uiChoices = UI != null ? UI._numUpgradeChoices : 0;
            int bldChoices = BuildingMgr.I != null ? BuildingMgr.I.NumLvlUpChoices : 0;
            return Math.Max(3, Math.Max(uiChoices, bldChoices));
        }

        private static int ComputeSlotLevelSeed(int baseSeed, int upgradeLvl, int slot, int slotRerolls, LevelUpType levelUpType)
        {
            unchecked
            {
                uint h = (uint)baseSeed;
                h ^= (uint)(upgradeLvl + 1) * 2654435761u;
                h ^= (uint)(slot + 1) * 2246822519u;
                h ^= (uint)(slotRerolls + 1) * 3266489917u;
                h ^= (uint)((int)levelUpType + 17) * 668265263u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                h *= 3266489917u;
                h ^= h >> 16;
                int result = (int)(h & 0x7FFFFFFFu);
                return result == 0 ? 1 : result;
            }
        }

        // --- Native List Backup & Safe ValueType Add ---

        private static unsafe void AddUpgradeChoiceSafe(Il2CppSystem.Collections.Generic.List<UpgradeChoice> list, UpgradeChoice choice)
        {
            if (list == null || list.Pointer == IntPtr.Zero || choice == null || choice.Pointer == IntPtr.Zero) return;
            if (_nativeUpgradeChoiceListAddPtr == IntPtr.Zero)
            {
                Type listType = typeof(Il2CppSystem.Collections.Generic.List<UpgradeChoice>);
                RuntimeHelpers.RunClassConstructor(listType.TypeHandle);
                FieldInfo addPtrField = listType.GetField(
                    "NativeMethodInfoPtr_Add_Public_Virtual_Final_New_Void_T_0",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    ?? listType.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                        .FirstOrDefault(f => f.FieldType == typeof(IntPtr)
                            && f.Name.StartsWith("NativeMethodInfoPtr_Add_", StringComparison.Ordinal)
                            && !f.Name.Contains("WithResize", StringComparison.Ordinal));
                if (addPtrField != null) _nativeUpgradeChoiceListAddPtr = (IntPtr)addPtrField.GetValue(null);
            }
            if (_nativeUpgradeChoiceListAddPtr == IntPtr.Zero) return;

            IntPtr boxedPtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(choice);
            IntPtr unboxedPtr = IL2CPP.il2cpp_object_unbox(boxedPtr);
            void** args = stackalloc void*[1];
            args[0] = (void*)unboxedPtr;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(_nativeUpgradeChoiceListAddPtr, IL2CPP.Il2CppObjectBaseToPtrNotNull(list), args, ref exc);
            Il2CppException.RaiseExceptionIfNecessary(exc);
            GC.KeepAlive(choice);
        }

        private static unsafe bool InvokeNativeAddRange(object targetList, ListOps ops, object sourceList)
        {
            if (ops == null || ops.NativeAddRangePtr == IntPtr.Zero) return false;
            if (targetList is not Il2CppObjectBase targetObj || sourceList is not Il2CppObjectBase sourceObj) return false;
            IntPtr targetPtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(targetObj);
            IntPtr sourcePtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(sourceObj);
            if (targetPtr == IntPtr.Zero || sourcePtr == IntPtr.Zero) return false;
            void** args = stackalloc void*[1];
            args[0] = (void*)sourcePtr;
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(ops.NativeAddRangePtr, targetPtr, args, ref exc);
            Il2CppException.RaiseExceptionIfNecessary(exc);
            GC.KeepAlive(sourceObj);
            GC.KeepAlive(targetObj);
            return true;
        }

        private static ListOps ResolveListOps(Type listType)
        {
            if (listType == null) return null;
            if (CachedListOps.TryGetValue(listType, out ListOps cached)) return cached;
            try { RuntimeHelpers.RunClassConstructor(listType.TypeHandle); } catch { }

            FieldInfo addRangeField = listType.GetField(
                "NativeMethodInfoPtr_AddRange_Public_Void_IEnumerable_1_T_0",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?? listType.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(f => f.FieldType == typeof(IntPtr)
                        && f.Name.StartsWith("NativeMethodInfoPtr_AddRange_", StringComparison.Ordinal));

            var ops = new ListOps
            {
                CountProp = listType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public),
                GetItemMethod = listType.GetMethod("get_Item", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int) }, null),
                ClearMethod = listType.GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null),
                AddMethod = listType.GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1),
                NativeAddRangePtr = addRangeField != null ? (IntPtr)addRangeField.GetValue(null) : IntPtr.Zero
            };
            if (ops.CountProp == null || ops.ClearMethod == null || (ops.NativeAddRangePtr == IntPtr.Zero && (ops.GetItemMethod == null || ops.AddMethod == null)))
            {
                ops = null;
            }
            CachedListOps[listType] = ops;
            return ops;
        }

        private static ChoiceListBackup CaptureChoiceListBackup(object listObj)
        {
            var snap = new ChoiceListBackup { ListRef = listObj };
            if (listObj == null) return snap;
            Type listType = listObj.GetType();
            ListOps ops = ResolveListOps(listType);
            if (ops == null) return snap;
            int count = (int)ops.CountProp.GetValue(listObj);
            if (count <= 0)
            {
                snap.Elements = Array.Empty<object>();
                return snap;
            }
            if (ops.NativeAddRangePtr != IntPtr.Zero)
            {
                object clone = Activator.CreateInstance(listType);
                if (clone != null && InvokeNativeAddRange(clone, ops, listObj))
                {
                    snap.NativeClone = clone;
                    return snap;
                }
            }
            object[] items = new object[count];
            object[] idxArgs = new object[1];
            for (int i = 0; i < count; i++)
            {
                idxArgs[0] = i;
                items[i] = ops.GetItemMethod.Invoke(listObj, idxArgs);
            }
            snap.Elements = items;
            return snap;
        }

        private static void RestoreChoiceListBackup(PropertyInfo prop, ChoiceListBackup snap)
        {
            if (prop == null || snap == null) return;
            object listObj = snap.ListRef;
            if (listObj != null)
            {
                ListOps ops = ResolveListOps(listObj.GetType());
                if (ops != null)
                {
                    ops.ClearMethod.Invoke(listObj, null);
                    if (snap.NativeClone != null && InvokeNativeAddRange(listObj, ops, snap.NativeClone))
                    {
                        // Restored natively without Il2CppSystem.ValueType List<T>.Add boxing corruption.
                    }
                    else if (snap.Elements != null && ops.AddMethod != null)
                    {
                        object[] addArgs = new object[1];
                        for (int i = 0; i < snap.Elements.Length; i++)
                        {
                            addArgs[0] = snap.Elements[i];
                            ops.AddMethod.Invoke(listObj, addArgs);
                        }
                    }
                }
            }
            prop.SetValue(UI, listObj);
        }

        private static void BuildSlot(int slot, HashSet<IntPtr> excludeInfos = null, bool forcePopulate = false)
        {
            if (UI == null) return;

            if (BattleSaveData.I != null && BattleSaveData.I.Seed == 0)
            {
                BattleSaveData.I.Seed = unchecked((int)((uint)Environment.TickCount * 1664525u + 1013904223u));
            }

            if (slot == 0)
            {
                CoopCharacterRuntime.ForPrimary(() =>
                {
                    bool needsPopulate = forcePopulate || UI._choices == null || UI._choices.Count == 0 || (excludeInfos != null && excludeInfos.Count > 0);
                    if (needsPopulate && BattleSaveData.I != null)
                    {
                        int savedSeed = BattleSaveData.I.Seed;
                        Il2CppSystem.Random savedRnd = UI._rnd;
                        int p1Seed = ComputeSlotLevelSeed(savedSeed, BattleSaveData.I.UpgradeLvl, 0, SlotRerolls[0], UI.Type);
                        try
                        {
                            BattleSaveData.I.Seed = p1Seed;
                            UI._rnd = new Il2CppSystem.Random(p1Seed);
                            if (UI._prevChoices != null) UI._prevChoices.Clear();
                            if (excludeInfos != null && excludeInfos.Count > 0 && UI._prevChoices != null)
                            {
                                for (int k = 0; k < Options[0].Count; k++)
                                {
                                    UpgradeChoice prevOptChoice = Options[0][k]?.Upgrade;
                                    if (prevOptChoice != null && prevOptChoice.Pointer != IntPtr.Zero
                                        && excludeInfos.Contains(HarmonyHelper.Ptr(prevOptChoice.Info)))
                                    {
                                        AddUpgradeChoiceSafe(UI._prevChoices, prevOptChoice);
                                    }
                                }
                            }
                            UI.PopulateUpgrades();
                        }
                        finally
                        {
                            BattleSaveData.I.Seed = savedSeed;
                            UI._rnd = savedRnd;
                        }
                    }
                    BuildInSlotContext(Options[0], 0, excludeInfos);
                });
                NormalizeSlotNoOp(0);
                return;
            }

            // Slot 1: backup P1's choice lists AND scalar LevelUpUI fields before running PopulateUpgrades in P2 context.
            var backup = new ChoiceListBackup[CachedChoiceLists.Count];
            for (int i = 0; i < backup.Length; i++)
            {
                backup[i] = CaptureChoiceListBackup(CachedChoiceLists[i].GetValue(UI));
            }
            int p1SavedSeed = BattleSaveData.I != null ? BattleSaveData.I.Seed : 0;
            Il2CppSystem.Random p1SavedRnd = UI._rnd;
            int p1SavedNumChoices = UI._numUpgradeChoices;
            int p1SavedRerollCost = UI._rerollCost;
            bool p1SavedFuserRevealed = UI._isFuserRevealed;

            try
            {
                CoopCharacterRuntime.ForSecond(() =>
                {
                    if (BattleSaveData.I != null)
                    {
                        int p2Seed = ComputeSlotLevelSeed(p1SavedSeed, BattleSaveData.I.UpgradeLvl, 1, SlotRerolls[1], UI.Type);
                        BattleSaveData.I.Seed = p2Seed;
                        UI._rnd = new Il2CppSystem.Random(p2Seed);
                    }
                    if (UI._prevChoices == null)
                    {
                        UI._prevChoices = new Il2CppSystem.Collections.Generic.List<UpgradeChoice>();
                    }
                    else
                    {
                        UI._prevChoices.Clear();
                    }
                    if (excludeInfos != null && excludeInfos.Count > 0)
                    {
                        for (int k = 0; k < Options[1].Count; k++)
                        {
                            UpgradeChoice prevOptChoice = Options[1][k]?.Upgrade;
                            if (prevOptChoice != null && prevOptChoice.Pointer != IntPtr.Zero
                                && excludeInfos.Contains(HarmonyHelper.Ptr(prevOptChoice.Info)))
                            {
                                AddUpgradeChoiceSafe(UI._prevChoices, prevOptChoice);
                            }
                        }
                    }
                    UI.PopulateUpgrades();
                    BuildInSlotContext(Options[1], 1, excludeInfos);
                });
            }
            finally
            {
                if (BattleSaveData.I != null) BattleSaveData.I.Seed = p1SavedSeed;
                UI._rnd = p1SavedRnd;
                UI._numUpgradeChoices = p1SavedNumChoices;
                UI._rerollCost = p1SavedRerollCost;
                UI._isFuserRevealed = p1SavedFuserRevealed;
                for (int i = 0; i < backup.Length; i++)
                {
                    RestoreChoiceListBackup(CachedChoiceLists[i], backup[i]);
                }
            }

            NormalizeSlotNoOp(1);
        }

        private static void Prepare()
        {
            if (UI == null) return;

            try { UI.RefreshRerolls(); } catch { }

            Gate.Reset();
            BuildSlot(0, forcePopulate: true);
            BuildSlot(1);
            _started = true;

            if (CoopBalance.AutoPickMaxed && UI.Type != LevelUpType.kFuser)
            {
                var autoSummary = new StringBuilder();
                for (int slot = 0; slot < 2; slot++)
                {
                    if (!SlotIsMaxedFallbackOnly(slot)) continue;
                    bool needsHeal = CoopVitals.Life.Health[slot] < CoopVitals.Life.Maximum[slot] * 0.75f;
                    int chosenIdx = 0;
                    for (int i = 0; i < Options[slot].Count; i++)
                    {
                        Option cand = Options[slot][i];
                        if ((needsHeal && cand.IsHealFallback) || (!needsHeal && cand.IsCoinBagFallback))
                        {
                            chosenIdx = i;
                            break;
                        }
                    }
                    Gate.Index[slot] = chosenIdx;
                    Gate.Ready[slot] = true;
                    Option picked = Options[slot][chosenIdx];
                    if (autoSummary.Length > 0) autoSummary.Append("  |  ");
                    autoSummary.Append($"P{slot + 1}: {(picked.IsHealFallback ? "+" + picked.FallbackAmount + " HP" : "+" + picked.FallbackAmount + "g")}");
                }
                if (autoSummary.Length > 0)
                {
                    CoopHudAndResults.ShowToast(CoopLocalization.Text("Maxed Auto-Pick: ", "Auto (Max) : ") + autoSummary, 3.2f);
                }
                if (Gate.Ready[0] && Gate.Ready[1])
                {
                    Commit();
                }
            }
        }

        // --- Deterministic Frame Tick (Update + Render) ---

        public static void Tick()
        {
            if (!_pending || UI == null)
            {
                return;
            }
            if (!HarmonyHelper.IsInGameScene())
            {
                AbortForSceneChange();
                return;
            }

            if (Time.unscaledTime >= _armedAt && Application.isFocused)
            {
                if (!_started)
                {
                    Prepare();
                    if (!_pending) return;
                }
                UpdateInput();
            }

            if (_pending && UI != null)
            {
                Render();
            }
        }

        private static void UpdateInput()
        {
            if (_showingFissionSummary)
            {
                bool dismiss = OverlayInput.ConsumeActionClicked("pause_0")
                    || UnityEngine.Input.GetKeyDown(KeyCode.Return)
                    || UnityEngine.Input.GetKeyDown(KeyCode.Space)
                    || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
                    || UnityEngine.Input.GetKeyDown(KeyCode.Escape);
                for (int p = 0; p < 2; p++)
                {
                    IGamepadTemplate pad = GamepadRouter.GetPad(p);
                    if (pad != null && (GamepadRouter.ButtonJustPressed(pad.a) || GamepadRouter.ButtonJustPressed(pad.b)))
                    {
                        dismiss = true;
                    }
                }
                if (dismiss && Time.unscaledTime >= _fissionSummaryArmedAt)
                {
                    ModalOverlayState.ConsumeCancelThisFrame();
                    CloseDecisionOverlayAfterCommit();
                }
                return;
            }

            bool isFuser = UI != null && UI.Type == LevelUpType.kFuser;
            bool canBanish = CanBanish();
            int rerollRequestedSlot = -1;

            for (int slot = 0; slot < 2; slot++)
            {
                if (isFuser && InFuserCategoryMenu[slot] && !CoopVitals.Down(slot))
                {
                    ReadDecisionInput(slot, out float catMoveX, out bool catConfirm, out bool catCancel, out _, out _);
                    if (OverlayInput.TryGetHoveredOptionIndex(slot, out int hCat) && !Gate.Ready[slot] && hCat >= 0 && hCat < 3)
                    {
                        FuserCategoryIndex[slot] = hCat;
                    }
                    if (OverlayInput.TryGetClickedOptionIndex(slot, out int cCat) && cCat >= 0 && cCat < 3)
                    {
                        if (Gate.Ready[slot]) Gate.Cancel(slot);
                        FuserCategoryIndex[slot] = cCat;
                        catConfirm = true;
                    }
                    if (OverlayInput.ConsumeActionClicked("confirm_" + slot))
                    {
                        if (Gate.Ready[slot]) Gate.Cancel(slot);
                        else catConfirm = true;
                    }
                    if (Mathf.Abs(catMoveX) < 0.45f) NextInput[slot] = 0f;
                    if (!Gate.Ready[slot] && Mathf.Abs(catMoveX) > 0.55f && Time.unscaledTime >= NextInput[slot])
                    {
                        int dir = catMoveX > 0f ? 1 : -1;
                        FuserCategoryIndex[slot] = (FuserCategoryIndex[slot] + dir + 3) % 3;
                        NextInput[slot] = Time.unscaledTime + 0.2f;
                    }
                    if (catCancel && Gate.Ready[slot])
                    {
                        ModalOverlayState.ConsumeCancelThisFrame();
                        Gate.Cancel(slot);
                    }
                    if (catConfirm && !Gate.Ready[slot])
                    {
                        int chosenCat = Mathf.Clamp(FuserCategoryIndex[slot], 0, 2);
                        if (chosenCat == (int)FuserCategory.Evolution)
                        {
                            if (AllFuserEvos[slot].Count > 0)
                            {
                                InFuserCategoryMenu[slot] = false;
                                ActiveFuserCategory[slot] = FuserCategory.Evolution;
                                Options[slot].Clear();
                                Options[slot].AddRange(AllFuserEvos[slot]);
                                Gate.Index[slot] = 0;
                                NextInput[slot] = Time.unscaledTime + 0.18f;
                            }
                        }
                        else if (chosenCat == (int)FuserCategory.Fusion)
                        {
                            if (AllFuserCombos[slot].Count > 0)
                            {
                                InFuserCategoryMenu[slot] = false;
                                ActiveFuserCategory[slot] = FuserCategory.Fusion;
                                Options[slot].Clear();
                                Options[slot].AddRange(AllFuserCombos[slot]);
                                Gate.Index[slot] = 0;
                                NextInput[slot] = Time.unscaledTime + 0.18f;
                            }
                        }
                        else
                        {
                            // Keep InFuserCategoryMenu[slot] = true when Fission is confirmed so Undo takes 1 click instead of 2!
                            ActiveFuserCategory[slot] = FuserCategory.Fission;
                            Options[slot].Clear();
                            if (FissionOptions[slot] != null) Options[slot].Add(FissionOptions[slot]);
                            Gate.Index[slot] = 0;
                            Gate.Confirm(slot, connected: true);
                            GamepadRouter.VibratePad(slot, 0.15f, 0.08f);
                        }
                    }
                    continue;
                }

                if (Options[slot].Count == 1 && IsNoOpOption(Options[slot][0]))
                {
                    continue;
                }

                ReadDecisionInput(slot, out float moveX, out bool confirm, out bool cancel, out bool reroll, out bool banishToggle);

                if (OverlayInput.TryGetHoveredOptionIndex(slot, out int hOpt)
                    && !Gate.Ready[slot] && hOpt >= 0 && hOpt < Options[slot].Count)
                {
                    Gate.Index[slot] = hOpt;
                }
                if (OverlayInput.TryGetClickedOptionIndex(slot, out int cOpt)
                    && cOpt >= 0 && cOpt < Options[slot].Count)
                {
                    if (Gate.Ready[slot] && Gate.Index[slot] == cOpt)
                    {
                        Gate.Cancel(slot);
                    }
                    else
                    {
                        if (Gate.Ready[slot]) Gate.Cancel(slot);
                        Gate.Index[slot] = cOpt;
                        confirm = true;
                    }
                }
                if (OverlayInput.ConsumeActionClicked("confirm_" + slot))
                {
                    if (Gate.Ready[slot]) Gate.Cancel(slot);
                    else confirm = true;
                }
                if (OverlayInput.ConsumeActionClicked("reroll_" + slot)) reroll = true;
                if (OverlayInput.ConsumeActionClicked("banish_" + slot))
                {
                    if (isFuser && !InFuserCategoryMenu[slot]) cancel = true;
                    else banishToggle = true;
                }

                if (reroll && CanReroll(slot) && rerollRequestedSlot < 0)
                {
                    rerollRequestedSlot = slot;
                }
                if (canBanish && banishToggle && !Gate.Ready[slot])
                {
                    Banishing[slot] = !Banishing[slot];
                }

                if (Mathf.Abs(moveX) < 0.45f) NextInput[slot] = 0f;
                if (Mathf.Abs(moveX) > 0.55f && Time.unscaledTime >= NextInput[slot])
                {
                    Gate.Move(slot, moveX > 0f ? 1 : -1, Options[slot].Count);
                    NextInput[slot] = Time.unscaledTime + 0.2f;
                }

                if (cancel)
                {
                    ModalOverlayState.ConsumeCancelThisFrame();
                    if (Banishing[slot])
                    {
                        Banishing[slot] = false;
                    }
                    else if (Gate.Ready[slot])
                    {
                        Gate.Cancel(slot);
                        if (isFuser && ActiveFuserCategory[slot] == FuserCategory.Fission)
                        {
                            InFuserCategoryMenu[slot] = true;
                        }
                    }
                    else if (isFuser && !InFuserCategoryMenu[slot])
                    {
                        InFuserCategoryMenu[slot] = true;
                        Gate.Index[slot] = 0;
                        NextInput[slot] = Time.unscaledTime + 0.15f;
                    }
                }

                if (confirm)
                {
                    if (Banishing[slot] && canBanish)
                    {
                        ExecuteBanish(slot);
                    }
                    else
                    {
                        Gate.Confirm(slot, connected: true);
                        GamepadRouter.VibratePad(slot, 0.15f, 0.08f);
                    }
                }
            }

            if (rerollRequestedSlot >= 0 && CanReroll(rerollRequestedSlot))
            {
                TriggerReroll(rerollRequestedSlot);
                return;
            }

            if (Gate.Complete)
            {
                Commit();
            }
        }

        private static void ReadDecisionInput(int slot, out float moveAxis, out bool confirm, out bool cancel, out bool reroll, out bool banishToggle)
        {
            moveAxis = 0f;
            confirm = false;
            cancel = false;
            reroll = false;
            banishToggle = false;

            IGamepadTemplate pad = GamepadRouter.GetPad(slot);
            if (pad != null)
            {
                Vector2 stick = GamepadRouter.MenuStick(pad);
                moveAxis = Mathf.Abs(stick.y) >= Mathf.Abs(stick.x) ? -stick.y : stick.x;
                confirm = GamepadRouter.ButtonJustPressed(pad.a);
                cancel = GamepadRouter.ButtonJustPressed(pad.b);
                reroll = GamepadRouter.ButtonJustPressed(pad.x) || GamepadRouter.ButtonJustPressed(pad.y);
                banishToggle = GamepadRouter.ButtonJustPressed(pad.leftBumper) || GamepadRouter.ButtonJustPressed(pad.rightBumper);
            }

            if (slot == 0)
            {
                float kx = (UnityEngine.Input.GetKey(KeyCode.D) ? 1f : 0f) - (UnityEngine.Input.GetKey(KeyCode.A) ? 1f : 0f);
                float ky = (UnityEngine.Input.GetKey(KeyCode.S) ? 1f : 0f) - (UnityEngine.Input.GetKey(KeyCode.W) ? 1f : 0f);
                float kbAxis = Mathf.Abs(ky) >= Mathf.Abs(kx) ? ky : kx;
                if (Mathf.Abs(kbAxis) > Mathf.Abs(moveAxis)) moveAxis = kbAxis;
                confirm |= UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.Space);
                cancel |= UnityEngine.Input.GetKeyDown(KeyCode.Escape);
                reroll |= UnityEngine.Input.GetKeyDown(KeyCode.R);
                banishToggle |= UnityEngine.Input.GetKeyDown(KeyCode.Tab);
            }
            else if (!GamepadRouter.UsesGamepadForSlot(1) || pad == null)
            {
                float kx = ((UnityEngine.Input.GetKey(ModConfig.KeyMoveRight) || UnityEngine.Input.GetKey(KeyCode.RightArrow)) ? 1f : 0f)
                         - ((UnityEngine.Input.GetKey(ModConfig.KeyMoveLeft) || UnityEngine.Input.GetKey(KeyCode.LeftArrow)) ? 1f : 0f);
                float ky = ((UnityEngine.Input.GetKey(ModConfig.KeyMoveDown) || UnityEngine.Input.GetKey(KeyCode.DownArrow)) ? 1f : 0f)
                         - ((UnityEngine.Input.GetKey(ModConfig.KeyMoveUp) || UnityEngine.Input.GetKey(KeyCode.UpArrow)) ? 1f : 0f);
                float kbAxis = Mathf.Abs(ky) >= Mathf.Abs(kx) ? ky : kx;
                if (Mathf.Abs(kbAxis) > Mathf.Abs(moveAxis)) moveAxis = kbAxis;
                // Includes TKL keyboard fallbacks (Slash / RightControl / KeypadEnter)
                confirm |= UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter) || UnityEngine.Input.GetKeyDown(KeyCode.RightControl) || UnityEngine.Input.GetKeyDown(KeyCode.Slash);
                cancel |= UnityEngine.Input.GetKeyDown(KeyCode.Backspace);
                reroll |= UnityEngine.Input.GetKeyDown(KeyCode.RightShift);
                banishToggle |= UnityEngine.Input.GetKeyDown(KeyCode.Backslash);
            }
        }

        private static void ExecuteBanish(int slot)
        {
            if (Gate.Index[slot] < 0 || Gate.Index[slot] >= Options[slot].Count)
            {
                Banishing[slot] = false;
                return;
            }
            Option selected = Options[slot][Gate.Index[slot]];
            if (selected?.Upgrade?.Info == null || BattleSaveData.I == null || BattleSaveData.I.NumBanishes <= 0)
            {
                Banishing[slot] = false;
                return;
            }

            UpgradeInfo banishedInfo = selected.Upgrade.Info;
            BattleSaveData.I.NumBanishes--;
            BattleSaveData.I.BanishedItems.Add(banishedInfo);
            Banishing[slot] = false;

            // ResetSlot BEFORE BuildSlot so NormalizeSlotNoOp's auto-confirm on 1-item NoOp is never overwritten!
            Gate.ResetSlot(slot);
            BuildSlot(slot, forcePopulate: true);

            int other = 1 - slot;
            if (SlotContainsInfo(other, banishedInfo))
            {
                Banishing[other] = false;
                Gate.ResetSlot(other);
                BuildSlot(other, forcePopulate: true);
            }
        }

        /// <summary>
        /// Unified synchronous reroll for both P1 and P2: deducts gold via PickupMgr.I.AddGold, updates counters,
        /// and immediately rebuilds choices via PopulateUpgrades (eliminating _AnimateReroll race and 350ms P2 freeze).
        /// </summary>
        private static void TriggerReroll(int slot)
        {
            if (UI == null || BattleSaveData.I == null || !CanReroll(slot))
            {
                return;
            }

            if (SlotFreeRerolls[slot] > 0)
            {
                SlotFreeRerolls[slot]--;
            }
            else
            {
                int cost = SlotRerollCost(slot);
                if (cost > 0)
                {
                    if (PickupMgr.I != null)
                    {
                        PickupMgr.I.AddGold(-cost);
                    }
                    else
                    {
                        BattleSaveData.I.NumResources?.AddResource(ResourceType.kGold, -cost);
                    }
                }
                SlotRerollsInRow[slot]++;
            }

            BattleSaveData.I.NumLvlUpRerolls++;
            SlotRerolls[slot]++;

            // Keep BattleSaveData synced to P1's free reroll state and update _lastObservedBattleFreeRerolls on decrement too!
            BattleSaveData.I.NumFreeRerolls = SlotFreeRerolls[0];
            BattleSaveData.I.NumRerollsInARow = SlotRerollsInRow[0];
            _lastObservedBattleFreeRerolls = BattleSaveData.I.NumFreeRerolls;
            try { UI.RefreshRerolls(); } catch { }

            HashSet<IntPtr> exclude = CaptureOptionInfoPointers(slot);
            Banishing[slot] = false;
            Gate.ResetSlot(slot);
            BuildSlot(slot, exclude, forcePopulate: true);
            GamepadRouter.VibratePad(slot, 0.2f, 0.1f);
        }

        // --- Fission & Apply ---

        private static void GetFissionRange(out int minFiss, out int maxFiss, out string rangeLabel)
        {
            minFiss = BuildingMgr.I != null && BuildingMgr.I.MinFuserUpgrades > 0 ? BuildingMgr.I.MinFuserUpgrades : 1;
            maxFiss = BuildingMgr.I != null && BuildingMgr.I.MaxFissionUpgrades >= minFiss ? BuildingMgr.I.MaxFissionUpgrades : Math.Max(minFiss, 5);
            rangeLabel = minFiss == maxFiss ? minFiss.ToString() : $"{minFiss}-{maxFiss}";
        }

        private static void ExecuteFissionForCurrentPlayer(int slot)
        {
            LastFissionGrants[slot].Clear();
            if (BattleSaveData.I == null || UpgradeMgr.I == null) return;

            GetFissionRange(out int minFiss, out int maxFiss, out _);
            int rollTarget = UnityEngine.Random.Range(minFiss, maxFiss + 1);
            int applied = 0;
            bool upgradedHero = false;

            for (int step = 0; step < rollTarget; step++)
            {
                var candidates = new List<(UpgradeChoice choice, Sprite icon, string name, int curLvl)>();
                var heroes = BattleSaveData.I.Heroes;
                if (heroes != null)
                {
                    for (int i = 0; i < heroes.Count; i++)
                    {
                        HeroInst h = heroes[i];
                        HeroInfo hi = h?.GetInfo();
                        if (h != null && hi != null && h.CanUpgrade())
                        {
                            candidates.Add((new UpgradeChoice(UpgradeType.kHero, i, hi, false, -1), hi.Icon, SafeUpgradeName(hi), h.Lvl));
                        }
                    }
                }
                var passives = BattleSaveData.I.Passives;
                if (passives != null)
                {
                    for (int i = 0; i < passives.Count; i++)
                    {
                        PassiveInst p = passives[i];
                        PassiveInfo pi = p?.GetInfo();
                        if (p != null && pi != null && p.CanUpgrade())
                        {
                            candidates.Add((new UpgradeChoice(UpgradeType.kPassive, i, pi, false, -1), pi.Icon, SafeUpgradeName(pi), p.Lvl));
                        }
                    }
                }

                if (candidates.Count == 0) break;

                var pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                int beforeLvl = pick.curLvl;
                UpgradeMgr.I.ApplyUpgrade(pick.choice);
                int newLvl = beforeLvl;
                try
                {
                    if (pick.choice.Type == UpgradeType.kHero && heroes != null && pick.choice.EquipmentIdx >= 0 && pick.choice.EquipmentIdx < heroes.Count)
                    {
                        HeroInst targetHero = heroes[pick.choice.EquipmentIdx];
                        if (targetHero.Lvl <= beforeLvl && targetHero.CanUpgrade())
                        {
                            targetHero.Lvl = beforeLvl + 1;
                            UpgradeMgr.I.CalculateStats();
                        }
                        newLvl = targetHero.Lvl;
                        upgradedHero = true;
                    }
                    else if (pick.choice.Type == UpgradeType.kPassive && passives != null && pick.choice.EquipmentIdx >= 0 && pick.choice.EquipmentIdx < passives.Count)
                    {
                        PassiveInst targetPassive = passives[pick.choice.EquipmentIdx];
                        if (targetPassive.Lvl <= beforeLvl && targetPassive.CanUpgrade())
                        {
                            targetPassive.Lvl = beforeLvl + 1;
                            UpgradeMgr.I.CalculateStats();
                        }
                        newLvl = targetPassive.Lvl;
                    }
                }
                catch { }

                if (newLvl > beforeLvl)
                {
                    LastFissionGrants[slot].Add(new FissionGrant
                    {
                        Icon = pick.icon,
                        Name = pick.name,
                        OldLvl = beforeLvl + 1,
                        NewLvl = newLvl + 1
                    });
                    applied++;
                }
            }

            if (upgradedHero)
            {
                try { BallMgr.I?.OnHeroesChanged(); } catch { }
            }

            BattleSaveData.I.NumFissionsDone++;
            BattleSaveData.I.NumFissionUpgradesReceived += applied;
            BattleSaveData.I.LastFissionCount = applied;

            if (applied <= 0)
            {
                int minG = LevelUpUI.kMinFuserGold > 0 ? LevelUpUI.kMinFuserGold : 100;
                int maxG = LevelUpUI.kMaxFuserGold >= minG ? LevelUpUI.kMaxFuserGold : 300;
                int gold = UnityEngine.Random.Range(minG, maxG + 1);
                if (PickupMgr.I != null) PickupMgr.I.AddGold(gold);
                else BattleSaveData.I.NumResources?.AddResource(ResourceType.kGold, gold);

                Sprite fissionIcon = null;
                try { fissionIcon = UI?.BtnFuserFreeUpgrades?.ImgIcon?.sprite ?? LevelUpUI.I?.BtnFuserFreeUpgrades?.ImgIcon?.sprite; } catch { }
                LastFissionGrants[slot].Add(new FissionGrant
                {
                    Icon = fissionIcon,
                    Name = CoopLocalization.Text("Bonus Gold", "Or bonus"),
                    Gold = gold
                });
            }
        }

        private static void Apply(Option choice, int slot)
        {
            if (choice == null) return;

            if (choice.Upgrade != null)
            {
                bool wasMerge = choice.Upgrade.Info != null && choice.Upgrade.Info.IsMerged();
                UpgradeMgr.I.ApplyUpgrade(choice.Upgrade);
                if (choice.Upgrade.Type == UpgradeType.kHero)
                {
                    try { BallMgr.I?.OnHeroesChanged(); } catch { }
                }
                if (wasMerge && UI != null && UI.Type == LevelUpType.kFuser && BattleSaveData.I != null)
                {
                    BattleSaveData.I.NumEvosFused++;
                }
            }
            else if (choice.Combo.HasValue)
            {
                HeroCombo val = choice.Combo.Value;
                var heroes = BattleSaveData.I.Heroes;
                int idx1 = -1;
                int idx2 = -1;
                for (int i = 0; i < heroes.Count; i++)
                {
                    if (heroes[i].Type == val.H1 && heroes[i].IsAtMaxSolo()) { idx1 = i; break; }
                }
                for (int j = 0; j < heroes.Count; j++)
                {
                    if (j != idx1 && heroes[j].Type == val.H2 && heroes[j].IsAtMaxSolo()) { idx2 = j; break; }
                }
                if (idx1 >= 0 && idx2 >= 0)
                {
                    UpgradeMgr.I.CombineHeroes(new HeroCombo(idx1, val.H1, idx2, val.H2));
                    if (BattleSaveData.I != null) BattleSaveData.I.NumCombosFused++;
                }
                else
                {
                    Plugin.Logger?.LogWarning($"P{slot + 1} Fusion ingredients ({val.H1} x {val.H2}) were no longer available at commit.");
                }
            }
            else if (choice.IsFreeFuserUpgrade)
            {
                ExecuteFissionForCurrentPlayer(slot);
            }
            else if (choice.IsCoinBagFallback)
            {
                if (PickupMgr.I != null) PickupMgr.I.AddGold(choice.FallbackAmount);
                else BattleSaveData.I?.NumResources?.AddResource(ResourceType.kGold, choice.FallbackAmount);
            }
            else if (choice.IsHealFallback)
            {
                Player p = CoopCombat.PlayerAt(slot);
                if (p != null) p.Heal(choice.FallbackAmount);
            }

            UpgradeMgr.I.CalculateStats();
        }

        private static void CloseDecisionOverlayAfterCommit()
        {
            LevelUpUI ui = UI;
            _showingFissionSummary = false;
            _pending = false;
            _awaitResume = true;
            ModalOverlayState.ClearIf(ActiveModalOverlay.LevelUp);
            ChoiceOverlay.Hide();
            if (ui != null)
            {
                try { ui.CloseLevelUp(); }
                catch
                {
                    if (GameMgr.I != null) GameMgr.I.SetState(GameState.kPlaying);
                }
            }
        }

        private static PreCommitFuserSnapshot CapturePreCommitSnapshot(Option opt)
        {
            if (opt == null) return new PreCommitFuserSnapshot();
            return new PreCommitFuserSnapshot
            {
                IsFission = opt.IsFreeFuserUpgrade,
                HasFusedItem = opt.Upgrade != null || opt.Combo.HasValue,
                ShortName = opt.FuserShortName ?? opt.Name ?? "",
                Plaque = opt.FuserPlaque ?? opt.RichDescription ?? "",
                ResultIcon = opt.MainIcon,
                Ing1 = opt.RecipeIng1,
                Ing2 = opt.RecipeIng2
            };
        }

        private static void Commit()
        {
            _applying = true;
            try
            {
                LastFissionGrants[0].Clear();
                LastFissionGrants[1].Clear();

                Option pick0 = (Gate.Index[0] >= 0 && Gate.Index[0] < Options[0].Count) ? Options[0][Gate.Index[0]] : null;
                Option pick1 = (Gate.Index[1] >= 0 && Gate.Index[1] < Options[1].Count) ? Options[1][Gate.Index[1]] : null;

                // Snapshot Fuser display data BEFORE Apply() mutates HeroInst/PassiveInst levels!
                PreCommitSnapshots[0] = CapturePreCommitSnapshot(pick0);
                PreCommitSnapshots[1] = CapturePreCommitSnapshot(pick1);

                // Isolate each player's Apply in its own try/catch so a single failed option NEVER softlocks the run!
                try { CoopCharacterRuntime.ForPrimary(() => Apply(pick0, 0)); }
                catch (Exception ex0) { Plugin.Logger?.LogError("P1 decision apply error: " + ex0.Message); }

                try { CoopCharacterRuntime.ForSecond(() => Apply(pick1, 1)); }
                catch (Exception ex1) { Plugin.Logger?.LogError("P2 decision apply error: " + ex1.Message); }

                CoopCharacterRuntime.InvalidateStats();
                CoopVitals.RefreshStats();

                bool ranFission = UI != null && UI.Type == LevelUpType.kFuser
                    && ((PreCommitSnapshots[0].IsFission && LastFissionGrants[0].Count > 0)
                        || (PreCommitSnapshots[1].IsFission && LastFissionGrants[1].Count > 0));
                if (ranFission)
                {
                    _showingFissionSummary = true;
                    _fissionSummaryArmedAt = Time.unscaledTime + 0.45f;
                    return;
                }

                CloseDecisionOverlayAfterCommit();
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError("Decision commit error (closing safely to prevent softlock): " + ex);
                CloseDecisionOverlayAfterCommit();
            }
            finally
            {
                _applying = false;
            }
        }

        // --- Rendering ---

        private static string BuildEquipmentSummary(int slot)
        {
            var heroes = CoopCharacterRuntime.HeroesFor(slot);
            var passives = CoopCharacterRuntime.PassivesFor(slot);
            int hCount = heroes != null ? heroes.Count : 0;
            int pCount = passives != null ? passives.Count : 0;
            int maxBalls = BuildingMgr.I != null ? Math.Max(hCount, BuildingMgr.I.NumBallSlots) : hCount;
            int maxPassives = BuildingMgr.I != null ? Math.Max(pCount, BuildingMgr.I.NumPassiveSlots) : pCount;
            int curHp = Mathf.CeilToInt(CoopVitals.Life.Health[slot]);
            int maxHp = Mathf.CeilToInt(CoopVitals.Life.Maximum[slot]);
            return $"<color=#5CD6FF>{CoopLocalization.Text("Balls", "Balles")}:</color> {hCount}/{maxBalls}" +
                   $"   |   <color=#C996FF>{CoopLocalization.Text("Passives", "Passifs")}:</color> {pCount}/{maxPassives}" +
                   $"   |   <color=#78E69C>{CoopLocalization.Text("HP", "PV")} {curHp}/{maxHp}</color>";
        }

        private static void Render()
        {
            if (!_pending || UI == null) return;

            if (_showingFissionSummary)
            {
                RenderFissionSummary();
                return;
            }

            bool isFuser = UI.Type == LevelUpType.kFuser;
            bool p1CanReroll = CanReroll(0);
            bool p2CanReroll = CanReroll(1);
            bool canBanish = CanBanish();
            int gold = BattleSaveData.I != null ? BattleSaveData.I.GetNumResources(ResourceType.kGold) : 0;
            int banishes = BattleSaveData.I != null ? BattleSaveData.I.NumBanishes : 0;

            var footer = new StringBuilder();
            if (isFuser)
            {
                footer.Append(CoopLocalization.Text(
                    "Choose Evolution, Fusion or Fission per player  |  [A]/Click: Enter / Confirm  |  [B]: Back",
                    "Choisissez Évolution, Fusion ou Fission par joueur  |  [A]/Clic : Entrer / Valider  |  [B] : Retour"));
            }
            else
            {
                footer.Append(CoopLocalization.Text("Game resumes once both players confirm.", "La partie reprend dès que les 2 joueurs confirment."));
            }

            footer.Append("  |  <color=#F4C86A>").Append(CoopLocalization.Text("Gold", "Or")).Append(": ").Append(gold).Append("</color>");
            if (p1CanReroll || p2CanReroll)
            {
                string freeLbl = CoopLocalization.Text("FREE", "GRATUIT");
                string p1CostText = SlotFreeRerolls[0] > 0 ? $"{freeLbl} x{SlotFreeRerolls[0]}" : $"{SlotRerollCost(0)}g";
                string p2CostText = SlotFreeRerolls[1] > 0 ? $"{freeLbl} x{SlotFreeRerolls[1]}" : $"{SlotRerollCost(1)}g";
                if (p1CanReroll && p2CanReroll)
                {
                    footer.Append("  |  X/Y: ").Append(CoopLocalization.Text("Reroll", "Relance"))
                          .Append($" ({CoopLocalization.Text("P1", "J1")}: ").Append(p1CostText)
                          .Append($" / {CoopLocalization.Text("P2", "J2")}: ").Append(p2CostText).Append(')');
                }
                else if (p1CanReroll)
                {
                    footer.Append("  |  ").Append(CoopLocalization.Text("P1", "J1")).Append(" X/Y/R: ")
                          .Append(CoopLocalization.Text("Reroll", "Relance")).Append(" (").Append(p1CostText).Append(')');
                }
                else
                {
                    footer.Append("  |  ").Append(CoopLocalization.Text("P2", "J2")).Append(" X/Y/RShift: ")
                          .Append(CoopLocalization.Text("Reroll", "Relance")).Append(" (").Append(p2CostText).Append(')');
                }
            }
            if (canBanish)
            {
                footer.Append("  |  LB/Tab/\\: ").Append(CoopLocalization.Text("Banish", "Bannir")).Append(" (").Append(banishes).Append(')');
            }

            ChoiceOverlay.Show(
                isFuser
                    ? CoopLocalization.Text("FUSION REACTOR  |  EVOLUTION, FUSION & FISSION", "RÉACTEUR DE FUSION  |  ÉVOLUTION, FUSION & FISSION")
                    : CoopLocalization.Text("PLAYER UPGRADES  |  CHOOSE TOGETHER", "AMÉLIORATIONS  |  CHOISISSEZ ENSEMBLE"),
                footer.ToString());

            if (isFuser) ChoiceOverlay.SetCosmicMode(true);
            ChoiceOverlay.SetHeaderButtons(showClose: false);

            for (int slot = 0; slot < 2; slot++)
            {
                ChoiceOverlay.SetPlayerHeader(slot, $"{CoopLocalization.Text("PLAYER", "JOUEUR")} {slot + 1}  |  {GamepadRouter.SlotInputLabel(slot)}");
                Option choice = _started && Gate.Index[slot] >= 0 && Gate.Index[slot] < Options[slot].Count ? Options[slot][Gate.Index[slot]] : null;
                ChoiceOverlay.Card(
                    slot,
                    choice == null ? CoopLocalization.Text("Preparing options...", "Préparation des options...") : choice.DisplayRowTitle,
                    Gate.Ready[slot],
                    choice?.MainIcon);

                Sprite portrait = CoopCharacterRuntime.CharacterFor(slot)?.GetInfo()?.GetIconSprite();
                if (!isFuser)
                {
                    ChoiceOverlay.SetEquipmentSummary(slot, BuildEquipmentSummary(slot), portrait);
                }
                else
                {
                    int curHp = Mathf.CeilToInt(CoopVitals.Life.Health[slot]);
                    int maxHp = Mathf.CeilToInt(CoopVitals.Life.Maximum[slot]);
                    string hpLabel = $"{CoopLocalization.Text("HP", "PV")} {curHp}/{maxHp} | {CoopLocalization.Text("Gold", "Or")} {gold}";
                    ChoiceOverlay.SetStatusBadge(
                        slot,
                        Gate.Ready[slot] ? CoopLocalization.Text("* READY", "* PRÊT") : hpLabel,
                        Gate.Ready[slot] ? OverlayTheme.ColReadyGreen : OverlayTheme.ColGoldHighlight);
                }

                if (Banishing[slot])
                {
                    ChoiceOverlay.SetStatusBadge(slot, CoopLocalization.Text("BANISH MODE", "MODE BANNIR"), new Color(0.98f, 0.35f, 0.35f));
                }

                if (!_started) continue;

                bool isNoOp = (!isFuser || !InFuserCategoryMenu[slot] || CoopVitals.Down(slot))
                    && Options[slot].Count == 1 && IsNoOpOption(Options[slot][0]);
                if (isFuser && !isNoOp && InFuserCategoryMenu[slot])
                {
                    RenderFuserCategorySlot(slot);
                    continue;
                }

                if (!isNoOp)
                {
                    bool slotCanReroll = CanReroll(slot);
                    int slotFree = SlotFreeRerolls[slot];
                    int slotCost = SlotRerollCost(slot);
                    string rerollBase = CoopLocalization.Text("Re-roll", "Relancer");
                    string slotRerollText = slotFree > 0
                        ? $"{rerollBase} (<color=#78E69C>{CoopLocalization.Text("FREE", "GRATUIT")} x{slotFree}</color>)"
                        : $"{rerollBase} (<color=#F4C86A>{slotCost}g</color>)";
                    ChoiceOverlay.SetCardRerollButton(slot, slotCanReroll ? slotRerollText : null, enabled: !Gate.Ready[slot]);
                    ChoiceOverlay.SetCardBanishButton(
                        slot,
                        isFuser
                            ? CoopLocalization.Text("<- Categories [B]", "<- Catégories [B]")
                            : (canBanish ? $"{CoopLocalization.Text("Banish", "Bannir")} ({banishes})" : null),
                        active: Banishing[slot],
                        enabled: isFuser || !Gate.Ready[slot]);
                    ChoiceOverlay.SetCardConfirmButton(
                        slot,
                        Gate.Ready[slot]
                            ? CoopLocalization.Text("Ready! (Undo)", "Prêt ! (Annuler)")
                            : (Banishing[slot] ? CoopLocalization.Text("Banish Pick", "Bannir ce choix") : CoopLocalization.Text("Confirm", "Confirmer")),
                        active: Gate.Ready[slot],
                        enabled: true);
                }
                else
                {
                    ChoiceOverlay.SetCardRerollButton(slot, null);
                    ChoiceOverlay.SetCardBanishButton(slot, null);
                    ChoiceOverlay.SetCardConfirmButton(slot, null);
                }

                if (isFuser && !isNoOp)
                {
                    int count = Options[slot].Count;
                    string[] offerNames = new string[count];
                    Sprite[] resIcons = new Sprite[count];
                    Sprite[] ing1s = new Sprite[count];
                    Sprite[] ing2s = new Sprite[count];
                    for (int j = 0; j < count; j++)
                    {
                        Option opt = Options[slot][j];
                        offerNames[j] = opt.FuserShortName ?? opt.DisplayRowTitle;
                        resIcons[j] = opt.MainIcon;
                        ing1s[j] = opt.RecipeIng1;
                        ing2s[j] = opt.RecipeIng2;
                    }
                    string hint = CoopLocalization.Text(
                        "Up/Down / Wheel: Browse All Offers  |  [A] / Click: Select  |  [B]: Categories",
                        "Haut/Bas / Molette : Toutes les offres  |  [A] / Clic : Choisir  |  [B] : Catégories");
                    ChoiceOverlay.Options(
                        slot,
                        offerNames,
                        resIcons,
                        Gate.Index[slot],
                        showPortraitFromSummary: false,
                        ing1Icons: ing1s,
                        ing2Icons: ing2s,
                        cosmicMode: true,
                        plaqueText: choice?.FuserPlaque ?? string.Empty,
                        controlHintOverride: hint);
                }
                else
                {
                    int count = Options[slot].Count;
                    string[] names = new string[count];
                    Sprite[] icons = new Sprite[count];
                    Sprite[] fusions = new Sprite[count];
                    for (int j = 0; j < count; j++)
                    {
                        Option opt = Options[slot][j];
                        names[j] = opt.DisplayRowTitle;
                        icons[j] = opt.MainIcon;
                        fusions[j] = opt.FusionSecondIcon;
                    }
                    ChoiceOverlay.Options(slot, names, icons, Gate.Index[slot], fusions, choice?.RichDescription ?? string.Empty);
                }
            }
        }

        private static void RenderFuserCategorySlot(int slot)
        {
            int evoCount = AllFuserEvos[slot].Count;
            int comboCount = AllFuserCombos[slot].Count;
            bool hasFree = SlotHasFreeUpgrades[slot];
            GetFissionRange(out _, out _, out string rangeLabel);

            Sprite evoSprite = null, comboSprite = null, fissSprite = null;
            try { evoSprite = UI?.BtnFuserEvolution?.ImgIcon?.sprite ?? LevelUpUI.I?.BtnFuserEvolution?.ImgIcon?.sprite; } catch { }
            try { comboSprite = UI?.BtnFuserCombo?.ImgIcon?.sprite ?? LevelUpUI.I?.BtnFuserCombo?.ImgIcon?.sprite; } catch { }
            try { fissSprite = UI?.BtnFuserFreeUpgrades?.ImgIcon?.sprite ?? LevelUpUI.I?.BtnFuserFreeUpgrades?.ImgIcon?.sprite; } catch { }

            string evoLabel = $"{CoopLocalization.Text("Evolution", "Évolution")}   ({CoopLocalization.Text($"{evoCount} available", $"{evoCount} dispo")})";
            string comboLabel = $"{CoopLocalization.Text("Fusion", "Fusion")}   ({CoopLocalization.Text($"{comboCount} available", $"{comboCount} dispo")})";
            string fissLabel = hasFree
                ? $"{CoopLocalization.Text("Fission", "Fission")}   (+{rangeLabel} {CoopLocalization.Text("upgrade levels", "niveaux d'amélioration")})"
                : $"{CoopLocalization.Text("Fission", "Fission")}   ({CoopLocalization.Text("Bonus Gold", "Or bonus")})";

            string[] catNames = { evoLabel, comboLabel, fissLabel };
            Sprite[] catIcons = { evoSprite, comboSprite, fissSprite };
            bool[] catEnabled = { evoCount > 0, comboCount > 0, true };

            int catIdx = Mathf.Clamp(FuserCategoryIndex[slot], 0, 2);
            string plaque;
            if (Gate.Ready[slot])
            {
                plaque = $"<size=122%><b><color=#78E69C>{CoopLocalization.Text("Fission - READY", "Fission - PRÊT")}</color></b></size>\n" +
                    CoopLocalization.Text(
                        $"Upgrades {rangeLabel} random non-maxed items up to Lv.3 (or grants Gold if maxed).",
                        $"Améliore {rangeLabel} objets aléatoires jusqu'au Nv.3 (ou octroie de l'Or si tout est max).");
            }
            else if (catIdx == (int)FuserCategory.Evolution)
            {
                plaque = $"<size=122%><b><color=#F4E6D5>{CoopLocalization.Text("Evolution", "Évolution")} - {CoopLocalization.Text($"{evoCount} available", $"{evoCount} disponible(s)")}</color></b></size>\n" +
                    (evoCount > 0
                        ? CoopLocalization.Text("Combine two compatible Lv.3 items into a unique Evolved Ball or Passive.", "Combinez deux objets Nv.3 compatibles en un objet évolué unique.")
                        : CoopLocalization.Text("No Lv.3 Evolution recipes are currently ready for this player.", "Aucune recette d'évolution Nv.3 n'est prête pour ce joueur."));
            }
            else if (catIdx == (int)FuserCategory.Fusion)
            {
                plaque = $"<size=122%><b><color=#F4E6D5>{CoopLocalization.Text("Fusion", "Fusion")} - {CoopLocalization.Text($"{comboCount} available", $"{comboCount} disponible(s)")}</color></b></size>\n" +
                    (comboCount > 0
                        ? CoopLocalization.Text("Fuse two unfused Lv.3 Balls together so they launch combined effects.", "Fusionnez deux boules Nv.3 pour combiner leurs effets.")
                        : CoopLocalization.Text("Need at least two unfused Lv.3 Balls to fuse.", "Requiert au moins deux boules Nv.3 non fusionnées."));
            }
            else
            {
                plaque = $"<size=122%><b><color=#FF9F43>{CoopLocalization.Text("Fission", "Fission")} (+{rangeLabel})</color></b></size>\n" +
                    (hasFree
                        ? CoopLocalization.Text($"Randomly upgrades {rangeLabel} equipped non-maxed Balls or Passives (up to Lv.3).", $"Améliore aléatoirement {rangeLabel} boules ou artefacts équipés (jusqu'au Nv.3).")
                        : CoopLocalization.Text("All equipped items are maxed - grants bonus Gold instead!", "Tous vos objets sont au maximum - octroie de l'Or bonus !"));
            }

            ChoiceOverlay.SetCardRerollButton(slot, null);
            ChoiceOverlay.SetCardBanishButton(slot, null);
            ChoiceOverlay.SetCardConfirmButton(
                slot,
                Gate.Ready[slot]
                    ? CoopLocalization.Text("Ready! (Undo)", "Prêt ! (Annuler)")
                    : (catIdx == (int)FuserCategory.Fission
                        ? CoopLocalization.Text("Run Fission", "Lancer Fission")
                        : CoopLocalization.Text("View Offers", "Voir les offres")),
                active: Gate.Ready[slot],
                enabled: catEnabled[catIdx] || Gate.Ready[slot]);

            string hint = CoopLocalization.Text(
                "Up/Down / Mouse: Select Category  |  [A] / Click: Enter",
                "Haut/Bas / Souris : Catégorie  |  [A] / Clic : Choisir");
            ChoiceOverlay.Options(
                slot,
                catNames,
                catIcons,
                catIdx,
                showPortraitFromSummary: false,
                enabledFlags: catEnabled,
                cosmicMode: true,
                plaqueText: plaque,
                controlHintOverride: hint);
        }

        private static void RenderFissionSummary()
        {
            ChoiceOverlay.Show(
                CoopLocalization.Text("FISSION - UPGRADES GRANTED", "FISSION - AMÉLIORATIONS OCTROYÉES"),
                CoopLocalization.Text("Press [A], [Enter], or Click OK to continue", "Appuyez sur [A], [Entrée] ou cliquez sur OK pour continuer"));
            ChoiceOverlay.SetCosmicMode(true);
            ChoiceOverlay.SetHeaderButtons(showClose: false);
            ChoiceOverlay.SetPauseActionBar(new[] { CoopLocalization.Text("OK  [A / Enter]", "OK  [A / Entrée]") }, 0);

            Sprite fissionSprite = null;
            try { fissionSprite = UI?.BtnFuserFreeUpgrades?.ImgIcon?.sprite ?? LevelUpUI.I?.BtnFuserFreeUpgrades?.ImgIcon?.sprite; } catch { }

            for (int slot = 0; slot < 2; slot++)
            {
                ChoiceOverlay.SetPlayerHeader(slot, $"{CoopLocalization.Text("PLAYER", "JOUEUR")} {slot + 1}");
                ChoiceOverlay.SetCardRerollButton(slot, null);
                ChoiceOverlay.SetCardBanishButton(slot, null);
                ChoiceOverlay.SetCardConfirmButton(slot, null);

                PreCommitFuserSnapshot snap = PreCommitSnapshots[slot];
                var grants = LastFissionGrants[slot];

                if (snap.IsFission && grants.Count > 0)
                {
                    bool isGoldFallback = grants.Count == 1 && grants[0].Gold > 0;
                    ChoiceOverlay.SetStatusBadge(
                        slot,
                        isGoldFallback
                            ? $"+{grants[0].Gold} {CoopLocalization.Text("GOLD", "OR")}"
                            : $"+{grants.Count} {CoopLocalization.Text("UPGRADE(S)", "AMÉLIORATION(S)")}",
                        isGoldFallback ? OverlayTheme.ColGoldHighlight : OverlayTheme.ColReadyGreen);

                    string[] rows = new string[grants.Count];
                    Sprite[] icons = new Sprite[grants.Count];
                    for (int i = 0; i < grants.Count; i++)
                    {
                        FissionGrant g = grants[i];
                        icons[i] = g.Icon ?? fissionSprite;
                        rows[i] = g.Gold > 0
                            ? $"{g.Name}  (+{g.Gold} {CoopLocalization.Text("Gold", "Or")})"
                            : $"{g.Name}   {CoopLocalization.Text("Lv.", "Nv.")}{g.OldLvl} -> {CoopLocalization.Text("Lv.", "Nv.")}{g.NewLvl} (+1)";
                    }

                    string plaque = isGoldFallback
                        ? $"<size=122%><b><color=#F4C86A>{CoopLocalization.Text("Fission - Bonus Gold", "Fission - Or bonus")}</color></b></size>\n" +
                          CoopLocalization.Text("All equipped items were already at max level.", "Tous vos objets équipés étaient déjà au niveau maximum.")
                        : $"<size=122%><b><color=#78E69C>{CoopLocalization.Text($"Fission granted +{grants.Count} level(s)!", $"Fission a accordé +{grants.Count} niveau(x) !")}</color></b></size>\n" +
                          CoopLocalization.Text("Upgraded items applied to this player's loadout:", "Objets améliorés appliqués à l'équipement de ce joueur :");

                    ChoiceOverlay.Options(slot, rows, icons, 0, showPortraitFromSummary: false, cosmicMode: true, plaqueText: plaque, controlHintOverride: "");
                }
                else if (snap.HasFusedItem)
                {
                    // Uses pre-commit snapshot so partner's fused/evolved ball displays accurate pre-commit recipe & description!
                    ChoiceOverlay.SetStatusBadge(slot, CoopLocalization.Text("FUSED", "FUSIONNÉ"), new Color(0.361f, 0.839f, 1f, 1f));
                    ChoiceOverlay.Options(
                        slot,
                        new[] { snap.ShortName },
                        new[] { snap.ResultIcon },
                        0,
                        showPortraitFromSummary: false,
                        ing1Icons: new[] { snap.Ing1 },
                        ing2Icons: new[] { snap.Ing2 },
                        cosmicMode: true,
                        plaqueText: snap.Plaque,
                        controlHintOverride: "");
                }
                else
                {
                    ChoiceOverlay.SetStatusBadge(slot, CoopLocalization.Text("NO CHANGE", "INCHANGÉ"), new Color(0.75f, 0.66f, 0.69f, 1f));
                    ChoiceOverlay.Options(
                        slot,
                        new[] { CoopLocalization.Text("Waiting / Downed", "En attente / À terre") },
                        new[] { fissionSprite },
                        0,
                        showPortraitFromSummary: false,
                        cosmicMode: true,
                        plaqueText: CoopLocalization.Text("No Fission upgrades applied to this player.", "Aucune amélioration de Fission pour ce joueur."),
                        controlHintOverride: "");
                }
            }
        }
    }

    /// <summary>
    /// Tracks synchronized dual-player menu confirmation and cursor navigation.
    /// </summary>
    public sealed class DecisionGate
    {
        public readonly int[] Index = new int[2];
        public readonly bool[] Ready = new bool[2];

        public bool Complete => Ready[0] && Ready[1];

        public void Reset()
        {
            Index[0] = Index[1] = 0;
            Ready[0] = Ready[1] = false;
        }

        public void ResetSlot(int slot)
        {
            if (slot >= 0 && slot < 2)
            {
                Index[slot] = 0;
                Ready[slot] = false;
            }
        }

        public void Move(int slot, int direction, int count)
        {
            if (slot < 0 || slot >= 2 || Ready[slot] || count <= 0) return;
            Index[slot] = (Index[slot] + direction + count) % count;
        }

        public void Confirm(int slot, bool connected)
        {
            if (slot >= 0 && slot < 2) Ready[slot] = connected;
        }

        public void Cancel(int slot)
        {
            if (slot >= 0 && slot < 2) Ready[slot] = false;
        }
    }
}
