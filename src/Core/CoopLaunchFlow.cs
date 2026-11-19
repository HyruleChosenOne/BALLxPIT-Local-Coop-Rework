using System;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.UI;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BALLxPITLocalCoop.Core
{
    /// <summary>
    /// Orchestrates the native Town/MainMenu -> Battle transition sequence for Co-op runs.
    /// Fixes QuickRematch timeout handling, restores MainMenuUI/BaseUI cleanly on abort/timeout,
    /// waits for CharSelectItem population before confirming, and guards LevelSelectUI.Activate.
    /// </summary>
    public static class CoopLaunchFlow
    {
        private const float LaunchTimeoutSeconds = 12f;

        private static bool _installed;
        private static bool _launchingNative;
        private static bool _preparingRun;
        private static bool _waitingForSceneLeave;
        private static int _launchStage;
        private static float _launchAt;
        private static float _launchStartedAt;
        private static LevelInfo _pendingLevel;

        public static bool IsLaunchingNative { get; private set; }
        public static bool IsPreparingRun => CoopMode.Enabled && _preparingRun;

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(LevelSelectUI), nameof(LevelSelectUI.Activate)),
                postfix: new HarmonyMethod(typeof(CoopLaunchFlow), nameof(OnLevelSelectActivate)),
                label: "LevelSelectUI.Activate");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(SaveMgr), nameof(SaveMgr.LoadBattle)),
                prefix: new HarmonyMethod(typeof(CoopLaunchFlow), nameof(OnLoadBattle)),
                label: "SaveMgr.LoadBattle");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(LevelData), nameof(LevelData.IsUnlocked)),
                prefix: new HarmonyMethod(typeof(CoopLaunchFlow), nameof(OnLevelDataIsUnlocked)),
                label: "LevelData.IsUnlocked");
        }

        public static void Shutdown()
        {
            ResetState();
            _installed = false;
        }

        public static void ResetState()
        {
            _launchingNative = false;
            _preparingRun = false;
            _waitingForSceneLeave = false;
            _launchStage = 0;
            _pendingLevel = null;
            IsLaunchingNative = false;
        }

        public static void LaunchNative(LevelInfo level)
        {
            if (level == null)
            {
                ChoiceOverlay.Hide();
                ModalOverlayState.ClearIf(ActiveModalOverlay.LoadingBanner);
                return;
            }

            _launchingNative = true;
            CoopProgression.ResetRunRewards();
            try
            {
                if (MetaSaveData.I == null)
                {
                    try { SaveMgr.I?.LoadMeta(); } catch { }
                }

                int ngPlus = Mathf.Clamp(CoopDlcAndLevels.SelectedNGPlus, 0, CoopDlcAndLevels.MaxNGPlusFor(level));
                CoopDlcAndLevels.SelectedNGPlus = ngPlus;
                int speedTier = CoopDlcAndLevels.ClampSpeedTierFor(level, CoopDlcAndLevels.SelectedSpeedTier);
                CoopDlcAndLevels.SelectedSpeedTier = speedTier;

                if (MetaSaveData.I != null)
                {
                    MetaSaveData.I.LastChar = CoopCharacterRuntime.FirstType;
                    MetaSaveData.I.LastLevel = level.Type;
                    MetaSaveData.I.LastLevelNGPlus = ngPlus;
                }

                CharBattleInst.I = CoopMode.Character(0);
                if (LevelMgr.I != null) LevelMgr.I.CurDifficulty = speedTier;
                if (SaveMgr.I != null) SaveMgr.I.BattleSave = null;

                GameScene curScene = HarmonyHelper.SafeGetCurScene();
                _pendingLevel = level;
                _waitingForSceneLeave = curScene == GameScene.kGame;
                _preparingRun = true;
                _launchStage = 0;
                _launchStartedAt = Time.unscaledTime;
                _launchAt = curScene == GameScene.kBase ? Time.unscaledTime + 0.05f : Time.unscaledTime + 0.25f;

                ModalOverlayState.Set(ActiveModalOverlay.LoadingBanner);
                ChoiceOverlay.LoadingBanner(
                    CoopLocalization.Text("Local Co-op", "Co-op Locale"),
                    CoopLocalization.Text("Loading run and characters...", "Chargement de la partie et des personnages..."));

                if (curScene == GameScene.kMainMenu && MainMenuUI.I != null)
                {
                    IsLaunchingNative = true;
                    try
                    {
                        MainMenuUI.I.SelectBtn(MainMenuOption.kContinue);
                    }
                    finally
                    {
                        IsLaunchingNative = false;
                    }
                }
                else if (curScene != GameScene.kBase)
                {
                    SceneMgr.I?.LoadScene(GameScene.kBase, 0.2f);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"LaunchNative failed: {ex.Message}");
                AbortAndRestoreUI();
            }
            finally
            {
                _launchingNative = false;
            }
        }

        public static void Step()
        {
            if (!_preparingRun || _pendingLevel == null)
            {
                return;
            }

            GameScene curScene = HarmonyHelper.SafeGetCurScene();

            // Check timeout unconditionally — including when _waitingForSceneLeave is stuck in kGame during QuickRematch!
            bool timedOut = (Time.unscaledTime - _launchStartedAt) >= LaunchTimeoutSeconds
                && (_waitingForSceneLeave || curScene != GameScene.kGame);
            if (timedOut)
            {
                Plugin.Logger?.LogWarning($"Native launch timed out after {LaunchTimeoutSeconds:0}s (stage={_launchStage}, waitingLeave={_waitingForSceneLeave}, scene={curScene}); aborting launch overlay.");
                AbortAndRestoreUI();
                return;
            }

            if (_waitingForSceneLeave)
            {
                if (curScene != GameScene.kGame)
                {
                    _waitingForSceneLeave = false;
                }
                return;
            }

            if (curScene == GameScene.kGame)
            {
                if (CoopCharacterRuntime.IndependentReady || Time.unscaledTime >= _launchAt)
                {
                    _preparingRun = false;
                    _pendingLevel = null;
                    CoopProgression.State.TotalRuns++;
                    CoopProgression.Save();
                    ChoiceOverlay.Hide();
                    ModalOverlayState.ClearIf(ActiveModalOverlay.LoadingBanner);
                }
                return;
            }

            if (Time.unscaledTime < _launchAt)
            {
                return;
            }

            if (curScene != GameScene.kBase || BaseMgr.I == null || CharSelectUI.I == null || LevelSelectUI.I == null)
            {
                return;
            }

            try
            {
                if (_launchStage == 0)
                {
                    if (BaseMgr.I.CurState != BaseState.kSelectingChar)
                    {
                        BaseMgr.I.SetState(BaseState.kSelectingChar);
                        CharSelectUI.I.Activate();
                    }

                    var items = ((Component)(object)CharSelectUI.I).GetComponentsInChildren(Il2CppType.Of<CharSelectItem>(), includeInactive: true);
                    if (items == null || items.Length == 0)
                    {
                        // Wait briefly for CharSelectItem population instead of clicking Select prematurely.
                        _launchAt = Time.unscaledTime + 0.08f;
                        return;
                    }

                    CharSelectItem matched = null;
                    for (int i = 0; i < items.Length; i++)
                    {
                        CharSelectItem item = items[i]?.TryCast<CharSelectItem>();
                        if (item != null && item.TgtInfo != null && item.TgtInfo.Type == CoopCharacterRuntime.FirstType)
                        {
                            matched = item;
                            break;
                        }
                    }
                    if (matched != null)
                    {
                        CharSelectUI.I.ClickItem(matched);
                    }
                    CharSelectUI.I.OnSelectClicked();
                    _launchStage = 1;
                    _launchAt = Time.unscaledTime + 0.15f;
                    return;
                }

                if (BaseMgr.I.CurState == BaseState.kSelectingLoadout && LoadoutUI.I != null)
                {
                    LoadoutUI.I.OnConfirmClicked();
                    _launchAt = Time.unscaledTime + 0.15f;
                    return;
                }

                if (_launchStage == 1)
                {
                    int ngPlus = Mathf.Clamp(CoopDlcAndLevels.SelectedNGPlus, 0, CoopDlcAndLevels.MaxNGPlusFor(_pendingLevel));
                    int speedTier = CoopDlcAndLevels.ClampSpeedTierFor(_pendingLevel, CoopDlcAndLevels.SelectedSpeedTier);
                    CoopDlcAndLevels.SelectedSpeedTier = speedTier;

                    BaseMgr.I.SetState(BaseState.kSelectingLevel);
                    LevelSelectUI.I.Activate();
                    SelectInLevelUI(LevelSelectUI.I, _pendingLevel);
                    LevelSelectUI.I._selectedLevel = _pendingLevel;
                    LevelSelectUI.I._selectedNGPlus = ngPlus;
                    LevelSelectUI.I._selectedDiff = speedTier;
                    LevelSelectUI.I._selectedItem?.SetSelectedDiff(speedTier);
                    _launchStage = 2;
                    LevelSelectUI.I.OnPlayClicked();
                    _launchAt = Time.unscaledTime + 0.2f;
                    return;
                }

                if (BaseMgr.I.CurState == BaseState.kEnteringLvl && _launchStage == 2)
                {
                    int ngPlus = Mathf.Clamp(CoopDlcAndLevels.SelectedNGPlus, 0, CoopDlcAndLevels.MaxNGPlusFor(_pendingLevel));
                    int speedTier = CoopDlcAndLevels.ClampSpeedTierFor(_pendingLevel, CoopDlcAndLevels.SelectedSpeedTier);
                    _launchStage = 3;
                    LevelMgr.I?.SetLevel(_pendingLevel.Type);
                    BattleSaveData.I = new BattleSaveData(CharBattleInst.I = CoopMode.Character(0));
                    if (BattleSaveData.I.Seed == 0)
                    {
                        BattleSaveData.I.Seed = unchecked((int)((uint)Environment.TickCount * 1664525u ^ (uint)UnityEngine.Random.Range(1, int.MaxValue)));
                    }
                    BattleSaveData.I.CurLevel = _pendingLevel.Type;
                    BattleSaveData.I.CurNGPlusLvl = ngPlus;
                    BattleSaveData.I.CurDifficulty = speedTier;
                    if (LevelMgr.I != null) LevelMgr.I.CurDifficulty = speedTier;
                    if (SaveMgr.I != null)
                    {
                        SaveMgr.I.BattleSave = BattleSaveData.I;
                        SaveMgr.I.StartNewBattle();
                    }
                    BaseMgr.I.CompleteEnterLvl();
                    _launchAt = Time.unscaledTime + 1.5f;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"Native launch step failed (stage={_launchStage}): {ex.Message}");
                AbortAndRestoreUI();
            }
        }

        private static void AbortAndRestoreUI()
        {
            ResetState();
            ChoiceOverlay.Hide();
            ModalOverlayState.ClearIf(ActiveModalOverlay.LoadingBanner);

            try
            {
                if (MainMenuUI.I != null)
                {
                    if (MainMenuUI.I.GrpBtns != null) MainMenuUI.I.GrpBtns.blocksRaycasts = true;
                    MainMenuUI.I.SetBtnsEnabled(true);
                }
            }
            catch { }

            try
            {
                if (BaseUI.I != null)
                {
                    BaseUI.I.BlockSidebarInput(false);
                    if (BaseUI.I.CvsGrpMain != null)
                    {
                        BaseUI.I.CvsGrpMain.blocksRaycasts = true;
                        BaseUI.I.CvsGrpMain.interactable = true;
                    }
                    CoolButtonEventMgr.SetNavLocked(false);
                    CoolButtonEventMgr.SetButtonsEnabled(true);
                }
            }
            catch { }
        }

        private static bool OnLoadBattle(SaveMgr __instance, ref bool __result)
        {
            if (!CoopMode.Enabled || !_preparingRun || HarmonyHelper.IsInGameScene())
            {
                return true;
            }
            __result = false;
            return false;
        }

        private static bool OnLevelDataIsUnlocked(LevelData __instance, ref bool __result)
        {
            if (!CoopMode.Enabled || !_preparingRun)
            {
                return true;
            }
            __result = __instance != null && __instance.NGPlusLvl <= Math.Max(0, CoopDlcAndLevels.SelectedNGPlus);
            return false;
        }

        private static void OnLevelSelectActivate(LevelSelectUI __instance)
        {
            if (!CoopMode.Enabled || _launchingNative || _preparingRun || CoopDlcAndLevels.Phases.Count == 0)
            {
                return;
            }

            int idx = Mathf.Clamp(CoopMode.SelectedPhase, 0, CoopDlcAndLevels.Phases.Count - 1);
            LevelInfo target = CoopDlcAndLevels.Phases[idx];
            if (target != null && BattleSaveData.I != null && BattleSaveData.I.CurLevel == target.Type)
            {
                SelectInLevelUI(__instance, target);
            }
        }

        private static void SelectInLevelUI(LevelSelectUI ui, LevelInfo level)
        {
            if (ui == null || level == null) return;

            bool isDlc = CoopDlcAndLevels.SafeCanAccessBallbylonDLC() && CoopDlcAndLevels.SafeIsDLCLevel(level.Type);
            Il2CppReferenceArray<LevelSelectItem> dlcItems = null;
            try { dlcItems = ui.ItemsDLC; } catch { }
            ui.SetPage(isDlc ? LevelSelectPage.kDLCLevels : LevelSelectPage.kBaseLevels);

            var items = isDlc && dlcItems != null ? dlcItems : ui.Items;
            LevelSelectItem targetItem = FindLevelSelectItem(items, level.Type)
                ?? FindLevelSelectItem(isDlc ? ui.Items : dlcItems, level.Type);

            if (targetItem != null)
            {
                ui.ClickItem(targetItem);
                ui.SelectItem(targetItem);
            }
        }

        private static LevelSelectItem FindLevelSelectItem(Il2CppReferenceArray<LevelSelectItem> list, LevelType levelType)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Length; i++)
            {
                LevelSelectItem item = list[i];
                if (item != null && item.TgtInfo != null && item.TgtInfo.Type == levelType) return item;
            }
            return null;
        }
    }
}
