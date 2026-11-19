using System;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Input;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BALLxPITLocalCoop.UI
{
    /// <summary>
    /// Town entry point for Local Co-op: adds an independent CO-OP button beside vanilla Play (Solo),
    /// Main Menu splash badge, clean mouse/keyboard/gamepad navigation, and dynamic hint switching.
    /// </summary>
    public static class MainMenuCoopUI
    {
        private const string CoopButtonName = "CoopTownStandaloneButton";
        private const string LegacyCoopButtonName = "CoopTownSidebarButton";
        private const string SoloBadgeName = "SoloBadgeText";

        private static readonly Vector3[] WorldCornerBuf = new Vector3[4];

        private static bool _installed;
        private static GameObject _coopGo;
        private static RectTransform _coopRt;
        private static Image[] _coopImages;
        private static Sprite _coopIconSprite;
        private static TextMeshProUGUI _coopBadge;
        private static TextMeshProUGUI _soloBadge;
        private static BaseUI _ownerUi;
        private static int _lastTriggerFrame = -1;
        private static bool _coopSelected;
        private static bool _wasPadRightHeld;
        private static bool _wasPadLeftHeld;
        private static bool _preferGamepadHint;
        private static bool _loggedRuntimeDiag;
        private static float _diagAt;
        private static float _townReopenCooldownUntil;
        private static bool _coopBlockedTownInput;
        private static GameObject _splashCanvasGo;
        private static GameObject _splashBannerGo;
        private static RectTransform _splashBannerRt;
        private static TextMeshProUGUI _splashBannerText;

        public static bool HasBaseButton => HarmonyHelper.IsUnityAlive(_coopGo);

        public static void Trace(string msg) => Plugin.Logger?.LogInfo(msg);

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(MainMenuUI), nameof(MainMenuUI.Start)),
                postfix: new HarmonyMethod(typeof(MainMenuCoopUI), nameof(OnMainMenuStart)),
                label: "MainMenuUI.Start");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(BaseUI), nameof(BaseUI.OnPlayClicked)),
                prefix: new HarmonyMethod(typeof(MainMenuCoopUI), nameof(OnBasePlayClicked)),
                label: "BaseUI.OnPlayClicked");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(BaseMgr), nameof(BaseMgr.MyUpdate)),
                prefix: new HarmonyMethod(typeof(MainMenuCoopUI), nameof(OnBaseMgrUpdate)),
                label: "BaseMgr.MyUpdate (MainMenuCoopUI)");
        }

        public static void Shutdown()
        {
            if (HarmonyHelper.IsUnityAlive(_coopGo))
            {
                UnityEngine.Object.Destroy(_coopGo);
            }
            if (HarmonyHelper.IsUnityAlive(_soloBadge))
            {
                UnityEngine.Object.Destroy(_soloBadge.gameObject);
            }
            if (HarmonyHelper.IsUnityAlive(_splashCanvasGo))
            {
                UnityEngine.Object.Destroy(_splashCanvasGo);
            }
            _coopGo = null;
            _coopRt = null;
            _coopImages = null;
            _coopIconSprite = null;
            _coopBadge = null;
            _soloBadge = null;
            _ownerUi = null;
            _splashCanvasGo = null;
            _splashBannerGo = null;
            _splashBannerRt = null;
            _splashBannerText = null;
            _coopSelected = false;
            _coopBlockedTownInput = false;
            _installed = false;
        }

        public static void NotifyCoopClosedCooldown()
        {
            _townReopenCooldownUntil = Time.unscaledTime + 0.35f;
            _coopSelected = false;
            RestoreTownInteraction(BaseUI.I);
        }

        public static void RestoreTownInteraction(BaseUI ui)
        {
            if (ui == null) return;
            try
            {
                _coopBlockedTownInput = false;
                ui.BlockSidebarInput(false);
                if (ui.CvsGrpMain != null)
                {
                    ui.CvsGrpMain.interactable = true;
                    ui.CvsGrpMain.blocksRaycasts = true;
                }
                CoolButtonEventMgr.SetNavLocked(false);
                CoolButtonEventMgr.SetButtonsEnabled(true);
                if (CoolButtonEventMgr.SelectedBtn == null && ui.BtnPlay?.Btn != null && BaseMgr.I != null && BaseMgr.I.CurState == BaseState.kNormal)
                {
                    CoolButtonEventMgr.SetSelectedBtn(ui.BtnPlay.Btn);
                }
            }
            catch { }
        }

        private static void OnMainMenuStart(MainMenuUI __instance)
        {
            try
            {
                CoopMode.DisableForSolo();
                _coopGo = null;
                _coopRt = null;
                _coopImages = null;
                _coopIconSprite = null;
                _coopBadge = null;
                _soloBadge = null;
                _ownerUi = null;
                _coopSelected = false;
                _loggedRuntimeDiag = false;
                _diagAt = Time.unscaledTime + 0.6f;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError("MainMenu start hook error: " + ex);
            }
        }

        private static bool OnBasePlayClicked()
        {
            if (ModalOverlayState.IsTownMenuOpen || CoopMenuUI.Selecting)
            {
                return false;
            }
            if (_coopSelected)
            {
                _coopSelected = false;
                TriggerBaseCoop();
                return false;
            }
            // Only treat mouse hover over CO-OP as a redirect when a mouse click actually occurred (never hijack gamepad/KB Solo clicks!).
            bool mouseClickEvent = UnityEngine.Input.GetMouseButtonUp(0) || UnityEngine.Input.GetMouseButtonDown(0) || UnityEngine.Input.GetMouseButton(0);
            if (mouseClickEvent && HarmonyHelper.IsUnityAlive(_coopGo) && _coopGo.activeInHierarchy
                && TryGetScreenBounds(_coopRt, out float x0, out float y0, out float x1, out float y1))
            {
                Vector3 mp = UnityEngine.Input.mousePosition;
                if (mp.x >= x0 && mp.x <= x1 && mp.y >= y0 && mp.y <= y1)
                {
                    TriggerBaseCoop();
                    return false;
                }
            }
            if (CoopMode.Enabled)
            {
                CoopMode.DisableForSolo();
            }
            return true;
        }

        private static bool OnBaseMgrUpdate()
        {
            if (CoopMenuUI.Selecting && !CoopMode.Enabled)
            {
                CoopMenuUI.Close();
            }
            return !(CoopMode.Enabled && (CoopMenuUI.Selecting || ModalOverlayState.IsTownMenuOpen));
        }

        private static void EnsureCoopButton(BaseUI ui)
        {
            if (ui == null || ui.BtnPlay == null) return;
            if (HarmonyHelper.IsUnityAlive(_coopGo) && HarmonyHelper.SameNative(_ownerUi, ui)) return;

            _coopGo = null;
            _coopRt = null;
            _coopImages = null;
            _coopIconSprite = null;
            _coopBadge = null;
            _soloBadge = null;
            _coopSelected = false;
            _ownerUi = ui;

            Transform playT = ui.BtnPlay.transform;
            Transform parent = playT.parent;
            if (parent == null) return;

            Transform legacy = parent.Find(LegacyCoopButtonName);
            if (legacy != null) UnityEngine.Object.Destroy(legacy.gameObject);
            Transform legacySolo = playT.Find(SoloBadgeName);
            if (legacySolo != null) UnityEngine.Object.Destroy(legacySolo.gameObject);
            Transform existing = parent.Find(CoopButtonName);
            if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);

            // Instantiate while template is temporarily inactive so vanilla CoolButton/BaseSidebarBtn Awake/OnEnable never fire!
            GameObject playGo = ui.BtnPlay.gameObject;
            bool playWasActive = playGo.activeSelf;
            GameObject clone;
            try
            {
                playGo.SetActive(false);
                clone = UnityEngine.Object.Instantiate(playGo, parent);
            }
            finally
            {
                playGo.SetActive(playWasActive);
            }
            clone.name = CoopButtonName;

            // Strip all non-visual/non-layout behaviours cleanly before activating clone.
            Il2CppReferenceArray<Component> comps = clone.GetComponentsInChildren(Il2CppType.Of<Component>(), includeInactive: true);
            if (comps != null)
            {
                foreach (Component comp in comps)
                {
                    if (comp == null) continue;
                    if (comp.TryCast<Transform>() != null) continue;
                    if (comp.TryCast<Graphic>() != null) continue;
                    if (comp.TryCast<CanvasRenderer>() != null) continue;
                    if (comp.TryCast<LayoutElement>() != null) continue;
                    if (comp.TryCast<CanvasGroup>() != null) continue;
                    UnityEngine.Object.Destroy(comp);
                }
            }

            // Prevent parent LayoutGroup (if any) from overriding our side-by-side anchored position.
            LayoutElement le = clone.GetComponent(Il2CppType.Of<LayoutElement>())?.TryCast<LayoutElement>()
                ?? clone.AddComponent(Il2CppType.Of<LayoutElement>()).Cast<LayoutElement>();
            le.ignoreLayout = true;

            clone.transform.SetSiblingIndex(playT.GetSiblingIndex() + 1);
            clone.SetActive(true);

            _coopRt = clone.GetComponent(Il2CppType.Of<RectTransform>())?.TryCast<RectTransform>();
            try { _coopIconSprite = ui.BtnPlay.Icon != null ? ui.BtnPlay.Icon.sprite : null; } catch { _coopIconSprite = null; }

            Il2CppReferenceArray<Component> imgs = clone.GetComponentsInChildren(Il2CppType.Of<Image>(), includeInactive: true);
            _coopImages = new Image[imgs?.Length ?? 0];
            for (int i = 0; i < _coopImages.Length; i++)
            {
                _coopImages[i] = imgs[i]?.TryCast<Image>();
                if (_coopImages[i] != null) _coopImages[i].raycastTarget = true;
            }

            Il2CppReferenceArray<Component> texts = clone.GetComponentsInChildren(Il2CppType.Of<TMP_Text>(), includeInactive: true);
            if (texts != null)
            {
                foreach (Component t in texts)
                {
                    TMP_Text tt = t?.TryCast<TMP_Text>();
                    if (tt != null) tt.text = string.Empty;
                }
            }

            OverlayTheme.EnsureFonts(ui);
            TMP_FontAsset badgeFont = OverlayTheme.DisplayFont ?? OverlayTheme.ReadableFont
                ?? ui.GetComponentInChildren(Il2CppType.Of<TMP_Text>(), includeInactive: true)?.TryCast<TMP_Text>()?.font;

            _coopBadge = CreateBadgeText("CoopBadgeText", clone.transform, new Color(0.96f, 0.84f, 0.44f, 1f), badgeFont);
            _soloBadge = CreateBadgeText(SoloBadgeName, playT, new Color(0.94f, 0.86f, 0.72f, 1f), badgeFont);
            _soloBadge.text = "SOLO";

            // Single authoritative mouse click path via Button.onClick (no duplicate manual MouseButtonUp trigger).
            try
            {
                Button uiBtn = clone.AddComponent(Il2CppType.Of<Button>()).Cast<Button>();
                uiBtn.transition = Selectable.Transition.None;
                uiBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
                {
                    TriggerBaseCoop();
                }));
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("CO-OP Button add warning: " + ex.Message);
            }

            _coopGo = clone;
            UpdateDynamicBadgeText();
            Plugin.Logger?.LogInfo("Town CO-OP button placed side-by-side next to SOLO (Play) button.");
        }

        private static TextMeshProUGUI CreateBadgeText(string name, Transform parent, Color color, TMP_FontAsset font)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            RectTransform rt = obj.AddComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0.44f);
            rt.offsetMin = new Vector2(2f, 2f);
            rt.offsetMax = new Vector2(-2f, 0f);

            TextMeshProUGUI tmp = obj.AddComponent(Il2CppType.Of<TextMeshProUGUI>()).Cast<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.alignment = TextAlignmentOptions.Bottom;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 8f;
            tmp.fontSizeMax = 13f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = color;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static void TriggerBaseCoop()
        {
            if (_lastTriggerFrame == Time.frameCount
                || Time.unscaledTime < _townReopenCooldownUntil
                || CoopMenuUI.Selecting
                || CoopMode.Enabled)
            {
                return;
            }
            _lastTriggerFrame = Time.frameCount;
            _coopSelected = false;

            if (BaseUI.I != null)
            {
                try
                {
                    _coopBlockedTownInput = true;
                    BaseUI.I.ClearSidebarSelection();
                    BaseUI.I.BlockSidebarInput(true);
                    if (BaseUI.I.CvsGrpMain != null)
                    {
                        BaseUI.I.CvsGrpMain.blocksRaycasts = false;
                    }
                }
                catch { }
            }
            CoopMode.Begin(null);
        }

        public static void TickMenuNavigation()
        {
            if (!_loggedRuntimeDiag && MainMenuUI.I != null && _diagAt > 0f && Time.unscaledTime >= _diagAt)
            {
                _loggedRuntimeDiag = true;
                LogMenuAndDlcDiagnostics();
            }

            GameScene curScene = HarmonyHelper.SafeGetCurScene();
            if (MainMenuUI.I != null && curScene == GameScene.kMainMenu)
            {
                try { UpdateMainMenuSplashBanner(); }
                catch (Exception ex) { Plugin.Logger?.LogWarning("MainMenu splash banner warning: " + ex.Message); }
            }
            else if (HarmonyHelper.IsUnityAlive(_splashCanvasGo) && _splashCanvasGo.activeSelf)
            {
                _splashCanvasGo.SetActive(false);
            }

            if (BaseUI.I != null && BaseMgr.I != null && curScene == GameScene.kBase)
            {
                if (!CoopMode.Enabled && !CoopMenuUI.Selecting && !ChoiceOverlay.Visible)
                {
                    GamepadRouter.EnsureTownControllers();
                    if (_coopBlockedTownInput)
                    {
                        RestoreTownInteraction(BaseUI.I);
                    }
                }
                try { TickBaseCoopButton(); }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogWarning("Town CO-OP tick: " + ex.Message);
                    _coopGo = null;
                }
            }
        }

        private static void UpdateMainMenuSplashBanner()
        {
            MainMenuUI menu = MainMenuUI.I;
            bool shouldShow = menu != null
                && menu.WrapperTitle != null
                && menu.WrapperTitle.activeInHierarchy
                && !menu.IsRunningSplashScreen;

            if (!shouldShow)
            {
                if (HarmonyHelper.IsUnityAlive(_splashCanvasGo) && _splashCanvasGo.activeSelf)
                {
                    _splashCanvasGo.SetActive(false);
                }
                return;
            }

            if (!HarmonyHelper.IsUnityAlive(_splashBannerGo))
            {
                TMP_Text templateTxt = menu.GetComponentInChildren(Il2CppType.Of<TMP_Text>(), includeInactive: true)?.TryCast<TMP_Text>();
                TMP_FontAsset font = templateTxt?.font ?? OverlayTheme.DisplayFont ?? OverlayTheme.ReadableFont;
                if (font == null)
                {
                    var fonts = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>());
                    if (fonts != null)
                    {
                        foreach (UnityEngine.Object obj in fonts)
                        {
                            TMP_FontAsset candidate = obj?.TryCast<TMP_FontAsset>();
                            if (candidate != null && candidate.atlasTexture != null)
                            {
                                font = candidate;
                                break;
                            }
                        }
                    }
                }
                if (font == null) return;

                GameObject canvasGo = new GameObject("CoopMainMenuSplashCanvas");
                UnityEngine.Object.DontDestroyOnLoad(canvasGo);
                Canvas canvas = canvasGo.AddComponent(Il2CppType.Of<Canvas>()).Cast<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 100;
                CanvasScaler scaler = canvasGo.AddComponent(Il2CppType.Of<CanvasScaler>()).Cast<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 1f;

                GameObject go = new GameObject("CoopSplashBanner");
                go.transform.SetParent(canvasGo.transform, false);
                RectTransform rt = go.AddComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(316f, 36f);
                rt.localRotation = Quaternion.Euler(0f, 0f, -4f);

                Image outer = go.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                outer.color = new Color(0.95f, 0.74f, 0.22f, 0.92f);
                outer.raycastTarget = false;

                GameObject innerGo = new GameObject("BannerBg");
                innerGo.transform.SetParent(go.transform, false);
                RectTransform irt = innerGo.AddComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();
                irt.anchorMin = Vector2.zero;
                irt.anchorMax = Vector2.one;
                irt.offsetMin = new Vector2(4f, 2f);
                irt.offsetMax = new Vector2(-2f, -2f);
                Image bg = innerGo.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
                bg.color = new Color(0.08f, 0.04f, 0.14f, 0.90f);
                bg.raycastTarget = false;

                GameObject txtGo = new GameObject("Label");
                txtGo.transform.SetParent(go.transform, false);
                RectTransform trt = txtGo.AddComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();
                trt.anchorMin = Vector2.zero;
                trt.anchorMax = Vector2.one;
                trt.offsetMin = new Vector2(10f, 2f);
                trt.offsetMax = new Vector2(-8f, -2f);
                TextMeshProUGUI tmp = txtGo.AddComponent(Il2CppType.Of<TextMeshProUGUI>()).Cast<TextMeshProUGUI>();
                tmp.font = font;
                if (templateTxt != null && templateTxt.fontSharedMaterial != null && templateTxt.font == font)
                {
                    tmp.fontSharedMaterial = templateTxt.fontSharedMaterial;
                }
                tmp.fontSize = 18f;
                tmp.enableAutoSizing = true;
                tmp.fontSizeMin = 12f;
                tmp.fontSizeMax = 20f;
                tmp.fontStyle = FontStyles.Bold;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.enableWordWrapping = false;
                tmp.color = new Color(1f, 0.87f, 0.34f, 1f);
                tmp.raycastTarget = false;

                _splashCanvasGo = canvasGo;
                _splashBannerGo = go;
                _splashBannerRt = rt;
                _splashBannerText = tmp;
            }

            if (HarmonyHelper.IsUnityAlive(_splashCanvasGo) && !_splashCanvasGo.activeSelf)
            {
                _splashCanvasGo.SetActive(true);
            }

            if (HarmonyHelper.IsUnityAlive(_splashBannerRt))
            {
                float scaleTo1080 = 1080f / Mathf.Max(1f, Screen.height);
                float canvasWidth = Screen.width * scaleTo1080;
                Vector2 pos = new Vector2(canvasWidth * 0.5f + 190f, 1080f * 0.72f);

                RectTransform anchorRt = (menu.LogoPit != null ? menu.LogoPit.rectTransform : null)
                    ?? menu.WrapperTitle.GetComponent(Il2CppType.Of<RectTransform>())?.TryCast<RectTransform>();
                if (anchorRt != null && TryGetScreenBounds(anchorRt, out _, out _, out float maxX, out float maxY))
                {
                    float bx = maxX * scaleTo1080 + 18f;
                    float by = maxY * scaleTo1080 + 16f;
                    pos = new Vector2(
                        Mathf.Clamp(bx, 180f, Mathf.Max(220f, canvasWidth - 180f)),
                        Mathf.Clamp(by, 120f, 1020f));
                }

                _splashBannerRt.anchoredPosition = pos;
                float pulse = 1f + 0.035f * Mathf.Sin(Time.unscaledTime * 3.4f);
                _splashBannerRt.localScale = new Vector3(pulse, pulse, 1f);
                if (_splashBannerText != null)
                {
                    _splashBannerText.text = CoopLocalization.Text("NOW WITH LOCAL CO-OP!", "MAINTENANT EN CO-OP LOCAL !");
                }
            }
        }

        private static void UpdateInputDeviceHint(bool anyPadConnected, bool padActivity, bool l3Pressed, bool padAPressed)
        {
            // Switch dynamically based on last active input device (never lock permanently to gamepad when KB+M is used!).
            if (padActivity || l3Pressed || padAPressed)
            {
                _preferGamepadHint = true;
                return;
            }
            if (UnityEngine.Input.anyKeyDown || UnityEngine.Input.GetMouseButtonDown(0))
            {
                _preferGamepadHint = false;
                return;
            }
            if (!anyPadConnected)
            {
                _preferGamepadHint = false;
            }
        }

        private static void UpdateDynamicBadgeText()
        {
            if (!HarmonyHelper.IsUnityAlive(_coopBadge)) return;
            _coopBadge.text = _coopSelected
                ? (_preferGamepadHint ? "CO-OP [A]" : "CO-OP [ENTER]")
                : (_preferGamepadHint ? "CO-OP [LS]" : "CO-OP [TAB]");
        }

        private static bool IsTownMenuIdle(BaseUI ui)
        {
            if (ui == null || BaseMgr.I == null || BaseMgr.I.CurState != BaseState.kNormal) return false;
            if (ui._isUIHidden || ui.IsSidebarBlocked()) return false;
            if (BasePauseUI.I != null && BasePauseUI.I.gameObject.activeInHierarchy) return false;
            if (BaseSettingsUI.I != null && BaseSettingsUI.I.gameObject.activeInHierarchy) return false;
            if (EncyclopediaUI.I != null && EncyclopediaUI.I.gameObject.activeInHierarchy) return false;
            return !ChoiceOverlay.Visible && !CoopMenuUI.Selecting && !CoopMode.Enabled;
        }

        private static void TickBaseCoopButton()
        {
            BaseUI ui = BaseUI.I;
            EnsureCoopButton(ui);
            if (!HarmonyHelper.IsUnityAlive(_coopGo)) return;

            bool playShown = ui.BtnPlay != null && ui.BtnPlay.gameObject.activeInHierarchy;
            bool overlayOpen = CoopMenuUI.Selecting || CoopMode.Enabled || ChoiceOverlay.Visible;
            bool show = playShown && !overlayOpen;
            if (_coopGo.activeSelf != show) _coopGo.SetActive(show);
            if (!show)
            {
                _coopSelected = false;
                return;
            }

            RectTransform playRt = ui.BtnPlay != null ? ui.BtnPlay.transform.TryCast<RectTransform>() : null;
            if (_coopRt != null && playRt != null)
            {
                _coopRt.anchorMin = playRt.anchorMin;
                _coopRt.anchorMax = playRt.anchorMax;
                _coopRt.pivot = playRt.pivot;
                _coopRt.sizeDelta = playRt.sizeDelta;
                _coopRt.anchoredPosition = playRt.anchoredPosition + new Vector2(playRt.rect.width + 6f, 0f);
                float scale = _coopSelected ? 1.07f : 1f;
                _coopRt.localScale = new Vector3(scale, scale, 1f);
            }

            if (HarmonyHelper.IsUnityAlive(_soloBadge)) _soloBadge.text = "SOLO";

            GamepadRouter.PollAnyTownGamepad(
                out bool anyPadConnected,
                out bool l3Pressed,
                out bool padAPressed,
                out bool padBPressed,
                out bool padActivity,
                out float stickX,
                out _);
            UpdateInputDeviceHint(anyPadConnected, padActivity, l3Pressed, padAPressed);

            bool usable = IsTownMenuIdle(ui) && Time.unscaledTime >= _townReopenCooldownUntil;
            bool inside = false;
            if (usable && TryGetScreenBounds(_coopRt, out float minX, out float minY, out float maxX, out float maxY))
            {
                Vector3 mp = UnityEngine.Input.mousePosition;
                inside = mp.x >= minX && mp.x <= maxX && mp.y >= minY && mp.y <= maxY;
            }

            if (usable)
            {
                bool padRight = stickX > 0.55f;
                bool padLeft = stickX < -0.55f;
                bool rightEdge = (padRight && !_wasPadRightHeld) || UnityEngine.Input.GetKeyDown(KeyCode.RightArrow) || UnityEngine.Input.GetKeyDown(KeyCode.D);
                bool leftEdge = (padLeft && !_wasPadLeftHeld) || UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow) || UnityEngine.Input.GetKeyDown(KeyCode.A);
                _wasPadRightHeld = padRight;
                _wasPadLeftHeld = padLeft;

                CoolButton currentSelected = CoolButtonEventMgr.SelectedBtn;

                if (_coopSelected)
                {
                    if (leftEdge || UnityEngine.Input.GetKeyDown(KeyCode.Escape) || padBPressed)
                    {
                        _coopSelected = false;
                        if (ui.BtnPlay?.Btn != null) CoolButtonEventMgr.SetSelectedBtn(ui.BtnPlay.Btn);
                    }
                    else if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.Space) || padAPressed)
                    {
                        TriggerBaseCoop();
                        return;
                    }
                    else if (currentSelected != null)
                    {
                        _coopSelected = false;
                    }
                }
                else if (rightEdge && currentSelected == ui.BtnPlay?.Btn)
                {
                    _coopSelected = true;
                    try { ui.ClearSidebarSelection(); } catch { }
                }

                if (UnityEngine.Input.GetKeyDown(KeyCode.Tab) || l3Pressed)
                {
                    TriggerBaseCoop();
                    return;
                }
            }
            else
            {
                _coopSelected = false;
            }

            UpdateDynamicBadgeText();

            Color accent = PlayerPalette.GetPlayerColor(1);
            float mul = !usable ? 0.55f : (_coopSelected || inside) ? 1.25f : 1f;
            if (_coopImages != null)
            {
                foreach (Image img in _coopImages)
                {
                    if (!HarmonyHelper.IsUnityAlive(img)) continue;
                    bool isIcon = _coopIconSprite != null && img.sprite == _coopIconSprite;
                    Color baseC = isIcon ? accent : (_coopSelected ? new Color(1f, 0.92f, 0.72f, 1f) : Color.white);
                    img.color = new Color(Mathf.Min(1f, baseC.r * mul), Mathf.Min(1f, baseC.g * mul), Mathf.Min(1f, baseC.b * mul), 1f);
                }
            }
        }

        private static bool TryGetScreenBounds(RectTransform rt, out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = minY = maxX = maxY = 0f;
            if (rt == null) return false;
            Canvas canvas = rt.GetComponentInParent(Il2CppType.Of<Canvas>())?.TryCast<Canvas>();
            if (canvas == null) return false;

            Camera cam = null;
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
                if (cam == null) return false;
            }
            rt.GetWorldCorners(WorldCornerBuf);
            minX = float.MaxValue;
            minY = float.MaxValue;
            maxX = float.MinValue;
            maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, WorldCornerBuf[i]);
                if (sp.x < minX) minX = sp.x;
                if (sp.y < minY) minY = sp.y;
                if (sp.x > maxX) maxX = sp.x;
                if (sp.y > maxY) maxY = sp.y;
            }
            return (maxX - minX) >= 16f && (maxY - minY) >= 8f;
        }

        private static void LogMenuAndDlcDiagnostics()
        {
            try
            {
                bool ownsDlc = CoopDlcAndLevels.SafeCanAccessBallbylonDLC();
                int dbChars = InfoDB.I?.Chars?.Length ?? -1;
                int dbLvls = InfoDB.I?.Levels?.Length ?? -1;
                int playable = CoopMenuUI.CountPlayableCharacters();
                Plugin.Logger?.LogInfo(
                    $"MainMenu ready: OwnsDLC={ownsDlc}, InfoDB=(Chars={dbChars}, Playable={playable}, Levels={dbLvls}), " +
                    $"GetPad0={GamepadRouter.GetPad(0) != null}, GetPad1={GamepadRouter.GetPad(1) != null}, Pairing={GamepadRouter.PairingMode}");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("MainMenu status check warning: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Suppresses blocking tutorial popups while a co-op session is active so Player 2 never stalls gameplay.
    /// </summary>
    public static class CoopTutorial
    {
        private static bool _installed;

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null) return;
            _installed = true;

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GameTutMgr), "IsSeen"),
                prefix: new HarmonyMethod(typeof(CoopTutorial), nameof(Seen)),
                label: "GameTutMgr.IsSeen");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GameTutMgr), "Show"),
                prefix: new HarmonyMethod(typeof(CoopTutorial), nameof(Show)),
                label: "GameTutMgr.Show");
        }

        public static void Shutdown()
        {
            _installed = false;
        }

        private static bool Seen(GameTutType __0, ref bool __result)
        {
            if (!CoopMode.Enabled) return true;
            __result = true;
            return false;
        }

        private static bool Show(GameTutType __0) => !CoopMode.Enabled;
    }
}
