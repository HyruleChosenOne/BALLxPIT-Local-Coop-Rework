using System;
using System.IO;
using System.Text;
using BALLxPITLocalCoop.Core;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BALLxPITLocalCoop.UI
{
    /// <summary>
    /// Shared BALL x PIT visual theme palette, TMP font resolution (with French glyph re-check on LevelUpUI sync),
    /// procedural 9-slice ornate frame + single-texture cosmic starfield, and text sanitizer.
    /// </summary>
    public static class OverlayTheme
    {
        public static readonly Color ColBackdrop = new Color(0.045f, 0.025f, 0.048f, 0.94f);
        public static readonly Color ColCosmicVoid = new Color(0.014f, 0.010f, 0.028f, 0.975f);
        public static readonly Color ColPanelFill = new Color(0.118f, 0.071f, 0.098f, 0.985f);
        public static readonly Color ColCosmicPanelFill = new Color(0.055f, 0.035f, 0.072f, 0.90f);
        public static readonly Color ColPanelWell = new Color(0.078f, 0.045f, 0.066f, 0.985f);
        public static readonly Color ColCellFill = new Color(0.153f, 0.090f, 0.125f, 0.98f);
        public static readonly Color ColBronzeOuter = new Color(0.580f, 0.392f, 0.243f, 1f);
        public static readonly Color ColBronzeBright = new Color(0.792f, 0.588f, 0.365f, 1f);
        public static readonly Color ColBronzeDark = new Color(0.282f, 0.176f, 0.125f, 0.98f);
        public static readonly Color ColGoldHighlight = new Color(0.910f, 0.714f, 0.310f, 1f);
        public static readonly Color ColReadyGreen = new Color(0.365f, 0.808f, 0.510f, 1f);
        public static readonly Color ColHoverGlow = new Color(0.306f, 0.184f, 0.220f, 0.98f);
        public static readonly Color ColTextParchment = new Color(0.949f, 0.902f, 0.835f, 1f);
        public static readonly Color ColTextMuted = new Color(0.753f, 0.659f, 0.690f, 1f);

        private static TMP_FontAsset _headerFont;
        private static TMP_FontAsset _bodyFont;
        private static TMP_FontAsset _dynamicBodyFont;
        private static bool _bodyFontSupportsFrench;
        private static bool _syncedLevelUpFonts;
        private static bool _loggedFontPick;

        private static Sprite _nativeSlicedFrame;
        private static Sprite _ornateFrameSprite;
        private static Texture2D _ornateFrameTex;
        private static Sprite _starfieldSprite;
        private static Texture2D _starfieldTex;

        private static readonly StringBuilder SanitizeSb = new StringBuilder(256);

        public static TMP_FontAsset DisplayFont => _headerFont;
        public static TMP_FontAsset ReadableFont => _bodyFont;
        public static Sprite OrnateFrameSprite => _ornateFrameSprite;
        public static Sprite NativeSlicedFrame => _nativeSlicedFrame;
        public static Sprite StarfieldSprite => _starfieldSprite;
        public static bool BodyFontSupportsFrench => _bodyFontSupportsFrench;

        public static Color PlayerAccent(int slot) => PlayerPalette.GetPlayerColor(slot);

        public static Color PlayerBannerTint(int slot)
        {
            Color c = PlayerPalette.GetPlayerColor(slot);
            return new Color(0.055f + c.r * 0.17f, 0.045f + c.g * 0.14f, 0.065f + c.b * 0.18f, 0.98f);
        }

        public static void EnsureFonts(Component source = null)
        {
            if (_bodyFont == null)
            {
                ResolveFontsAndSprites(source);
            }
        }

        public static void ResolveFontsAndSprites(Component source)
        {
            TMP_FontAsset fallbackSample = source != null
                ? source.GetComponentInChildren(Il2CppType.Of<TextMeshProUGUI>(), includeInactive: true)?.TryCast<TextMeshProUGUI>()?.font
                : null;
            if (fallbackSample == null && BaseUI.I != null)
            {
                fallbackSample = BaseUI.I.GetComponentInChildren(Il2CppType.Of<TextMeshProUGUI>(), includeInactive: true)?.TryCast<TextMeshProUGUI>()?.font;
            }
            if (fallbackSample == null && MainMenuUI.I != null)
            {
                fallbackSample = MainMenuUI.I.GetComponentInChildren(Il2CppType.Of<TextMeshProUGUI>(), includeInactive: true)?.TryCast<TextMeshProUGUI>()?.font;
            }

            _headerFont = fallbackSample;
            _bodyFont = null;
            TMP_FontAsset firstAnyFont = null;

            try
            {
                Il2CppReferenceArray<UnityEngine.Object> allFonts = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>());
                if (allFonts != null)
                {
                    StringBuilder fontLog = _loggedFontPick ? null : new StringBuilder();
                    foreach (UnityEngine.Object obj in allFonts)
                    {
                        TMP_FontAsset f = obj?.TryCast<TMP_FontAsset>();
                        if (f == null || string.IsNullOrEmpty(f.name)) continue;

                        firstAnyFont ??= f;
                        if (fontLog != null) fontLog.Append(f.name).Append(", ");

                        string low = f.name.ToLowerInvariant();
                        if (low.Contains("title") || low.Contains("header") || low.Contains("display") || low.Contains("gothic") || low.Contains("alagard"))
                        {
                            if (_headerFont == fallbackSample || _headerFont == null) _headerFont = f;
                        }
                        if (!low.Contains("pixel") && !low.Contains("alagard") && !low.Contains("bauhaus")
                            && (low.Contains("body") || low.Contains("regular") || low.Contains("sans") || low.Contains("noto") || low.Contains("liberation")))
                        {
                            _bodyFont ??= f;
                        }
                    }
                    if (fontLog != null)
                    {
                        _loggedFontPick = true;
                        Plugin.Logger?.LogDebug("Discovered TMP_FontAssets: " + fontLog);
                    }
                }
            }
            catch { }

            _bodyFont ??= fallbackSample ?? firstAnyFont ?? _headerFont;
            if (_bodyFont == null)
            {
                try { _bodyFont = TMP_Settings.defaultFontAsset; } catch { }
            }
            if (_bodyFont == null)
            {
                const string windowsSegoe = "C:\\Windows\\Fonts\\segoeui.ttf";
                try
                {
                    if (File.Exists(windowsSegoe))
                    {
                        _dynamicBodyFont = TMP_FontAsset.CreateFontAsset(
                            windowsSegoe, 0, 90, 9,
                            UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                            1024, 1024, AtlasPopulationMode.Dynamic);
                        _bodyFont = _dynamicBodyFont;
                        if (_dynamicBodyFont != null)
                        {
                            UnityEngine.Object.DontDestroyOnLoad(_dynamicBodyFont);
                        }
                    }
                }
                catch { }
            }

            _headerFont ??= _bodyFont;
            UpdateFrenchGlyphSupport();

            Plugin.Logger?.LogInfo($"Overlay fonts: Header={_headerFont?.name}, Body={_bodyFont?.name}, FrenchGlyphs={_bodyFontSupportsFrench}");

            if (_nativeSlicedFrame == null)
            {
                try
                {
                    Il2CppReferenceArray<UnityEngine.Object> images = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Image>());
                    if (images != null)
                    {
                        foreach (UnityEngine.Object obj in images)
                        {
                            Image img = obj?.TryCast<Image>();
                            Sprite sp = img?.sprite;
                            if (sp != null && sp.border != Vector4.zero && sp.rect.width >= 64f && sp.rect.height >= 48f)
                            {
                                string low = sp.name.ToLowerInvariant();
                                if (low.Contains("frame") || low.Contains("panel") || low.Contains("border") || low.Contains("box"))
                                {
                                    _nativeSlicedFrame = sp;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            CreateProceduralSprites();
            RefreshFontsFromLevelUpUI();
        }

        public static bool RefreshFontsFromLevelUpUI()
        {
            if (_syncedLevelUpFonts || LevelUpUI.I == null)
            {
                return false;
            }
            try
            {
                TMP_FontAsset lvlTitleFont = LevelUpUI.I.TxtTitleMain?.font;
                TMP_FontAsset lvlDescFont = LevelUpUI.I.LocDetailsDesc?.GetComponent<TextMeshProUGUI>()?.font
                    ?? LevelUpUI.I.TxtCurGold?.font;

                bool changed = false;
                if (lvlTitleFont != null && lvlTitleFont != _headerFont)
                {
                    _headerFont = lvlTitleFont;
                    changed = true;
                }
                if (lvlDescFont != null && lvlDescFont != _bodyFont)
                {
                    _bodyFont = lvlDescFont;
                    changed = true;
                }

                // Re-check French glyph support whenever BodyFont updates!
                UpdateFrenchGlyphSupport();
                _syncedLevelUpFonts = _headerFont != null && _bodyFont != null;

                if (changed)
                {
                    Plugin.Logger?.LogInfo($"Synced native LevelUpUI fonts: Header={_headerFont?.name}, Body={_bodyFont?.name}, FrenchGlyphs={_bodyFontSupportsFrench}");
                }
                return changed;
            }
            catch
            {
                return false;
            }
        }

        private static void UpdateFrenchGlyphSupport()
        {
            _bodyFontSupportsFrench = false;
            if (_bodyFont != null)
            {
                try
                {
                    _bodyFontSupportsFrench = _bodyFont.HasCharacters("ÉÈÊËéèêëÀÂÄàâäÎÏîïÔÖôöÙÛÜùûüÇçŒœ");
                }
                catch { }
            }
        }

        private static void CreateProceduralSprites()
        {
            if (_ornateFrameSprite == null)
            {
                try
                {
                    const int size = 32;
                    Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;

                    Color clear = new Color(0f, 0f, 0f, 0f);
                    Color outline = new Color(0.08f, 0.04f, 0.05f, 0.96f);

                    for (int y = 0; y < size; y++)
                    {
                        for (int x = 0; x < size; x++)
                        {
                            int dx = x < size / 2 ? x : size - 1 - x;
                            int dy = y < size / 2 ? y : size - 1 - y;
                            int d = Math.Min(dx, dy);
                            Color c = d switch
                            {
                                0 => outline,
                                1 => ColBronzeOuter,
                                2 => ColBronzeBright,
                                3 => ColBronzeDark,
                                _ => clear
                            };

                            if (dx < 9 && dy < 9)
                            {
                                if ((dx == 1 || dy == 1) && (dx < 8 && dy < 8)) c = ColBronzeBright;
                                if (dx + dy == 5 || dx + dy == 6) c = ColBronzeBright;
                                if ((dx == 4 && dy == 4) || (dx == 3 && dy == 6) || (dx == 6 && dy == 3)) c = ColGoldHighlight;
                                if ((dx == 2 && dy == 7) || (dx == 7 && dy == 2)) c = ColBronzeOuter;
                                if (dx == 5 && dy == 5) c = ColBronzeDark;
                            }
                            if ((dx >= 14 && dx <= 15) && dy == 2) c = ColGoldHighlight;
                            if ((dy >= 14 && dy <= 15) && dx == 2) c = ColGoldHighlight;

                            tex.SetPixel(x, y, c);
                        }
                    }
                    tex.Apply();
                    _ornateFrameTex = tex;
                    _ornateFrameSprite = Sprite.Create(
                        tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                        100f, 0u, SpriteMeshType.FullRect, new Vector4(10f, 10f, 10f, 10f));
                    UnityEngine.Object.DontDestroyOnLoad(tex);
                    UnityEngine.Object.DontDestroyOnLoad(_ornateFrameSprite);
                }
                catch { }
            }

            // Single procedural starfield texture replaces 95 separate star GameObjects!
            if (_starfieldSprite == null)
            {
                try
                {
                    const int w = 480;
                    const int h = 270;
                    Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;

                    Color clear = new Color(0f, 0f, 0f, 0f);
                    Color[] pixels = new Color[w * h];
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

                    Color[] palette =
                    {
                        new Color(0.95f, 0.97f, 1.00f, 0.90f),
                        new Color(0.55f, 0.85f, 1.00f, 0.85f),
                        new Color(1.00f, 0.84f, 0.52f, 0.80f),
                        new Color(0.84f, 0.68f, 1.00f, 0.75f),
                        new Color(0.50f, 0.72f, 0.92f, 0.45f)
                    };

                    uint seed = 0x5B17C0DEu;
                    for (int i = 0; i < 110; i++)
                    {
                        seed = seed * 1664525u + 1013904223u;
                        int sx = (int)(((seed >> 16) & 0xFFFF) % (uint)(w - 2)) + 1;
                        seed = seed * 1664525u + 1013904223u;
                        int sy = (int)(((seed >> 16) & 0xFFFF) % (uint)(h - 2)) + 1;
                        Color col = palette[i % palette.Length];
                        pixels[sy * w + sx] = col;
                        if (i % 7 == 0)
                        {
                            Color halo = new Color(col.r, col.g, col.b, col.a * 0.45f);
                            pixels[sy * w + (sx - 1)] = halo;
                            pixels[sy * w + (sx + 1)] = halo;
                            pixels[(sy - 1) * w + sx] = halo;
                            pixels[(sy + 1) * w + sx] = halo;
                        }
                    }

                    tex.SetPixels(pixels);
                    tex.Apply();
                    _starfieldTex = tex;
                    _starfieldSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
                    UnityEngine.Object.DontDestroyOnLoad(tex);
                    UnityEngine.Object.DontDestroyOnLoad(_starfieldSprite);
                }
                catch { }
            }
        }

        public static void Shutdown()
        {
            if (_dynamicBodyFont != null)
            {
                UnityEngine.Object.Destroy(_dynamicBodyFont);
                _dynamicBodyFont = null;
            }
            if (_ornateFrameSprite != null)
            {
                UnityEngine.Object.Destroy(_ornateFrameSprite);
                _ornateFrameSprite = null;
            }
            if (_ornateFrameTex != null)
            {
                UnityEngine.Object.Destroy(_ornateFrameTex);
                _ornateFrameTex = null;
            }
            if (_starfieldSprite != null)
            {
                UnityEngine.Object.Destroy(_starfieldSprite);
                _starfieldSprite = null;
            }
            if (_starfieldTex != null)
            {
                UnityEngine.Object.Destroy(_starfieldTex);
                _starfieldTex = null;
            }
            _headerFont = null;
            _bodyFont = null;
            _nativeSlicedFrame = null;
            _bodyFontSupportsFrench = false;
            _syncedLevelUpFonts = false;
            _loggedFontPick = false;
        }

        public static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            bool stripFrench = !_bodyFontSupportsFrench;
            bool needsChange = false;
            int len = text.Length;
            for (int i = 0; i < len; i++)
            {
                char c = text[i];
                if (c == '|'
                    || (c == '{' && i + 4 < len && string.Compare(text, i, "{[x]}", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                    || (c == '[' && i + 4 < len && string.Compare(text, i, "[[x]]", 0, 5, StringComparison.Ordinal) == 0)
                    || (c == ' ' && i + 2 < len && text[i + 1] == ' ' && text[i + 2] == 'x')
                    || (c == 'x' && i + 2 < len && text[i + 1] == ' ' && text[i + 2] == ' ')
                    || IsNonAsciiSanitizable(c, stripFrench))
                {
                    needsChange = true;
                    break;
                }
            }
            if (!needsChange) return text;

            SanitizeSb.Clear();
            if (SanitizeSb.Capacity < len + 16) SanitizeSb.EnsureCapacity(len + 16);

            for (int i = 0; i < len; i++)
            {
                char c = text[i];
                if (c == '{' && i + 4 < len && string.Compare(text, i, "{[x]}", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    AppendNormalizedTimes(SanitizeSb, text, ref i, 5);
                    continue;
                }
                if (c == '[' && i + 4 < len && string.Compare(text, i, "[[x]]", 0, 5, StringComparison.Ordinal) == 0)
                {
                    AppendNormalizedTimes(SanitizeSb, text, ref i, 5);
                    continue;
                }
                if (c == ' ' && i + 2 < len && text[i + 1] == ' ' && text[i + 2] == 'x')
                {
                    if (i + 3 < len && text[i + 3] == ' ')
                    {
                        SanitizeSb.Append(" x ");
                        i += (i + 4 < len && text[i + 4] == ' ') ? 4 : 3;
                        continue;
                    }
                }
                if (c == ' ' && i + 3 < len && text[i + 1] == 'x' && text[i + 2] == ' ' && text[i + 3] == ' ')
                {
                    SanitizeSb.Append(" x ");
                    i += 3;
                    continue;
                }

                switch (c)
                {
                    case '▶': SanitizeSb.Append('>'); break;
                    case '▼': SanitizeSb.Append('v'); break;
                    case '▲': SanitizeSb.Append('^'); break;
                    case '■':
                    case '●':
                    case '✓':
                    case '★': SanitizeSb.Append('*'); break;
                    case '•':
                    case '|':
                    case '—':
                    case '–': SanitizeSb.Append('-'); break;
                    case '→': SanitizeSb.Append("->"); break;
                    case '←': SanitizeSb.Append("<-"); break;
                    case '×': SanitizeSb.Append('x'); break;
                    case '’': SanitizeSb.Append('\''); break;
                    case '«':
                    case '»': SanitizeSb.Append('"'); break;
                    case '\u00A0': SanitizeSb.Append(' '); break;
                    default:
                        if (stripFrench) AppendStrippedFrench(SanitizeSb, c);
                        else SanitizeSb.Append(c);
                        break;
                }
            }
            return SanitizeSb.ToString();
        }

        private static void AppendNormalizedTimes(StringBuilder sb, string text, ref int index, int tokenLen)
        {
            if (sb.Length == 0 || sb[sb.Length - 1] != ' ') sb.Append(' ');
            sb.Append("x ");
            int after = index + tokenLen;
            index = (after < text.Length && text[after] == ' ') ? after : after - 1;
        }

        private static bool IsNonAsciiSanitizable(char c, bool stripFrench)
        {
            if (c < 0x80) return false;
            switch (c)
            {
                case '▶': case '▼': case '▲': case '■': case '●': case '✓': case '★':
                case '•': case '—': case '–': case '→': case '←': case '×': case '’':
                case '«': case '»': case '\u00A0':
                    return true;
                default:
                    if (!stripFrench) return false;
                    return c is 'É' or 'È' or 'Ê' or 'Ë' or 'é' or 'è' or 'ê' or 'ë'
                        or 'À' or 'Â' or 'Ä' or 'à' or 'â' or 'ä'
                        or 'Î' or 'Ï' or 'î' or 'ï'
                        or 'Ô' or 'Ö' or 'ô' or 'ö'
                        or 'Ù' or 'Û' or 'Ü' or 'ù' or 'û' or 'ü'
                        or 'Ç' or 'ç' or 'Œ' or 'œ';
            }
        }

        private static void AppendStrippedFrench(StringBuilder sb, char c)
        {
            switch (c)
            {
                case 'É': case 'È': case 'Ê': case 'Ë': sb.Append('E'); break;
                case 'é': case 'è': case 'ê': case 'ë': sb.Append('e'); break;
                case 'À': case 'Â': case 'Ä': sb.Append('A'); break;
                case 'à': case 'â': case 'ä': sb.Append('a'); break;
                case 'Î': case 'Ï': sb.Append('I'); break;
                case 'î': case 'ï': sb.Append('i'); break;
                case 'Ô': case 'Ö': sb.Append('O'); break;
                case 'ô': case 'ö': sb.Append('o'); break;
                case 'Ù': case 'Û': case 'Ü': sb.Append('U'); break;
                case 'ù': case 'û': case 'ü': sb.Append('u'); break;
                case 'Ç': sb.Append('C'); break;
                case 'ç': sb.Append('c'); break;
                case 'Œ': sb.Append("OE"); break;
                case 'œ': sb.Append("oe"); break;
                default: sb.Append(c); break;
            }
        }
    }
}
