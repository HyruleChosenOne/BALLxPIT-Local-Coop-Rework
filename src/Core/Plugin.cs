using System;
using BALLxPITLocalCoop.Diagnostics;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.Input;
using BALLxPITLocalCoop.PlayerTwo;
using BALLxPITLocalCoop.UI;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace BALLxPITLocalCoop.Core
{
    /// <summary>
    /// Unified BepInEx 6 IL2CPP entry point for BALLxPIT: Local Co-op Rework (v1.0 modular architecture).
    /// Registers a single deterministic CoopHost MonoBehaviour (no OnGUI churn) and performs symmetrical Install/Shutdown.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BasePlugin
    {
        public const string PluginGuid = "hyrule.ballxpit.localcoop";
        public const string PluginName = "LocalCoopRework";
        public const string PluginId = "BALLxPITLocalCoop";
        public const string PluginVersion = "1.0.0";

        private static Harmony _harmony;
        private static CoopHost _coopHost;
        private static bool _il2cppTypesRegistered;

        internal static ManualLogSource Logger { get; private set; }

        public override void Load()
        {
            Logger = Log;
            ModConfig.Initialize();

            if (ModConfig.HangWatchdogEnabled?.Value ?? false)
            {
                HangWatchdog.Start();
            }

            if (!_il2cppTypesRegistered)
            {
                ClassInjector.RegisterTypeInIl2Cpp<CoopHost>();
                _il2cppTypesRegistered = true;
            }

            _harmony?.UnpatchSelf();
            _harmony = new Harmony(PluginGuid);

            // Deterministic module installation order (Core -> Input -> PlayerTwo -> Gameplay -> UI)
            CoopLocalization.Install(_harmony);
            CoopCompatibility.Install(_harmony);
            GamepadRouter.Install(_harmony, Log);
            PlayerTwoPatches.Install(_harmony);
            CoopSession.Install(_harmony);
            CoopMode.Install(_harmony);
            CoopBalance.Install(_harmony, Log);
            CoopCharacterRuntime.Install(_harmony);
            CoopVitals.Install(_harmony);
            CoopPickups.Install(_harmony);
            CoopCombat.Install(_harmony);
            CoopAmmoRecovery.Install(_harmony);
            CoopComfort.Install(_harmony);
            CoopRoundStats.Install(_harmony);
            CoopDecisions.Install(_harmony);
            CoopRevivePrompt.Install(_harmony);
            CoopPause.Install(_harmony);
            CoopVisuals.Install(_harmony);
            CoopTutorial.Install(_harmony);

            if (_coopHost == null)
            {
                _coopHost = AddComponent<CoopHost>();
            }

            Log.LogInfo(
                $"{PluginName} {PluginVersion} initialized | " +
                $"P2 keys: {ModConfig.GetControlsSummary()} | " +
                $"P2 color: #{PlayerPalette.GetPlayerColorHex(1)} | " +
                $"Watchdog: {(HangWatchdog.IsRunning ? "ON" : "OFF")}.");
        }

        public override bool Unload()
        {
            try
            {
                HangWatchdog.Shutdown();

                _harmony?.UnpatchSelf();
                _harmony = null;

                // Symmetrical shutdown of all modules (UI -> Gameplay -> PlayerTwo -> Input -> Core)
                HarmonyHelper.SafeInvoke(CoopTutorial.Shutdown, "CoopTutorial.Shutdown");
                HarmonyHelper.SafeInvoke(CoopVisuals.Shutdown, "CoopVisuals.Shutdown");
                HarmonyHelper.SafeInvoke(CoopPause.Shutdown, "CoopPause.Shutdown");
                HarmonyHelper.SafeInvoke(CoopRevivePrompt.Shutdown, "CoopRevivePrompt.Shutdown");
                HarmonyHelper.SafeInvoke(CoopDecisions.Shutdown, "CoopDecisions.Shutdown");
                HarmonyHelper.SafeInvoke(CoopHudAndResults.Shutdown, "CoopHudAndResults.Shutdown");
                HarmonyHelper.SafeInvoke(CoopMenuUI.Shutdown, "CoopMenuUI.Shutdown");
                HarmonyHelper.SafeInvoke(MainMenuCoopUI.Shutdown, "MainMenuCoopUI.Shutdown");
                HarmonyHelper.SafeInvoke(ChoiceOverlay.Shutdown, "ChoiceOverlay.Shutdown");
                HarmonyHelper.SafeInvoke(CoopRoundStats.Shutdown, "CoopRoundStats.Shutdown");

                HarmonyHelper.SafeInvoke(CoopComfort.Shutdown, "CoopComfort.Shutdown");
                HarmonyHelper.SafeInvoke(CoopAmmoRecovery.Shutdown, "CoopAmmoRecovery.Shutdown");
                HarmonyHelper.SafeInvoke(CoopCombat.Shutdown, "CoopCombat.Shutdown");
                HarmonyHelper.SafeInvoke(CoopPickups.Shutdown, "CoopPickups.Shutdown");
                HarmonyHelper.SafeInvoke(CoopVitals.Shutdown, "CoopVitals.Shutdown");
                HarmonyHelper.SafeInvoke(CoopCharacterRuntime.Shutdown, "CoopCharacterRuntime.Shutdown");
                HarmonyHelper.SafeInvoke(CoopBalance.Shutdown, "CoopBalance.Shutdown");

                HarmonyHelper.SafeInvoke(PlayerTwoRuntime.ResetInstance, "PlayerTwoRuntime.ResetInstance");
                HarmonyHelper.SafeInvoke(PlayerTwoPatches.Shutdown, "PlayerTwoPatches.Shutdown");
                HarmonyHelper.SafeInvoke(PreviewIsolation.Shutdown, "PreviewIsolation.Shutdown");
                HarmonyHelper.SafeInvoke(GamepadRouter.Shutdown, "GamepadRouter.Shutdown");
                HarmonyHelper.SafeInvoke(CoopMode.Shutdown, "CoopMode.Shutdown");
                HarmonyHelper.SafeInvoke(CoopSession.Shutdown, "CoopSession.Shutdown");
                HarmonyHelper.SafeInvoke(CoopCompatibility.Shutdown, "CoopCompatibility.Shutdown");
                HarmonyHelper.SafeInvoke(CoopLocalization.Shutdown, "CoopLocalization.Shutdown");
                HarmonyHelper.SafeInvoke(ModalOverlayState.Reset, "ModalOverlayState.Reset");

                if (_coopHost != null)
                {
                    UnityEngine.Object.Destroy(_coopHost);
                    _coopHost = null;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Error during plugin unload: {ex.Message}");
            }
            return base.Unload();
        }
    }

    /// <summary>
    /// Single deterministic frame host for all Local Co-op subsystems.
    /// Zero OnGUI() overhead — all retained UGUI overlays are driven once per frame in Update().
    /// </summary>
    public sealed class CoopHost : MonoBehaviour
    {
        public CoopHost(IntPtr ptr) : base(ptr) { }

        public void Update()
        {
            HangWatchdog.Heartbeat();

            GamepadRouter.Poll();
            OverlayInput.PollMouseFrame();
            CoopLocalization.Tick();
            PlayerTwoRuntime.TickUpdate();
            CoopMode.Update();
            CoopDecisions.Tick();
            CoopRevivePrompt.Tick();
            CoopVitals.Tick();
            CoopCombat.Tick();
            CoopPause.Tick();
            CoopComfort.Tick();
            CoopRoundStats.Tick();
        }

        public void LateUpdate()
        {
            PlayerTwoRuntime.TickLateUpdate();
            CoopVisuals.EnforceOverlayCursor();
        }

        public void OnDestroy()
        {
            CoopSession.ResetRound();
        }
    }
}
