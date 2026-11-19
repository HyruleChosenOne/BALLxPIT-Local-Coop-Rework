using System;
using System.Collections.Generic;
using System.Text;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.Input;
using Rewired;
using UnityEngine;

namespace BALLxPITLocalCoop.UI
{
    /// <summary>
    /// Town Co-op menus: Character Select, Stage Select, and Co-op Settings (with Input Mode & Color Palette drawers).
    /// Extracted cleanly from gameplay runtime, driven once per frame from Update() (zero OnGUI churn).
    /// </summary>
    public static class CoopMenuUI
    {
        private const int SettingsRowCount = 7;

        private static readonly List<CharType> AvailableTypes = new List<CharType>(32);
        private static bool _lastLoadedDlc;
        private static bool _rosterLoaded;
        private static Sprite[] _cachedRosterSprites = Array.Empty<Sprite>();
        private static bool[] _cachedRosterUnlocked = Array.Empty<bool>();
        private static string[] _cachedRosterBadges = Array.Empty<string>();
        private static Sprite[] _cachedStageSprites = Array.Empty<Sprite>();
        private static bool[] _cachedStageUnlocked = Array.Empty<bool>();
        private static string[] _cachedStageBadges = Array.Empty<string>();

        private static readonly int[] SelectedIndex = new int[2];
        private static readonly float[] NextMove = new float[2];
        private static readonly DecisionGate ReadyGate = new DecisionGate();

        private static bool _stageSelection;
        private static bool _settingsOpen;
        private static int _settingsIndex;
        private static float _settingsMoveAt;
        private static int _colorDropdownSlot = -1;
        private static int _colorDropdownIndex;
        private static float _colorMoveAt;
        private static bool _inputDropdownOpen;
        private static int _inputDropdownIndex;
        private static float _inputMoveAt;
        private static float _armedAt;

        private static readonly Sprite[] ColorSwatchSprites = new Sprite[PlayerPalette.ColorCount];
        private static readonly Texture2D[] ColorSwatchTextures = new Texture2D[PlayerPalette.ColorCount];
        private static Sprite _randomSprite;
        private static Texture2D _randomTex;

        public static bool Selecting { get; set; }

        private static int RosterCount => AvailableTypes.Count + 1;
        private static bool IsRandomSlot(int gridIdx) => gridIdx == 0;

        public static void RefreshRosterOrder() => LoadAvailableCharacters(force: true);

        public static int CountPlayableCharacters()
        {
            LoadAvailableCharacters();
            return AvailableTypes.Count;
        }

        public static void Shutdown()
        {
            Close();
            if (_randomSprite != null) { UnityEngine.Object.Destroy(_randomSprite); _randomSprite = null; }
            if (_randomTex != null) { UnityEngine.Object.Destroy(_randomTex); _randomTex = null; }
            for (int i = 0; i < ColorSwatchSprites.Length; i++)
            {
                if (ColorSwatchSprites[i] != null) { UnityEngine.Object.Destroy(ColorSwatchSprites[i]); ColorSwatchSprites[i] = null; }
                if (ColorSwatchTextures[i] != null) { UnityEngine.Object.Destroy(ColorSwatchTextures[i]); ColorSwatchTextures[i] = null; }
            }
            AvailableTypes.Clear();
            _rosterLoaded = false;
        }

        public static void OpenStandalone()
        {
            CoopMode.SyncActiveSaveSlot();
            LoadAvailableCharacters(force: true);
            CoopDlcAndLevels.PopulateLevels(force: true);
            Selecting = true;
            _stageSelection = false;
            _settingsOpen = false;
            _colorDropdownSlot = -1;
            _inputDropdownOpen = false;
            _armedAt = Time.unscaledTime + 0.28f;
            ReadyGate.Reset();
            NextMove[0] = NextMove[1] = 0f;

            if (!AvailableTypes.Contains(CoopCharacterRuntime.FirstType) || !CoopDlcAndLevels.Unlocked(CoopCharacterRuntime.FirstType))
            {
                CoopCharacterRuntime.FirstType = FirstUnlockedType(CharType.kDefault);
            }
            if (!AvailableTypes.Contains(CoopCharacterRuntime.SecondType) || !CoopDlcAndLevels.Unlocked(CoopCharacterRuntime.SecondType))
            {
                CoopCharacterRuntime.SecondType = FirstUnlockedType(CoopCharacterRuntime.FirstType);
            }
            if (!CoopDlcAndLevels.LevelUnlocked(CoopMode.SelectedPhase))
            {
                CoopMode.SelectedPhase = 0;
            }

            SelectedIndex[0] = FindTypeIndex(CoopCharacterRuntime.FirstType);
            SelectedIndex[1] = FindTypeIndex(CoopCharacterRuntime.SecondType);

            ModalOverlayState.Set(ActiveModalOverlay.CharSelect);
            ChoiceOverlay.EnsureCreated((Component)BaseUI.I ?? (Component)MainMenuUI.I ?? BaseMgr.I);
            Plugin.Logger?.LogInfo($"Co-op menu opened: characters={AvailableTypes.Count}, stages={CoopDlcAndLevels.Phases.Count}.");
        }

        public static void Close()
        {
            Selecting = false;
            _stageSelection = false;
            _settingsOpen = false;
            _colorDropdownSlot = -1;
            _inputDropdownOpen = false;
            _armedAt = 0f;
            ReadyGate.Reset();
            ModalOverlayState.ClearIf(ActiveModalOverlay.CharSelect);
            ModalOverlayState.ClearIf(ActiveModalOverlay.StageSelect);
            ModalOverlayState.ClearIf(ActiveModalOverlay.Settings);
        }

        public static void ReopenFromChallenges()
        {
            Selecting = true;
            _armedAt = Time.unscaledTime + 0.16f;
            ModalOverlayState.Set(_stageSelection ? ActiveModalOverlay.StageSelect : ActiveModalOverlay.CharSelect);
        }

        public static bool IsPlayableCharacter(CharInfo info, bool canAccessDlc)
        {
            if (info == null || !info.IsInGame) return false;
            if ((int)info.Type < 0 || info.Type >= CharType.kNum || info.Type == CharType.kInfluencer) return false;
            if (CoopDlcAndLevels.SafeGetCharDLCVersion(info) > 0 && !canAccessDlc) return false;
            if (!CoopDlcAndLevels.SafeCharIncludeInGame(info)) return false;
            return true;
        }

        private static void LoadAvailableCharacters(bool force = false)
        {
            bool canAccessDlc = CoopDlcAndLevels.SafeCanAccessBallbylonDLC();
            if (!force && _rosterLoaded && AvailableTypes.Count > 0 && _lastLoadedDlc == canAccessDlc)
            {
                return;
            }
            _rosterLoaded = true;
            _lastLoadedDlc = canAccessDlc;
            AvailableTypes.Clear();

            if (InfoDB.I?.Chars != null)
            {
                foreach (CharInfo info in InfoDB.I.Chars)
                {
                    if (IsPlayableCharacter(info, canAccessDlc) && !AvailableTypes.Contains(info.Type))
                    {
                        AvailableTypes.Add(info.Type);
                    }
                }
            }
            if (MetaSaveData.I?.Chars != null)
            {
                foreach (CharMetaInst c in MetaSaveData.I.Chars)
                {
                    CharInfo info = c?.GetInfo();
                    if (IsPlayableCharacter(info, canAccessDlc) && !AvailableTypes.Contains(info.Type))
                    {
                        AvailableTypes.Add(info.Type);
                    }
                }
            }

            AvailableTypes.Sort((a, b) => ((int)a).CompareTo((int)b));
            if (AvailableTypes.Count == 0) AvailableTypes.Add(CharType.kRecaller);
        }

        private static CharType FirstUnlockedType(CharType fallback)
        {
            for (int i = 0; i < AvailableTypes.Count; i++)
            {
                if (CoopDlcAndLevels.Unlocked(AvailableTypes[i])) return AvailableTypes[i];
            }
            return AvailableTypes.Count > 0 ? AvailableTypes[0] : fallback;
        }

        private static int FindTypeIndex(CharType type)
        {
            int idx = AvailableTypes.IndexOf(type);
            return idx >= 0 ? idx + 1 : 1;
        }

        private static CharType RollRandomUnlockedChar(CharType? avoid)
        {
            var pool = new List<CharType>(AvailableTypes.Count);
            for (int i = 0; i < AvailableTypes.Count; i++)
            {
                if (CoopDlcAndLevels.Unlocked(AvailableTypes[i])) pool.Add(AvailableTypes[i]);
            }
            if (pool.Count == 0) return FirstUnlockedType(CharType.kDefault);
            if (avoid.HasValue && pool.Count > 1) pool.Remove(avoid.Value);
            return pool[UnityEngine.Random.Range(0, pool.Count)];
        }

        public static CharInfo GetCharInfo(CharType type)
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
            return null;
        }

        // --- Single-frame Update + Render (no OnGUI) ---

        public static void TickSelection()
        {
            if (!Selecting || CoopHudAndResults.ChallengesOpen)
            {
                return;
            }

            LoadAvailableCharacters();
            if (AvailableTypes.Count == 0) return;

            if (Time.unscaledTime >= _armedAt && Application.isFocused)
            {
                if (_settingsOpen)
                {
                    UpdateSettingsInput();
                }
                else if (IsSettingsHotkeyPressed())
                {
                    _settingsOpen = true;
                    _settingsIndex = 0;
                    _armedAt = Time.unscaledTime + 0.16f;
                    ModalOverlayState.Set(ActiveModalOverlay.Settings);
                }
                else if (_stageSelection)
                {
                    UpdateStageInput();
                }
                else
                {
                    UpdateCharacterInput();
                }
            }

            if (!Selecting || CoopHudAndResults.ChallengesOpen)
            {
                return;
            }

            if (_settingsOpen) RenderSettings();
            else if (_stageSelection) RenderStageSelect();
            else RenderCharacterSelect();
        }

        private static void UpdateCharacterInput()
        {
            if (OverlayInput.ConsumeActionClicked("close"))
            {
                CoopMode.CancelToMainMenu();
                return;
            }

            IGamepadTemplate pad0 = GamepadRouter.GetPad(0);
            IGamepadTemplate pad1 = GamepadRouter.GetPad(1);
            if (OverlayInput.ConsumeActionClicked("action1")
                || UnityEngine.Input.GetKeyDown(KeyCode.C)
                || (pad0 != null && GamepadRouter.ButtonJustPressed(pad0.x))
                || (pad1 != null && GamepadRouter.ButtonJustPressed(pad1.x)))
            {
                CoopHudAndResults.OpenChallenges();
                return;
            }

            if (OverlayInput.ConsumeActionClicked("action2"))
            {
                OpenInputModeDropdown();
                return;
            }

            const int gridCols = 8;
            int rosterCount = RosterCount;
            for (int slot = 0; slot < 2; slot++)
            {
                ReadSlotNavigation(slot, out float moveX, out float moveY, out bool confirm, out bool cancel);

                if (OverlayInput.TryGetHoveredGridIndex(slot, out int hGrid)
                    && hGrid >= 0 && hGrid < rosterCount && !ReadyGate.Ready[slot] && SelectedIndex[slot] != hGrid)
                {
                    SelectedIndex[slot] = hGrid;
                }

                if (OverlayInput.TryGetClickedGridIndex(slot, out int cGrid) && cGrid >= 0 && cGrid < rosterCount)
                {
                    if (ReadyGate.Ready[slot] && cGrid == SelectedIndex[slot])
                    {
                        ReadyGate.Cancel(slot);
                    }
                    else
                    {
                        if (ReadyGate.Ready[slot]) ReadyGate.Cancel(slot);
                        SelectedIndex[slot] = cGrid;
                        confirm = true;
                    }
                }

                if (OverlayInput.ConsumeActionClicked("confirm_" + slot))
                {
                    if (ReadyGate.Ready[slot]) ReadyGate.Cancel(slot);
                    else confirm = true;
                }

                if (cancel && !ModalOverlayState.IsCancelConsumedThisFrame())
                {
                    if (ReadyGate.Ready[slot])
                    {
                        ReadyGate.Cancel(slot);
                    }
                    else if (slot == 0)
                    {
                        ModalOverlayState.ConsumeCancelThisFrame();
                        CoopMode.CancelToMainMenu();
                        return;
                    }
                }

                if (ReadyGate.Ready[slot]) continue;

                if (Mathf.Abs(moveX) < 0.35f && Mathf.Abs(moveY) < 0.35f)
                {
                    NextMove[slot] = 0f;
                }
                else if (Time.unscaledTime >= NextMove[slot] && (Mathf.Abs(moveX) > 0.45f || Mathf.Abs(moveY) > 0.45f))
                {
                    int delta = 0;
                    if (Mathf.Abs(moveY) >= Mathf.Abs(moveX) && Mathf.Abs(moveY) > 0.45f)
                        delta = moveY > 0f ? -gridCols : gridCols;
                    else if (Mathf.Abs(moveX) > 0.45f)
                        delta = moveX > 0f ? 1 : -1;

                    if (delta != 0)
                    {
                        SelectedIndex[slot] = (((SelectedIndex[slot] + delta) % rosterCount) + rosterCount) % rosterCount;
                        NextMove[slot] = Time.unscaledTime + 0.16f;
                    }
                }

                if (confirm)
                {
                    int idx = Mathf.Clamp(SelectedIndex[slot], 0, rosterCount - 1);
                    if (IsRandomSlot(idx))
                    {
                        // Random is resolved ONCE when both players confirm (no double-roll!).
                        ReadyGate.Confirm(slot, connected: true);
                    }
                    else
                    {
                        CharType chosen = AvailableTypes[idx - 1];
                        if (CoopDlcAndLevels.Unlocked(chosen))
                        {
                            if (slot == 0) CoopCharacterRuntime.FirstType = chosen;
                            else CoopCharacterRuntime.SecondType = chosen;
                            ReadyGate.Confirm(slot, connected: true);
                        }
                    }
                }
            }

            if (ReadyGate.Complete)
            {
                bool p1Random = IsRandomSlot(SelectedIndex[0]);
                bool p2Random = IsRandomSlot(SelectedIndex[1]);
                if (!p1Random) CoopCharacterRuntime.FirstType = AvailableTypes[Mathf.Clamp(SelectedIndex[0] - 1, 0, AvailableTypes.Count - 1)];
                if (!p2Random) CoopCharacterRuntime.SecondType = AvailableTypes[Mathf.Clamp(SelectedIndex[1] - 1, 0, AvailableTypes.Count - 1)];
                if (p1Random) CoopCharacterRuntime.FirstType = RollRandomUnlockedChar(!p2Random ? CoopCharacterRuntime.SecondType : (CharType?)null);
                if (p2Random) CoopCharacterRuntime.SecondType = RollRandomUnlockedChar(CoopCharacterRuntime.FirstType);

                _stageSelection = true;
                ModalOverlayState.Set(ActiveModalOverlay.StageSelect);
                _armedAt = Time.unscaledTime + 0.28f;
                ReadyGate.Reset();
                NextMove[0] = NextMove[1] = 0f;
            }
        }

        private static void UpdateStageInput()
        {
            CoopDlcAndLevels.PopulateLevels();
            if (CoopDlcAndLevels.Phases.Count == 0) return;

            if (OverlayInput.ConsumeActionClicked("close"))
            {
                ReturnToCharSelect();
                return;
            }
            if (OverlayInput.ConsumeActionClicked("action1") || UnityEngine.Input.GetKeyDown(KeyCode.G) || UnityEngine.Input.GetKeyDown(KeyCode.F2))
            {
                CoopDlcAndLevels.CycleGameSpeed();
                return;
            }
            if (OverlayInput.ConsumeActionClicked("action2"))
            {
                OpenInputModeDropdown();
                return;
            }

            int curPhase = Mathf.Clamp(CoopMode.SelectedPhase, 0, CoopDlcAndLevels.Phases.Count - 1);
            LevelInfo curLevel = CoopDlcAndLevels.Phases[curPhase];
            IGamepadTemplate p0 = GamepadRouter.GetPad(0);
            IGamepadTemplate p1 = GamepadRouter.GetPad(1);

            if (OverlayInput.ConsumeActionClicked("speed_toggle")
                || UnityEngine.Input.GetKeyDown(KeyCode.RightBracket)
                || (p0 != null && (GamepadRouter.ButtonJustPressed(p0.x) || GamepadRouter.ButtonJustPressed(p0.rightBumper)))
                || (p1 != null && (GamepadRouter.ButtonJustPressed(p1.x) || GamepadRouter.ButtonJustPressed(p1.rightBumper))))
            {
                CoopDlcAndLevels.CycleSpeedTier(curLevel, 1);
                return;
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftBracket)
                || (p0 != null && GamepadRouter.ButtonJustPressed(p0.leftBumper))
                || (p1 != null && GamepadRouter.ButtonJustPressed(p1.leftBumper)))
            {
                CoopDlcAndLevels.CycleSpeedTier(curLevel, -1);
                return;
            }
            if (OverlayInput.ConsumeActionClicked("ng_prev")
                || UnityEngine.Input.GetKeyDown(KeyCode.N)
                || (p0 != null && GamepadRouter.ButtonJustPressed(p0.rightTrigger))
                || (p1 != null && GamepadRouter.ButtonJustPressed(p1.rightTrigger)))
            {
                CoopDlcAndLevels.CycleNGPlus(curLevel, 1);
                return;
            }
            if ((p0 != null && GamepadRouter.ButtonJustPressed(p0.leftTrigger))
                || (p1 != null && GamepadRouter.ButtonJustPressed(p1.leftTrigger)))
            {
                CoopDlcAndLevels.CycleNGPlus(curLevel, -1);
                return;
            }

            ReadSlotNavigation(0, out float moveX, out float moveY, out bool confirm, out bool cancel);
            ReadSlotNavigation(1, out float p2MoveX, out float p2MoveY, out bool p2Confirm, out bool p2Cancel);

            if (GamepadRouter.GetPad(0) == null && GamepadRouter.PairingMode == InputPairingMode.P1KeyboardP2Gamepad)
            {
                if (Mathf.Abs(p2MoveX) > Mathf.Abs(moveX)) moveX = p2MoveX;
                if (Mathf.Abs(p2MoveY) > Mathf.Abs(moveY)) moveY = p2MoveY;
                confirm |= p2Confirm;
                cancel |= p2Cancel;
            }

            if (OverlayInput.TryGetHoveredGridIndex(0, out int hStage) && hStage >= 0 && hStage < CoopDlcAndLevels.Phases.Count)
            {
                CoopMode.SelectedPhase = hStage;
            }
            if (OverlayInput.TryGetClickedGridIndex(0, out int cStage) && cStage >= 0 && cStage < CoopDlcAndLevels.Phases.Count)
            {
                CoopMode.SelectedPhase = cStage;
                if (CoopDlcAndLevels.LevelUnlocked(cStage)) confirm = true;
            }
            if (OverlayInput.ConsumeActionClicked("confirm_0")) confirm = true;

            if (cancel && !ModalOverlayState.IsCancelConsumedThisFrame())
            {
                ModalOverlayState.ConsumeCancelThisFrame();
                ReturnToCharSelect();
                return;
            }

            int cols = Math.Min(8, Math.Max(1, CoopDlcAndLevels.Phases.Count));
            if (Mathf.Abs(moveX) < 0.35f && Mathf.Abs(moveY) < 0.35f)
            {
                NextMove[0] = 0f;
            }
            else if (Time.unscaledTime >= NextMove[0] && (Mathf.Abs(moveX) > 0.45f || Mathf.Abs(moveY) > 0.45f))
            {
                int delta = 0;
                if (Mathf.Abs(moveY) >= Mathf.Abs(moveX) && Mathf.Abs(moveY) > 0.45f)
                    delta = moveY > 0f ? -cols : cols;
                else if (Mathf.Abs(moveX) > 0.45f)
                    delta = moveX > 0f ? 1 : -1;

                if (delta != 0)
                {
                    CoopMode.SelectedPhase = (((CoopMode.SelectedPhase + delta) % CoopDlcAndLevels.Phases.Count) + CoopDlcAndLevels.Phases.Count) % CoopDlcAndLevels.Phases.Count;
                    NextMove[0] = Time.unscaledTime + 0.16f;
                }
            }

            if (confirm && CoopDlcAndLevels.LevelUnlocked(CoopMode.SelectedPhase))
            {
                LevelInfo chosenLevel = CoopDlcAndLevels.Phases[CoopMode.SelectedPhase];
                CoopDlcAndLevels.SelectedSpeedTier = CoopDlcAndLevels.ClampSpeedTierFor(chosenLevel, CoopDlcAndLevels.SelectedSpeedTier);
                CoopMode.StartRun(CoopMode.SelectedPhase);
            }
        }

        private static void ReturnToCharSelect()
        {
            _stageSelection = false;
            ModalOverlayState.Set(ActiveModalOverlay.CharSelect);
            _armedAt = Time.unscaledTime + 0.28f;
            ReadyGate.Reset();
            NextMove[0] = NextMove[1] = 0f;
        }

        private static bool IsSettingsHotkeyPressed()
        {
            if (OverlayInput.ConsumeActionClicked("action3")
                || UnityEngine.Input.GetKeyDown(KeyCode.Tab)
                || UnityEngine.Input.GetKeyDown(KeyCode.F3))
            {
                return true;
            }
            IGamepadTemplate p0 = GamepadRouter.GetPad(0);
            IGamepadTemplate p1 = GamepadRouter.GetPad(1);
            return (p0 != null && GamepadRouter.ButtonJustPressed(p0.y))
                || (p1 != null && GamepadRouter.ButtonJustPressed(p1.y));
        }

        private static void OpenInputModeDropdown()
        {
            _settingsOpen = true;
            _settingsIndex = 2;
            _colorDropdownSlot = -1;
            _inputDropdownOpen = true;
            _inputDropdownIndex = GamepadRouter.CurrentInputPresetIndex();
            _inputMoveAt = Time.unscaledTime + 0.16f;
            _armedAt = Time.unscaledTime + 0.12f;
            ModalOverlayState.Set(ActiveModalOverlay.Settings);
        }

        private static void CloseSettings()
        {
            _colorDropdownSlot = -1;
            _inputDropdownOpen = false;
            _settingsOpen = false;
            _armedAt = Time.unscaledTime + 0.16f;
            ModalOverlayState.Set(_stageSelection ? ActiveModalOverlay.StageSelect : ActiveModalOverlay.CharSelect);
        }

        private static void UpdateSettingsInput()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.Tab) || UnityEngine.Input.GetKeyDown(KeyCode.F3) || OverlayInput.ConsumeActionClicked("close"))
            {
                CloseSettings();
                return;
            }

            if (_colorDropdownSlot >= 0)
            {
                // Consume background header clicks while dropdown is open so they don't queue up.
                OverlayInput.ConsumeActionClicked("action1");
                OverlayInput.ConsumeActionClicked("action2");
                OverlayInput.ConsumeActionClicked("action3");
                UpdateColorDropdownInput();
                return;
            }

            if (_inputDropdownOpen)
            {
                OverlayInput.ConsumeActionClicked("action1");
                OverlayInput.ConsumeActionClicked("action3");
                UpdateInputDropdownInput();
                return;
            }

            if (OverlayInput.ConsumeActionClicked("confirm_0") || UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                ModalOverlayState.ConsumeCancelThisFrame();
                CloseSettings();
                return;
            }

            if (OverlayInput.ConsumeActionClicked("action1"))
            {
                CoopBalance.ApplyPreset(2);
                return;
            }
            if (OverlayInput.ConsumeActionClicked("action2"))
            {
                OpenInputModeDropdown();
                return;
            }
            if (OverlayInput.ConsumeActionClicked("action3"))
            {
                CoopBalance.ToggleAutoPickMaxed();
                _settingsIndex = 4;
                return;
            }

            if (OverlayInput.TryGetHoveredOptionIndex(0, out int hovered) && hovered >= 0 && hovered < SettingsRowCount)
            {
                _settingsIndex = hovered;
            }
            if (OverlayInput.TryGetClickedOptionIndex(0, out int clicked) && clicked >= 0 && clicked < SettingsRowCount)
            {
                _settingsIndex = clicked;
                ActivateSettingRow(_settingsIndex, 1);
                return;
            }

            ReadSlotNavigation(0, out float moveX, out float moveY, out bool confirm, out bool cancel);
            ReadSlotNavigation(1, out float p2X, out float p2Y, out bool p2Confirm, out bool p2Cancel);
            if (Mathf.Abs(p2X) > Mathf.Abs(moveX)) moveX = p2X;
            if (Mathf.Abs(p2Y) > Mathf.Abs(moveY)) moveY = p2Y;
            confirm |= p2Confirm;
            cancel |= p2Cancel;

            IGamepadTemplate p0 = GamepadRouter.GetPad(0);
            IGamepadTemplate p1 = GamepadRouter.GetPad(1);
            if ((p0 != null && GamepadRouter.ButtonJustPressed(p0.y))
                || (p1 != null && GamepadRouter.ButtonJustPressed(p1.y))
                || cancel)
            {
                CloseSettings();
                return;
            }

            if (Mathf.Abs(moveX) < 0.35f && Mathf.Abs(moveY) < 0.35f)
            {
                _settingsMoveAt = 0f;
            }
            else if (Time.unscaledTime >= _settingsMoveAt)
            {
                if (Mathf.Abs(moveY) >= Mathf.Abs(moveX) && Mathf.Abs(moveY) > 0.45f)
                {
                    _settingsIndex = (((_settingsIndex + (moveY > 0f ? -1 : 1)) % SettingsRowCount) + SettingsRowCount) % SettingsRowCount;
                    _settingsMoveAt = Time.unscaledTime + 0.16f;
                }
                else if (Mathf.Abs(moveX) > 0.45f)
                {
                    ApplySettingDelta(_settingsIndex, moveX > 0f ? 1 : -1);
                    _settingsMoveAt = Time.unscaledTime + 0.18f;
                }
            }

            if (confirm) ActivateSettingRow(_settingsIndex, 1);
        }

        private static void UpdateColorDropdownInput()
        {
            int count = PlayerPalette.ColorCount;
            if (OverlayInput.ConsumeActionClicked("confirm_1") || UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                _colorDropdownSlot = -1;
                return;
            }
            if (OverlayInput.TryGetClickedOptionIndex(0, out int clickedRow) && clickedRow >= 0 && clickedRow < SettingsRowCount)
            {
                _settingsIndex = clickedRow;
                ActivateSettingRow(_settingsIndex, 1);
                return;
            }
            if (OverlayInput.TryGetHoveredGridIndex(1, out int hSwatch) && hSwatch >= 0 && hSwatch < count)
            {
                _colorDropdownIndex = hSwatch;
            }
            if (OverlayInput.TryGetClickedGridIndex(1, out int cSwatch) && cSwatch >= 0 && cSwatch < count)
            {
                PlayerPalette.SetPlayerColorIndex(_colorDropdownSlot, cSwatch);
                _colorDropdownSlot = -1;
                return;
            }

            ReadSlotNavigation(0, out float moveX, out float moveY, out bool confirm, out bool cancel);
            ReadSlotNavigation(1, out float p2X, out float p2Y, out bool p2Confirm, out bool p2Cancel);
            if (Mathf.Abs(p2X) > Mathf.Abs(moveX)) moveX = p2X;
            if (Mathf.Abs(p2Y) > Mathf.Abs(moveY)) moveY = p2Y;
            confirm |= p2Confirm;
            cancel |= p2Cancel;

            if (cancel) { _colorDropdownSlot = -1; return; }

            int cols = count > 9 ? 5 : 3;
            if (Mathf.Abs(moveX) < 0.35f && Mathf.Abs(moveY) < 0.35f)
            {
                _colorMoveAt = 0f;
            }
            else if (Time.unscaledTime >= _colorMoveAt)
            {
                int delta = 0;
                if (Mathf.Abs(moveY) >= Mathf.Abs(moveX) && Mathf.Abs(moveY) > 0.45f) delta = moveY > 0f ? -cols : cols;
                else if (Mathf.Abs(moveX) > 0.45f) delta = moveX > 0f ? 1 : -1;
                if (delta != 0)
                {
                    _colorDropdownIndex = (((_colorDropdownIndex + delta) % count) + count) % count;
                    _colorMoveAt = Time.unscaledTime + 0.16f;
                }
            }

            if (confirm)
            {
                PlayerPalette.SetPlayerColorIndex(_colorDropdownSlot, _colorDropdownIndex);
                _colorDropdownSlot = -1;
            }
        }

        private static void UpdateInputDropdownInput()
        {
            int count = GamepadRouter.InputPresetCount;
            if (OverlayInput.ConsumeActionClicked("confirm_1")
                || OverlayInput.ConsumeActionClicked("action2")
                || UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                _inputDropdownOpen = false;
                return;
            }
            if (OverlayInput.TryGetClickedOptionIndex(0, out int clickedRow) && clickedRow >= 0 && clickedRow < SettingsRowCount)
            {
                _settingsIndex = clickedRow;
                ActivateSettingRow(_settingsIndex, 1);
                return;
            }
            if (OverlayInput.TryGetHoveredOptionIndex(1, out int hovOpt) && hovOpt >= 0 && hovOpt < count)
            {
                _inputDropdownIndex = hovOpt;
            }
            if (OverlayInput.TryGetClickedOptionIndex(1, out int clkOpt) && clkOpt >= 0 && clkOpt < count)
            {
                GamepadRouter.SetInputPresetByIndex(clkOpt);
                _inputDropdownOpen = false;
                return;
            }

            ReadSlotNavigation(0, out _, out float moveY, out bool confirm, out bool cancel);
            ReadSlotNavigation(1, out _, out float p2Y, out bool p2Confirm, out bool p2Cancel);
            if (Mathf.Abs(p2Y) > Mathf.Abs(moveY)) moveY = p2Y;
            confirm |= p2Confirm;
            cancel |= p2Cancel;

            if (cancel) { _inputDropdownOpen = false; return; }

            if (Mathf.Abs(moveY) < 0.35f) _inputMoveAt = 0f;
            else if (Time.unscaledTime >= _inputMoveAt)
            {
                _inputDropdownIndex = (((_inputDropdownIndex + (moveY > 0f ? -1 : 1)) % count) + count) % count;
                _inputMoveAt = Time.unscaledTime + 0.16f;
            }

            if (confirm)
            {
                GamepadRouter.SetInputPresetByIndex(_inputDropdownIndex);
                _inputDropdownOpen = false;
            }
        }

        private static void ActivateSettingRow(int row, int delta)
        {
            if (row == 2)
            {
                _colorDropdownSlot = -1;
                _inputDropdownOpen = !_inputDropdownOpen;
                if (_inputDropdownOpen)
                {
                    _inputDropdownIndex = GamepadRouter.CurrentInputPresetIndex();
                    _inputMoveAt = Time.unscaledTime + 0.16f;
                }
                return;
            }
            if (row == 5 || row == 6)
            {
                _inputDropdownOpen = false;
                int slot = row == 5 ? 0 : 1;
                if (_colorDropdownSlot == slot) _colorDropdownSlot = -1;
                else
                {
                    _colorDropdownSlot = slot;
                    _colorDropdownIndex = PlayerPalette.GetPlayerColorIndex(slot);
                    _colorMoveAt = Time.unscaledTime + 0.16f;
                }
                return;
            }
            ApplySettingDelta(row, delta);
        }

        private static void ApplySettingDelta(int row, int delta)
        {
            switch (row)
            {
                case 0: CoopBalance.CyclePreset(delta); break;
                case 1: CoopDlcAndLevels.CycleGameSpeed(); break;
                case 2: GamepadRouter.CyclePairingMode(delta); break;
                case 3: GamepadRouter.SharedMouseAimEnabled = !GamepadRouter.SharedMouseAimEnabled; break;
                case 4: CoopBalance.ToggleAutoPickMaxed(); break;
                case 5: PlayerPalette.CyclePlayerColor(0, delta); break;
                case 6: PlayerPalette.CyclePlayerColor(1, delta); break;
            }
        }

        private static void ReadSlotNavigation(int slot, out float moveX, out float moveY, out bool confirm, out bool cancel)
        {
            moveX = 0f;
            moveY = 0f;
            confirm = false;
            cancel = false;

            IGamepadTemplate pad = GamepadRouter.GetPad(slot);
            if (pad != null)
            {
                Vector2 stick = GamepadRouter.MenuStick(pad);
                moveX = stick.x;
                moveY = stick.y;
                confirm = GamepadRouter.ButtonJustPressed(pad.a);
                cancel = GamepadRouter.ButtonJustPressed(pad.b);
            }

            if (slot == 0)
            {
                bool allowArrows = _stageSelection
                    && !GamepadRouter.IsSharedKeyboardMode
                    && GamepadRouter.GetPad(0) == null
                    && GamepadRouter.GetPad(1) == null;
                float kx = (UnityEngine.Input.GetKey(KeyCode.D) || (allowArrows && UnityEngine.Input.GetKey(KeyCode.RightArrow)) ? 1f : 0f)
                         - (UnityEngine.Input.GetKey(KeyCode.A) || (allowArrows && UnityEngine.Input.GetKey(KeyCode.LeftArrow)) ? 1f : 0f);
                float ky = (UnityEngine.Input.GetKey(KeyCode.W) || (allowArrows && UnityEngine.Input.GetKey(KeyCode.UpArrow)) ? 1f : 0f)
                         - (UnityEngine.Input.GetKey(KeyCode.S) || (allowArrows && UnityEngine.Input.GetKey(KeyCode.DownArrow)) ? 1f : 0f);
                if (Mathf.Abs(kx) > Mathf.Abs(moveX)) moveX = kx;
                if (Mathf.Abs(ky) > Mathf.Abs(moveY)) moveY = ky;
                confirm |= UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.Space) || UnityEngine.Input.GetKeyDown(KeyCode.E);
                cancel |= UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Q);
            }
            else if (!GamepadRouter.UsesGamepadForSlot(1) || pad == null)
            {
                float kx = ((UnityEngine.Input.GetKey(ModConfig.KeyMoveRight) || UnityEngine.Input.GetKey(KeyCode.RightArrow)) ? 1f : 0f)
                         - ((UnityEngine.Input.GetKey(ModConfig.KeyMoveLeft) || UnityEngine.Input.GetKey(KeyCode.LeftArrow)) ? 1f : 0f);
                float ky = ((UnityEngine.Input.GetKey(ModConfig.KeyMoveUp) || UnityEngine.Input.GetKey(KeyCode.UpArrow)) ? 1f : 0f)
                         - ((UnityEngine.Input.GetKey(ModConfig.KeyMoveDown) || UnityEngine.Input.GetKey(KeyCode.DownArrow)) ? 1f : 0f);
                if (Mathf.Abs(kx) > Mathf.Abs(moveX)) moveX = kx;
                if (Mathf.Abs(ky) > Mathf.Abs(moveY)) moveY = ky;
                confirm |= UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter) || UnityEngine.Input.GetKeyDown(KeyCode.RightControl) || UnityEngine.Input.GetKeyDown(KeyCode.Slash);
                cancel |= UnityEngine.Input.GetKeyDown(KeyCode.Backspace) || UnityEngine.Input.GetKeyDown(KeyCode.RightShift);
            }
        }

        // --- Rendering ---

        private static void RenderCharacterSelect()
        {
            int rosterCount = RosterCount;
            if (_cachedRosterSprites.Length != rosterCount)
            {
                _cachedRosterSprites = new Sprite[rosterCount];
                _cachedRosterUnlocked = new bool[rosterCount];
                _cachedRosterBadges = new string[rosterCount];
            }

            _cachedRosterSprites[0] = EnsureRandomPortraitSprite();
            _cachedRosterUnlocked[0] = true;
            _cachedRosterBadges[0] = "?";
            for (int i = 0; i < AvailableTypes.Count; i++)
            {
                CharType ct = AvailableTypes[i];
                CharInfo info = GetCharInfo(ct);
                bool isCharUnlocked = CoopDlcAndLevels.Unlocked(ct);
                _cachedRosterSprites[i + 1] = info?.GetIconSprite();
                _cachedRosterUnlocked[i + 1] = isCharUnlocked;
                if (isCharUnlocked)
                {
                    CharMetaInst meta = CoopProgression.FindCharMeta(ct);
                    _cachedRosterBadges[i + 1] = meta != null && meta.Lvl > 0 ? $"Lv.{meta.Lvl}" : string.Empty;
                }
                else
                {
                    _cachedRosterBadges[i + 1] = string.Empty;
                }
            }

            ChoiceOverlay.Show(
                CoopLocalization.Text("LOCAL CO-OP - CHARACTER SELECT", "CO-OP LOCAL - CHOIX DU PERSONNAGE"),
                CoopLocalization.Text(
                    "Stick / Click: Select   -   [A]: Ready   -   [Y/Tab]: Settings   -   [C]: Challenges",
                    "Stick / Clic : Choisir   -   [A] : Prêt   -   [Y/Tab] : Options   -   [C] : Défis"));

            ChoiceOverlay.SetHeaderButtons(
                CoopLocalization.Text("Challenges [C]", "Défis [C]"),
                CoopLocalization.Text("Input: ", "Entrées : ") + GamepadRouter.PairingModeSummary(),
                showClose: true,
                action3Text: CoopLocalization.Text("Settings [Tab/Y]", "Options [Tab/Y]"));

            for (int slot = 0; slot < 2; slot++)
            {
                int idx = Mathf.Clamp(SelectedIndex[slot], 0, rosterCount - 1);
                SelectedIndex[slot] = idx;

                string title;
                Sprite cardSprite;
                string profile;
                bool isUnlocked = _cachedRosterUnlocked[idx];
                if (IsRandomSlot(idx))
                {
                    title = CoopLocalization.Text("Random (?)", "Aléatoire (?)");
                    cardSprite = _cachedRosterSprites[0];
                    profile = BuildRandomProfile();
                }
                else
                {
                    CharType chosen = AvailableTypes[idx - 1];
                    CharInfo info = GetCharInfo(chosen);
                    string lockedTag = CoopLocalization.Text(" <color=#F2706B>(LOCKED)</color>", " <color=#F2706B>(VERROUILLÉ)</color>");
                    title = info != null
                        ? CharacterDisplayName(info) + (isUnlocked ? "" : lockedTag)
                        : CoopLocalization.PrettifyEnumName(chosen.ToString());
                    cardSprite = info?.GetIconSprite();
                    profile = Profile(info, slot);
                }

                ChoiceOverlay.SetPlayerHeader(slot, $"{CoopLocalization.Text("PLAYER", "JOUEUR")} {slot + 1}  |  {GamepadRouter.SlotInputLabel(slot)}");
                ChoiceOverlay.Card(slot, title, ReadyGate.Ready[slot], cardSprite);
                ChoiceOverlay.CharacterGrid(slot, _cachedRosterSprites, _cachedRosterUnlocked, idx, level: false, profile: profile, tileBadges: _cachedRosterBadges);
                ChoiceOverlay.SetCardConfirmButton(
                    slot,
                    ReadyGate.Ready[slot]
                        ? CoopLocalization.Text("Ready! (Click to Change)", "Prêt ! (Modifier)")
                        : (isUnlocked ? CoopLocalization.Text("Lock In Character", "Valider le personnage") : CoopLocalization.Text("Locked", "Verrouillé")),
                    active: ReadyGate.Ready[slot],
                    enabled: isUnlocked);
            }
        }

        private static void RenderStageSelect()
        {
            CoopDlcAndLevels.PopulateLevels();
            int phaseCount = CoopDlcAndLevels.Phases.Count;
            if (phaseCount == 0) return;

            int phaseIndex = Mathf.Clamp(CoopMode.SelectedPhase, 0, phaseCount - 1);
            CoopMode.SelectedPhase = phaseIndex;

            if (_cachedStageSprites.Length != phaseCount)
            {
                _cachedStageSprites = new Sprite[phaseCount];
                _cachedStageUnlocked = new bool[phaseCount];
                _cachedStageBadges = new string[phaseCount];
            }
            for (int i = 0; i < phaseCount; i++)
            {
                LevelInfo lvl = CoopDlcAndLevels.Phases[i];
                _cachedStageSprites[i] = lvl?.SprPreview ?? lvl?.SprIcon;
                _cachedStageUnlocked[i] = CoopDlcAndLevels.LevelUnlocked(i);
                _cachedStageBadges[i] = FormatStageTileBadge(lvl, _cachedStageUnlocked[i]);
            }

            LevelInfo cur = CoopDlcAndLevels.Phases[phaseIndex];
            int maxNg = CoopDlcAndLevels.MaxNGPlusFor(cur);
            CoopDlcAndLevels.SelectedNGPlus = Mathf.Clamp(CoopDlcAndLevels.SelectedNGPlus, 0, maxNg);
            int maxSpeedTier = CoopDlcAndLevels.MaxSpeedTierFor(cur);
            int speedTier = CoopDlcAndLevels.SelectedSpeedTier = CoopDlcAndLevels.ClampSpeedTierFor(cur, CoopDlcAndLevels.SelectedSpeedTier);
            int bestTier = CoopDlcAndLevels.BestCompletedSpeedTierFor(cur);
            int fastTrophies = CoopDlcAndLevels.LevelFastTiersCompleted(cur);
            int curBlueprints = CoopDlcAndLevels.AvailableBlueprintsForLevel(cur);
            int p1BestTier = CoopDlcAndLevels.CharBestFastTierForLevel(cur, CoopCharacterRuntime.FirstType);
            int p2BestTier = CoopDlcAndLevels.CharBestFastTierForLevel(cur, CoopCharacterRuntime.SecondType);

            string ngLabel = maxNg > 0 ? (CoopDlcAndLevels.SelectedNGPlus == 0 ? "NG+: 0 [N]" : $"NG+: {CoopDlcAndLevels.SelectedNGPlus}/{maxNg} [N]") : null;
            string speedTierLabel = CoopDlcAndLevels.SpeedTierLabel(speedTier) + (maxSpeedTier > 1 ? " [X]" : "");
            string simSpeedLabel = CoopDlcAndLevels.GameSpeedLabel() + " [G]";

            string stageName = CoopLocalization.StageName(cur);
            bool isDlc = cur != null && CoopDlcAndLevels.SafeCanAccessBallbylonDLC() && CoopDlcAndLevels.SafeIsDLCLevel(cur.Type);
            bool cleared = CoopDlcAndLevels.IsLevelCleared(cur);
            string p1Name = CharacterDisplayName(GetCharInfo(CoopCharacterRuntime.FirstType));
            string p2Name = CharacterDisplayName(GetCharInfo(CoopCharacterRuntime.SecondType));

            float durationSec = CoopDlcAndLevels.SpeedTierDurationSeconds(speedTier);
            int durationMin = Mathf.FloorToInt(durationSec / 60f);
            int durationRem = Mathf.FloorToInt(durationSec % 60f);
            int goldPct = CoopDlcAndLevels.SpeedTierGoldBonusPct(speedTier);

            var descSb = new StringBuilder();
            if (!_cachedStageUnlocked[phaseIndex])
            {
                descSb.Append("<color=#F2706B><b>").Append(CoopLocalization.Text("Status: Locked", "Statut : Verrouillé")).Append("</b> - ")
                      .Append(CoopLocalization.Text("Clear the previous stage to unlock", "Terminez le niveau précédent pour débloquer")).Append("</color>");
            }
            else if (cleared)
            {
                descSb.Append("<color=#F4C86A><b>").Append(CoopLocalization.Text("Status: Cleared *", "Statut : Terminé *")).Append("</b></color>  |  ")
                      .Append("<color=#78E69C>").Append(CoopLocalization.Text("Best Tier: ", "Meilleur palier : ")).Append(CoopDlcAndLevels.SpeedTierName(Math.Max(1, bestTier))).Append("</color>");
                if (fastTrophies > 0)
                {
                    descSb.Append("  |  <color=#5CD6FF>").Append(CoopLocalization.Text("Fast Trophies: ", "Trophées Rapide : ")).Append(fastTrophies).Append("</color>");
                }
            }
            else
            {
                descSb.Append("<color=#78E69C><b>").Append(CoopLocalization.Text("Status: Unlocked", "Statut : Débloqué")).Append("</b></color>  |  ")
                      .Append(CoopLocalization.Text("Clear Normal to unlock Fast modes", "Terminez en Normal pour débloquer Rapide"));
            }

            string bpLine = curBlueprints > 0
                ? $"<color=#F4C86A>{CoopLocalization.Text("Blueprints Remaining:", "Plans restants :")} <b>{curBlueprints}</b></color>"
                : $"<color=#78E69C>{CoopLocalization.Text("Blueprints:", "Plans :")} <b>{CoopLocalization.Text("All Collected *", "Tous récupérés *")}</b></color>";
            string p1Hex = PlayerPalette.GetPlayerColorHex(0);
            string p2Hex = PlayerPalette.GetPlayerColorHex(1);
            string p1Trophy = p1BestTier > 0 ? CoopDlcAndLevels.SpeedTierName(p1BestTier) : CoopLocalization.Text("None", "Aucun");
            string p2Trophy = p2BestTier > 0 ? CoopDlcAndLevels.SpeedTierName(p2BestTier) : CoopLocalization.Text("None", "Aucun");
            descSb.Append('\n').Append(bpLine)
                  .Append($"   |   <color=#{p1Hex}>P1: ").Append(p1Trophy).Append($"</color>   |   <color=#{p2Hex}>P2: ").Append(p2Trophy).Append("</color>");

            if (isDlc)
            {
                descSb.Append("\n<color=#5CD6FF><b>").Append(CoopLocalization.Text("Expansion:", "Extension :")).Append("</b> Ballbylon Rise DLC</color>");
            }
            descSb.Append("\n<color=#F4C86A>").Append(CoopLocalization.Text("Stage Speed:", "Palier vitesse :")).Append("</color> <b>")
                  .Append(CoopDlcAndLevels.SpeedTierName(speedTier)).Append($"</b> ({durationMin:D2}:{durationRem:D2}")
                  .Append(goldPct > 0 ? $" - +{goldPct}% Gold" : "")
                  .Append(maxSpeedTier > 1 ? $" - {CoopLocalization.Text("Max:", "Max :")} {CoopDlcAndLevels.SpeedTierName(maxSpeedTier)}" : "")
                  .Append(")")
                  .Append(maxNg > 0 ? $"   |   NG+{CoopDlcAndLevels.SelectedNGPlus}" : "")
                  .Append("   |   ").Append(CoopBalance.CurrentPresetLabel());
            descSb.Append($"\n<color=#{p1Hex}>").Append(CoopLocalization.Text("P1", "J1")).Append(' ').Append(p1Name).Append($"</color>   |   <color=#{p2Hex}>")
                  .Append(CoopLocalization.Text("P2", "J2")).Append(' ').Append(p2Name).Append("</color>");

            ChoiceOverlay.Show(
                CoopLocalization.Text("STAGE SELECT", "CHOIX DU NIVEAU"),
                CoopLocalization.Text(
                    "Click / Stick: Stage   -   [A]: Launch   -   [X]: Tier   -   [N/RT]: NG+   -   [G]: Speed   -   [B]: Back",
                    "Clic / Stick : Niveau   -   [A] : Lancer   -   [X] : Palier   -   [N/RT] : NG+   -   [G] : Vitesse   -   [B] : Retour"));

            ChoiceOverlay.SetHeaderButtons(
                simSpeedLabel,
                CoopLocalization.Text("Input: ", "Entrées : ") + GamepadRouter.PairingModeSummary(),
                showClose: true,
                action3Text: CoopLocalization.Text("Settings [Tab/Y]", "Options [Tab/Y]"));

            ChoiceOverlay.SingleStage(
                stageName,
                _cachedStageUnlocked[phaseIndex],
                cleared,
                isDlc,
                cur?.SprPreview ?? cur?.SprIcon,
                _cachedStageSprites,
                _cachedStageUnlocked,
                phaseIndex,
                descSb.ToString(),
                _cachedStageBadges);

            ChoiceOverlay.SetStageExtraButtons(ngLabel, speedTierLabel);
            ChoiceOverlay.SetCardConfirmButton(
                0,
                _cachedStageUnlocked[phaseIndex]
                    ? CoopLocalization.Text("Launch Co-op Run", "Lancer la partie")
                    : CoopLocalization.Text("Stage Locked", "Niveau verrouillé"),
                active: _cachedStageUnlocked[phaseIndex],
                enabled: _cachedStageUnlocked[phaseIndex]);
        }

        private static void RenderSettings()
        {
            EnsureColorSwatches();
            int activePreset = CoopBalance.CurrentPresetIndex();

            ChoiceOverlay.Show(
                CoopLocalization.Text("CO-OP SETTINGS", "PARAMÈTRES CO-OP"),
                CoopLocalization.Text(
                    "Up/Down or Wheel: Browse   -   Left/Right/[A]: Change   -   [B]/Esc: Back",
                    "Haut/Bas ou Molette : Choisir   -   Gauche/Droite/[A] : Modifier   -   [B]/Échap : Retour"));

            string autoPickBadge = CoopLocalization.Text("Auto-Pick: ", "Auto-choisir : ")
                + (CoopBalance.AutoPickMaxed ? CoopLocalization.Text("ON", "OUI") : CoopLocalization.Text("OFF", "NON"));

            ChoiceOverlay.SetHeaderButtons(
                CoopLocalization.Text("Reset Default", "Réinit. Défaut"),
                CoopLocalization.Text("Input: ", "Entrées : ") + GamepadRouter.PairingModeSummary(),
                showClose: true,
                action3Text: autoPickBadge);

            string p1Hex = PlayerPalette.GetPlayerColorHex(0);
            string p2Hex = PlayerPalette.GetPlayerColorHex(1);
            string p1ColorName = PlayerPalette.GetPlayerColorName(0, CoopLocalization.IsFrench);
            string p2ColorName = PlayerPalette.GetPlayerColorName(1, CoopLocalization.IsFrench);

            var summary = new StringBuilder();
            summary.Append("<color=#F4C86A><b>").Append(CoopLocalization.Text("Difficulty:", "Difficulté :")).Append("</b></color> <color=#78E69C><b>")
                   .Append(CoopBalance.CurrentPresetLabel()).Append("</b></color>   -   <color=#F4C86A><b>")
                   .Append(CoopLocalization.Text("Speed:", "Vitesse :")).Append("</b></color> ").Append(CoopDlcAndLevels.GameSpeedLabel())
                   .Append("\n<color=#F4C86A><b>").Append(CoopLocalization.Text("Colors:", "Couleurs :")).Append($"</b></color> <color=#{p1Hex}><b>P1 {p1ColorName}</b></color>  -  <color=#{p2Hex}><b>P2 {p2ColorName}</b></color>   -   ")
                   .Append(CoopLocalization.Text("Auto-Pick:", "Auto-choisir :")).Append(' ')
                   .Append(CoopBalance.AutoPickMaxed ? "<color=#78E69C><b>" + CoopLocalization.Text("ON", "OUI") + "</b></color>" : "<color=#B8A69B>" + CoopLocalization.Text("OFF", "NON") + "</color>")
                   .Append("\n<color=#5CD6FF>").Append(CoopBalance.PresetDescription(activePreset)).Append("</color>");

            string mouseAimVal = GamepadRouter.SharedMouseAimEnabled
                ? (GamepadRouter.IsSharedKeyboardMode ? CoopLocalization.Text("ON (Both Mouse)", "OUI (Souris J1+J2)") : CoopLocalization.Text("ON (When KB+Arrows)", "OUI (Si Clavier+Flèches)"))
                : CoopLocalization.Text("OFF (P2 J/I/L)", "NON (J2 J/I/L)");
            string autoPickVal = CoopBalance.AutoPickMaxed
                ? CoopLocalization.Text("ON (Coin/Heal)", "OUI (Or/Soin)")
                : CoopLocalization.Text("OFF (Prompt)", "NON (Choix)");

            string[] rows =
            {
                $"{CoopLocalization.Text("Co-op Difficulty:", "Difficulté Co-op :")}  < {CoopBalance.CurrentPresetLabel()} >",
                $"{CoopLocalization.Text("Game Speed:", "Vitesse du jeu :")}  < {CoopDlcAndLevels.GameSpeedLabel()} >",
                $"{CoopLocalization.Text("Input Mode:", "Mode d'entrées :")}  [{GamepadRouter.InputPresetLabel(GamepadRouter.CurrentInputPresetIndex())}]",
                $"{CoopLocalization.Text("Shared Mouse Aim (KB+Arrows):", "Visée Souris 2J (Clavier) :")}  < {mouseAimVal} >",
                $"{CoopLocalization.Text("Auto-Pick Maxed:", "Auto-choisir si plein :")}  < {autoPickVal} >",
                $"{CoopLocalization.Text("Player 1 Color:", "Couleur Joueur 1 :")}  <color=#{p1Hex}><b>[{p1ColorName}]</b></color>",
                $"{CoopLocalization.Text("Player 2 Color:", "Couleur Joueur 2 :")}  <color=#{p2Hex}><b>[{p2ColorName}]</b></color>"
            };

            ChoiceOverlay.SinglePrompt(
                heading: string.Empty,
                statusText: CoopLocalization.Text("BepInEx Sync", "Config BepInEx"),
                statusColor: new Color(0.36f, 0.78f, 0.96f, 1f),
                summaryText: summary.ToString(),
                options: rows,
                selected: _settingsIndex,
                confirmText: CoopLocalization.Text("Save & Back [B]", "Valider [B / Échap]"),
                cardHeading: CoopLocalization.Text("CO-OP MOD SETTINGS", "OPTIONS DU MOD CO-OP"));

            if (_inputDropdownOpen)
            {
                int count = GamepadRouter.InputPresetCount;
                int activeInputIdx = GamepadRouter.CurrentInputPresetIndex();
                string[] presetRows = new string[count];
                for (int p = 0; p < count; p++)
                {
                    presetRows[p] = (p == activeInputIdx ? "* " : "  ") + GamepadRouter.InputPresetLabel(p);
                }
                ChoiceOverlay.SettingsListDropdown(
                    CoopLocalization.Text("INPUT MODE SELECT", "CHOIX DES ENTRÉES"),
                    GamepadRouter.InputPresetLabel(_inputDropdownIndex),
                    GamepadRouter.PairingModeSummary(),
                    presetRows,
                    _inputDropdownIndex,
                    GamepadRouter.InputPresetDescription(_inputDropdownIndex),
                    CoopLocalization.Text("Close Dropdown [B / Esc]", "Fermer [B / Échap]"));
            }
            else if (_colorDropdownSlot >= 0)
            {
                int count = PlayerPalette.ColorCount;
                string[] colorNames = new string[count];
                for (int c = 0; c < count; c++) colorNames[c] = PlayerPalette.ColorName(c, CoopLocalization.IsFrench);

                string dropdownHeading = _colorDropdownSlot == 0
                    ? CoopLocalization.Text("PLAYER 1 COLOR", "COULEUR JOUEUR 1")
                    : CoopLocalization.Text("PLAYER 2 COLOR", "COULEUR JOUEUR 2");
                ChoiceOverlay.SettingsColorDropdown(
                    _colorDropdownSlot,
                    ColorSwatchSprites,
                    colorNames,
                    _colorDropdownIndex,
                    dropdownHeading,
                    PlayerPalette.ColorName(_colorDropdownIndex, CoopLocalization.IsFrench),
                    CoopLocalization.Text("Close Dropdown [B / Esc]", "Fermer [B / Échap]"));
            }
        }

        // --- Display Formatting & Procedural Sprites ---

        private static string FormatStageTileBadge(LevelInfo lvl, bool isUnlocked)
        {
            if (lvl == null || !isUnlocked) return string.Empty;
            int p1T = CoopDlcAndLevels.CharBestFastTierForLevel(lvl, CoopCharacterRuntime.FirstType);
            int p2T = CoopDlcAndLevels.CharBestFastTierForLevel(lvl, CoopCharacterRuntime.SecondType);
            int charBest = Math.Max(p1T, p2T);
            if (charBest > 1) return $"OK*{charBest - 1}";
            return charBest == 1 ? "OK" : string.Empty;
        }

        public static string CharacterDisplayName(CharInfo info)
        {
            if (info == null) return CoopLocalization.Text("Unknown", "Inconnu");
            try
            {
                string term = info.GetNameSlug();
                string slugged = CoopLocalization.Translate(term);
                if (IsValidCharNameCandidate(slugged, term)) return slugged.Trim();
            }
            catch { }
            if (IsValidCharNameCandidate(info.Name, null)) return info.Name.Trim();
            if (info.Type == CharType.kDefault) return CoopLocalization.Text("The Warrior", "Le Guerrier");
            return CoopLocalization.PrettifyEnumName(info.Type.ToString());
        }

        private static bool IsValidCharNameCandidate(string candidate, string slugTerm)
        {
            if (!CoopLocalization.IsValidTranslation(candidate)) return false;
            string trimmed = candidate.Trim();
            if (!string.IsNullOrEmpty(slugTerm) && string.Equals(trimmed, slugTerm, StringComparison.OrdinalIgnoreCase)) return false;
            if (trimmed.StartsWith("char/", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("char_", StringComparison.OrdinalIgnoreCase)
                || (trimmed.StartsWith("k", StringComparison.Ordinal) && trimmed.Length > 2 && char.IsUpper(trimmed[1]) && !trimmed.Contains(" ")))
            {
                return false;
            }
            return true;
        }

        private static string ResolveCharacterDescription(CharInfo info)
        {
            if (info == null) return string.Empty;
            string[] candidates =
            {
                TrySafe(() => CoopLocalization.Translate(info.GetGameplayDescSlug())),
                info.GameplayDesc,
                TrySafe(() => CoopLocalization.Translate(info.GetFlavorDescSlug())),
                info.FlavorDesc
            };
            foreach (string raw in candidates)
            {
                if (IsValidCharNameCandidate(raw, info.Slug) && !raw.Contains("_desc", StringComparison.OrdinalIgnoreCase))
                {
                    string clean = raw.Replace("\r", " ").Replace("\n", " ").Trim();
                    return clean.Length > 135 ? clean.Substring(0, 132).TrimEnd() + "..." : clean;
                }
            }
            return string.Empty;
        }

        private static string TrySafe(Func<string> getter)
        {
            try { return getter(); } catch { return string.Empty; }
        }

        private static string BuildRandomProfile()
        {
            int unlockedCount = 0;
            for (int i = 0; i < AvailableTypes.Count; i++)
            {
                if (CoopDlcAndLevels.Unlocked(AvailableTypes[i])) unlockedCount++;
            }
            return $"<color=#F4C86A><b>{CoopLocalization.Text("Starter Ball: Surprise!", "Balle de départ : Surprise !")}</b></color>  |  " +
                   $"<color=#5CD6FF>{CoopLocalization.Text($"Pool: {unlockedCount} Unlocked", $"Pool : {unlockedCount} débloqués")}</color>\n" +
                   CoopLocalization.Text(
                       "Randomly drafts any unlocked character (avoiding duplicates with your partner when possible).",
                       "Choisit aléatoirement n'importe quel personnage débloqué (évite les doublons avec votre partenaire).") +
                   $"\n<color=#78E69C>{CoopLocalization.Text("Duo Synergy: Wildcard Kit", "Synergie Duo : Équipement surprise")}</color>";
        }

        public static string Profile(CharInfo info, int slot)
        {
            if (info == null) return string.Empty;
            string starter = string.Empty;
            try
            {
                UpgradeInfo su = info.StarterUpgrade;
                if (su != null)
                {
                    string localized = CoopLocalization.Translate(su.GetNameSlug());
                    starter = CoopLocalization.IsValidTranslation(localized) ? localized : su.Name;
                }
            }
            catch { }

            string desc = ResolveCharacterDescription(info);
            string levelExp = BuildLevelExpLine(info);
            string coreAttrs = BuildCoreAttributeLine(info);
            string warnings = BuildDisruptiveWarnings(info);
            string synergy = BuildDuoSynergyLine(info, slot);

            var sb = new StringBuilder();
            if (!CoopDlcAndLevels.Unlocked(info.Type))
            {
                sb.Append($"<color=#F2706B><b>{CoopLocalization.Text("Locked - ", "Verrouillé - ")}{CoopDlcAndLevels.CharacterRequirement(info.Type)}</b></color>");
            }
            else
            {
                sb.Append(string.IsNullOrEmpty(starter)
                    ? $"<color=#F4C86A><b>{CoopLocalization.Text("Starter: Standard", "Départ : Standard")}</b></color>"
                    : $"<color=#F4C86A><b>{CoopLocalization.Text("Starter:", "Départ :")} {starter}</b></color>");
            }

            if (!string.IsNullOrEmpty(levelExp)) sb.Append("  |  ").Append(levelExp);
            if (!string.IsNullOrEmpty(coreAttrs)) sb.Append("\n<color=#5CD6FF>").Append(coreAttrs).Append("</color>");
            if (!string.IsNullOrEmpty(warnings)) sb.Append("\n<color=#FF8C52><b>").Append(warnings).Append("</b></color>");
            if (!string.IsNullOrEmpty(desc)) sb.Append('\n').Append(desc);
            if (!string.IsNullOrEmpty(synergy)) sb.Append("\n<color=#78E69C>").Append(synergy).Append("</color>");
            return sb.ToString();
        }

        private static string BuildLevelExpLine(CharInfo info)
        {
            if (info == null || !CoopDlcAndLevels.Unlocked(info.Type)) return string.Empty;
            try
            {
                CharMetaInst meta = CoopProgression.FindCharMeta(info.Type);
                if (meta == null) return string.Empty;
                int lvl = Math.Max(1, meta.Lvl);
                int tgtXp = meta.GetTgtXP(lvl);
                if (lvl >= CharMetaInst.kMaxLvl || tgtXp <= 0)
                {
                    return $"<color=#F4C86A><b>Lv.{lvl}</b></color> <color=#78E69C>(MAX)</color>";
                }
                int curXp = Math.Max(0, meta.CurXP);
                int pct = Mathf.Clamp(Mathf.RoundToInt(meta.GetXPPct() * 100f), 0, 100);
                return $"<color=#F4C86A><b>Lv.{lvl}</b></color>  <color=#B8A69B>EXP {curXp}/{tgtXp} ({pct}%)</color>";
            }
            catch { return string.Empty; }
        }

        private static string BuildCoreAttributeLine(CharInfo info)
        {
            if (info == null) return string.Empty;
            try
            {
                var stats = info.Stats;
                if (stats == null || stats.Length == 0) return string.Empty;
                // Display all 6 character attributes (END, STR, LDR, SPD, DEX, INT)!
                string[] labels = { "END", "STR", "LDR", "SPD", "DEX", "INT" };
                int count = Math.Min(labels.Length, stats.Length);
                var parts = new List<string>(count);
                for (int i = 0; i < count; i++) parts.Add($"{labels[i]} {stats[i]}");
                return string.Join("  ", parts);
            }
            catch { return string.Empty; }
        }

        private static string BuildDisruptiveWarnings(CharInfo info)
        {
            if (info == null) return string.Empty;
            var tags = new List<string>();
            try
            {
                if (info.Type == CharType.kTactician || info.GetMoveMode() == MoveMode.kOnlyDodge)
                    tags.Add(CoopLocalization.Text("! TURN-BASED MOVE", "! DÉPLACEMENT AU TOUR"));
                if (info.Type == CharType.kCogitator || info.HasProp(CharProp.kAutopickUpgrades))
                    tags.Add(CoopLocalization.Text("! AUTO-PICKS UPGRADES", "! AMÉLIOS AUTO"));
                if (info.HasProp(CharProp.kAI) || info.Type == CharType.kRadicalAI)
                    tags.Add(CoopLocalization.Text("! AI AUTOPILOT", "! PILOTE AUTO IA"));
                if (info.HasProp(CharProp.kNoBallDamage))
                    tags.Add(CoopLocalization.Text("! NO BALL HIT DMG", "! BALLES SANS DÉGÂTS"));
                if (info.HasProp(CharProp.kAlwaysShoot))
                    tags.Add(CoopLocalization.Text("! ALWAYS FIRING", "! TIR CONTINU"));
                if (info.HasProp(CharProp.kAimless))
                    tags.Add(CoopLocalization.Text("! RANDOM AIM", "! VISÉE ALÉATOIRE"));
                if (info.HasProp(CharProp.kDoublePassives))
                    tags.Add(CoopLocalization.Text("! 2x PASSIVES", "! 2x PASSIFS"));
                if (info.HasProp(CharProp.kMultiHero) || info.HasProp(CharProp.kMirror))
                    tags.Add(CoopLocalization.Text("! DUAL BODY / MIRROR", "! DOUBLE CORPS"));
            }
            catch { }
            return tags.Count > 0 ? string.Join("  -  ", tags) : string.Empty;
        }

        private static CharInfo GetHoveredCharInfo(int slot)
        {
            int idx = Mathf.Clamp(SelectedIndex[slot], 0, RosterCount - 1);
            if (IsRandomSlot(idx) || idx - 1 < 0 || idx - 1 >= AvailableTypes.Count) return null;
            return GetCharInfo(AvailableTypes[idx - 1]);
        }

        private static string BuildDuoSynergyLine(CharInfo info, int slot)
        {
            if (info == null) return string.Empty;
            CharInfo partner = GetHoveredCharInfo(1 - slot);
            if (partner == null)
            {
                return CoopLocalization.Text("Duo Synergy: Flexible with Random partner", "Synergie Duo : Polyvalent avec Aléatoire");
            }
            try
            {
                var notes = new List<string>(2);
                if (info.Type == partner.Type) notes.Add(CoopLocalization.Text("Mirror Duo", "Duo Miroir"));
                bool p1Auto = info.HasProp(CharProp.kAI) || info.HasProp(CharProp.kAutopickUpgrades);
                bool p2Auto = partner.HasProp(CharProp.kAI) || partner.HasProp(CharProp.kAutopickUpgrades);
                if (p1Auto && p2Auto) notes.Add(CoopLocalization.Text("Double Chaos (Full Auto)", "Double chaos (Auto)"));
                else if (p1Auto ^ p2Auto) notes.Add(CoopLocalization.Text("Manual Anchor + Auto", "Manuel + Automatique"));

                int endA = info.Stats != null && info.Stats.Length > 0 ? info.Stats[0] : 5;
                int strA = info.Stats != null && info.Stats.Length > 1 ? info.Stats[1] : 5;
                int ldrA = info.Stats != null && info.Stats.Length > 2 ? info.Stats[2] : 5;
                int endB = partner.Stats != null && partner.Stats.Length > 0 ? partner.Stats[0] : 5;
                int strB = partner.Stats != null && partner.Stats.Length > 1 ? partner.Stats[1] : 5;
                int ldrB = partner.Stats != null && partner.Stats.Length > 2 ? partner.Stats[2] : 5;

                if (endA >= endB + 2 && strB >= strA + 2) notes.Add(CoopLocalization.Text("Frontline Tank + Carry", "Tank + Dégâts"));
                else if (strA >= strB + 2 && endB >= endA + 2) notes.Add(CoopLocalization.Text("Glass Cannon + Tank", "Dégâts + Tank"));
                else if (ldrA >= 7 || ldrB >= 7) notes.Add(CoopLocalization.Text("Strong Baby Swarm", "Essaim d'alliés"));
                else if (notes.Count == 0) notes.Add(CoopLocalization.Text("Balanced Firepower", "Puissance équilibrée"));

                return $"Duo w/ {CharacterDisplayName(partner)}: {string.Join(" - ", notes)}";
            }
            catch { return string.Empty; }
        }

        private static Sprite EnsureRandomPortraitSprite()
        {
            if (_randomSprite != null) return _randomSprite;
            try
            {
                const int size = 64;
                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.hideFlags = HideFlags.HideAndDontSave;
                tex.filterMode = FilterMode.Point;
                Color bg = new Color(0.14f, 0.08f, 0.13f, 1f);
                Color rim = new Color(0.84f, 0.65f, 0.28f, 1f);
                Color glyph = new Color(0.98f, 0.85f, 0.42f, 1f);
                Color shadow = new Color(0.06f, 0.03f, 0.06f, 1f);
                Color[] pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        bool border = x <= 1 || x >= size - 2 || y <= 1 || y >= size - 2;
                        pixels[y * size + x] = border ? rim : bg;
                    }
                }
                string[] q = { ".#####.", "##...##", ".....##", "....##.", "...##..", "...##..", ".......", "...##..", "...##.." };
                const int scale = 4;
                int ox = (size - q[0].Length * scale) / 2;
                int oy = (size - q.Length * scale) / 2;
                for (int row = 0; row < q.Length; row++)
                {
                    for (int col = 0; col < q[row].Length; col++)
                    {
                        if (q[row][col] != '#') continue;
                        int pyTop = size - 1 - (oy + row * scale);
                        int pxLeft = ox + col * scale;
                        for (int dy = 0; dy < scale; dy++)
                            for (int dx = 0; dx < scale; dx++)
                            {
                                int sx = pxLeft + dx + 2, sy = pyTop - dy - 2;
                                if (sx >= 2 && sx < size - 2 && sy >= 2 && sy < size - 2) pixels[sy * size + sx] = shadow;
                            }
                    }
                }
                for (int row = 0; row < q.Length; row++)
                {
                    for (int col = 0; col < q[row].Length; col++)
                    {
                        if (q[row][col] != '#') continue;
                        int pyTop = size - 1 - (oy + row * scale);
                        int pxLeft = ox + col * scale;
                        for (int dy = 0; dy < scale; dy++)
                            for (int dx = 0; dx < scale; dx++)
                            {
                                int px = pxLeft + dx, py = pyTop - dy;
                                if (px >= 2 && px < size - 2 && py >= 2 && py < size - 2) pixels[py * size + px] = glyph;
                            }
                    }
                }
                tex.SetPixels(pixels);
                tex.Apply(false, false);
                _randomTex = tex;
                _randomSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 64f);
                _randomSprite.hideFlags = HideFlags.HideAndDontSave;
            }
            catch { }
            return _randomSprite;
        }

        private static void EnsureColorSwatches()
        {
            int count = PlayerPalette.ColorCount;
            for (int i = 0; i < count && i < ColorSwatchSprites.Length; i++)
            {
                if (ColorSwatchSprites[i] != null) continue;
                try
                {
                    const int size = 32;
                    Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                    tex.hideFlags = HideFlags.HideAndDontSave;
                    tex.filterMode = FilterMode.Point;
                    Color c = PlayerPalette.ColorAt(i);
                    Color rim = new Color(c.r * 0.45f, c.g * 0.45f, c.b * 0.45f, 1f);
                    Color hi = Color.Lerp(c, Color.white, 0.35f);
                    Color[] pixels = new Color[size * size];
                    for (int y = 0; y < size; y++)
                    {
                        for (int x = 0; x < size; x++)
                        {
                            bool outer = x < 2 || x >= size - 2 || y < 2 || y >= size - 2;
                            bool innerHi = !outer && (y >= size - 5 || x <= 4);
                            pixels[y * size + x] = outer ? rim : (innerHi ? hi : c);
                        }
                    }
                    tex.SetPixels(pixels);
                    tex.Apply(false, false);
                    ColorSwatchTextures[i] = tex;
                    ColorSwatchSprites[i] = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 32f);
                    ColorSwatchSprites[i].hideFlags = HideFlags.HideAndDontSave;
                }
                catch { }
            }
        }
    }
}
