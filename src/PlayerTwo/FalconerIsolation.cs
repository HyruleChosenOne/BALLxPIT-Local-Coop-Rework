using System;
using System.Reflection;
using BALLxPITLocalCoop.Core;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace BALLxPITLocalCoop.PlayerTwo
{
    /// <summary>
    /// Isolates PlayerCharControllerFalconer.I between P1 and P2.
    /// Caches search completion flags so non-Falconer runs perform ZERO GetComponentsInChildren calls per frame,
    /// and properly resets .I to null when P1 is not Falconer and P2 is Falconer.
    /// </summary>
    internal static class FalconerIsolation
    {
        private static PlayerCharControllerFalconer _p1Falconer;
        private static PlayerCharControllerFalconer _p2Falconer;
        private static bool _p1Resolved;
        private static bool _p2Resolved;
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
                AccessTools.Method(typeof(PlayerCharControllerFalconer), nameof(PlayerCharControllerFalconer.Init), Type.EmptyTypes),
                postfix: typeof(FalconerIsolation).GetMethod(nameof(FalconerInitPostfix), BindingFlags.Static | BindingFlags.NonPublic),
                label: "PlayerCharControllerFalconer.Init");
        }

        public static void Shutdown()
        {
            Reset();
            _installed = false;
        }

        public static void BeforeClone(Player playerOne)
        {
            _p1Falconer = FindFalconer(playerOne) ?? PlayerCharControllerFalconer.I;
            _p1Resolved = playerOne != null;
            _p2Falconer = null;
            _p2Resolved = false;
        }

        public static void AfterClone(Player playerOne, Player playerTwo)
        {
            RefreshControllers(playerOne, playerTwo);
            ApplyForSlot(0);
        }

        public static void RefreshControllers(Player playerOne, Player playerTwo)
        {
            _p1Falconer = FindFalconer(playerOne);
            _p2Falconer = FindFalconer(playerTwo);
            _p1Resolved = playerOne != null;
            _p2Resolved = playerTwo != null;
        }

        public static void ApplyForSlot(int slot)
        {
            if (slot == 1)
            {
                if (!_p2Resolved && CoopContext.PlayerTwo != null)
                {
                    _p2Falconer = FindFalconer(CoopContext.PlayerTwo);
                    _p2Resolved = true;
                }
                if (!HarmonyHelper.SameNative(PlayerCharControllerFalconer.I, _p2Falconer))
                {
                    PlayerCharControllerFalconer.I = _p2Falconer;
                }
            }
            else
            {
                if (!_p1Resolved && CoopContext.OriginalPlayer != null)
                {
                    _p1Falconer = FindFalconer(CoopContext.OriginalPlayer);
                    _p1Resolved = true;
                }
                // Always write _p1Falconer (including null!) so P2's Falconer never leaks into P1.
                if (_p1Falconer == null)
                {
                    if (PlayerCharControllerFalconer.I != null)
                    {
                        PlayerCharControllerFalconer.I = null;
                    }
                }
                else if (!HarmonyHelper.SameNative(PlayerCharControllerFalconer.I, _p1Falconer))
                {
                    PlayerCharControllerFalconer.I = _p1Falconer;
                }
            }
        }

        public static void Reset()
        {
            _p1Falconer = null;
            _p2Falconer = null;
            _p1Resolved = false;
            _p2Resolved = false;
        }

        private static void FalconerInitPostfix(PlayerCharControllerFalconer __instance)
        {
            if (__instance == null)
            {
                return;
            }
            Player owner = __instance.GetComponentInParent<Player>();
            if (owner != null && CoopContext.IsPlayerTwo(owner))
            {
                _p2Falconer = __instance;
                _p2Resolved = true;
                if (!CoopContext.IsFullPlayerTwoContext)
                {
                    ApplyForSlot(0);
                }
                return;
            }
            if (owner != null && (HarmonyHelper.SameNative(owner, CoopContext.OriginalPlayer) || !CoopContext.IsCreatingPlayerTwo))
            {
                _p1Falconer = __instance;
                _p1Resolved = true;
            }
        }

        private static PlayerCharControllerFalconer FindFalconer(Player player)
        {
            if (player == null)
            {
                return null;
            }
            try
            {
                Il2CppArrayBase<PlayerCharControllerFalconer> list = player.GetComponentsInChildren<PlayerCharControllerFalconer>(true);
                if (list != null)
                {
                    for (int i = 0; i < list.Length; i++)
                    {
                        PlayerCharControllerFalconer fc = list[i];
                        if (fc != null && fc.gameObject != null && fc.gameObject.activeInHierarchy)
                        {
                            return fc;
                        }
                    }
                    if (list.Length > 0 && list[0] != null)
                    {
                        return list[0];
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
