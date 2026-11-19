using System;
using System.Collections.Generic;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.Input;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Rewired;
using UnityEngine;

namespace BALLxPITLocalCoop.UI
{
    /// <summary>
    /// Routes pause/unpause triggers from either player cleanly during co-op battles and renders
    /// the dual-player pause menu + mid-run Co-op Settings + vanilla Game Settings passthrough.
    /// Driven cleanly from Update() (zero OnGUI), fixes double "/s" DPS, MouseMovedThisFrame hover gating,
    /// P2 stick/keyboard navigation in settings & loadout, OverlayUI stack check, and DisconnectNotice expiry.
    /// </summary>
    public static class CoopPause
    {
        private const int PauseActionCount = 5;
        private const int PauseSettingsCount = 6;

        private static readonly PauseInputGate Gate = new PauseInputGate();
        private static bool _installed;
        private static bool _pauseOverlayOpen;
        private static bool _pauseSubSettings;
        private static bool _pauseInputDropdown;
        private static int _pauseInputIdx;
        private static bool _inVanillaSettings;
        private static int _pauseCursor;
        private static int _settingsCursor;
        private static int _p1InspectCursor;
        private static int _p2InspectCursor;
        private static float _nextNavAt;
        private static float _p1NavYAt;
        private static float _p2NextNavAt;

        public static string DisconnectNotice { get; private set; }
        public static float DisconnectNoticeUntil { get; private set; }
        public static bool IsCustomPauseActive => _pauseOverlayOpen;

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            string[] methods = { nameof(InputMgr.IsBtnDown), nameof(InputMgr.IsBtnHeld), nameof(InputMgr.IsBtnUp) };
            foreach (string name in methods)
            {
                HarmonyHelper.TryPatch(
                    harmony,
                    AccessTools.Method(typeof(InputMgr), name, new[] { typeof(GameActionType) }),
                    prefix: new HarmonyMethod(typeof(CoopPause), nameof(SuppressNativePauseInput)) { priority = Priority.First },
                    label: $"InputMgr.{name} (CoopPause)");
            }

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GameMgr), nameof(GameMgr.SetState)),
                prefix: new HarmonyMethod(typeof(CoopPause), nameof(CaptureBeforeState)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(CoopPause), nameof(OnStateChanged)) { priority = Priority.Last },
                label: "GameMgr.SetState (CoopPause)");
        }

        public static void Shutdown()
        {
            Reset();
            _installed = false;
        }

        public static void Reset()
        {
            Gate.Reset();
            DisconnectNotice = null;
            DisconnectNoticeUntil = 0f;
            ClosePauseOverlay();
            _inVanillaSettings = false;
        }

        private static bool InBattle() =>
            CoopMode.Enabled && SceneMgr.I != null && HarmonyHelper.IsInGameScene() && GameMgr.I != null;

        private static bool SuppressNativePauseInput(GameActionType __0, ref bool __result)
        {
            if (__0 != GameActionType.kPause || !InBattle()) return true;
            __result = false;
            return false;
        }

        private static void CaptureBeforeState(GameMgr __instance, out GameState __state) =>
            __state = __instance != null ? __instance.CurState : GameState.kPlaying;

        private static void OnStateChanged(GameMgr __instance, GameState __state)
        {
            if (__instance == null) return;
            GameState cur = __instance.CurState;
            if (InBattle() && cur != __state && (cur == GameState.kPaused || cur == GameState.kPlaying))
            {
                SyncGateTransition();
                if (cur == GameState.kPlaying)
                {
                    DisconnectNotice = null;
                    DisconnectNoticeUntil = 0f;
                }
            }
        }

        private static void SyncGateTransition()
        {
            IGamepadTemplate p1 = GamepadRouter.GetPad(0);
            IGamepadTemplate p2 = GamepadRouter.GetPad(1);
            Gate.Transition(
                Time.frameCount,
                Time.unscaledTime,
                p1?.start.value ?? false,
                p2?.start.value ?? false,
                UnityEngine.Input.GetKey(KeyCode.Escape));
        }

        public static void TriggerDisconnectPause(string notice)
        {
            if (!InBattle() || PauseUI.I == null || GameMgr.I.CurState != GameState.kPlaying || ModalOverlayState.IsBattleOverlayBlockingPause) return;
            DisconnectNotice = notice;
            DisconnectNoticeUntil = Time.unscaledTime + 15f;
            PauseUI.I.Activate();
            if (GameMgr.I.CurState != GameState.kPaused)
            {
                GameMgr.I.SetState(GameState.kPaused);
            }
        }

        public static void Tick()
        {
            if (!InBattle())
            {
                Gate.Reset();
                ClosePauseOverlay();
                _inVanillaSettings = false;
                return;
            }

            // Enforce DisconnectNotice expiration cleanly.
            if (!string.IsNullOrEmpty(DisconnectNotice) && Time.unscaledTime >= DisconnectNoticeUntil)
            {
                DisconnectNotice = null;
                DisconnectNoticeUntil = 0f;
            }

            if (_inVanillaSettings)
            {
                SetVanillaPauseHidden(false);
                if (!IsVanillaSubpageOpen())
                {
                    _inVanillaSettings = false;
                }
                else
                {
                    return;
                }
            }

            if (IsCustomPauseEligible())
            {
                if (!_pauseOverlayOpen)
                {
                    if (!ChoiceOverlay.EnsureCreated(PauseUI.I))
                    {
                        SetVanillaPauseHidden(false);
                        return;
                    }
                    _pauseOverlayOpen = true;
                    _pauseSubSettings = false;
                    _pauseInputDropdown = false;
                    _pauseCursor = 0;
                    _p1InspectCursor = 0;
                    _p2InspectCursor = 0;
                    _nextNavAt = Time.unscaledTime + 0.16f;
                    _p1NavYAt = 0f;
                    _p2NextNavAt = 0f;
                    ModalOverlayState.Set(ActiveModalOverlay.Pause);
                    SetVanillaPauseHidden(true);
                }

                bool handled = TickCustomPauseMenu();
                if (_pauseOverlayOpen && !_inVanillaSettings && IsCustomPauseEligible())
                {
                    Render();
                }
                if (handled)
                {
                    return;
                }
            }
            else if (_pauseOverlayOpen)
            {
                ClosePauseOverlay();
            }

            // Gate pause toggling cleanly on ModalOverlayState so Esc/Start never toggle pause during LevelUp/Revive/Result!
            if (ModalOverlayState.IsBattleOverlayBlockingPause || ModalOverlayState.IsCancelConsumedThisFrame())
            {
                return;
            }

            IGamepadTemplate p1 = GamepadRouter.GetPad(0);
            IGamepadTemplate p2 = GamepadRouter.GetPad(1);
            ProcessPauseToggle(
                Time.frameCount,
                Time.unscaledTime,
                Application.isFocused,
                p1?.start.value ?? false,
                p2?.start.value ?? false,
                UnityEngine.Input.GetKey(KeyCode.Escape));
        }

        private static void ProcessPauseToggle(int frame, float time, bool focused, bool pad0Start, bool pad1Start, bool esc)
        {
            if (GameMgr.I == null || PauseUI.I == null) return;
            GameState state = GameMgr.I.CurState;
            if (state != GameState.kPlaying && state != GameState.kPaused) return;
            if (!Gate.Update(frame, time, focused, pad0Start, pad1Start, esc)) return;

            if (state == GameState.kPlaying)
            {
                PauseUI.I.Activate();
                if (GameMgr.I.CurState != GameState.kPaused) GameMgr.I.SetState(GameState.kPaused);
            }
            else
            {
                ResumeRun();
            }
        }

        private static bool IsCustomPauseEligible()
        {
            return InBattle()
                && GameMgr.I != null
                && GameMgr.I.CurState == GameState.kPaused
                && PauseUI.I != null
                && PauseUI.I.gameObject.activeInHierarchy
                && !IsVanillaSubpageOpen()
                && !ModalOverlayState.IsBattleOverlayBlockingPause
                && !CoopHudAndResults.ResultShown
                && !CoopDecisions.IsPending
                && !CoopRevivePrompt.Pending
                && !CoopMenuUI.Selecting;
        }

        private static bool IsVanillaSubpageOpen()
        {
            if (PauseUI.I == null || !PauseUI.I.gameObject.activeInHierarchy) return false;
            try
            {
                if (BaseSettingsUI.I != null && BaseSettingsUI.I.gameObject.activeInHierarchy) return true;
                if (EncyclopediaUI.I != null && EncyclopediaUI.I.gameObject.activeInHierarchy) return true;
                if (OverlayUI.sOverlayStack != null)
                {
                    for (int i = 0; i < OverlayUI.sOverlayStack.Count; i++)
                    {
                        OverlayUI ov = OverlayUI.sOverlayStack[i];
                        if (HarmonyHelper.IsUnityAlive(ov)
                            && ov.gameObject.activeInHierarchy
                            && !HarmonyHelper.SameNative(ov, PauseUI.I))
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static void SetVanillaPauseHidden(bool hide)
        {
            if (PauseUI.I == null) return;
            try
            {
                var cgType = Il2CppType.Of<CanvasGroup>();
                CanvasGroup cg = PauseUI.I.gameObject.GetComponent(cgType)?.TryCast<CanvasGroup>();
                if (cg == null && hide) cg = PauseUI.I.gameObject.AddComponent(cgType)?.TryCast<CanvasGroup>();
                if (cg != null)
                {
                    cg.alpha = hide ? 0f : 1f;
                    cg.interactable = !hide;
                    cg.blocksRaycasts = !hide;
                }
            }
            catch { }
        }

        private static void ClosePauseOverlay()
        {
            if (_pauseOverlayOpen)
            {
                _pauseOverlayOpen = false;
                _pauseSubSettings = false;
                _pauseInputDropdown = false;
                ModalOverlayState.ClearIf(ActiveModalOverlay.Pause);
                ChoiceOverlay.Hide();
            }
            SetVanillaPauseHidden(false);
        }

        private static void ResumeRun()
        {
            ClosePauseOverlay();
            if (PauseUI.I != null)
            {
                try { PauseUI.I.OnCloseClicked(); } catch { }
            }
            if (GameMgr.I != null && GameMgr.I.CurState == GameState.kPaused)
            {
                GameMgr.I.SetState(GameState.kPlaying);
            }
            SyncGateTransition();
        }

        private static void OpenVanillaGameSettings()
        {
            _inVanillaSettings = true;
            ClosePauseOverlay();
            if (PauseUI.I != null)
            {
                try { PauseUI.I.OnSettingsClicked(); } catch { }
            }
        }

        private static void RestartCoopRun()
        {
            ClosePauseOverlay();
            if (PauseUI.I != null)
            {
                try { PauseUI.I.OnCloseClicked(); } catch { }
            }
            Time.timeScale = 1f;
            CoopMode.QuickRematch();
        }

        private static void ReturnToBaseFromPause()
        {
            ClosePauseOverlay();
            CoopMode.PreparePauseReturnToBase();
            if (PauseUI.I != null)
            {
                try { PauseUI.I.OnBaseConfirmed(); } catch { }
            }
            else if (GameMgr.I != null)
            {
                try { GameMgr.I.GoToBase(); } catch { }
            }
        }

        private static bool TickCustomPauseMenu()
        {
            if (!Application.isFocused) return true;

            if (OverlayInput.ConsumeActionClicked("close"))
            {
                if (_pauseInputDropdown)
                {
                    _pauseInputDropdown = false;
                    _nextNavAt = Time.unscaledTime + 0.16f;
                    return true;
                }
                if (_pauseSubSettings)
                {
                    _pauseSubSettings = false;
                    _nextNavAt = Time.unscaledTime + 0.16f;
                    return true;
                }
                ResumeRun();
                return true;
            }

            if (_pauseSubSettings)
            {
                return TickPauseSettings();
            }

            for (int p = 0; p < PauseActionCount; p++)
            {
                if (OverlayInput.ConsumeActionClicked("pause_" + p))
                {
                    _pauseCursor = p;
                    ExecutePauseAction(p);
                    return true;
                }
                if (OverlayInput.MouseMovedThisFrame && OverlayInput.IsActionHovered("pause_" + p))
                {
                    _pauseCursor = p;
                }
            }

            if (OverlayInput.TryGetHoveredOptionIndex(0, out int hov0) && hov0 >= 0) _p1InspectCursor = hov0;
            if (OverlayInput.TryGetHoveredOptionIndex(1, out int hov1) && hov1 >= 0) _p2InspectCursor = hov1;

            IGamepadTemplate p1 = GamepadRouter.GetPad(0);
            IGamepadTemplate p2 = GamepadRouter.GetPad(1);

            // P1 vertical loadout scroll
            float moveY1 = 0f;
            if (UnityEngine.Input.GetKey(KeyCode.W)) moveY1 += 1f;
            if (UnityEngine.Input.GetKey(KeyCode.S)) moveY1 -= 1f;
            if (!GamepadRouter.IsSharedKeyboardMode && !GamepadRouter.UsesGamepadForSlot(0))
            {
                if (UnityEngine.Input.GetKey(KeyCode.UpArrow)) moveY1 += 1f;
                if (UnityEngine.Input.GetKey(KeyCode.DownArrow)) moveY1 -= 1f;
            }
            if (p1 != null && Mathf.Abs(GamepadRouter.MenuStick(p1).y) > Mathf.Abs(moveY1)) moveY1 = GamepadRouter.MenuStick(p1).y;
            if (Mathf.Abs(moveY1) < 0.45f) _p1NavYAt = 0f;
            else if (Time.unscaledTime >= _p1NavYAt)
            {
                BuildPlayerLoadoutRows(0, out string[] p1Rows, out _);
                if (p1Rows.Length > 0) _p1InspectCursor = (_p1InspectCursor + (moveY1 > 0f ? -1 : 1) + p1Rows.Length) % p1Rows.Length;
                _p1NavYAt = Time.unscaledTime + 0.16f;
            }

            // P2 vertical loadout scroll (gamepad OR P2 keyboard keys!)
            float p2Y = p2 != null ? GamepadRouter.MenuStick(p2).y : 0f;
            if (!GamepadRouter.UsesGamepadForSlot(1))
            {
                float kb2Y = ((UnityEngine.Input.GetKey(ModConfig.KeyMoveUp) || UnityEngine.Input.GetKey(KeyCode.UpArrow)) ? 1f : 0f)
                           - ((UnityEngine.Input.GetKey(ModConfig.KeyMoveDown) || UnityEngine.Input.GetKey(KeyCode.DownArrow)) ? 1f : 0f);
                if (Mathf.Abs(kb2Y) > Mathf.Abs(p2Y)) p2Y = kb2Y;
            }
            if (Mathf.Abs(p2Y) < 0.45f) _p2NextNavAt = 0f;
            else if (Time.unscaledTime >= _p2NextNavAt)
            {
                BuildPlayerLoadoutRows(1, out string[] p2Rows, out _);
                if (p2Rows.Length > 0) _p2InspectCursor = (_p2InspectCursor + (p2Y > 0f ? -1 : 1) + p2Rows.Length) % p2Rows.Length;
                _p2NextNavAt = Time.unscaledTime + 0.16f;
            }

            if ((p1 != null && GamepadRouter.ButtonJustPressed(p1.b)) || (p2 != null && GamepadRouter.ButtonJustPressed(p2.b)))
            {
                ResumeRun();
                return true;
            }

            float moveX = 0f;
            if (UnityEngine.Input.GetKey(KeyCode.A) || UnityEngine.Input.GetKey(KeyCode.LeftArrow)) moveX -= 1f;
            if (UnityEngine.Input.GetKey(KeyCode.D) || UnityEngine.Input.GetKey(KeyCode.RightArrow)) moveX += 1f;
            if (p1 != null && Mathf.Abs(GamepadRouter.MenuStick(p1).x) > Mathf.Abs(moveX)) moveX = GamepadRouter.MenuStick(p1).x;
            if (p2 != null && Mathf.Abs(GamepadRouter.MenuStick(p2).x) > Mathf.Abs(moveX)) moveX = GamepadRouter.MenuStick(p2).x;

            if (Mathf.Abs(moveX) < 0.35f) _nextNavAt = 0f;
            else if (Time.unscaledTime >= _nextNavAt)
            {
                _pauseCursor = (_pauseCursor + (moveX < 0f ? -1 : 1) + PauseActionCount) % PauseActionCount;
                _nextNavAt = Time.unscaledTime + 0.16f;
            }

            bool confirmPressed = UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.Space)
                || (p1 != null && GamepadRouter.ButtonJustPressed(p1.a))
                || (p2 != null && GamepadRouter.ButtonJustPressed(p2.a));
            if (confirmPressed)
            {
                ExecutePauseAction(_pauseCursor);
                return true;
            }

            return false;
        }

        private static bool TickPauseSettings()
        {
            IGamepadTemplate p1 = GamepadRouter.GetPad(0);
            IGamepadTemplate p2 = GamepadRouter.GetPad(1);

            if (_pauseInputDropdown)
            {
                int presetCount = GamepadRouter.InputPresetCount;
                if (OverlayInput.ConsumeActionClicked("confirm_1")
                    || UnityEngine.Input.GetKeyDown(KeyCode.Escape)
                    || UnityEngine.Input.GetKeyDown(KeyCode.Backspace)
                    || (p1 != null && GamepadRouter.ButtonJustPressed(p1.b))
                    || (p2 != null && GamepadRouter.ButtonJustPressed(p2.b)))
                {
                    _pauseInputDropdown = false;
                    _nextNavAt = Time.unscaledTime + 0.16f;
                    ModalOverlayState.ConsumeCancelThisFrame();
                    SyncGateTransition();
                    return true;
                }
                if (OverlayInput.TryGetHoveredOptionIndex(1, out int hOpt) && hOpt >= 0 && hOpt < presetCount) _pauseInputIdx = hOpt;
                if (OverlayInput.TryGetClickedOptionIndex(1, out int cOpt) && cOpt >= 0 && cOpt < presetCount)
                {
                    GamepadRouter.SetInputPresetByIndex(cOpt);
                    _pauseInputDropdown = false;
                    return true;
                }

                float dMoveY = 0f;
                if (UnityEngine.Input.GetKey(KeyCode.W) || UnityEngine.Input.GetKey(KeyCode.UpArrow)) dMoveY += 1f;
                if (UnityEngine.Input.GetKey(KeyCode.S) || UnityEngine.Input.GetKey(KeyCode.DownArrow)) dMoveY -= 1f;
                if (p1 != null && Mathf.Abs(GamepadRouter.MenuStick(p1).y) > Mathf.Abs(dMoveY)) dMoveY = GamepadRouter.MenuStick(p1).y;
                if (p2 != null && Mathf.Abs(GamepadRouter.MenuStick(p2).y) > Mathf.Abs(dMoveY)) dMoveY = GamepadRouter.MenuStick(p2).y;

                if (Mathf.Abs(dMoveY) < 0.35f) _nextNavAt = 0f;
                else if (Time.unscaledTime >= _nextNavAt)
                {
                    _pauseInputIdx = (((_pauseInputIdx + (dMoveY > 0f ? -1 : 1)) % presetCount) + presetCount) % presetCount;
                    _nextNavAt = Time.unscaledTime + 0.16f;
                }

                if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.Space)
                    || (p1 != null && GamepadRouter.ButtonJustPressed(p1.a))
                    || (p2 != null && GamepadRouter.ButtonJustPressed(p2.a)))
                {
                    GamepadRouter.SetInputPresetByIndex(_pauseInputIdx);
                    _pauseInputDropdown = false;
                }
                return true;
            }

            if (OverlayInput.ConsumeActionClicked("confirm_0")
                || OverlayInput.ConsumeActionClicked("confirm_1")
                || UnityEngine.Input.GetKeyDown(KeyCode.Escape)
                || UnityEngine.Input.GetKeyDown(KeyCode.Backspace)
                || (p1 != null && GamepadRouter.ButtonJustPressed(p1.b))
                || (p2 != null && GamepadRouter.ButtonJustPressed(p2.b)))
            {
                _pauseSubSettings = false;
                _nextNavAt = Time.unscaledTime + 0.16f;
                ModalOverlayState.ConsumeCancelThisFrame();
                SyncGateTransition();
                return true;
            }

            if (OverlayInput.TryGetHoveredOptionIndex(0, out int hov) && hov >= 0 && hov < PauseSettingsCount) _settingsCursor = hov;
            if (OverlayInput.TryGetClickedOptionIndex(0, out int clk) && clk >= 0 && clk < PauseSettingsCount)
            {
                _settingsCursor = clk;
                if (clk == 2)
                {
                    _pauseInputDropdown = true;
                    _pauseInputIdx = GamepadRouter.CurrentInputPresetIndex();
                    _nextNavAt = Time.unscaledTime + 0.16f;
                }
                else ApplyPauseSettingDelta(_settingsCursor, +1);
                return true;
            }

            float moveY = 0f, moveX = 0f;
            if (UnityEngine.Input.GetKey(KeyCode.W) || UnityEngine.Input.GetKey(KeyCode.UpArrow)) moveY += 1f;
            if (UnityEngine.Input.GetKey(KeyCode.S) || UnityEngine.Input.GetKey(KeyCode.DownArrow)) moveY -= 1f;
            if (UnityEngine.Input.GetKey(KeyCode.D) || UnityEngine.Input.GetKey(KeyCode.RightArrow)) moveX += 1f;
            if (UnityEngine.Input.GetKey(KeyCode.A) || UnityEngine.Input.GetKey(KeyCode.LeftArrow)) moveX -= 1f;
            if (p1 != null)
            {
                Vector2 s1 = GamepadRouter.MenuStick(p1);
                if (Mathf.Abs(s1.y) > Mathf.Abs(moveY)) moveY = s1.y;
                if (Mathf.Abs(s1.x) > Mathf.Abs(moveX)) moveX = s1.x;
            }
            if (p2 != null)
            {
                Vector2 s2 = GamepadRouter.MenuStick(p2);
                if (Mathf.Abs(s2.y) > Mathf.Abs(moveY)) moveY = s2.y;
                if (Mathf.Abs(s2.x) > Mathf.Abs(moveX)) moveX = s2.x;
            }

            if (Mathf.Abs(moveY) < 0.35f && Mathf.Abs(moveX) < 0.35f) _nextNavAt = 0f;
            else if (Time.unscaledTime >= _nextNavAt)
            {
                if (Mathf.Abs(moveY) >= Mathf.Abs(moveX))
                    _settingsCursor = (_settingsCursor + (moveY > 0f ? -1 : 1) + PauseSettingsCount) % PauseSettingsCount;
                else
                    ApplyPauseSettingDelta(_settingsCursor, moveX > 0f ? +1 : -1);
                _nextNavAt = Time.unscaledTime + 0.16f;
            }

            if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.Space)
                || (p1 != null && GamepadRouter.ButtonJustPressed(p1.a))
                || (p2 != null && GamepadRouter.ButtonJustPressed(p2.a)))
            {
                if (_settingsCursor == 2)
                {
                    _pauseInputDropdown = true;
                    _pauseInputIdx = GamepadRouter.CurrentInputPresetIndex();
                    _nextNavAt = Time.unscaledTime + 0.16f;
                }
                else ApplyPauseSettingDelta(_settingsCursor, +1);
            }
            return true;
        }

        private static void ExecutePauseAction(int index)
        {
            switch (index)
            {
                case 0: ResumeRun(); break;
                case 1:
                    _pauseSubSettings = true;
                    _settingsCursor = 0;
                    _nextNavAt = Time.unscaledTime + 0.16f;
                    break;
                case 2: OpenVanillaGameSettings(); break;
                case 3: RestartCoopRun(); break;
                case 4: ReturnToBaseFromPause(); break;
            }
        }

        private static void ApplyPauseSettingDelta(int row, int delta)
        {
            switch (row)
            {
                case 0: CoopBalance.CyclePreset(delta); break;
                case 1: CoopDlcAndLevels.CycleGameSpeed(); break;
                case 2: GamepadRouter.CyclePairingMode(delta); break;
                case 3: CoopBalance.ToggleAutoPickMaxed(); break;
                case 4: PlayerPalette.CyclePlayerColor(0, delta); break;
                case 5: PlayerPalette.CyclePlayerColor(1, delta); break;
            }
        }

        private static void Render()
        {
            if (_pauseSubSettings)
            {
                RenderPauseSettings();
                return;
            }

            ChoiceOverlay.Show(
                CoopLocalization.Text("CO-OP PAUSE", "PAUSE CO-OP"),
                CoopLocalization.Text(
                    "Left/Right: Action   -   Up/Down: Loadout   -   [A]/Enter/Click: Confirm   -   [B]/Start: Resume",
                    "Gauche/Droite : Action   -   Haut/Bas : Équipement   -   [A]/Entrée/Clic : Valider   -   [B]/Start : Reprendre"));
            ChoiceOverlay.SetHeaderButtons(showClose: true);

            string[] actionLabels =
            {
                CoopLocalization.Text("Resume", "Reprendre"),
                CoopLocalization.Text("Co-op Settings", "Options Co-op"),
                CoopLocalization.Text("Game Settings", "Paramètres"),
                CoopLocalization.Text("Restart", "Recommencer"),
                CoopLocalization.Text("Return to Base", "Village")
            };
            ChoiceOverlay.SetPauseActionBar(actionLabels, _pauseCursor);

            BuildPlayerLoadoutRows(0, out string[] p1Items, out Sprite[] p1Icons);
            BuildPlayerLoadoutRows(1, out string[] p2Items, out Sprite[] p2Icons);

            DrawPlayerPauseCard(0, p1Items, p1Icons, _p1InspectCursor);
            DrawPlayerPauseCard(1, p2Items, p2Icons, _p2InspectCursor);
        }

        private static void RenderPauseSettings()
        {
            ChoiceOverlay.Show(
                CoopLocalization.Text("CO-OP SETTINGS", "OPTIONS CO-OP"),
                CoopLocalization.Text(
                    "Up/Down: Choose   -   Left/Right/[A]: Change   -   [B]/Esc: Back",
                    "Haut/Bas : Choisir   -   Gauche/Droite/[A] : Modifier   -   [B]/Échap : Retour"));
            ChoiceOverlay.CenterSingleCard(0.14f, 0.86f);
            ChoiceOverlay.SetHeaderButtons(showClose: true);

            string p1Hex = PlayerPalette.GetPlayerColorHex(0);
            string p2Hex = PlayerPalette.GetPlayerColorHex(1);
            string p1ColorName = PlayerPalette.GetPlayerColorName(0, CoopLocalization.IsFrench);
            string p2ColorName = PlayerPalette.GetPlayerColorName(1, CoopLocalization.IsFrench);
            string autoPickVal = CoopBalance.AutoPickMaxed
                ? CoopLocalization.Text("YES (Auto)", "OUI (Auto)")
                : CoopLocalization.Text("NO (Choice)", "NON (Choix)");

            string[] rows =
            {
                $"{CoopLocalization.Text("Co-op Difficulty:", "Difficulté Co-op :")}  < {CoopBalance.CurrentPresetLabel()} >",
                $"{CoopLocalization.Text("Game Speed:", "Vitesse du jeu :")}  < {CoopDlcAndLevels.GameSpeedLabel()} >",
                $"{CoopLocalization.Text("Input Mode:", "Mode d'entrées :")}  [{GamepadRouter.InputPresetLabel(GamepadRouter.CurrentInputPresetIndex())}]",
                $"{CoopLocalization.Text("Auto-Pick Maxed:", "Auto-choisir si plein :")}  < {autoPickVal} >",
                $"{CoopLocalization.Text("Player 1 Color:", "Couleur Joueur 1 :")}  <color=#{p1Hex}><b>[{p1ColorName}]</b></color>",
                $"{CoopLocalization.Text("Player 2 Color:", "Couleur Joueur 2 :")}  <color=#{p2Hex}><b>[{p2ColorName}]</b></color>"
            };

            ChoiceOverlay.SetPlayerHeader(0, CoopLocalization.Text("MID-RUN CONFIGURATION", "CONFIGURATION EN PARTIE"));
            ChoiceOverlay.Card(0, CoopBalance.CurrentPresetLabel(), ready: false, null);
            ChoiceOverlay.SetEquipmentSummary(0, CoopBalance.PresetDescription(CoopBalance.CurrentPresetIndex()));
            ChoiceOverlay.SetStatusBadge(0, CoopLocalization.Text("BepInEx Config Active", "Config BepInEx active"), new Color(0.36f, 0.84f, 1f));
            ChoiceOverlay.SetCardConfirmButton(0, CoopLocalization.Text("Back [B]", "Retour [B]"));
            ChoiceOverlay.Options(0, rows, null, _settingsCursor);

            if (_pauseInputDropdown)
            {
                int count = GamepadRouter.InputPresetCount;
                int activeIdx = GamepadRouter.CurrentInputPresetIndex();
                string[] presetRows = new string[count];
                for (int p = 0; p < count; p++)
                {
                    presetRows[p] = (p == activeIdx ? "* " : "  ") + GamepadRouter.InputPresetLabel(p);
                }
                ChoiceOverlay.SettingsListDropdown(
                    CoopLocalization.Text("INPUT MODE SELECT", "CHOIX DES ENTRÉES"),
                    GamepadRouter.InputPresetLabel(_pauseInputIdx),
                    GamepadRouter.PairingModeSummary(),
                    presetRows,
                    _pauseInputIdx,
                    GamepadRouter.InputPresetDescription(_pauseInputIdx),
                    CoopLocalization.Text("Close [B]", "Fermer [B]"));
            }
        }

        private static void DrawPlayerPauseCard(int slot, string[] items, Sprite[] icons, int selectedIdx)
        {
            CharBattleInst charInst = CoopCharacterRuntime.CharacterFor(slot);
            CharInfo charInfo = charInst?.GetInfo();
            string charName = CoopMenuUI.CharacterDisplayName(charInfo);
            Sprite portrait = charInfo?.GetIconSprite();

            ChoiceOverlay.SetPlayerHeader(slot, $"{CoopLocalization.Text("PLAYER", "JOUEUR")} {slot + 1}  |  {GamepadRouter.SlotInputLabel(slot)}");
            ChoiceOverlay.Card(slot, charName, ready: false, portrait);

            int hp = Mathf.CeilToInt(CoopVitals.Life.Health[slot]);
            int maxHp = Mathf.CeilToInt(CoopVitals.Life.Maximum[slot]);
            bool isDown = CoopVitals.Life.Down(slot);
            string badge = isDown
                ? CoopLocalization.Text("KO", "KO")
                : $"{CoopLocalization.Text("HP", "PV")} {hp}/{maxHp} | {CoopLocalization.Text("Revives", "Vies")} {CoopVitals.Life.Revives[slot]}/{CoopVitals.Life.MaxRevives[slot]}";
            Color badgeCol = isDown ? new Color(0.95f, 0.44f, 0.42f) : PlayerPalette.GetPlayerColor(slot);
            ChoiceOverlay.SetStatusBadge(slot, badge, badgeCol);

            // FormatDps already includes "/s" (fixes duplicate "/s/s" bug!).
            string dps = CoopRoundStats.FormatDps(CoopRoundStats.GetLiveDps(slot));
            string summary = CoopLocalization.Text(
                $"DPS: {dps}   |   Kills: {CoopRoundStats.Kills[slot]}   |   DMG: {CoopRoundStats.Damage[slot]:N0}\nInput: {GamepadRouter.SlotInputLabel(slot)}",
                $"DPS : {dps}   |   Kills : {CoopRoundStats.Kills[slot]}   |   Dégâts : {CoopRoundStats.Damage[slot]:N0}\nEntrée : {GamepadRouter.SlotInputLabel(slot)}");
            ChoiceOverlay.SetEquipmentSummary(slot, summary, portrait);
            ChoiceOverlay.Options(slot, items, icons, selectedIdx);
        }

        private static void BuildPlayerLoadoutRows(int slot, out string[] itemsOut, out Sprite[] iconsOut)
        {
            var items = new List<string>(16);
            var icons = new List<Sprite>(16);

            var heroes = CoopCharacterRuntime.HeroesFor(slot);
            int heroCount = heroes != null ? heroes.Count : 0;
            int ballCap = Math.Max(4, BuildingMgr.I != null && BuildingMgr.I.NumBallSlots > 0 ? BuildingMgr.I.NumBallSlots : 4);
            int shownBallCap = Math.Max(ballCap, heroCount);
            items.Add($"<color=#5CD6FF>--- {CoopLocalization.Text("BALLS", "BOULES")} ({heroCount}/{ballCap}) ---</color>");
            icons.Add(null);
            for (int i = 0; i < shownBallCap; i++)
            {
                if (heroes != null && i < heroCount)
                {
                    UpgradeInfo info = heroes[i].GetInfo();
                    items.Add($"{CoopDecisions.SafeUpgradeName(info, "Ball")}   [Lv.{heroes[i].Lvl + 1}]");
                    icons.Add(info?.Icon);
                }
                else
                {
                    items.Add($"<color=#7E6A75>[ - ] {CoopLocalization.Text("Empty Slot", "Emplacement vide")}</color>");
                    icons.Add(null);
                }
            }

            var passives = CoopCharacterRuntime.PassivesFor(slot);
            int passCount = passives != null ? passives.Count : 0;
            int passCap = Math.Max(4, BuildingMgr.I != null && BuildingMgr.I.NumPassiveSlots > 0 ? BuildingMgr.I.NumPassiveSlots : 4);
            int shownPassCap = Math.Max(passCap, passCount);
            items.Add($"<color=#C996FF>--- {CoopLocalization.Text("ARTIFACTS", "ARTEFACTS")} ({passCount}/{passCap}) ---</color>");
            icons.Add(null);
            for (int i = 0; i < shownPassCap; i++)
            {
                if (passives != null && i < passCount)
                {
                    UpgradeInfo info = passives[i].GetInfo();
                    items.Add($"{CoopDecisions.SafeUpgradeName(info, "Artifact")}   [Lv.{passives[i].Lvl + 1}]");
                    icons.Add(info?.Icon);
                }
                else
                {
                    items.Add($"<color=#7E6A75>[ - ] {CoopLocalization.Text("Empty Slot", "Emplacement vide")}</color>");
                    icons.Add(null);
                }
            }

            itemsOut = items.ToArray();
            iconsOut = icons.ToArray();
        }
    }
}
