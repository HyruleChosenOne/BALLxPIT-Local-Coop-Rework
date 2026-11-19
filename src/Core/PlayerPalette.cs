using System;
using UnityEngine;

namespace BALLxPITLocalCoop.Core
{
    /// <summary>
    /// Single source of truth for Player 1 & Player 2 colors, palette cycling, and P2 hex sync.
    /// Fixes the v2.2 circular sync bug where changing Player2ColorIndex was immediately vetoed by the old ConfiguredHex.
    /// </summary>
    public static class PlayerPalette
    {
        public const string DefaultP2ColorHex = "26D9FF";
        private static readonly Color DefaultP2Color = new Color(0.15f, 0.85f, 1.00f, 1f);

        private static readonly Color[] PaletteColors =
        {
            new Color(1.00f, 0.36f, 0.58f, 1f), // 0: Rose Pink
            new Color(0.15f, 0.85f, 1.00f, 1f), // 1: Cyan
            new Color(1.00f, 0.27f, 0.27f, 1f), // 2: Red
            new Color(1.00f, 0.72f, 0.18f, 1f), // 3: Gold
            new Color(0.24f, 0.90f, 0.48f, 1f), // 4: Green
            new Color(0.23f, 0.51f, 1.00f, 1f), // 5: Blue
            new Color(0.72f, 0.36f, 1.00f, 1f), // 6: Purple
            new Color(1.00f, 0.48f, 0.18f, 1f), // 7: Orange
            new Color(0.96f, 0.95f, 0.93f, 1f), // 8: White
            new Color(0.08f, 0.79f, 0.58f, 1f), // 9: Emerald
            new Color(1.00f, 0.88f, 0.21f, 1f), // 10: Yellow
            new Color(1.00f, 0.24f, 0.85f, 1f), // 11: Magenta
            new Color(0.85f, 0.13f, 0.27f, 1f), // 12: Crimson
            new Color(0.07f, 0.66f, 0.66f, 1f), // 13: Teal
            new Color(0.66f, 0.71f, 0.77f, 1f)  // 14: Silver
        };

        private static readonly string[] PaletteHex =
        {
            "FF5C94", "26D9FF", "FF4444", "FFB82E", "3CE67A",
            "3B82FF", "B85CFF", "FF7A2F", "F5F2EE", "14C993",
            "FFE135", "FF3ED8", "D92045", "12A8A8", "A8B4C4"
        };

        private static readonly string[] PaletteNamesEn =
        {
            "Rose Pink", "Cyan", "Red", "Gold", "Green",
            "Blue", "Purple", "Orange", "White", "Emerald",
            "Yellow", "Magenta", "Crimson", "Teal", "Silver"
        };

        private static readonly string[] PaletteNamesFr =
        {
            "Rose", "Cyan", "Rouge", "Or", "Vert",
            "Bleu", "Violet", "Orange", "Blanc", "Emeraude",
            "Jaune", "Magenta", "Cramoisi", "Turquoise", "Argent"
        };

        private static Color _p2Color = DefaultP2Color;
        private static string _p2Hex = DefaultP2ColorHex;
        private static bool _hasCustomP2Hex;
        private static string _lastInvalidHex;

        public static int ColorCount => PaletteColors.Length;
        public static Color PlayerTwoColor => _p2Color;
        public static Color PlayerTwoLineColor => new Color(_p2Color.r, _p2Color.g, _p2Color.b, 0.72f);
        public static Color PlayerTwoRingColor => new Color(_p2Color.r, _p2Color.g, _p2Color.b, 0.85f);

        public static Color ColorAt(int index) => PaletteColors[Mathf.Clamp(index, 0, ColorCount - 1)];
        public static string ColorHex(int index) => PaletteHex[Mathf.Clamp(index, 0, ColorCount - 1)];

        public static string ColorName(int index, bool french)
        {
            int clamped = Mathf.Clamp(index, 0, ColorCount - 1);
            return french ? PaletteNamesFr[clamped] : PaletteNamesEn[clamped];
        }

        public static int GetPlayerColorIndex(int slot)
        {
            int raw = slot == 0
                ? (ModConfig.Player1ColorIndex?.Value ?? 0)
                : (ModConfig.Player2ColorIndex?.Value ?? 1);
            return Mathf.Clamp(raw, 0, ColorCount - 1);
        }

        public static Color GetPlayerColor(int slot)
        {
            if (slot == 1)
            {
                return _hasCustomP2Hex ? _p2Color : PaletteColors[GetPlayerColorIndex(1)];
            }
            return PaletteColors[GetPlayerColorIndex(0)];
        }

        public static string GetPlayerColorHex(int slot)
        {
            if (slot == 1 && _hasCustomP2Hex)
            {
                return _p2Hex;
            }
            return ColorHex(GetPlayerColorIndex(slot));
        }

        public static string GetPlayerColorName(int slot, bool french)
        {
            if (slot == 1 && _hasCustomP2Hex)
            {
                return "#" + _p2Hex;
            }
            return ColorName(GetPlayerColorIndex(slot), french);
        }

        public static void SetPlayerColorIndex(int slot, int index)
        {
            int clamped = ((index % ColorCount) + ColorCount) % ColorCount;
            if (slot == 0)
            {
                if (ModConfig.Player1ColorIndex != null)
                {
                    ModConfig.Player1ColorIndex.Value = clamped;
                }
            }
            else
            {
                if (ModConfig.Player2ColorIndex != null)
                {
                    ModConfig.Player2ColorIndex.Value = clamped;
                }
                SyncPlayerTwoHexFromIndex(save: false);
            }
            ModConfig.Save();
        }

        public static void CyclePlayerColor(int slot, int delta)
        {
            SetPlayerColorIndex(slot, GetPlayerColorIndex(slot) + delta);
        }

        public static void SyncPlayerTwoHexFromIndex(bool save = true)
        {
            int idx = GetPlayerColorIndex(1);
            string hex = ColorHex(idx);
            if (ModConfig.P2ColorHex != null)
            {
                ModConfig.P2ColorHex.Value = hex;
            }
            _p2Color = PaletteColors[idx];
            _p2Hex = hex;
            _hasCustomP2Hex = false;
            _lastInvalidHex = null;
            if (save)
            {
                ModConfig.Save();
            }
        }

        public static void RefreshFromConfig(bool preferIndexOnStartup)
        {
            int idx = GetPlayerColorIndex(1);
            string indexHex = ColorHex(idx);
            string raw = ModConfig.P2ColorHex?.Value?.Trim();

            if (preferIndexOnStartup
                && idx != 1
                && (string.IsNullOrWhiteSpace(raw)
                    || string.Equals(raw.TrimStart('#'), DefaultP2ColorHex, StringComparison.OrdinalIgnoreCase)))
            {
                SyncPlayerTwoHexFromIndex(save: false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(raw))
            {
                string withHash = raw.StartsWith("#", StringComparison.Ordinal) ? raw : "#" + raw;
                if (ColorUtility.TryParseHtmlString(withHash, out Color parsed))
                {
                    parsed.a = 1f;
                    string formatted = FormatHex(parsed);
                    _p2Color = parsed;
                    _p2Hex = formatted;
                    _lastInvalidHex = null;

                    int matchedPalette = FindPaletteIndex(formatted);
                    if (matchedPalette >= 0)
                    {
                        if (ModConfig.Player2ColorIndex != null)
                        {
                            ModConfig.Player2ColorIndex.Value = matchedPalette;
                        }
                        _hasCustomP2Hex = false;
                    }
                    else
                    {
                        _hasCustomP2Hex = !string.Equals(formatted, indexHex, StringComparison.OrdinalIgnoreCase);
                    }
                    return;
                }

                if (_lastInvalidHex != raw)
                {
                    _lastInvalidHex = raw;
                    Plugin.Logger?.LogWarning($"Invalid P2 color '{raw}'. Expected RGB hex such as {DefaultP2ColorHex}; falling back to palette color {indexHex}.");
                }
            }

            _p2Color = PaletteColors[idx];
            _p2Hex = indexHex;
            _hasCustomP2Hex = false;
        }

        private static int FindPaletteIndex(string hex)
        {
            for (int i = 0; i < PaletteHex.Length; i++)
            {
                if (string.Equals(PaletteHex[i], hex, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private static string FormatHex(Color c)
        {
            int r = Math.Clamp((int)Math.Round(c.r * 255f, MidpointRounding.AwayFromZero), 0, 255);
            int g = Math.Clamp((int)Math.Round(c.g * 255f, MidpointRounding.AwayFromZero), 0, 255);
            int b = Math.Clamp((int)Math.Round(c.b * 255f, MidpointRounding.AwayFromZero), 0, 255);
            return $"{r:X2}{g:X2}{b:X2}";
        }

        public static string GetSummary() => $"P1={ColorHex(GetPlayerColorIndex(0))}, P2={_p2Hex}";
    }
}
