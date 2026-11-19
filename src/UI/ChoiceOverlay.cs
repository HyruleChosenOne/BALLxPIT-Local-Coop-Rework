using System;
using System.Collections.Generic;
using BALLxPITLocalCoop.Core;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BALLxPITLocalCoop.UI
{
    /// <summary>
    /// Clean retained UGUI overlay for all co-op screens (Character Select, Stage Select, Settings,
    /// Level-Up / Fuser, Team Revive, Pause, Challenges, Run Summary, and Loading Banner).
    /// Eliminates ~184 dead GameObjects (removed dead FuserShowcase pillars and replaced 95 star GameObjects with 1 sprite),
    /// fixes header/label/button layout overlaps, and resets scroll state cleanly across screen transitions.
    /// </summary>
    public static class ChoiceOverlay
    {
        internal sealed class ScrollOptionRow
        {
            public Image Back;
            public Image Fill;
            public Image IconRim;
            public Image Icon;
            public Image FusionIcon;
            public TextMeshProUGUI Name;
            public Image Ing1Rim;
            public Image Ing1;
            public TextMeshProUGUI IngCross;
            public Image Ing2Rim;
            public Image Ing2;
        }

        internal const float OptionRowHeight = 64f;
        internal const float OptionRowSpacing = 8f;
        internal const float OptionRowStep = OptionRowHeight + OptionRowSpacing;

        private static GameObject _root;
        private static Image _backdropImage;
        private static Image _cosmicStarImage;
        private static TextMeshProUGUI _title;
        private static TextMeshProUGUI _footer;
        private static string _currentTitle = "";

        internal static readonly RectTransform[] Cards = new RectTransform[2];
        private static readonly Image[] CardRims = new Image[2];
        private static readonly Image[] CardFills = new Image[2];
        private static readonly Image[] CardNativeFrames = new Image[2];
        private static readonly Image[] HeaderBanners = new Image[2];
        private static readonly Image[] HeaderStripes = new Image[2];
        private static readonly Image[] HeaderPlayerAccents = new Image[2];
        private static readonly TextMeshProUGUI[] PlayerLabels = new TextMeshProUGUI[2];
        private static readonly TextMeshProUGUI[] Status = new TextMeshProUGUI[2];
        private static readonly Image[] PortraitFrames = new Image[2];
        private static readonly Image[] Icons = new Image[2];
        private static readonly TextMeshProUGUI[] Names = new TextMeshProUGUI[2];
        private static readonly Image[] DescriptionBoxes = new Image[2];
        private static readonly TextMeshProUGUI[] Descriptions = new TextMeshProUGUI[2];
        private static readonly Image[] EquipmentBoxes = new Image[2];
        private static readonly TextMeshProUGUI[] EquipmentLabels = new TextMeshProUGUI[2];
        private static readonly Image[] FooterDividers = new Image[2];
        private static readonly TextMeshProUGUI[] ControlHints = new TextMeshProUGUI[2];

        internal static readonly RectTransform[] OptionViewports = new RectTransform[2];
        private static readonly RectTransform[] OptionContents = new RectTransform[2];
        internal static readonly Image[] OptionScrollTracks = new Image[2];
        private static readonly Image[] OptionScrollThumbs = new Image[2];
        internal static readonly List<ScrollOptionRow>[] OptionRows = { new List<ScrollOptionRow>(16), new List<ScrollOptionRow>(16) };
        internal static readonly float[] OptionScrollY = new float[2];
        internal static readonly float[] OptionViewportHeight = new float[2];
        internal static readonly float[] OptionContentHeight = new float[2];
        private static readonly int[] OptionLastSelected = { -1, -1 };
        private static readonly int[] OptionLastCount = { -1, -1 };

        internal static readonly List<Image>[] GridBacks = { new List<Image>(32), new List<Image>(32) };
        private static readonly List<Image>[] GridFills = { new List<Image>(32), new List<Image>(32) };
        private static readonly List<Image>[] GridIcons = { new List<Image>(32), new List<Image>(32) };
        private static readonly List<TextMeshProUGUI>[] GridBadges = { new List<TextMeshProUGUI>(32), new List<TextMeshProUGUI>(32) };

        internal static readonly Dictionary<string, OverlayInput.ActionButton> ActionButtons = new Dictionary<string, OverlayInput.ActionButton>(16);

        public static bool Visible => _root != null && _root.activeSelf;

        public static bool EnsureCreated(Component source = null)
        {
            if (_root != null) return true;
            Create(source ?? (Component)BaseUI.I ?? (Component)MainMenuUI.I ?? (Component)BaseMgr.I ?? (Component)InGameUI.I);
            return _root != null;
        }

        public static void Create(Component source)
        {
            if (_root != null)
            {
                return;
            }

            OverlayTheme.ResolveFontsAndSprites(source);
            if (OverlayTheme.ReadableFont == null)
            {
                Plugin.Logger?.LogWarning("ChoiceOverlay.Create deferred: ReadableFont not yet available.");
                return;
            }

            TMP_FontAsset headerFont = OverlayTheme.DisplayFont ?? OverlayTheme.ReadableFont;
            TMP_FontAsset bodyFont = OverlayTheme.ReadableFont;

            _root = new GameObject("LocalCoopChoiceOverlay");
            RectTransform parent = _root.AddComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();
            Canvas canvas = _root.AddComponent(Il2CppType.Of<Canvas>()).Cast<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            CanvasScaler scaler = _root.AddComponent(Il2CppType.Of<CanvasScaler>()).Cast<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            _root.AddComponent(Il2CppType.Of<GraphicRaycaster>());
            UnityEngine.Object.DontDestroyOnLoad(_root);

            _backdropImage = Box("Backdrop", parent, Vector2.zero, Vector2.one, OverlayTheme.ColBackdrop);
            _backdropImage.raycastTarget = true;

            // Single-object procedural starfield image (replaces 95 GameObjects).
            _cosmicStarImage = Box("CosmicStarfield", parent, Vector2.zero, Vector2.one, Color.white);
            if (OverlayTheme.StarfieldSprite != null)
            {
                _cosmicStarImage.sprite = OverlayTheme.StarfieldSprite;
            }
            _cosmicStarImage.gameObject.SetActive(false);

            // Top ornate bronze-framed header bar (non-overlapping Title 0.09..0.452 and action3 0.460..0.598!).
            CreateFramedBox("TopBanner", parent, new Vector2(0.035f, 0.875f), new Vector2(0.965f, 0.972f),
                OverlayTheme.ColBronzeOuter, OverlayTheme.ColBronzeDark, OverlayTheme.ColPanelFill, borderNorm: 0.0028f, withCornerStuds: true);

            _title = Text("Heading", parent, new Vector2(0.090f, 0.880f), new Vector2(0.452f, 0.968f), 34, OverlayTheme.ColTextParchment, headerFont);
            _title.alignment = TextAlignmentOptions.MidlineLeft;
            _title.fontStyle = FontStyles.Bold;
            _title.enableAutoSizing = true;
            _title.fontSizeMin = 20f;
            _title.fontSizeMax = 34f;
            _title.overflowMode = TextOverflowModes.Ellipsis;

            CreateActionButton("close", parent, new Vector2(0.042f, 0.889f), new Vector2(0.080f, 0.958f), "X", 24, OverlayTheme.ColBronzeBright, OverlayTheme.ColPanelWell);
            CreateActionButton("action3", parent, new Vector2(0.460f, 0.889f), new Vector2(0.598f, 0.958f), "", 19, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill);
            CreateActionButton("action1", parent, new Vector2(0.606f, 0.889f), new Vector2(0.742f, 0.958f), "", 20, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill);
            CreateActionButton("action2", parent, new Vector2(0.750f, 0.889f), new Vector2(0.957f, 0.958f), "", 19, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill);

            // Bottom ornate footer bar
            CreateFramedBox("Footer", parent, new Vector2(0.035f, 0.020f), new Vector2(0.965f, 0.098f),
                OverlayTheme.ColBronzeOuter, OverlayTheme.ColBronzeDark, OverlayTheme.ColPanelFill, borderNorm: 0.0028f, withCornerStuds: false);
            _footer = Text("FooterText", parent, new Vector2(0.05f, 0.025f), new Vector2(0.95f, 0.093f), 22, OverlayTheme.ColTextParchment, bodyFont);
            _footer.enableAutoSizing = true;
            _footer.fontSizeMin = 16f;
            _footer.fontSizeMax = 24f;

            Sprite frameSprite = OverlayTheme.OrnateFrameSprite ?? OverlayTheme.NativeSlicedFrame;

            for (int i = 0; i < 2; i++)
            {
                float left = i == 0 ? 0.035f : 0.510f;
                RectTransform card = Rect("Player" + (i + 1), parent, new Vector2(left, 0.114f), new Vector2(left + 0.455f, 0.860f));
                Cards[i] = card;

                Image rim = card.gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                rim.color = OverlayTheme.ColBronzeOuter;
                rim.raycastTarget = false;
                CardRims[i] = rim;

                Box("CardDarkInner", card, new Vector2(0.004f, 0.005f), new Vector2(0.996f, 0.995f), OverlayTheme.ColBronzeDark);
                CardFills[i] = Box("CardFill", card, new Vector2(0.008f, 0.010f), new Vector2(0.992f, 0.990f), OverlayTheme.ColPanelFill);
                AddCornerStuds(card, OverlayTheme.ColBronzeBright, 0.012f, 0.010f);

                if (frameSprite != null)
                {
                    Image nativeFrame = Box("NativeFiligree", card, Vector2.zero, Vector2.one,
                        OverlayTheme.OrnateFrameSprite != null ? new Color(1f, 0.88f, 0.72f, 0.88f) : new Color(0.92f, 0.78f, 0.62f, 0.40f));
                    nativeFrame.sprite = frameSprite;
                    nativeFrame.type = Image.Type.Sliced;
                    nativeFrame.fillCenter = false;
                    CardNativeFrames[i] = nativeFrame;
                }

                Color accent = OverlayTheme.PlayerAccent(i);
                HeaderBanners[i] = Box("HeaderBanner", card, new Vector2(0.008f, 0.900f), new Vector2(0.992f, 0.990f), OverlayTheme.PlayerBannerTint(i));
                HeaderStripes[i] = Box("HeaderStripe", card, new Vector2(0.008f, 0.894f), new Vector2(0.992f, 0.900f), OverlayTheme.ColBronzeOuter);
                HeaderPlayerAccents[i] = Box("HeaderPlayerAccent", card, new Vector2(0.008f, 0.900f), new Vector2(0.018f, 0.990f), accent);

                // Non-overlapping PlayerLabels (0.032..0.555) and Status (0.565..0.970)!
                PlayerLabels[i] = Text("Player", card, new Vector2(0.032f, 0.904f), new Vector2(0.555f, 0.986f), 25, accent, headerFont);
                PlayerLabels[i].alignment = TextAlignmentOptions.MidlineLeft;
                PlayerLabels[i].fontStyle = FontStyles.Bold;
                PlayerLabels[i].enableWordWrapping = false;
                PlayerLabels[i].overflowMode = TextOverflowModes.Ellipsis;
                PlayerLabels[i].enableAutoSizing = true;
                PlayerLabels[i].fontSizeMin = 16f;
                PlayerLabels[i].fontSizeMax = 26f;
                PlayerLabels[i].text = "PLAYER " + (i + 1);

                Status[i] = Text("Status", card, new Vector2(0.565f, 0.904f), new Vector2(0.970f, 0.986f), 20, OverlayTheme.ColGoldHighlight, bodyFont);
                Status[i].alignment = TextAlignmentOptions.MidlineRight;
                Status[i].fontStyle = FontStyles.Bold;
                Status[i].enableWordWrapping = false;
                Status[i].overflowMode = TextOverflowModes.Ellipsis;
                Status[i].enableAutoSizing = true;
                Status[i].fontSizeMin = 13f;
                Status[i].fontSizeMax = 21f;

                // Portrait box
                PortraitFrames[i] = Box("PortraitFrame", card, new Vector2(0.04f, 0.715f), new Vector2(0.24f, 0.880f), OverlayTheme.ColBronzeOuter);
                Box("PortraitInnerRim", PortraitFrames[i].transform, new Vector2(0.03f, 0.03f), new Vector2(0.97f, 0.97f), OverlayTheme.ColBronzeDark);
                Box("PortraitInnerWell", PortraitFrames[i].transform, new Vector2(0.06f, 0.06f), new Vector2(0.94f, 0.94f), OverlayTheme.ColPanelWell);

                Image icon = Rect("Icon", card, new Vector2(0.048f, 0.723f), new Vector2(0.232f, 0.872f)).gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                Icons[i] = icon;

                Names[i] = Text("Name", card, new Vector2(0.26f, 0.715f), new Vector2(0.96f, 0.880f), 29, OverlayTheme.ColTextParchment, headerFont);
                Names[i].fontStyle = FontStyles.Bold;
                Names[i].alignment = TextAlignmentOptions.MidlineLeft;
                Names[i].enableAutoSizing = true;
                Names[i].fontSizeMin = 18f;
                Names[i].fontSizeMax = 30f;

                // Equipment / Summary box
                EquipmentBoxes[i] = Box("EquipmentBox", card, new Vector2(0.255f, 0.745f), new Vector2(0.96f, 0.880f), OverlayTheme.ColBronzeOuter);
                Box("EquipmentWell", EquipmentBoxes[i].transform, new Vector2(0.008f, 0.035f), new Vector2(0.992f, 0.965f), OverlayTheme.ColPanelWell);
                EquipmentLabels[i] = Text("EquipmentSummary", EquipmentBoxes[i].transform, new Vector2(0.025f, 0.06f), new Vector2(0.975f, 0.94f), 19, OverlayTheme.ColTextParchment, bodyFont);
                EquipmentLabels[i].alignment = TextAlignmentOptions.Center;
                EquipmentLabels[i].enableAutoSizing = true;
                EquipmentLabels[i].fontSizeMin = 12f;
                EquipmentLabels[i].fontSizeMax = 19f;
                EquipmentLabels[i].lineSpacing = 2f;
                EquipmentLabels[i].overflowMode = TextOverflowModes.Ellipsis;
                EquipmentBoxes[i].gameObject.SetActive(false);

                // Description / Cosmic Plaque box
                DescriptionBoxes[i] = Box("DescriptionBox", card, new Vector2(0.04f, 0.175f), new Vector2(0.96f, 0.360f), OverlayTheme.ColBronzeOuter);
                Box("DescWell", DescriptionBoxes[i].transform, new Vector2(0.006f, 0.020f), new Vector2(0.994f, 0.980f), OverlayTheme.ColPanelWell);
                if (OverlayTheme.OrnateFrameSprite != null)
                {
                    Image descFiligree = Box("DescFiligree", DescriptionBoxes[i].transform, Vector2.zero, Vector2.one, new Color(1f, 0.86f, 0.68f, 0.75f));
                    descFiligree.sprite = OverlayTheme.OrnateFrameSprite;
                    descFiligree.type = Image.Type.Sliced;
                    descFiligree.fillCenter = false;
                }
                DescriptionBoxes[i].gameObject.SetActive(false);

                Descriptions[i] = Text("Description", card, new Vector2(0.055f, 0.183f), new Vector2(0.945f, 0.352f), 22, OverlayTheme.ColTextParchment, bodyFont);
                Descriptions[i].enableAutoSizing = true;
                Descriptions[i].fontSizeMin = 13f;
                Descriptions[i].fontSizeMax = 23f;
                Descriptions[i].lineSpacing = 3f;
                Descriptions[i].overflowMode = TextOverflowModes.Ellipsis;
                Descriptions[i].gameObject.SetActive(false);

                // Scrollable options viewport + scrollbar
                RectTransform vp = Rect("OptionsViewport", card, new Vector2(0.04f, 0.175f), new Vector2(0.935f, 0.730f));
                vp.gameObject.AddComponent(Il2CppType.Of<RectMask2D>());
                OptionViewports[i] = vp;

                RectTransform content = Rect("OptionsContent", vp, new Vector2(0f, 1f), new Vector2(1f, 1f));
                content.pivot = new Vector2(0.5f, 1f);
                content.offsetMin = content.offsetMax = Vector2.zero;
                OptionContents[i] = content;

                Image sbTrack = Box("OptionsScrollTrack", card, new Vector2(0.944f, 0.175f), new Vector2(0.962f, 0.730f), OverlayTheme.ColPanelWell);
                Box("OptionsScrollTrackRim", sbTrack.transform, new Vector2(0f, 0f), new Vector2(0.18f, 1f), OverlayTheme.ColBronzeDark);
                Image sbThumb = Box("OptionsScrollThumb", sbTrack.transform, new Vector2(0.14f, 0.70f), new Vector2(0.86f, 1f), OverlayTheme.ColBronzeBright);
                OptionScrollTracks[i] = sbTrack;
                OptionScrollThumbs[i] = sbThumb;
                sbTrack.gameObject.SetActive(false);
                vp.gameObject.SetActive(false);

                // Card action buttons
                CreateActionButton("reroll_" + i, card, new Vector2(0.04f, 0.092f), new Vector2(0.46f, 0.158f), "Re-roll", 21, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill);
                CreateActionButton("banish_" + i, card, new Vector2(0.475f, 0.092f), new Vector2(0.715f, 0.158f), "Banish", 20, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill);
                CreateActionButton("confirm_" + i, card, new Vector2(0.73f, 0.092f), new Vector2(0.96f, 0.158f), "Ready", 21, OverlayTheme.ColBronzeBright, new Color(0.20f, 0.13f, 0.17f, 0.98f));

                if (i == 0)
                {
                    CreateActionButton("ng_prev", card, new Vector2(0.04f, 0.092f), new Vector2(0.24f, 0.158f), "< NG+ >", 20, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill);
                    CreateActionButton("speed_toggle", card, new Vector2(0.255f, 0.092f), new Vector2(0.62f, 0.158f), "Speed Tier: Normal", 20, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill);
                }

                FooterDividers[i] = Box("FooterDivider", card, new Vector2(0.03f, 0.076f), new Vector2(0.97f, 0.080f), OverlayTheme.ColBronzeDark);
                ControlHints[i] = Text("Controls", card, new Vector2(0.035f, 0.012f), new Vector2(0.965f, 0.072f), 19, OverlayTheme.ColTextMuted, bodyFont);
                ControlHints[i].enableAutoSizing = true;
                ControlHints[i].fontSizeMin = 15f;
                ControlHints[i].fontSizeMax = 21f;
                ControlHints[i].text = OverlayTheme.Sanitize("Stick / D-Pad / Mouse: Browse  -  [A]/Click: Confirm  -  [B]: Back");
            }

            // Dedicated horizontal Pause/Result action bar
            for (int p = 0; p < 5; p++)
            {
                float pMin = 0.035f + p * 0.188f;
                float pMax = pMin + 0.178f;
                CreateActionButton("pause_" + p, parent, new Vector2(pMin, 0.114f), new Vector2(pMax, 0.192f), "", 21, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill);
            }

            HideAllActionButtons();
            _root.SetActive(false);
        }

        public static void Shutdown()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
            }
            _backdropImage = null;
            _cosmicStarImage = null;
            _title = null;
            _footer = null;
            _currentTitle = "";

            Array.Clear(Cards, 0, Cards.Length);
            Array.Clear(CardRims, 0, CardRims.Length);
            Array.Clear(CardFills, 0, CardFills.Length);
            Array.Clear(CardNativeFrames, 0, CardNativeFrames.Length);
            Array.Clear(HeaderBanners, 0, HeaderBanners.Length);
            Array.Clear(HeaderStripes, 0, HeaderStripes.Length);
            Array.Clear(HeaderPlayerAccents, 0, HeaderPlayerAccents.Length);
            Array.Clear(PlayerLabels, 0, PlayerLabels.Length);
            Array.Clear(Status, 0, Status.Length);
            Array.Clear(PortraitFrames, 0, PortraitFrames.Length);
            Array.Clear(Icons, 0, Icons.Length);
            Array.Clear(Names, 0, Names.Length);
            Array.Clear(DescriptionBoxes, 0, DescriptionBoxes.Length);
            Array.Clear(Descriptions, 0, Descriptions.Length);
            Array.Clear(EquipmentBoxes, 0, EquipmentBoxes.Length);
            Array.Clear(EquipmentLabels, 0, EquipmentLabels.Length);
            Array.Clear(FooterDividers, 0, FooterDividers.Length);
            Array.Clear(ControlHints, 0, ControlHints.Length);
            Array.Clear(OptionViewports, 0, OptionViewports.Length);
            Array.Clear(OptionContents, 0, OptionContents.Length);
            Array.Clear(OptionScrollTracks, 0, OptionScrollTracks.Length);
            Array.Clear(OptionScrollThumbs, 0, OptionScrollThumbs.Length);

            for (int i = 0; i < 2; i++)
            {
                OptionRows[i].Clear();
                GridBacks[i].Clear();
                GridFills[i].Clear();
                GridIcons[i].Clear();
                GridBadges[i].Clear();
                OptionScrollY[i] = 0f;
                OptionViewportHeight[i] = 0f;
                OptionContentHeight[i] = 0f;
                OptionLastSelected[i] = -1;
                OptionLastCount[i] = -1;
            }

            ActionButtons.Clear();
            OverlayInput.ResetFrameState();
            OverlayTheme.Shutdown();
        }

        private static void ApplySyncedFontsToExistingControls()
        {
            if (_root == null) return;
            TMP_FontAsset headerFont = OverlayTheme.DisplayFont ?? OverlayTheme.ReadableFont;
            TMP_FontAsset bodyFont = OverlayTheme.ReadableFont;
            if (headerFont == null || bodyFont == null) return;

            if (_title != null) _title.font = headerFont;
            if (_footer != null) _footer.font = bodyFont;
            for (int i = 0; i < 2; i++)
            {
                if (PlayerLabels[i] != null) PlayerLabels[i].font = headerFont;
                if (Status[i] != null) Status[i].font = bodyFont;
                if (Names[i] != null) Names[i].font = headerFont;
                if (EquipmentLabels[i] != null) EquipmentLabels[i].font = bodyFont;
                if (Descriptions[i] != null) Descriptions[i].font = bodyFont;
                if (ControlHints[i] != null) ControlHints[i].font = bodyFont;
                for (int r = 0; r < OptionRows[i].Count; r++)
                {
                    if (OptionRows[i][r]?.Name != null) OptionRows[i][r].Name.font = bodyFont;
                }
            }
            foreach (var kv in ActionButtons)
            {
                if (kv.Value?.Label != null) kv.Value.Label.font = bodyFont;
            }
        }

        public static void SetCosmicMode(bool enabled)
        {
            if (_backdropImage != null)
            {
                _backdropImage.color = enabled ? OverlayTheme.ColCosmicVoid : OverlayTheme.ColBackdrop;
            }
            if (_cosmicStarImage != null)
            {
                _cosmicStarImage.gameObject.SetActive(enabled);
            }
            for (int i = 0; i < 2; i++)
            {
                if (CardFills[i] != null)
                {
                    CardFills[i].color = enabled ? OverlayTheme.ColCosmicPanelFill : OverlayTheme.ColPanelFill;
                }
            }
        }

        public static void Show(string title, string footer)
        {
            if (!EnsureCreated()) return;
            if (OverlayTheme.RefreshFontsFromLevelUpUI())
            {
                ApplySyncedFontsToExistingControls();
            }
            if (_currentTitle != title)
            {
                ResetLayout();
                _currentTitle = title;
            }
            _root.SetActive(true);
            _title.text = OverlayTheme.Sanitize(title);
            _footer.text = OverlayTheme.Sanitize(footer);
        }

        public static void Hide()
        {
            SetCosmicMode(false);
            if (_root != null) _root.SetActive(false);
            _currentTitle = "";
            ResetScrollState();
            OverlayInput.ResetFrameState();
            HideAllActionButtons();
        }

        private static void ResetScrollState()
        {
            for (int i = 0; i < 2; i++)
            {
                OptionScrollY[i] = 0f;
                OptionLastSelected[i] = -1;
                OptionLastCount[i] = -1;
            }
        }

        private static void HideAllActionButtons()
        {
            foreach (var kv in ActionButtons)
            {
                if (kv.Value?.Rect != null) kv.Value.Rect.gameObject.SetActive(false);
            }
        }

        public static void ClearChoices()
        {
            if (_root == null) return;
            SetCosmicMode(false);
            HideAllActionButtons();
            ResetScrollState();
            for (int i = 0; i < 2; i++)
            {
                if (Icons[i] != null) Icons[i].enabled = false;
                if (PortraitFrames[i] != null) PortraitFrames[i].gameObject.SetActive(false);
                if (Names[i] != null) Names[i].text = "";
                if (Status[i] != null) Status[i].text = "";
                if (DescriptionBoxes[i] != null) DescriptionBoxes[i].gameObject.SetActive(false);
                if (Descriptions[i] != null) Descriptions[i].gameObject.SetActive(false);
                if (EquipmentBoxes[i] != null) EquipmentBoxes[i].gameObject.SetActive(false);
                if (OptionViewports[i] != null) OptionViewports[i].gameObject.SetActive(false);
                if (OptionScrollTracks[i] != null) OptionScrollTracks[i].gameObject.SetActive(false);
                for (int r = 0; r < OptionRows[i].Count; r++)
                {
                    if (OptionRows[i][r]?.Back != null) OptionRows[i][r].Back.gameObject.SetActive(false);
                }
                for (int g = 0; g < GridBacks[i].Count; g++)
                {
                    GridBacks[i][g].gameObject.SetActive(false);
                }
            }
        }

        public static void SetHeaderButtons(string action1Text = null, string action2Text = null, bool showClose = true, string action3Text = null)
        {
            SetActionButton("close", showClose, "X", OverlayTheme.ColBronzeBright, OverlayTheme.ColPanelWell, false);
            SetActionButton("action3", !string.IsNullOrEmpty(action3Text), action3Text, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill, false);
            SetActionButton("action1", !string.IsNullOrEmpty(action1Text), action1Text, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill, false);
            SetActionButton("action2", !string.IsNullOrEmpty(action2Text), action2Text, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill, false);
        }

        public static void SetCardConfirmButton(int slot, string text, bool active = false, bool enabled = true)
        {
            if (slot < 0 || slot >= 2) return;
            bool stageMode = slot == 0
                && ((ActionButtons.TryGetValue("ng_prev", out var ngBtn) && ngBtn?.Rect != null && ngBtn.Rect.gameObject.activeSelf)
                    || (ActionButtons.TryGetValue("speed_toggle", out var spdBtn) && spdBtn?.Rect != null && spdBtn.Rect.gameObject.activeSelf));
            if (ActionButtons.TryGetValue("confirm_" + slot, out var btn) && btn?.Rect != null)
            {
                bool hasLeftBtns = (ActionButtons.TryGetValue("reroll_" + slot, out var rr) && rr?.Rect != null && rr.Rect.gameObject.activeSelf)
                    || (ActionButtons.TryGetValue("banish_" + slot, out var bn) && bn?.Rect != null && bn.Rect.gameObject.activeSelf);
                if (stageMode)
                {
                    btn.Rect.anchorMin = new Vector2(0.740f, 0.034f);
                    btn.Rect.anchorMax = new Vector2(0.955f, 0.098f);
                }
                else
                {
                    btn.Rect.anchorMin = new Vector2(hasLeftBtns ? 0.73f : 0.56f, 0.092f);
                    btn.Rect.anchorMax = new Vector2(0.96f, 0.158f);
                }
                btn.Rect.offsetMin = btn.Rect.offsetMax = Vector2.zero;
            }
            SetActionButton("confirm_" + slot, !string.IsNullOrEmpty(text), text,
                active ? OverlayTheme.ColReadyGreen : OverlayTheme.ColBronzeBright, OverlayTheme.ColCellFill, active, enabled);
        }

        public static void SetCardRerollButton(int slot, string text, bool enabled = true)
        {
            if (slot < 0 || slot >= 2) return;
            SetActionButton("reroll_" + slot, !string.IsNullOrEmpty(text), text, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill, false, enabled);
        }

        public static void SetCardBanishButton(int slot, string text, bool active = false, bool enabled = true)
        {
            if (slot < 0 || slot >= 2) return;
            SetActionButton("banish_" + slot, !string.IsNullOrEmpty(text), text,
                active ? new Color(0.95f, 0.35f, 0.35f, 1f) : OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill, active, enabled);
        }

        public static void SetStageExtraButtons(string ngText, string speedText)
        {
            bool hasNg = !string.IsNullOrEmpty(ngText);
            if (ActionButtons.TryGetValue("ng_prev", out var ngBtn) && ngBtn?.Rect != null)
            {
                ngBtn.Rect.anchorMin = new Vector2(0.360f, 0.034f);
                ngBtn.Rect.anchorMax = new Vector2(0.500f, 0.098f);
                ngBtn.Rect.offsetMin = ngBtn.Rect.offsetMax = Vector2.zero;
            }
            if (ActionButtons.TryGetValue("speed_toggle", out var spdBtn) && spdBtn?.Rect != null)
            {
                spdBtn.Rect.anchorMin = new Vector2(hasNg ? 0.515f : 0.480f, 0.034f);
                spdBtn.Rect.anchorMax = new Vector2(0.725f, 0.098f);
                spdBtn.Rect.offsetMin = spdBtn.Rect.offsetMax = Vector2.zero;
            }
            SetActionButton("ng_prev", hasNg, ngText, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill, false, true);
            SetActionButton("speed_toggle", !string.IsNullOrEmpty(speedText), speedText, OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill, false, true);
        }

        public static void SetPauseActionBar(string[] labels, int selectedIdx)
        {
            if (_root == null) return;
            for (int i = 0; i < 2; i++)
            {
                if (Cards[i] != null)
                {
                    float left = i == 0 ? 0.035f : 0.510f;
                    Cards[i].gameObject.SetActive(true);
                    Cards[i].anchorMin = new Vector2(left, 0.206f);
                    Cards[i].anchorMax = new Vector2(left + 0.455f, 0.860f);
                    Cards[i].offsetMin = Cards[i].offsetMax = Vector2.zero;
                }
                if (FooterDividers[i] != null) FooterDividers[i].gameObject.SetActive(false);
                if (ControlHints[i] != null) ControlHints[i].gameObject.SetActive(false);
                if (OptionViewports[i] != null)
                {
                    OptionViewports[i].anchorMin = new Vector2(0.04f, 0.045f);
                    OptionViewports[i].anchorMax = new Vector2(0.935f, 0.730f);
                    OptionViewports[i].offsetMin = OptionViewports[i].offsetMax = Vector2.zero;
                }
                if (OptionScrollTracks[i] != null)
                {
                    OptionScrollTracks[i].rectTransform.anchorMin = new Vector2(0.944f, 0.045f);
                    OptionScrollTracks[i].rectTransform.anchorMax = new Vector2(0.962f, 0.730f);
                    OptionScrollTracks[i].rectTransform.offsetMin = OptionScrollTracks[i].rectTransform.offsetMax = Vector2.zero;
                }
            }

            int count = labels != null ? Math.Min(5, labels.Length) : 0;
            for (int p = 0; p < 5; p++)
            {
                bool visible = labels != null && p < labels.Length && !string.IsNullOrEmpty(labels[p]);
                bool sel = p == selectedIdx;
                if (visible && ActionButtons.TryGetValue("pause_" + p, out var ab) && ab?.Rect != null)
                {
                    if (count > 0 && count < 5)
                    {
                        float btnW = count == 2 ? 0.24f : (count == 3 ? 0.22f : 0.19f);
                        float gap = 0.022f;
                        float totalW = count * btnW + (count - 1) * gap;
                        float xStart = 0.5f - totalW * 0.5f;
                        float pMin = xStart + p * (btnW + gap);
                        ab.Rect.anchorMin = new Vector2(pMin, 0.114f);
                        ab.Rect.anchorMax = new Vector2(pMin + btnW, 0.192f);
                    }
                    else
                    {
                        float pMin = 0.035f + p * 0.188f;
                        ab.Rect.anchorMin = new Vector2(pMin, 0.114f);
                        ab.Rect.anchorMax = new Vector2(pMin + 0.178f, 0.192f);
                    }
                    ab.Rect.offsetMin = ab.Rect.offsetMax = Vector2.zero;
                }
                SetActionButton("pause_" + p, visible, visible ? labels[p] : "",
                    sel ? OverlayTheme.ColGoldHighlight : OverlayTheme.ColBronzeOuter, OverlayTheme.ColCellFill, sel, enabled: true);
            }
        }

        public static void SetPlayerHeader(int slot, string text)
        {
            if (_root == null || slot < 0 || slot >= 2 || PlayerLabels[slot] == null) return;
            Color accent = OverlayTheme.PlayerAccent(slot);
            PlayerLabels[slot].text = OverlayTheme.Sanitize(text);
            PlayerLabels[slot].color = accent;
            if (HeaderBanners[slot] != null) HeaderBanners[slot].color = OverlayTheme.PlayerBannerTint(slot);
            if (HeaderPlayerAccents[slot] != null) HeaderPlayerAccents[slot].color = accent;
        }

        public static void SetStatusBadge(int slot, string text, Color color)
        {
            if (_root == null || slot < 0 || slot >= 2) return;
            Status[slot].gameObject.SetActive(true);
            Status[slot].text = OverlayTheme.Sanitize(text);
            Status[slot].color = color;
        }

        public static void SetEquipmentSummary(int slot, string summary, Sprite portrait = null)
        {
            if (_root == null || slot < 0 || slot >= 2 || EquipmentBoxes[slot] == null) return;
            bool has = !string.IsNullOrEmpty(summary);
            EquipmentBoxes[slot].gameObject.SetActive(has);
            EquipmentLabels[slot].text = OverlayTheme.Sanitize(summary);

            if (portrait != null)
            {
                Icons[slot].sprite = portrait;
                Icons[slot].color = Color.white;
                Icons[slot].enabled = true;
                if (PortraitFrames[slot] != null)
                {
                    PortraitFrames[slot].gameObject.SetActive(true);
                    PortraitFrames[slot].rectTransform.anchorMin = new Vector2(0.04f, 0.745f);
                    PortraitFrames[slot].rectTransform.anchorMax = new Vector2(0.23f, 0.880f);
                    Icons[slot].rectTransform.anchorMin = new Vector2(0.048f, 0.752f);
                    Icons[slot].rectTransform.anchorMax = new Vector2(0.222f, 0.873f);
                }
                EquipmentBoxes[slot].rectTransform.anchorMin = new Vector2(0.245f, 0.745f);
                EquipmentBoxes[slot].rectTransform.anchorMax = new Vector2(0.96f, 0.880f);
            }
            else
            {
                // Always hide portrait & frame when no portrait is requested (fixes stale icon bleeding behind EquipmentBox).
                Icons[slot].enabled = false;
                if (PortraitFrames[slot] != null) PortraitFrames[slot].gameObject.SetActive(false);
                EquipmentBoxes[slot].rectTransform.anchorMin = new Vector2(0.04f, 0.745f);
                EquipmentBoxes[slot].rectTransform.anchorMax = new Vector2(0.96f, 0.880f);
            }
        }

        public static void Card(int slot, string name, bool ready, Sprite icon)
        {
            if (_root == null || slot < 0 || slot >= 2) return;
            Names[slot].enabled = !string.IsNullOrEmpty(name);
            Names[slot].text = OverlayTheme.Sanitize(name);
            Status[slot].text = OverlayTheme.Sanitize(ready ? CoopLocalization.Text("* READY", "* PRÊT") : CoopLocalization.Text("SELECTING", "SÉLECTION"));
            Status[slot].color = ready ? OverlayTheme.ColReadyGreen : OverlayTheme.ColGoldHighlight;
            CardRims[slot].color = ready ? OverlayTheme.ColReadyGreen : OverlayTheme.ColBronzeOuter;
            Icons[slot].sprite = icon;
            Icons[slot].color = Color.white;
            Icons[slot].enabled = icon != null;
            if (PortraitFrames[slot] != null) PortraitFrames[slot].gameObject.SetActive(icon != null);
        }

        public static void Details(int slot, string text, bool ready)
        {
            Card(slot, text, ready, null);
            if (_root == null || slot < 0 || slot >= 2) return;
            Names[slot].font = OverlayTheme.ReadableFont;
            Names[slot].fontSizeMin = 19f;
            Names[slot].fontSizeMax = 27f;
            Names[slot].alignment = TextAlignmentOptions.Center;
            Names[slot].rectTransform.anchorMin = new Vector2(0.06f, 0.18f);
            Names[slot].rectTransform.anchorMax = new Vector2(0.94f, 0.86f);
        }

        public static void SinglePrompt(
            string heading,
            string statusText,
            Color statusColor,
            string summaryText,
            string[] options,
            int selected,
            string confirmText,
            Sprite icon = null,
            string cardHeading = null,
            bool[] enabledFlags = null)
        {
            if (_root == null) return;

            if (Cards[1] != null) Cards[1].gameObject.SetActive(false);
            if (Cards[0] != null)
            {
                Cards[0].gameObject.SetActive(true);
                Cards[0].anchorMin = new Vector2(0.24f, 0.14f);
                Cards[0].anchorMax = new Vector2(0.76f, 0.86f);
                Cards[0].offsetMin = Cards[0].offsetMax = Vector2.zero;

                string headerText = !string.IsNullOrEmpty(cardHeading)
                    ? cardHeading
                    : (!string.IsNullOrEmpty(heading) ? heading : CoopLocalization.Text("RESURRECTION", "RÉSURRECTION"));
                SetPlayerHeader(0, headerText);
                if (ControlHints[0] != null)
                {
                    ControlHints[0].gameObject.SetActive(true);
                    ControlHints[0].text = OverlayTheme.Sanitize(CoopLocalization.Text(
                        "Stick / Arrows / Mouse: Select   -   [A] / Enter / Click: Confirm",
                        "Stick / Flèches / Souris : Choisir   -   [A] / Entrée / Clic : Confirmer"));
                }
            }

            Card(0, "", ready: false, icon: null);
            SetStatusBadge(0, statusText, statusColor);
            CardRims[0].color = statusColor;
            string combinedSummary = !string.IsNullOrEmpty(heading) && !string.IsNullOrEmpty(cardHeading)
                ? $"<b>{heading}</b>\n{summaryText}"
                : summaryText;
            SetEquipmentSummary(0, combinedSummary, icon);
            SetCardConfirmButton(0, confirmText, active: true, enabled: true);
            if (options != null && options.Length > 0)
            {
                Options(0, options, new Sprite[options.Length], selected, showPortraitFromSummary: icon != null, enabledFlags: enabledFlags);
            }
        }

        public static void SettingsColorDropdown(
            int targetPlayerSlot,
            Sprite[] swatchSprites,
            string[] colorNames,
            int selectedIdx,
            string heading,
            string selectedColorLabel,
            string cancelLabel)
        {
            if (_root == null || swatchSprites == null || swatchSprites.Length == 0 || Cards[0] == null || Cards[1] == null) return;

            Cards[0].anchorMin = new Vector2(0.055f, 0.14f);
            Cards[0].anchorMax = new Vector2(0.525f, 0.86f);
            Cards[0].offsetMin = Cards[0].offsetMax = Vector2.zero;

            Cards[1].gameObject.SetActive(true);
            Cards[1].anchorMin = new Vector2(0.545f, 0.14f);
            Cards[1].anchorMax = new Vector2(0.945f, 0.86f);
            Cards[1].offsetMin = Cards[1].offsetMax = Vector2.zero;

            int safeIdx = Mathf.Clamp(selectedIdx, 0, swatchSprites.Length - 1);
            Color targetAccent = PlayerPalette.ColorAt(safeIdx);
            CardRims[1].color = targetAccent;
            SetPlayerHeader(1, heading);
            if (PlayerLabels[1] != null) PlayerLabels[1].color = targetAccent;
            if (HeaderPlayerAccents[1] != null) HeaderPlayerAccents[1].color = targetAccent;
            if (HeaderBanners[1] != null)
            {
                HeaderBanners[1].color = new Color(0.055f + targetAccent.r * 0.17f, 0.045f + targetAccent.g * 0.14f, 0.065f + targetAccent.b * 0.18f, 0.98f);
            }

            Card(1, selectedColorLabel, ready: false, icon: swatchSprites[safeIdx]);
            SetStatusBadge(1, CoopLocalization.Text("PALETTE", "COULEURS"), targetAccent);
            if (ControlHints[1] != null)
            {
                ControlHints[1].gameObject.SetActive(true);
                ControlHints[1].text = OverlayTheme.Sanitize(CoopLocalization.Text(
                    "Click / [A]: Apply   -   [B] / Esc: Close",
                    "Clic / [A] : Appliquer   -   [B] / Echap : Fermer"));
            }

            bool[] allUnlocked = new bool[swatchSprites.Length];
            for (int i = 0; i < allUnlocked.Length; i++) allUnlocked[i] = true;
            CharacterGrid(1, swatchSprites, allUnlocked, safeIdx, level: false, profile: null, tileBadges: colorNames, customCols: swatchSprites.Length > 9 ? 5 : 3);
            SetCardConfirmButton(1, cancelLabel, active: false, enabled: true);
        }

        public static void SettingsListDropdown(
            string heading,
            string subtitle,
            string badgeText,
            string[] optionLabels,
            int selectedIdx,
            string detailText,
            string cancelLabel)
        {
            if (_root == null || optionLabels == null || optionLabels.Length == 0 || Cards[0] == null || Cards[1] == null) return;

            Cards[0].anchorMin = new Vector2(0.055f, 0.14f);
            Cards[0].anchorMax = new Vector2(0.525f, 0.86f);
            Cards[0].offsetMin = Cards[0].offsetMax = Vector2.zero;

            Cards[1].gameObject.SetActive(true);
            Cards[1].anchorMin = new Vector2(0.545f, 0.14f);
            Cards[1].anchorMax = new Vector2(0.945f, 0.86f);
            Cards[1].offsetMin = Cards[1].offsetMax = Vector2.zero;

            Color accent = new Color(0.36f, 0.84f, 1f, 1f);
            CardRims[1].color = accent;
            SetPlayerHeader(1, heading);
            if (PlayerLabels[1] != null) PlayerLabels[1].color = accent;
            if (HeaderPlayerAccents[1] != null) HeaderPlayerAccents[1].color = accent;
            if (HeaderBanners[1] != null)
            {
                HeaderBanners[1].color = new Color(0.055f + accent.r * 0.16f, 0.045f + accent.g * 0.14f, 0.065f + accent.b * 0.18f, 0.98f);
            }

            int safeIdx = Mathf.Clamp(selectedIdx, 0, optionLabels.Length - 1);
            Card(1, subtitle ?? optionLabels[safeIdx], ready: false, icon: null);
            SetStatusBadge(1, badgeText, accent);
            SetEquipmentSummary(1, detailText ?? string.Empty);
            if (ControlHints[1] != null)
            {
                ControlHints[1].gameObject.SetActive(true);
                ControlHints[1].text = OverlayTheme.Sanitize(CoopLocalization.Text(
                    "Click / [A]: Select   -   [B] / Esc: Close",
                    "Clic / [A] : Choisir   -   [B] / Echap : Fermer"));
            }

            SetCardConfirmButton(1, cancelLabel, active: false, enabled: true);
            Options(1, optionLabels, null, safeIdx);
        }

        public static void SingleStage(string stageName, bool unlocked, bool cleared, bool isDlc, Sprite icon, Sprite[] sprites, bool[] unlockedArr, int selected, string description, string[] tileBadges = null)
        {
            if (_root == null) return;

            if (Cards[1] != null) Cards[1].gameObject.SetActive(false);
            if (Cards[0] != null)
            {
                Cards[0].gameObject.SetActive(true);
                Cards[0].anchorMin = new Vector2(0.06f, 0.105f);
                Cards[0].anchorMax = new Vector2(0.94f, 0.875f);
                Cards[0].offsetMin = Cards[0].offsetMax = Vector2.zero;

                SetPlayerHeader(0, CoopLocalization.Text(
                    $"STAGE SELECT  |  PLAYER 1 CONTROL  |  STAGE {selected + 1}/{Math.Max(1, sprites != null ? sprites.Length : 1)}",
                    $"NIVEAU  |  CONTRÔLE JOUEUR 1  |  NIVEAU {selected + 1}/{Math.Max(1, sprites != null ? sprites.Length : 1)}"));
                if (FooterDividers[0] != null) FooterDividers[0].gameObject.SetActive(false);
                if (ControlHints[0] != null) ControlHints[0].gameObject.SetActive(false);
            }

            Card(0, stageName, ready: false, icon);
            Status[0].gameObject.SetActive(true);
            if (!unlocked)
            {
                Status[0].text = OverlayTheme.Sanitize(CoopLocalization.Text("LOCKED", "VERROUILLÉ"));
                Status[0].color = new Color(0.96f, 0.42f, 0.36f);
                CardRims[0].color = new Color(0.65f, 0.25f, 0.22f, 0.95f);
            }
            else if (isDlc)
            {
                Status[0].text = OverlayTheme.Sanitize(cleared
                    ? CoopLocalization.Text("DLC | CLEARED *", "DLC | TERMINÉ *")
                    : "DLC | BALLBYLON RISE");
                Status[0].color = new Color(0.45f, 0.88f, 1f);
                CardRims[0].color = OverlayTheme.ColBronzeBright;
            }
            else if (cleared)
            {
                Status[0].text = OverlayTheme.Sanitize(CoopLocalization.Text("CLEARED *", "TERMINÉ *"));
                Status[0].color = OverlayTheme.ColGoldHighlight;
                CardRims[0].color = OverlayTheme.ColBronzeBright;
            }
            else
            {
                Status[0].text = OverlayTheme.Sanitize(CoopLocalization.Text("UNLOCKED", "DÉBLOQUÉ"));
                Status[0].color = OverlayTheme.ColReadyGreen;
                CardRims[0].color = OverlayTheme.ColBronzeOuter;
            }

            CharacterGrid(0, sprites, unlockedArr, selected, level: true, profile: description, tileBadges: tileBadges);
        }

        public static void LoadingBanner(string title, string message)
        {
            if (!EnsureCreated()) return;
            Show(title, message);
            ClearChoices();
            if (Cards[1] != null) Cards[1].gameObject.SetActive(false);
            if (Cards[0] != null)
            {
                Cards[0].gameObject.SetActive(true);
                Cards[0].anchorMin = new Vector2(0.18f, 0.24f);
                Cards[0].anchorMax = new Vector2(0.82f, 0.76f);
                Cards[0].offsetMin = Cards[0].offsetMax = Vector2.zero;
            }
            Information(0, CoopLocalization.Text("PREPARING CO-OP", "PRÉPARATION CO-OP"), message);
        }

        public static void Information(int slot, string heading, string text)
        {
            Details(slot, text, ready: false);
            if (_root == null || slot < 0 || slot >= 2) return;
            SetPlayerHeader(slot, heading);
            Status[slot].gameObject.SetActive(false);
            if (ControlHints[slot] != null) ControlHints[slot].gameObject.SetActive(false);
            Names[slot].rectTransform.anchorMin = new Vector2(0.06f, 0.14f);
            Names[slot].rectTransform.anchorMax = new Vector2(0.94f, 0.84f);
        }

        public static void CharacterGrid(int slot, Sprite[] sprites, bool[] unlocked, int selected, bool level = false, string profile = null, string[] tileBadges = null, int customCols = 0)
        {
            if (_root == null || sprites == null || sprites.Length == 0) return;

            if (OptionViewports[slot] != null) OptionViewports[slot].gameObject.SetActive(false);
            if (OptionScrollTracks[slot] != null) OptionScrollTracks[slot].gameObject.SetActive(false);
            if (EquipmentBoxes[slot] != null) EquipmentBoxes[slot].gameObject.SetActive(false);

            int safeSelected = Mathf.Clamp(selected, 0, sprites.Length - 1);
            Icons[slot].color = unlocked[safeSelected] ? Color.white : new Color(0.32f, 0.28f, 0.34f, 0.9f);
            ConfigureGridCardHeader(slot, level, profile);

            int cols = customCols > 0
                ? customCols
                : (level ? (sprites.Length <= 5 ? sprites.Length : Math.Min(6, (sprites.Length + 1) / 2)) : 8);
            int rows = Math.Max(1, (sprites.Length + cols - 1) / cols);
            float refHeight = 1080f;
            float refWidth = Screen.height > 0 ? refHeight * Screen.width / Screen.height : 1920f;
            float cardWidthFrac = Cards[slot] != null ? Mathf.Max(0.25f, Cards[slot].anchorMax.x - Cards[slot].anchorMin.x) : (level ? 0.88f : 0.455f);
            float gridWidth = refWidth * cardWidthFrac * 0.92f;
            float gridHeight = refHeight * (level ? 0.77f * 0.345f : 0.746f * 0.315f);
            float aspect = customCols > 0 ? 1.35f : (level ? 1.62f : 1f);
            float cellHeight = Mathf.Min(gridWidth / (cols * aspect), gridHeight / rows);
            if (level || customCols > 0) cellHeight = Mathf.Min(cellHeight, customCols > 0 ? 112f : 148f);
            float cellWidth = cellHeight * aspect;
            float centerY = level ? 0.315f : 0.325f;

            Color accent = level ? OverlayTheme.ColGoldHighlight : OverlayTheme.PlayerAccent(slot);
            Color selectedRim = Color.Lerp(OverlayTheme.ColGoldHighlight, accent, 0.35f);
            int hoveredCell = OverlayInput.GetLastHoveredGrid(slot);

            for (int j = 0; j < sprites.Length; j++)
            {
                if (GridBacks[slot].Count <= j)
                {
                    RectTransform cellRect = Rect("Cell" + j, Cards[slot], new Vector2(0.5f, centerY), new Vector2(0.5f, centerY));
                    Image backImg = cellRect.gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                    backImg.raycastTarget = false;
                    GridBacks[slot].Add(backImg);

                    Image innerWell = Box("CellInner", cellRect, new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f), OverlayTheme.ColCellFill);
                    GridFills[slot].Add(innerWell);

                    Image portraitImg = Rect("Portrait", cellRect, new Vector2(0.08f, 0.08f), new Vector2(0.92f, 0.92f)).gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                    portraitImg.preserveAspect = true;
                    portraitImg.raycastTarget = false;
                    GridIcons[slot].Add(portraitImg);

                    TextMeshProUGUI badgeTxt = Text("CellBadge", cellRect, new Vector2(0.05f, 0.03f), new Vector2(0.95f, 0.28f), 12, OverlayTheme.ColGoldHighlight, OverlayTheme.ReadableFont);
                    badgeTxt.enableWordWrapping = false;
                    badgeTxt.enableAutoSizing = true;
                    badgeTxt.fontSizeMin = 9f;
                    badgeTxt.fontSizeMax = 13f;
                    badgeTxt.alignment = TextAlignmentOptions.Bottom;
                    badgeTxt.fontStyle = FontStyles.Bold;
                    GridBadges[slot].Add(badgeTxt);
                }

                RectTransform rt = GridBacks[slot][j].rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, centerY);
                rt.sizeDelta = new Vector2(cellWidth - 7f, cellHeight - 6f);
                rt.anchoredPosition = new Vector2(((j % cols) - (cols - 1) / 2f) * cellWidth, ((rows - 1) / 2f - (j / cols)) * cellHeight);

                bool isSelected = j == safeSelected;
                bool isHovered = j == hoveredCell;
                GridBacks[slot][j].gameObject.SetActive(true);
                GridBacks[slot][j].color = isSelected
                    ? selectedRim
                    : (isHovered ? OverlayTheme.ColBronzeBright : (unlocked[j] ? OverlayTheme.ColBronzeDark : new Color(0.20f, 0.14f, 0.17f, 0.85f)));
                GridFills[slot][j].color = isSelected
                    ? new Color(0.24f, 0.15f, 0.19f, 0.98f)
                    : (isHovered ? OverlayTheme.ColHoverGlow : (unlocked[j] ? OverlayTheme.ColCellFill : OverlayTheme.ColPanelWell));
                GridIcons[slot][j].sprite = sprites[j];
                GridIcons[slot][j].color = unlocked[j] ? Color.white : new Color(0.32f, 0.29f, 0.34f, 0.85f);

                string badgeStr = tileBadges != null && j < tileBadges.Length ? tileBadges[j] : null;
                bool hasBadge = !string.IsNullOrEmpty(badgeStr);
                GridBadges[slot][j].gameObject.SetActive(hasBadge);
                if (hasBadge) GridBadges[slot][j].text = OverlayTheme.Sanitize(badgeStr);
            }

            for (int k = sprites.Length; k < GridBacks[slot].Count; k++)
            {
                GridBacks[slot][k].gameObject.SetActive(false);
            }
        }

        private static void ConfigureGridCardHeader(int slot, bool level, string profile)
        {
            TMP_FontAsset headerFont = OverlayTheme.DisplayFont ?? OverlayTheme.ReadableFont;
            if (!level)
            {
                if (PortraitFrames[slot] != null)
                {
                    PortraitFrames[slot].gameObject.SetActive(true);
                    PortraitFrames[slot].rectTransform.anchorMin = new Vector2(0.04f, 0.700f);
                    PortraitFrames[slot].rectTransform.anchorMax = new Vector2(0.245f, 0.882f);
                }
                Icons[slot].rectTransform.anchorMin = new Vector2(0.05f, 0.710f);
                Icons[slot].rectTransform.anchorMax = new Vector2(0.235f, 0.872f);
                Names[slot].font = headerFont;
                Names[slot].alignment = TextAlignmentOptions.MidlineLeft;
                Names[slot].rectTransform.anchorMin = new Vector2(0.268f, 0.700f);
                Names[slot].rectTransform.anchorMax = new Vector2(0.96f, 0.882f);
                Names[slot].enableAutoSizing = true;
                Names[slot].fontSizeMin = 20f;
                Names[slot].fontSizeMax = 30f;

                bool hasProfile = !string.IsNullOrEmpty(profile);
                if (DescriptionBoxes[slot] != null)
                {
                    DescriptionBoxes[slot].rectTransform.anchorMin = new Vector2(0.04f, 0.505f);
                    DescriptionBoxes[slot].rectTransform.anchorMax = new Vector2(0.96f, 0.688f);
                    DescriptionBoxes[slot].gameObject.SetActive(hasProfile);
                }
                Descriptions[slot].rectTransform.anchorMin = new Vector2(0.055f, 0.513f);
                Descriptions[slot].rectTransform.anchorMax = new Vector2(0.945f, 0.680f);
                Descriptions[slot].alignment = TextAlignmentOptions.Center;
                Descriptions[slot].fontSizeMin = 17f;
                Descriptions[slot].fontSizeMax = 24f;
                Descriptions[slot].text = OverlayTheme.Sanitize(profile);
                Descriptions[slot].gameObject.SetActive(hasProfile);
                return;
            }

            if (PortraitFrames[slot] != null)
            {
                PortraitFrames[slot].gameObject.SetActive(Icons[slot].sprite != null);
                PortraitFrames[slot].rectTransform.anchorMin = new Vector2(0.035f, 0.485f);
                PortraitFrames[slot].rectTransform.anchorMax = new Vector2(0.425f, 0.870f);
            }
            Icons[slot].rectTransform.anchorMin = new Vector2(0.044f, 0.495f);
            Icons[slot].rectTransform.anchorMax = new Vector2(0.416f, 0.860f);
            Names[slot].font = headerFont;
            Names[slot].alignment = TextAlignmentOptions.MidlineLeft;
            Names[slot].rectTransform.anchorMin = new Vector2(0.448f, 0.755f);
            Names[slot].rectTransform.anchorMax = new Vector2(0.962f, 0.870f);
            Names[slot].enableAutoSizing = true;
            Names[slot].fontSizeMin = 24f;
            Names[slot].fontSizeMax = 36f;

            bool hasDesc = !string.IsNullOrEmpty(profile);
            if (DescriptionBoxes[slot] != null)
            {
                DescriptionBoxes[slot].rectTransform.anchorMin = new Vector2(0.448f, 0.485f);
                DescriptionBoxes[slot].rectTransform.anchorMax = new Vector2(0.962f, 0.742f);
                DescriptionBoxes[slot].gameObject.SetActive(hasDesc);
            }
            Descriptions[slot].rectTransform.anchorMin = new Vector2(0.464f, 0.498f);
            Descriptions[slot].rectTransform.anchorMax = new Vector2(0.946f, 0.729f);
            Descriptions[slot].alignment = TextAlignmentOptions.MidlineLeft;
            Descriptions[slot].fontSizeMin = 18f;
            Descriptions[slot].fontSizeMax = 26f;
            Descriptions[slot].text = OverlayTheme.Sanitize(profile);
            Descriptions[slot].gameObject.SetActive(hasDesc);
        }

        private static bool HasVisibleCardActionButtons(int slot)
        {
            return (ActionButtons.TryGetValue("reroll_" + slot, out var r) && r?.Rect != null && r.Rect.gameObject.activeSelf)
                || (ActionButtons.TryGetValue("banish_" + slot, out var b) && b?.Rect != null && b.Rect.gameObject.activeSelf)
                || (ActionButtons.TryGetValue("confirm_" + slot, out var c) && c?.Rect != null && c.Rect.gameObject.activeSelf);
        }

        public static void Options(
            int slot,
            string[] names,
            Sprite[] icons,
            int selected,
            Sprite[] fusionIcons = null,
            string description = null,
            bool showPortraitFromSummary = true,
            Sprite[] ing1Icons = null,
            Sprite[] ing2Icons = null,
            bool[] enabledFlags = null,
            bool cosmicMode = false,
            string plaqueText = null,
            string controlHintOverride = null)
        {
            if (_root == null || names == null || names.Length == 0) return;

            if (cosmicMode) SetCosmicMode(true);

            for (int g = 0; g < GridBacks[slot].Count; g++)
            {
                GridBacks[slot][g].gameObject.SetActive(false);
            }

            if (!showPortraitFromSummary && EquipmentBoxes[slot] != null)
            {
                EquipmentBoxes[slot].gameObject.SetActive(false);
            }

            bool hasEquip = EquipmentBoxes[slot] != null && EquipmentBoxes[slot].gameObject.activeSelf;
            if (!showPortraitFromSummary || !hasEquip)
            {
                Icons[slot].enabled = false;
                if (PortraitFrames[slot] != null) PortraitFrames[slot].gameObject.SetActive(false);
            }
            Names[slot].enabled = false;

            int safeSelected = Mathf.Clamp(selected, 0, names.Length - 1);

            bool hasPlaque = !string.IsNullOrEmpty(plaqueText);
            bool hasDesc = hasPlaque || !string.IsNullOrEmpty(description);
            string activeDesc = hasPlaque ? plaqueText : description;
            if (DescriptionBoxes[slot] != null)
            {
                DescriptionBoxes[slot].rectTransform.anchorMin = hasPlaque ? new Vector2(0.04f, 0.595f) : new Vector2(0.04f, 0.175f);
                DescriptionBoxes[slot].rectTransform.anchorMax = hasPlaque ? new Vector2(0.96f, 0.878f) : new Vector2(0.96f, 0.355f);
                DescriptionBoxes[slot].color = hasPlaque ? OverlayTheme.ColBronzeBright : OverlayTheme.ColBronzeOuter;
                DescriptionBoxes[slot].gameObject.SetActive(hasDesc);
            }
            if (Descriptions[slot] != null)
            {
                Descriptions[slot].rectTransform.anchorMin = hasPlaque ? new Vector2(0.058f, 0.606f) : new Vector2(0.055f, 0.183f);
                Descriptions[slot].rectTransform.anchorMax = hasPlaque ? new Vector2(0.942f, 0.866f) : new Vector2(0.945f, 0.347f);
                Descriptions[slot].alignment = TextAlignmentOptions.Center;
                Descriptions[slot].fontSizeMin = hasPlaque ? 17f : 13f;
                Descriptions[slot].fontSizeMax = hasPlaque ? 26f : 23f;
                Descriptions[slot].overflowMode = TextOverflowModes.Ellipsis;
                Descriptions[slot].text = OverlayTheme.Sanitize(activeDesc);
                Descriptions[slot].gameObject.SetActive(hasDesc);
            }

            if (controlHintOverride != null && ControlHints[slot] != null)
            {
                bool showHint = controlHintOverride.Length > 0;
                ControlHints[slot].gameObject.SetActive(showHint);
                ControlHints[slot].text = showHint ? OverlayTheme.Sanitize(controlHintOverride) : string.Empty;
            }

            bool hintVisible = ControlHints[slot] != null && ControlHints[slot].gameObject.activeSelf;
            bool bottomReserved = hintVisible || HasVisibleCardActionButtons(slot);
            float vpTop = hasPlaque ? 0.578f : (hasEquip ? 0.728f : 0.868f);
            float vpBot = hasPlaque
                ? (bottomReserved ? 0.175f : 0.055f)
                : (hasDesc ? 0.372f : (bottomReserved ? 0.175f : 0.055f));

            float cardFracH = Cards[slot] != null ? Mathf.Max(0.35f, Cards[slot].anchorMax.y - Cards[slot].anchorMin.y) : 0.74f;
            float vpHeight = Mathf.Max(120f, (vpTop - vpBot) * cardFracH * 1080f);
            float contentHeight = names.Length * OptionRowStep - OptionRowSpacing;
            float maxScroll = Math.Max(0f, contentHeight - vpHeight);
            bool needsScrollbar = maxScroll > 2f;

            OptionViewportHeight[slot] = vpHeight;
            OptionContentHeight[slot] = contentHeight;

            if (safeSelected != OptionLastSelected[slot] || names.Length != OptionLastCount[slot])
            {
                float rowTop = safeSelected * OptionRowStep;
                float rowBot = rowTop + OptionRowHeight;
                if (rowTop < OptionScrollY[slot]) OptionScrollY[slot] = rowTop;
                else if (rowBot > OptionScrollY[slot] + vpHeight) OptionScrollY[slot] = rowBot - vpHeight;
                OptionLastSelected[slot] = safeSelected;
                OptionLastCount[slot] = names.Length;
            }
            OptionScrollY[slot] = Mathf.Clamp(OptionScrollY[slot], 0f, maxScroll);

            if (OptionViewports[slot] != null)
            {
                OptionViewports[slot].gameObject.SetActive(true);
                OptionViewports[slot].anchorMin = new Vector2(0.04f, vpBot);
                OptionViewports[slot].anchorMax = new Vector2(needsScrollbar ? 0.935f : 0.96f, vpTop);
                OptionViewports[slot].offsetMin = OptionViewports[slot].offsetMax = Vector2.zero;
            }
            if (OptionContents[slot] != null)
            {
                OptionContents[slot].sizeDelta = new Vector2(0f, contentHeight);
                OptionContents[slot].anchoredPosition = new Vector2(0f, OptionScrollY[slot]);
            }
            if (OptionScrollTracks[slot] != null)
            {
                OptionScrollTracks[slot].gameObject.SetActive(needsScrollbar);
                if (needsScrollbar)
                {
                    OptionScrollTracks[slot].rectTransform.anchorMin = new Vector2(0.944f, vpBot);
                    OptionScrollTracks[slot].rectTransform.anchorMax = new Vector2(0.962f, vpTop);
                    OptionScrollTracks[slot].rectTransform.offsetMin = OptionScrollTracks[slot].rectTransform.offsetMax = Vector2.zero;
                    if (OptionScrollThumbs[slot] != null)
                    {
                        float thumbFrac = Mathf.Clamp(vpHeight / Math.Max(1f, contentHeight), 0.14f, 0.85f);
                        float norm = maxScroll > 0.01f ? Mathf.Clamp01(OptionScrollY[slot] / maxScroll) : 0f;
                        float thumbMax = 1f - norm * (1f - thumbFrac);
                        float thumbMin = thumbMax - thumbFrac;
                        OptionScrollThumbs[slot].rectTransform.anchorMin = new Vector2(0.14f, thumbMin);
                        OptionScrollThumbs[slot].rectTransform.anchorMax = new Vector2(0.86f, thumbMax);
                        OptionScrollThumbs[slot].rectTransform.offsetMin = OptionScrollThumbs[slot].rectTransform.offsetMax = Vector2.zero;
                        OptionScrollThumbs[slot].color = OverlayInput.DraggingScrollSlot == slot ? OverlayTheme.ColGoldHighlight : OverlayTheme.ColBronzeBright;
                    }
                }
            }

            Color selFill = slot == 0 ? new Color(0.28f, 0.13f, 0.20f, 0.98f) : new Color(0.11f, 0.23f, 0.29f, 0.98f);
            int hoveredRow = OverlayInput.GetLastHoveredOption(slot);

            for (int i = 0; i < names.Length; i++)
            {
                ScrollOptionRow row = EnsureScrollRow(slot, i);
                bool rowEnabled = enabledFlags == null || i >= enabledFlags.Length || enabledFlags[i];
                bool isSel = i == safeSelected;
                bool isHovered = i == hoveredRow;

                row.Back.gameObject.SetActive(true);
                row.Back.rectTransform.anchoredPosition = new Vector2(0f, -i * OptionRowStep);
                row.Back.rectTransform.sizeDelta = new Vector2(0f, OptionRowHeight);
                row.Back.color = !rowEnabled
                    ? new Color(0.22f, 0.17f, 0.19f, 0.85f)
                    : (isSel ? OverlayTheme.ColGoldHighlight : (isHovered ? OverlayTheme.ColBronzeBright : OverlayTheme.ColBronzeOuter));
                row.Fill.color = !rowEnabled
                    ? OverlayTheme.ColPanelWell
                    : (isSel ? selFill : (isHovered ? OverlayTheme.ColHoverGlow : OverlayTheme.ColCellFill));

                Sprite sp = icons != null && i < icons.Length ? icons[i] : null;
                bool hasIcon = sp != null;
                row.IconRim.gameObject.SetActive(hasIcon);
                row.Icon.sprite = sp;
                row.Icon.enabled = hasIcon;
                row.Icon.color = rowEnabled ? Color.white : new Color(0.45f, 0.42f, 0.44f, 0.72f);

                Sprite fsp = fusionIcons != null && i < fusionIcons.Length ? fusionIcons[i] : null;
                row.FusionIcon.sprite = fsp;
                row.FusionIcon.gameObject.SetActive(fsp != null);

                Sprite ing1 = ing1Icons != null && i < ing1Icons.Length ? ing1Icons[i] : null;
                Sprite ing2 = ing2Icons != null && i < ing2Icons.Length ? ing2Icons[i] : null;
                bool hasRecipe = ing1 != null && ing2 != null;
                if (row.Ing1Rim != null)
                {
                    row.Ing1Rim.gameObject.SetActive(hasRecipe);
                    row.Ing1.sprite = ing1;
                    row.Ing1.enabled = hasRecipe;
                    row.IngCross.gameObject.SetActive(hasRecipe);
                    row.Ing2Rim.gameObject.SetActive(hasRecipe);
                    row.Ing2.sprite = ing2;
                    row.Ing2.enabled = hasRecipe;
                }

                row.Name.rectTransform.anchorMin = new Vector2(hasIcon ? 0.135f : 0.03f, 0.08f);
                row.Name.rectTransform.anchorMax = new Vector2(hasRecipe ? 0.715f : 0.975f, 0.92f);
                row.Name.rectTransform.offsetMin = row.Name.rectTransform.offsetMax = Vector2.zero;
                row.Name.text = (isSel ? "> " : "") + OverlayTheme.Sanitize(names[i]);
                row.Name.color = !rowEnabled
                    ? new Color(0.55f, 0.48f, 0.50f, 0.88f)
                    : (isSel ? OverlayTheme.ColTextParchment : new Color(0.86f, 0.82f, 0.80f));
            }

            for (int i = names.Length; i < OptionRows[slot].Count; i++)
            {
                if (OptionRows[slot][i]?.Back != null) OptionRows[slot][i].Back.gameObject.SetActive(false);
            }
        }

        public static void CenterSingleCard(float left = 0.14f, float right = 0.86f)
        {
            if (Cards[1] != null) Cards[1].gameObject.SetActive(false);
            if (Cards[0] != null)
            {
                Cards[0].gameObject.SetActive(true);
                Cards[0].anchorMin = new Vector2(left, 0.114f);
                Cards[0].anchorMax = new Vector2(right, 0.860f);
                Cards[0].offsetMin = Cards[0].offsetMax = Vector2.zero;
            }
        }

        private static ScrollOptionRow EnsureScrollRow(int slot, int idx)
        {
            TMP_FontAsset bodyFont = OverlayTheme.ReadableFont;
            while (OptionRows[slot].Count <= idx)
            {
                int rIdx = OptionRows[slot].Count;
                RectTransform rt = Rect("ScrollOpt" + rIdx, OptionContents[slot], new Vector2(0f, 1f), new Vector2(1f, 1f));
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -rIdx * OptionRowStep);
                rt.sizeDelta = new Vector2(0f, OptionRowHeight);

                Image back = rt.gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                back.color = OverlayTheme.ColBronzeOuter;
                back.raycastTarget = false;

                Image fill = Box("OptFill", rt, new Vector2(0.006f, 0.03f), new Vector2(0.994f, 0.97f), OverlayTheme.ColCellFill);
                if (OverlayTheme.OrnateFrameSprite != null)
                {
                    Image optFiligree = Box("OptFiligree", rt, Vector2.zero, Vector2.one, new Color(1f, 0.86f, 0.68f, 0.42f));
                    optFiligree.sprite = OverlayTheme.OrnateFrameSprite;
                    optFiligree.type = Image.Type.Sliced;
                    optFiligree.fillCenter = false;
                }

                Image optIconRim = Box("OptIconRim", rt, new Vector2(0.015f, 0.11f), new Vector2(0.118f, 0.89f), OverlayTheme.ColBronzeDark);
                Box("OptIconWell", optIconRim.transform, new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f), OverlayTheme.ColPanelWell);
                Image optIcon = Rect("OptIcon", optIconRim.transform, new Vector2(0.08f, 0.08f), new Vector2(0.92f, 0.92f)).gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                optIcon.preserveAspect = true;
                optIcon.raycastTarget = false;

                Image optFusion = Rect("OptFusion", rt, new Vector2(0.085f, 0.08f), new Vector2(0.135f, 0.44f)).gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                optFusion.preserveAspect = true;
                optFusion.raycastTarget = false;
                optFusion.gameObject.SetActive(false);

                TextMeshProUGUI optText = Text("OptText", rt, new Vector2(0.135f, 0.08f), new Vector2(0.98f, 0.92f), 22, OverlayTheme.ColTextParchment, bodyFont);
                optText.alignment = TextAlignmentOptions.MidlineLeft;
                optText.enableAutoSizing = true;
                optText.fontSizeMin = 14f;
                optText.fontSizeMax = 24f;
                optText.lineSpacing = 2f;
                optText.overflowMode = TextOverflowModes.Ellipsis;

                Image ing1Rim = Box("OptIng1Rim", rt, new Vector2(0.730f, 0.14f), new Vector2(0.818f, 0.86f), OverlayTheme.ColBronzeDark);
                Box("OptIng1Well", ing1Rim.transform, new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f), OverlayTheme.ColPanelWell);
                Image ing1 = Rect("OptIng1", ing1Rim.transform, new Vector2(0.08f, 0.08f), new Vector2(0.92f, 0.92f)).gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                ing1.preserveAspect = true;
                ing1.raycastTarget = false;
                ing1Rim.gameObject.SetActive(false);

                TextMeshProUGUI ingCross = Text("OptIngCross", rt, new Vector2(0.822f, 0.14f), new Vector2(0.870f, 0.86f), 22, OverlayTheme.ColGoldHighlight, bodyFont);
                ingCross.fontStyle = FontStyles.Bold;
                ingCross.text = "x";
                ingCross.gameObject.SetActive(false);

                Image ing2Rim = Box("OptIng2Rim", rt, new Vector2(0.874f, 0.14f), new Vector2(0.962f, 0.86f), OverlayTheme.ColBronzeDark);
                Box("OptIng2Well", ing2Rim.transform, new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f), OverlayTheme.ColPanelWell);
                Image ing2 = Rect("OptIng2", ing2Rim.transform, new Vector2(0.08f, 0.08f), new Vector2(0.92f, 0.92f)).gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                ing2.preserveAspect = true;
                ing2.raycastTarget = false;
                ing2Rim.gameObject.SetActive(false);

                OptionRows[slot].Add(new ScrollOptionRow
                {
                    Back = back,
                    Fill = fill,
                    IconRim = optIconRim,
                    Icon = optIcon,
                    FusionIcon = optFusion,
                    Name = optText,
                    Ing1Rim = ing1Rim,
                    Ing1 = ing1,
                    IngCross = ingCross,
                    Ing2Rim = ing2Rim,
                    Ing2 = ing2
                });
            }
            return OptionRows[slot][idx];
        }

        private static void ResetLayout()
        {
            SetCosmicMode(false);
            HideAllActionButtons();
            ResetScrollState();

            TMP_FontAsset headerFont = OverlayTheme.DisplayFont ?? OverlayTheme.ReadableFont;
            for (int i = 0; i < 2; i++)
            {
                if (Cards[i] == null) continue;
                float left = i == 0 ? 0.035f : 0.510f;
                Cards[i].gameObject.SetActive(true);
                Cards[i].anchorMin = new Vector2(left, 0.114f);
                Cards[i].anchorMax = new Vector2(left + 0.455f, 0.860f);
                Cards[i].offsetMin = Cards[i].offsetMax = Vector2.zero;

                SetPlayerHeader(i, CoopLocalization.Text("PLAYER ", "JOUEUR ") + (i + 1));
                Status[i].gameObject.SetActive(true);
                if (FooterDividers[i] != null) FooterDividers[i].gameObject.SetActive(true);
                if (ControlHints[i] != null)
                {
                    ControlHints[i].gameObject.SetActive(true);
                    ControlHints[i].text = OverlayTheme.Sanitize(CoopLocalization.Text(
                        "Stick / D-Pad / Mouse: Browse  |  [A] / Click: Confirm  |  [B]: Back",
                        "Stick / Croix / Souris : Naviguer  |  [A] / Clic : Confirmer  |  [B] : Retour"));
                }
                if (OptionViewports[i] != null)
                {
                    OptionViewports[i].anchorMin = new Vector2(0.04f, 0.175f);
                    OptionViewports[i].anchorMax = new Vector2(0.935f, 0.730f);
                    OptionViewports[i].offsetMin = OptionViewports[i].offsetMax = Vector2.zero;
                }
                if (OptionScrollTracks[i] != null)
                {
                    OptionScrollTracks[i].rectTransform.anchorMin = new Vector2(0.944f, 0.175f);
                    OptionScrollTracks[i].rectTransform.anchorMax = new Vector2(0.962f, 0.730f);
                    OptionScrollTracks[i].rectTransform.offsetMin = OptionScrollTracks[i].rectTransform.offsetMax = Vector2.zero;
                }
                if (PortraitFrames[i] != null)
                {
                    PortraitFrames[i].rectTransform.anchorMin = new Vector2(0.04f, 0.715f);
                    PortraitFrames[i].rectTransform.anchorMax = new Vector2(0.24f, 0.880f);
                }
                Icons[i].rectTransform.anchorMin = new Vector2(0.048f, 0.723f);
                Icons[i].rectTransform.anchorMax = new Vector2(0.232f, 0.872f);
                Names[i].font = headerFont;
                Names[i].rectTransform.anchorMin = new Vector2(0.04f, 0.30f);
                Names[i].rectTransform.anchorMax = new Vector2(0.96f, 0.48f);
                Names[i].alignment = TextAlignmentOptions.Center;
                Names[i].fontSize = 32f;
                if (DescriptionBoxes[i] != null)
                {
                    DescriptionBoxes[i].color = OverlayTheme.ColBronzeOuter;
                    DescriptionBoxes[i].gameObject.SetActive(false);
                }
                if (Descriptions[i] != null)
                {
                    Descriptions[i].fontSizeMin = 18f;
                    Descriptions[i].fontSizeMax = 26f;
                    Descriptions[i].gameObject.SetActive(false);
                }
                if (EquipmentBoxes[i] != null) EquipmentBoxes[i].gameObject.SetActive(false);
            }
        }

        private static void CreateActionButton(string id, Transform parent, Vector2 min, Vector2 max, string initialText, int fontSize, Color rimColor, Color fillColor)
        {
            RectTransform rt = Rect("Btn_" + id, parent, min, max);
            Image rim = rt.gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
            rim.color = rimColor;
            rim.raycastTarget = false;

            Image fill = Box("BtnFill", rt, new Vector2(0.015f, 0.06f), new Vector2(0.985f, 0.94f), fillColor);
            TextMeshProUGUI label = Text("BtnLabel", rt, new Vector2(0.04f, 0.06f), new Vector2(0.96f, 0.94f), fontSize, OverlayTheme.ColTextParchment, OverlayTheme.ReadableFont);
            label.fontStyle = FontStyles.Bold;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.enableAutoSizing = true;
            label.fontSizeMin = 13f;
            label.fontSizeMax = Math.Max(fontSize, 22);
            label.text = OverlayTheme.Sanitize(initialText);

            ActionButtons[id] = new OverlayInput.ActionButton
            {
                Id = id,
                Rect = rt,
                Rim = rim,
                Fill = fill,
                Label = label,
                BaseRim = rimColor,
                BaseFill = fillColor
            };
        }

        private static void SetActionButton(string id, bool visible, string text, Color rimColor, Color fillColor, bool active, bool enabled = true)
        {
            if (!ActionButtons.TryGetValue(id, out var btn) || btn?.Rect == null) return;
            btn.Rect.gameObject.SetActive(visible);
            if (!visible) return;
            btn.Enabled = enabled;
            btn.Active = active;
            btn.BaseRim = rimColor;
            btn.BaseFill = fillColor;
            btn.Label.text = OverlayTheme.Sanitize(text);
            btn.Label.color = enabled ? OverlayTheme.ColTextParchment : OverlayTheme.ColTextMuted;
            OverlayInput.ApplyActionButtonVisual(btn, OverlayInput.ContainsScreenPoint(btn.Rect, UnityEngine.Input.mousePosition) && enabled);
        }

        private static void CreateFramedBox(string name, Transform parent, Vector2 min, Vector2 max, Color rimColor, Color innerRimColor, Color fillColor, float borderNorm, bool withCornerStuds)
        {
            RectTransform rt = Rect(name, parent, min, max);
            Image rim = rt.gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
            rim.color = rimColor;
            rim.raycastTarget = false;
            Box(name + "_InnerRim", rt, new Vector2(borderNorm, borderNorm * 3.5f), new Vector2(1f - borderNorm, 1f - borderNorm * 3.5f), innerRimColor);
            Box(name + "_Fill", rt, new Vector2(borderNorm * 2f, borderNorm * 7f), new Vector2(1f - borderNorm * 2f, 1f - borderNorm * 7f), fillColor);
            if (OverlayTheme.OrnateFrameSprite != null)
            {
                Image filigree = Box(name + "_Filigree", rt, Vector2.zero, Vector2.one, new Color(1f, 0.88f, 0.72f, 0.80f));
                filigree.sprite = OverlayTheme.OrnateFrameSprite;
                filigree.type = Image.Type.Sliced;
                filigree.fillCenter = false;
            }
            if (withCornerStuds) AddCornerStuds(rt, OverlayTheme.ColBronzeBright, 0.009f, 0.08f);
        }

        private static void AddCornerStuds(Transform parent, Color color, float w, float h)
        {
            Box("CornerTL", parent, new Vector2(0f, 1f - h), new Vector2(w, 1f), color);
            Box("CornerTR", parent, new Vector2(1f - w, 1f - h), new Vector2(1f, 1f), color);
            Box("CornerBL", parent, new Vector2(0f, 0f), new Vector2(w, h), color);
            Box("CornerBR", parent, new Vector2(1f - w, 0f), new Vector2(1f, h), color);
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max)
        {
            RectTransform rt = new GameObject(name).AddComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();
            rt.SetParent(parent, worldPositionStays: false);
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        private static Image Box(string name, Transform parent, Vector2 min, Vector2 max, Color color)
        {
            Image img = Rect(name, parent, min, max).gameObject.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static TextMeshProUGUI Text(string name, Transform parent, Vector2 min, Vector2 max, int size, Color color, TMP_FontAsset font)
        {
            TextMeshProUGUI txt = Rect(name, parent, min, max).gameObject.AddComponent(Il2CppType.Of<TextMeshProUGUI>()).Cast<TextMeshProUGUI>();
            txt.font = font ?? OverlayTheme.ReadableFont;
            txt.fontSize = size;
            txt.color = color;
            txt.alignment = TextAlignmentOptions.Center;
            txt.raycastTarget = false;
            txt.enableWordWrapping = true;
            txt.richText = true;
            return txt;
        }
    }
}
