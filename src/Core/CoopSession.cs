using System;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.Input;
using BALLxPITLocalCoop.PlayerTwo;
using BALLxPITLocalCoop.UI;
using HarmonyLib;

namespace BALLxPITLocalCoop.Core
{
    /// <summary>
    /// Coordinates per-round state reset cleanly (each subsystem isolated via SafeInvoke so GameMgr.Start never aborts)
    /// and exposes pure aim, life, and session rules.
    /// </summary>
    public static class CoopSession
    {
        private static bool _installed;

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GameMgr), nameof(GameMgr.Start)),
                prefix: new HarmonyMethod(typeof(CoopSession), nameof(ResetRound)) { priority = Priority.First },
                label: "GameMgr.Start (CoopSession)");
        }

        public static void Shutdown()
        {
            ResetRound();
            _installed = false;
        }

        public static bool AllowRuntime() =>
            CoopMode.Enabled
            && SceneMgr.I != null
            && CoopSessionRules.AllowSecondPlayer(coop: true, HarmonyHelper.IsInGameScene());

        public static bool ShouldUpdateIntroFollow()
        {
            if (!AllowRuntime())
            {
                return false;
            }
            if (GameMgr.I != null && GameMgr.I.CurState == GameState.kPlaying)
            {
                PlayerTwoRuntime.ClearIntroFollow();
                return false;
            }
            return true;
        }

        public static void ResetRound()
        {
            HarmonyHelper.SafeInvoke(PlayerTwoRuntime.ResetInstance, "PlayerTwoRuntime.ResetInstance");
            HarmonyHelper.SafeInvoke(PlayerTwoPatches.ResetRound, "PlayerTwoPatches.ResetRound");
            HarmonyHelper.SafeInvoke(CoopCharacterRuntime.ResetRound, "CoopCharacterRuntime.ResetRound");
            HarmonyHelper.SafeInvoke(CoopVitals.ResetRound, "CoopVitals.ResetRound");
            HarmonyHelper.SafeInvoke(CoopPickups.ResetRound, "CoopPickups.ResetRound");
            HarmonyHelper.SafeInvoke(CoopCombat.ResetRound, "CoopCombat.ResetRound");
            HarmonyHelper.SafeInvoke(CoopAmmoRecovery.ResetRound, "CoopAmmoRecovery.ResetRound");
            HarmonyHelper.SafeInvoke(CoopComfort.ResetRound, "CoopComfort.ResetRound");
            HarmonyHelper.SafeInvoke(CoopDecisions.ResetRound, "CoopDecisions.ResetRound");
            HarmonyHelper.SafeInvoke(CoopRevivePrompt.Reset, "CoopRevivePrompt.Reset");
            HarmonyHelper.SafeInvoke(CoopRoundStats.ResetRound, "CoopRoundStats.ResetRound");
            HarmonyHelper.SafeInvoke(CoopHudAndResults.ResetRound, "CoopHudAndResults.ResetRound");
            HarmonyHelper.SafeInvoke(CoopVisuals.ResetRound, "CoopVisuals.ResetRound");
            HarmonyHelper.SafeInvoke(CoopPause.Reset, "CoopPause.Reset");
            HarmonyHelper.SafeInvoke(GamepadRouter.ResetInputSession, "GamepadRouter.ResetInputSession");
        }
    }

    public static class CoopAimRules
    {
        public static float TargetDegrees(float x, float y, float min, float max)
        {
            if (y <= 0f && Math.Abs(x) < 1e-4f)
            {
                return (min + max) * 0.5f;
            }
            double clampedY = y > 0f ? y : 0.0;
            return Math.Max(min, Math.Min(max, (float)(Math.Atan2(clampedY, x) * 180.0 / Math.PI)));
        }

        public static float TurnDegrees(float current, float target, float maxStep)
        {
            float delta = target - current;
            return current + Math.Sign(delta) * Math.Min(Math.Abs(delta), Math.Max(0f, maxStep));
        }
    }

    /// <summary>
    /// Tracks independent per-player health (including Overheal above Maximum), down state,
    /// personal revives, manual/gold rescue, and team revive eligibility.
    /// </summary>
    public sealed class CoopLifeRules
    {
        public const float ManualRescueDuration = 3.5f;
        public const float ManualReviveHealthPct = 0.30f;

        public readonly float[] Health = { 1f, 1f };
        public readonly float[] Maximum = { 1f, 1f };
        public readonly int[] Revives = new int[2];
        public readonly int[] MaxRevives = new int[2];
        public readonly int[] RevivesUsed = new int[2];
        public readonly bool[] Rescued = new bool[2];
        public readonly float[] RescueProgress = new float[2];
        public float ReviveHealthPct = 0.5f;

        public bool Defeated => Down(0) && Down(1);
        public int TotalRevivesRemaining => Math.Max(0, Revives[0]) + Math.Max(0, Revives[1]);
        public int TotalRevivesUsed => RevivesUsed[0] + RevivesUsed[1];

        public bool Down(int slot) => slot >= 0 && slot <= 1 && Health[slot] <= 0f;

        public void Reset(float first, float second, int p1Revives = 0, int p2Revives = 0, float revivePct = 0.5f)
        {
            Maximum[0] = Math.Max(1f, first);
            Maximum[1] = Math.Max(1f, second);
            Health[0] = Maximum[0];
            Health[1] = Maximum[1];
            MaxRevives[0] = Math.Max(0, p1Revives);
            MaxRevives[1] = Math.Max(0, p2Revives);
            Revives[0] = MaxRevives[0];
            Revives[1] = MaxRevives[1];
            RevivesUsed[0] = RevivesUsed[1] = 0;
            ReviveHealthPct = revivePct > 0.05f ? Math.Min(1f, revivePct) : 0.5f;
            Rescued[0] = Rescued[1] = false;
            RescueProgress[0] = RescueProgress[1] = 0f;
        }

        public void SetMaximum(int slot, float value)
        {
            if (slot < 0 || slot > 1) return;
            value = Math.Max(1f, value);
            float bonus = Math.Max(0f, value - Maximum[slot]);
            Maximum[slot] = value;
            if (!Down(slot))
            {
                // Preserve Overheal if current HP already exceeds Maximum (up to vanilla's 4x cap).
                float ceiling = Math.Max(value, Math.Min(Health[slot], value * 4f));
                Health[slot] = Math.Min(ceiling, Health[slot] + bonus);
            }
        }

        public bool TryAutoRevive(int slot)
        {
            if (slot < 0 || slot > 1 || !Down(slot) || Down(1 - slot) || Revives[slot] <= 0)
            {
                return false;
            }

            Revives[slot]--;
            RevivesUsed[slot]++;
            Health[slot] = Math.Max(1f, Math.Min(Maximum[slot], Maximum[slot] * ReviveHealthPct));
            Rescued[slot] = Revives[slot] <= 0;
            RescueProgress[slot] = 0f;
            return true;
        }

        public bool ConsumeTeamRevive()
        {
            // Spend from whichever player has more revives remaining first (tie goes to P1).
            int preferred = Revives[1] > Revives[0] ? 1 : 0;
            int fallback = 1 - preferred;
            if (Revives[preferred] > 0)
            {
                Revives[preferred]--;
                RevivesUsed[preferred]++;
                Rescued[preferred] = Revives[preferred] <= 0;
                return true;
            }
            if (Revives[fallback] > 0)
            {
                Revives[fallback]--;
                RevivesUsed[fallback]++;
                Rescued[fallback] = Revives[fallback] <= 0;
                return true;
            }
            return false;
        }

        public void ReviveTeam()
        {
            float pct = ReviveHealthPct > 0.05f ? Math.Min(1f, ReviveHealthPct) : 0.5f;
            for (int i = 0; i < 2; i++)
            {
                Health[i] = Math.Max(1f, Math.Min(Maximum[i], Maximum[i] * pct));
                RescueProgress[i] = 0f;
            }
        }

        public int ManualReviveGoldCost(float elapsedSeconds)
        {
            int minutes = Math.Max(0, (int)(elapsedSeconds / 60f));
            return Math.Min(500, 50 + minutes * 15 + TotalRevivesUsed * 30);
        }

        public bool CanManualRevive(int targetSlot) =>
            targetSlot >= 0 && targetSlot <= 1 && Down(targetSlot) && !Down(1 - targetSlot) && Revives[targetSlot] <= 0;

        public bool CompleteManualRevive(int targetSlot)
        {
            if (!CanManualRevive(targetSlot))
            {
                return false;
            }
            Health[targetSlot] = Math.Max(1f, Math.Min(Maximum[targetSlot], Maximum[targetSlot] * ManualReviveHealthPct));
            RevivesUsed[targetSlot]++;
            Rescued[targetSlot] = true;
            RescueProgress[targetSlot] = 0f;
            return true;
        }

        public void Heal(int slot, float amount)
        {
            if (slot >= 0 && slot <= 1 && !Down(slot) && amount > 0f)
            {
                float ceiling = Math.Max(Maximum[slot], Health[slot]);
                Health[slot] = Math.Min(ceiling, Health[slot] + amount);
            }
        }
    }

    public static class CoopSessionRules
    {
        public static bool AllowSecondPlayer(bool coop, bool inBattle) => coop && inBattle;

        public static int InputSlot(bool coop, bool independent, int simulationOwner, bool secondContext)
        {
            if (!coop || !independent)
            {
                return -1;
            }
            if (simulationOwner == 0 || simulationOwner == 1)
            {
                return simulationOwner;
            }
            return secondContext ? 1 : 0;
        }
    }
}
