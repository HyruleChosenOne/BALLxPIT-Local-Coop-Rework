using UnityEngine;

namespace BALLxPITLocalCoop.Core
{
    public enum ActiveModalOverlay
    {
        None = 0,
        CharSelect = 1,
        StageSelect = 2,
        Challenges = 3,
        Settings = 4,
        LevelUp = 5,
        Revive = 6,
        Pause = 7,
        Result = 8,
        LoadingBanner = 9
    }

    /// <summary>
    /// Single authoritative modal overlay coordinator so Town menus, Challenges, Pause,
    /// Level-Up/Fuser, Revive, and Result screens never collide or bleed Esc/B across frames.
    /// </summary>
    public static class ModalOverlayState
    {
        private static int _cancelConsumedFrame = -1;
        private static int _confirmConsumedFrame = -1;

        public static ActiveModalOverlay Current { get; private set; } = ActiveModalOverlay.None;

        public static bool IsAnyOpen => Current != ActiveModalOverlay.None;

        public static bool IsTownMenuOpen =>
            Current == ActiveModalOverlay.CharSelect
            || Current == ActiveModalOverlay.StageSelect
            || Current == ActiveModalOverlay.Challenges
            || Current == ActiveModalOverlay.Settings
            || Current == ActiveModalOverlay.LoadingBanner;

        public static bool IsBattleOverlayBlockingPause =>
            Current == ActiveModalOverlay.LevelUp
            || Current == ActiveModalOverlay.Revive
            || Current == ActiveModalOverlay.Result
            || Current == ActiveModalOverlay.LoadingBanner;

        public static void Set(ActiveModalOverlay overlay)
        {
            Current = overlay;
        }

        public static void ClearIf(ActiveModalOverlay overlay)
        {
            if (Current == overlay)
            {
                Current = ActiveModalOverlay.None;
            }
        }

        public static void Reset()
        {
            Current = ActiveModalOverlay.None;
            _cancelConsumedFrame = -1;
            _confirmConsumedFrame = -1;
        }

        public static void ConsumeCancelThisFrame() => _cancelConsumedFrame = Time.frameCount;
        public static bool IsCancelConsumedThisFrame() => _cancelConsumedFrame == Time.frameCount;

        public static void ConsumeConfirmThisFrame() => _confirmConsumedFrame = Time.frameCount;
        public static bool IsConfirmConsumedThisFrame() => _confirmConsumedFrame == Time.frameCount;
    }
}
