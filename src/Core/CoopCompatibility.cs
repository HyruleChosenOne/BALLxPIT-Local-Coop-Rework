using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.PlayerTwo;
using BALLxPITLocalCoop.UI;
using HarmonyLib;
using UnityEngine;

namespace BALLxPITLocalCoop.Core
{
    /// <summary>
    /// Compatibility guards for city-grid queries and UI/shield coroutines during co-op.
    /// Uses compiled property accessors (zero per-frame reflection) and proper UnityEngine.Object liveness checks.
    /// </summary>
    public static class CoopCompatibility
    {
        private static readonly Dictionary<Type, Func<object, object>> CachedGrpGetters = new Dictionary<Type, Func<object, object>>();
        private static readonly Dictionary<Type, Func<object, object>> CachedThisGetters = new Dictionary<Type, Func<object, object>>();
        private static bool _installed;

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            int ok = 0;
            // BaseGridMgr.FindEmptySpots() returns List<Vector2Int>; supply an empty list when suppressed in battle.
            if (HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(BaseGridMgr), nameof(BaseGridMgr.FindEmptySpots), Type.EmptyTypes),
                prefix: typeof(CoopCompatibility).GetMethod(nameof(GuardFindEmptySpots), BindingFlags.Static | BindingFlags.NonPublic),
                label: "BaseGridMgr.FindEmptySpots"))
            {
                ok++;
            }

            if (PatchNestedMoveNext(harmony, typeof(UIUtl), "FadeGroup", nameof(FadeAlive))) ok++;
            if (PatchNestedMoveNext(harmony, typeof(InGameUI), "RunLevelCompleteBonus", nameof(BonusAlive))) ok++;
            if (PatchNestedMoveNext(harmony, typeof(Player), "RunShieldBounce", nameof(BounceAlive))) ok++;
            if (PatchNestedMoveNext(harmony, typeof(RadialVFX), "MyUpdate", nameof(ShieldAlive), nameof(ShieldEnd))) ok++;

            Plugin.Logger?.LogInfo($"CoopCompatibility installed ({ok}/5 hooks active).");
        }

        public static void Shutdown()
        {
            CachedGrpGetters.Clear();
            CachedThisGetters.Clear();
            _installed = false;
        }

        private static bool PatchNestedMoveNext(Harmony harmony, Type owner, string nestedName, string prefixMethod, string finalizerMethod = null)
        {
            Type nested = HarmonyHelper.FindNestedCoroutineType(owner, nestedName);
            MethodInfo moveNext = nested != null ? AccessTools.Method(nested, "MoveNext", Type.EmptyTypes) : null;
            return HarmonyHelper.TryPatch(
                harmony,
                moveNext,
                prefix: typeof(CoopCompatibility).GetMethod(prefixMethod, BindingFlags.Static | BindingFlags.NonPublic),
                finalizer: finalizerMethod != null ? typeof(CoopCompatibility).GetMethod(finalizerMethod, BindingFlags.Static | BindingFlags.NonPublic) : null,
                label: $"{owner.Name}.{nestedName}.MoveNext");
        }

        private static bool GuardFindEmptySpots(ref Il2CppSystem.Collections.Generic.List<Vector2Int> __result)
        {
            if (!HarmonyHelper.IsInGameScene())
            {
                return true;
            }
            __result = new Il2CppSystem.Collections.Generic.List<Vector2Int>();
            return false;
        }

        private static bool FadeAlive(object __instance, ref bool __result)
        {
            if (!CoopMode.Enabled)
            {
                return true;
            }
            object grp = ReadCompiledMember(__instance, "grp", CachedGrpGetters);
            if (HarmonyHelper.IsUnityAlive(grp))
            {
                return true;
            }
            __result = false;
            return false;
        }

        private static bool BonusAlive(object __instance, ref bool __result)
        {
            if (!CoopMode.Enabled)
            {
                return true;
            }
            object host = ReadCompiledMember(__instance, "__4__this", CachedThisGetters);
            if (HarmonyHelper.IsUnityAlive(host))
            {
                return true;
            }
            __result = false;
            return false;
        }

        private static bool BounceAlive(object __instance, ref bool __result)
        {
            if (!CoopMode.Enabled)
            {
                return true;
            }
            object raw = ReadCompiledMember(__instance, "__4__this", CachedThisGetters);
            if (!HarmonyHelper.IsUnityAlive(raw) || !HarmonyHelper.IsInGameScene() || CoopRoundStats.Complete)
            {
                __result = false;
                return false;
            }
            return true;
        }

        private static bool ShieldAlive(object __instance, ref bool __result, out CoopContext.ScopeToken __state)
        {
            __state = default;
            if (!CoopMode.Enabled)
            {
                return true;
            }
            object raw = ReadCompiledMember(__instance, "__4__this", CachedThisGetters);
            RadialVFX vfx = raw as RadialVFX ?? (raw as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<RadialVFX>();
            if (vfx == null)
            {
                __result = false;
                return false;
            }
            if (vfx.Type != RadialVFXType.kPlayerShield)
            {
                return true;
            }
            Player owner = ResolveRadialShieldOwner(vfx);
            if (owner == null || !HarmonyHelper.IsInGameScene() || CoopRoundStats.Complete)
            {
                __result = false;
                return false;
            }

            if (CoopContext.IsPlayerTwo(owner))
            {
                __state = CoopContext.EnterPlayerTwo(owner, CoopContext.ScopeKind.PositionOnly);
            }
            else if (CoopContext.IsPlayerTwoContext)
            {
                __state = CoopContext.EnterPlayerOne(CoopContext.ScopeKind.PositionOnly);
            }
            return true;
        }

        private static void ShieldEnd(CoopContext.ScopeToken __state)
        {
            CoopContext.Exit(__state);
        }

        private static Player ResolveRadialShieldOwner(RadialVFX vfx)
        {
            if (vfx == null)
            {
                return null;
            }
            Player p1 = CoopContext.OriginalPlayer;
            Player p2 = CoopContext.PlayerTwo;

            Transform tgt = vfx._tgt;
            if (tgt != null)
            {
                Transform p2Xf = p2 != null ? p2.transform : null;
                if (p2Xf != null && (tgt.Pointer == p2Xf.Pointer || tgt.IsChildOf(p2Xf)))
                {
                    return p2;
                }
                Transform p1Xf = p1 != null ? p1.transform : null;
                if (p1Xf != null && (tgt.Pointer == p1Xf.Pointer || tgt.IsChildOf(p1Xf)))
                {
                    return p1;
                }
            }

            PassiveInst passive = vfx._tgtPassive;
            if (passive != null)
            {
                if (CoopCharacterRuntime.IsPassiveInSlot(1, passive)) return p2;
                if (CoopCharacterRuntime.IsPassiveInSlot(0, passive)) return p1;
            }

            // Do not blindly re-attach an orphaned shield whose target was destroyed.
            return null;
        }

        private static object ReadCompiledMember(object instance, string memberName, Dictionary<Type, Func<object, object>> cache)
        {
            if (instance == null)
            {
                return null;
            }
            Type type = instance.GetType();
            if (!cache.TryGetValue(type, out Func<object, object> getter))
            {
                getter = BuildGetter(type, memberName);
                cache[type] = getter;
            }
            return getter != null ? getter(instance) : null;
        }

        private static Func<object, object> BuildGetter(Type type, string memberName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo prop = type.GetProperty(memberName, flags);
            FieldInfo field = prop == null ? type.GetField(memberName, flags) : null;
            if (prop == null && field == null)
            {
                return null;
            }
            try
            {
                ParameterExpression param = Expression.Parameter(typeof(object), "inst");
                UnaryExpression cast = Expression.Convert(param, type);
                MemberExpression access = prop != null ? Expression.Property(cast, prop) : Expression.Field(cast, field);
                UnaryExpression box = Expression.Convert(access, typeof(object));
                return Expression.Lambda<Func<object, object>>(box, param).Compile();
            }
            catch
            {
                if (prop != null) return inst => prop.GetValue(inst);
                return inst => field.GetValue(inst);
            }
        }
    }
}
