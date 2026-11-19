using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BALLxPITLocalCoop.UI
{
    /// <summary>
    /// Top-down pointer, scroll, and hover hit-testing for <see cref="ChoiceOverlay"/>.
    /// Consumes clicks on foreground action buttons and scrollbars before testing grid cells or option rows
    /// behind them, preventing bleed-through clicks and spurious hover overrides.
    /// </summary>
    public static class OverlayInput
    {
        internal sealed class ActionButton
        {
            public string Id;
            public RectTransform Rect;
            public Image Rim;
            public Image Fill;
            public TextMeshProUGUI Label;
            public bool Enabled = true;
            public bool Active;
            public Color BaseRim = OverlayTheme.ColBronzeOuter;
            public Color BaseFill = OverlayTheme.ColCellFill;
            public Color ActiveFill = new Color(0.29f, 0.19f, 0.12f, 0.98f);
        }

        private static readonly Il2CppStructArray<Vector3> WorldCornersBuffer = new Il2CppStructArray<Vector3>(4);
        private static readonly HashSet<string> ClickedActions = new HashSet<string>();
        private static readonly int[] HoveredGrid = { -1, -1 };
        private static readonly int[] LastHoveredGrid = { -1, -1 };
        private static readonly int[] ClickedGrid = { -1, -1 };
        private static readonly int[] HoveredOption = { -1, -1 };
        private static readonly int[] LastHoveredOption = { -1, -1 };
        private static readonly int[] ClickedOption = { -1, -1 };

        private static Vector3 _lastMousePos;
        private static bool _mouseMovedThisFrame;
        private static int _lastPolledFrame = -1;
        private static bool _wasVisibleLastFrame;
        private static int _draggingScrollSlot = -1;

        public static bool MouseMovedThisFrame => _mouseMovedThisFrame;
        public static int DraggingScrollSlot => _draggingScrollSlot;
        public static int GetLastHoveredGrid(int slot) => slot >= 0 && slot < 2 ? LastHoveredGrid[slot] : -1;
        public static int GetLastHoveredOption(int slot) => slot >= 0 && slot < 2 ? LastHoveredOption[slot] : -1;

        public static void ResetFrameState()
        {
            ClickedActions.Clear();
            ClickedGrid[0] = ClickedGrid[1] = -1;
            ClickedOption[0] = ClickedOption[1] = -1;
            HoveredGrid[0] = HoveredGrid[1] = -1;
            HoveredOption[0] = HoveredOption[1] = -1;
            LastHoveredGrid[0] = LastHoveredGrid[1] = -1;
            LastHoveredOption[0] = LastHoveredOption[1] = -1;
            _wasVisibleLastFrame = false;
            _draggingScrollSlot = -1;
        }

        public static void PollMouseFrame()
        {
            if (_lastPolledFrame == Time.frameCount)
            {
                return;
            }
            _lastPolledFrame = Time.frameCount;

            ClickedActions.Clear();
            ClickedGrid[0] = ClickedGrid[1] = -1;
            ClickedOption[0] = ClickedOption[1] = -1;
            HoveredGrid[0] = HoveredGrid[1] = -1;
            HoveredOption[0] = HoveredOption[1] = -1;

            if (!ChoiceOverlay.Visible || !Application.isFocused)
            {
                _wasVisibleLastFrame = false;
                _draggingScrollSlot = -1;
                return;
            }

            Vector3 mousePos = UnityEngine.Input.mousePosition;
            _mouseMovedThisFrame = _wasVisibleLastFrame && (mousePos - _lastMousePos).sqrMagnitude > 2.5f;
            _lastMousePos = mousePos;
            _wasVisibleLastFrame = true;

            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            bool leftClick = UnityEngine.Input.GetMouseButtonDown(0);
            bool leftHeld = UnityEngine.Input.GetMouseButton(0);
            if (!leftHeld)
            {
                _draggingScrollSlot = -1;
            }
            float scrollWheel = UnityEngine.Input.mouseScrollDelta.y;

            // 1. Test foreground ActionButtons first; consume click so it never bleeds into cards/grids/rows behind!
            bool pointerConsumed = false;
            foreach (var kv in ChoiceOverlay.ActionButtons)
            {
                ActionButton btn = kv.Value;
                if (btn?.Rect == null || !btn.Rect.gameObject.activeInHierarchy) continue;

                bool over = btn.Enabled && ContainsScreenPoint(btn.Rect, mousePos);
                ApplyActionButtonVisual(btn, over);
                if (over)
                {
                    pointerConsumed = true;
                    if (leftClick)
                    {
                        ClickedActions.Add(btn.Id);
                    }
                }
            }

            // 2. Test per-card scrollbars, grids, and option rows only if foreground button didn't consume pointer.
            for (int slot = 0; slot < 2; slot++)
            {
                RectTransform card = ChoiceOverlay.Cards[slot];
                if (card == null || !card.gameObject.activeInHierarchy) continue;

                RectTransform vp = ChoiceOverlay.OptionViewports[slot];
                Image sbTrack = ChoiceOverlay.OptionScrollTracks[slot];
                if (vp != null && vp.gameObject.activeInHierarchy)
                {
                    float maxScroll = Math.Max(0f, ChoiceOverlay.OptionContentHeight[slot] - ChoiceOverlay.OptionViewportHeight[slot]);
                    if (maxScroll > 0.5f && sbTrack != null && sbTrack.gameObject.activeInHierarchy)
                    {
                        if (!pointerConsumed && leftClick && ContainsScreenPoint(sbTrack.rectTransform, mousePos))
                        {
                            _draggingScrollSlot = slot;
                            pointerConsumed = true;
                        }
                        if (_draggingScrollSlot == slot && leftHeld)
                        {
                            sbTrack.rectTransform.GetWorldCorners(WorldCornersBuffer);
                            float trackMin = WorldCornersBuffer[0].y;
                            float trackMax = WorldCornersBuffer[2].y;
                            float normFromTop = 1f - Mathf.Clamp01((mousePos.y - trackMin) / Mathf.Max(1f, trackMax - trackMin));
                            ChoiceOverlay.OptionScrollY[slot] = normFromTop * maxScroll;
                            pointerConsumed = true;
                        }
                        else if (Mathf.Abs(scrollWheel) > 0.01f && ContainsScreenPoint(card, mousePos))
                        {
                            ChoiceOverlay.OptionScrollY[slot] = Mathf.Clamp(
                                ChoiceOverlay.OptionScrollY[slot] - scrollWheel * ChoiceOverlay.OptionRowStep * 0.85f,
                                0f,
                                maxScroll);
                        }
                    }
                }

                if (pointerConsumed)
                {
                    continue;
                }

                var gridBacks = ChoiceOverlay.GridBacks[slot];
                for (int j = 0; j < gridBacks.Count; j++)
                {
                    Image cell = gridBacks[j];
                    if (cell == null || !cell.gameObject.activeInHierarchy) continue;
                    if (ContainsScreenPoint(cell.rectTransform, mousePos))
                    {
                        HoveredGrid[slot] = j;
                        if (leftClick) ClickedGrid[slot] = j;
                        break;
                    }
                }

                if (vp != null && vp.gameObject.activeInHierarchy && ContainsScreenPoint(vp, mousePos))
                {
                    var rows = ChoiceOverlay.OptionRows[slot];
                    for (int r = 0; r < rows.Count; r++)
                    {
                        var row = rows[r];
                        if (row?.Back == null || !row.Back.gameObject.activeInHierarchy) continue;
                        if (ContainsScreenPoint(row.Back.rectTransform, mousePos))
                        {
                            HoveredOption[slot] = r;
                            if (leftClick) ClickedOption[slot] = r;
                            break;
                        }
                    }
                }
            }

            LastHoveredGrid[0] = HoveredGrid[0];
            LastHoveredGrid[1] = HoveredGrid[1];
            LastHoveredOption[0] = HoveredOption[0];
            LastHoveredOption[1] = HoveredOption[1];
        }

        public static bool ConsumeActionClicked(string id) => ClickedActions.Remove(id);

        public static bool IsActionHovered(string id) =>
            ChoiceOverlay.ActionButtons.TryGetValue(id, out ActionButton btn)
            && btn?.Rect != null
            && btn.Enabled
            && btn.Rect.gameObject.activeInHierarchy
            && ContainsScreenPoint(btn.Rect, UnityEngine.Input.mousePosition);

        public static bool TryGetClickedGridIndex(int slot, out int index)
        {
            index = slot >= 0 && slot < 2 ? ClickedGrid[slot] : -1;
            return index >= 0;
        }

        public static bool TryGetHoveredGridIndex(int slot, out int index)
        {
            index = slot >= 0 && slot < 2 ? HoveredGrid[slot] : -1;
            return index >= 0 && _mouseMovedThisFrame;
        }

        public static bool TryGetClickedOptionIndex(int slot, out int index)
        {
            index = slot >= 0 && slot < 2 ? ClickedOption[slot] : -1;
            return index >= 0;
        }

        public static bool TryGetHoveredOptionIndex(int slot, out int index)
        {
            index = slot >= 0 && slot < 2 ? HoveredOption[slot] : -1;
            return index >= 0 && _mouseMovedThisFrame;
        }

        internal static bool ContainsScreenPoint(RectTransform rt, Vector3 screenPoint)
        {
            if (rt == null) return false;
            rt.GetWorldCorners(WorldCornersBuffer);
            return screenPoint.x >= WorldCornersBuffer[0].x
                && screenPoint.x <= WorldCornersBuffer[2].x
                && screenPoint.y >= WorldCornersBuffer[0].y
                && screenPoint.y <= WorldCornersBuffer[2].y;
        }

        internal static void ApplyActionButtonVisual(ActionButton btn, bool hovered)
        {
            if (!btn.Enabled)
            {
                btn.Rim.color = OverlayTheme.ColBronzeDark;
                btn.Fill.color = OverlayTheme.ColPanelWell;
                return;
            }
            if (btn.Active)
            {
                btn.Rim.color = OverlayTheme.ColGoldHighlight;
                btn.Fill.color = hovered ? new Color(0.38f, 0.25f, 0.16f, 0.98f) : btn.ActiveFill;
                return;
            }
            btn.Rim.color = hovered ? OverlayTheme.ColGoldHighlight : btn.BaseRim;
            btn.Fill.color = hovered ? OverlayTheme.ColHoverGlow : btn.BaseFill;
        }
    }
}
