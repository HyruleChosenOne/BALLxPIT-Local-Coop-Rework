using System;
using System.Collections.Generic;
using System.Reflection;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.PlayerTwo;
using BALLxPITLocalCoop.UI;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace BALLxPITLocalCoop.Input
{
    /// <summary>
    /// Clean Rewired gamepad & keyboard router separating Player 1 (slot 0) and Player 2 (slot 1).
    /// Eliminates per-GetAxis Rewired scans, preserves DualGamepad slot identities on disconnect,
    /// honors AZERTY / custom keyboard bindings, and keeps P1 mouse visible in P1 KB+M + P2 Gamepad mode.
    /// </summary>
    public static class GamepadRouter
    {
        private static ManualLogSource _log;
        private static bool _installed;

        private static ConfigEntry<bool> _enabled => ModConfig.ControllersEnabled;
        private static ConfigEntry<InputPairingMode> _pairingMode => ModConfig.PairingMode;
        private static ConfigEntry<float> _deadzone => ModConfig.Deadzone;
        private static ConfigEntry<float> _aimSpeed => ModConfig.AimTurnSpeed;
        private static ConfigEntry<bool> _invertY => ModConfig.InvertY;
        private static ConfigEntry<bool> _p2MouseMode => ModConfig.P2MouseAndArrows;

        private static readonly ControllerRoles Roles = new ControllerRoles();
        private static readonly ControllerFrameAim SecondTurn = new ControllerFrameAim();
        private static readonly HashSet<int> _prevLeftStickDownIds = new HashSet<int>();
        private static readonly HashSet<int> _townActiveDownScratch = new HashSet<int>();
        private static readonly Dictionary<int, int[]> _cachedL3ButtonIndices = new Dictionary<int, int[]>();
        private static readonly Dictionary<string, int> _scratchFallbackSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly List<int> OriginalJoysticks = new List<int>();
        private static readonly IntPtr[] _cachedPadPointers = new IntPtr[2];
        private static readonly int[] _cachedPadJoyIds = { -1, -1 };
        private static readonly IGamepadTemplate[] _cachedPads = new IGamepadTemplate[2];

        // Cached P1 keyboard movement bindings from Rewired (excluding P2's configured move keys) for AZERTY/custom support.
        private static readonly List<KeyCode> _p1MoveLeftKeys = new List<KeyCode>(2);
        private static readonly List<KeyCode> _p1MoveRightKeys = new List<KeyCode>(2);
        private static readonly List<KeyCode> _p1MoveUpKeys = new List<KeyCode>(2);
        private static readonly List<KeyCode> _p1MoveDownKeys = new List<KeyCode>(2);
        private static bool _p1MoveKeysCached;
        private static KeyCode _cachedExcludedLeft, _cachedExcludedRight, _cachedExcludedUp, _cachedExcludedDown;

        private static int[] _cachedPollIds = Array.Empty<int>();
        private static int[] _scratchPollIds = Array.Empty<int>();
        private static string[] _scratchPollIdentities = Array.Empty<string>();
        private static InputPairingMode _cachedRolesMode;
        private static bool _rolesDirty = true;
        private static bool _townPadsSynced;
        private static int _lastTownIdsHash;
        private static int _lastJoystickCount = -1;
        private static int _lastFirstSlot = -1;
        private static int _lastSecondSlot = -1;

        private static IGamepadTemplate _secondPad;
        private static Rewired.Player _routingPlayer;
        private static bool _originalAutoAssignment;
        private static MethodInfo _activeControllerChanged;
        private static int _mappedFirst = -1;
        private static int _mapRetries;
        private static bool _firstTemplateReady;
        private static float _nextMapRefresh;
        private static bool _hadFocus;

        private static Vector2 _secondAimDir = Vector2.up;
        private static bool _secondAutoShoot = true;
        private static bool _settingPrimaryAim;
        private static bool _warnedPollError;
        private static bool _warnedSecondTemplate;
        private static bool _wasFirstActivePad;
        private static bool _wasSecondActivePad;

        public static bool PrimaryUsesPad => GetPad(0) != null;
        public static bool SecondAutoShootEnabled => _secondAutoShoot;

        public static readonly InputPairingMode[] InputPresetOrder =
        {
            InputPairingMode.TwoPlayersKeyboardP2Mouse,
            InputPairingMode.P1KeyboardP2Gamepad,
            InputPairingMode.DualGamepad
        };

        public static InputPairingMode PairingMode
        {
            get => _pairingMode?.Value ?? InputPairingMode.P1KeyboardP2Gamepad;
            set
            {
                if (_pairingMode != null && _pairingMode.Value != value)
                {
                    _pairingMode.Value = value;
                    _mappedFirst = -2;
                    _rolesDirty = true;
                    ModConfig.Save();
                }
            }
        }

        public static bool IsSharedKeyboardMode =>
            PairingMode == InputPairingMode.TwoPlayersKeyboardP1Mouse
            || PairingMode == InputPairingMode.TwoPlayersKeyboardP2Mouse;

        public static bool SharedMouseAimEnabled
        {
            get => _p2MouseMode == null || _p2MouseMode.Value;
            set
            {
                if (_p2MouseMode != null && _p2MouseMode.Value != value)
                {
                    _p2MouseMode.Value = value;
                    ModConfig.Save();
                    SecondTurn.Reset();
                    _log?.LogInfo($"2P shared mouse aim toggled to: {value}");
                }
            }
        }

        public static bool P2MouseMode
        {
            get => IsSharedKeyboardMode && SharedMouseAimEnabled;
            set => SharedMouseAimEnabled = value;
        }

        public static int InputPresetCount => InputPresetOrder.Length;

        public static int CurrentInputPresetIndex()
        {
            InputPairingMode cur = PairingMode;
            if (cur == InputPairingMode.TwoPlayersKeyboardP1Mouse) cur = InputPairingMode.TwoPlayersKeyboardP2Mouse;
            else if (cur == InputPairingMode.Auto) cur = InputPairingMode.DualGamepad;
            else if (cur == InputPairingMode.P1GamepadP2Keyboard) cur = InputPairingMode.P1KeyboardP2Gamepad;

            for (int i = 0; i < InputPresetOrder.Length; i++)
            {
                if (InputPresetOrder[i] == cur) return i;
            }
            return 1;
        }

        public static void SetInputPresetByIndex(int index)
        {
            int safe = ((index % InputPresetOrder.Length) + InputPresetOrder.Length) % InputPresetOrder.Length;
            InputPairingMode chosen = InputPresetOrder[safe];
            PairingMode = chosen;
            _log?.LogInfo($"Co-op input preset selected: {chosen}");
        }

        public static void CyclePairingMode(int delta = 1) => SetInputPresetByIndex(CurrentInputPresetIndex() + delta);

        public static string InputPresetLabel(int index)
        {
            int safe = ((index % InputPresetOrder.Length) + InputPresetOrder.Length) % InputPresetOrder.Length;
            return InputPresetOrder[safe] switch
            {
                InputPairingMode.TwoPlayersKeyboardP2Mouse => CoopLocalization.Text("1. KB+M (P1 WASD + P2 Arrows)", "1. Clavier+S (J1 WASD + J2 Fleches)"),
                InputPairingMode.P1KeyboardP2Gamepad => CoopLocalization.Text("2. P1 KB+M + P2 Gamepad", "2. J1 Clavier+S + J2 Manette"),
                InputPairingMode.DualGamepad => CoopLocalization.Text("3. Gamepad (P1 + P2)", "3. Manette (J1 + J2)"),
                _ => "2. P1 KB+M + P2 Gamepad"
            };
        }

        public static string InputPresetDescription(int index)
        {
            int safe = ((index % InputPresetOrder.Length) + InputPresetOrder.Length) % InputPresetOrder.Length;
            return InputPresetOrder[safe] switch
            {
                InputPairingMode.TwoPlayersKeyboardP2Mouse => CoopLocalization.Text(
                    "P1 Keyboard + P2 Arrows. Both crosshairs follow the Mouse (toggleable).",
                    "J1 Clavier + J2 Fleches. Les 2 viseurs suivent la Souris (activable)."),
                InputPairingMode.P1KeyboardP2Gamepad => CoopLocalization.Text(
                    "Default: P1 Keyboard + Mouse (vanilla). P2 Gamepad (LB/RB: Speed, X: Auto-Shoot).",
                    "Par defaut : J1 Clavier + Souris (vanilla). J2 Manette (LB/RB : Vitesse, X : Tir Auto)."),
                InputPairingMode.DualGamepad => CoopLocalization.Text(
                    "Gamepad for both players: P1 uses Gamepad 1, P2 uses Gamepad 2.",
                    "Manette pour les 2 joueurs : J1 Manette 1, J2 Manette 2."),
                _ => string.Empty
            };
        }

        public static string SlotInputLabel(int slot)
        {
            if (slot == 0)
            {
                if (GetPad(0) != null) return CoopLocalization.Text("GAMEPAD 1", "MANETTE 1");
                return IsSharedKeyboardMode
                    ? CoopLocalization.Text("KB + MOUSE", "CLAVIER + SOURIS")
                    : CoopLocalization.Text("KB + MOUSE", "CLAVIER + SOURIS");
            }
            if (P2MouseMode)
            {
                return CoopLocalization.Text("ARROWS + MOUSE", "FLECHES + SOURIS");
            }
            if (GetPad(1) != null)
            {
                return PairingMode == InputPairingMode.P1KeyboardP2Gamepad || Roles.First < 0
                    ? CoopLocalization.Text("GAMEPAD 1", "MANETTE 1")
                    : CoopLocalization.Text("GAMEPAD 2", "MANETTE 2");
            }
            return CoopLocalization.Text("ARROWS (J/I/L Aim)", "FLECHES (Visee J/I/L)");
        }

        public static string PairingModeSummary() => $"P1 {SlotInputLabel(0)}  -  P2 {SlotInputLabel(1)}";

        public static void MarkRolesDirty()
        {
            _rolesDirty = true;
            _p1MoveKeysCached = false;
        }

        public static void SyncSecondAimFromExternal(Vector2 dir)
        {
            if (dir.sqrMagnitude > 0.0001f)
            {
                _secondAimDir = dir.normalized;
                SecondTurn.Reset();
            }
        }

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;
            _log = log;

            _activeControllerChanged = AccessTools.Method(typeof(InputMgr), "OnLastActiveControllerChanged");
            HarmonyHelper.TryPatch(harmony, _activeControllerChanged,
                prefix: typeof(GamepadRouter).GetMethod(nameof(AllowActiveController), BindingFlags.Static | BindingFlags.NonPublic),
                label: "InputMgr.OnLastActiveControllerChanged");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(Player), "UpdateAim", Type.EmptyTypes),
                postfix: typeof(GamepadRouter).GetMethod(nameof(AfterPrimaryAim), BindingFlags.Static | BindingFlags.NonPublic),
                label: "Player.UpdateAim");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(Player), "SetAimDir", new[] { typeof(Vector2) }),
                prefix: typeof(GamepadRouter).GetMethod(nameof(BeforePrimarySetAim), BindingFlags.Static | BindingFlags.NonPublic),
                label: "Player.SetAimDir");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(InputMgr), "GetAxis", new[] { typeof(GameActionType) }),
                prefix: typeof(GamepadRouter).GetMethod(nameof(PrimaryAxis), BindingFlags.Static | BindingFlags.NonPublic),
                prefixPriority: 800,
                label: "InputMgr.GetAxis(GameActionType)");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(InputMgr), "GetAxis", new[] { typeof(ControllerBtn) }),
                prefix: typeof(GamepadRouter).GetMethod(nameof(PrimaryControllerBtnAxis), BindingFlags.Static | BindingFlags.NonPublic),
                prefixPriority: 800,
                label: "InputMgr.GetAxis(ControllerBtn)");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(InputMgr), "IsBtnHeld", new[] { typeof(GameActionType) }),
                prefix: typeof(GamepadRouter).GetMethod(nameof(PrimaryHeld), BindingFlags.Static | BindingFlags.NonPublic),
                prefixPriority: 800,
                label: "InputMgr.IsBtnHeld(GameActionType)");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(InputMgr), "IsBtnDown", new[] { typeof(GameActionType) }),
                prefix: typeof(GamepadRouter).GetMethod(nameof(PrimaryDown), BindingFlags.Static | BindingFlags.NonPublic),
                postfix: typeof(GamepadRouter).GetMethod(nameof(PrimaryDownPostfix), BindingFlags.Static | BindingFlags.NonPublic),
                prefixPriority: 800,
                label: "InputMgr.IsBtnDown(GameActionType)");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(PlayerPrefsMgr), nameof(PlayerPrefsMgr.SetAutofire)),
                prefix: typeof(GamepadRouter).GetMethod(nameof(BlockPlayerTwoAutofire), BindingFlags.Static | BindingFlags.NonPublic),
                label: "PlayerPrefsMgr.SetAutofire");
        }

        public static void Shutdown()
        {
            LeaveCoopRouting();
            Roles.Reset();
            _cachedL3ButtonIndices.Clear();
            _prevLeftStickDownIds.Clear();
            _p1MoveKeysCached = false;
            _installed = false;
        }

        public static void Poll()
        {
            _secondPad = null;
            CoopVisuals.TwoControllers = false;

            if (_enabled == null || !_enabled.Value)
            {
                if (_routingPlayer != null)
                {
                    LeaveCoopRouting();
                }
                return;
            }
            if (!ReInput.isReady)
            {
                return;
            }

            try
            {
                var joysticks = ReInput.controllers.Joysticks;
                int count = ReInput.controllers.joystickCount;
                if (count != _lastJoystickCount)
                {
                    _log?.LogInfo($"Connected gamepads: {count}");
                    _lastJoystickCount = count;
                    _rolesDirty = true;
                    _cachedL3ButtonIndices.Clear();
                }

                InputPairingMode curMode = PairingMode;
                if (!_rolesDirty && (curMode != _cachedRolesMode || count != _cachedPollIds.Length))
                {
                    _rolesDirty = true;
                }
                if (!_rolesDirty)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (joysticks[i].id != _cachedPollIds[i])
                        {
                            _rolesDirty = true;
                            _cachedL3ButtonIndices.Clear();
                            break;
                        }
                    }
                }

                if (_rolesDirty)
                {
                    if (_scratchPollIds.Length != count)
                    {
                        _scratchPollIds = count == 0 ? Array.Empty<int>() : new int[count];
                        _scratchPollIdentities = count == 0 ? Array.Empty<string>() : new string[count];
                        _cachedPollIds = count == 0 ? Array.Empty<int>() : new int[count];
                    }
                    _scratchFallbackSeen.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        Joystick joy = joysticks[i];
                        int id = joy.id;
                        _scratchPollIds[i] = id;
                        _cachedPollIds[i] = id;
                        string guid = joy.deviceInstanceGuid.ToString();
                        if (!string.IsNullOrEmpty(guid) && guid != "00000000-0000-0000-0000-000000000000")
                        {
                            _scratchPollIdentities[i] = guid;
                        }
                        else
                        {
                            string baseId = $"{joy.hardwareIdentifier}|{joy.name}";
                            _scratchFallbackSeen.TryGetValue(baseId, out int occ);
                            _scratchFallbackSeen[baseId] = occ + 1;
                            _scratchPollIdentities[i] = occ == 0 ? baseId : $"{baseId}#{occ}";
                        }
                    }
                    _scratchFallbackSeen.Clear();
                    ApplyPairingRoles(_scratchPollIds, _scratchPollIdentities);
                    _cachedRolesMode = curMode;
                    _rolesDirty = false;
                    _cachedPadPointers[0] = _cachedPadPointers[1] = IntPtr.Zero;
                    _cachedPads[0] = _cachedPads[1] = null;
                }

                Joystick firstJoy = FindJoystick(joysticks, count, Roles.First);
                Joystick secondJoy = FindJoystick(joysticks, count, Roles.Second);

                InputMgr inputMgr = InputMgr.I;
                Rewired.Player rewiredPlayer = inputMgr?.GetRewiredPlayer();
                if (inputMgr == null || rewiredPlayer == null)
                {
                    return;
                }

                IGamepadTemplate firstTemplate = ResolveCachedPad(0, firstJoy);
                IGamepadTemplate secondTemplate = ResolveCachedPad(1, secondJoy);
                _secondPad = secondTemplate;

                if (!CoopMode.Enabled)
                {
                    _wasFirstActivePad = false;
                    _wasSecondActivePad = false;
                    LeaveCoopRouting();
                    EnsureTownControllers(rewiredPlayer, inputMgr);
                    return;
                }
                _townPadsSynced = false;

                bool activeBattle = IsBattlePlaying();
                if (activeBattle)
                {
                    bool firstNow = firstJoy != null;
                    bool secondNow = secondJoy != null;
                    if (_wasFirstActivePad && !firstNow)
                    {
                        _wasFirstActivePad = false;
                        _wasSecondActivePad = secondNow;
                        CoopPause.TriggerDisconnectPause(CoopLocalization.Text(
                            "P1 controller disconnected! Reconnect controller and resume.",
                            "Manette J1 deconnectee ! Reconnectez la manette pour reprendre."));
                    }
                    else if (_wasSecondActivePad && !secondNow)
                    {
                        _wasFirstActivePad = firstNow;
                        _wasSecondActivePad = false;
                        CoopPause.TriggerDisconnectPause(CoopLocalization.Text(
                            "P2 controller disconnected! Reconnect controller and resume.",
                            "Manette J2 deconnectee ! Reconnectez la manette pour reprendre."));
                    }
                    else
                    {
                        _wasFirstActivePad = firstNow;
                        _wasSecondActivePad = secondNow;
                    }
                }
                else if (!HarmonyHelper.IsInGameScene() || GameMgr.I == null || GameMgr.I.CurState != GameState.kPaused)
                {
                    _wasFirstActivePad = false;
                    _wasSecondActivePad = false;
                }

                if (_routingPlayer == null || _routingPlayer.Pointer != rewiredPlayer.Pointer)
                {
                    LeaveCoopRouting();
                    _routingPlayer = rewiredPlayer;
                    _originalAutoAssignment = rewiredPlayer.controllers.excludeFromControllerAutoAssignment;
                    OriginalJoysticks.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        if (rewiredPlayer.controllers.ContainsController(joysticks[i]))
                        {
                            OriginalJoysticks.Add(joysticks[i].id);
                        }
                    }
                }

                bool changed = false;
                bool regainedFocus = Application.isFocused && !_hadFocus;
                _hadFocus = Application.isFocused;

                rewiredPlayer.controllers.excludeFromControllerAutoAssignment = true;
                for (int i = 0; i < count; i++)
                {
                    if (joysticks[i].id != Roles.First && rewiredPlayer.controllers.ContainsController(joysticks[i]))
                    {
                        rewiredPlayer.controllers.RemoveController(joysticks[i]);
                        changed = true;
                    }
                }
                if (firstJoy != null && !rewiredPlayer.controllers.ContainsController(firstJoy))
                {
                    rewiredPlayer.controllers.AddController(firstJoy, removeFromOtherPlayers: true);
                    changed = true;
                }

                bool firstTemplateReady = firstTemplate != null;
                if (_mappedFirst != Roles.First || changed || firstTemplateReady != _firstTemplateReady || regainedFocus)
                {
                    _mappedFirst = Roles.First;
                    _firstTemplateReady = firstTemplateReady;
                    _mapRetries = 3;
                    _nextMapRefresh = Time.unscaledTime + 0.25f;
                }

                if (firstJoy != null && _mapRetries > 0 && Time.unscaledTime >= _nextMapRefresh)
                {
                    _mapRetries--;
                    _nextMapRefresh = Time.unscaledTime + 0.5f;
                    try { _activeControllerChanged?.Invoke(inputMgr, new object[] { rewiredPlayer, firstJoy }); } catch { }
                    inputMgr.RefreshControllerMap(force: true);
                    if (inputMgr._lastJoystickGameMap != null)
                    {
                        _mapRetries = 0;
                        _log?.LogInfo($"Refreshed P1 controller mapping: {firstJoy.name}");
                    }
                }

                if (Roles.First != _lastFirstSlot || Roles.Second != _lastSecondSlot)
                {
                    if (Roles.Second != _lastSecondSlot)
                    {
                        SecondTurn.Reset();
                    }
                    LogRoleAssignment(firstJoy, secondJoy);
                    _lastFirstSlot = Roles.First;
                    _lastSecondSlot = Roles.Second;
                    _warnedSecondTemplate = false;
                }

                // Only hide mouse cursor when BOTH players are actively using gamepads (never in P1 KB+M + P2 Gamepad!).
                CoopVisuals.TwoControllers = firstJoy != null && firstTemplate != null && secondJoy != null && secondTemplate != null;

                if (secondJoy != null && secondTemplate == null && !_warnedSecondTemplate)
                {
                    _warnedSecondTemplate = true;
                    _log?.LogWarning("Second controller lacks a standard Gamepad template; P2 keyboard fallback remains active.");
                }

                if (IsBattlePlaying() && Application.isFocused)
                {
                    if (!CoopVitals.Down(1))
                    {
                        if (ButtonJustPressed(_secondPad?.x) || UnityEngine.Input.GetKeyDown(ModConfig.KeyToggleAutoShoot))
                        {
                            _secondAutoShoot = !_secondAutoShoot;
                            CoopHudAndResults.ShowToast(
                                _secondAutoShoot
                                    ? CoopLocalization.Text("P2 Auto-Shoot: ON", "Tir Auto J2 : OUI")
                                    : CoopLocalization.Text("P2 Auto-Shoot: OFF", "Tir Auto J2 : NON"),
                                1.5f);
                        }
                    }

                    if (IsSharedKeyboardMode && UnityEngine.Input.GetKeyDown(ModConfig.KeyToggleAimMode))
                    {
                        SharedMouseAimEnabled = !SharedMouseAimEnabled;
                        CoopHudAndResults.ShowToast(
                            SharedMouseAimEnabled
                                ? CoopLocalization.Text("2P Mouse Aim (Both Players): ON", "Visee Souris (2 Joueurs) : OUI")
                                : CoopLocalization.Text("2P Mouse Aim: OFF (P2 J/I/L)", "Visee Souris J2 : NON (J/I/L)"),
                            1.8f);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_warnedPollError)
                {
                    _warnedPollError = true;
                    _log?.LogWarning($"Gamepad routing unavailable: {ex.Message}");
                }
            }
        }

        public static bool IsBattlePlaying()
        {
            return CoopMode.Enabled
                && HarmonyHelper.IsInGameScene()
                && !CoopRoundStats.Complete
                && GameMgr.I != null
                && GameMgr.I.CurState == GameState.kPlaying;
        }

        public static bool CanSecondPlayerAct() => IsBattlePlaying() && !CoopVitals.Down(1);

        public static Vector2 ReadP2KeyboardMoveVector()
        {
            float x = 0f;
            float y = 0f;
            if (UnityEngine.Input.GetKey(ModConfig.KeyMoveLeft)) x -= 1f;
            if (UnityEngine.Input.GetKey(ModConfig.KeyMoveRight)) x += 1f;
            if (UnityEngine.Input.GetKey(ModConfig.KeyMoveDown)) y -= 1f;
            if (UnityEngine.Input.GetKey(ModConfig.KeyMoveUp)) y += 1f;
            Vector2 dir = new Vector2(x, y);
            return dir.sqrMagnitude > 1f ? dir.normalized : dir;
        }

        public static Vector2 GetSecondPlayerMoveInput()
        {
            if (!CanSecondPlayerAct() || !Application.isFocused)
            {
                return Vector2.zero;
            }

            if (!P2MouseMode && _secondPad != null)
            {
                Vector2 raw = SafeStickValue(_secondPad.leftStick);
                if (_invertY.Value) raw.y = -raw.y;
                Vector2 stick = Stick(raw, _deadzone.Value);
                if (stick.sqrMagnitude > 0.0001f)
                {
                    return stick;
                }
            }

            return ReadP2KeyboardMoveVector();
        }

        public static Vector2? GetSecondPlayerAimDirection()
        {
            if (P2MouseMode && CanSecondPlayerAct())
            {
                return null;
            }
            if (_secondPad == null)
            {
                return null;
            }
            if (!CanSecondPlayerAct() || !Application.isFocused)
            {
                return _secondAimDir;
            }

            Vector2 rightStick = SafeStickValue(_secondPad.rightStick);
            if (_invertY.Value) rightStick.y = -rightStick.y;

            float dz = _deadzone.Value;
            if (rightStick.sqrMagnitude > dz * dz && !(rightStick.y < 0f && Mathf.Abs(rightStick.x) < Math.Max(0.25f, dz)))
            {
                GetSecondPlayerAimArcDegrees(out float minAngle, out float maxAngle);
                float targetDeg = CoopAimRules.TargetDegrees(rightStick.x, rightStick.y, minAngle, maxAngle);
                float currentDeg = CoopAimRules.TargetDegrees(_secondAimDir.x, _secondAimDir.y, minAngle, maxAngle);
                float nextDeg = _aimSpeed.Value >= 2000f
                    ? targetDeg
                    : SecondTurn.Update(Time.frameCount, Time.deltaTime, currentDeg, targetDeg, _aimSpeed.Value, minAngle, maxAngle);
                float rad = nextDeg * (MathF.PI / 180f);
                _secondAimDir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            }

            return _secondAimDir;
        }

        public static bool ReadSecondPlayerShoot()
        {
            if (!CanSecondPlayerAct())
            {
                return false;
            }
            if (_secondAutoShoot)
            {
                return true;
            }
            if (!Application.isFocused)
            {
                return false;
            }
            if (P2MouseMode && UnityEngine.Input.GetMouseButton(0))
            {
                return true;
            }
            if (UnityEngine.Input.GetKey(ModConfig.KeyShoot))
            {
                return true;
            }
            return _secondPad != null && (SafeAxisValue(_secondPad.rightTrigger) > 0.25f || ButtonHeld(_secondPad.a));
        }

        public static Vector2 MenuStick(IGamepadTemplate pad)
        {
            if (pad == null)
            {
                return Vector2.zero;
            }
            Vector2 v = SafeStickValue(pad.leftStick);
            try
            {
                IControllerTemplateDPad dPad = pad.dPad;
                if (dPad != null)
                {
                    float dx = (ButtonHeld(dPad.right) ? 1f : 0f) - (ButtonHeld(dPad.left) ? 1f : 0f);
                    float dy = (ButtonHeld(dPad.up) ? 1f : 0f) - (ButtonHeld(dPad.down) ? 1f : 0f);
                    if (Mathf.Abs(dx) > Mathf.Abs(v.x)) v.x = dx;
                    if (Mathf.Abs(dy) > Mathf.Abs(v.y)) v.y = dy;
                }
            }
            catch { }
            return v;
        }

        public static bool ButtonJustPressed(IControllerTemplateButton button)
        {
            try { return button != null && button.justPressed; }
            catch { return false; }
        }

        public static bool ButtonJustPressed(IControllerTemplateAxis axis)
        {
            try { return axis?.AsButton != null && axis.AsButton.justPressed; }
            catch { return false; }
        }

        public static bool ButtonHeld(IControllerTemplateButton button)
        {
            try { return button != null && button.value; }
            catch { return false; }
        }

        public static float SafeAxisValue(IControllerTemplateAxis axis)
        {
            try { return axis != null ? axis.value : 0f; }
            catch { return 0f; }
        }

        public static Vector2 SafeStickValue(IControllerTemplateStick stick)
        {
            try { return stick != null ? stick.value : Vector2.zero; }
            catch { return Vector2.zero; }
        }

        public static Vector2 SafeStickValue(IControllerTemplateThumbStick stick)
        {
            try { return stick != null ? stick.value : Vector2.zero; }
            catch { return Vector2.zero; }
        }

        public static void EnsureDefaultAutoShoot()
        {
            _secondAutoShoot = true;
            try { PlayerPrefsMgr.sAutofire = true; } catch { }
        }

        public static Vector2 Stick(Vector2 v, float deadzone)
        {
            float mag = v.magnitude;
            if (mag <= deadzone) return Vector2.zero;
            return v.normalized * Mathf.Clamp01((mag - deadzone) / (1f - deadzone));
        }

        public static bool UsesGamepadForSlot(int slot) => GetPad(slot) != null;

        public static IGamepadTemplate GetPad(int slot)
        {
            if (!ReInput.isReady)
            {
                return null;
            }
            int targetId = slot == 0 ? Roles.First : Roles.Second;
            if (targetId < 0)
            {
                return null;
            }
            var joysticks = ReInput.controllers.Joysticks;
            int count = ReInput.controllers.joystickCount;
            for (int i = 0; i < count; i++)
            {
                if (joysticks[i].id == targetId)
                {
                    return ResolveCachedPad(slot, joysticks[i]);
                }
            }
            return null;
        }

        public static void VibratePad(int slot, float intensity, float duration)
        {
            if (!ReInput.isReady)
            {
                return;
            }
            int targetId = slot == 0 ? Roles.First : Roles.Second;
            if (targetId < 0)
            {
                return;
            }
            var joysticks = ReInput.controllers.Joysticks;
            int count = ReInput.controllers.joystickCount;
            for (int i = 0; i < count; i++)
            {
                if (joysticks[i].id == targetId && joysticks[i].supportsVibration)
                {
                    joysticks[i].SetVibration(Mathf.Clamp01(intensity), Mathf.Clamp01(intensity), Mathf.Max(0f, duration), Mathf.Max(0f, duration));
                }
            }
        }

        public static bool Automatic(int slot)
        {
            if (!CoopCharacterRuntime.IndependentReady)
            {
                return false;
            }
            CharBattleInst ch = CoopCharacterRuntime.CharacterFor(slot);
            if (ch != null && ch.HasProp(CharProp.kAlwaysShoot))
            {
                return true;
            }
            return slot == 1 ? _secondAutoShoot : PlayerPrefsMgr.sAutofire;
        }

        public static void ResetInputSession()
        {
            SecondTurn.Reset();
            _secondAimDir = Vector2.up;
            _secondAutoShoot = true;
            _mappedFirst = -1;
            _firstTemplateReady = false;
            _mapRetries = 0;
            _nextMapRefresh = 0f;
            _hadFocus = false;
            _rolesDirty = true;
            _p1MoveKeysCached = false;
            _cachedPadPointers[0] = _cachedPadPointers[1] = IntPtr.Zero;
            _cachedPads[0] = _cachedPads[1] = null;
        }

        public static void LeaveCoopRouting()
        {
            if (_routingPlayer == null)
            {
                return;
            }
            Rewired.Player player = _routingPlayer;
            try
            {
                player.controllers.excludeFromControllerAutoAssignment = _originalAutoAssignment;
                if (ReInput.isReady)
                {
                    var joysticks = ReInput.controllers.Joysticks;
                    int count = ReInput.controllers.joystickCount;
                    bool added = false;
                    for (int i = 0; i < count; i++)
                    {
                        if (!player.controllers.ContainsController(joysticks[i]))
                        {
                            player.controllers.AddController(joysticks[i], removeFromOtherPlayers: false);
                            added = true;
                        }
                    }
                    if (added)
                    {
                        InputMgr.I?.RefreshControllerMap(force: true);
                    }
                }
            }
            catch { }

            _routingPlayer = null;
            _townPadsSynced = false;
            OriginalJoysticks.Clear();
            ResetInputSession();
        }

        public static void EnsureTownControllers()
        {
            if (!ReInput.isReady || InputMgr.I == null) return;
            EnsureTownControllers(InputMgr.I.GetRewiredPlayer(), InputMgr.I);
        }

        public static void RestoreTownControllers()
        {
            _townPadsSynced = false;
            if (!ReInput.isReady || InputMgr.I == null) return;
            LeaveCoopRouting();
            EnsureTownControllers(InputMgr.I.GetRewiredPlayer(), InputMgr.I);
        }

        private static void EnsureTownControllers(Rewired.Player rewiredPlayer, InputMgr inputMgr)
        {
            if (rewiredPlayer == null || !ReInput.isReady)
            {
                return;
            }

            if (CoopMode.Enabled && HarmonyHelper.IsInGameScene())
            {
                bool allowVanillaOverlayPads = (GameOverUI.I != null && GameOverUI.I.gameObject.activeInHierarchy)
                    || (GameMgr.I != null && GameMgr.I.CurState == GameState.kPaused
                        && OverlayUI.sOverlayStack != null && OverlayUI.sOverlayStack.Count > 0
                        && ModalOverlayState.Current == ActiveModalOverlay.None);
                if (!allowVanillaOverlayPads)
                {
                    _townPadsSynced = false;
                    return;
                }
            }

            var joysticks = ReInput.controllers.Joysticks;
            int count = ReInput.controllers.joystickCount;
            int idsHash = count;
            for (int i = 0; i < count; i++)
            {
                idsHash = (idsHash * 397) ^ joysticks[i].id;
            }
            if (_townPadsSynced && idsHash == _lastTownIdsHash)
            {
                return;
            }
            _lastTownIdsHash = idsHash;
            _townPadsSynced = true;

            try
            {
                rewiredPlayer.controllers.excludeFromControllerAutoAssignment = false;
                bool changed = false;
                for (int i = 0; i < count; i++)
                {
                    if (!rewiredPlayer.controllers.ContainsController(joysticks[i]))
                    {
                        rewiredPlayer.controllers.AddController(joysticks[i], removeFromOtherPlayers: false);
                        changed = true;
                    }
                }
                if (changed && inputMgr != null)
                {
                    inputMgr.RefreshControllerMap(force: true);
                }
            }
            catch { }
        }

        public static void PollAnyTownGamepad(
            out bool anyConnected,
            out bool leftStickJustPressed,
            out bool aJustPressed,
            out bool bJustPressed,
            out bool anyPadActivity,
            out float stickX,
            out float stickY)
        {
            anyConnected = false;
            leftStickJustPressed = false;
            aJustPressed = false;
            bJustPressed = false;
            anyPadActivity = false;
            stickX = 0f;
            stickY = 0f;

            if (!ReInput.isReady)
            {
                _prevLeftStickDownIds.Clear();
                return;
            }

            var joysticks = ReInput.controllers.Joysticks;
            int count = ReInput.controllers.joystickCount;
            anyConnected = count > 0;
            _townActiveDownScratch.Clear();

            for (int i = 0; i < count; i++)
            {
                Joystick joy = joysticks[i];
                if (joy == null) continue;

                IGamepadTemplate pad = i < 2 ? ResolveCachedPad(i, joy) : null;
                if (pad == null)
                {
                    try { pad = joy.GetTemplate<IGamepadTemplate>(); } catch { }
                }

                bool lsHeld = false;
                if (pad != null)
                {
                    IControllerTemplateButton lsBtn = null;
                    try { lsBtn = pad.leftStick?.press; } catch { }
                    if (ButtonJustPressed(lsBtn))
                    {
                        leftStickJustPressed = true;
                        anyPadActivity = true;
                    }
                    if (ButtonHeld(lsBtn))
                    {
                        lsHeld = true;
                    }

                    if (ButtonJustPressed(pad.a)) { aJustPressed = true; anyPadActivity = true; }
                    if (ButtonJustPressed(pad.b)) { bJustPressed = true; anyPadActivity = true; }
                    if (ButtonJustPressed(pad.x) || ButtonJustPressed(pad.y) || ButtonJustPressed(pad.start))
                    {
                        anyPadActivity = true;
                    }

                    Vector2 st = MenuStick(pad);
                    if (Mathf.Abs(st.x) > Mathf.Abs(stickX)) stickX = st.x;
                    if (Mathf.Abs(st.y) > Mathf.Abs(stickY)) stickY = st.y;
                    if (Mathf.Abs(st.x) > 0.25f || Mathf.Abs(st.y) > 0.25f) anyPadActivity = true;
                }

                // Exact L3 click fallback (never matches directional "Left Stick Up/Down/Left/Right"!).
                try
                {
                    if (!_cachedL3ButtonIndices.TryGetValue(joy.id, out int[] l3Indices))
                    {
                        var found = new List<int>();
                        var btns = joy.buttons;
                        if (btns != null)
                        {
                            int maxB = Math.Min(joy.buttonCount, btns.Length);
                            for (int b = 0; b < maxB; b++)
                            {
                                string bName = btns[b]?.elementIdentifier?.name;
                                if (IsExactLeftStickClickName(bName))
                                {
                                    found.Add(b);
                                }
                            }
                        }
                        l3Indices = found.ToArray();
                        _cachedL3ButtonIndices[joy.id] = l3Indices;
                    }
                    for (int k = 0; k < l3Indices.Length; k++)
                    {
                        int b = l3Indices[k];
                        if (joy.GetButtonDown(b)) { leftStickJustPressed = true; anyPadActivity = true; }
                        if (joy.GetButton(b)) lsHeld = true;
                    }
                }
                catch { }

                if (lsHeld)
                {
                    _townActiveDownScratch.Add(joy.id);
                    if (!_prevLeftStickDownIds.Contains(joy.id))
                    {
                        leftStickJustPressed = true;
                        anyPadActivity = true;
                    }
                }
            }

            _prevLeftStickDownIds.Clear();
            foreach (int id in _townActiveDownScratch) _prevLeftStickDownIds.Add(id);
            _townActiveDownScratch.Clear();
        }

        private static bool IsExactLeftStickClickName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return false;
            string low = rawName.Trim().ToLowerInvariant();
            if (low.Contains("up") || low.Contains("down") || low.Contains("left ") && low.Contains("right") || low.EndsWith(" right") || low.EndsWith(" left") && low != "stick left")
            {
                return false;
            }
            return low == "l3"
                || low == "ls"
                || low == "left stick button"
                || low == "left stick click"
                || low == "left stick press"
                || low == "left thumb"
                || low == "left thumbstick button"
                || low == "left thumb button";
        }

        public static void GetSecondPlayerAimArcDegrees(out float minAngle, out float maxAngle)
        {
            minAngle = 15f;
            maxAngle = 165f;
            BallMgr mgr = BallMgr.I;
            Player p2 = PlayerTwoRuntime.PlayerTwo;
            if (mgr == null || p2 == null)
            {
                return;
            }
            var scope = CoopContext.EnterPlayerTwo(p2, CoopContext.ScopeKind.Full);
            try
            {
                ResolveMinMaxAngles(mgr, out minAngle, out maxAngle);
            }
            catch { }
            finally
            {
                CoopContext.Exit(scope);
            }
        }

        private static void ResolveMinMaxAngles(BallMgr mgr, out float minAngle, out float maxAngle)
        {
            Vector2 minDir = mgr.ThetaToAimDir(mgr.GetMinTheta());
            Vector2 maxDir = mgr.ThetaToAimDir(mgr.GetMaxTheta());
            float a = (Mathf.Atan2(minDir.y, minDir.x) * Mathf.Rad2Deg) % 360f;
            if (a < 0f) a += 360f;
            float b = (Mathf.Atan2(maxDir.y, maxDir.x) * Mathf.Rad2Deg) % 360f;
            if (b < 0f) b += 360f;
            if (a > b) (a, b) = (b, a);
            if (a >= 0f && b <= 180f && b - a >= 1f)
            {
                minAngle = a;
                maxAngle = b;
            }
            else
            {
                minAngle = 15f;
                maxAngle = 165f;
            }
        }

        private static void ApplyPairingRoles(int[] ids, string[] identities)
        {
            InputPairingMode mode = PairingMode;
            if (mode == InputPairingMode.TwoPlayersKeyboardP1Mouse || mode == InputPairingMode.TwoPlayersKeyboardP2Mouse)
            {
                Roles.First = -1;
                Roles.Second = -1;
                return;
            }

            if (mode == InputPairingMode.P1KeyboardP2Gamepad)
            {
                Roles.First = -1;
                if (ids.Length == 0) Roles.Second = -1;
                else if (Array.IndexOf(ids, Roles.Second) < 0) Roles.Second = ids[0];
                return;
            }

            if (mode == InputPairingMode.P1GamepadP2Keyboard)
            {
                Roles.Second = -1;
                if (ids.Length == 0) Roles.First = -1;
                else if (Array.IndexOf(ids, Roles.First) < 0) Roles.First = ids[0];
                return;
            }

            // Auto or DualGamepad: ALWAYS preserve remembered slot identities so unplugging P1 never hijacks P2's pad!
            if (ids.Length == 0)
            {
                Roles.First = -1;
                Roles.Second = -1;
                return;
            }

            if (mode == InputPairingMode.Auto && ids.Length == 1 && !Roles.HasRememberedSlots)
            {
                // Auto with a single controller plugged in at boot defaults to P1 KB+M + P2 Gamepad.
                Roles.First = -1;
                Roles.Second = ids[0];
                return;
            }

            Roles.Update(ids, identities, preserveSlots: true);
        }

        private static IGamepadTemplate ResolveCachedPad(int slot, Joystick joy)
        {
            if (slot < 0 || slot > 1) return null;
            if (joy == null)
            {
                _cachedPadPointers[slot] = IntPtr.Zero;
                _cachedPadJoyIds[slot] = -1;
                _cachedPads[slot] = null;
                return null;
            }
            IntPtr ptr = joy.Pointer;
            int id = joy.id;
            if (_cachedPadPointers[slot] == ptr && _cachedPadJoyIds[slot] == id && _cachedPads[slot] != null)
            {
                return _cachedPads[slot];
            }
            IGamepadTemplate template = null;
            try { template = joy.GetTemplate<IGamepadTemplate>(); } catch { }
            _cachedPadPointers[slot] = ptr;
            _cachedPadJoyIds[slot] = id;
            _cachedPads[slot] = template;
            return template;
        }

        private static Joystick FindJoystick(Il2CppSystem.Collections.Generic.IList<Joystick> list, int count, int id)
        {
            if (id < 0) return null;
            for (int i = 0; i < count; i++)
            {
                if (list[i].id == id) return list[i];
            }
            return null;
        }

        private static void LogRoleAssignment(Joystick first, Joystick second)
        {
            _log?.LogInfo($"Separated controllers: P1={(first == null ? "KB/Mouse" : first.name)}; P2={(second == null ? "Keyboard" : second.name)}");
        }

        private static int CurrentInputSlot()
        {
            if (_enabled == null || !_enabled.Value || !CoopVitals.Active)
            {
                return -1;
            }
            Player cur = Player.I;
            Player p2 = CoopCombat.PlayerAt(1);
            bool isP2 = cur != null && p2 != null && cur.Pointer == p2.Pointer;
            return CoopSessionRules.InputSlot(CoopMode.Enabled, CoopCharacterRuntime.IndependentReady, CoopCombat.SimulationOwner, isP2);
        }

        private static bool IsAnyP2MoveKeyHeld() =>
            UnityEngine.Input.GetKey(ModConfig.KeyMoveLeft)
            || UnityEngine.Input.GetKey(ModConfig.KeyMoveRight)
            || UnityEngine.Input.GetKey(ModConfig.KeyMoveUp)
            || UnityEngine.Input.GetKey(ModConfig.KeyMoveDown);

        private static bool ShouldIsolatePrimaryFromP2Keys() =>
            GetPad(1) == null || IsSharedKeyboardMode || IsAnyP2MoveKeyHeld();

        private static void EnsurePrimaryKeyboardMoveBindingsCached()
        {
            if (_p1MoveKeysCached
                && _cachedExcludedLeft == ModConfig.KeyMoveLeft
                && _cachedExcludedRight == ModConfig.KeyMoveRight
                && _cachedExcludedUp == ModConfig.KeyMoveUp
                && _cachedExcludedDown == ModConfig.KeyMoveDown)
            {
                return;
            }

            _p1MoveLeftKeys.Clear();
            _p1MoveRightKeys.Clear();
            _p1MoveUpKeys.Clear();
            _p1MoveDownKeys.Clear();
            _cachedExcludedLeft = ModConfig.KeyMoveLeft;
            _cachedExcludedRight = ModConfig.KeyMoveRight;
            _cachedExcludedUp = ModConfig.KeyMoveUp;
            _cachedExcludedDown = ModConfig.KeyMoveDown;
            _p1MoveKeysCached = true;

            try
            {
                Rewired.Player rp = InputMgr.I?.GetRewiredPlayer();
                var kbMaps = rp?.controllers?.maps?.GetAllMaps(ControllerType.Keyboard);
                if (kbMaps != null)
                {
                    int horizId = (int)GameActionType.kMoveHorizontal;
                    int vertId = (int)GameActionType.kMoveVertical;
                    var enumerator = kbMaps.Cast<Il2CppSystem.Collections.IEnumerable>().GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        ControllerMap map = enumerator.Current?.TryCast<ControllerMap>();
                        if (map == null || !map.enabled) continue;
                        var elems = map.GetElementMaps();
                        if (elems == null) continue;
                        for (int e = 0; e < elems.Length; e++)
                        {
                            ActionElementMap aem = elems[e];
                            if (aem == null || !aem.enabled) continue;
                            KeyCode kc = aem.keyCode;
                            if (kc == KeyCode.None
                                || kc == _cachedExcludedLeft || kc == _cachedExcludedRight
                                || kc == _cachedExcludedUp || kc == _cachedExcludedDown)
                            {
                                continue;
                            }
                            if (aem.actionId == horizId)
                            {
                                var list = aem.axisContribution == Pole.Positive ? _p1MoveRightKeys : _p1MoveLeftKeys;
                                if (!list.Contains(kc)) list.Add(kc);
                            }
                            else if (aem.actionId == vertId)
                            {
                                var list = aem.axisContribution == Pole.Positive ? _p1MoveUpKeys : _p1MoveDownKeys;
                                if (!list.Contains(kc)) list.Add(kc);
                            }
                        }
                    }
                }
            }
            catch { }

            if (_p1MoveLeftKeys.Count == 0) _p1MoveLeftKeys.Add(KeyCode.A);
            if (_p1MoveRightKeys.Count == 0) _p1MoveRightKeys.Add(KeyCode.D);
            if (_p1MoveUpKeys.Count == 0) _p1MoveUpKeys.Add(KeyCode.W);
            if (_p1MoveDownKeys.Count == 0) _p1MoveDownKeys.Add(KeyCode.S);
        }

        private static bool AnyKeyHeld(List<KeyCode> keys)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (UnityEngine.Input.GetKey(keys[i])) return true;
            }
            return false;
        }

        private static float ReadPrimaryKeyboardHorizontal()
        {
            EnsurePrimaryKeyboardMoveBindingsCached();
            return (AnyKeyHeld(_p1MoveRightKeys) ? 1f : 0f) - (AnyKeyHeld(_p1MoveLeftKeys) ? 1f : 0f);
        }

        private static float ReadPrimaryKeyboardVertical()
        {
            EnsurePrimaryKeyboardMoveBindingsCached();
            return (AnyKeyHeld(_p1MoveUpKeys) ? 1f : 0f) - (AnyKeyHeld(_p1MoveDownKeys) ? 1f : 0f);
        }

        private static bool AllowActiveController(Controller __1)
        {
            if (CoopMode.Enabled && _enabled != null && _enabled.Value && __1 != null && __1.type == Rewired.ControllerType.Joystick)
            {
                return Roles.First >= 0 && __1.id == Roles.First;
            }
            return true;
        }

        private static bool PrimaryControllerBtnAxis(ControllerBtn __0, ref float __result)
        {
            int slot = CurrentInputSlot();
            if (slot < 0) return true;
            if (!Application.isFocused || slot != 0)
            {
                __result = 0f;
                return false;
            }
            if (!ShouldIsolatePrimaryFromP2Keys())
            {
                return true;
            }

            IGamepadTemplate pad = GetPad(0);
            switch (__0)
            {
                case ControllerBtn.kLeftStickHorizontal:
                {
                    float sx = pad != null ? Stick(SafeStickValue(pad.leftStick), _deadzone.Value).x : 0f;
                    if (Mathf.Abs(sx) > 0.001f) { __result = sx; return false; }
                    __result = (pad != null && PairingMode == InputPairingMode.DualGamepad) ? 0f : ReadPrimaryKeyboardHorizontal();
                    return false;
                }
                case ControllerBtn.kLeftStickVertical:
                {
                    Vector2 st = pad != null ? Stick(SafeStickValue(pad.leftStick), _deadzone.Value) : Vector2.zero;
                    float sy = _invertY.Value ? -st.y : st.y;
                    if (Mathf.Abs(sy) > 0.001f) { __result = sy; return false; }
                    __result = (pad != null && PairingMode == InputPairingMode.DualGamepad) ? 0f : ReadPrimaryKeyboardVertical();
                    return false;
                }
                case ControllerBtn.kRightStickHorizontal:
                case ControllerBtn.kRightStickVertical:
                {
                    if (pad != null)
                    {
                        Vector2 rs = Stick(SafeStickValue(pad.rightStick), _deadzone.Value);
                        if (_invertY.Value) rs.y = -rs.y;
                        __result = __0 == ControllerBtn.kRightStickHorizontal ? rs.x : rs.y;
                        return false;
                    }
                    __result = 0f;
                    return false;
                }
                default:
                    return true;
            }
        }

        private static bool PrimaryAxis(GameActionType __0, ref float __result)
        {
            int slot = CurrentInputSlot();
            if (slot < 0) return true;
            if (!Application.isFocused || slot != 0)
            {
                if (__0 == GameActionType.kMoveHorizontal || __0 == GameActionType.kMoveVertical
                    || __0 == GameActionType.kAimHorizontal || __0 == GameActionType.kAimVertical)
                {
                    __result = 0f;
                    return false;
                }
                return true;
            }

            bool isolateP2 = ShouldIsolatePrimaryFromP2Keys();
            IGamepadTemplate pad = GetPad(0);
            if (pad == null)
            {
                if (isolateP2)
                {
                    switch (__0)
                    {
                        case GameActionType.kMoveHorizontal:
                            __result = ReadPrimaryKeyboardHorizontal();
                            return false;
                        case GameActionType.kMoveVertical:
                            __result = ReadPrimaryKeyboardVertical();
                            return false;
                        case GameActionType.kAimHorizontal:
                        case GameActionType.kAimVertical:
                            __result = 0f;
                            return false;
                    }
                }
                return true;
            }

            Vector2 value;
            switch (__0)
            {
                case GameActionType.kMoveHorizontal:
                case GameActionType.kMoveVertical:
                    value = Stick(SafeStickValue(pad.leftStick), _deadzone.Value);
                    break;
                case GameActionType.kAimHorizontal:
                case GameActionType.kAimVertical:
                    value = Stick(SafeStickValue(pad.rightStick), _deadzone.Value);
                    break;
                default:
                    return true;
            }
            if (_invertY.Value) value.y = -value.y;

            float axis = (__0 == GameActionType.kMoveHorizontal || __0 == GameActionType.kAimHorizontal) ? value.x : value.y;
            if (Mathf.Abs(axis) <= 0.001f)
            {
                if (isolateP2)
                {
                    if (__0 == GameActionType.kMoveHorizontal)
                    {
                        __result = PairingMode == InputPairingMode.DualGamepad ? 0f : ReadPrimaryKeyboardHorizontal();
                        return false;
                    }
                    if (__0 == GameActionType.kMoveVertical)
                    {
                        __result = PairingMode == InputPairingMode.DualGamepad ? 0f : ReadPrimaryKeyboardVertical();
                        return false;
                    }
                    __result = 0f;
                    return false;
                }
                return true;
            }

            __result = axis;
            return false;
        }

        private static bool PrimaryDown(GameActionType __0, ref bool __result)
        {
            if (CoopMode.Enabled && __0 == GameActionType.kLevelUp && CoopVitals.IsKeyboardGoldReviveEligible
                && (UnityEngine.Input.GetKey(KeyCode.E) || UnityEngine.Input.GetKeyDown(KeyCode.E) || UnityEngine.Input.GetKeyUp(KeyCode.E)))
            {
                __result = false;
                return false;
            }

            int slot = CurrentInputSlot();
            if (slot != 0 && IsBattlePlaying()
                && (__0 == GameActionType.kAutofire || __0 == GameActionType.kShoot || __0 == GameActionType.kLevelUp))
            {
                __result = false;
                return false;
            }

            if (__0 == GameActionType.kAutofire && IsBattlePlaying())
            {
                bool p2Toggled = ButtonJustPressed(_secondPad?.x) || UnityEngine.Input.GetKeyDown(ModConfig.KeyToggleAutoShoot);
                IGamepadTemplate p1Pad = GetPad(0);
                if (p2Toggled && (p1Pad != null ? !ButtonJustPressed(p1Pad.x) : !UnityEngine.Input.anyKeyDown || UnityEngine.Input.GetKeyDown(ModConfig.KeyToggleAutoShoot)))
                {
                    __result = false;
                    return false;
                }
            }

            if (slot == 0 && IsBattlePlaying() && Application.isFocused && _secondPad != null)
            {
                if (__0 == GameActionType.kDecreaseSpeed && ButtonJustPressed(_secondPad.leftBumper))
                {
                    __result = true;
                    return false;
                }
                if (__0 == GameActionType.kIncreaseSpeed && ButtonJustPressed(_secondPad.rightBumper))
                {
                    __result = true;
                    return false;
                }
            }
            return true;
        }

        private static void PrimaryDownPostfix(GameActionType __0, ref bool __result)
        {
            if (!__result || !IsBattlePlaying() || __0 != GameActionType.kAutofire)
            {
                return;
            }
            if (CurrentInputSlot() != 0)
            {
                __result = false;
                return;
            }
            if (Roles.First < 0 && ButtonJustPressed(_secondPad?.x) && !UnityEngine.Input.anyKeyDown)
            {
                __result = false;
            }
        }

        private static bool BlockPlayerTwoAutofire()
        {
            if (!CoopMode.Enabled || !HarmonyHelper.IsInGameScene())
            {
                return true;
            }
            if (CoopCharacterRuntime.IndependentReady
                && (CoopCombat.SimulationOwner == 1 || CoopContext.IsPlayerTwoContext || CurrentInputSlot() == 1))
            {
                return false;
            }
            return true;
        }

        private static bool PrimaryHeld(GameActionType __0, ref bool __result)
        {
            int slot = CurrentInputSlot();
            if (slot < 0) return true;
            if (!Application.isFocused || slot != 0)
            {
                if (__0 == GameActionType.kShoot)
                {
                    __result = false;
                    return false;
                }
                return true;
            }
            if (__0 != GameActionType.kShoot)
            {
                return true;
            }

            IGamepadTemplate pad = GetPad(0);
            if (pad == null)
            {
                return true;
            }
            // Note: Right Bumper is intentionally NOT mapped to shoot (RB is kIncreaseSpeed).
            if (SafeAxisValue(pad.rightTrigger) > 0.25f || ButtonHeld(pad.a))
            {
                __result = true;
                return false;
            }
            return true;
        }

        private static bool TryGetPrimaryActiveRightStickAim(Player player, out Vector2 direction)
        {
            direction = Vector2.up;
            if (_enabled == null || !_enabled.Value || !CoopMode.Enabled || !CoopVitals.Active || !Application.isFocused
                || CoopVitals.Down(0) || player == null || !HarmonyHelper.SameNative(player, CoopCombat.PlayerAt(0)) || BallMgr.I == null)
            {
                return false;
            }
            IGamepadTemplate pad = GetPad(0);
            if (pad == null || (BattleSaveData.I?.CurChar?.HasProp(CharProp.kAimless) ?? false))
            {
                return false;
            }

            Vector2 value = SafeStickValue(pad.rightStick);
            if (_invertY.Value) value.y = -value.y;

            float dz = _deadzone.Value;
            if (value.sqrMagnitude <= dz * dz || (value.y < 0f && Mathf.Abs(value.x) < Math.Max(0.25f, dz)))
            {
                // When P1 right stick is idle, do NOT clobber SetAimDir (lets resets / auto-aim / left-stick aim work).
                return false;
            }

            ResolveMinMaxAngles(BallMgr.I, out float minAngle, out float maxAngle);
            float target = CoopAimRules.TargetDegrees(value.x, value.y, minAngle, maxAngle);
            float rad = target * (MathF.PI / 180f);
            direction = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            return true;
        }

        private static void BeforePrimarySetAim(Player __instance, ref Vector2 __0)
        {
            if (_settingPrimaryAim) return;
            if (TryGetPrimaryActiveRightStickAim(__instance, out Vector2 direction))
            {
                __0 = direction;
            }
        }

        private static void AfterPrimaryAim(Player __instance)
        {
            if (_settingPrimaryAim) return;
            if (TryGetPrimaryActiveRightStickAim(__instance, out Vector2 direction))
            {
                _settingPrimaryAim = true;
                try { __instance.SetAimDir(direction); }
                finally { _settingPrimaryAim = false; }
            }
        }
    }
}
