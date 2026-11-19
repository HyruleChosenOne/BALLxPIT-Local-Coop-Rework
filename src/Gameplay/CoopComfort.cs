using System;
using System.Collections.Generic;
using BALLxPITLocalCoop.Core;
using HarmonyLib;
using UnityEngine;

namespace BALLxPITLocalCoop.Gameplay
{
    /// <summary>
    /// Softens enemy hit-flash pulsing during co-op when ReduceHitFlashes is enabled,
    /// safely restoring pooled enemies' original colors even when deactivated mid-flash.
    /// </summary>
    public static class CoopComfort
    {
        private sealed class Pulse
        {
            public GridPieceObj Piece;
            public Color RestoreColor;
            public float Until;
            public float Next;
            public bool Flashed;
        }

        private static readonly Dictionary<IntPtr, Pulse> Pulses = new Dictionary<IntPtr, Pulse>(64);
        private static readonly List<IntPtr> StalePulseKeys = new List<IntPtr>(32);
        private static bool _installed;

        public static void Install(Harmony harmony)
        {
            if (harmony == null || _installed)
            {
                return;
            }
            _installed = true;
            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GridPieceObj), "HitFlash"),
                prefix: new HarmonyMethod(typeof(CoopComfort), nameof(Hit)),
                label: "GridPieceObj.HitFlash");
        }

        public static void ResetRound()
        {
            RestoreAndClearPulses();
        }

        public static void Shutdown()
        {
            RestoreAndClearPulses();
            _installed = false;
        }

        private static bool Hit(GridPieceObj __instance)
        {
            if (!CoopMode.Enabled || !(ModConfig.ReduceHitFlashes?.Value ?? false) || __instance == null)
            {
                return true;
            }
            IntPtr ptr = HarmonyHelper.Ptr(__instance);
            if (ptr == IntPtr.Zero)
            {
                return true;
            }
            if (!Pulses.TryGetValue(ptr, out Pulse pulse))
            {
                pulse = new Pulse { Piece = __instance, RestoreColor = Color.white };
                Pulses[ptr] = pulse;
            }
            float now = Time.unscaledTime;
            if (now >= pulse.Next)
            {
                if (!pulse.Flashed)
                {
                    pulse.RestoreColor = (__instance.SpriteObj != null && __instance.SpriteObj.Rend != null)
                        ? __instance.SpriteObj.Rend.color
                        : Color.white;
                }
                pulse.Next = now + 0.18f;
                pulse.Until = now + 0.07f;
                pulse.Flashed = true;
                __instance.SetColor(new Color(0.82f, 0.78f, 0.74f, 1f));
            }
            return false;
        }

        public static void Tick()
        {
            if (!CoopMode.Enabled || !(ModConfig.ReduceHitFlashes?.Value ?? false))
            {
                if (Pulses.Count > 0) RestoreAndClearPulses();
                return;
            }
            if (!HarmonyHelper.IsInGameScene())
            {
                Pulses.Clear();
                StalePulseKeys.Clear();
                return;
            }
            if (Pulses.Count == 0)
            {
                return;
            }

            StalePulseKeys.Clear();
            float now = Time.unscaledTime;
            foreach (KeyValuePair<IntPtr, Pulse> kv in Pulses)
            {
                Pulse pulse = kv.Value;
                if (!HarmonyHelper.IsUnityAlive(pulse.Piece) || pulse.Piece.gameObject == null)
                {
                    StalePulseKeys.Add(kv.Key);
                    continue;
                }
                if (!pulse.Piece.gameObject.activeInHierarchy)
                {
                    // Restore color before dropping pooled enemy so reuse doesn't inherit muted grey tint!
                    if (pulse.Flashed)
                    {
                        try { pulse.Piece.SetColor(pulse.RestoreColor); } catch { }
                        pulse.Flashed = false;
                    }
                    StalePulseKeys.Add(kv.Key);
                    continue;
                }
                if (pulse.Flashed && now >= pulse.Until)
                {
                    pulse.Flashed = false;
                    pulse.Until = 0f;
                    try { pulse.Piece.SetColor(pulse.RestoreColor); } catch { }
                }
                if (!pulse.Flashed && now >= pulse.Next)
                {
                    StalePulseKeys.Add(kv.Key);
                }
            }
            for (int i = 0; i < StalePulseKeys.Count; i++)
            {
                Pulses.Remove(StalePulseKeys[i]);
            }
            StalePulseKeys.Clear();
        }

        private static void RestoreAndClearPulses()
        {
            foreach (KeyValuePair<IntPtr, Pulse> kv in Pulses)
            {
                Pulse pulse = kv.Value;
                if (pulse != null && pulse.Flashed && HarmonyHelper.IsUnityAlive(pulse.Piece))
                {
                    try { pulse.Piece.SetColor(pulse.RestoreColor); } catch { }
                }
            }
            Pulses.Clear();
            StalePulseKeys.Clear();
        }
    }
}
