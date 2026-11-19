using System;
using System.Collections.Generic;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.Input;
using BALLxPITLocalCoop.UI;
using HarmonyLib;
using UnityEngine;

namespace BALLxPITLocalCoop.Core
{
    /// <summary>
    /// High-level Co-op lifecycle facade coordinating save-slot sync, run entry/rematch/exit,
    /// post-co-op town sanitization, and victory/defeat hooks.
    /// Delegates DLC/stage catalog to <see cref="CoopDlcAndLevels"/>, progression to <see cref="CoopProgression"/>,
    /// and native launch sequencing to <see cref="CoopLaunchFlow"/>.
    /// </summary>
    public static class CoopMode
    {
        public static bool Enabled { get; set; }
        public static int SelectedPhase { get; set; }

        public static bool IsLaunchingNative => CoopLaunchFlow.IsLaunchingNative;
        public static bool IsPreparingRun => CoopLaunchFlow.IsPreparingRun;
        public static bool ReturningToBase => Time.realtimeSinceStartup < _returningToBaseUntil;
        public static List<LevelInfo> Phases => CoopDlcAndLevels.Phases;
        public static CoopProgression.ProgressData State => CoopProgression.State;

        private static bool _installed;
        private static int _loadedSaveSlot = -1;
        private static float _returningToBaseUntil;
        private static bool _needsPostCoopBaseSanitize;

        public static void Install(Harmony harmony)
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            CoopProgression.Load();
            CoopLaunchFlow.Install(harmony);
            MainMenuCoopUI.Install(harmony);

            if (harmony == null)
            {
                return;
            }

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GameMgr), nameof(GameMgr.GoToBase)),
                prefix: new HarmonyMethod(typeof(CoopMode), nameof(OnReturnToBase)),
                label: "GameMgr.GoToBase");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GameMgr), nameof(GameMgr.MarkLevelComplete)),
                postfix: new HarmonyMethod(typeof(CoopMode), nameof(OnLevelComplete)),
                label: "GameMgr.MarkLevelComplete (CoopMode)");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GameMgr), nameof(GameMgr.LoseGame)),
                postfix: new HarmonyMethod(typeof(CoopMode), nameof(OnGameLost)),
                label: "GameMgr.LoseGame (CoopMode)");
        }

        public static void Shutdown()
        {
            CoopLaunchFlow.Shutdown();
            ExitToBaseCleanup();
            _loadedSaveSlot = -1;
            _returningToBaseUntil = 0f;
            _needsPostCoopBaseSanitize = false;
            _installed = false;
        }

        public static void SyncActiveSaveSlot()
        {
            try
            {
                int activeSlot = PlayerPrefsMgr.sSaveSlot;
                if (MetaSaveData.I == null || _loadedSaveSlot != activeSlot)
                {
                    SaveMgr.I?.LoadMeta();
                    _loadedSaveSlot = activeSlot;
                    CoopDlcAndLevels.InvalidateCache();
                    CoopProgression.Load(force: true);
                    CoopMenuUI.RefreshRosterOrder();
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"SyncActiveSaveSlot failed: {ex.Message}");
            }
        }

        public static CharBattleInst Character(int slot)
        {
            CharType selected = slot == 0 ? CoopCharacterRuntime.FirstType : CoopCharacterRuntime.SecondType;
            return new CharBattleInst(selected);
        }

        public static void Begin(MainMenuUI ui)
        {
            CoopProgression.Load();
            CoopLaunchFlow.ResetState();
            SyncActiveSaveSlot();

            CoopDlcAndLevels.PopulateLevels(force: true);
            SelectedPhase = Mathf.Clamp(CoopProgression.State.SelectedPhase, 0, Math.Max(0, Phases.Count - 1));
            Enabled = true;

            if (ui != null)
            {
                if (ui.GrpBtns != null) ui.GrpBtns.blocksRaycasts = false;
                ui.SetBtnsEnabled(false);
            }
            CoopMenuUI.OpenStandalone();
        }

        public static void CancelToMainMenu()
        {
            MainMenuCoopUI.NotifyCoopClosedCooldown();
            DisableForSolo();
            if (MainMenuUI.I != null)
            {
                if (MainMenuUI.I.GrpBtns != null) MainMenuUI.I.GrpBtns.blocksRaycasts = true;
                MainMenuUI.I.SetBtnsEnabled(true);
            }
        }

        public static void DisableForSolo()
        {
            ExitToBaseCleanup();
            GamepadRouter.RestoreTownControllers();
            if (BaseUI.I != null)
            {
                try
                {
                    BaseUI.I.BlockSidebarInput(false);
                    if (BaseUI.I.CvsGrpMain != null)
                    {
                        BaseUI.I.CvsGrpMain.blocksRaycasts = true;
                        BaseUI.I.CvsGrpMain.interactable = true;
                    }
                    CoolButtonEventMgr.SetNavLocked(false);
                    CoolButtonEventMgr.SetButtonsEnabled(true);
                    if (BaseUI.I.BtnPlay?.Btn != null)
                    {
                        CoolButtonEventMgr.SetSelectedBtn(BaseUI.I.BtnPlay.Btn);
                    }
                }
                catch { }
            }
        }

        public static void SwitchToSoloFromCoop()
        {
            DisableForSolo();
            if (BaseUI.I != null)
            {
                try { BaseUI.I.OnPlayClicked(); } catch { }
            }
        }

        public static void QuickRematch()
        {
            CoopProgression.AwardCharacterProgressXp(includeEarlyReturn: true);
            CoopProgression.Load();
            Enabled = true;
            Time.timeScale = 1f;
            CoopSession.ResetRound();
            ChoiceOverlay.Hide();
            StartRun(SelectedPhase);
        }

        public static void StartRun(int phaseIndex)
        {
            CoopDlcAndLevels.PopulateLevels();
            SelectedPhase = Mathf.Clamp(phaseIndex, 0, Math.Max(0, Phases.Count - 1));
            CoopProgression.State.SelectedPhase = SelectedPhase;
            CoopProgression.Save();

            CoopMenuUI.Close();
            CoopHudAndResults.ResetRound();

            if (SelectedPhase < Phases.Count)
            {
                CoopLaunchFlow.LaunchNative(Phases[SelectedPhase]);
            }
            else
            {
                ChoiceOverlay.Hide();
            }
        }

        public static void MarkReturningToBase(float seconds = 8f)
        {
            _returningToBaseUntil = Time.realtimeSinceStartup + Mathf.Max(2f, seconds);
            _needsPostCoopBaseSanitize = true;
        }

        public static void ClearReturningToBase()
        {
            _returningToBaseUntil = 0f;
        }

        public static void PreparePauseReturnToBase()
        {
            CoopProgression.AwardCharacterProgressXp(includeEarlyReturn: true);
            MarkReturningToBase();
            ExitToBaseCleanup();
        }

        public static void ExitToBaseCleanup()
        {
            Time.timeScale = 1f;
            Enabled = false;
            CoopLaunchFlow.ResetState();
            CoopMenuUI.Close();
            CoopHudAndResults.CloseChallenges();
            CoopSession.ResetRound();
            ChoiceOverlay.Hide();
            ModalOverlayState.Reset();
            GamepadRouter.RestoreTownControllers();
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        public static void EnsurePostCoopBaseClean()
        {
            if (!_needsPostCoopBaseSanitize && !ReturningToBase)
            {
                return;
            }
            if (SceneMgr.I == null || HarmonyHelper.SafeGetCurScene() != GameScene.kBase)
            {
                return;
            }
            if (BaseMgr.I == null || BaseMgr.I.CurState == BaseState.kReturningFromLvl)
            {
                return;
            }

            _needsPostCoopBaseSanitize = false;
            _returningToBaseUntil = 0f;

            try
            {
                Time.timeScale = 1f;
                if (OverlayUI.sOverlayStack != null && OverlayUI.sOverlayStack.Count > 0)
                {
                    bool hasDeadOverlay = false;
                    for (int i = 0; i < OverlayUI.sOverlayStack.Count; i++)
                    {
                        if (OverlayUI.sOverlayStack[i] == null)
                        {
                            hasDeadOverlay = true;
                            break;
                        }
                    }
                    if (hasDeadOverlay)
                    {
                        OverlayUI.InitializeOnLoad();
                    }
                }
            }
            catch { }

            try { GamepadRouter.RestoreTownControllers(); } catch { }

            try
            {
                if (MetaSaveData.I != null && MetaSaveData.I.Chars != null)
                {
                    bool touched = false;
                    for (int i = 0; i < MetaSaveData.I.Chars.Length; i++)
                    {
                        CharMetaInst c = MetaSaveData.I.Chars[i];
                        if (c == null || !c.IsUnlocked) continue;

                        if ((int)c.CurState < 0 || (int)c.CurState >= (int)CharState.kNum)
                        {
                            c.SetState(CharState.kIdle);
                            touched = true;
                        }
                        CharInfo info = null;
                        try { info = c.GetInfo(); } catch { }
                        if (c.Type != CharType.kInfluencer
                            && info != null
                            && CoopDlcAndLevels.SafeCharIncludeInGame(info)
                            && MetaSaveData.I.CharWorkerOrder != null
                            && !MetaSaveData.I.CharWorkerOrder.Contains(c.Type))
                        {
                            MetaSaveData.I.CharWorkerOrder.Add(c.Type);
                            touched = true;
                        }
                    }
                    if (BaseMgr.I != null && (BaseMgr.I.ActiveWorkers == null || BaseMgr.I.ActiveWorkers.Count == 0))
                    {
                        BaseMgr.I.SetUpActiveWorkers();
                    }
                    if (touched)
                    {
                        SaveMgr.I?.MarkMetaDirty();
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"EnsurePostCoopBaseClean failed: {ex.Message}");
            }
        }

        private static bool OnReturnToBase()
        {
            if (!Enabled) return true;
            if (CoopHudAndResults.InterceptReturn()) return false;
            CoopProgression.AwardCharacterProgressXp(includeEarlyReturn: true);
            MarkReturningToBase();
            ExitToBaseCleanup();
            return true;
        }

        private static void OnLevelComplete()
        {
            if (!Enabled || CoopRoundStats.Complete) return;
            CoopRoundStats.Complete = true;
            CoopProgression.RecordSpeedTierCompletion();
            CoopProgression.AwardCharacterProgressXp(includeEarlyReturn: true);
            CoopProgression.RecordVictory(SelectedPhase);
        }

        private static void OnGameLost()
        {
            if (!Enabled || CoopRoundStats.Complete) return;
            CoopRoundStats.Complete = true;
            CoopProgression.AwardCharacterProgressXp(includeEarlyReturn: true);
            CoopProgression.RecordDefeat(SelectedPhase);
        }

        public static void Update()
        {
            MainMenuCoopUI.TickMenuNavigation();
            CoopHudAndResults.Tick();

            if (Enabled && CoopMenuUI.Selecting)
            {
                CoopMenuUI.TickSelection();
            }

            if (Enabled && CoopLaunchFlow.IsPreparingRun)
            {
                CoopLaunchFlow.Step();
            }

            // Only auto-cleanup in Base Town when NO town co-op menu (CharSelect, StageSelect, Settings, Challenges) is open!
            if (Enabled
                && !CoopLaunchFlow.IsPreparingRun
                && !ModalOverlayState.IsTownMenuOpen
                && !CoopMenuUI.Selecting
                && !CoopHudAndResults.ChallengesOpen
                && SceneMgr.I != null
                && HarmonyHelper.SafeGetCurScene() == GameScene.kBase)
            {
                CoopProgression.AwardCharacterProgressXp(includeEarlyReturn: true);
                MarkReturningToBase();
                ExitToBaseCleanup();
                EnsurePostCoopBaseClean();
            }
        }
    }
}
