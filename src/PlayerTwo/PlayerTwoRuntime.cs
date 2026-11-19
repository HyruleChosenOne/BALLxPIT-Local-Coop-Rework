using System;
using BALLxPITLocalCoop.Core;
using BALLxPITLocalCoop.Diagnostics;
using BALLxPITLocalCoop.Gameplay;
using BALLxPITLocalCoop.Input;
using BALLxPITLocalCoop.UI;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BALLxPITLocalCoop.PlayerTwo
{
    /// <summary>
    /// Deterministic coordinator for Player 2 spawning, intro follow, input, aiming, and shooting.
    /// Driven directly from CoopHost.Update / LateUpdate (single host MonoBehaviour, guaranteed ordering).
    /// </summary>
    internal static class PlayerTwoRuntime
    {
        private const float InitialOffsetX = 0.75f;
        private const float IntroMaxFollowDuration = 1.3f;
        private const float IntroMinFollowDuration = 0.75f;
        private const float IntroStableDuration = 0.3f;
        private const float ScanInterval = 0.25f;

        private static bool _evaluatingMouseAim;
        private static Vector2 _aimDir = Vector2.up;
        private static Vector2 _moveDir = Vector2.zero;
        private static bool _p2HasExternalAim;
        private static bool _mouseAimActive;

        private static bool _isFollowingIntro;
        private static bool _introObservedP1Movement;
        private static float _introElapsed;
        private static float _introStableElapsed;
        private static Vector3 _lastP1WorldPos;
        private static float _nextScanTime;

        private static Player _playerOne;
        private static Player _playerTwo;
        private static GameObject _playerTwoObject;
        private static PlayerCharController _p1Controller;
        private static PlayerCharController _p2Controller;

        public static bool IsEvaluatingMouseAim => _evaluatingMouseAim;
        public static bool IsRuntimeEnabled => CoopMode.Enabled && !IsInBaseTownScene();
        public static Player PlayerOne => _playerOne;
        public static Player PlayerTwo => _playerTwo;
        public static bool IsFollowingIntro => _isFollowingIntro;

        public static PlayerCharController PlayerTwoCharController
        {
            get => _p2Controller;
            set => _p2Controller = value;
        }

        public static Vector2 AimDir
        {
            get => _aimDir;
            set
            {
                if (value.sqrMagnitude > 0.0001f) _aimDir = value.normalized;
            }
        }

        public static bool IsPlayerTwoActive() => IsRuntimeEnabled && _playerTwo != null;
        public static void ClearIntroFollow() => _isFollowingIntro = false;

        private static bool IsInBaseTownScene()
        {
            try
            {
                if (BaseMgr.I != null) return true;
                return HarmonyHelper.SafeGetCurScene() == GameScene.kBase;
            }
            catch
            {
                return false;
            }
        }

        public static void TickUpdate()
        {
            using (HangWatchdog.Enter(HangWatchdog.PhaseId.P2Update))
            {
                ModConfig.PollForChanges();

                if (!IsRuntimeEnabled)
                {
                    if (_playerTwoObject != null || _playerTwo != null)
                    {
                        CoopContext.ForceRestorePlayerOne();
                        ResetInstance();
                    }
                    return;
                }

                try
                {
                    CoopContext.ForceRestorePlayerOne();

                    if (Time.unscaledTime >= _nextScanTime)
                    {
                        _nextScanTime = Time.unscaledTime + ScanInterval;
                        RefreshBinding();
                    }

                    if (_playerTwo != null)
                    {
                        PlayerTwoPatches.RefreshGlobalRecallState(BallMgr.I);
                        UpdateInput();
                        if (_isFollowingIntro)
                        {
                            UpdateIntroFollow();
                        }
                    }

                    CoopVisuals.SyncMouse();
                }
                finally
                {
                    CoopContext.ForceRestorePlayerOne();
                }
            }
        }

        public static void TickLateUpdate()
        {
            using (HangWatchdog.Enter(HangWatchdog.PhaseId.P2LateUpdate))
            {
                if (!IsRuntimeEnabled || _playerTwo == null || _p2Controller == null)
                {
                    PreviewIsolation.HideAimCursor();
                    CoopVisuals.SyncFirstAim();
                    return;
                }

                try
                {
                    bool isPlaying = GameMgr.I != null && GameMgr.I.CurState == GameState.kPlaying;
                    PlayerTwoPatches.RefreshGlobalRecallState(BallMgr.I);

                    if (!isPlaying)
                    {
                        PreviewIsolation.HideAimCursor();
                        CoopVisuals.SyncFirstAim();
                        return;
                    }

                    RefreshAim();
                    ApplyAim();

                    if (!PlayerTwoPatches.IsGlobalRecallActive)
                    {
                        CoopAmmoRecovery.Tick();
                        PlayerTwoPatches.RunPlayerTwoBallMgrMaintenance(BallMgr.I, _playerTwo);
                        TryShoot();
                        PreviewIsolation.RefreshPreview(BallMgr.I, _playerTwo);
                        PreviewIsolation.UpdateAimCursor(_playerTwo, _aimDir, !_p2HasExternalAim && _mouseAimActive);
                    }
                    else
                    {
                        PreviewIsolation.HideAimCursor();
                    }

                    CoopVisuals.SyncFirstAim();
                }
                finally
                {
                    CoopContext.ForceRestorePlayerOne();
                }
            }
        }

        public static void ResetInstance()
        {
            Player previousP2 = _playerTwo;
            CoopContext.ClearPlayerTwo(previousP2);
            if (_playerTwoObject != null)
            {
                try { UnityEngine.Object.Destroy(_playerTwoObject); } catch { }
            }
            PreviewIsolation.ResetRound();

            _playerOne = null;
            _playerTwo = null;
            _playerTwoObject = null;
            _p1Controller = null;
            _p2Controller = null;
            _moveDir = Vector2.zero;
            _aimDir = Vector2.up;
            _mouseAimActive = false;
            _p2HasExternalAim = false;
            _isFollowingIntro = false;
            _introObservedP1Movement = false;
            _introElapsed = 0f;
            _introStableElapsed = 0f;
            _lastP1WorldPos = Vector3.zero;
            _nextScanTime = 0f;
        }

        public static void ReapplyAim() => ApplyAim();
        public static void ReapplyMovementOverrides() => ApplyMovementOverrides();

        private static void RefreshBinding()
        {
            if (!IsRuntimeEnabled)
            {
                if (_playerTwoObject != null || _playerTwo != null) ResetInstance();
                return;
            }

            Player p1 = Player.I;
            if (p1 == null)
            {
                if (_playerTwoObject != null || _playerTwo != null) ResetInstance();
                return;
            }

            if (_playerOne != null && !HarmonyHelper.SameNative(p1, _playerOne))
            {
                if (HarmonyHelper.SameNative(p1, _playerTwo)) return;
                ResetInstance();
            }

            // Recover if _playerTwoObject or _p2Controller was externally destroyed.
            if (_playerTwoObject != null && _playerTwo != null && _p2Controller != null)
            {
                return;
            }
            if (_playerTwoObject != null || _playerTwo != null)
            {
                ResetInstance();
            }

            Il2CppReferenceArray<PlayerCharController> controllers = p1.CharControllers;
            if (controllers != null && controllers.Length > 0 && controllers[0] != null)
            {
                SpawnNativePlayerTwo(p1, controllers[0]);
            }
        }

        private static void SpawnNativePlayerTwo(Player playerOne, PlayerCharController playerOneController)
        {
            Plugin.Logger?.LogInfo("Spawning native Player 2 clone...");
            GameObject clonedObj = null;
            try
            {
                CoopContext.BeginPlayerTwoCreation(playerOne);

                clonedObj = UnityEngine.Object.Instantiate(playerOne.gameObject, playerOne.transform.parent);
                clonedObj.name = playerOne.gameObject.name + "_LocalCoopP2_NATIVE";
                clonedObj.SetActive(false);

                Player clonedPlayer = clonedObj.GetComponent<Player>();
                if (clonedPlayer == null)
                {
                    throw new InvalidOperationException("Cloned Player GameObject lacks a Player component.");
                }

                CoopContext.CompletePlayerTwoCreation(clonedPlayer);
                _playerOne = playerOne;
                _playerTwo = clonedPlayer;
                _playerTwoObject = clonedObj;
                _p1Controller = playerOneController;
                _moveDir = Vector2.zero;
                _aimDir = playerOne.GetLastAimDir();
                if (_aimDir.sqrMagnitude <= 0.0001f) _aimDir = Vector2.up;

                _playerTwo.IsOverridingInput = true;
                _playerTwo.InputXOverride = 0f;
                _playerTwo.InputYOverride = 0f;

                Vector3 p1Pos = playerOneController.transform.position;
                float clampedX = p1Pos.x + InitialOffsetX;
                try { clampedX = playerOne.ClampXPos(clampedX); } catch { }
                Vector3 spawnPos = new Vector3(clampedX, p1Pos.y, p1Pos.z);

                var scope = CoopContext.EnterPlayerTwo(_playerTwo, CoopContext.ScopeKind.Full);
                try
                {
                    clonedObj.SetActive(true);
                    _playerTwo.SetPos(spawnPos, snap: true);
                }
                finally
                {
                    CoopContext.Exit(scope);
                }

                Il2CppReferenceArray<PlayerCharController> controllers = _playerTwo.CharControllers;
                if (controllers == null || controllers.Length == 0 || controllers[0] == null)
                {
                    throw new InvalidOperationException("Cloned Player has empty/null CharControllers.");
                }

                _p2Controller = controllers[0];
                _p2Controller.SetMoveDir(Vector2.zero);
                _p2Controller.SetAimDir(_aimDir);

                BallMgrVirtualizer.Initialize(BallMgr.I);
                HeroInventoryIsolation.Initialize();
                PreviewIsolation.ResetRound();

                _isFollowingIntro = true;
                _introObservedP1Movement = false;
                _introElapsed = 0f;
                _introStableElapsed = 0f;
                _lastP1WorldPos = p1Pos;
                _mouseAimActive = GamepadRouter.P2MouseMode;

                // Swap P2 to its independent character & inventory, THEN ensure multi-hero secondary previews!
                CoopCharacterRuntime.OnPlayerTwoSpawned(_playerTwo);
                PreviewIsolation.EnsureSecondaryPreviewForPlayerTwo();

                Plugin.Logger?.LogInfo("Native P2 created successfully.");
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError($"Native P2 creation failed: {ex}");
                CoopContext.CancelPlayerTwoCreation();
                if (clonedObj != null)
                {
                    try { UnityEngine.Object.Destroy(clonedObj); } catch { }
                }
                _playerOne = null;
                _playerTwo = null;
                _playerTwoObject = null;
                _p1Controller = null;
                _p2Controller = null;
            }
            finally
            {
                CoopContext.ForceRestorePlayerOne();
            }
        }

        private static void UpdateIntroFollow()
        {
            if (!CoopSession.ShouldUpdateIntroFollow() || _p1Controller == null || _playerTwo == null)
            {
                return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            _introElapsed += dt;
            Vector3 p1Pos = _p1Controller.transform.position;
            if ((p1Pos - _lastP1WorldPos).sqrMagnitude > 2.5e-05f)
            {
                _introObservedP1Movement = true;
                _introStableElapsed = 0f;
            }
            else
            {
                _introStableElapsed += dt;
            }
            _lastP1WorldPos = p1Pos;

            float clampedX = p1Pos.x + InitialOffsetX;
            try { clampedX = _playerTwo.ClampXPos(clampedX); } catch { }
            Vector3 targetPos = new Vector3(clampedX, p1Pos.y, p1Pos.z);

            var scope = CoopContext.EnterPlayerTwo(_playerTwo, CoopContext.ScopeKind.PositionOnly);
            try
            {
                _playerTwo.SetPos(targetPos, snap: true);
            }
            finally
            {
                CoopContext.Exit(scope);
            }

            if ((_introObservedP1Movement && _introElapsed >= IntroMinFollowDuration && _introStableElapsed >= IntroStableDuration)
                || _introElapsed >= IntroMaxFollowDuration)
            {
                _isFollowingIntro = false;
                ApplyMovementOverrides();
            }
        }

        private static void UpdateInput()
        {
            _moveDir = GamepadRouter.GetSecondPlayerMoveInput();
            _mouseAimActive = GamepadRouter.P2MouseMode;
            ApplyMovementOverrides();
        }

        private static void RefreshAim()
        {
            if (_playerTwo == null || CoopVitals.Down(1)) return;
            _mouseAimActive = GamepadRouter.P2MouseMode;

            Vector2? padAim = GamepadRouter.GetSecondPlayerAimDirection();
            _p2HasExternalAim = padAim.HasValue;
            if (padAim.HasValue)
            {
                if (padAim.Value.sqrMagnitude > 0.0001f)
                {
                    _aimDir = padAim.Value.normalized;
                }
                return;
            }

            if (_mouseAimActive)
            {
                UpdateMouseAim();
            }
            else
            {
                UpdateKeyboardAim();
            }
        }

        private static void UpdateKeyboardAim()
        {
            if (!Application.isFocused) return;
            Vector2 aimInput = Vector2.zero;
            if (UnityEngine.Input.GetKey(ModConfig.KeyAimLeft)) aimInput.x -= 1f;
            if (UnityEngine.Input.GetKey(ModConfig.KeyAimRight)) aimInput.x += 1f;
            if (UnityEngine.Input.GetKey(ModConfig.KeyAimUp)) aimInput.y += 1f;
            if (aimInput.sqrMagnitude <= 0.0001f) return;

            aimInput.Normalize();
            GamepadRouter.GetSecondPlayerAimArcDegrees(out float minAngle, out float maxAngle);
            float targetAngle = CoopAimRules.TargetDegrees(aimInput.x, aimInput.y, minAngle, maxAngle);
            float currentAngle = CoopAimRules.TargetDegrees(_aimDir.x, _aimDir.y, minAngle, maxAngle);
            float turnSpeed = ModConfig.AimTurnSpeed != null ? ModConfig.AimTurnSpeed.Value : 1080f;
            float nextRad = Mathf.MoveTowards(currentAngle, targetAngle, turnSpeed * Time.deltaTime) * Mathf.Deg2Rad;
            _aimDir = new Vector2(Mathf.Cos(nextRad), Mathf.Sin(nextRad));
            GamepadRouter.SyncSecondAimFromExternal(_aimDir);
        }

        private static void UpdateMouseAim()
        {
            if (_playerTwo == null || !Application.isFocused) return;
            try
            {
                Player mouseSource = CoopContext.OriginalPlayer ?? _playerTwo;
                Vector3 mouseWorld = mouseSource.GetMouseWorldPos();
                BallMgr mgr = BallMgr.I;
                if (mgr != null)
                {
                    var scope = CoopContext.EnterPlayerTwo(_playerTwo, CoopContext.ScopeKind.Full);
                    _evaluatingMouseAim = true;
                    try
                    {
                        Vector2 aim = mgr.MousePosToAimDir(mouseWorld, 0);
                        if (aim.sqrMagnitude > 0.0001f)
                        {
                            _aimDir = aim.normalized;
                            return;
                        }
                    }
                    finally
                    {
                        _evaluatingMouseAim = false;
                        CoopContext.Exit(scope);
                    }
                }

                Vector3 origin = _p2Controller != null && _p2Controller.ShootXfm != null
                    ? _p2Controller.ShootXfm.position
                    : CoopContext.GetPlayerPosition(_playerTwo);
                Vector2 delta = new Vector2(mouseWorld.x - origin.x, mouseWorld.y - origin.y);
                if (delta.sqrMagnitude > 0.0001f)
                {
                    _aimDir = delta.normalized;
                }
            }
            catch { }
        }

        private static void ApplyAim()
        {
            if (_playerTwo == null || _aimDir.sqrMagnitude <= 0.0001f) return;
            var scope = CoopContext.EnterPlayerTwo(_playerTwo, CoopContext.ScopeKind.PositionOnly);
            try
            {
                _playerTwo.SetAimDir(_aimDir);
                _p2Controller?.SetAimDir(_aimDir);
                var controllers = _playerTwo.CharControllers;
                if (controllers != null)
                {
                    for (int i = 0; i < controllers.Length; i++)
                    {
                        if (controllers[i] != null && !HarmonyHelper.SameNative(controllers[i], _p2Controller))
                        {
                            controllers[i].SetAimDir(_aimDir);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                CoopContext.Exit(scope);
            }
        }

        private static void ApplyMovementOverrides()
        {
            if (_playerTwo == null) return;
            _playerTwo.IsOverridingInput = true;
            if (_isFollowingIntro || CoopVitals.Down(1))
            {
                _playerTwo.InputXOverride = 0f;
                _playerTwo.InputYOverride = 0f;
                return;
            }
            Vector2 move = _moveDir.sqrMagnitude > 1f ? _moveDir.normalized : _moveDir;
            _playerTwo.InputXOverride = move.x;
            _playerTwo.InputYOverride = move.y;
        }

        private static void TryShoot()
        {
            if (_isFollowingIntro || PlayerTwoPatches.IsPlayerOneGameOverActive
                || GameMgr.I == null || GameMgr.I.CurState != GameState.kPlaying || CoopVitals.Down(1))
            {
                return;
            }

            // Fast-path skip when a volley coroutine is already running (eliminates all P2 shot-blocked log spam!).
            if (PlayerTwoPatches.HasActivePlayerTwoBallRun)
            {
                return;
            }

            CharBattleInst p2Char = CoopCharacterRuntime.SecondChar;
            bool alwaysShoots = p2Char != null && p2Char.HasProp(CharProp.kAlwaysShoot);
            if (!alwaysShoots && !GamepadRouter.ReadSecondPlayerShoot())
            {
                return;
            }

            BallMgr mgr = BallMgr.I;
            if (mgr == null) return;

            ApplyAim();
            PlayerTwoPatches.StartPlayerTwoRunBalls(mgr, _playerTwo);
        }
    }
}
