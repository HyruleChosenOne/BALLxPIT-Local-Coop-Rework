using System;
using System.Collections.Generic;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.Input;
using BALLxPITLocalCoop.PlayerTwo;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Rewired;
using Shapes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BALLxPITLocalCoop.UI
{
    /// <summary>
    /// Run-end summary report persisted in <see cref="CoopProgression.ProgressData"/>.
    /// Uses 64-bit <see cref="TopBallDps"/> and records real <see cref="EarnedCharXp"/>.
    /// </summary>
    public sealed class CoopRunReport
    {
        public bool Victory { get; set; }
        public int Phase { get; set; }
        public int[] Characters { get; set; } = new int[2];
        public long[] Damage { get; set; } = new long[2];
        public long[] BossDamage { get; set; } = new long[2];
        public int[] Kills { get; set; } = new int[2];
        public int[] Pickups { get; set; } = new int[2];
        public int[] RevivesUsed { get; set; } = new int[2];
        public bool[] Rescued { get; set; } = new bool[2];
        public int[] RevivesLeft { get; set; } = new int[2];
        public int[] MaxRevives { get; set; } = new int[2];
        public string[] TopBallName { get; set; } = new string[2];
        public long[] TopBallDps { get; set; } = new long[2];
        public bool[] TopBallEvolved { get; set; } = new bool[2];
        public int MvpSlot { get; set; } = -1;
        public long TeamDamage { get; set; }
        public int TeamKills { get; set; }
        public int NativeKills { get; set; }
        public int EarnedCharXp { get; set; }
    }

    /// <summary>
    /// Renders the dual-player in-game HUD (responsive 4:3-safe layout, properly parented revive hints),
    /// Run Result screen (real earned Town XP, safe EnterEndless recovery), and Challenges viewer
    /// (ModalOverlayState-coordinated so Esc/B never tears down Co-op).
    /// </summary>
    public static class CoopHudAndResults
    {
        private static GameObject _hud;
        private static readonly RectTransform[] _hudCards = new RectTransform[2];
        private static readonly Image[] _frames = new Image[2];
        private static readonly Image[] _headerShades = new Image[2];
        private static readonly Image[] _identityStripes = new Image[2];
        private static readonly Image[] _portraits = new Image[2];
        private static readonly Image[] _bars = new Image[2];
        private static readonly TextMeshProUGUI[] _texts = new TextMeshProUGUI[2];
        private static readonly string[] _cachedNameHex = new string[2];
        private static readonly string[] _cachedNameLang = new string[2];
        private static readonly string[] _cachedNameRaw = new string[2];
        private static readonly TextMeshProUGUI[] _healthLabels = new TextMeshProUGUI[2];
        private static readonly TextMeshProUGUI[] _statusLabels = new TextMeshProUGUI[2];
        private static readonly TextMeshProUGUI[] _hints = new TextMeshProUGUI[2];
        private static readonly Image[,] _skillRims = new Image[2, 16];
        private static readonly Image[,] _skillIcons = new Image[2, 16];
        private static readonly Image[,] _skillLevelBgs = new Image[2, 16];
        private static readonly TextMeshProUGUI[,] _skillLevels = new TextMeshProUGUI[2, 16];
        private static RectTransform _xpRect;
        private static TextMeshProUGUI _experienceLabel;
        private static GameObject _toastObj;
        private static TextMeshProUGUI _toastLabel;
        private static string _toastMessage;
        private static float _toastUntil;
        private static bool _nativeSkin;
        private static float _nextHud;
        private static int _lastScreenW;
        private static int _lastScreenH;

        private static readonly Dictionary<int, GameObject> HiddenNative = new Dictionary<int, GameObject>();
        private static readonly Dictionary<int, bool> NativeActive = new Dictionary<int, bool>();

        private static readonly DecisionGate Gate = new DecisionGate();
        private static readonly int[] ChallengeIndex = new int[2];
        private static readonly float[] NextInput = new float[2];
        private static string[] _cachedChallengeEntries;
        private static Sprite[] _cachedChallengeIcons;
        private static int _cachedUnlockedCharCount;
        private static int _cachedTotalCharCount;

        private static float _resultAt;
        private static float _resultInputAt;
        private static int _victoryChoice = 1;

        public static bool ResultShown { get; private set; }
        public static bool ResultAck { get; private set; }
        public static bool ChallengesOpen { get; private set; }

        public static void ShowToast(string message, float duration = 3.0f)
        {
            _toastMessage = message;
            _toastUntil = Time.unscaledTime + Math.Max(0.5f, duration);
        }

        public static void ResetRound()
        {
            ResultShown = false;
            ResultAck = false;
            _resultAt = 0f;
            _resultInputAt = 0f;
            _victoryChoice = 1;
            Array.Clear(_cachedNameHex, 0, _cachedNameHex.Length);
            Array.Clear(_cachedNameLang, 0, _cachedNameLang.Length);
            Array.Clear(_cachedNameRaw, 0, _cachedNameRaw.Length);
            Gate.Reset();
            RestoreNativeHud();
            ModalOverlayState.ClearIf(ActiveModalOverlay.Result);
        }

        public static void Shutdown()
        {
            ResetRound();
            CloseChallenges();
            if (HarmonyHelper.IsUnityAlive(_hud))
            {
                UnityEngine.Object.Destroy(_hud);
            }
            _hud = null;
            Array.Clear(_hudCards, 0, _hudCards.Length);
            Array.Clear(_frames, 0, _frames.Length);
            Array.Clear(_headerShades, 0, _headerShades.Length);
            Array.Clear(_identityStripes, 0, _identityStripes.Length);
            Array.Clear(_portraits, 0, _portraits.Length);
            Array.Clear(_bars, 0, _bars.Length);
            Array.Clear(_texts, 0, _texts.Length);
            Array.Clear(_healthLabels, 0, _healthLabels.Length);
            Array.Clear(_statusLabels, 0, _statusLabels.Length);
            Array.Clear(_hints, 0, _hints.Length);
            Array.Clear(_skillRims, 0, _skillRims.Length);
            Array.Clear(_skillIcons, 0, _skillIcons.Length);
            Array.Clear(_skillLevelBgs, 0, _skillLevelBgs.Length);
            Array.Clear(_skillLevels, 0, _skillLevels.Length);
            _xpRect = null;
            _experienceLabel = null;
            _toastObj = null;
            _toastLabel = null;
            _toastMessage = null;
            _toastUntil = 0f;
            _nativeSkin = false;
            _nextHud = 0f;
            _lastScreenW = _lastScreenH = 0;
        }

        // --- Native HUD Hiding ---

        private static void HideNative(GameObject obj)
        {
            if (!HarmonyHelper.IsUnityAlive(obj)) return;
            int id = obj.GetInstanceID();
            if (!HiddenNative.ContainsKey(id))
            {
                HiddenNative[id] = obj;
                NativeActive[id] = obj.activeSelf;
            }
            obj.SetActive(false);
        }

        private static void RestoreNativeHud()
        {
            if (HiddenNative.Count == 0) return;
            foreach (KeyValuePair<int, GameObject> kv in HiddenNative)
            {
                if (HarmonyHelper.IsUnityAlive(kv.Value) && NativeActive.TryGetValue(kv.Key, out bool wasActive))
                {
                    kv.Value.SetActive(wasActive);
                }
            }
            HiddenNative.Clear();
            NativeActive.Clear();
        }

        private static void NativeHudVisibility(bool hide)
        {
            if (!hide)
            {
                RestoreNativeHud();
                return;
            }
            InGameUI ui = InGameUI.I;
            if (ui == null) return;
            if (ui.CvsHealth != null) HideNative(ui.CvsHealth.gameObject);
            if (ui.WrapperWidget != null) HideNative(ui.WrapperWidget.gameObject);
            if (ui.WrapperHUDEquipment != null) HideNative(ui.WrapperHUDEquipment.gameObject);
            if (ui.GrpHeroes != null) HideNative(ui.GrpHeroes.gameObject);
            if (ui.GrpPassives != null) HideNative(ui.GrpPassives.gameObject);
        }

        // --- HUD Creation & Responsive Layout ---

        private static RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max)
        {
            RectTransform rt = new GameObject(name).AddComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();
            rt.SetParent(parent, worldPositionStays: false);
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        private static TextMeshProUGUI Text(Transform parent, Vector2 min, Vector2 max, int size, Color color)
        {
            TextMeshProUGUI txt = Rect("Text", parent, min, max).gameObject.AddComponent(Il2CppType.Of<TextMeshProUGUI>()).Cast<TextMeshProUGUI>();
            txt.font = OverlayTheme.ReadableFont;
            txt.fontSize = size;
            txt.color = color;
            txt.alignment = TextAlignmentOptions.TopLeft;
            txt.raycastTarget = false;
            txt.enableWordWrapping = true;
            txt.richText = true;
            return txt;
        }

        private static Image ImageAt(string name, Transform parent, Vector2 min, Vector2 max, Color color)
        {
            Image img = Rect(name, parent, min, max).gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static void CreateHud()
        {
            if (HarmonyHelper.IsUnityAlive(_hud) || !CoopMode.Enabled) return;
            if (OverlayTheme.ReadableFont == null)
            {
                OverlayTheme.EnsureFonts(InGameUI.I);
            }
            if (OverlayTheme.ReadableFont == null) return;

            _hud = new GameObject("CoopIndividualHUD");
            RectTransform parent = _hud.AddComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();
            Canvas canvas = _hud.AddComponent(Il2CppType.Of<Canvas>()).Cast<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 29900;
            CanvasScaler scaler = _hud.AddComponent(Il2CppType.Of<CanvasScaler>()).Cast<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            UnityEngine.Object.DontDestroyOnLoad(_hud);

            Color rimColor = new Color(0.580f, 0.408f, 0.243f);
            Color innerTrim = new Color(0.345f, 0.220f, 0.165f);
            Color darkPlum = new Color(0.075f, 0.044f, 0.072f, 0.96f);
            Color wellPlum = new Color(0.055f, 0.035f, 0.062f, 0.96f);
            Color headerDark = new Color(0.115f, 0.068f, 0.106f, 0.96f);

            TMP_FontAsset crispFont = OverlayTheme.DisplayFont ?? OverlayTheme.ReadableFont;
            TMP_FontAsset bodyFont = OverlayTheme.ReadableFont ?? crispFont;

            for (int i = 0; i < 2; i++)
            {
                RectTransform card = Rect("Player" + (i + 1), parent, new Vector2(i, 0f), new Vector2(i, 0f));
                card.pivot = new Vector2(i, 0f);
                card.anchoredPosition = new Vector2(i == 0 ? 22f : -22f, 20f);
                card.sizeDelta = new Vector2(540f, 214f);
                _hudCards[i] = card;

                Color pColor = PlayerPalette.GetPlayerColor(i);
                ImageAt("BronzeRim", card, Vector2.zero, Vector2.one, rimColor);
                ImageAt("InnerBevel", card, new Vector2(0.004f, 0.011f), new Vector2(0.996f, 0.989f), innerTrim);
                ImageAt("DarkStone", card, new Vector2(0.008f, 0.022f), new Vector2(0.992f, 0.978f), darkPlum);
                _headerShades[i] = ImageAt("HeaderShade", card, new Vector2(0.012f, 0.508f), new Vector2(0.988f, 0.972f), headerDark);
                _frames[i] = ImageAt("NativeFrame", card, Vector2.zero, Vector2.one, new Color(1f, 0.88f, 0.70f, 0.72f));
                _frames[i].enabled = false;
                _identityStripes[i] = ImageAt("IdentityStripe", card, new Vector2(0.012f, 0.508f), new Vector2(0.022f, 0.972f), pColor);
                ImageAt("Divider", card, new Vector2(0.022f, 0.498f), new Vector2(0.982f, 0.508f), rimColor);

                ImageAt("PortraitRim", card, new Vector2(0.032f, 0.530f), new Vector2(0.184f, 0.946f), rimColor);
                ImageAt("PortraitBacking", card, new Vector2(0.038f, 0.544f), new Vector2(0.178f, 0.932f), wellPlum);
                _portraits[i] = ImageAt("NativeCharacterPortrait", card, new Vector2(0.042f, 0.548f), new Vector2(0.174f, 0.928f), Color.white);
                _portraits[i].preserveAspect = true;

                _texts[i] = Text(card, new Vector2(0.200f, 0.812f), new Vector2(0.968f, 0.948f), 20, new Color(0.957f, 0.902f, 0.835f));
                _texts[i].font = crispFont;
                _texts[i].fontStyle = FontStyles.Bold;
                _texts[i].enableWordWrapping = false;
                _texts[i].enableAutoSizing = true;
                _texts[i].fontSizeMin = 15f;
                _texts[i].fontSizeMax = 21f;
                _texts[i].overflowMode = TextOverflowModes.Ellipsis;
                _texts[i].alignment = TextAlignmentOptions.MidlineLeft;

                ImageAt("HealthRim", card, new Vector2(0.200f, 0.654f), new Vector2(0.968f, 0.794f), rimColor);
                ImageAt("HealthEmpty", card, new Vector2(0.206f, 0.670f), new Vector2(0.962f, 0.778f), new Color(0.130f, 0.040f, 0.068f));
                _bars[i] = ImageAt("HealthFill", card, new Vector2(0.206f, 0.670f), new Vector2(0.962f, 0.778f), pColor);
                ImageAt("HealthGlint", card, new Vector2(0.206f, 0.750f), new Vector2(0.962f, 0.774f), new Color(1f, 1f, 1f, 0.18f));
                _healthLabels[i] = Text(card, new Vector2(0.208f, 0.654f), new Vector2(0.960f, 0.794f), 16, new Color(0.99f, 0.97f, 0.92f));
                _healthLabels[i].font = crispFont;
                _healthLabels[i].alignment = TextAlignmentOptions.Center;
                _healthLabels[i].fontStyle = FontStyles.Bold;

                ImageAt("StatusWell", card, new Vector2(0.200f, 0.522f), new Vector2(0.968f, 0.640f), wellPlum);
                _statusLabels[i] = Text(card, new Vector2(0.214f, 0.522f), new Vector2(0.956f, 0.640f), 14, new Color(0.949f, 0.902f, 0.835f));
                _statusLabels[i].font = bodyFont;
                _statusLabels[i].enableWordWrapping = false;
                _statusLabels[i].enableAutoSizing = true;
                _statusLabels[i].fontSizeMin = 11f;
                _statusLabels[i].fontSizeMax = 14f;
                _statusLabels[i].alignment = TextAlignmentOptions.MidlineLeft;

                ImageAt("BallTray", card, new Vector2(0.020f, 0.268f), new Vector2(0.976f, 0.482f), new Color(0.055f, 0.034f, 0.062f, 0.78f));
                ImageAt("PassTray", card, new Vector2(0.020f, 0.036f), new Vector2(0.976f, 0.250f), new Color(0.055f, 0.034f, 0.062f, 0.78f));

                ImageAt("BallTagBg", card, new Vector2(0.024f, 0.284f), new Vector2(0.064f, 0.466f), new Color(0.10f, 0.24f, 0.30f, 0.95f));
                TextMeshProUGUI ballTag = Text(card, new Vector2(0.024f, 0.284f), new Vector2(0.064f, 0.466f), 13, new Color(0.50f, 0.88f, 1f));
                ballTag.font = crispFont;
                ballTag.alignment = TextAlignmentOptions.Center;
                ballTag.fontStyle = FontStyles.Bold;
                ballTag.text = "B";

                ImageAt("PassTagBg", card, new Vector2(0.024f, 0.052f), new Vector2(0.064f, 0.234f), new Color(0.22f, 0.12f, 0.30f, 0.95f));
                TextMeshProUGUI passTag = Text(card, new Vector2(0.024f, 0.052f), new Vector2(0.064f, 0.234f), 13, new Color(0.84f, 0.66f, 1f));
                passTag.font = crispFont;
                passTag.alignment = TextAlignmentOptions.Center;
                passTag.fontStyle = FontStyles.Bold;
                passTag.text = "P";

                for (int j = 0; j < 16; j++)
                {
                    int col = j % 8;
                    int row = j / 8;
                    float x = 0.072f + col * 0.1125f;
                    float y = row == 0 ? 0.278f : 0.046f;
                    RectTransform slotRect = Rect("Equipment" + j, card, new Vector2(x, y), new Vector2(x + 0.104f, y + 0.194f));
                    _skillRims[i, j] = ImageAt("SlotRim", slotRect, Vector2.zero, Vector2.one, rimColor);
                    ImageAt("SlotStone", slotRect, new Vector2(0.045f, 0.055f), new Vector2(0.955f, 0.945f), new Color(0.065f, 0.042f, 0.085f));
                    _skillIcons[i, j] = ImageAt("NativeSkill", slotRect, new Vector2(0.10f, 0.10f), new Vector2(0.90f, 0.90f), Color.white);
                    _skillIcons[i, j].preserveAspect = true;
                    _skillLevelBgs[i, j] = ImageAt("LevelPill", slotRect, new Vector2(0.56f, 0.03f), new Vector2(0.97f, 0.45f), new Color(0.04f, 0.025f, 0.055f, 0.92f));
                    _skillLevels[i, j] = Text(slotRect, new Vector2(0.56f, 0.02f), new Vector2(0.97f, 0.46f), 12, new Color(0.98f, 0.95f, 0.85f));
                    _skillLevels[i, j].font = crispFont;
                    _skillLevels[i, j].alignment = TextAlignmentOptions.Center;
                    _skillLevels[i, j].fontStyle = FontStyles.Bold;
                    slotRect.gameObject.SetActive(false);
                }

                // Parent revive hint directly under Player card so it hides automatically when Player card hides (e.g. disconnect pause)!
                _hints[i] = Text(card, new Vector2(0f, 1.04f), new Vector2(1f, 1.28f), 20, pColor);
                _hints[i].font = crispFont;
                _hints[i].alignment = i == 0 ? TextAlignmentOptions.BottomLeft : TextAlignmentOptions.BottomRight;
            }

            _xpRect = Rect("SharedExperience", parent, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
            _xpRect.pivot = new Vector2(0.5f, 0f);
            _xpRect.anchoredPosition = new Vector2(0f, 20f);
            _xpRect.sizeDelta = new Vector2(340f, 40f);
            ImageAt("BronzeRim", _xpRect, Vector2.zero, Vector2.one, rimColor);
            ImageAt("InnerBevel", _xpRect, new Vector2(0.006f, 0.04f), new Vector2(0.994f, 0.96f), innerTrim);
            ImageAt("Stone", _xpRect, new Vector2(0.012f, 0.08f), new Vector2(0.988f, 0.92f), new Color(0.065f, 0.044f, 0.078f, 0.97f));
            _experienceLabel = Text(_xpRect, new Vector2(0.04f, 0.08f), new Vector2(0.96f, 0.92f), 17, new Color(0.957f, 0.875f, 0.665f));
            _experienceLabel.alignment = TextAlignmentOptions.Center;
            _experienceLabel.fontStyle = FontStyles.Bold;
            _experienceLabel.enableAutoSizing = true;
            _experienceLabel.fontSizeMin = 12f;
            _experienceLabel.fontSizeMax = 17f;

            RectTransform toastRect = Rect("CoopToast", parent, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f));
            toastRect.pivot = new Vector2(0.5f, 1f);
            toastRect.anchoredPosition = new Vector2(0f, -20f);
            toastRect.sizeDelta = new Vector2(680f, 44f);
            ImageAt("ToastRim", toastRect, Vector2.zero, Vector2.one, rimColor);
            ImageAt("ToastBg", toastRect, new Vector2(0.006f, 0.06f), new Vector2(0.994f, 0.94f), new Color(0.085f, 0.055f, 0.095f, 0.96f));
            _toastLabel = Text(toastRect, new Vector2(0.03f, 0.08f), new Vector2(0.97f, 0.92f), 18, new Color(0.98f, 0.84f, 0.45f));
            _toastLabel.alignment = TextAlignmentOptions.Center;
            _toastLabel.fontStyle = FontStyles.Bold;
            _toastObj = toastRect.gameObject;
            _toastObj.SetActive(false);

            UpdateResponsiveHudSizes();
        }

        /// <summary>
        /// Dynamically scales P1/P2 HUD cards and shared XP bar so 4:3, 16:10, and 16:9 aspect ratios never collide!
        /// </summary>
        private static void UpdateResponsiveHudSizes()
        {
            if (_hudCards[0] == null || _hudCards[1] == null || _xpRect == null) return;
            int sw = Screen.width;
            int sh = Math.Max(1, Screen.height);
            if (sw == _lastScreenW && sh == _lastScreenH) return;
            _lastScreenW = sw;
            _lastScreenH = sh;

            float refW = 1080f * sw / sh;
            float availW = Math.Max(900f, refW - 64f);
            float cardW = Mathf.Clamp(availW * 0.35f, 380f, 540f);
            float xpW = Mathf.Clamp(availW - cardW * 2f - 28f, 180f, 340f);

            for (int i = 0; i < 2; i++)
            {
                _hudCards[i].sizeDelta = new Vector2(cardW, 214f);
            }
            _xpRect.sizeDelta = new Vector2(xpW, 40f);
        }

        private static void ApplyNativeHudSkin()
        {
            if (_nativeSkin || InGameUI.I == null) return;
            Sprite ornate = OverlayTheme.OrnateFrameSprite;
            for (int j = 0; j < 2; j++)
            {
                if (ornate != null && _frames[j] != null)
                {
                    _frames[j].sprite = ornate;
                    _frames[j].type = Image.Type.Sliced;
                    _frames[j].fillCenter = false;
                    _frames[j].enabled = true;
                }
            }
            _nativeSkin = true;
        }

        private static void SetSkillSlot(int slot, int index, UpgradeInfo info, int level)
        {
            if (index >= 16 || info == null) return;
            Image img = _skillIcons[slot, index];
            img.transform.parent.gameObject.SetActive(true);
            info.ApplyIcon(img);
            img.enabled = img.sprite != null;
            int displayLvl = Math.Max(1, level + 1);
            if (_skillRims[slot, index] != null)
                _skillRims[slot, index].color = displayLvl >= 3 ? new Color(0.91f, 0.72f, 0.30f) : new Color(0.580f, 0.408f, 0.243f);
            if (_skillLevelBgs[slot, index] != null)
                _skillLevelBgs[slot, index].enabled = true;
            _skillLevels[slot, index].color = displayLvl >= 3 ? new Color(0.98f, 0.82f, 0.36f) : new Color(0.96f, 0.94f, 0.88f);
            _skillLevels[slot, index].text = displayLvl.ToString();
        }

        private static void SetEmptySkillSlot(int slot, int index)
        {
            if (index >= 16) return;
            Image img = _skillIcons[slot, index];
            img.transform.parent.gameObject.SetActive(true);
            img.enabled = false;
            img.sprite = null;
            if (_skillRims[slot, index] != null)
                _skillRims[slot, index].color = new Color(0.32f, 0.22f, 0.26f, 0.55f);
            if (_skillLevelBgs[slot, index] != null)
                _skillLevelBgs[slot, index].enabled = false;
            _skillLevels[slot, index].text = string.Empty;
        }

        private static void UpdateHud()
        {
            ApplyNativeHudSkin();
            UpdateResponsiveHudSizes();
            if (BattleSaveData.I == null) return;

            _experienceLabel.font = OverlayTheme.DisplayFont ?? OverlayTheme.ReadableFont;
            _experienceLabel.text = $"{CoopLocalization.Text("LV.", "NIV.")} {BattleSaveData.I.UpgradeLvl}  |  XP {BattleSaveData.I.CurXP:0}";

            for (int i = 0; i < 2; i++)
            {
                CharBattleInst charInst = CoopCharacterRuntime.CharacterFor(i);
                if (charInst == null) break;

                Color pColor = PlayerPalette.GetPlayerColor(i);
                string pHex = "#" + PlayerPalette.GetPlayerColorHex(i);
                if (_headerShades[i] != null) _headerShades[i].color = new Color(0.115f, 0.068f, 0.106f, 0.96f);
                if (_identityStripes[i] != null) _identityStripes[i].color = pColor;

                var info = charInst.GetInfo();
                Sprite icon = info?.GetIconSprite();
                if (_portraits[i].sprite != icon) _portraits[i].sprite = icon;

                string langTag = CoopLocalization.Text("P", "J");
                string displayName = CoopMenuUI.CharacterDisplayName(info);
                if (_cachedNameHex[i] != pHex || _cachedNameLang[i] != langTag || _cachedNameRaw[i] != displayName || _texts[i].text == null)
                {
                    _cachedNameHex[i] = pHex;
                    _cachedNameLang[i] = langTag;
                    _cachedNameRaw[i] = displayName;
                    _texts[i].text = OverlayTheme.Sanitize($"<color={pHex}>[{langTag}{i + 1}]</color> {displayName}");
                }

                _healthLabels[i].text = Mathf.CeilToInt(CoopVitals.Life.Health[i]) + " / " + Mathf.CeilToInt(CoopVitals.Life.Maximum[i]);
                float ratio = Mathf.Clamp01(CoopVitals.Life.Health[i] / Mathf.Max(1f, CoopVitals.Life.Maximum[i]));
                _bars[i].rectTransform.anchorMax = new Vector2(0.206f + 0.756f * ratio, 0.778f);
                _bars[i].color = ratio <= 0.25f ? new Color(1f, 0.24f, 0.22f) : pColor;

                string autoLabel = GamepadRouter.Automatic(i)
                    ? $"<color=#78E69C>{CoopLocalization.Text("AUTO ON", "AUTO OUI")}</color>"
                    : $"<color=#A896A0>{CoopLocalization.Text("AUTO OFF", "AUTO NON")}</color>";
                string dpsText = $"<color=#5CD6FF>DPS {CoopRoundStats.FormatDps(CoopRoundStats.GetLiveDps(i))}</color>";
                bool canManualRevive = CoopVitals.Life.CanManualRevive(i);
                int rescuePct = Mathf.RoundToInt(Mathf.Clamp01(CoopVitals.Life.RescueProgress[i]) * 100f);
                bool rescuingNow = canManualRevive && (CoopVitals.ManualRescueInRing[i] || rescuePct > 0);

                _statusLabels[i].text = !CoopVitals.Life.Down(i)
                    ? $"{CoopLocalization.Text("REVIVES", "VIES")} <color=#F4C86A>{CoopVitals.Life.Revives[i]}/{CoopVitals.Life.MaxRevives[i]}</color>  |  {autoLabel}  |  {dpsText}"
                    : (CoopVitals.Life.Revives[i] > 0
                        ? $"<color=#F4C86A>{CoopLocalization.Text("REVIVING...", "RÉANIMATION...")}</color>  |  {dpsText}"
                        : (rescuingNow
                            ? $"<color=#78E69C>{CoopLocalization.Text("REVIVING", "RÉANIMATION")} {rescuePct}%</color>  |  {dpsText}"
                            : $"<color=#F2706B>{CoopLocalization.Text("DOWN", "KO")}</color>  |  {dpsText}"));

                string hint = string.Empty;
                if (canManualRevive)
                {
                    if (CoopVitals.ManualRescueInRing[i])
                    {
                        string keyLabel = GamepadRouter.UsesGamepadForSlot(1 - i) ? "[Y]" : "[E]";
                        int cost = BattleSaveData.I != null ? CoopVitals.Life.ManualReviveGoldCost(BattleSaveData.I.ElapsedTime) : 0;
                        hint = cost > 0
                            ? $"<color=#78E69C>{CoopLocalization.Text("REVIVING", "RÉANIMATION")} {rescuePct}%</color>  <color=#F4C86A>{keyLabel} {cost}G</color>"
                            : $"<color=#78E69C>{CoopLocalization.Text("REVIVING", "RÉANIMATION")} {rescuePct}%</color>";
                    }
                    else
                    {
                        hint = $"<color=#F4C86A>{CoopLocalization.Text("STAND IN RING TO REVIVE", "RESTEZ DANS LE CERCLE")}</color>";
                    }
                }
                _hints[i].text = hint;

                var heroes = CoopCharacterRuntime.HeroesFor(i);
                int heroCount = heroes != null ? Math.Min(8, heroes.Count) : 0;
                int rawBallCap = BuildingMgr.I != null && BuildingMgr.I.NumBallSlots > 0 ? BuildingMgr.I.NumBallSlots : 4;
                int ballCap = Math.Clamp(rawBallCap, Math.Max(4, heroCount), 8);
                for (int j = 0; j < 8; j++)
                {
                    if (j < heroCount) SetSkillSlot(i, j, heroes[j].GetInfo(), heroes[j].Lvl);
                    else if (j < ballCap) SetEmptySkillSlot(i, j);
                    else _skillIcons[i, j].transform.parent.gameObject.SetActive(false);
                }

                var passives = CoopCharacterRuntime.PassivesFor(i);
                int passCount = passives != null ? Math.Min(8, passives.Count) : 0;
                int rawPassCap = BuildingMgr.I != null && BuildingMgr.I.NumPassiveSlots > 0 ? BuildingMgr.I.NumPassiveSlots : 4;
                int passCap = Math.Clamp(rawPassCap, Math.Max(4, passCount), 8);
                for (int k = 0; k < 8; k++)
                {
                    if (k < passCount) SetSkillSlot(i, 8 + k, passives[k].GetInfo(), passives[k].Lvl);
                    else if (k < passCap) SetEmptySkillSlot(i, 8 + k);
                    else _skillIcons[i, 8 + k].transform.parent.gameObject.SetActive(false);
                }
            }
        }

        // --- Run Report & Challenges ---

        public static void ResultReady() => _resultAt = Time.unscaledTime + 0.8f;

        public static CoopRunReport CaptureReport(bool victory, int phase, int first, int second, int earnedCharXp)
        {
            CoopRoundStats.SyncBeforeCapture();
            var topName = new string[2];
            var topDps = new long[2];
            var topEvo = new bool[2];
            for (int s = 0; s < 2; s++)
            {
                CoopRoundStats.GetTopBallDps(s, out topName[s], out topDps[s], out topEvo[s]);
            }
            long[] dmg = (long[])CoopRoundStats.Damage.Clone();
            int[] kills = (int[])CoopRoundStats.Kills.Clone();
            int nativeKills = BattleSaveData.I != null ? BattleSaveData.I.NumKills : 0;
            long totalTeamDmg = dmg[0] + dmg[1] + CoopRoundStats.TeamDamage;
            int totalTeamKills = Math.Max(nativeKills, kills[0] + kills[1] + CoopRoundStats.TeamKills);
            return new CoopRunReport
            {
                Victory = victory,
                Phase = phase,
                Characters = new[] { first, second },
                Damage = dmg,
                BossDamage = (long[])CoopRoundStats.BossDamage.Clone(),
                Kills = kills,
                Pickups = (int[])CoopRoundStats.PickupsCollected.Clone(),
                RevivesUsed = (int[])CoopVitals.Life.RevivesUsed.Clone(),
                Rescued = (bool[])CoopVitals.Life.Rescued.Clone(),
                RevivesLeft = (int[])CoopVitals.Life.Revives.Clone(),
                MaxRevives = (int[])CoopVitals.Life.MaxRevives.Clone(),
                TopBallName = topName,
                TopBallDps = topDps,
                TopBallEvolved = topEvo,
                MvpSlot = CoopRoundStats.MvpSlot(),
                TeamDamage = totalTeamDmg,
                TeamKills = totalTeamKills,
                NativeKills = nativeKills,
                EarnedCharXp = Math.Max(0, earnedCharXp)
            };
        }

        private static bool CanOfferEndless()
        {
            CoopRunReport last = CoopProgression.State.LastResult;
            return last != null
                && last.Victory
                && GameMgr.I != null
                && BattleSaveData.I != null
                && !BattleSaveData.I.IsEndless;
        }

        private static void ShowResult()
        {
            if (!ResultShown && !ResultAck && CoopProgression.State.LastResult != null)
            {
                if (!ChoiceOverlay.EnsureCreated(InGameUI.I)) return;
                ResultShown = true;
                _victoryChoice = CanOfferEndless() ? 1 : 0;
                _resultInputAt = Time.unscaledTime + 0.35f;
                Gate.Reset();
                ModalOverlayState.Set(ActiveModalOverlay.Result);
                ChoiceOverlay.ClearChoices();
                Time.timeScale = 0f;
            }
        }

        public static bool InterceptReturn()
        {
            if (!CoopRoundStats.Complete || ResultAck) return false;
            ShowResult();
            return true;
        }

        public static void OpenChallenges()
        {
            ChallengesOpen = true;
            CoopMenuUI.Selecting = false;
            ChallengeIndex[0] = ChallengeIndex[1] = 0;
            NextInput[0] = NextInput[1] = 0f;
            _cachedChallengeEntries = null;
            _cachedChallengeIcons = null;
            EnsureChallengeListCache();
            ModalOverlayState.Set(ActiveModalOverlay.Challenges);
            ChoiceOverlay.ClearChoices();
        }

        public static void CloseChallenges()
        {
            if (!ChallengesOpen) return;
            ChallengesOpen = false;
            _cachedChallengeEntries = null;
            _cachedChallengeIcons = null;
            ModalOverlayState.ClearIf(ActiveModalOverlay.Challenges);
            ModalOverlayState.ConsumeCancelThisFrame();
            if (CoopMode.Enabled && HarmonyHelper.SafeGetCurScene() == GameScene.kBase)
            {
                CoopMenuUI.ReopenFromChallenges();
            }
            else
            {
                ChoiceOverlay.Hide();
            }
        }

        private static void EnsureChallengeListCache()
        {
            if (_cachedChallengeEntries != null && _cachedChallengeIcons != null) return;
            List<string> items = BuildChallengeList(out List<Sprite> icons);
            _cachedChallengeEntries = items.ToArray();
            _cachedChallengeIcons = icons.ToArray();
        }

        private static List<string> BuildChallengeList(out List<Sprite> icons)
        {
            var entries = new List<string>(48);
            icons = new List<Sprite>(48);
            var chars = new List<CharInfo>(32);
            bool canAccessDlc = CoopDlcAndLevels.SafeCanAccessBallbylonDLC();

            if (InfoDB.I?.Chars != null)
            {
                foreach (CharInfo inf in InfoDB.I.Chars)
                {
                    if (CoopMenuUI.IsPlayableCharacter(inf, canAccessDlc) && !chars.Contains(inf)) chars.Add(inf);
                }
            }
            else if (MetaSaveData.I?.Chars != null)
            {
                foreach (CharMetaInst c in MetaSaveData.I.Chars)
                {
                    CharInfo inf = c?.GetInfo();
                    if (CoopMenuUI.IsPlayableCharacter(inf, canAccessDlc) && !chars.Contains(inf)) chars.Add(inf);
                }
            }
            chars.Sort((a, b) => ((int)a.Type).CompareTo((int)b.Type));

            int unlockedChars = 0;
            foreach (CharInfo inf in chars)
            {
                bool unlocked = CoopDlcAndLevels.Unlocked(inf.Type);
                if (unlocked) unlockedChars++;
                entries.Add(CoopMenuUI.CharacterDisplayName(inf) + "\n" + (unlocked
                    ? $"<color=#78E69C>{CoopLocalization.Text("UNLOCKED", "DÉBLOQUÉ")}</color>"
                    : ("<color=#F2706B>" + CoopDlcAndLevels.CharacterRequirement(inf.Type) + "</color>")));
                icons.Add(inf.GetIconSprite());
            }
            _cachedUnlockedCharCount = unlockedChars;
            _cachedTotalCharCount = Math.Max(1, chars.Count);

            CoopDlcAndLevels.PopulateLevels();
            for (int i = 0; i < CoopDlcAndLevels.Phases.Count; i++)
            {
                bool unlocked = CoopDlcAndLevels.LevelUnlocked(i);
                bool cleared = CoopDlcAndLevels.IsLevelCleared(CoopDlcAndLevels.Phases[i]);
                entries.Add($"{CoopLocalization.Text("Stage", "Niveau")} {i + 1} - {CoopLocalization.StageName(CoopDlcAndLevels.Phases[i])}\n" + (cleared
                    ? $"<color=#F4C86A>{CoopLocalization.Text("CLEARED *", "TERMINÉ *")}</color>"
                    : (unlocked
                        ? $"<color=#78E69C>{CoopLocalization.Text("UNLOCKED", "DÉBLOQUÉ")}</color>"
                        : $"<color=#F2706B>{CoopLocalization.Text("Clear previous stage to unlock", "Terminez le niveau précédent pour débloquer")}</color>")));
                icons.Add(CoopDlcAndLevels.Phases[i].SprIcon);
            }
            return entries;
        }

        // --- Tick & Render ---

        public static void Tick()
        {
            CreateHud();
            bool inBattle = CoopMode.Enabled && HarmonyHelper.IsInGameScene();
            bool showHud = CoopVitals.Active && CoopCharacterRuntime.IndependentReady && !ResultShown && inBattle;
            bool showToastOnly = inBattle && GameMgr.I != null && GameMgr.I.CurState == GameState.kPaused && !string.IsNullOrEmpty(CoopPause.DisconnectNotice);
            NativeHudVisibility(inBattle);

            if (HarmonyHelper.IsUnityAlive(_hud))
            {
                _hud.SetActive(showHud || showToastOnly);
                if (_hudCards[0] != null) _hudCards[0].gameObject.SetActive(showHud);
                if (_hudCards[1] != null) _hudCards[1].gameObject.SetActive(showHud);
                if (_xpRect != null) _xpRect.gameObject.SetActive(showHud);

                if (_toastObj != null)
                {
                    string toast = !string.IsNullOrEmpty(CoopPause.DisconnectNotice)
                        ? CoopPause.DisconnectNotice
                        : (Time.unscaledTime < _toastUntil ? _toastMessage : null);
                    bool showToast = !string.IsNullOrEmpty(toast) && (showHud || showToastOnly);
                    _toastObj.SetActive(showToast);
                    if (showToast && _toastLabel != null)
                    {
                        _toastLabel.text = toast;
                        _toastLabel.color = !string.IsNullOrEmpty(CoopPause.DisconnectNotice)
                            ? new Color(1f, 0.45f, 0.40f)
                            : new Color(0.98f, 0.84f, 0.45f);
                    }
                }
            }

            if (showHud && Time.unscaledTime >= _nextHud)
            {
                _nextHud = Time.unscaledTime + 0.1f;
                UpdateHud();
            }
            if (CoopRoundStats.Complete && !ResultAck && GameMgr.I != null && Time.unscaledTime >= _resultAt)
            {
                ShowResult();
            }

            if (ResultShown)
            {
                if (Application.isFocused) UpdateResultInput();
                if (ResultShown) RenderResult();
            }
            else if (ChallengesOpen)
            {
                if (Application.isFocused) UpdateChallengesInput();
                if (ChallengesOpen) RenderChallenges();
            }
        }

        private static void UpdateResultInput()
        {
            bool offerEndless = CanOfferEndless();
            bool enterEndless = false;
            bool quickRematch = OverlayInput.ConsumeActionClicked("action2");
            bool returnClicked = OverlayInput.ConsumeActionClicked("close");

            if (offerEndless)
            {
                if (OverlayInput.ConsumeActionClicked("pause_0") || OverlayInput.ConsumeActionClicked("confirm_0")) returnClicked = true;
                if (OverlayInput.ConsumeActionClicked("pause_1") || OverlayInput.ConsumeActionClicked("confirm_1") || OverlayInput.ConsumeActionClicked("action1")) enterEndless = true;
                if (OverlayInput.ConsumeActionClicked("pause_2")) quickRematch = true;
            }
            else
            {
                if (OverlayInput.ConsumeActionClicked("pause_0") || OverlayInput.ConsumeActionClicked("confirm_0")) quickRematch = true;
                if (OverlayInput.ConsumeActionClicked("pause_1") || OverlayInput.ConsumeActionClicked("confirm_1")) returnClicked = true;
            }

            if (Time.unscaledTime >= _resultInputAt)
            {
                for (int i = 0; i < 2; i++)
                {
                    IGamepadTemplate pad = GamepadRouter.GetPad(i);
                    bool kbConfirm = i == 0
                        ? (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.Space))
                        : (UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter) || UnityEngine.Input.GetKeyDown(KeyCode.RightControl));
                    bool kbCancel = i == 0 ? UnityEngine.Input.GetKeyDown(KeyCode.Escape) : UnityEngine.Input.GetKeyDown(KeyCode.Backspace);
                    bool kbRematch = (i == 0 && UnityEngine.Input.GetKeyDown(KeyCode.R)) || (pad != null && GamepadRouter.ButtonJustPressed(pad.x));

                    float moveX = pad != null ? GamepadRouter.MenuStick(pad).x : 0f;
                    float kbMove = i == 0
                        ? ((UnityEngine.Input.GetKeyDown(KeyCode.D) || UnityEngine.Input.GetKeyDown(KeyCode.RightArrow) ? 1f : 0f)
                           - (UnityEngine.Input.GetKeyDown(KeyCode.A) || UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow) ? 1f : 0f))
                        : 0f;
                    if (Mathf.Abs(kbMove) > Mathf.Abs(moveX)) moveX = kbMove;
                    if (Mathf.Abs(moveX) > 0.55f && Time.unscaledTime >= NextInput[i])
                    {
                        int maxIdx = offerEndless ? 2 : 1;
                        _victoryChoice = Mathf.Clamp(_victoryChoice + (moveX > 0f ? 1 : -1), 0, maxIdx);
                        NextInput[i] = Time.unscaledTime + 0.18f;
                    }

                    if (kbRematch)
                    {
                        quickRematch = true;
                    }
                    else if (offerEndless)
                    {
                        if ((pad != null && GamepadRouter.ButtonJustPressed(pad.y)) || (i == 0 && UnityEngine.Input.GetKeyDown(KeyCode.E)))
                            enterEndless = true;
                        else if ((pad != null && GamepadRouter.ButtonJustPressed(pad.b)) || kbCancel)
                            returnClicked = true;
                        else if ((pad != null && GamepadRouter.ButtonJustPressed(pad.a)) || kbConfirm)
                        {
                            if (_victoryChoice == 1) enterEndless = true;
                            else if (_victoryChoice == 2) quickRematch = true;
                            else returnClicked = true;
                        }
                    }
                    else
                    {
                        if ((pad != null && GamepadRouter.ButtonJustPressed(pad.b)) || kbCancel)
                            returnClicked = true;
                        else if ((pad != null && GamepadRouter.ButtonJustPressed(pad.a)) || kbConfirm)
                        {
                            if (_victoryChoice == 0) quickRematch = true;
                            else returnClicked = true;
                        }
                    }
                }
            }

            if (quickRematch)
            {
                ModalOverlayState.ClearIf(ActiveModalOverlay.Result);
                CoopMode.QuickRematch();
                return;
            }

            if (enterEndless)
            {
                ResultAck = false;
                ResultShown = false;
                CoopRoundStats.Complete = false;
                _resultAt = 0f;
                Gate.Reset();
                ModalOverlayState.ClearIf(ActiveModalOverlay.Result);
                Time.timeScale = 1f;
                ChoiceOverlay.Hide();
                try
                {
                    GameMgr.I.EnterEndless();
                    Player p2 = CoopCharacterRuntime.SecondPlayer;
                    if (p2 != null && Player.I != null)
                    {
                        Vector3 p1Pos = Player.I.transform.position;
                        var scope = CoopContext.EnterPlayerTwo(p2, CoopContext.ScopeKind.PositionOnly);
                        try
                        {
                            float clampedX = p2.ClampXPos(p1Pos.x + 1.1f);
                            float clampedY = p2.ClampYPos(p1Pos.y);
                            p2.SetPos(new Vector3(clampedX, clampedY, p1Pos.z), snap: true);
                        }
                        finally
                        {
                            CoopContext.Exit(scope);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogWarning("EnterEndless failed: " + ex.Message);
                    CoopRoundStats.Complete = true;
                    ShowResult();
                }
                return;
            }

            if (returnClicked)
            {
                ModalOverlayState.ConsumeCancelThisFrame();
                ModalOverlayState.ClearIf(ActiveModalOverlay.Result);
                ResultAck = true;
                ResultShown = false;
                Time.timeScale = 1f;
                ChoiceOverlay.Hide();
                if (GameMgr.I != null)
                {
                    GameMgr.I.GoToBase();
                }
                else
                {
                    CoopMode.ExitToBaseCleanup();
                    SceneMgr.I?.LoadScene(GameScene.kBase);
                }
            }
        }

        private static void UpdateChallengesInput()
        {
            if (OverlayInput.ConsumeActionClicked("close")
                || OverlayInput.ConsumeActionClicked("confirm_0")
                || UnityEngine.Input.GetKeyDown(KeyCode.Escape)
                || UnityEngine.Input.GetKeyDown(KeyCode.Backspace)
                || UnityEngine.Input.GetKeyDown(KeyCode.C))
            {
                CloseChallenges();
                return;
            }

            EnsureChallengeListCache();
            int count = _cachedChallengeEntries.Length;
            if (OverlayInput.TryGetHoveredOptionIndex(0, out int hOpt) && hOpt >= 0 && hOpt < count) ChallengeIndex[0] = hOpt;
            if (OverlayInput.TryGetClickedOptionIndex(0, out int cOpt) && cOpt >= 0 && cOpt < count) ChallengeIndex[0] = cOpt;

            float move = 0f;
            for (int j = 0; j < 2; j++)
            {
                IGamepadTemplate pad = GamepadRouter.GetPad(j);
                if (pad != null && (GamepadRouter.ButtonJustPressed(pad.b) || GamepadRouter.ButtonJustPressed(pad.x)))
                {
                    CloseChallenges();
                    return;
                }
                if (pad != null)
                {
                    Vector2 stick = GamepadRouter.MenuStick(pad);
                    float padMove = Mathf.Abs(stick.y) >= Mathf.Abs(stick.x) ? -stick.y : stick.x;
                    if (Mathf.Abs(padMove) > Mathf.Abs(move)) move = padMove;
                }
            }

            float kbMove = ((UnityEngine.Input.GetKey(KeyCode.S) || UnityEngine.Input.GetKey(KeyCode.DownArrow)
                            || UnityEngine.Input.GetKey(KeyCode.D) || UnityEngine.Input.GetKey(KeyCode.RightArrow)) ? 1f : 0f)
                         - ((UnityEngine.Input.GetKey(KeyCode.W) || UnityEngine.Input.GetKey(KeyCode.UpArrow)
                            || UnityEngine.Input.GetKey(KeyCode.A) || UnityEngine.Input.GetKey(KeyCode.LeftArrow)) ? 1f : 0f);
            if (Mathf.Abs(kbMove) > Mathf.Abs(move)) move = kbMove;

            if (Mathf.Abs(move) < 0.35f) NextInput[0] = 0f;
            if (Mathf.Abs(move) > 0.45f && Time.unscaledTime >= NextInput[0])
            {
                if (count > 0) ChallengeIndex[0] = (ChallengeIndex[0] + (move > 0f ? 1 : -1) + count) % count;
                NextInput[0] = Time.unscaledTime + 0.16f;
            }
        }

        private static void RenderResult()
        {
            CoopRunReport last = CoopProgression.State.LastResult;
            if (last == null) return;

            bool offerEndless = CanOfferEndless();
            ChoiceOverlay.Show(
                last.Victory
                    ? (offerEndless
                        ? CoopLocalization.Text("VICTORY - EXPEDITION COMPLETE!", "VICTOIRE - EXPÉDITION TERMINÉE !")
                        : CoopLocalization.Text("VICTORY!", "VICTOIRE !"))
                    : CoopLocalization.Text("RUN COMPLETE", "FIN DE LA PARTIE"),
                offerEndless
                    ? CoopLocalization.Text(
                        $"Left/Right: Select   |   [Y/E]: Continue Endless   |   [X/R]: Quick Rematch   |   [B/Esc]: Base   |   Team DMG: {last.TeamDamage:N0}",
                        $"Gauche/Droite : Choisir   |   [Y/E] : Mode Infini   |   [X/R] : Revanche   |   [B/Échap] : Base   |   Dégâts : {last.TeamDamage:N0}")
                    : CoopLocalization.Text(
                        $"Left/Right: Select   |   [X/R]: Quick Rematch   |   [B/Esc]: Return to Base   |   Team DMG: {last.TeamDamage:N0}   |   Kills: {last.TeamKills:N0}",
                        $"Gauche/Droite : Choisir   |   [X/R] : Revanche rapide   |   [B/Échap] : Base   |   Dégâts : {last.TeamDamage:N0}   |   Éliminations : {last.TeamKills:N0}"));

            ChoiceOverlay.SetHeaderButtons(showClose: true);
            int charXp = last.EarnedCharXp > 0 ? last.EarnedCharXp : CoopProgression.LastEarnedCharXp;

            for (int i = 0; i < 2; i++)
            {
                int revLeft = last.RevivesLeft != null && i < last.RevivesLeft.Length ? last.RevivesLeft[i] : 0;
                int revMax = last.MaxRevives != null && i < last.MaxRevives.Length ? last.MaxRevives[i] : 0;
                int revUsed = last.RevivesUsed != null && i < last.RevivesUsed.Length ? last.RevivesUsed[i] : 0;
                long bossDmg = last.BossDamage != null && i < last.BossDamage.Length ? last.BossDamage[i] : 0L;
                int pickups = last.Pickups != null && i < last.Pickups.Length ? last.Pickups[i] : 0;
                string topBall = last.TopBallName != null && i < last.TopBallName.Length && !string.IsNullOrEmpty(last.TopBallName[i]) ? last.TopBallName[i] : "-";
                long topDps = last.TopBallDps != null && i < last.TopBallDps.Length ? last.TopBallDps[i] : 0L;
                bool topEvo = last.TopBallEvolved != null && i < last.TopBallEvolved.Length && last.TopBallEvolved[i];
                string evoTag = topEvo ? $"<color=#FF9F43>[{CoopLocalization.Text("EVO", "ÉVO")}]</color> " : "";
                string mvpBadge = last.MvpSlot == i ? $"  <color=#F4C86A>* {CoopLocalization.Text("MVP", "MVP")} *</color>" : "";

                ChoiceOverlay.SetPlayerHeader(i, $"{CoopLocalization.Text("PLAYER", "JOUEUR")} {i + 1}{mvpBadge}  |  {GamepadRouter.SlotInputLabel(i)}");
                ChoiceOverlay.Details(
                    i,
                    $"<color=#F4C86A>{CoopLocalization.Text("Total Damage:", "Dégâts totaux :")}</color> {last.Damage[i]:N0}"
                        + $"\n<color=#FF8866>{CoopLocalization.Text("Boss Damage:", "Dégâts aux boss :")}</color> {bossDmg:N0}"
                        + $"\n<color=#F2706B>{CoopLocalization.Text("Enemies Defeated:", "Ennemis vaincus :")}</color> {last.Kills[i]:N0}"
                        + $"\n<color=#78E69C>{CoopLocalization.Text("Pickups Collected:", "Objets ramassés :")}</color> {pickups:N0}"
                        + $"\n<color=#C996FF>{CoopLocalization.Text("Top Ball DPS:", "Meilleure balle :")}</color> {evoTag}{topBall} ({topDps:N0}/s)"
                        + $"\n<color=#F4E6D5>{CoopLocalization.Text("Revives:", "Vies :")}</color> {revUsed} {CoopLocalization.Text("used", "utilisées")}  |  {revLeft}/{revMax} {CoopLocalization.Text("left", "restantes")}"
                        + $"\n<color=#5CD6FF>{CoopLocalization.Text("Character XP Earned:", "XP Personnage gagnée :")}</color> +{charXp:N0} XP",
                    ready: false);
                ChoiceOverlay.SetCardConfirmButton(i, null);
            }

            if (offerEndless)
            {
                ChoiceOverlay.SetPauseActionBar(
                    new[]
                    {
                        CoopLocalization.Text("Return to Base [B]", "Retour au village [B]"),
                        CoopLocalization.Text("Continue Endless [Y/E]", "Mode Infini [Y/E]"),
                        CoopLocalization.Text("Restart [X/R]", "Revanche [X/R]")
                    },
                    Mathf.Clamp(_victoryChoice, 0, 2));
            }
            else
            {
                ChoiceOverlay.SetPauseActionBar(
                    new[]
                    {
                        CoopLocalization.Text("Restart [X/R]", "Recommencer [X/R]"),
                        CoopLocalization.Text("Return to Base [B]", "Retour au village [B]")
                    },
                    Mathf.Clamp(_victoryChoice, 0, 1));
            }
        }

        private static void RenderChallenges()
        {
            ChoiceOverlay.Show(
                CoopLocalization.Text("CHALLENGES & UNLOCKS", "DÉFIS & DÉBLOCAGES"),
                CoopLocalization.Text(
                    "Up/Down or Wheel: Scroll   -   [B] / Esc: Back",
                    "Haut/Bas ou Molette : Défiler   -   [B] / Échap : Retour"));
            ChoiceOverlay.CenterSingleCard(0.14f, 0.86f);
            ChoiceOverlay.SetHeaderButtons(showClose: true);

            EnsureChallengeListCache();
            int clearedStages = 0;
            for (int p = 0; p < CoopDlcAndLevels.Phases.Count; p++)
            {
                if (CoopDlcAndLevels.IsLevelCleared(CoopDlcAndLevels.Phases[p])) clearedStages++;
            }
            string summary = CoopLocalization.Text(
                $"<color=#5CD6FF>Runs:</color> {CoopProgression.State.TotalRuns}  -  <color=#78E69C>Wins:</color> {CoopProgression.State.TotalVictories}  -  <color=#F4C86A>Stages:</color> {clearedStages}/{Mathf.Max(1, CoopDlcAndLevels.Phases.Count)}",
                $"<color=#5CD6FF>Parties :</color> {CoopProgression.State.TotalRuns}  -  <color=#78E69C>Victoires :</color> {CoopProgression.State.TotalVictories}  -  <color=#F4C86A>Niveaux :</color> {clearedStages}/{Mathf.Max(1, CoopDlcAndLevels.Phases.Count)}");

            ChoiceOverlay.SetPlayerHeader(0, CoopLocalization.Text("SAVEFILE ARCHIVE & PROGRESS", "ARCHIVES & PROGRESSION"));
            ChoiceOverlay.Card(0, "", ready: false, null);
            ChoiceOverlay.SetEquipmentSummary(0, summary);
            ChoiceOverlay.SetStatusBadge(
                0,
                $"{CoopLocalization.Text("Heroes:", "Héros :")} {_cachedUnlockedCharCount}/{_cachedTotalCharCount}",
                OverlayTheme.ColGoldHighlight);
            ChoiceOverlay.SetCardConfirmButton(0, CoopLocalization.Text("Back [B]", "Retour [B]"));
            ChoiceOverlay.Options(0, _cachedChallengeEntries, _cachedChallengeIcons, ChallengeIndex[0]);
        }
    }

    /// <summary>
    /// Manages P1/P2 aim cursor visibility/tint, cursor locking only when BOTH players use controllers,
    /// and hiding preview renderers for whichever player is currently down.
    /// </summary>
    public static class CoopVisuals
    {
        public static bool TwoControllers { get; set; }

        private static bool _installed;
        private static bool _mouseHidden;
        private static bool _mouseWasVisible;
        private static Image _hiddenImage;
        private static bool _imageWasEnabled;
        private static Image _firstAim;
        private static Image _secondAim;
        private static Sprite _reticleSprite;
        private static Texture2D _reticleTex;
        private static readonly Dictionary<IntPtr, bool> PreviewStates = new Dictionary<IntPtr, bool>(8);
        private static readonly Dictionary<IntPtr, GameObject> PreviewObjects = new Dictionary<IntPtr, GameObject>(8);

        public static void Install(Harmony harmony)
        {
            if (harmony == null || _installed) return;
            _installed = true;

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.PropertySetter(typeof(Cursor), nameof(Cursor.visible)),
                prefix: new HarmonyMethod(typeof(CoopVisuals), nameof(OnSetCursorVisible)),
                label: "Cursor.set_visible");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.PropertySetter(typeof(Cursor), nameof(Cursor.lockState)),
                prefix: new HarmonyMethod(typeof(CoopVisuals), nameof(OnSetCursorLockState)),
                label: "Cursor.set_lockState");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(ControllerCursorUI), nameof(ControllerCursorUI.MyUpdate)),
                postfix: new HarmonyMethod(typeof(CoopVisuals), nameof(OnControllerCursorUpdate)),
                label: "ControllerCursorUI.MyUpdate");
        }

        public static void ResetRound()
        {
            RestoreAllPreviewsAndCursorImage();
            if (HarmonyHelper.IsUnityAlive(_firstAim))
            {
                UnityEngine.Object.Destroy(_firstAim.gameObject);
            }
            _firstAim = null;
            _secondAim = null;
        }

        public static void Shutdown()
        {
            ResetRound();
            if (_mouseHidden)
            {
                _mouseHidden = false;
                Cursor.visible = _mouseWasVisible;
            }
            if (_reticleSprite != null)
            {
                UnityEngine.Object.Destroy(_reticleSprite);
                _reticleSprite = null;
            }
            if (_reticleTex != null)
            {
                UnityEngine.Object.Destroy(_reticleTex);
                _reticleTex = null;
            }
            TwoControllers = false;
            _installed = false;
        }

        private static void RestoreAllPreviewsAndCursorImage()
        {
            foreach (KeyValuePair<IntPtr, bool> kv in PreviewStates)
            {
                if (PreviewObjects.TryGetValue(kv.Key, out GameObject go) && HarmonyHelper.IsUnityAlive(go))
                {
                    try { go.SetActive(kv.Value); } catch { }
                }
            }
            PreviewStates.Clear();
            PreviewObjects.Clear();

            if (HarmonyHelper.IsUnityAlive(_hiddenImage))
            {
                try { _hiddenImage.enabled = _imageWasEnabled; } catch { }
            }
            _hiddenImage = null;
        }

        private static Sprite EnsureReticleSprite()
        {
            if (_reticleSprite != null) return _reticleSprite;
            try
            {
                const int size = 48;
                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                float half = (size - 1) * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = x - half;
                        float dy = y - half;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        float ringAlpha = Mathf.Clamp01(1f - Mathf.Abs(r - 15.5f) / 1.9f);
                        float ax = Mathf.Abs(dx);
                        float ay = Mathf.Abs(dy);
                        bool hTick = ay <= 1.25f && ax >= 11.5f && ax <= 21.5f;
                        bool vTick = ax <= 1.25f && ay >= 11.5f && ay <= 21.5f;
                        float tickAlpha = (hTick || vTick) ? 0.92f : 0f;
                        float centerAlpha = r <= 2.4f ? Mathf.Clamp01((2.4f - r) * 0.9f) : 0f;
                        float a = Mathf.Clamp01(Mathf.Max(ringAlpha, Mathf.Max(tickAlpha, centerAlpha)));
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }
                tex.Apply();
                _reticleTex = tex;
                _reticleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
                UnityEngine.Object.DontDestroyOnLoad(tex);
                UnityEngine.Object.DontDestroyOnLoad(_reticleSprite);
            }
            catch { }
            return _reticleSprite;
        }

        public static bool ShouldForceCursorVisible()
        {
            if (ChoiceOverlay.Visible
                || ModalOverlayState.IsAnyOpen
                || CoopDecisions.IsPending
                || CoopRevivePrompt.Pending
                || CoopHudAndResults.ResultShown
                || CoopMenuUI.Selecting
                || CoopHudAndResults.ChallengesOpen)
            {
                return true;
            }
            return CoopMode.Enabled
                && SceneMgr.I != null
                && HarmonyHelper.IsInGameScene()
                && GameMgr.I != null
                && GameMgr.I.CurState != GameState.kPlaying;
        }

        public static void EnforceOverlayCursor()
        {
            if (!ShouldForceCursorVisible()) return;
            _mouseHidden = false;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private static bool IsActiveTwoControllerBattle()
        {
            if (!TwoControllers || !CoopMode.Enabled || ShouldForceCursorVisible()) return false;
            if (!HarmonyHelper.IsInGameScene() || GameMgr.I == null || GameMgr.I.CurState != GameState.kPlaying || Time.timeScale <= 0f) return false;
            Player p2 = PlayerTwoRuntime.PlayerTwo;
            return p2 != null && p2.gameObject.activeInHierarchy;
        }

        private static void OnSetCursorVisible(ref bool __0)
        {
            if (IsActiveTwoControllerBattle()) __0 = false;
            else if (ShouldForceCursorVisible()) __0 = true;
        }

        private static void OnSetCursorLockState(ref CursorLockMode __0)
        {
            if (ShouldForceCursorVisible()) __0 = CursorLockMode.None;
        }

        private static void OnControllerCursorUpdate(ControllerCursorUI __instance)
        {
            if (IsActiveTwoControllerBattle() && CoopVitals.Down(0) && __instance != null && __instance.Img != null)
            {
                if (!HarmonyHelper.SameNative(_hiddenImage, __instance.Img))
                {
                    if (HarmonyHelper.IsUnityAlive(_hiddenImage)) _hiddenImage.enabled = _imageWasEnabled;
                    _hiddenImage = __instance.Img;
                    _imageWasEnabled = _hiddenImage.enabled;
                }
                _hiddenImage.enabled = false;
            }
        }

        private static void SyncPreviewComponentVisibility(Component comp, bool down)
        {
            if (!HarmonyHelper.IsUnityAlive(comp)) return;
            GameObject go = comp.gameObject;
            IntPtr ptr = HarmonyHelper.Ptr(go);
            if (ptr == IntPtr.Zero) return;

            if (down)
            {
                if (!PreviewStates.ContainsKey(ptr))
                {
                    PreviewStates[ptr] = go.activeSelf;
                    PreviewObjects[ptr] = go;
                }
                go.SetActive(false);
            }
            else if (PreviewStates.TryGetValue(ptr, out bool prev))
            {
                go.SetActive(prev);
                PreviewStates.Remove(ptr);
                PreviewObjects.Remove(ptr);
            }
        }

        internal static void SyncMouse()
        {
            if (CoopMode.Enabled && CoopCharacterRuntime.IndependentReady)
            {
                bool p1Down = CoopVitals.Down(0);
                SyncPreviewComponentVisibility(PreviewIsolation.PlayerOneGamePreview, p1Down);
                SyncPreviewComponentVisibility(PreviewIsolation.PlayerOneBallPreview, p1Down);

                bool p2Down = CoopVitals.Down(1);
                SyncPreviewComponentVisibility(PreviewIsolation.PlayerTwoGamePreview, p2Down);
                SyncPreviewComponentVisibility(PreviewIsolation.PlayerTwoBallPreview, p2Down);

                if (InGameUI.I != null && InGameUI.I.ImgAimCursor != null)
                {
                    InGameUI.I.ImgAimCursor.enabled = !p1Down && (!HarmonyHelper.IsUnityAlive(_firstAim) || !_firstAim.gameObject.activeSelf);
                }

                if (!HarmonyHelper.IsUnityAlive(_secondAim))
                {
                    _secondAim = PreviewIsolation.CursorImage;
                }
                Sprite reticle = EnsureReticleSprite();
                if (HarmonyHelper.IsUnityAlive(_secondAim) && reticle != null)
                {
                    _secondAim.sprite = reticle;
                    _secondAim.overrideSprite = reticle;
                }
            }

            if (ShouldForceCursorVisible())
            {
                _mouseHidden = false;
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
            else if (IsActiveTwoControllerBattle())
            {
                if (!_mouseHidden)
                {
                    _mouseWasVisible = Cursor.visible;
                    _mouseHidden = true;
                }
                Cursor.visible = false;
            }
            else if (_mouseHidden)
            {
                _mouseHidden = false;
                Cursor.visible = _mouseWasVisible;
                if (HarmonyHelper.IsUnityAlive(_hiddenImage))
                {
                    _hiddenImage.enabled = _imageWasEnabled;
                }
                _hiddenImage = null;
            }

            if ((!CoopVitals.Down(0) || !IsActiveTwoControllerBattle()) && _hiddenImage != null)
            {
                if (HarmonyHelper.IsUnityAlive(_hiddenImage))
                {
                    _hiddenImage.enabled = _imageWasEnabled;
                }
                _hiddenImage = null;
            }
        }

        internal static void SyncFirstAim()
        {
            bool show = CoopMode.Enabled && CoopCharacterRuntime.IndependentReady && HarmonyHelper.IsInGameScene()
                && !CoopVitals.Down(0) && !CoopRoundStats.Complete && GamepadRouter.PrimaryUsesPad;
            BallPreview ballPreview = PreviewIsolation.PlayerOneBallPreview ?? BallPreview.I;
            Image img = InGameUI.I != null ? InGameUI.I.ImgAimCursor : null;
            RectTransform xfm = InGameUI.I != null ? InGameUI.I.XfmAimCursor : null;

            if (!show || ballPreview == null || ballPreview.StartLine == null || img == null || img.sprite == null || xfm == null || xfm.parent == null || Camera.main == null)
            {
                if (HarmonyHelper.IsUnityAlive(_firstAim)) _firstAim.gameObject.SetActive(false);
                return;
            }

            if (!HarmonyHelper.IsUnityAlive(_firstAim))
            {
                GameObject go = new GameObject("CoopP1AimCursor") { layer = img.gameObject.layer };
                RectTransform rt = go.AddComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();
                rt.SetParent(xfm.parent, worldPositionStays: false);
                _firstAim = go.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                Sprite reticle = EnsureReticleSprite() ?? img.sprite;
                _firstAim.sprite = reticle;
                _firstAim.overrideSprite = reticle;
                _firstAim.type = img.type;
                _firstAim.preserveAspect = img.preserveAspect;
                _firstAim.fillCenter = img.fillCenter;
                _firstAim.fillMethod = img.fillMethod;
                _firstAim.fillAmount = img.fillAmount;
                _firstAim.fillClockwise = img.fillClockwise;
                _firstAim.fillOrigin = img.fillOrigin;
                _firstAim.useSpriteMesh = img.useSpriteMesh;
                _firstAim.pixelsPerUnitMultiplier = img.pixelsPerUnitMultiplier;
                _firstAim.material = img.material;
                _firstAim.maskable = img.maskable;
                _firstAim.raycastTarget = false;
                _firstAim.color = PlayerPalette.GetPlayerColor(0);
                rt.anchorMin = xfm.anchorMin;
                rt.anchorMax = xfm.anchorMax;
                rt.pivot = xfm.pivot;
                rt.SetSiblingIndex(xfm.GetSiblingIndex() + 1);
            }

            Line startLine = ballPreview.StartLine;
            Vector3 worldEnd = startLine.transform.TransformPoint(startLine.End);
            Vector3 screen = Camera.main.WorldToScreenPoint(worldEnd);
            if (screen.z <= 0f)
            {
                _firstAim.gameObject.SetActive(false);
                return;
            }

            RectTransform cursorRt = _firstAim.rectTransform;
            RectTransform parentRt = cursorRt.parent.Cast<RectTransform>();
            Canvas canvas = _firstAim.canvas;
            Camera uiCam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;

            if (!RectTransformUtility.ScreenPointToWorldPointInRectangle(parentRt, new Vector2(screen.x, screen.y), uiCam, out Vector3 worldPoint))
            {
                _firstAim.gameObject.SetActive(false);
                return;
            }

            cursorRt.sizeDelta = xfm.sizeDelta * 1.15f;
            cursorRt.localScale = xfm.localScale;
            cursorRt.localRotation = xfm.localRotation;
            cursorRt.position = worldPoint;
            _firstAim.color = PlayerPalette.GetPlayerColor(0);
            _firstAim.gameObject.SetActive(true);
        }
    }
}
