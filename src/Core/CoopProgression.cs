using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.UI;
using UnityEngine;

namespace BALLxPITLocalCoop.Core
{
    /// <summary>
    /// Per-save-slot co-op progress persistence (atomic file writes + legacy migration),
    /// speed-tier / combo completion recording (honoring Cheated flag), and accurate P1/P2 town character XP awards.
    /// </summary>
    public static class CoopProgression
    {
        public sealed class ProgressData
        {
            public List<int> Cleared { get; set; } = new List<int>();
            public List<int> Unlocked { get; set; } = new List<int> { 0, 1 };
            public int SelectedPhase { get; set; }
            public int TotalRuns { get; set; }
            public int TotalVictories { get; set; }
            public CoopRunReport LastResult { get; set; }
        }

        public static ProgressData State { get; private set; } = new ProgressData();
        public static int LastEarnedCharXp { get; private set; }

        private static string _saveDir;
        private static int _loadedProgressSlot = int.MinValue;
        private static bool _charXpAwardedThisRun;
        private static bool _p1AwardedByMod;
        private static bool _p2AwardedByMod;
        private static int _p1StartLvl;
        private static int _p1StartXp;
        private static int _p1StartBattles;

        private static int CurrentSaveSlot()
        {
            try { return Math.Max(0, PlayerPrefsMgr.sSaveSlot); }
            catch { return 0; }
        }

        private static string EnsureSaveDir()
        {
            if (string.IsNullOrEmpty(_saveDir))
            {
                _saveDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BALLxPITCoop");
            }
            Directory.CreateDirectory(_saveDir);
            return _saveDir;
        }

        private static string PathForSlot(int slot) =>
            Path.Combine(EnsureSaveDir(), $"progress_slot{Math.Max(0, slot)}.json");

        public static void Load(bool force = false)
        {
            int slot = CurrentSaveSlot();
            if (!force && _loadedProgressSlot == slot)
            {
                return;
            }
            _loadedProgressSlot = slot;
            State = new ProgressData();

            try
            {
                string slotPath = PathForSlot(slot);
                string legacyPath = Path.Combine(EnsureSaveDir(), "progress.json");

                string sourcePath = null;
                if (File.Exists(slotPath))
                {
                    sourcePath = slotPath;
                }
                else if (slot == 0 && File.Exists(legacyPath))
                {
                    sourcePath = legacyPath;
                }

                if (!string.IsNullOrEmpty(sourcePath))
                {
                    var parsed = JsonSerializer.Deserialize<ProgressData>(File.ReadAllText(sourcePath));
                    if (parsed != null)
                    {
                        State = parsed;
                    }
                    // One-time migration to slot-specific file for slot 0.
                    if (sourcePath == legacyPath && !File.Exists(slotPath))
                    {
                        Save();
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"Failed to load co-op progress (slot {slot}): {ex.Message}");
                State = new ProgressData();
            }

            EnsureStateDefaults();
            CoopMode.SelectedPhase = Math.Max(0, State.SelectedPhase);
        }

        public static void Save()
        {
            EnsureStateDefaults();
            int slot = CurrentSaveSlot();
            try
            {
                string destPath = PathForSlot(slot);
                string tempPath = destPath + ".tmp";
                State.SelectedPhase = CoopMode.SelectedPhase;
                string json = JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, destPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"Failed to save co-op progress (slot {slot}): {ex.Message}");
            }
        }

        private static void EnsureStateDefaults()
        {
            State ??= new ProgressData();
            State.Cleared ??= new List<int>();
            State.Unlocked ??= new List<int> { 0, 1 };
        }

        public static void ResetRunRewards()
        {
            _charXpAwardedThisRun = false;
            _p1AwardedByMod = false;
            _p2AwardedByMod = false;
            LastEarnedCharXp = 0;

            CharMetaInst p1Meta = FindCharMeta(CoopCharacterRuntime.FirstType);
            if (p1Meta != null)
            {
                _p1StartLvl = p1Meta.Lvl;
                _p1StartXp = p1Meta.CurXP;
                _p1StartBattles = p1Meta.NumBattles;
            }
            else
            {
                _p1StartLvl = 0;
                _p1StartXp = 0;
                _p1StartBattles = 0;
            }
        }

        public static void RecordVictory(int phase)
        {
            if (!CoopMode.Enabled) return;
            Load();
            if (phase >= 0 && phase < CoopDlcAndLevels.Phases.Count)
            {
                int levelId = (int)CoopDlcAndLevels.Phases[phase].Type;
                if (!State.Cleared.Contains(levelId))
                {
                    State.Cleared.Add(levelId);
                }
            }
            State.TotalVictories++;
            State.LastResult = CoopHudAndResults.CaptureReport(
                victory: true,
                phase,
                (int)CoopCharacterRuntime.FirstType,
                (int)CoopCharacterRuntime.SecondType,
                LastEarnedCharXp);
            Save();
            CoopHudAndResults.ResultReady();
        }

        public static void RecordDefeat(int phase)
        {
            if (!CoopMode.Enabled) return;
            Load();
            State.LastResult = CoopHudAndResults.CaptureReport(
                victory: false,
                phase,
                (int)CoopCharacterRuntime.FirstType,
                (int)CoopCharacterRuntime.SecondType,
                LastEarnedCharXp);
            Save();
            CoopHudAndResults.ResultReady();
        }

        public static void RecordSpeedTierCompletion()
        {
            try
            {
                if (MetaSaveData.I == null || (BattleSaveData.I != null && BattleSaveData.I.Cheated))
                {
                    return;
                }

                int curDiff = Mathf.Max(
                    1,
                    BattleSaveData.I != null && BattleSaveData.I.CurDifficulty > 0 ? BattleSaveData.I.CurDifficulty :
                    (LevelMgr.I != null && LevelMgr.I.CurDifficulty > 0 ? LevelMgr.I.CurDifficulty : CoopDlcAndLevels.SelectedSpeedTier));

                LevelType curLevel = BattleSaveData.I != null
                    ? BattleSaveData.I.CurLevel
                    : (LevelMgr.I != null
                        ? LevelMgr.I.CurLevel
                        : (CoopMode.SelectedPhase >= 0 && CoopMode.SelectedPhase < CoopDlcAndLevels.Phases.Count
                            ? CoopDlcAndLevels.Phases[CoopMode.SelectedPhase].Type
                            : default));

                int ngPlus = BattleSaveData.I != null ? BattleSaveData.I.CurNGPlusLvl : Math.Max(0, CoopDlcAndLevels.SelectedNGPlus);
                float elapsed = BattleSaveData.I != null ? BattleSaveData.I.ElapsedTime : 0f;

                LevelData data = MetaSaveData.I.GetLvlData(curLevel, ngPlus);
                if (data == null)
                {
                    return;
                }

                data.DidComplete = true;
                CreditCharCompletion(data, (int)CoopCharacterRuntime.FirstType, curDiff, elapsed);
                CreditCharCompletion(data, (int)CoopCharacterRuntime.SecondType, curDiff, elapsed);

                int c1 = (int)CoopCharacterRuntime.FirstType;
                int c2 = (int)CoopCharacterRuntime.SecondType;
                if (data.BestDifficultyByCharCombo != null
                    && c1 >= 0 && c1 < data.BestDifficultyByCharCombo.Length
                    && c2 >= 0 && c2 < data.BestDifficultyByCharCombo.Length)
                {
                    var row1 = data.BestDifficultyByCharCombo[c1];
                    if (row1 != null && c2 < row1.Length && row1[c2] < curDiff) row1[c2] = curDiff;
                    var row2 = data.BestDifficultyByCharCombo[c2];
                    if (row2 != null && c1 < row2.Length && row2[c1] < curDiff) row2[c1] = curDiff;
                }

                SaveMgr.I?.MarkMetaDirty();
                SaveMgr.I?.SaveMeta();
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"RecordSpeedTierCompletion failed: {ex.Message}");
            }
        }

        private static void CreditCharCompletion(LevelData data, int charIdx, int curDiff, float elapsed)
        {
            if (data == null || charIdx < 0) return;
            if (data.BestDifficultyByChar != null && charIdx < data.BestDifficultyByChar.Length)
            {
                if (data.BestDifficultyByChar[charIdx] < curDiff)
                {
                    data.BestDifficultyByChar[charIdx] = curDiff;
                }
            }
            if (elapsed > 0f && data.BestTimeByChar != null && charIdx < data.BestTimeByChar.Length)
            {
                float prev = data.BestTimeByChar[charIdx];
                if (prev <= 0f || elapsed < prev)
                {
                    data.BestTimeByChar[charIdx] = elapsed;
                }
            }
        }

        public static CharMetaInst FindCharMeta(CharType charType)
        {
            if (MetaSaveData.I?.Chars == null) return null;
            int idx = (int)charType;
            if (idx >= 0 && idx < MetaSaveData.I.Chars.Length)
            {
                CharMetaInst direct = MetaSaveData.I.Chars[idx];
                if (direct != null && direct.Type == charType) return direct;
            }
            for (int i = 0; i < MetaSaveData.I.Chars.Length; i++)
            {
                CharMetaInst c = MetaSaveData.I.Chars[i];
                if (c != null && c.Type == charType) return c;
            }
            return null;
        }

        private static int ComputeEarnedCharacterXp(bool includeEarlyReturn)
        {
            if (BattleSaveData.I == null || BattleSaveData.I.Cheated)
            {
                return 0;
            }

            int xp = 0;
            try
            {
                if (GameOverUI.I != null)
                {
                    xp = Math.Max(0, GameOverUI.I.CalculateXPEarned());
                }
            }
            catch { }

            if (xp <= 0 && includeEarlyReturn)
            {
                bool earnedProgress = BattleSaveData.I.CompletedLevel
                    || BattleSaveData.I.UpgradeLvl > 0
                    || BattleSaveData.I.NumKills > 0
                    || BattleSaveData.I.ElapsedTime >= 5f
                    || BattleSaveData.I.CurTurn > 1;
                if (earnedProgress)
                {
                    float charMult = 1f;
                    float lvlMult = 1f;
                    float diffBonus = 0f;
                    try
                    {
                        if (UpgradeMgr.I != null) charMult += Math.Max(0f, UpgradeMgr.I.BonusCharXP);
                        if (LevelMgr.I?.CurLevelInfo != null && LevelMgr.I.CurLevelInfo.XPMult > 0f) lvlMult = LevelMgr.I.CurLevelInfo.XPMult;
                        if (LevelMgr.I != null && LevelMgr.I.CurDifficulty > 0) diffBonus = LevelMgr.I.CurDifficulty * 2f;
                    }
                    catch { }

                    float totalMult = Mathf.Max(1f, charMult * lvlMult + diffBonus);
                    int raw = BattleSaveData.I.UpgradeLvl * 20
                        + BattleSaveData.I.NumKills
                        + Mathf.RoundToInt(BattleSaveData.I.ElapsedTime * 0.5f)
                        + (BattleSaveData.I.CompletedLevel ? 150 : 0);
                    if (raw <= 0) raw = 10;
                    xp = Mathf.Max(1, Mathf.RoundToInt(raw * totalMult * 0.25f));
                }
            }
            return xp;
        }

        private static void ApplyXpToChar(CharMetaInst meta, int xp, bool incrementBattles)
        {
            if (meta == null) return;
            if (incrementBattles)
            {
                meta.NumBattles++;
            }
            if (xp <= 0) return;

            meta.CurXP += xp;
            int guard = 0;
            while (meta.Lvl < CharMetaInst.kMaxLvl && guard++ < 64)
            {
                int tgt = meta.GetTgtXP(-1);
                if (tgt <= 0 || meta.CurXP < tgt) break;
                meta.AddLvl();
            }
        }

        public static void AwardCharacterProgressXp(bool includeEarlyReturn = true)
        {
            if (_charXpAwardedThisRun || MetaSaveData.I == null || MetaSaveData.I.Chars == null || BattleSaveData.I == null)
            {
                return;
            }

            try
            {
                int xp = ComputeEarnedCharacterXp(includeEarlyReturn);
                LastEarnedCharXp = xp;
                if (xp <= 0)
                {
                    // Do not increment NumBattles or mark meta dirty on 0-XP immediate quits.
                    return;
                }

                CharMetaInst p1Meta = FindCharMeta(CoopCharacterRuntime.FirstType);
                CharMetaInst p2Meta = FindCharMeta(CoopCharacterRuntime.SecondType);

                bool changed = false;
                if (p1Meta != null && !_p1AwardedByMod)
                {
                    bool vanillaAlreadyAwarded = p1Meta.Lvl > _p1StartLvl
                        || (p1Meta.Lvl == _p1StartLvl && p1Meta.CurXP > _p1StartXp);
                    if (!vanillaAlreadyAwarded)
                    {
                        ApplyXpToChar(p1Meta, xp, incrementBattles: p1Meta.NumBattles == _p1StartBattles);
                        changed = true;
                    }
                    _p1AwardedByMod = true;
                }

                if (p2Meta != null && !HarmonyHelper.SameNative(p1Meta, p2Meta) && !_p2AwardedByMod)
                {
                    ApplyXpToChar(p2Meta, xp, incrementBattles: true);
                    _p2AwardedByMod = true;
                    changed = true;
                }

                _charXpAwardedThisRun = true;
                if (changed)
                {
                    Plugin.Logger?.LogInfo(
                        $"Awarded +{xp} char XP (P1 {CoopCharacterRuntime.FirstType}: Lv.{p1Meta?.Lvl} CurXP={p1Meta?.CurXP}, " +
                        $"P2 {CoopCharacterRuntime.SecondType}: Lv.{p2Meta?.Lvl} CurXP={p2Meta?.CurXP}).");
                    SaveMgr.I?.MarkMetaDirty();
                    SaveMgr.I?.SaveMeta();
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"AwardCharacterProgressXp failed: {ex.Message}");
            }
        }
    }
}
