using System;
using System.Collections.Generic;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Diagnostics;
using BALLxPITLocalCoop.Input;
using BALLxPITLocalCoop.PlayerTwo;
using BALLxPITLocalCoop.UI;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MEC;
using UnityEngine;

namespace BALLxPITLocalCoop.Gameplay
{
    /// <summary>
    /// Isolates P1 and P2 health pools, native healing (preserving Overheal, heal babies, and floating numbers),
    /// damage transactions, invulnerability frames (pause-safe), downed renderer+collider disabling,
    /// manual/gold rescue, and team death/revive transitions.
    /// </summary>
    public static class CoopVitals
    {
        private const float DamageGuardOffset = 65536f;
        public const float ManualRescueRadius = 2.4f;

        private sealed class DamageScope
        {
            public int Owner;
            public int PreviousSlot;
            public float Before;
            public float PriorBackingHealth;
            public float GuardedHealth;
            public float NestedDelta;
            public DamageScope Parent;
            public bool Closed;

            public void Close()
            {
                if (!Closed)
                {
                    Closed = true;
                    _transactions = Math.Max(0, _transactions - 1);
                    _activeSlot = PreviousSlot;
                    _currentDamageScope = Parent;
                }
            }
        }

        private sealed class HealScope
        {
            public int Owner;
            public int PreviousSlot;
            public float Before;
            public float PriorBackingHealth;
            public CoopContext.ScopeToken Context;
            public bool InsideDamage;
            public bool Closed;
        }

        public static readonly CoopLifeRules Life = new CoopLifeRules();
        public static readonly float[] ImmuneUntil = new float[2];
        public static readonly Vector3[] DownedPos = new Vector3[2];
        public static readonly bool[] HasDownedPos = new bool[2];
        public static readonly bool[] ManualRescueInRing = new bool[2];

        private static int _activeSlot;
        private static int _transactions;
        private static int _healDepth;
        private static DamageScope _currentDamageScope;
        private static bool _allowDefeat;
        private static bool _writingBacking;
        private static float _lastLivingHitTime;

        private static readonly Dictionary<IntPtr, bool> RendererStates = new Dictionary<IntPtr, bool>();
        private static readonly Dictionary<IntPtr, List<Renderer>> CachedRenderers = new Dictionary<IntPtr, List<Renderer>>();
        private static readonly Dictionary<IntPtr, bool> ColliderStates = new Dictionary<IntPtr, bool>();
        private static readonly Dictionary<IntPtr, List<Collider2D>> CachedColliders = new Dictionary<IntPtr, List<Collider2D>>();
        private static readonly float[] NextVisualRefreshAt = new float[2];
        private static bool _installed;

        public static bool Ready { get; private set; }
        public static bool Active => Ready && CoopMode.Enabled && HarmonyHelper.IsInGameScene();
        public static bool Down(int slot) => Active && Life.Down(slot);

        public static int Owner(Player player)
        {
            return CoopContext.IsPlayerTwo(player) ? 1 : 0;
        }

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(Player), nameof(Player.Damage)),
                prefix: new HarmonyMethod(typeof(CoopVitals), nameof(BeforeDamage)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(CoopVitals), nameof(AfterDamage)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(CoopVitals), nameof(FinishDamage)),
                label: "Player.Damage (CoopVitals)");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(Player), nameof(Player.Heal)),
                prefix: new HarmonyMethod(typeof(CoopVitals), nameof(BeforeHeal)),
                postfix: new HarmonyMethod(typeof(CoopVitals), nameof(AfterHeal)),
                finalizer: new HarmonyMethod(typeof(CoopVitals), nameof(FinishHeal)),
                label: "Player.Heal (CoopVitals)");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(Player), nameof(Player.RunGameOver)),
                prefix: new HarmonyMethod(typeof(CoopVitals), nameof(AllowNativeGameOver)),
                label: "Player.RunGameOver");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(Player), nameof(Player.MyUpdate)),
                prefix: new HarmonyMethod(typeof(CoopVitals), nameof(CanSimulatePlayer)) { priority = Priority.First },
                label: "Player.MyUpdate (CoopVitals)");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(Player), nameof(Player.FixedUpdate)),
                prefix: new HarmonyMethod(typeof(CoopVitals), nameof(CanSimulatePlayer)) { priority = Priority.First },
                label: "Player.FixedUpdate (CoopVitals)");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(Player), nameof(Player.CanBeDamaged)),
                prefix: new HarmonyMethod(typeof(CoopVitals), nameof(CanBeDamaged)),
                label: "Player.CanBeDamaged");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(GameMgr), nameof(GameMgr.LoseGame)),
                prefix: new HarmonyMethod(typeof(CoopVitals), nameof(AllowTeamDeath)),
                label: "GameMgr.LoseGame");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), nameof(BallMgr.RunBalls)),
                prefix: new HarmonyMethod(typeof(CoopVitals), nameof(CanShoot)),
                label: "BallMgr.RunBalls (CoopVitals)");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(BallMgr), nameof(BallMgr.MoveBalls)),
                prefix: new HarmonyMethod(typeof(CoopVitals), nameof(CanMoveBalls)),
                label: "BallMgr.MoveBalls (CoopVitals)");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(GameMgr), nameof(GameMgr.MarkLevelComplete)),
                prefix: new HarmonyMethod(typeof(CoopVitals), nameof(CanWin)),
                label: "GameMgr.MarkLevelComplete");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(InGameUI), nameof(InGameUI.RefreshHealthBar)),
                prefix: new HarmonyMethod(typeof(CoopVitals), nameof(AllowNativeHealthBar)),
                label: "InGameUI.RefreshHealthBar");

            HarmonyHelper.TryPatch(harmony, AccessTools.Method(typeof(InGameUI), nameof(InGameUI.OnDamaged)),
                prefix: new HarmonyMethod(typeof(CoopVitals), nameof(OnUiDamaged)),
                label: "InGameUI.OnDamaged");
        }

        public static void Shutdown()
        {
            RestoreAllDownedVisualsAndColliders();
            ResetRound();
            _installed = false;
        }

        public static void ResetRound()
        {
            RestoreAllDownedVisualsAndColliders();
            Ready = false;
            _activeSlot = 0;
            _transactions = 0;
            _healDepth = 0;
            _currentDamageScope = null;
            _allowDefeat = false;
            _writingBacking = false;
            _lastLivingHitTime = 0f;
            ImmuneUntil[0] = ImmuneUntil[1] = 0f;
            DownedPos[0] = DownedPos[1] = Vector3.zero;
            HasDownedPos[0] = HasDownedPos[1] = false;
            ManualRescueInRing[0] = ManualRescueInRing[1] = false;
            NextVisualRefreshAt[0] = NextVisualRefreshAt[1] = 0f;
            RendererStates.Clear();
            CachedRenderers.Clear();
            ColliderStates.Clear();
            CachedColliders.Clear();
            Life.Reset(100f, 100f, 0, 0, 0.5f);
        }

        private static void WriteBackingHealth()
        {
            // Never overwrite BattleSaveData.I.CurHealth while inside a Player.Damage or Player.Heal transaction!
            if (_transactions > 0 || _healDepth > 0 || BattleSaveData.I == null || _writingBacking)
            {
                return;
            }
            _writingBacking = true;
            try
            {
                int target = _activeSlot;
                if (Life.Down(target) && !Life.Down(1 - target))
                {
                    target = 1 - target;
                }
                BattleSaveData.I.CurHealth = Life.Down(target) ? (Life.Defeated ? 0f : 1f) : Life.Health[target];
            }
            finally
            {
                _writingBacking = false;
            }
        }

        public static void SyncBackingHealth()
        {
            if (Active && _transactions == 0 && _healDepth == 0)
            {
                _activeSlot = CoopContext.IsPlayerTwoContext ? 1 : 0;
                WriteBackingHealth();
            }
        }

        // --- Damage transaction ---

        private static bool BeforeDamage(Player __instance, ref EnemyAttackResult __result, float __0, PieceDmgType __1, out DamageScope __state)
        {
            __state = null;
            if (!Active || BattleSaveData.I == null)
            {
                return true;
            }

            int owner = Owner(__instance);
            if (Life.Down(owner) || Time.time < ImmuneUntil[owner])
            {
                __result = EnemyAttackResult.kInvulnerable;
                return false;
            }

            // Pad CurHealth above zero so vanilla Player.Damage never trips CurHealth <= 0f
            // and fires RunDeathBonus / LoseGame / SetState(kRevive) when only one player goes down.
            DamageScope parent = _currentDamageScope;
            float priorBacking = BattleSaveData.I.CurHealth;
            float guarded = Life.Health[owner] + DamageGuardOffset;
            __state = new DamageScope
            {
                Owner = owner,
                PreviousSlot = _activeSlot,
                Before = Life.Health[owner],
                PriorBackingHealth = priorBacking,
                GuardedHealth = guarded,
                Parent = parent
            };
            _transactions++;
            _currentDamageScope = __state;
            _activeSlot = owner;
            BattleSaveData.I.CurHealth = guarded;
            return true;
        }

        private static void AfterDamage(DamageScope __state)
        {
            if (__state == null || BattleSaveData.I == null)
            {
                return;
            }

            float delta = BattleSaveData.I.CurHealth - __state.GuardedHealth;
            DamageScope sameOwnerParent = __state.Parent;
            while (sameOwnerParent != null && sameOwnerParent.Owner != __state.Owner)
            {
                sameOwnerParent = sameOwnerParent.Parent;
            }
            if (sameOwnerParent != null)
            {
                sameOwnerParent.NestedDelta += delta + __state.NestedDelta;
                BattleSaveData.I.CurHealth = __state.PriorBackingHealth;
                __state.Close();
                return;
            }

            // Allow Overheal ceiling when a nested heal raised HP above MaxHealth, otherwise cap at current ceiling.
            float ceiling = Math.Max(Life.Maximum[__state.Owner], __state.Before);
            if (__state.NestedDelta > 0f)
            {
                ceiling = Math.Max(ceiling, Life.Maximum[__state.Owner] * 4f);
            }
            float current = Mathf.Clamp(__state.Before + delta + __state.NestedDelta, 0f, ceiling);
            Life.Health[__state.Owner] = current;

            int other = 1 - __state.Owner;
            if (__state.Parent == null)
            {
                BattleSaveData.I.CurHealth = (current <= 0f && !Life.Defeated)
                    ? (!Life.Down(other) ? Life.Health[other] : 1f)
                    : current;
            }
            else
            {
                BattleSaveData.I.CurHealth = __state.PriorBackingHealth;
            }
            __state.Close();

            if (current > 0f && current < __state.Before)
            {
                _lastLivingHitTime = Time.unscaledTime;
            }
            if (current < __state.Before)
            {
                GamepadRouter.VibratePad(__state.Owner, 0.65f, 0.2f);
            }
            if (current <= 0f && __state.Before > 0f)
            {
                int owner = __state.Owner;
                if (Life.TryAutoRevive(owner))
                {
                    ImmuneUntil[owner] = Time.time + 1.25f;
                    MovePlayerToSafeBottom(owner, CoopCombat.PlayerAt(owner));
                    GamepadRouter.VibratePad(owner, 0.7f, 0.35f);
                }
                else
                {
                    Player downed = CoopCombat.PlayerAt(owner);
                    DownedPos[owner] = downed != null ? CoopCombat.Pos(downed) : Vector3.zero;
                    HasDownedPos[owner] = downed != null;
                    CancelOwnerActiveShot(owner);
                    if (!Life.Defeated && MinLivingHpRatio() > 0.30f)
                    {
                        ClearBloodOverlay();
                    }
                }
            }

            if (_transactions == 0 && _healDepth == 0)
            {
                WriteBackingHealth();
            }
        }

        private static void FinishDamage(DamageScope __state)
        {
            if (__state != null && !__state.Closed)
            {
                if (__state.Parent != null && BattleSaveData.I != null)
                {
                    BattleSaveData.I.CurHealth = __state.PriorBackingHealth;
                }
                __state.Close();
                if (_transactions == 0 && _healDepth == 0)
                {
                    WriteBackingHealth();
                }
            }
        }

        // --- Native Heal transaction (preserves Overheal up to 4x, heal babies, floating numbers) ---

        private static bool BeforeHeal(Player __instance, float __0, out HealScope __state)
        {
            __state = null;
            if (CoopMode.Enabled && !CoopCharacterRuntime.IndependentReady)
            {
                return false;
            }
            if (!Active || BattleSaveData.I == null || UpgradeMgr.I == null)
            {
                return true;
            }

            int slot = Owner(__instance);
            if (Life.Down(slot))
            {
                return false;
            }

            int maxHp = CoopCharacterRuntime.GetMaxHealth(slot);
            if (maxHp > 0)
            {
                Life.SetMaximum(slot, maxHp);
            }

            // If healing occurs nested inside a DamageScope for this same slot (e.g. Thorns -> HealthPerKill),
            // compute the current effective HP so vanilla Overheal caps accurately against live HP.
            float effectiveBefore = Life.Health[slot];
            DamageScope matchingDmg = FindDamageScopeFor(slot);
            if (matchingDmg != null)
            {
                float dmgSoFar = (BattleSaveData.I.CurHealth - matchingDmg.GuardedHealth) + matchingDmg.NestedDelta;
                effectiveBefore = Mathf.Max(1f, matchingDmg.Before + dmgSoFar);
            }

            var ctx = CoopContext.EnterSlot(slot, CoopContext.ScopeKind.Full);
            float priorBacking = BattleSaveData.I.CurHealth;

            __state = new HealScope
            {
                Owner = slot,
                PreviousSlot = _activeSlot,
                Before = effectiveBefore,
                PriorBackingHealth = priorBacking,
                Context = ctx,
                InsideDamage = _transactions > 0
            };

            _healDepth++;
            _activeSlot = slot;
            BattleSaveData.I.CurHealth = effectiveBefore;
            return true;
        }

        private static void AfterHeal(HealScope __state)
        {
            if (__state == null || __state.Closed || BattleSaveData.I == null)
            {
                return;
            }

            float after = BattleSaveData.I.CurHealth;
            float delta = after - __state.Before;

            DamageScope matchingDmg = FindDamageScopeFor(__state.Owner);
            if (matchingDmg != null)
            {
                matchingDmg.NestedDelta += delta;
                BattleSaveData.I.CurHealth = __state.PriorBackingHealth;
            }
            else
            {
                float maxAllowed = Math.Max(Life.Maximum[__state.Owner] * 4f, Life.Maximum[__state.Owner]);
                Life.Health[__state.Owner] = Mathf.Clamp(after, 0f, maxAllowed);
                if (__state.InsideDamage)
                {
                    BattleSaveData.I.CurHealth = __state.PriorBackingHealth;
                }
            }

            CloseHealScope(__state);
        }

        private static void FinishHeal(HealScope __state)
        {
            if (__state != null && !__state.Closed)
            {
                if (__state.InsideDamage && BattleSaveData.I != null)
                {
                    BattleSaveData.I.CurHealth = __state.PriorBackingHealth;
                }
                CloseHealScope(__state);
            }
        }

        private static void CloseHealScope(HealScope scope)
        {
            scope.Closed = true;
            _healDepth = Math.Max(0, _healDepth - 1);
            _activeSlot = scope.PreviousSlot;
            CoopContext.Exit(scope.Context);
            if (_transactions == 0 && _healDepth == 0)
            {
                WriteBackingHealth();
            }
        }

        private static DamageScope FindDamageScopeFor(int slot)
        {
            DamageScope cur = _currentDamageScope;
            while (cur != null)
            {
                if (cur.Owner == slot) return cur;
                cur = cur.Parent;
            }
            return null;
        }

        // --- Stats & Guards ---

        public static void RefreshStats()
        {
            if (!Active || UpgradeMgr.I == null)
            {
                return;
            }
            for (int slot = 0; slot < 2; slot++)
            {
                int maxHp = CoopCharacterRuntime.GetMaxHealth(slot);
                if (maxHp > 0)
                {
                    Life.SetMaximum(slot, maxHp);
                }
            }
            WriteBackingHealth();
        }

        private static bool CanBeDamaged(Player __instance, ref bool __result)
        {
            if (!Active)
            {
                return true;
            }
            int slot = Owner(__instance);
            if (!Life.Down(slot) && Time.time >= ImmuneUntil[slot])
            {
                return true;
            }
            __result = false;
            return false;
        }

        private static bool CanSimulatePlayer(Player __instance) => !Active || !Life.Down(Owner(__instance));
        private static bool AllowNativeGameOver() => !Active;

        private static bool AllowTeamDeath()
        {
            if (!Active) return true;
            if (_allowDefeat) return true;
            // Never let vanilla force defeat while either player is alive OR revives remain!
            return Life.Defeated && Life.TotalRevivesRemaining <= 0;
        }

        private static bool CanShoot() => !Active || !Life.Down(Owner(Player.I));
        private static bool CanWin() => !Active || !Life.Defeated;

        private static bool CanMoveBalls(BallMgr __instance) =>
            !CoopMode.Enabled
            || (__instance != null
                && HarmonyHelper.SameNative(__instance, BallMgr.I)
                && HarmonyHelper.IsInGameScene()
                && !CoopRoundStats.Complete);

        private static bool AllowNativeHealthBar() => !CoopMode.Enabled || !Ready;

        private static void CancelOwnerActiveShot(int owner)
        {
            if (owner == 1)
            {
                PlayerTwoPatches.CancelActivePlayerTwoCoroutine();
                if (BallMgr.I != null && PlayerTwoRuntime.PlayerTwo != null)
                {
                    var scope = CoopContext.EnterPlayerTwo(PlayerTwoRuntime.PlayerTwo, CoopContext.ScopeKind.Full);
                    try
                    {
                        if (BallMgr.I._ballAnim.IsValid) Timing.KillCoroutines(BallMgr.I._ballAnim);
                    }
                    finally
                    {
                        CoopContext.Exit(scope);
                    }
                }
            }
            else if (BallMgr.I != null)
            {
                var scope = CoopContext.EnterPlayerOne(CoopContext.ScopeKind.Full);
                try
                {
                    if (BallMgr.I._ballAnim.IsValid) Timing.KillCoroutines(BallMgr.I._ballAnim);
                }
                finally
                {
                    CoopContext.Exit(scope);
                }
            }
        }

        public static void StopShots()
        {
            CancelOwnerActiveShot(1);
            CancelOwnerActiveShot(0);
        }

        private static void MovePlayerToSafeBottom(int slot, Player target)
        {
            if (target == null)
            {
                return;
            }
            float minX = -5f;
            float maxX = 5f;
            float minY = -4f;
            try
            {
                minX = target.GetMinX();
                maxX = target.GetMaxX();
                minY = target.GetMinY();
            }
            catch { }

            float spanX = Mathf.Max(1f, maxX - minX);
            float spawnX = target.ClampXPos(minX + spanX * (slot == 0 ? 0.36f : 0.64f));
            float spawnY = target.ClampYPos(minY + 0.65f);
            Vector3 cur = CoopCombat.Pos(target);
            Vector3 safePos = new Vector3(spawnX, spawnY, cur.z);

            CoopCharacterRuntime.ForSlot(slot, () =>
            {
                target.SetPos(safePos, snap: true);
                target.IsRunningLevelUp = false;
            });
        }

        public static bool ReviveTeam()
        {
            if (!Active)
            {
                return false;
            }

            Life.ReviveTeam();
            for (int i = 0; i < 2; i++)
            {
                ImmuneUntil[i] = Time.time + 1.25f;
                HasDownedPos[i] = false;
                ManualRescueInRing[i] = false;
                MovePlayerToSafeBottom(i, CoopCombat.PlayerAt(i));
                GamepadRouter.VibratePad(i, 0.55f, 0.3f);
            }
            ClearBloodOverlay();
            UpdateDownedVisualsAndColliders();
            WriteBackingHealth();
            return true;
        }

        public static bool TryPayGoldRevive(int downedSlot)
        {
            if (!Active || GameMgr.I == null || GameMgr.I.CurState != GameState.kPlaying || CoopRoundStats.Complete)
            {
                return false;
            }
            if (!Life.CanManualRevive(downedSlot) || !ManualRescueInRing[downedSlot] || BattleSaveData.I == null || BattleSaveData.I.NumResources == null)
            {
                return false;
            }
            int cost = Life.ManualReviveGoldCost(BattleSaveData.I.ElapsedTime);
            if (BattleSaveData.I.GetNumResources(ResourceType.kGold) < cost)
            {
                return false;
            }
            // Only deduct gold if ExecuteManualRevive actually succeeds!
            if (!ExecuteManualRevive(downedSlot))
            {
                return false;
            }
            BattleSaveData.I.NumResources.AddResource(ResourceType.kGold, -cost);
            return true;
        }

        private static bool ExecuteManualRevive(int targetSlot)
        {
            if (!Life.CompleteManualRevive(targetSlot))
            {
                return false;
            }
            HasDownedPos[targetSlot] = false;
            ManualRescueInRing[targetSlot] = false;
            ImmuneUntil[targetSlot] = Time.time + 1.25f;
            MovePlayerToSafeBottom(targetSlot, CoopCombat.PlayerAt(targetSlot));
            ClearBloodOverlay();
            UpdateDownedVisualsAndColliders();
            WriteBackingHealth();
            GamepadRouter.VibratePad(targetSlot, 0.7f, 0.35f);
            GamepadRouter.VibratePad(1 - targetSlot, 0.45f, 0.25f);
            return true;
        }

        public static float MinLivingHpRatio()
        {
            float minRatio = 1f;
            bool anyLiving = false;
            for (int i = 0; i < 2; i++)
            {
                if (!Life.Down(i))
                {
                    anyLiving = true;
                    minRatio = Math.Min(minRatio, Life.Health[i] / Math.Max(1f, Life.Maximum[i]));
                }
            }
            return anyLiving ? minRatio : 0f;
        }

        private static bool OnUiDamaged()
        {
            if (!Active)
            {
                return true;
            }

            // During BeforeDamage..AfterDamage, compute predicted post-hit HP rather than stale pre-hit state.
            bool activeIsDown = Life.Down(_activeSlot);
            if (!activeIsDown && _currentDamageScope != null && _currentDamageScope.Owner == _activeSlot && BattleSaveData.I != null)
            {
                float predicted = _currentDamageScope.Before + (BattleSaveData.I.CurHealth - _currentDamageScope.GuardedHealth) + _currentDamageScope.NestedDelta;
                if (predicted <= 0f)
                {
                    activeIsDown = true;
                }
            }

            int other = 1 - _activeSlot;
            bool otherAliveAndHealthy = !Life.Down(other) && (Life.Health[other] / Math.Max(1f, Life.Maximum[other])) > 0.30f;
            if (activeIsDown && otherAliveAndHealthy)
            {
                ClearBloodOverlay();
                return false;
            }

            if (InGameUI.I != null && InGameUI.I.DamageOverlay != null)
            {
                InGameUI.I.DamageOverlay.enabled = true;
            }
            return true;
        }

        private static void ClearBloodOverlay()
        {
            if (InGameUI.I == null)
            {
                return;
            }
            try
            {
                Timing.KillCoroutines(InGameUI.I._damageAnim);
                if (InGameUI.I.DamageOverlay != null)
                {
                    Color c = InGameUI.I.DamageOverlay.color;
                    InGameUI.I.DamageOverlay.color = new Color(c.r, c.g, c.b, 0f);
                    InGameUI.I.DamageOverlay.enabled = false;
                }
            }
            catch { }
        }

        private static void UpdateBloodVignette()
        {
            if (!Active || Life.Defeated || InGameUI.I == null || InGameUI.I.DamageOverlay == null)
            {
                return;
            }
            if ((Life.Down(0) || Life.Down(1)) && MinLivingHpRatio() > 0.35f && Time.unscaledTime > _lastLivingHitTime + 0.55f)
            {
                if (InGameUI.I.DamageOverlay.enabled || InGameUI.I.DamageOverlay.color.a > 0.01f)
                {
                    ClearBloodOverlay();
                }
            }
        }

        private static void TickManualRescue()
        {
            if (!Active || GameMgr.I == null || GameMgr.I.CurState != GameState.kPlaying || CoopRoundStats.Complete)
            {
                ManualRescueInRing[0] = ManualRescueInRing[1] = false;
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                if (!Life.CanManualRevive(i))
                {
                    Life.RescueProgress[i] = 0f;
                    ManualRescueInRing[i] = false;
                    continue;
                }
                Player downed = CoopCombat.PlayerAt(i);
                Player savior = CoopCombat.PlayerAt(1 - i);
                if (downed == null || savior == null)
                {
                    ManualRescueInRing[i] = false;
                    continue;
                }
                if (!HasDownedPos[i])
                {
                    DownedPos[i] = CoopCombat.Pos(downed);
                    HasDownedPos[i] = true;
                }
                Vector2 dPos = new Vector2(DownedPos[i].x, DownedPos[i].y);
                Vector3 saviorNearest = CoopContext.GetClosestBodyPosition(savior, DownedPos[i]);
                Vector2 sPos = new Vector2(saviorNearest.x, saviorNearest.y);
                bool inRing = (dPos - sPos).sqrMagnitude <= ManualRescueRadius * ManualRescueRadius;
                ManualRescueInRing[i] = inRing;
                if (inRing)
                {
                    Life.RescueProgress[i] = Mathf.Clamp01(Life.RescueProgress[i] + Time.deltaTime / CoopLifeRules.ManualRescueDuration);
                    int saviorSlot = 1 - i;
                    var pad = GamepadRouter.GetPad(saviorSlot);
                    bool kbGoldKey = !GamepadRouter.UsesGamepadForSlot(saviorSlot) && UnityEngine.Input.GetKeyDown(KeyCode.E);
                    bool goldKey = kbGoldKey || GamepadRouter.ButtonJustPressed(pad?.y);
                    if (goldKey && TryPayGoldRevive(i))
                    {
                        return;
                    }
                    if (Life.RescueProgress[i] >= 1f)
                    {
                        ExecuteManualRevive(i);
                        return;
                    }
                }
                else
                {
                    Life.RescueProgress[i] = Mathf.Max(0f, Life.RescueProgress[i] - Time.deltaTime * 0.45f);
                }
            }
        }

        public static bool IsKeyboardGoldReviveEligible
        {
            get
            {
                if (!Active || GameMgr.I == null || GameMgr.I.CurState != GameState.kPlaying || CoopRoundStats.Complete)
                {
                    return false;
                }
                for (int i = 0; i < 2; i++)
                {
                    if (ManualRescueInRing[i] && Life.CanManualRevive(i) && !GamepadRouter.UsesGamepadForSlot(1 - i))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        private static void SyncVanillaReviveBudget()
        {
            if (BattleSaveData.I == null)
            {
                return;
            }
            int baseRevives = BuildingMgr.I != null ? Math.Max(0, BuildingMgr.I.NumRevives) : 0;
            if (baseRevives > 0 && Life.TotalRevivesRemaining > 0)
            {
                BattleSaveData.I.NumRevives = Math.Max(0, baseRevives - 1);
            }
            else
            {
                BattleSaveData.I.NumRevives = Math.Max(1, baseRevives);
            }
        }

        private static void RestoreAllDownedVisualsAndColliders()
        {
            foreach (var list in CachedRenderers.Values)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    Renderer r = list[i];
                    if (HarmonyHelper.IsUnityAlive(r) && RendererStates.TryGetValue(r.Pointer, out bool orig))
                    {
                        try { r.enabled = orig; } catch { }
                    }
                }
            }
            foreach (var list in CachedColliders.Values)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    Collider2D c = list[i];
                    if (HarmonyHelper.IsUnityAlive(c) && ColliderStates.TryGetValue(c.Pointer, out bool orig))
                    {
                        try { c.enabled = orig; } catch { }
                    }
                }
            }
            RendererStates.Clear();
            ColliderStates.Clear();
        }

        private static void UpdateDownedVisualsAndColliders()
        {
            if (!Life.Down(0) && !Life.Down(1) && RendererStates.Count == 0 && ColliderStates.Count == 0)
            {
                return;
            }
            float now = Time.unscaledTime;
            for (int i = 0; i < 2; i++)
            {
                bool down = Life.Down(i);
                if (!down && RendererStates.Count == 0 && ColliderStates.Count == 0)
                {
                    continue;
                }
                Player p = CoopCombat.PlayerAt(i);
                IntPtr pPtr = HarmonyHelper.Ptr(p);
                if (pPtr == IntPtr.Zero)
                {
                    continue;
                }

                bool refresh = !CachedRenderers.TryGetValue(pPtr, out var rList)
                    | !CachedColliders.TryGetValue(pPtr, out var cList)
                    || (down && now >= NextVisualRefreshAt[i]);

                if (refresh)
                {
                    if (rList == null)
                    {
                        rList = new List<Renderer>(16);
                        CachedRenderers[pPtr] = rList;
                    }
                    else
                    {
                        rList.Clear();
                    }
                    Il2CppReferenceArray<Component> renderers = p.GetComponentsInChildren(Il2CppType.Of<Renderer>(), includeInactive: true);
                    for (int j = 0; j < renderers.Length; j++)
                    {
                        Renderer r = renderers[j]?.TryCast<Renderer>();
                        if (HarmonyHelper.IsUnityAlive(r)) rList.Add(r);
                    }

                    if (cList == null)
                    {
                        cList = new List<Collider2D>(8);
                        CachedColliders[pPtr] = cList;
                    }
                    else
                    {
                        cList.Clear();
                    }
                    Il2CppReferenceArray<Component> cols = p.GetComponentsInChildren(Il2CppType.Of<Collider2D>(), includeInactive: true);
                    for (int j = 0; j < cols.Length; j++)
                    {
                        Collider2D c = cols[j]?.TryCast<Collider2D>();
                        if (HarmonyHelper.IsUnityAlive(c)) cList.Add(c);
                    }
                    NextVisualRefreshAt[i] = now + 0.5f;
                }

                for (int j = 0; j < rList.Count; j++)
                {
                    Renderer r = rList[j];
                    if (!HarmonyHelper.IsUnityAlive(r)) continue;
                    IntPtr rPtr = r.Pointer;
                    if (down)
                    {
                        if (!RendererStates.ContainsKey(rPtr)) RendererStates[rPtr] = r.enabled;
                        r.enabled = false;
                    }
                    else if (RendererStates.TryGetValue(rPtr, out bool orig))
                    {
                        r.enabled = orig;
                        RendererStates.Remove(rPtr);
                    }
                }

                for (int j = 0; j < cList.Count; j++)
                {
                    Collider2D c = cList[j];
                    if (!HarmonyHelper.IsUnityAlive(c)) continue;
                    IntPtr cPtr = c.Pointer;
                    if (down)
                    {
                        if (!ColliderStates.ContainsKey(cPtr)) ColliderStates[cPtr] = c.enabled;
                        c.enabled = false;
                    }
                    else if (ColliderStates.TryGetValue(cPtr, out bool orig))
                    {
                        c.enabled = orig;
                        ColliderStates.Remove(cPtr);
                    }
                }
            }
        }

        public static void Tick()
        {
            using (HangWatchdog.Enter(HangWatchdog.PhaseId.VitalsTick))
            {
                if (!CoopMode.Enabled || !HarmonyHelper.IsInGameScene() || GameMgr.I == null
                    || !CoopCharacterRuntime.IndependentReady || UpgradeMgr.I == null || BattleSaveData.I == null)
                {
                    return;
                }

                if (!Ready)
                {
                    CoopCharacterRuntime.InvalidateStats();
                    float p1Max = CoopCharacterRuntime.GetMaxHealth(0);
                    float p2Max = CoopCharacterRuntime.GetMaxHealth(1);
                    int baseRevives = BuildingMgr.I != null ? Math.Max(0, BuildingMgr.I.NumRevives) : 0;
                    float revivePct = BuildingMgr.I != null && BuildingMgr.I.ReviveHealthPct > 0.05f ? BuildingMgr.I.ReviveHealthPct : 0.5f;
                    Life.Reset(p1Max, p2Max, baseRevives, baseRevives, revivePct);
                    _activeSlot = 0;
                    Ready = true;
                    SyncVanillaReviveBudget();
                    WriteBackingHealth();
                }

                if (GameMgr.I.CurState == GameState.kPlaying)
                {
                    float p1Max = CoopCharacterRuntime.GetMaxHealth(0);
                    float p2Max = CoopCharacterRuntime.GetMaxHealth(1);
                    if (p1Max > 0f) Life.SetMaximum(0, p1Max);
                    if (p2Max > 0f) Life.SetMaximum(1, p2Max);
                    WriteBackingHealth();
                }

                SyncVanillaReviveBudget();
                TickManualRescue();
                UpdateDownedVisualsAndColliders();
                UpdateBloodVignette();

                if (Life.Defeated && !CoopRoundStats.Complete && GameMgr.I.CurState == GameState.kPlaying)
                {
                    StopShots();
                    SyncVanillaReviveBudget();
                    if (Life.TotalRevivesRemaining > 0 && !CoopRevivePrompt.Pending)
                    {
                        BattleSaveData.I.CurHealth = 0f;
                        if (ReviveUI.I != null)
                        {
                            GameMgr.I.SetState(GameState.kRevive);
                        }
                        else
                        {
                            // Even if vanilla ReviveUI isn't wired yet, open our custom team revive prompt directly.
                            CoopRevivePrompt.OpenDirect();
                        }
                        return;
                    }
                    if (Life.TotalRevivesRemaining > 0)
                    {
                        return;
                    }

                    _allowDefeat = true;
                    BattleSaveData.I.CurHealth = 0f;
                    try
                    {
                        GameMgr.I.LoseGame();
                    }
                    finally
                    {
                        _allowDefeat = false;
                    }
                }
            }
        }
    }
}
