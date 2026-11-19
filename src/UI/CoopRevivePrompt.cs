using System;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.Input;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace BALLxPITLocalCoop.UI
{
    /// <summary>
    /// Manages the Player-1-controlled Team Defeated / Revive prompt shown when both players fall.
    /// Driven cleanly from Update() (no OnGUI), removes competing Player.Heal prefix (handled by CoopVitals),
    /// prevents accidental Esc/B instant-defeat, disables Revive row when 0 revives remain, and cleanly hides on state exit.
    /// </summary>
    public static class CoopRevivePrompt
    {
        private static bool _installed;
        private static bool _applying;
        private static float _armedAt;
        private static int _selected;
        private static float _nextMove;

        public static bool Pending { get; private set; }

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(InGameUI), nameof(InGameUI.RefreshHealthBar)),
                prefix: new HarmonyMethod(typeof(CoopRevivePrompt), nameof(OnRefreshHealthBar)),
                label: "InGameUI.RefreshHealthBar (CoopRevivePrompt)");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(GameMgr), nameof(GameMgr.SetState)),
                postfix: new HarmonyMethod(typeof(CoopRevivePrompt), nameof(OnGameStateChanged)),
                label: "GameMgr.SetState (CoopRevivePrompt)");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(ReviveUI), nameof(ReviveUI.OnYesClicked)),
                prefix: new HarmonyMethod(typeof(CoopRevivePrompt), nameof(GuardReviveButton)),
                label: "ReviveUI.OnYesClicked");

            HarmonyHelper.TryPatch(
                harmony,
                AccessTools.Method(typeof(ReviveUI), nameof(ReviveUI.OnNoClicked)),
                prefix: new HarmonyMethod(typeof(CoopRevivePrompt), nameof(GuardReviveButton)),
                label: "ReviveUI.OnNoClicked");
        }

        public static void Reset()
        {
            if (Pending)
            {
                Pending = false;
                ModalOverlayState.ClearIf(ActiveModalOverlay.Revive);
                ChoiceOverlay.Hide();
            }
            _applying = false;
            _selected = 0;
            _nextMove = 0f;
            _armedAt = 0f;
        }

        public static void Shutdown()
        {
            Reset();
            _installed = false;
        }

        internal static void OpenDirect()
        {
            if (!CoopMode.Enabled || !CoopCharacterRuntime.IndependentReady) return;
            if (!ChoiceOverlay.EnsureCreated((Component)ReviveUI.I ?? InGameUI.I)) return;

            Pending = true;
            _selected = CoopVitals.Life.TotalRevivesRemaining > 0 ? 0 : 1;
            _nextMove = 0f;
            _armedAt = Time.unscaledTime + 0.35f;
            ModalOverlayState.Set(ActiveModalOverlay.Revive);
            Time.timeScale = 0f;
        }

        private static void OnGameStateChanged(GameState __0)
        {
            if (__0 != GameState.kRevive)
            {
                if (Pending)
                {
                    Pending = false;
                    ModalOverlayState.ClearIf(ActiveModalOverlay.Revive);
                    ChoiceOverlay.Hide();
                }
                return;
            }

            bool eligible = CoopMode.Enabled && CoopCharacterRuntime.IndependentReady && ReviveUI.I != null;
            if (eligible && ChoiceOverlay.EnsureCreated(ReviveUI.I))
            {
                Pending = true;
                _selected = CoopVitals.Life.TotalRevivesRemaining > 0 ? 0 : 1;
                _nextMove = 0f;
                _armedAt = Time.unscaledTime + 0.35f;
                ModalOverlayState.Set(ActiveModalOverlay.Revive);
            }
            else
            {
                Pending = false;
            }
        }

        private static bool OnRefreshHealthBar(ref bool __0)
        {
            if (CoopMode.Enabled && _applying) __0 = false;
            return true;
        }

        private static bool GuardReviveButton() => !Pending || _applying;

        public static void Tick()
        {
            if (!Pending)
            {
                return;
            }

            if (Time.unscaledTime >= _armedAt && Application.isFocused)
            {
                bool canRevive = CoopVitals.Life.TotalRevivesRemaining > 0;
                if (!canRevive) _selected = 1;

                ReadPrimaryInput(out float moveY, out bool confirm, out bool cancel);

                if (OverlayInput.TryGetHoveredOptionIndex(0, out int hOpt) && hOpt >= 0 && hOpt < 2)
                {
                    if (hOpt == 1 || canRevive) _selected = hOpt;
                }
                if (OverlayInput.TryGetClickedOptionIndex(0, out int cOpt) && cOpt >= 0 && cOpt < 2)
                {
                    if (cOpt == 1 || canRevive)
                    {
                        _selected = cOpt;
                        confirm = true;
                    }
                }
                if (OverlayInput.ConsumeActionClicked("confirm_0"))
                {
                    confirm = true;
                }

                if (Mathf.Abs(moveY) < 0.4f)
                {
                    _nextMove = 0f;
                }
                else if (canRevive && Mathf.Abs(moveY) > 0.55f && Time.unscaledTime >= _nextMove)
                {
                    _selected = (_selected + 1) % 2;
                    _nextMove = Time.unscaledTime + 0.22f;
                }

                // Cancel (Esc/B) only moves focus to "End Run" instead of immediately forfeiting a run with revives left!
                if (cancel)
                {
                    ModalOverlayState.ConsumeCancelThisFrame();
                    _selected = 1;
                }

                if (confirm)
                {
                    Commit();
                    return;
                }
            }

            if (Pending)
            {
                Render();
            }
        }

        private static void ReadPrimaryInput(out float move, out bool confirm, out bool cancel)
        {
            move = 0f;
            confirm = false;
            cancel = false;

            IGamepadTemplate pad = GamepadRouter.GetPad(0);
            if (pad != null)
            {
                Vector2 stick = GamepadRouter.MenuStick(pad);
                move = Mathf.Abs(stick.y) >= Mathf.Abs(stick.x) ? -stick.y : stick.x;
                confirm = GamepadRouter.ButtonJustPressed(pad.a);
                cancel = GamepadRouter.ButtonJustPressed(pad.b);
            }

            float kb = ((UnityEngine.Input.GetKey(KeyCode.S) || UnityEngine.Input.GetKey(KeyCode.DownArrow)
                        || UnityEngine.Input.GetKey(KeyCode.D) || UnityEngine.Input.GetKey(KeyCode.RightArrow)) ? 1f : 0f)
                     - ((UnityEngine.Input.GetKey(KeyCode.W) || UnityEngine.Input.GetKey(KeyCode.UpArrow)
                        || UnityEngine.Input.GetKey(KeyCode.A) || UnityEngine.Input.GetKey(KeyCode.LeftArrow)) ? 1f : 0f);
            if (Mathf.Abs(kb) > Mathf.Abs(move)) move = kb;
            confirm |= UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.Space);
            cancel |= UnityEngine.Input.GetKeyDown(KeyCode.Escape);
        }

        private static void Commit()
        {
            if (!Pending) return;

            bool canRevive = CoopVitals.Life.TotalRevivesRemaining > 0;
            bool wantRevive = _selected == 0 && canRevive;

            _applying = true;
            try
            {
                Pending = false;
                ModalOverlayState.ClearIf(ActiveModalOverlay.Revive);
                ChoiceOverlay.Hide();

                if (wantRevive)
                {
                    if (BattleSaveData.I != null)
                    {
                        int baseRevives = BuildingMgr.I != null ? Math.Max(1, BuildingMgr.I.NumRevives) : 1;
                        BattleSaveData.I.NumRevives = Math.Max(0, baseRevives - 1);
                    }
                    CoopVitals.Life.ConsumeTeamRevive();
                    if (ReviveUI.I != null)
                    {
                        ReviveUI.I.OnYesClicked();
                    }
                    else
                    {
                        Time.timeScale = 1f;
                        if (GameMgr.I != null && GameMgr.I.CurState != GameState.kPlaying)
                        {
                            GameMgr.I.SetState(GameState.kPlaying);
                        }
                    }
                    CoopVitals.ReviveTeam();
                }
                else
                {
                    if (ReviveUI.I != null)
                    {
                        ReviveUI.I.OnNoClicked();
                    }
                    else
                    {
                        Time.timeScale = 1f;
                        GameMgr.I?.LoseGame();
                    }
                }
            }
            finally
            {
                ModalOverlayState.ClearIf(ActiveModalOverlay.Revive);
                _applying = false;
            }
        }

        private static void Render()
        {
            int totalRevives = CoopVitals.Life.TotalRevivesRemaining;
            bool canRevive = totalRevives > 0;
            int pct = Mathf.RoundToInt(CoopVitals.Life.ReviveHealthPct * 100f);

            string[] choices =
            {
                canRevive
                    ? $"<color=#78E69C><b>{CoopLocalization.Text("Revive Team", "Réanimer l'équipe")}</b></color>  <color=#F4C86A>({CoopLocalization.Text($"Uses 1 Revive | {pct}% HP", $"Utilise 1 vie | {pct}% PV")})</color>"
                    : $"<color=#F2706B>{CoopLocalization.Text("No Team Revives Remaining", "Aucune vie d'équipe restante")}</color>",
                $"<color=#F2706B><b>{CoopLocalization.Text("End Run", "Terminer la partie")}</b></color>"
            };
            bool[] enabledFlags = { canRevive, true };

            string badgeText = canRevive
                ? CoopLocalization.Text($"REVIVES LEFT: {totalRevives}", $"VIES RESTANTES : {totalRevives}")
                : CoopLocalization.Text("DEFEATED", "DÉFAITE");
            Color badgeColor = canRevive ? new Color(0.95f, 0.78f, 0.28f) : new Color(0.96f, 0.36f, 0.34f);

            string summary = CoopLocalization.Text(
                $"Both players have fallen.\nPlayer 1 decides whether to spend 1 remaining revive (<color=#F4C86A>{totalRevives} available</color>) or end the run.",
                $"Les deux joueurs sont tombés.\nLe Joueur 1 décide d'utiliser 1 vie restante (<color=#F4C86A>{totalRevives} disponible(s)</color>) ou de terminer la partie.");

            ChoiceOverlay.Show(
                CoopLocalization.Text("TEAM DEFEATED", "ÉQUIPE VAINCUE"),
                CoopLocalization.Text(
                    "Player 1 Only  |  Stick / Arrows / Mouse: Choose  |  [A] / Enter / Click: Confirm  |  [B] / Esc: Select End Run",
                    "Joueur 1 uniquement  |  Stick / Flèches / Souris : Choisir  |  [A] / Entrée / Clic : Confirmer  |  [B] / Échap : Fin"));

            ChoiceOverlay.SetHeaderButtons(showClose: false);
            ChoiceOverlay.SinglePrompt(
                heading: $"{CoopLocalization.Text("PLAYER 1 DECISION", "DÉCISION JOUEUR 1")}  |  {GamepadRouter.SlotInputLabel(0)}",
                statusText: badgeText,
                statusColor: badgeColor,
                summaryText: summary,
                options: choices,
                selected: canRevive ? Mathf.Clamp(_selected, 0, 1) : 1,
                confirmText: _selected == 0 && canRevive
                    ? CoopLocalization.Text("Revive Team", "Réanimer")
                    : CoopLocalization.Text("End Run", "Terminer"),
                icon: CoopCharacterRuntime.CharacterFor(0)?.GetInfo()?.GetIconSprite(),
                cardHeading: CoopLocalization.Text("RESURRECTION", "RÉSURRECTION"),
                enabledFlags: enabledFlags);
        }
    }
}
