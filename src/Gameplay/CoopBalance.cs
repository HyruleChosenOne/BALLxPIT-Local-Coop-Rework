using System;
using System.Reflection;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.PlayerTwo;
using BALLxPITLocalCoop.UI;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BALLxPITLocalCoop.Gameplay
{
    /// <summary>
    /// Scales incoming enemy/boss damage while Player 2 is active in battle without modifying character/world stats.
    /// Color palette storage has been moved cleanly to <see cref="PlayerPalette"/>.
    /// </summary>
    public static class CoopBalance
    {
        public const int PresetCount = 4;

        private static bool _installed;

        public static bool AutoPickMaxed => ModConfig.AutoPickWhenMaxed?.Value ?? false;
        public static int ColorCount => PlayerPalette.ColorCount;

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            MethodInfo target = AccessTools.Method(
                typeof(GridPieceInst),
                nameof(GridPieceInst.Damage),
                new[] { typeof(int), typeof(DamageType), typeof(HitType) });

            if (HarmonyHelper.TryPatch(harmony, target, prefix: new HarmonyMethod(typeof(CoopBalance), nameof(BeforeDamage)), label: "GridPieceInst.Damage (CoopBalance)"))
            {
                bool enabled = ModConfig.BalanceEnabled?.Value ?? false;
                float enemy = ModConfig.EnemyResistance?.Value ?? 1.8f;
                float boss = ModConfig.BossResistance?.Value ?? 2.0f;
                if (enabled)
                {
                    log?.LogInfo($"CoopBalance active: regular={enemy:0.##}x, boss={boss:0.##}x.");
                }
                else
                {
                    log?.LogInfo("CoopBalance installed (currently disabled / 1.0x Vanilla).");
                }
            }
            else
            {
                log?.LogWarning("Could not locate GridPieceInst.Damage; CoopBalance inactive.");
            }
        }

        public static void Shutdown()
        {
            _installed = false;
        }

        public static int CurrentPresetIndex()
        {
            if (ModConfig.BalanceEnabled == null
                || !ModConfig.BalanceEnabled.Value
                || ((ModConfig.EnemyResistance?.Value ?? 1.8f) <= 1.05f && (ModConfig.BossResistance?.Value ?? 2.0f) <= 1.05f))
            {
                return 0;
            }
            float enemy = ModConfig.EnemyResistance?.Value ?? 1.8f;
            if (enemy < 1.6f) return 1;
            if (enemy >= 2.2f) return 3;
            return 2;
        }

        public static string PresetLabel(int presetIndex)
        {
            return presetIndex switch
            {
                0 => CoopLocalization.Text("1.0x Vanilla", "1.0x Vanille"),
                1 => CoopLocalization.Text("1.4x Casual", "1.4x Détente"),
                2 => CoopLocalization.Text("1.8x-2.0x Default", "1.8x-2.0x Défaut"),
                3 => CoopLocalization.Text("2.4x Hardcore", "2.4x Hardcore"),
                _ => CoopLocalization.Text("1.8x-2.0x Default", "1.8x-2.0x Défaut")
            };
        }

        public static string PresetDescription(int presetIndex)
        {
            return presetIndex switch
            {
                0 => CoopLocalization.Text(
                    "No extra co-op enemy/boss HP scaling (1.0x Enemies / 1.0x Bosses).",
                    "Aucun bonus de PV co-op (1.0x Ennemis / 1.0x Boss)."),
                1 => CoopLocalization.Text(
                    "Gentle co-op scaling for relaxed runs (1.4x Enemies / 1.4x Bosses).",
                    "Équilibrage léger pour parties détendues (1.4x Ennemis / 1.4x Boss)."),
                2 => CoopLocalization.Text(
                    "Balanced two-player scaling (1.8x Enemies / 2.0x Bosses).",
                    "Équilibrage standard à deux joueurs (1.8x Ennemis / 2.0x Boss)."),
                3 => CoopLocalization.Text(
                    "Intense endurance challenge (2.4x Enemies / 2.4x Bosses).",
                    "Défi d'endurance intense (2.4x Ennemis / 2.4x Boss)."),
                _ => string.Empty
            };
        }

        public static string CurrentPresetLabel() => PresetLabel(CurrentPresetIndex());

        public static void ApplyPreset(int presetIndex)
        {
            if (ModConfig.BalanceEnabled == null || ModConfig.EnemyResistance == null || ModConfig.BossResistance == null)
            {
                return;
            }
            switch (((presetIndex % PresetCount) + PresetCount) % PresetCount)
            {
                case 0:
                    ModConfig.BalanceEnabled.Value = false;
                    ModConfig.EnemyResistance.Value = 1.0f;
                    ModConfig.BossResistance.Value = 1.0f;
                    break;
                case 1:
                    ModConfig.BalanceEnabled.Value = true;
                    ModConfig.EnemyResistance.Value = 1.4f;
                    ModConfig.BossResistance.Value = 1.4f;
                    break;
                case 3:
                    ModConfig.BalanceEnabled.Value = true;
                    ModConfig.EnemyResistance.Value = 2.4f;
                    ModConfig.BossResistance.Value = 2.4f;
                    break;
                default:
                    ModConfig.BalanceEnabled.Value = true;
                    ModConfig.EnemyResistance.Value = 1.8f;
                    ModConfig.BossResistance.Value = 2.0f;
                    break;
            }
            ModConfig.Save();
        }

        public static void CyclePreset(int delta = 1)
        {
            int next = (((CurrentPresetIndex() + delta) % PresetCount) + PresetCount) % PresetCount;
            ApplyPreset(next);
        }

        public static void ToggleAutoPickMaxed()
        {
            if (ModConfig.AutoPickWhenMaxed == null)
            {
                return;
            }
            ModConfig.AutoPickWhenMaxed.Value = !ModConfig.AutoPickWhenMaxed.Value;
            ModConfig.Save();
        }

        // Thin forwarding helpers for palette ergonomics across UI modules.
        public static int GetPlayerColorIndex(int slot) => PlayerPalette.GetPlayerColorIndex(slot);
        public static Color GetPlayerColor(int slot) => PlayerPalette.GetPlayerColor(slot);
        public static Color ColorAt(int index) => PlayerPalette.ColorAt(index);
        public static string ColorHex(int index) => PlayerPalette.ColorHex(index);
        public static string GetPlayerColorHex(int slot) => PlayerPalette.GetPlayerColorHex(slot);
        public static string ColorName(int index, bool french) => PlayerPalette.ColorName(index, french);
        public static string GetPlayerColorName(int slot, bool french) => PlayerPalette.GetPlayerColorName(slot, french);
        public static void SetPlayerColorIndex(int slot, int index) => PlayerPalette.SetPlayerColorIndex(slot, index);
        public static void CyclePlayerColor(int slot, int delta) => PlayerPalette.CyclePlayerColor(slot, delta);

        internal static bool IsAllyPiece(GridPieceType type) =>
            type == GridPieceType.kAlly
            || type == GridPieceType.kAllyGold
            || type == GridPieceType.kAllyHealer
            || type == GridPieceType.kAllyArcher;

        private static void BeforeDamage(GridPieceInst __instance, ref int __0)
        {
            if (!CoopMode.Enabled
                || !(ModConfig.BalanceEnabled?.Value ?? false)
                || __instance == null
                || __0 <= 0
                || CoopContext.PlayerTwo == null)
            {
                return;
            }
            GridPieceType type = __instance.Type;
            if (IsAllyPiece(type) || type == GridPieceType.kGemspring || type == GridPieceType.kNum)
            {
                return;
            }
            float multiplier = type.IsBoss()
                ? (ModConfig.BossResistance?.Value ?? 2.0f)
                : (ModConfig.EnemyResistance?.Value ?? 1.8f);
            __0 = ScaleDamage(__0, multiplier);
        }

        public static int ScaleDamage(int amount, float multiplier)
        {
            if (amount <= 0 || float.IsNaN(multiplier) || float.IsInfinity(multiplier) || multiplier <= 1f)
            {
                return amount;
            }
            return Math.Max(1, (int)Math.Round(amount / Math.Min(multiplier, 4f), MidpointRounding.AwayFromZero));
        }
    }
}
