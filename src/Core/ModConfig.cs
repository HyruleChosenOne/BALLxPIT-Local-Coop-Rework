using System;
using System.IO;
using BALLxPITLocalCoop.Diagnostics;
using BALLxPITLocalCoop.Input;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace BALLxPITLocalCoop.Core
{
    /// <summary>
    /// Centralized BALLxPITLocalCoop.cfg backing store with cached KeyCodes, clamped ranges,
    /// and glitch-free disk hot-reload.
    /// </summary>
    internal static class ModConfig
    {
        private const double ReloadCheckIntervalSeconds = 0.25;
        private static ConfigFile _config;
        private static DateTime _lastWriteTimeUtc;
        private static DateTime _nextReloadCheckUtc;
        private static bool _suppressNextReloadCheck;

        // Cached parsed KeyCodes to avoid per-frame string.Trim() + Enum.TryParse allocations.
        public static KeyCode KeyMoveLeft { get; private set; } = KeyCode.LeftArrow;
        public static KeyCode KeyMoveDown { get; private set; } = KeyCode.DownArrow;
        public static KeyCode KeyMoveRight { get; private set; } = KeyCode.RightArrow;
        public static KeyCode KeyMoveUp { get; private set; } = KeyCode.UpArrow;
        public static KeyCode KeyShoot { get; private set; } = KeyCode.RightShift;
        public static KeyCode KeyToggleAutoShoot { get; private set; } = KeyCode.RightControl;
        public static KeyCode KeyToggleAimMode { get; private set; } = KeyCode.Keypad9;
        public static KeyCode KeyAimLeft { get; private set; } = KeyCode.J;
        public static KeyCode KeyAimUp { get; private set; } = KeyCode.I;
        public static KeyCode KeyAimRight { get; private set; } = KeyCode.L;

        // [General]
        public static ConfigEntry<bool> EnableHotReload { get; private set; }

        // [Input]
        public static ConfigEntry<bool> ControllersEnabled { get; private set; }
        public static ConfigEntry<InputPairingMode> PairingMode { get; private set; }
        public static ConfigEntry<float> Deadzone { get; private set; }
        public static ConfigEntry<float> AimTurnSpeed { get; private set; }
        public static ConfigEntry<bool> InvertY { get; private set; }
        public static ConfigEntry<bool> P2MouseAndArrows { get; private set; }

        // [PlayerTwo]
        public static ConfigEntry<string> MoveLeft { get; private set; }
        public static ConfigEntry<string> MoveDown { get; private set; }
        public static ConfigEntry<string> MoveRight { get; private set; }
        public static ConfigEntry<string> MoveUp { get; private set; }
        public static ConfigEntry<string> Shoot { get; private set; }
        public static ConfigEntry<string> ToggleAutoShoot { get; private set; }
        public static ConfigEntry<string> ToggleAimMode { get; private set; }
        public static ConfigEntry<string> AimLeft { get; private set; }
        public static ConfigEntry<string> AimUp { get; private set; }
        public static ConfigEntry<string> AimRight { get; private set; }
        public static ConfigEntry<string> P2ColorHex { get; private set; }

        // [Gameplay]
        public static ConfigEntry<bool> BalanceEnabled { get; private set; }
        public static ConfigEntry<float> EnemyResistance { get; private set; }
        public static ConfigEntry<float> BossResistance { get; private set; }
        public static ConfigEntry<bool> AutoPickWhenMaxed { get; private set; }

        // [UI]
        public static ConfigEntry<int> Player1ColorIndex { get; private set; }
        public static ConfigEntry<int> Player2ColorIndex { get; private set; }

        // [Comfort]
        public static ConfigEntry<bool> ReduceHitFlashes { get; private set; }

        // [Diagnostics]
        public static ConfigEntry<bool> HangWatchdogEnabled { get; private set; }
        public static ConfigEntry<bool> HangWatchdogWriteDump { get; private set; }

        public static void Initialize()
        {
            _config = new ConfigFile(Path.Combine(Paths.ConfigPath, $"{Plugin.PluginId}.cfg"), saveOnInit: false);
            _config.SaveOnConfigSet = false;

            // [General]
            EnableHotReload = _config.Bind("General", "EnableHotReload", true,
                "Automatically reload configuration changes when BALLxPITLocalCoop.cfg is edited on disk.");

            // [Input]
            ControllersEnabled = _config.Bind("Input", "Enabled", true,
                "Separate first and second connected gamepads. Keyboard fallback remains available.");
            PairingMode = _config.Bind("Input", "PairingMode", InputPairingMode.P1KeyboardP2Gamepad,
                "Controller assignment: TwoPlayersKeyboardP2Mouse, P1KeyboardP2Gamepad, P1GamepadP2Keyboard, DualGamepad, or Auto.");
            Deadzone = _config.Bind("Input", "Deadzone", 0.18f,
                new ConfigDescription("Radial analog stick deadzone.", new AcceptableValueRange<float>(0.05f, 0.5f)));
            AimTurnSpeed = _config.Bind("Input", "AimTurnSpeed", 1080f,
                new ConfigDescription("Maximum P2 aim turn speed in degrees per second.", new AcceptableValueRange<float>(30f, 2160f)));
            InvertY = _config.Bind("Input", "InvertY", false,
                "Invert both vertical sticks only if your controller uses reversed Y.");
            P2MouseAndArrows = _config.Bind("Input", "P2MouseAndArrows", true,
                "When using 'KB+M (P1 WASD + P2 Arrows)' mode, both P1 and P2 aim/crosshairs follow the Mouse (toggleable ON/OFF; when OFF, P2 uses keyboard aim keys).");

            // [PlayerTwo]
            MoveLeft = _config.Bind("PlayerTwo", "MoveLeft", "LeftArrow", "Move P2 left (Unity KeyCode name, e.g. LeftArrow, Keypad4).");
            MoveDown = _config.Bind("PlayerTwo", "MoveDown", "DownArrow", "Move P2 down (Unity KeyCode name, e.g. DownArrow, Keypad5).");
            MoveRight = _config.Bind("PlayerTwo", "MoveRight", "RightArrow", "Move P2 right (Unity KeyCode name, e.g. RightArrow, Keypad6).");
            MoveUp = _config.Bind("PlayerTwo", "MoveUp", "UpArrow", "Move P2 up (Unity KeyCode name, e.g. UpArrow, Keypad8).");
            Shoot = _config.Bind("PlayerTwo", "Shoot", "RightShift", "Hold to shoot with P2 (Unity KeyCode name, e.g. RightShift, Keypad0).");
            ToggleAutoShoot = _config.Bind("PlayerTwo", "ToggleAutoShoot", "RightControl", "Toggle P2 autoshoot (Unity KeyCode name, e.g. RightControl, Keypad7).");
            ToggleAimMode = _config.Bind("PlayerTwo", "ToggleAimMode", "Keypad9", "Toggle P2 aim between keyboard and mouse modes (Unity KeyCode name).");
            AimLeft = _config.Bind("PlayerTwo", "AimLeft", "J", "Rotate P2 aim toward the left native limit (Unity KeyCode name).");
            AimUp = _config.Bind("PlayerTwo", "AimUp", "I", "Rotate P2 aim toward straight up (Unity KeyCode name).");
            AimRight = _config.Bind("PlayerTwo", "AimRight", "L", "Rotate P2 aim toward the right native limit (Unity KeyCode name).");
            P2ColorHex = _config.Bind("PlayerTwo", "Color", PlayerPalette.DefaultP2ColorHex,
                "P2 aim cursor and trajectory color as RGB hex (for example 26D9FF or FF6B6B).");

            // [Gameplay]
            BalanceEnabled = _config.Bind("Gameplay", "BalanceEnabled", false,
                "Enable enemy/boss resistance scaling only while player two exists.");
            EnemyResistance = _config.Bind("Gameplay", "EnemyResistance", 1f,
                new ConfigDescription("Effective resistance multiplier for regular enemies. Incoming damage is divided by this number. Minimum damage stays 1.", new AcceptableValueRange<float>(1f, 4f)));
            BossResistance = _config.Bind("Gameplay", "BossResistance", 1f,
                new ConfigDescription("Effective resistance multiplier for bosses. Does not change their health values or rewards.", new AcceptableValueRange<float>(1f, 4f)));
            AutoPickWhenMaxed = _config.Bind("Gameplay", "AutoPickWhenMaxed", false,
                "Automatically claim Coin Bag or Heal on level-up once a player's ball and passive slots are full.");

            // [UI]
            Player1ColorIndex = _config.Bind("UI", "Player1ColorIndex", 0,
                new ConfigDescription("Selected color index (0-14) for Player 1 HUD, aim reticle, and ring.", new AcceptableValueRange<int>(0, PlayerPalette.ColorCount - 1)));
            Player2ColorIndex = _config.Bind("UI", "Player2ColorIndex", 1,
                new ConfigDescription("Selected color index (0-14) for Player 2 HUD, aim reticle, trajectory, and ring.", new AcceptableValueRange<int>(0, PlayerPalette.ColorCount - 1)));

            // [Comfort]
            ReduceHitFlashes = _config.Bind("Comfort", "ReduceHitFlashes", false,
                "Throttle and soften bright white enemy hit-flash pulsing during co-op.");

            // [Diagnostics]
            HangWatchdogEnabled = _config.Bind("Diagnostics", "HangWatchdogEnabled", false,
                "Opt-in background hang detection that captures BepInEx/hang_report.txt on long main-thread stalls.");
            HangWatchdogWriteDump = _config.Bind("Diagnostics", "HangWatchdogWriteDump", false,
                "Also write a BepInEx/hang_dump.dmp minidump when HangWatchdog captures a stall (off by default).");

            NormalizeNumericRanges();
            RefreshCachedKeys();
            PlayerPalette.RefreshFromConfig(preferIndexOnStartup: true);
            Save();
            _nextReloadCheckUtc = DateTime.UtcNow.AddSeconds(ReloadCheckIntervalSeconds);
        }

        private static void NormalizeNumericRanges()
        {
            if (AimTurnSpeed != null) AimTurnSpeed.Value = Mathf.Clamp(AimTurnSpeed.Value, 30f, 2160f);
            if (Deadzone != null) Deadzone.Value = Mathf.Clamp(Deadzone.Value, 0.05f, 0.5f);
            if (EnemyResistance != null) EnemyResistance.Value = Mathf.Clamp(EnemyResistance.Value, 1f, 4f);
            if (BossResistance != null) BossResistance.Value = Mathf.Clamp(BossResistance.Value, 1f, 4f);
            if (Player1ColorIndex != null) Player1ColorIndex.Value = Mathf.Clamp(Player1ColorIndex.Value, 0, PlayerPalette.ColorCount - 1);
            if (Player2ColorIndex != null) Player2ColorIndex.Value = Mathf.Clamp(Player2ColorIndex.Value, 0, PlayerPalette.ColorCount - 1);
        }

        private static void RefreshCachedKeys()
        {
            KeyMoveLeft = ParseKey(MoveLeft, KeyCode.LeftArrow);
            KeyMoveDown = ParseKey(MoveDown, KeyCode.DownArrow);
            KeyMoveRight = ParseKey(MoveRight, KeyCode.RightArrow);
            KeyMoveUp = ParseKey(MoveUp, KeyCode.UpArrow);
            KeyShoot = ParseKey(Shoot, KeyCode.RightShift);
            KeyToggleAutoShoot = ParseKey(ToggleAutoShoot, KeyCode.RightControl);
            KeyToggleAimMode = ParseKey(ToggleAimMode, KeyCode.Keypad9);
            KeyAimLeft = ParseKey(AimLeft, KeyCode.J);
            KeyAimUp = ParseKey(AimUp, KeyCode.I);
            KeyAimRight = ParseKey(AimRight, KeyCode.L);
        }

        public static KeyCode ParseKey(ConfigEntry<string> entry, KeyCode fallback)
        {
            string raw = entry?.Value?.Trim();
            if (!string.IsNullOrEmpty(raw)
                && !char.IsDigit(raw[0])
                && Enum.TryParse(raw, ignoreCase: true, out KeyCode parsed)
                && Enum.IsDefined(typeof(KeyCode), parsed))
            {
                return parsed;
            }
            return fallback;
        }

        public static string GetControlsSummary()
        {
            return $"move={KeyMoveLeft}/{KeyMoveDown}/{KeyMoveRight}/{KeyMoveUp}, shoot={KeyShoot}, autoshoot={KeyToggleAutoShoot}, aimMode={KeyToggleAimMode}, aim={KeyAimLeft}/{KeyAimUp}/{KeyAimRight}";
        }

        public static void Save()
        {
            if (_config == null)
            {
                return;
            }
            try
            {
                _config.Save();
                _lastWriteTimeUtc = GetWriteTimeUtc();
                _suppressNextReloadCheck = true;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"Failed to save {Plugin.PluginId}.cfg: {ex.Message}");
            }
        }

        public static void PollForChanges()
        {
            if (_config == null || (EnableHotReload != null && !EnableHotReload.Value))
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now < _nextReloadCheckUtc)
            {
                return;
            }
            _nextReloadCheckUtc = now.AddSeconds(ReloadCheckIntervalSeconds);

            DateTime writeTime = GetWriteTimeUtc();
            if (writeTime == DateTime.MinValue || writeTime == _lastWriteTimeUtc)
            {
                _suppressNextReloadCheck = false;
                return;
            }
            if (_suppressNextReloadCheck)
            {
                _suppressNextReloadCheck = false;
                _lastWriteTimeUtc = writeTime;
                return;
            }

            try
            {
                int prevP2Idx = Player2ColorIndex?.Value ?? 1;
                string prevP2Hex = P2ColorHex?.Value ?? string.Empty;
                bool prevWatchdog = HangWatchdogEnabled?.Value ?? false;

                _config.Reload();
                NormalizeNumericRanges();
                RefreshCachedKeys();

                bool idxChanged = Player2ColorIndex != null && Player2ColorIndex.Value != prevP2Idx;
                bool hexChanged = !string.Equals(P2ColorHex?.Value ?? string.Empty, prevP2Hex, StringComparison.OrdinalIgnoreCase);
                if (idxChanged && !hexChanged)
                {
                    PlayerPalette.SyncPlayerTwoHexFromIndex(save: false);
                }
                else
                {
                    PlayerPalette.RefreshFromConfig(preferIndexOnStartup: false);
                }

                bool nowWatchdog = HangWatchdogEnabled?.Value ?? false;
                if (nowWatchdog != prevWatchdog)
                {
                    if (nowWatchdog) HangWatchdog.Start();
                    else HangWatchdog.Shutdown();
                }

                GamepadRouter.MarkRolesDirty();
                _lastWriteTimeUtc = writeTime;
                Plugin.Logger?.LogInfo($"Configuration hot-reloaded | P2 controls: {GetControlsSummary()} | appearance: {PlayerPalette.GetSummary()}.");
            }
            catch (IOException)
            {
                // Transient editor lock: retry on the next poll without suppressing.
            }
            catch (Exception ex)
            {
                _lastWriteTimeUtc = writeTime;
                Plugin.Logger?.LogWarning($"Could not hot-reload {Plugin.PluginId}.cfg: {ex.Message}");
            }
        }

        private static DateTime GetWriteTimeUtc()
        {
            try
            {
                string path = _config?.ConfigFilePath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return DateTime.MinValue;
                }
                return File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
}
