using System;
using System.Collections.Generic;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Gameplay;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Shapes;
using UnityEngine;
using UnityEngine.UI;

namespace BALLxPITLocalCoop.PlayerTwo
{
    /// <summary>
    /// Isolates P2's private cloned GameBallPreview, AltPreview (for multi-hero P2), and tinted UI aim cursor.
    /// Fixes v2.2 bugs: virtual MyUpdate double-call, multi-hero AltPreview missing when P1 is single-hero,
    /// nonexistent WrapperPreview/SprPreview reflection, and per-frame Camera.main / GetComponent lookups.
    /// </summary>
    internal static class PreviewIsolation
    {
        private static GameBallPreview _p1GamePreview;
        private static BallPreview _p1BallPreview;
        private static GameBallPreview _p2GamePreview;
        private static BallPreview _p2BallPreview;
        private static GameObject _ownedGamePreviewObj;
        private static GameObject _ownedBallPreviewObj;
        private static readonly List<GameObject> _ownedAuxObjs = new List<GameObject>();
        private static bool _previewCloneAttempted;
        private static bool _secondaryCloneAttempted;
        private static bool _isRefreshing;

        // Cached P2 Aim Cursor
        private const float CursorRadiusScreenFraction = 0.16f;
        private const float CursorMinRadiusPixels = 96f;
        private static GameObject _cursorObj;
        private static RectTransform _cursorRect;
        private static RectTransform _cursorParentRect;
        private static Image _cursorImage;
        private static Canvas _cursorCanvas;
        private static Camera _cachedMainCam;
        private static Color _lastAppliedTint = new Color(-1f, -1f, -1f, -1f);

        public static bool IsRefreshing => _isRefreshing;
        public static bool IsPreviewVisible { get; private set; }
        public static Image CursorImage => _cursorImage;
        public static GameBallPreview PlayerOneGamePreview => _p1GamePreview;
        public static GameBallPreview PlayerTwoGamePreview => _p2GamePreview;
        public static BallPreview PlayerOneBallPreview => _p1BallPreview;
        public static BallPreview PlayerTwoBallPreview => _p2BallPreview;

        public static bool IsPlayerTwoPreview(BallPreview preview)
        {
            if (preview == null) return false;
            return HarmonyHelper.SameNative(preview, _p2GamePreview)
                || HarmonyHelper.SameNative(preview, _p2BallPreview)
                || (_p2GamePreview?.AltPreview != null && HarmonyHelper.SameNative(preview, _p2GamePreview.AltPreview));
        }

        public static void BeginCreation()
        {
            _p1GamePreview = GameBallPreview.I;
            _p1BallPreview = BallPreview.I;
            DestroyOwnedClones();
            _p2GamePreview = null;
            _p2BallPreview = null;
            _previewCloneAttempted = false;
            _secondaryCloneAttempted = false;
            IsPreviewVisible = false;
        }

        public static void CompleteCreation(Player playerTwo)
        {
            CaptureAndDisableClonedSingletons();
            EnsurePrivatePreviewRenderers();
            EnsurePrivateWrapperDefaultPreview();
            EnsureSecondaryPreviewForPlayerTwo();
            RestorePlayerOneSingletons();
        }

        /// <summary>
        /// Called AFTER CoopCharacterRuntime.OnPlayerTwoSpawned swaps P2 to its independent multi-hero character
        /// so AltPreview / AltGravityPreview are cloned even when P1 is a single-hero character.
        /// </summary>
        public static void EnsureSecondaryPreviewForPlayerTwo()
        {
            if (_secondaryCloneAttempted || _p1GamePreview == null || _p2GamePreview == null)
            {
                return;
            }
            if (!PlayerTwoHasMultipleBodies())
            {
                DisableSecondarySlot(_p2GamePreview);
                return;
            }
            _secondaryCloneAttempted = true;

            try
            {
                BallPreview p1Alt = _p1GamePreview.AltPreview;
                BallPreview p2Alt = _p2GamePreview.AltPreview;
                if (p1Alt != null && (p2Alt == null || HarmonyHelper.SameNative(p1Alt, p2Alt)))
                {
                    GameObject clone = UnityEngine.Object.Instantiate(p1Alt.gameObject, _p2GamePreview.transform);
                    clone.name = p1Alt.gameObject.name + "_LocalCoopP2_AltPreview";
                    BallPreview comp = clone.GetComponent<BallPreview>();
                    if (comp == null || HarmonyHelper.SameNative(comp.StartLine, p1Alt.StartLine))
                    {
                        UnityEngine.Object.Destroy(clone);
                    }
                    else
                    {
                        _ownedAuxObjs.Add(clone);
                        _p2GamePreview.AltPreview = comp;
                        DisableBallPreviewRenderer(comp);
                    }
                }

                LineRenderer p1Grav = _p1GamePreview.AltGravityPreview;
                LineRenderer p2Grav = _p2GamePreview.AltGravityPreview;
                if (p1Grav != null && (p2Grav == null || HarmonyHelper.SameNative(p1Grav, p2Grav)))
                {
                    GameObject cloneGrav = UnityEngine.Object.Instantiate(p1Grav.gameObject, _p2GamePreview.transform);
                    cloneGrav.name = p1Grav.gameObject.name + "_LocalCoopP2_AltGravityPreview";
                    LineRenderer lr = cloneGrav.GetComponent<LineRenderer>();
                    if (lr == null || HarmonyHelper.SameNative(lr, p1Grav))
                    {
                        UnityEngine.Object.Destroy(cloneGrav);
                    }
                    else
                    {
                        _ownedAuxObjs.Add(cloneGrav);
                        _p2GamePreview.AltGravityPreview = lr;
                        lr.enabled = false;
                    }
                }
                RestorePlayerOneSingletons();
            }
            catch (Exception ex)
            {
                RestorePlayerOneSingletons();
                Plugin.Logger?.LogWarning($"Could not isolate P2 secondary preview visuals: {ex.Message}");
            }
        }

        public static void CaptureOriginalSingletonsIfNeeded()
        {
            try
            {
                if (_p1GamePreview == null)
                {
                    GameBallPreview cur = GameBallPreview.I;
                    if (cur != null && !HarmonyHelper.SameNative(cur, _p2GamePreview))
                    {
                        _p1GamePreview = cur;
                    }
                }
                if (_p1BallPreview == null)
                {
                    BallPreview cur = BallPreview.I;
                    if (cur != null && !HarmonyHelper.SameNative(cur, _p2BallPreview))
                    {
                        _p1BallPreview = cur;
                    }
                }
            }
            catch { }
        }

        public static void CaptureAndDisableClonedSingletons()
        {
            try
            {
                GameBallPreview curGame = GameBallPreview.I;
                if (_p1GamePreview != null && curGame != null && !HarmonyHelper.SameNative(curGame, _p1GamePreview))
                {
                    _p2GamePreview = curGame;
                    DisableGamePreview(curGame);
                    DisableSecondarySlot(curGame);
                }
                BallPreview curBall = BallPreview.I;
                if (_p1BallPreview != null && curBall != null && !HarmonyHelper.SameNative(curBall, _p1BallPreview))
                {
                    _p2BallPreview = curBall;
                    DisableBallPreview(curBall);
                }
            }
            catch { }
        }

        public static void UsePlayerTwoSingletons()
        {
            try
            {
                if (_p2GamePreview != null && !HarmonyHelper.SameNative(GameBallPreview.I, _p2GamePreview))
                {
                    GameBallPreview.I = _p2GamePreview;
                }
                BallPreview bp = _p2BallPreview ?? _p2GamePreview;
                if (bp != null && !HarmonyHelper.SameNative(BallPreview.I, bp))
                {
                    BallPreview.I = bp;
                }
            }
            catch { }
        }

        public static void RestorePlayerOneSingletons()
        {
            try
            {
                if (_p1GamePreview != null && !HarmonyHelper.SameNative(GameBallPreview.I, _p1GamePreview))
                {
                    GameBallPreview.I = _p1GamePreview;
                }
                if (_p1BallPreview != null && !HarmonyHelper.SameNative(BallPreview.I, _p1BallPreview))
                {
                    BallPreview.I = _p1BallPreview;
                }
            }
            catch { }
        }

        public static void RefreshPreview(BallMgr ballMgr, Player playerTwo)
        {
            IsPreviewVisible = false;
            if (_isRefreshing || ballMgr == null || playerTwo == null || _p1GamePreview == null || _p2GamePreview == null)
            {
                return;
            }

            _isRefreshing = true;
            var scope = CoopContext.EnterPlayerTwo(playerTwo, CoopContext.ScopeKind.Full);
            try
            {
                _p2GamePreview.gameObject.SetActive(true);
                _p2GamePreview.enabled = true;
                _p2GamePreview.SetInputEnabled(true);

                bool hasMulti = PlayerTwoHasMultipleBodies();
                BallPreview alt = hasMulti ? _p2GamePreview.AltPreview : null;
                bool secActive = alt != null && !HarmonyHelper.SameNative(alt, _p1GamePreview.AltPreview);
                if (secActive)
                {
                    alt.gameObject.SetActive(true);
                    alt.enabled = true;
                    alt.SetInputEnabled(true);
                }
                else
                {
                    DisableSecondarySlot(_p2GamePreview);
                }

                ballMgr.RefreshBallPreview();

                // Call virtual MyUpdate() ONCE (fixes v2.2 double-call bug!).
                if (secActive)
                {
                    alt.MyUpdate();
                    alt.SetInputEnabled(false);
                }
                _p2GamePreview.MyUpdate();
                _p2GamePreview.SetInputEnabled(false);

                // Sync P2's own gravity preview visibility based on P2's GravityPreview (NOT P1's!).
                if (secActive && _p2GamePreview.AltGravityPreview != null)
                {
                    bool p2HasGravity = _p2GamePreview.GravityPreview != null
                        && _p2GamePreview.GravityPreview.gameObject.activeSelf
                        && _p2GamePreview.GravityPreview.enabled;
                    _p2GamePreview.AltGravityPreview.gameObject.SetActive(p2HasGravity);
                    _p2GamePreview.AltGravityPreview.enabled = p2HasGravity;
                }

                ApplyTint(_p2GamePreview, secActive);
                IsPreviewVisible = CheckLineVisible(_p2GamePreview) || (secActive && CheckLineVisible(alt));
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug($"P2 aim preview refresh failed: {ex.Message}");
            }
            finally
            {
                CoopContext.Exit(scope);
                _isRefreshing = false;
            }
        }

        public static void UpdateAimCursor(Player playerTwo, Vector2 aimDir, bool useNativeMouseOverlay)
        {
            bool playing = GameMgr.I != null && GameMgr.I.CurState == GameState.kPlaying;
            if (!playing || playerTwo == null || !playerTwo.gameObject.activeInHierarchy || aimDir.sqrMagnitude <= 0.0001f || CoopVitals.Down(1))
            {
                HideAimCursor();
                return;
            }
            try
            {
                if (!EnsureAimCursorVisual())
                {
                    HideAimCursor();
                    return;
                }

                Color tint = PlayerPalette.GetPlayerColor(1);
                tint.a = 1f;
                if (_lastAppliedTint != tint)
                {
                    _lastAppliedTint = tint;
                    _cursorImage.color = tint;
                }

                if (useNativeMouseOverlay && TryPositionOverNativeCursor())
                {
                    ShowAimCursor();
                    return;
                }

                if (_cachedMainCam == null)
                {
                    _cachedMainCam = Camera.main;
                }
                Camera main = _cachedMainCam;
                if (main == null)
                {
                    HideAimCursor();
                    return;
                }

                if (IsPreviewVisible && TryGetPreviewEndpointScreen(main, out Vector2 endpointScreen))
                {
                    PositionCursorVisual(endpointScreen);
                    ShowAimCursor();
                    return;
                }

                Vector3 p2World = CoopContext.GetPlayerPosition(playerTwo);
                Vector3 p2Screen = main.WorldToScreenPoint(p2World);
                Vector3 tipScreen = main.WorldToScreenPoint(p2World + new Vector3(aimDir.x, aimDir.y, 0f));
                if (p2Screen.z <= 0f || tipScreen.z <= 0f)
                {
                    HideAimCursor();
                    return;
                }
                Vector2 dirScreen = new Vector2(tipScreen.x - p2Screen.x, tipScreen.y - p2Screen.y);
                if (dirScreen.sqrMagnitude <= 0.0001f)
                {
                    HideAimCursor();
                    return;
                }
                dirScreen.Normalize();
                float radius = Mathf.Max(CursorMinRadiusPixels, Mathf.Min(Screen.width, Screen.height) * CursorRadiusScreenFraction);
                PositionCursorVisual(new Vector2(p2Screen.x, p2Screen.y) + dirScreen * radius);
                ShowAimCursor();
            }
            catch
            {
                HideAimCursor();
            }
        }

        public static void HideAimCursor()
        {
            try
            {
                if (_cursorObj != null && _cursorObj.activeSelf)
                {
                    _cursorObj.SetActive(false);
                }
            }
            catch { }
        }

        public static void ResetRound()
        {
            _isRefreshing = false;
            IsPreviewVisible = false;
            _previewCloneAttempted = false;
            _secondaryCloneAttempted = false;
            _cachedMainCam = null;
            _lastAppliedTint = new Color(-1f, -1f, -1f, -1f);
            HideAimCursor();
        }

        public static void ResetRefs()
        {
            _p1GamePreview = null;
            _p1BallPreview = null;
            _p2GamePreview = null;
            _p2BallPreview = null;
            _previewCloneAttempted = false;
            _secondaryCloneAttempted = false;
            IsPreviewVisible = false;
        }

        public static void DestroyOwnedClones()
        {
            try
            {
                for (int i = _ownedAuxObjs.Count - 1; i >= 0; i--)
                {
                    GameObject go = _ownedAuxObjs[i];
                    if (go != null)
                    {
                        go.SetActive(false);
                        UnityEngine.Object.Destroy(go);
                    }
                }
                _ownedAuxObjs.Clear();
                if (_ownedBallPreviewObj != null && _ownedBallPreviewObj != _ownedGamePreviewObj)
                {
                    _ownedBallPreviewObj.SetActive(false);
                    UnityEngine.Object.Destroy(_ownedBallPreviewObj);
                }
                if (_ownedGamePreviewObj != null)
                {
                    _ownedGamePreviewObj.SetActive(false);
                    UnityEngine.Object.Destroy(_ownedGamePreviewObj);
                }
            }
            catch { }
            _ownedGamePreviewObj = null;
            _ownedBallPreviewObj = null;
        }

        public static void Shutdown()
        {
            RestorePlayerOneSingletons();
            DestroyOwnedClones();
            ResetRefs();
            try
            {
                if (_cursorObj != null)
                {
                    UnityEngine.Object.Destroy(_cursorObj);
                }
            }
            catch { }
            _cursorObj = null;
            _cursorRect = null;
            _cursorParentRect = null;
            _cursorImage = null;
            _cursorCanvas = null;
            _cachedMainCam = null;
            _lastAppliedTint = new Color(-1f, -1f, -1f, -1f);
        }

        private static bool PlayerTwoHasMultipleBodies()
        {
            try
            {
                Player p2 = PlayerTwoRuntime.PlayerTwo ?? CoopContext.PlayerTwo;
                return p2?.CharControllers != null && p2.CharControllers.Length > 1;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsurePrivatePreviewRenderers()
        {
            if (_previewCloneAttempted) return;
            _previewCloneAttempted = true;
            try
            {
                if (_p2GamePreview == null && _p1GamePreview != null)
                {
                    GameObject clone = UnityEngine.Object.Instantiate(_p1GamePreview.gameObject, _p1GamePreview.transform.parent);
                    clone.name = _p1GamePreview.gameObject.name + "_LocalCoopP2";
                    GameBallPreview comp = clone.GetComponent<GameBallPreview>();
                    if (comp == null
                        || HarmonyHelper.SameNative(comp.StartLine, _p1GamePreview.StartLine)
                        || HarmonyHelper.SameNative(comp.BounceLine, _p1GamePreview.BounceLine))
                    {
                        UnityEngine.Object.Destroy(clone);
                    }
                    else
                    {
                        _ownedGamePreviewObj = clone;
                        _p2GamePreview = comp;
                        _p2BallPreview ??= clone.GetComponent<BallPreview>();
                        DisableGamePreview(comp);
                        DisableSecondarySlot(comp);
                    }
                }
                if (_p2BallPreview == null && _p1BallPreview != null)
                {
                    GameObject cloneBall = UnityEngine.Object.Instantiate(_p1BallPreview.gameObject, _p1BallPreview.transform.parent);
                    cloneBall.name = _p1BallPreview.gameObject.name + "_LocalCoopP2";
                    BallPreview compBall = cloneBall.GetComponent<BallPreview>();
                    if (compBall == null || HarmonyHelper.SameNative(compBall.StartLine, _p1BallPreview.StartLine))
                    {
                        UnityEngine.Object.Destroy(cloneBall);
                    }
                    else
                    {
                        _ownedBallPreviewObj = cloneBall;
                        _p2BallPreview = compBall;
                        DisableBallPreview(compBall);
                    }
                }
                RestorePlayerOneSingletons();
            }
            catch (Exception ex)
            {
                RestorePlayerOneSingletons();
                Plugin.Logger?.LogWarning($"Could not create private P2 aim preview: {ex.Message}");
            }
        }

        private static void EnsurePrivateWrapperDefaultPreview()
        {
            if (_p1GamePreview == null || _p2GamePreview == null) return;
            try
            {
                GameObject p1Wrap = _p1GamePreview.WrapperDefaultPreview;
                GameObject p2Wrap = _p2GamePreview.WrapperDefaultPreview;
                if (p1Wrap != null && HarmonyHelper.SameNative(p1Wrap, p2Wrap) && !HarmonyHelper.SameNative(p1Wrap, _p1GamePreview.gameObject))
                {
                    GameObject cloned = UnityEngine.Object.Instantiate(p1Wrap, p1Wrap.transform.parent);
                    cloned.name = p1Wrap.name + "_LocalCoopP2_DefaultPreview";
                    cloned.SetActive(p1Wrap.activeSelf);
                    _ownedAuxObjs.Add(cloned);
                    _p2GamePreview.WrapperDefaultPreview = cloned;
                }
            }
            catch { }
        }

        private static bool EnsureAimCursorVisual()
        {
            if (_cursorObj != null && _cursorRect != null && _cursorImage != null)
            {
                return true;
            }
            InGameUI hud = InGameUI.I;
            Image nativeImg = hud?.ImgAimCursor;
            RectTransform nativeRect = hud?.XfmAimCursor;
            if (nativeImg == null || nativeRect == null || nativeRect.parent == null)
            {
                return false;
            }
            var types = new Il2CppReferenceArray<Il2CppSystem.Type>(3);
            types[0] = Il2CppType.Of<RectTransform>();
            types[1] = Il2CppType.Of<CanvasRenderer>();
            types[2] = Il2CppType.Of<Image>();
            var go = new GameObject("BALLxPITLocalCoop.P2AimCursor", types);
            RectTransform rect = go.GetComponent<RectTransform>();
            Image img = go.GetComponent<Image>();
            if (rect == null || img == null)
            {
                UnityEngine.Object.Destroy(go);
                return false;
            }

            go.layer = nativeImg.gameObject.layer;
            rect.SetParent(nativeRect.parent, worldPositionStays: false);
            img.sprite = nativeImg.sprite;
            img.overrideSprite = nativeImg.overrideSprite;
            img.type = nativeImg.type;
            img.preserveAspect = nativeImg.preserveAspect;
            img.fillCenter = nativeImg.fillCenter;
            img.fillMethod = nativeImg.fillMethod;
            img.fillAmount = nativeImg.fillAmount;
            img.fillClockwise = nativeImg.fillClockwise;
            img.fillOrigin = nativeImg.fillOrigin;
            img.useSpriteMesh = nativeImg.useSpriteMesh;
            img.pixelsPerUnitMultiplier = nativeImg.pixelsPerUnitMultiplier;
            img.material = nativeImg.material;
            img.maskable = nativeImg.maskable;
            img.raycastTarget = false;
            rect.anchorMin = nativeRect.anchorMin;
            rect.anchorMax = nativeRect.anchorMax;
            rect.pivot = nativeRect.pivot;
            rect.sizeDelta = nativeRect.sizeDelta;
            rect.localScale = nativeRect.localScale;
            rect.localRotation = nativeRect.localRotation;
            try { rect.SetSiblingIndex(nativeRect.GetSiblingIndex() + 1); } catch { }
            go.SetActive(false);

            _cursorObj = go;
            _cursorRect = rect;
            _cursorParentRect = nativeRect.parent.GetComponent<RectTransform>();
            _cursorImage = img;
            _cursorCanvas = img.canvas;
            _lastAppliedTint = new Color(-1f, -1f, -1f, -1f);
            return true;
        }

        private static bool TryPositionOverNativeCursor()
        {
            RectTransform nativeRect = InGameUI.I?.XfmAimCursor;
            if (_cursorRect == null || nativeRect == null) return false;
            _cursorRect.position = nativeRect.position;
            return true;
        }

        private static bool TryGetPreviewEndpointScreen(Camera main, out Vector2 screenPos)
        {
            screenPos = Vector2.zero;
            BallPreview preview = (BallPreview)_p2GamePreview ?? _p2BallPreview;
            Line startLine = preview?.StartLine;
            if (startLine == null || !startLine.enabled || !startLine.gameObject.activeInHierarchy)
            {
                return false;
            }
            Vector3 worldStart = startLine.transform.TransformPoint(startLine.Start);
            Vector3 worldEnd = startLine.transform.TransformPoint(startLine.End);
            if ((worldEnd - worldStart).sqrMagnitude < 0.04f)
            {
                return false;
            }
            Vector3 screen = main.WorldToScreenPoint(worldEnd);
            if (screen.z <= 0f) return false;
            screenPos = new Vector2(screen.x, screen.y);
            return true;
        }

        private static void PositionCursorVisual(Vector2 targetScreen)
        {
            if (_cursorRect == null) return;
            Camera cam = (_cursorCanvas != null && _cursorCanvas.renderMode != RenderMode.ScreenSpaceOverlay) ? _cursorCanvas.worldCamera : null;
            if (_cursorParentRect != null && RectTransformUtility.ScreenPointToWorldPointInRectangle(_cursorParentRect, targetScreen, cam, out Vector3 worldPoint))
            {
                _cursorRect.position = worldPoint;
            }
            else
            {
                _cursorRect.position = new Vector3(targetScreen.x, targetScreen.y, _cursorRect.position.z);
            }
        }

        private static void ShowAimCursor()
        {
            if (_cursorObj != null && !_cursorObj.activeSelf) _cursorObj.SetActive(true);
        }

        private static bool CheckLineVisible(BallPreview preview)
        {
            Line line = preview?.StartLine;
            if (line == null) return false;
            float alpha = Mathf.Max(line.Color.a, Mathf.Max(line.ColorStart.a, line.ColorEnd.a));
            return line.enabled && line.gameObject.activeInHierarchy && alpha > 0.01f;
        }

        private static void ApplyTint(GameBallPreview preview, bool secondaryActive)
        {
            Color tint = PlayerPalette.GetPlayerColor(1);
            TintShapeLine(preview.StartLine, tint);
            TintShapeLine(preview.BounceLine, tint);
            TintUnityLine(preview.GravityPreview, tint);
            if (secondaryActive)
            {
                TintShapeLine(preview.AltPreview?.StartLine, tint);
                TintShapeLine(preview.AltPreview?.BounceLine, tint);
                TintUnityLine(preview.AltGravityPreview, tint);
            }
        }

        private static void TintShapeLine(Line line, Color tint)
        {
            if (line == null) return;
            line.Color = new Color(tint.r, tint.g, tint.b, line.Color.a);
            line.ColorStart = new Color(tint.r, tint.g, tint.b, line.ColorStart.a);
            line.ColorEnd = new Color(tint.r, tint.g, tint.b, line.ColorEnd.a);
        }

        private static void TintUnityLine(LineRenderer line, Color tint)
        {
            if (line == null) return;
            line.startColor = new Color(tint.r, tint.g, tint.b, line.startColor.a);
            line.endColor = new Color(tint.r, tint.g, tint.b, line.endColor.a);
        }

        private static void DisableSecondarySlot(GameBallPreview preview)
        {
            if (preview == null) return;
            if (_p1GamePreview == null || !HarmonyHelper.SameNative(preview.AltPreview, _p1GamePreview.AltPreview))
            {
                DisableBallPreviewRenderer(preview.AltPreview);
            }
            if (_p1GamePreview == null || !HarmonyHelper.SameNative(preview.AltGravityPreview, _p1GamePreview.AltGravityPreview))
            {
                try { if (preview.AltGravityPreview != null) preview.AltGravityPreview.enabled = false; } catch { }
            }
        }

        private static void DisableBallPreviewRenderer(BallPreview preview)
        {
            if (preview == null) return;
            try { preview.SetInputEnabled(false); preview.enabled = false; } catch { }
            try { if (preview.StartLine != null) preview.StartLine.enabled = false; } catch { }
            try { if (preview.BounceLine != null) preview.BounceLine.enabled = false; } catch { }
        }

        private static void DisableGamePreview(GameBallPreview preview)
        {
            if (preview == null || HarmonyHelper.SameNative(preview, _p1GamePreview)) return;
            try { preview.SetInputEnabled(false); preview.enabled = false; } catch { }
        }

        private static void DisableBallPreview(BallPreview preview)
        {
            if (preview == null || HarmonyHelper.SameNative(preview, _p1BallPreview) || HarmonyHelper.SameNative(preview, _p1GamePreview)) return;
            try { preview.SetInputEnabled(false); preview.enabled = false; } catch { }
        }
    }
}
