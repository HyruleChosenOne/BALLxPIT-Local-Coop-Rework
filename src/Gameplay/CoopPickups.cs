using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.PlayerTwo;
using BALLxPITLocalCoop.UI;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace BALLxPITLocalCoop.Gameplay
{
    /// <summary>
    /// Attributes world pickups (XP gems, gold, healing) and magnet animations to whichever living player collected them.
    /// Uses lightweight PositionOnly context during flight (zero UpgradeMgr/BallMgr churn) and Full context only on CompletePickup,
    /// with compiled expression property getters (zero per-frame reflection).
    /// </summary>
    public static class CoopPickups
    {
        private struct PickupScope
        {
            public CoopContext.ScopeToken Context;
            public IntPtr PickupPtr;
            public bool Complete;
            public bool Active;
        }

        private static readonly Dictionary<IntPtr, int> Owners = new Dictionary<IntPtr, int>(128);
        private static readonly HashSet<IntPtr> Consumed = new HashSet<IntPtr>(128);
        private static readonly Dictionary<Type, Func<object, PickupObj>> CompiledGetters = new Dictionary<Type, Func<object, PickupObj>>(4);
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
                AccessTools.Method(typeof(PickupObj), nameof(PickupObj.Init)),
                postfix: new HarmonyMethod(typeof(CoopPickups), nameof(OnInit)),
                label: "PickupObj.Init");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(PickupObj), nameof(PickupObj.StartPlayerPickUp)),
                prefix: new HarmonyMethod(typeof(CoopPickups), nameof(OnStartPickup)),
                label: "PickupObj.StartPlayerPickUp");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(PickupObj), nameof(PickupObj.CompletePickup)),
                prefix: new HarmonyMethod(typeof(CoopPickups), nameof(OnCompletePickup)),
                finalizer: new HarmonyMethod(typeof(CoopPickups), nameof(OnFinishScope)),
                label: "PickupObj.CompletePickup");

            Type animNested = HarmonyHelper.FindNestedCoroutineType(typeof(PickupObj), "AnimatePlayerPickUp");
            if (animNested != null)
            {
                MethodInfo moveNext = AccessTools.Method(animNested, "MoveNext");
                HarmonyHelper.TryPatch(
                    harmony,
                    moveNext,
                    prefix: new HarmonyMethod(typeof(CoopPickups), nameof(OnAnimateStep)),
                    postfix: new HarmonyMethod(typeof(CoopPickups), nameof(OnAnimateStepPostfix)),
                    finalizer: new HarmonyMethod(typeof(CoopPickups), nameof(OnFinishScope)),
                    label: "PickupObj.AnimatePlayerPickUp.MoveNext");
            }
        }

        public static void ResetRound()
        {
            Owners.Clear();
            Consumed.Clear();
        }

        public static void Shutdown()
        {
            ResetRound();
            CompiledGetters.Clear();
            _installed = false;
        }

        private static PickupObj ResolvePickupFromIterator(object iter)
        {
            if (iter == null) return null;
            Type iterType = iter.GetType();
            if (!CompiledGetters.TryGetValue(iterType, out var getter))
            {
                getter = BuildPickupGetter(iterType);
                CompiledGetters[iterType] = getter;
            }
            return getter?.Invoke(iter);
        }

        private static Func<object, PickupObj> BuildPickupGetter(Type iterType)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo prop = iterType.GetProperty("__4__this", flags);
                if (prop == null)
                {
                    foreach (PropertyInfo p in iterType.GetProperties(flags))
                    {
                        if (p.PropertyType == typeof(PickupObj))
                        {
                            prop = p;
                            break;
                        }
                    }
                }
                if (prop != null && prop.CanRead)
                {
                    ParameterExpression param = Expression.Parameter(typeof(object), "inst");
                    Expression cast = Expression.Convert(param, iterType);
                    Expression access = Expression.Property(cast, prop);
                    Expression result = prop.PropertyType == typeof(PickupObj)
                        ? access
                        : Expression.TypeAs(access, typeof(PickupObj));
                    return Expression.Lambda<Func<object, PickupObj>>(result, param).Compile();
                }

                FieldInfo field = iterType.GetField("__4__this", flags);
                if (field != null)
                {
                    ParameterExpression param = Expression.Parameter(typeof(object), "inst");
                    Expression cast = Expression.Convert(param, iterType);
                    Expression access = Expression.Field(cast, field);
                    Expression result = field.FieldType == typeof(PickupObj)
                        ? access
                        : Expression.TypeAs(access, typeof(PickupObj));
                    return Expression.Lambda<Func<object, PickupObj>>(result, param).Compile();
                }
            }
            catch { }
            return null;
        }

        private static void OnInit(PickupObj __instance)
        {
            IntPtr ptr = HarmonyHelper.Ptr(__instance);
            if (ptr != IntPtr.Zero)
            {
                Owners.Remove(ptr);
                Consumed.Remove(ptr);
            }
        }

        private static bool OnStartPickup(PickupObj __instance)
        {
            if (!CoopVitals.Active || __instance == null)
            {
                return true;
            }
            IntPtr ptr = HarmonyHelper.Ptr(__instance);
            if (ptr == IntPtr.Zero)
            {
                return true;
            }
            if (Owners.ContainsKey(ptr) || Consumed.Contains(ptr))
            {
                return false;
            }
            int owner = CoopCombat.SimulationOwner >= 0 ? CoopCombat.SimulationOwner : CoopVitals.Owner(Player.I);
            if (CoopVitals.Down(owner))
            {
                Player nearest = CoopCombat.NearestLiving(__instance.transform.position);
                if (nearest == null)
                {
                    return false;
                }
                owner = CoopVitals.Owner(nearest);
                if (CoopVitals.Down(owner))
                {
                    return false;
                }
            }
            Owners[ptr] = owner;
            return true;
        }

        private static bool OnAnimateStep(object __instance, ref bool __result, out PickupScope __state)
        {
            __state = default;
            if (!CoopMode.Enabled)
            {
                return true;
            }
            if (!HarmonyHelper.IsInGameScene() || CoopRoundStats.Complete)
            {
                __result = false;
                return false;
            }

            PickupObj pickup = ResolvePickupFromIterator(__instance);
            IntPtr ptr = HarmonyHelper.Ptr(pickup);
            if (!HarmonyHelper.IsUnityAlive(pickup) || !pickup.gameObject.activeInHierarchy || Player.I == null)
            {
                if (ptr != IntPtr.Zero) Owners.Remove(ptr);
                __result = false;
                return false;
            }
            if (!CoopVitals.Active || !Owners.TryGetValue(ptr, out int owner))
            {
                return true;
            }

            // If the original owner went down mid-flight, re-route to the living teammate if available.
            if (CoopVitals.Down(owner))
            {
                int other = 1 - owner;
                Player livingOther = CoopCombat.PlayerAt(other);
                if (livingOther != null && !CoopVitals.Down(other))
                {
                    owner = other;
                    Owners[ptr] = owner;
                }
                else
                {
                    Owners.Remove(ptr);
                    __result = false;
                    return false;
                }
            }

            Player target = CoopCombat.PlayerAt(owner);
            if (target == null)
            {
                Owners.Remove(ptr);
                __result = false;
                return false;
            }

            // Lightweight PositionOnly scope during magnet flight: only Player.I is swapped (zero BallMgr/UpgradeMgr churn!).
            __state = new PickupScope
            {
                PickupPtr = ptr,
                Complete = false,
                Active = true,
                Context = owner == 1
                    ? CoopContext.EnterPlayerTwo(target, CoopContext.ScopeKind.PositionOnly)
                    : CoopContext.EnterPlayerOne(CoopContext.ScopeKind.PositionOnly)
            };
            return true;
        }

        private static void OnAnimateStepPostfix(ref PickupScope __state, bool __result)
        {
            if (__state.Active && !__result && __state.PickupPtr != IntPtr.Zero)
            {
                Owners.Remove(__state.PickupPtr);
            }
        }

        private static bool OnCompletePickup(PickupObj __instance, out PickupScope __state)
        {
            __state = default;
            if (!CoopVitals.Active || __instance == null)
            {
                return true;
            }
            IntPtr ptr = HarmonyHelper.Ptr(__instance);
            if (ptr == IntPtr.Zero)
            {
                return true;
            }
            if (!Consumed.Add(ptr))
            {
                Owners.Remove(ptr);
                return false;
            }
            if (!Owners.TryGetValue(ptr, out int owner) || CoopVitals.Down(owner))
            {
                Player nearest = CoopCombat.NearestLiving(__instance.transform.position);
                owner = nearest != null ? CoopVitals.Owner(nearest) : 0;
            }

            CoopRoundStats.RecordPickup(owner);
            Player target = CoopCombat.PlayerAt(owner);

            // Full scope only at completion so heal/XP/passive triggers execute under the collector's UpgradeMgr/Passives.
            __state = new PickupScope
            {
                PickupPtr = ptr,
                Complete = true,
                Active = true,
                Context = (owner == 1 && target != null)
                    ? CoopContext.EnterPlayerTwo(target, CoopContext.ScopeKind.Full)
                    : CoopContext.EnterPlayerOne(CoopContext.ScopeKind.Full)
            };
            return true;
        }

        private static void OnFinishScope(ref PickupScope __state)
        {
            if (!__state.Active)
            {
                return;
            }
            try
            {
                CoopContext.Exit(__state.Context);
            }
            finally
            {
                if (__state.Complete && __state.PickupPtr != IntPtr.Zero)
                {
                    Owners.Remove(__state.PickupPtr);
                }
                __state.Active = false;
            }
        }
    }
}
