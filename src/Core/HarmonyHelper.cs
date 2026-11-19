using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace BALLxPITLocalCoop.Core
{
    /// <summary>
    /// Common lifecycle contract for all co-op subsystems so load, round reset, and unload stay symmetric.
    /// </summary>
    internal interface ICoopModule
    {
        string Name { get; }
        void Install(Harmony harmony);
        void ResetRound();
        void Shutdown();
    }

    /// <summary>
    /// Safe Harmony patching and IL2CPP/Unity object utilities shared across modules.
    /// </summary>
    internal static class HarmonyHelper
    {
        public static bool TryPatch(
            Harmony harmony,
            MethodBase target,
            MethodInfo prefix = null,
            MethodInfo postfix = null,
            MethodInfo finalizer = null,
            int prefixPriority = -1,
            int postfixPriority = -1,
            int finalizerPriority = -1,
            string label = null)
        {
            if (harmony == null)
            {
                return false;
            }
            if (target == null)
            {
                Plugin.Logger?.LogWarning($"Harmony target missing: {label ?? "<unnamed>"}");
                return false;
            }
            try
            {
                HarmonyMethod pre = prefix != null ? new HarmonyMethod(prefix) : null;
                HarmonyMethod post = postfix != null ? new HarmonyMethod(postfix) : null;
                HarmonyMethod fin = finalizer != null ? new HarmonyMethod(finalizer) : null;
                if (pre != null && prefixPriority >= 0) pre.priority = prefixPriority;
                if (post != null && postfixPriority >= 0) post.priority = postfixPriority;
                if (fin != null && finalizerPriority >= 0) fin.priority = finalizerPriority;
                harmony.Patch(target, prefix: pre, postfix: post, finalizer: fin);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"Failed to patch {label ?? target.Name}: {ex.Message}");
                return false;
            }
        }

        public static bool TryPatch(
            Harmony harmony,
            MethodBase target,
            HarmonyMethod prefix = null,
            HarmonyMethod postfix = null,
            HarmonyMethod finalizer = null,
            string label = null)
        {
            if (harmony == null)
            {
                return false;
            }
            if (target == null)
            {
                Plugin.Logger?.LogWarning($"Harmony target missing: {label ?? "<unnamed>"}");
                return false;
            }
            try
            {
                harmony.Patch(target, prefix: prefix, postfix: postfix, finalizer: finalizer);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"Failed to patch {label ?? target.Name}: {ex.Message}");
                return false;
            }
        }

        public static Type FindNestedCoroutineType(Type owner, string methodNamePart, string fallbackFullName = null)
        {
            if (owner == null)
            {
                return null;
            }
            try
            {
                var matches = new List<Type>();
                foreach (Type candidate in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (candidate.Name.IndexOf(methodNamePart, StringComparison.OrdinalIgnoreCase) >= 0
                        && candidate.Name.IndexOf("_d__", StringComparison.Ordinal) >= 0)
                    {
                        matches.Add(candidate);
                    }
                }
                if (matches.Count > 1)
                {
                    Plugin.Logger?.LogWarning($"Multiple nested coroutine types matched '{methodNamePart}' on {owner.Name}; using '{matches[0].Name}'.");
                }
                if (matches.Count > 0)
                {
                    return matches[0];
                }
                if (!string.IsNullOrEmpty(fallbackFullName))
                {
                    return owner.Assembly.GetType(fallbackFullName);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"Failed resolving nested coroutine '{methodNamePart}' on {owner.Name}: {ex.Message}");
            }
            return null;
        }

        public static IntPtr Ptr(object obj) => obj is Il2CppObjectBase b ? b.Pointer : IntPtr.Zero;

        public static bool SameNative(object left, object right)
        {
            if (left == null || right == null)
            {
                return false;
            }
            if (left is Il2CppObjectBase l && right is Il2CppObjectBase r)
            {
                return l.Pointer != IntPtr.Zero && l.Pointer == r.Pointer;
            }
            return ReferenceEquals(left, right);
        }

        /// <summary>
        /// Safely tests whether a UnityEngine.Object (even when boxed as System.Object) is alive in native memory.
        /// </summary>
        public static bool IsUnityAlive(object obj)
        {
            if (obj == null)
            {
                return false;
            }
            if (obj is UnityEngine.Object uo)
            {
                return uo != null && uo.Pointer != IntPtr.Zero;
            }
            if (obj is Il2CppObjectBase b)
            {
                UnityEngine.Object cast = b.TryCast<UnityEngine.Object>();
                return cast != null && cast.Pointer != IntPtr.Zero;
            }
            return true;
        }

        public static bool IsInGameScene()
        {
            try
            {
                return SceneMgr.I != null && SceneMgr.GetCurScene() == GameScene.kGame;
            }
            catch
            {
                return false;
            }
        }

        public static GameScene SafeGetCurScene()
        {
            try
            {
                return SceneMgr.I != null ? SceneMgr.GetCurScene() : GameScene.kMainMenu;
            }
            catch
            {
                return GameScene.kMainMenu;
            }
        }

        public static void SafeInvoke(string label, Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"[{label}] {ex.Message}");
            }
        }

        public static void SafeInvoke(Action action, string label = null)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"[{label ?? "SafeInvoke"}] {ex.Message}");
            }
        }
    }
}
