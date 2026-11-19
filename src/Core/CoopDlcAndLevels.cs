using System;
using System.Collections.Generic;
using BALLxPITLocalCoop.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BALLxPITLocalCoop.Core
{
    /// <summary>
    /// Cached DLC catalog/ownership checks, stage list population (ordered levels only),
    /// NG+ / Speed Tier / Game Speed helpers, and character/stage unlock queries.
    /// </summary>
    public static class CoopDlcAndLevels
    {
        public static List<LevelInfo> Phases { get; } = new List<LevelInfo>(16);
        public static int SelectedNGPlus { get; set; }
        public static int SelectedSpeedTier { get; set; } = 1;

        private static bool _dlcCacheValid;
        private static bool _cachedCanAccessDlc;
        private static bool _levelsPopulated;
        private static bool _lastLevelsDlc;

        public static void InvalidateCache()
        {
            _dlcCacheValid = false;
            _levelsPopulated = false;
        }

        private static bool HasAnyDlcInCatalog()
        {
            try
            {
                if (InfoDB.I == null)
                {
                    return true;
                }
                if (InfoDB.I.Chars != null)
                {
                    foreach (CharInfo ci in InfoDB.I.Chars)
                    {
                        if (ci != null && ci.DLCVersion > 0) return true;
                    }
                }
                if (InfoDB.I.Levels != null)
                {
                    foreach (LevelInfo li in InfoDB.I.Levels)
                    {
                        if (li != null && SafeIsDLCLevel(li.Type)) return true;
                    }
                }
                var dlcOrder = SafeGetLevelOrderDLC();
                return dlcOrder != null && dlcOrder.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool SafeCanAccessBallbylonDLC(bool forceRefresh = false)
        {
            if (_dlcCacheValid && !forceRefresh)
            {
                return _cachedCanAccessDlc;
            }

            bool result = false;
            if (HasAnyDlcInCatalog())
            {
                try
                {
                    if (Toybox.Port.PlatformDLCMgr.OwnsDLCNoDebug(DLCId.kBallbylonRise)) result = true;
                }
                catch { }

                if (!result)
                {
                    try
                    {
                        if (Toybox.Port.PlatformDLCMgr.OwnsDLC(DLCId.kBallbylonRise)) result = true;
                    }
                    catch { }
                }

                if (!result)
                {
                    try
                    {
                        if (MetaSaveData.I != null && DLCUtl.CanAccessDLC(DLCId.kBallbylonRise)) result = true;
                    }
                    catch { }
                }
            }

            // Only freeze the cache once InfoDB or MetaSaveData is loaded.
            if (InfoDB.I != null || MetaSaveData.I != null)
            {
                _cachedCanAccessDlc = result;
                _dlcCacheValid = true;
            }
            return result;
        }

        public static bool SafeIsDLCLevel(LevelType levelType)
        {
            try { return DLCUtl.IsDLC(levelType); }
            catch { return false; }
        }

        public static int SafeGetCharDLCVersion(CharInfo info)
        {
            if (info == null) return 0;
            try { return info.DLCVersion; }
            catch { return 0; }
        }

        public static bool SafeCharIncludeInGame(CharInfo info)
        {
            if (info == null || !info.IsInGame) return false;
            try { return info.IncludeInGame(); }
            catch { return info.IsInGame; }
        }

        public static Il2CppStructArray<LevelType> SafeGetLevelOrder()
        {
            try { return LevelMgr.kLevelOrder; }
            catch { return null; }
        }

        public static Il2CppStructArray<LevelType> SafeGetLevelOrderDLC()
        {
            try { return LevelMgr.kLevelOrderDLC; }
            catch { return null; }
        }

        public static void PopulateLevels(bool force = false)
        {
            bool canAccessDlc = SafeCanAccessBallbylonDLC(forceRefresh: force);
            if (!force && _levelsPopulated && Phases.Count > 0 && _lastLevelsDlc == canAccessDlc)
            {
                return;
            }
            _levelsPopulated = true;
            _lastLevelsDlc = canAccessDlc;
            Phases.Clear();

            var byType = new Dictionary<int, LevelInfo>(16);
            try
            {
                if (InfoDB.I?.Levels != null)
                {
                    foreach (LevelInfo info in InfoDB.I.Levels)
                    {
                        if (info != null && !byType.ContainsKey((int)info.Type))
                        {
                            byType[(int)info.Type] = info;
                        }
                    }
                }
            }
            catch { }

            try
            {
                var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<LevelInfo>());
                if (found != null)
                {
                    foreach (UnityEngine.Object obj in found)
                    {
                        LevelInfo info = obj?.TryCast<LevelInfo>();
                        if (info != null && !byType.ContainsKey((int)info.Type))
                        {
                            byType[(int)info.Type] = info;
                        }
                    }
                }
            }
            catch { }

            var seen = new HashSet<int>();
            AddOrderedLevels(byType, SafeGetLevelOrder(), seen, canAccessDlc);
            if (canAccessDlc)
            {
                AddOrderedLevels(byType, SafeGetLevelOrderDLC(), seen, canAccessDlc);
            }

            // Only fall back to unordered InfoDB levels if both ordered arrays yielded nothing (avoids debug/test levels!).
            if (Phases.Count == 0)
            {
                foreach (var kv in byType)
                {
                    LevelInfo info = kv.Value;
                    if (info == null || seen.Contains(kv.Key)) continue;
                    if (!SafeIsDLCLevel(info.Type) || canAccessDlc)
                    {
                        seen.Add(kv.Key);
                        Phases.Add(info);
                    }
                }
            }
        }

        private static void AddOrderedLevels(
            Dictionary<int, LevelInfo> byType,
            Il2CppStructArray<LevelType> order,
            HashSet<int> seen,
            bool canAccessDlc)
        {
            if (order == null) return;
            for (int i = 0; i < order.Length; i++)
            {
                LevelType lt = order[i];
                if (!canAccessDlc && SafeIsDLCLevel(lt)) continue;
                int key = (int)lt;
                if (seen.Contains(key)) continue;

                byType.TryGetValue(key, out LevelInfo match);
                if (match == null && MetaSaveData.I != null)
                {
                    try { match = MetaSaveData.I.GetLvlData(lt, 0)?.GetInfo(); } catch { }
                }
                if (match != null)
                {
                    seen.Add(key);
                    Phases.Add(match);
                }
            }
        }

        // --- Unlocks ---

        public static bool Unlocked(CharType type)
        {
            if (MetaSaveData.I?.Chars != null)
            {
                foreach (CharMetaInst c in MetaSaveData.I.Chars)
                {
                    if (c != null && c.GetInfo() != null && c.GetInfo().Type == type)
                    {
                        return c.IsUnlocked;
                    }
                }
                return type == CharType.kDefault;
            }
            return CoopProgression.State.Unlocked.Contains((int)type) || (int)type <= 1;
        }

        public static string CharacterRequirement(CharType type) =>
            CoopLocalization.Text("Unlock in town / by playing", "Débloquer au village / en jouant");

        public static bool IsLevelCleared(LevelInfo level)
        {
            if (level == null) return false;
            if (MetaSaveData.I != null)
            {
                try
                {
                    LevelData data = MetaSaveData.I.GetLvlData(level.Type, 0);
                    return data != null && data.DidComplete;
                }
                catch { }
            }
            return CoopProgression.State.Cleared.Contains((int)level.Type);
        }

        public static bool LevelUnlocked(int phaseIndex)
        {
            if (phaseIndex < 0 || phaseIndex >= Phases.Count) return false;
            if (phaseIndex == 0) return true;

            LevelInfo target = Phases[phaseIndex];
            if (MetaSaveData.I != null)
            {
                try
                {
                    LevelData targetData = MetaSaveData.I.GetLvlData(target.Type, 0);
                    if (targetData != null && (targetData.IsUnlocked() || targetData.DidComplete))
                    {
                        return true;
                    }
                    LevelData prevData = MetaSaveData.I.GetLvlData(Phases[phaseIndex - 1].Type, 0);
                    if (prevData != null && prevData.DidComplete)
                    {
                        return true;
                    }
                    if (SafeIsDLCLevel(target.Type) && SafeCanAccessBallbylonDLC())
                    {
                        var dlcOrder = SafeGetLevelOrderDLC();
                        if (dlcOrder != null && dlcOrder.Length > 0 && target.Type == dlcOrder[0])
                        {
                            return true;
                        }
                    }
                }
                catch { }
                return false;
            }

            return CoopProgression.State.Cleared.Contains((int)Phases[phaseIndex - 1].Type)
                || CoopProgression.State.Cleared.Contains((int)target.Type);
        }

        // --- NG+, Speed Tiers & Game Speed ---

        public static int MaxNGPlusFor(LevelInfo level)
        {
            if (level == null || MetaSaveData.I == null) return 0;
            try
            {
                bool isDlc = SafeCanAccessBallbylonDLC() && SafeIsDLCLevel(level.Type);
                return Mathf.Max(0, MetaSaveData.I.GetLatestNGPlusLevel(isDlc));
            }
            catch
            {
                return 0;
            }
        }

        public static void CycleNGPlus(LevelInfo level, int delta = 1)
        {
            int max = MaxNGPlusFor(level);
            if (max <= 0)
            {
                SelectedNGPlus = 0;
                return;
            }
            int count = max + 1;
            int clamped = Mathf.Clamp(SelectedNGPlus, 0, max);
            SelectedNGPlus = (((clamped + delta) % count) + count) % count;
            SelectedSpeedTier = ClampSpeedTierFor(level, SelectedSpeedTier);
        }

        public static int MaxSpeedTierCap()
        {
            try
            {
                var times = LevelMgr.kEndGameTimes;
                if (times != null && times.Length > 0) return times.Length;
            }
            catch { }
            return 11;
        }

        public static int BestCompletedSpeedTierFor(LevelInfo level, int ngPlus = -1)
        {
            if (level == null || MetaSaveData.I == null) return 0;
            int targetNg = ngPlus >= 0 ? ngPlus : Mathf.Clamp(SelectedNGPlus, 0, MaxNGPlusFor(level));
            int best = 0;
            try
            {
                LevelData data = MetaSaveData.I.GetLvlData(level.Type, targetNg);
                if (data != null)
                {
                    best = Math.Max(best, data.GetBestDiffCompleted());
                    if (best <= 0 && data.DidComplete) best = 1;
                }
            }
            catch { }

            if (best <= 0 && targetNg == 0 && CoopProgression.State.Cleared.Contains((int)level.Type))
            {
                best = 1;
            }
            return best;
        }

        public static int MaxSpeedTierFor(LevelInfo level)
        {
            int cap = MaxSpeedTierCap();
            int best = BestCompletedSpeedTierFor(level);
            return best <= 0 ? 1 : Mathf.Clamp(best + 1, 1, cap);
        }

        public static int ClampSpeedTierFor(LevelInfo level, int tier) =>
            Mathf.Clamp(tier, 1, MaxSpeedTierFor(level));

        public static void CycleSpeedTier(LevelInfo level, int delta = 1)
        {
            int max = MaxSpeedTierFor(level);
            if (max <= 1)
            {
                SelectedSpeedTier = 1;
                return;
            }
            int zeroBased = Mathf.Clamp(SelectedSpeedTier, 1, max) - 1;
            SelectedSpeedTier = (((zeroBased + delta) % max) + max) % max + 1;
        }

        public static string SpeedTierName(int tier)
        {
            int clamped = Math.Max(1, tier);
            if (clamped == 1) return CoopLocalization.Text("Normal", "Normal");
            if (clamped == 2) return CoopLocalization.Text("Fast", "Rapide");
            if (clamped == 3) return CoopLocalization.Text("Fast+", "Rapide+");
            if (clamped == 4) return CoopLocalization.Text("Fast++", "Rapide++");
            return CoopLocalization.Text("Fast+" + (clamped - 2), "Rapide+" + (clamped - 2));
        }

        public static string SpeedTierLabel(int tier) =>
            CoopLocalization.Text("Speed Tier: ", "Palier : ") + SpeedTierName(tier);

        public static int SpeedTierGoldBonusPct(int tier)
        {
            int clamped = Math.Max(1, tier);
            try
            {
                if (LevelMgr.I != null) return LevelMgr.I.GetGoldBonusPct(clamped);
            }
            catch { }
            return Math.Max(0, (clamped - 1) * 10);
        }

        public static float SpeedTierDurationSeconds(int tier)
        {
            int idx = Math.Max(0, tier - 1);
            try
            {
                var times = LevelMgr.kEndGameTimes;
                if (times != null && idx < times.Length && times[idx] > 0f) return times[idx];
            }
            catch { }
            return Math.Max(300f, 960f - idx * 45f);
        }

        public static int LevelFastTiersCompleted(LevelInfo level)
        {
            if (level == null || MetaSaveData.I == null) return 0;
            try
            {
                int targetNg = Mathf.Clamp(SelectedNGPlus, 0, MaxNGPlusFor(level));
                LevelData data = MetaSaveData.I.GetLvlData(level.Type, targetNg);
                return data != null ? Math.Max(0, data.GetNumFastTiersCompleted()) : 0;
            }
            catch { return 0; }
        }

        public static int AvailableBlueprintsForLevel(LevelInfo level)
        {
            if (level == null || BuildingMgr.I == null) return 0;
            try
            {
                var list = BuildingMgr.I.GetAvailBlueprintsForLevel(level.Type);
                return list != null ? Math.Max(0, list.Count) : 0;
            }
            catch { return 0; }
        }

        public static int CharBestFastTierForLevel(LevelInfo level, CharType ch)
        {
            if (level == null || MetaSaveData.I == null) return 0;
            try
            {
                int targetNg = Mathf.Clamp(SelectedNGPlus, 0, MaxNGPlusFor(level));
                LevelData data = MetaSaveData.I.GetLvlData(level.Type, targetNg);
                if (data?.BestDifficultyByChar == null) return 0;
                int idx = (int)ch;
                if (idx < 0 || idx >= data.BestDifficultyByChar.Length) return 0;
                return Math.Max(0, data.BestDifficultyByChar[idx]);
            }
            catch { return 0; }
        }

        public static string GameSpeedLabel()
        {
            string prefix = CoopLocalization.Text("Speed: ", "Vitesse : ");
            try
            {
                float s = PlayerPrefsMgr.sGameSpeed;
                if (s >= 1.75f) return prefix + "2.0x";
                if (s >= 1.25f) return prefix + "1.5x";
            }
            catch { }
            return prefix + "1.0x";
        }

        public static void CycleGameSpeed()
        {
            try
            {
                float current = PlayerPrefsMgr.sGameSpeed;
                float next = current < 1.25f ? 1.5f : (current < 1.75f ? 2.0f : 1.0f);
                PlayerPrefsMgr.SetGameSpeed(next);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"CycleGameSpeed failed: {ex.Message}");
            }
        }
    }
}
