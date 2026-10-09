using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DD2DamageMeter
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.dd2.damagemeter";
        private const string PluginName = "DD2 Damage Meter";
        private const string PluginVersion = "1.4.31";

        internal static ManualLogSource Log;
        internal static Plugin Instance { get; private set; }

        internal static string Version => PluginVersion;

        private Harmony _harmony;
        private DamageTracker _tracker;
        private FloorEffectSourceTracker _floorEffectSources;
        private DamageMeterUI _ui;
        private CombatLogTracker _logTracker;
        private CombatLogUI _logUi;
        private StatusLogUI _statusLogUi;
        private RunStatsTracker _runTracker;
        private RunStatsUI _runUi;
        private ContributionTracker _contributionTracker;
        private ConfigEntry<bool> _autoStartRecording;
        private ConfigEntry<bool> _autoShowInBattle;
        private ConfigEntry<bool> _autoShowOutsideBattle;
        private ConfigEntry<string> _exportDirectory;
        private ConfigEntry<string> _language;
        private ConfigEntry<int> _uiFontSize;
        private ConfigEntry<float> _uiScale;
        private bool _eventManagerReady;
        private float _checkTimer;
        private bool _battleActive;
        private bool _overlayHidden = true;
        private bool? _remoteOverlayBattleActive;
        private bool _autoStartPending;
        private bool _remoteBattleActive;
        private DamageMeterMpSnapshot _lastRemoteBattleSnapshot;
        private string _lastRemoteCapturedDigest;
        private RunStatsTracker.BattleSnapshot _lastCompletedBattleSnapshot;
        private uint _currentProfileGuid;
        private Guid _currentRunGuid;
        private bool _runIdentityBound;
        private bool _runContextActive;
        private bool _awaitingNewRunIdentity;
        private bool _runContainsRemoteBattles;
        private bool _undoRestoreInProgress;
        private string _undoRestoreId;
        private long _undoRestoreEpoch;
        private Assets.Code.Combat.CombatManager _gameCombatManager;
        private int _activeGameCombatCount;
        private Guid _activeGameCombatGuid;
        private bool _resumeCheckpointAvailable;
        private int _resumeCheckpointCombatCount;
        private Guid _resumeCheckpointCombatGuid;
        private bool _resumeCheckpointInCombat;
        private RunStatsTracker.PersistedRunState _pendingResumeState;
        private string _pendingResumePath;
        private bool _resumePromptShown;
        private float _resumePromptShownAt;
        private Assets.Code.UI.Widgets.ConfirmationDialogBhv _resumeDialog;
        private readonly object _persistenceLock = new object();
        private RunStatsTracker.PersistedRunState _pendingPersistenceState;
        private string _pendingPersistencePath;
        private int _pendingPersistenceGeneration;
        private int _persistenceGeneration;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;

            Config.SaveOnConfigSet = true;
            _language = Config.Bind(
                "UI",
                "Language",
                "auto",
                "UI/export language: auto, en, or zh."
            );
            DmText.SetLanguage(_language.Value);
            Log.LogInfo(DmText.Format("pluginLoading", PluginVersion));
            _uiFontSize = Config.Bind(
                "UI",
                "FontSize",
                11,
                new ConfigDescription(
                    "Base UI font size. Table row heights and fixed column widths expand when this is larger than 11.",
                    new AcceptableValueRange<int>(8, 28))
            );
            _uiScale = Config.Bind(
                "UI",
                "Scale",
                1f,
                new ConfigDescription(
                    "Additional UI scale multiplier layered on top of the automatic 1080p resolution scale.",
                    new AcceptableValueRange<float>(0.5f, 3f))
            );
            DamageMeterUiSettings.Configure(() => _uiFontSize.Value, () => _uiScale.Value);
            _autoShowInBattle = Config.Bind(
                "UI",
                "AutoShowInBattle",
                true,
                "Automatically show the overlay when combat begins."
            );
            _autoShowOutsideBattle = Config.Bind(
                "UI",
                "AutoShowOutsideBattle",
                false,
                "Automatically show the overlay when combat ends."
            );
            ApplyAutomaticVisibility(false);
            _autoStartRecording = Config.Bind(
                "Run",
                "AutoStartRecording",
                false,
                "Start run recording automatically when the plugin loads."
            );
            _exportDirectory = Config.Bind(
                "Export",
                "Directory",
                "",
                "Folder for exported TXT/CSV files. Empty uses the loaded plugin DLL folder."
            );
            _autoStartPending = _autoStartRecording.Value;
            Log.LogInfo(DmText.Format("settingsLoaded", _autoStartRecording.Value, _exportDirectory.Value, _language.Value));
            Log.LogInfo($"UI settings loaded: fontSize={DamageMeterUiSettings.FontSize}, scale={DamageMeterUiSettings.CustomScale:0.##}.");

            _floorEffectSources = new FloorEffectSourceTracker();
            _contributionTracker = new ContributionTracker(_floorEffectSources);
            _tracker = new DamageTracker(_floorEffectSources);

            _logTracker = new CombatLogTracker(_floorEffectSources);
            _logUi = new CombatLogUI(_logTracker);
            _statusLogUi = new StatusLogUI(_logTracker);

            _ui = new DamageMeterUI(_tracker, _contributionTracker, _logUi, _statusLogUi);

            _runTracker = new RunStatsTracker();
            _runUi = new RunStatsUI(_runTracker, _tracker, _contributionTracker);
            _runUi.IsBattleActive = () => _battleActive;

            _ui.OnToggleRecording = () =>
            {
                bool wasRecording = _runTracker.IsRecording;
                if (wasRecording)
                {
                    if (!CaptureLatestRemoteBattle() && _battleActive)
                    {
                        CaptureLocalBattle();
                    }
                }
                else
                {
                    _remoteBattleActive = false;
                    _lastRemoteBattleSnapshot = null;
                    _lastRemoteCapturedDigest = null;
                }
                _runTracker.ToggleRecording();
                CancelPendingPersistence();
                DeleteCurrentPersistedState();
                if (!wasRecording) _runContainsRemoteBattles = false;
            };
            _ui.OnShowRunStats = () => { _runUi.IsVisible = !_runUi.IsVisible; };
            _ui.OnExportCsv = () => { ExportRunCsv(); };
            _ui.IsRecording = () => _runTracker.IsRecording;
            _ui.BattleCount = () =>
            {
                DamageMeterMpSnapshot remote;
                bool remoteMode = DamageMeterMultiplayerApi.TryGetRemoteSnapshot(out remote);
                bool includeLocalBattle = !remoteMode && _battleActive;
                bool includeRemoteBattle = remoteMode && IsRemoteCombatActive(remote);
                return _runTracker.GetBattleCount(includeLocalBattle ? _tracker : null, includeLocalBattle ? _contributionTracker : null, includeRemoteBattle ? remote : null);
            };
            _ui.IsAutoRecordingEnabled = () => _autoStartRecording.Value;
            _ui.OnAutoRecordingChanged = enabled =>
            {
                _autoStartRecording.Value = enabled;
                Config.Save();
                _autoStartPending = enabled;
                Log.LogInfo($"Auto start recording {(enabled ? "enabled" : "disabled")}.");
                if (enabled && _eventManagerReady && _runContextActive)
                {
                    ApplyAutoStartRecording("setting changed");
                }
            };
            _ui.IsAutoShowInBattleEnabled = () => _autoShowInBattle.Value;
            _ui.OnAutoShowInBattleChanged = enabled =>
            {
                _autoShowInBattle.Value = enabled;
                Config.Save();
                Log.LogInfo($"Auto-show in battle {(enabled ? "enabled" : "disabled")}.");
            };
            _ui.IsAutoShowOutsideBattleEnabled = () => _autoShowOutsideBattle.Value;
            _ui.OnAutoShowOutsideBattleChanged = enabled =>
            {
                _autoShowOutsideBattle.Value = enabled;
                Config.Save();
                Log.LogInfo($"Auto-show outside battle {(enabled ? "enabled" : "disabled")}.");
            };
            _ui.GetExportDirectory = () => _exportDirectory.Value;
            _ui.OnExportDirectoryChanged = directory =>
            {
                _exportDirectory.Value = directory ?? "";
                Config.Save();
                try
                {
                    Log.LogInfo($"Export directory set to: {GetExportDirectory()}");
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"Export directory saved, but could not be prepared: {ex.Message}");
                }
            };
            _ui.GetLanguage = () => DmText.LanguageDisplay();
            _ui.OnLanguageChanged = language =>
            {
                _language.Value = language ?? "auto";
                DmText.SetLanguage(_language.Value);
                Config.Save();
                Log.LogInfo(DmText.Format("languageChanged", DmText.LanguageDisplay()));
            };

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{PluginName} loaded. Waiting for EventManager...");
        }

        internal bool IsEventManagerReady => _eventManagerReady;

        internal bool IsBattleActive => _battleActive;

        internal DamageTracker Tracker => _tracker;

        internal FloorEffectSourceTracker FloorEffectSources => _floorEffectSources;

        internal ContributionTracker ContributionTracker => _contributionTracker;

        internal CombatLogTracker LogTracker => _logTracker;

        internal bool IsUndoRestoreInProgress => _undoRestoreInProgress;

        internal void BeginUndoRestore(string restoreId, long epoch)
        {
            _undoRestoreInProgress = true;
            _undoRestoreId = restoreId ?? string.Empty;
            _undoRestoreEpoch = epoch;
            _lastCompletedBattleSnapshot = null;
            _lastRemoteCapturedDigest = null;
            Log.LogInfo($"DamageMeter undo bridge: begin id={_undoRestoreId}, epoch={_undoRestoreEpoch}.");
        }

        internal void EndUndoRestore(string restoreId, int combatCount, Guid combatGuid, bool combatActive, bool success)
        {
            if (!_undoRestoreInProgress)
            {
                Log.LogWarning($"DamageMeter undo bridge: end ignored id={restoreId ?? string.Empty}; no restore is active.");
                return;
            }

            bool idMatches = string.IsNullOrEmpty(_undoRestoreId) || string.Equals(_undoRestoreId, restoreId ?? string.Empty, StringComparison.Ordinal);
            if (!idMatches)
            {
                Log.LogWarning($"DamageMeter undo bridge: end id mismatch active={_undoRestoreId}, received={restoreId ?? string.Empty}.");
                return;
            }

            if (success && combatCount > 0 && combatGuid != Guid.Empty)
            {
                int removed = _runTracker.TrimToCheckpoint(combatCount, combatGuid, combatActive);
                if (removed > 0)
                    Log.LogInfo($"DamageMeter undo bridge: trimmed {removed} future battle snapshots.");
            }

            _undoRestoreInProgress = false;
            _undoRestoreId = null;
            _lastRemoteCapturedDigest = null;
            Log.LogInfo($"DamageMeter undo bridge: end success={success}, epoch={_undoRestoreEpoch}.");
        }

        private void Update()
        {
            TrackRemoteOverlayVisibility();
            HandleHotkeys();
            TrackRemoteDamageMeterRecording();
            TryShowResumePrompt();

            if (!_eventManagerReady)
            {
                _checkTimer += Time.unscaledDeltaTime;
                if (_checkTimer < 1f) return;
                _checkTimer = 0f;
                if (TryRegisterEvents())
                {
                    _eventManagerReady = true;
                    Log.LogInfo("EventManager ready, event listeners registered.");
                    ApplyAutoStartRecording("event manager ready");
                }
            }
        }

        private void ApplyAutoStartRecording(string reason)
        {
            if (!_autoStartPending || !_autoStartRecording.Value) return;
            _autoStartPending = false;

            if (_runTracker.IsRecording)
            {
                Log.LogInfo($"Auto start recording skipped ({reason}): already recording.");
                return;
            }

            _remoteBattleActive = false;
            _lastRemoteBattleSnapshot = null;
            _lastRemoteCapturedDigest = null;
            _runTracker.StartRecording();
            Log.LogInfo($"Auto start recording applied ({reason}).");
        }

        private void ApplyAutomaticVisibility(bool inBattle)
        {
            _overlayHidden = !(inBattle ? _autoShowInBattle.Value : _autoShowOutsideBattle.Value);
        }

        private void HandleHotkeys()
        {
            var input = BepInEx.UnityInput.Current;
            if (input.GetKeyDown(KeyCode.F2))
            {
                _overlayHidden = !_overlayHidden;
                Log.LogInfo($"Damage Meter overlay {(_overlayHidden ? "hidden" : "shown")}");
            }
            if (input.GetKeyDown(KeyCode.F3))
            {
                _ui.ClearRetainedStats();
                _lastCompletedBattleSnapshot = null;
                _tracker.Reset(true);
                _contributionTracker.Reset(true);
                Log.LogInfo("Damage stats reset.");
            }
            if (input.GetKeyDown(KeyCode.F4))
            {
                ExportReport();
            }
        }

        private bool TryRegisterEvents()
        {
            try
            {
                // Stats tracker
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventBattleStartRound>(evt => _floorEffectSources.OnBattleStartRound(evt.m_Round), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Skill.Events.EventSkillFinalizeResults>(evt => _floorEffectSources.OnSkillFinalizeResults(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Token.Events.EventTokenAdded>(evt => _floorEffectSources.OnTokenAdded(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Token.Events.EventTokenReplaced>(evt => _floorEffectSources.OnTokenReplaced(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Buff.Events.EventBuffAdded>(evt => _floorEffectSources.OnBuffAdded(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Dot.Events.EventDotAdded>(evt => _floorEffectSources.OnDotAdded(evt), false, 0);

                Assets.Code.Events.EventManager.AddListener<Assets.Code.Actor.Events.EventActorHealthDamage>(evt => _tracker.OnHealthDamage(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Actor.Events.EventActorHealthHeal>(evt => _tracker.OnHealthHeal(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventStressDamage>(evt => _tracker.OnStressDamage(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventStressHeal>(evt => _tracker.OnStressHeal(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Skill.Events.EventSkillFinalizeResults>(evt => _tracker.OnSkillFinalizeResults(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Actor.Events.EventActorDeath>(evt => _tracker.OnActorDeath(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventBattleBegin>(evt => OnBattleBegin(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventBattleExit>(evt => OnBattleExit(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventBattleStartRound>(evt => _tracker.OnBattleStartRound(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Dot.Events.EventDotAdded>(evt => _tracker.OnDotAdded(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Dot.Events.EventDotRemoved>(evt => _tracker.OnDotRemoved(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Dot.Events.EventDotApplied>(evt => _tracker.OnDotApplied(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Buff.Events.EventBuffAdded>(evt => _tracker.OnBuffAdded(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Buff.Events.EventBuffRemoved>(evt => _tracker.OnBuffRemoved(evt), false, 0);

                // Contribution tracker
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventBattleStartRound>(evt => _contributionTracker.OnBattleStartRound(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Skill.Events.EventSkillFinalizeResults>(evt => _contributionTracker.OnSkillFinalizeResults(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Token.Events.EventTokenAdded>(evt => _contributionTracker.OnTokenAdded(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Token.Events.EventTokenRemoved>(evt => _contributionTracker.OnTokenRemoved(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Token.Events.EventTokenConsumed>(evt => _contributionTracker.OnTokenConsumed(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Buff.Events.EventBuffAdded>(evt => _contributionTracker.OnBuffAdded(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Buff.Events.EventBuffRemoved>(evt => _contributionTracker.OnBuffRemoved(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Dot.Events.EventDotAdded>(evt => _contributionTracker.OnDotAdded(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Dot.Events.EventDotRemoved>(evt => _contributionTracker.OnDotRemoved(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Dot.Events.EventDotApplied>(evt => _contributionTracker.OnDotApplied(evt), false, 0);

                // Combat log
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventBattleBegin>(evt => _logTracker.OnBattleBegin(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventBattleExit>(evt => _logTracker.OnBattleExit(), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventBattleStartRound>(evt => _logTracker.OnBattleStartRound(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Skill.Events.EventSkillFinalizeResults>(evt => _logTracker.OnSkillFinalizeResults(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Actor.Events.EventActorHealthDamage>(evt => _logTracker.OnHealthDamage(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventStressDamage>(evt => _logTracker.OnStressDamage(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Actor.Events.EventActorDeath>(evt => _logTracker.OnActorDeath(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Dot.Events.EventDotAdded>(evt => _logTracker.OnDotAdded(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Dot.Events.EventDotRemoved>(evt => _logTracker.OnDotRemoved(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Dot.Events.EventDotApplied>(evt => _logTracker.OnDotApplied(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Token.Events.EventTokenAdded>(evt => _logTracker.OnTokenAdded(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Token.Events.EventTokenRemoved>(evt => _logTracker.OnTokenRemoved(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Token.Events.EventTokenConsumed>(evt => _logTracker.OnTokenConsumed(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Token.Events.EventTokenReplaced>(evt => _logTracker.OnTokenReplaced(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Token.Events.EventTokenNegated>(evt => _logTracker.OnTokenNegated(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Buff.Events.EventBuffAdded>(evt => _logTracker.OnBuffAdded(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Buff.Events.EventBuffRemoved>(evt => _logTracker.OnBuffRemoved(evt), false, 0);

                // Run recording persistence
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Run.Events.EventRunStarted>(evt => OnRunStarted(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Run.Events.EventRunEnded>(evt => OnRunEnded(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Utils.Serialization.Events.EventRunSaveCreated>(evt => OnRunSaveCreated(), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Utils.Serialization.Events.EventSaveCurrentGameModeStarted>(evt => OnGameSaveStarted(), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Utils.Serialization.Events.EventSaveCurrentGameModeCompleted>(evt => OnGameSaveCompleted(), false, 0);

                return true;
            }
            catch { return false; }
        }

        private void OnRunStarted(Assets.Code.Run.Events.EventRunStarted evt)
        {
            if (evt != null && evt.m_RunStartType == Assets.Code.Run.RunStartType.IN_CAMPAIGN_GAME_OVER)
            {
                _runContextActive = false;
                _gameCombatManager = evt.m_CombatManager;
                _resumeCheckpointAvailable = false;
                _awaitingNewRunIdentity = false;
                Log.LogInfo("Run recording: game-over results run detected; final statistics retained.");
                return;
            }

            _runContextActive = true;
            bool isRunLoad = evt != null && evt.m_IsRunLoad;
            bool shouldRecordFresh = _runTracker.IsRecording || _autoStartRecording.Value;
            uint loadedProfileGuid = 0;
            Guid loadedRunGuid = Guid.Empty;
            bool hasLoadedIdentity = isRunLoad && TryGetCurrentRunIdentity(out loadedProfileGuid, out loadedRunGuid);
            bool sameLoadedRun = hasLoadedIdentity && _runIdentityBound &&
                loadedProfileGuid == _currentProfileGuid && loadedRunGuid == _currentRunGuid;

            _gameCombatManager = evt == null ? null : evt.m_CombatManager;
            _resumeCheckpointAvailable = isRunLoad && _gameCombatManager != null;
            _resumeCheckpointCombatCount = _resumeCheckpointAvailable ? _gameCombatManager.CombatCount : 0;
            _resumeCheckpointCombatGuid = _resumeCheckpointAvailable ? _gameCombatManager.CombatGuid : Guid.Empty;
            _resumeCheckpointInCombat = _resumeCheckpointAvailable && evt.m_GameModeType == Assets.Code.Game.GameModeType.COMBAT;

            if (sameLoadedRun)
            {
                ClearPendingResume();
                _awaitingNewRunIdentity = false;
                if (_resumeCheckpointAvailable)
                {
                    int removed = _runTracker.TrimToCheckpoint(
                        _resumeCheckpointCombatCount,
                        _resumeCheckpointCombatGuid,
                        _resumeCheckpointInCombat);
                    if (removed > 0)
                        Log.LogWarning($"Run recording: removed {removed} in-memory battles newer than the loaded checkpoint.");
                }
                if (_resumeCheckpointAvailable && _runTracker.BattleCount == 0)
                    QueueResumeForCurrentRun();
                return;
            }

            CancelPendingPersistence();
            ClearPendingResume();
            _runIdentityBound = false;
            _currentProfileGuid = 0;
            _currentRunGuid = Guid.Empty;
            _runContainsRemoteBattles = false;
            _runTracker.ResetForRun(shouldRecordFresh);

            _awaitingNewRunIdentity = !isRunLoad;
            if (_awaitingNewRunIdentity)
            {
                Log.LogInfo("Run recording: fresh run detected; waiting for its native RunID.");
                return;
            }

            if (!hasLoadedIdentity)
            {
                Log.LogWarning("Run recording: continue detected, but the native run identity is unavailable.");
                return;
            }

            _currentProfileGuid = loadedProfileGuid;
            _currentRunGuid = loadedRunGuid;
            _runIdentityBound = true;
            QueueResumeForCurrentRun();
        }

        private void OnRunEnded(Assets.Code.Run.Events.EventRunEnded evt)
        {
            _runContextActive = false;
            if (evt == null || evt.m_RunEndType != Assets.Code.Run.RunEndType.RESET) return;

            CancelPendingPersistence();
            DeleteCurrentPersistedState();
            ClearPendingResume();
            _runTracker.StopRecording();
            _runIdentityBound = false;
            _awaitingNewRunIdentity = false;
            _currentProfileGuid = 0;
            _currentRunGuid = Guid.Empty;
            _gameCombatManager = null;
            _activeGameCombatCount = 0;
            _activeGameCombatGuid = Guid.Empty;
            _resumeCheckpointAvailable = false;
            Log.LogInfo("Run recording: ended run resume state cleared.");
        }

        private void OnRunSaveCreated()
        {
            if (!_awaitingNewRunIdentity && _runIdentityBound) return;
            if (!TryBindCurrentRunIdentity()) return;

            _awaitingNewRunIdentity = false;
            DeleteCurrentPersistedState();
            Log.LogInfo($"Run recording: bound fresh RunID {_currentRunGuid:N}.");
        }

        private bool TryBindCurrentRunIdentity()
        {
            uint profileGuid;
            Guid runGuid;
            if (!TryGetCurrentRunIdentity(out profileGuid, out runGuid)) return false;

            _currentProfileGuid = profileGuid;
            _currentRunGuid = runGuid;
            _runIdentityBound = true;
            return true;
        }

        private static bool TryGetCurrentRunIdentity(out uint profileGuid, out Guid runGuid)
        {
            profileGuid = 0;
            runGuid = Guid.Empty;
            if (!Assets.Code.Utils.Singleton<Assets.Code.Game.GameTypeMgr>.HasInstance() ||
                Assets.Code.Utils.Singleton<Assets.Code.Game.GameTypeMgr>.Instance.CurrentGameType != Assets.Code.Game.GameType.EXPEDITION)
                return false;
            if (!Assets.Code.Utils.SingletonMonoBehaviour<Assets.Code.Profile.ProfileBhv>.HasInstance(false)) return false;

            profileGuid = Assets.Code.Utils.SingletonMonoBehaviour<Assets.Code.Profile.ProfileBhv>.Instance.GetCurrentProfileGuid();
            runGuid = Assets.Code.Utils.Serialization.SaveUtils.GetRunGuid();
            if (profileGuid == 0 || runGuid == Guid.Empty) return false;
            return true;
        }

        private void EnsureRunIdentity()
        {
            uint profileGuid;
            Guid runGuid;
            if (!TryGetCurrentRunIdentity(out profileGuid, out runGuid))
            {
                if (!_runIdentityBound) return;

                bool shouldRecord = _runTracker.IsRecording || _autoStartRecording.Value;
                CancelPendingPersistence();
                ClearPendingResume();
                _runIdentityBound = false;
                _currentProfileGuid = 0;
                _currentRunGuid = Guid.Empty;
                _gameCombatManager = null;
                _activeGameCombatCount = 0;
                _activeGameCombatGuid = Guid.Empty;
                _resumeCheckpointAvailable = false;
                _runTracker.ResetForRun(shouldRecord);
                Log.LogInfo("Run recording: native Expedition RunID unavailable; persistent resume disabled for this game type.");
                return;
            }

            if (_runIdentityBound && profileGuid == _currentProfileGuid && runGuid == _currentRunGuid) return;

            bool freshRun = _awaitingNewRunIdentity;
            bool shouldRecordFresh = _runTracker.IsRecording || _autoStartRecording.Value;
            CancelPendingPersistence();
            ClearPendingResume();
            _currentProfileGuid = profileGuid;
            _currentRunGuid = runGuid;
            _runIdentityBound = true;
            _awaitingNewRunIdentity = false;
            _runContainsRemoteBattles = false;
            _runTracker.ResetForRun(shouldRecordFresh);
            if (freshRun)
                DeleteCurrentPersistedState();
            else if (_resumeCheckpointAvailable)
                QueueResumeForCurrentRun();
            else
                Log.LogWarning("Run recording: RunID changed without a loaded combat checkpoint; resume was skipped.");
        }

        private void QueueResumeForCurrentRun()
        {
            if (!_resumeCheckpointAvailable)
            {
                Log.LogWarning("Run recording: resume state ignored because the loaded combat checkpoint is unavailable.");
                return;
            }

            string path = GetCurrentPersistencePath();
            RunStatsTracker.PersistedRunState state;
            if (!RunStatsTracker.TryReadPersistedState(path, out state))
            {
                if (System.IO.File.Exists(path))
                {
                    try { System.IO.File.Copy(path, path + ".unreadable", true); }
                    catch (Exception ex) { Log.LogWarning($"Run recording: could not back up unreadable resume state: {ex.Message}"); }
                    _runTracker.ResetForRun(true);
                    Log.LogWarning("Run recording: resume state could not be read; a backup was kept and recording restarted.");
                }
                return;
            }

            if (state.ProfileGuid != _currentProfileGuid || state.RunGuid != _currentRunGuid || state.Snapshots.Count == 0)
            {
                RunStatsTracker.DeletePersistedState(path);
                return;
            }

            int removed = RunStatsTracker.TrimToCheckpoint(
                state,
                _resumeCheckpointCombatCount,
                _resumeCheckpointCombatGuid,
                _resumeCheckpointInCombat);
            if (removed > 0)
                Log.LogWarning($"Run recording: ignored {removed} battles newer than the loaded game checkpoint.");
            if (state.Snapshots.Count == 0)
            {
                _runTracker.ResetForRun(true);
                Log.LogInfo("Run recording: no saved battles remain at this checkpoint; recording restarted from here.");
                return;
            }

            _pendingResumeState = state;
            _pendingResumePath = path;
            _resumePromptShown = false;
            _runTracker.ResetForRun(false);
            Log.LogInfo($"Run recording: matching resume state found ({state.Snapshots.Count} battles).");
        }

        private void TryShowResumePrompt()
        {
            if (_pendingResumeState == null) return;
            if (_resumePromptShown)
            {
                if (_resumeDialog == null)
                    _resumeDialog = UnityEngine.Object.FindObjectOfType<Assets.Code.UI.Widgets.ConfirmationDialogBhv>();
                Assets.Code.UI.Screens.UiScreenBhv dialogScreen = _resumeDialog == null
                    ? null
                    : _resumeDialog.GetComponentInParent<Assets.Code.UI.Screens.UiScreenBhv>();
                if (dialogScreen != null && dialogScreen.IsOpen()) return;
                if (Time.unscaledTime - _resumePromptShownAt < 1f) return;
                _resumePromptShown = false;
                _resumeDialog = null;
            }
            if (!Assets.Code.Utils.SingletonMonoBehaviour<Assets.Code.UI.Managers.CommonUiBhv>.HasInstance(false)) return;

            Assets.Code.UI.Widgets.ConfirmationDialogBhv existingDialog =
                UnityEngine.Object.FindObjectOfType<Assets.Code.UI.Widgets.ConfirmationDialogBhv>();
            Assets.Code.UI.Screens.UiScreenBhv existingDialogScreen = existingDialog == null
                ? null
                : existingDialog.GetComponentInParent<Assets.Code.UI.Screens.UiScreenBhv>();
            if (existingDialogScreen != null && existingDialogScreen.IsOpen()) return;

            try
            {
                Assets.Code.Utils.SingletonMonoBehaviour<Assets.Code.UI.Managers.CommonUiBhv>.Instance.ShowConfirmationDialog(
                    Assets.Code.UI.Managers.CommonUiBhv.ConfirmationDialogType.Default,
                    DmText.T("resumeRecordingTitle"),
                    DmText.Format("resumeRecordingDescription", _pendingResumeState.Snapshots.Count),
                    new Action(ContinuePendingRunRecording),
                    DmText.T("continueRecording"),
                    new Action(StartFreshRunRecording),
                    DmText.T("startFreshRecording"),
                    Assets.Code.UI.Screens.ScreenStackBhv.Layer.Modal,
                    false);
                _resumePromptShown = true;
                _resumePromptShownAt = Time.unscaledTime;
                _resumeDialog = UnityEngine.Object.FindObjectOfType<Assets.Code.UI.Widgets.ConfirmationDialogBhv>();
            }
            catch (Exception ex)
            {
                _resumePromptShown = false;
                Log.LogWarning($"Run recording: resume prompt is not ready yet: {ex.Message}");
            }
        }

        private void ContinuePendingRunRecording()
        {
            RunStatsTracker.PersistedRunState state = _pendingResumeState;
            if (state == null) return;

            if (!_runIdentityBound || state.ProfileGuid != _currentProfileGuid || state.RunGuid != _currentRunGuid)
            {
                Log.LogWarning("Run recording: resume cancelled because the active RunID changed.");
                StartFreshRunRecording();
                return;
            }

            _runTracker.Resume(state);
            _runContainsRemoteBattles = false;
            ClearPendingResume();
        }

        private void StartFreshRunRecording()
        {
            if (_pendingResumeState == null) return;
            string path = _pendingResumePath;
            ClearPendingResume();
            CancelPendingPersistence();
            RunStatsTracker.DeletePersistedState(path);
            _runTracker.ResetForRun(true);
            _runContainsRemoteBattles = false;
            Log.LogInfo("Run recording: previous resume state discarded.");
        }

        private void ClearPendingResume()
        {
            _pendingResumeState = null;
            _pendingResumePath = null;
            _resumePromptShown = false;
            _resumePromptShownAt = 0f;
            _resumeDialog = null;
        }

        private void OnGameSaveStarted()
        {
            if (!_runContextActive) return;
            EnsureRunIdentity();
            if (_battleActive || !_runIdentityBound || _pendingResumeState != null || _runContainsRemoteBattles) return;

            RunStatsTracker.PersistedRunState state = _runTracker.CreatePersistedState(_currentProfileGuid, _currentRunGuid);
            if (state == null) return;

            lock (_persistenceLock)
            {
                _pendingPersistenceState = state;
                _pendingPersistencePath = GetCurrentPersistencePath();
                _pendingPersistenceGeneration = System.Threading.Volatile.Read(ref _persistenceGeneration);
            }
        }

        private void OnGameSaveCompleted()
        {
            RunStatsTracker.PersistedRunState state;
            string path;
            int generation;
            lock (_persistenceLock)
            {
                state = _pendingPersistenceState;
                path = _pendingPersistencePath;
                generation = _pendingPersistenceGeneration;
                _pendingPersistenceState = null;
                _pendingPersistencePath = null;
            }

            if (state == null || generation != System.Threading.Volatile.Read(ref _persistenceGeneration)) return;
            if (!RunStatsTracker.TryWritePersistedState(path, state)) return;

            if (generation != System.Threading.Volatile.Read(ref _persistenceGeneration))
            {
                RunStatsTracker.DeletePersistedState(path);
                return;
            }

            Log.LogInfo($"Run recording: resume state saved ({state.Snapshots.Count} battles).");
        }

        private void CancelPendingPersistence()
        {
            System.Threading.Interlocked.Increment(ref _persistenceGeneration);
            lock (_persistenceLock)
            {
                _pendingPersistenceState = null;
                _pendingPersistencePath = null;
            }
        }

        private string GetCurrentPersistencePath()
        {
            string directory = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "DD2DamageMeter", "runs");
            return System.IO.Path.Combine(directory, $"profile_{_currentProfileGuid}_{_currentRunGuid:N}.xml");
        }

        private void DeleteCurrentPersistedState()
        {
            if (_runIdentityBound) RunStatsTracker.DeletePersistedState(GetCurrentPersistencePath());
        }

        private void OnBattleBegin(Assets.Code.Combat.Events.EventBattleBegin evt)
        {
            _runContextActive = true;
            EnsureRunIdentity();
            // Capture previous battle stats if recording.
            // Normally the battle is captured on EventBattleExit, but this serves
            // as a fallback for edge cases where EventBattleExit did not fire.
            if (!_undoRestoreInProgress && _battleActive && _runTracker.IsRecording)
            {
                CaptureLocalBattle();
            }
            _lastCompletedBattleSnapshot = null;
            _activeGameCombatCount = _gameCombatManager == null ? 0 : _gameCombatManager.CombatCount;
            _activeGameCombatGuid = _gameCombatManager == null ? Guid.Empty : _gameCombatManager.CombatGuid;
            _battleActive = true;
            _remoteOverlayBattleActive = null;
            ApplyAutomaticVisibility(true);
            _ui.ClearRetainedStats();
            _floorEffectSources.Reset();
            _tracker.OnBattleBegin(evt);
            _contributionTracker.OnBattleBegin(evt);
        }

        private void OnBattleExit(Assets.Code.Combat.Events.EventBattleExit evt)
        {
            if (_undoRestoreInProgress)
            {
                _lastCompletedBattleSnapshot = null;
                _ui.ClearRetainedStats();
                _battleActive = false;
                ApplyAutomaticVisibility(false);
                _floorEffectSources.Reset();
                _tracker.Reset();
                _contributionTracker.Reset();
                Log.LogInfo($"Battle exited during undo restore; capture suppressed (epoch={_undoRestoreEpoch}).");
                return;
            }

            _lastCompletedBattleSnapshot = _runTracker.CreateStandaloneSnapshot(
                _tracker,
                _contributionTracker,
                _activeGameCombatCount,
                _activeGameCombatGuid);
            // Capture the battle immediately on exit so that out-of-combat events
            // (stress changes, healing in inn, etc.) do not pollute the snapshot.
            if (_battleActive && _runTracker.IsRecording)
            {
                CaptureLocalBattle();
            }
            _ui.RetainCurrentStats();
            _battleActive = false;
            ApplyAutomaticVisibility(false);
            _floorEffectSources.Reset();
            _tracker.Reset();
            _contributionTracker.Reset();
            Plugin.Log.LogInfo("Battle exited: stats captured, display retained, and trackers reset.");
        }

        private void TrackRemoteOverlayVisibility()
        {
            if (_battleActive) return;

            DamageMeterMpSnapshot snapshot;
            if (!DamageMeterMultiplayerApi.TryGetRemoteSnapshot(out snapshot) || snapshot == null || !snapshot.IsAvailable)
            {
                if (_remoteOverlayBattleActive == true) ApplyAutomaticVisibility(false);
                _remoteOverlayBattleActive = null;
                return;
            }

            if (_remoteOverlayBattleActive == snapshot.IsActive) return;
            _remoteOverlayBattleActive = snapshot.IsActive;
            ApplyAutomaticVisibility(snapshot.IsActive);
        }

        private void TrackRemoteDamageMeterRecording()
        {
            if (!_runTracker.IsRecording)
            {
                _remoteBattleActive = false;
                _lastRemoteBattleSnapshot = null;
                return;
            }

            DamageMeterMpSnapshot snapshot;
            if (!DamageMeterMultiplayerApi.TryGetRemoteSnapshot(out snapshot) || snapshot == null || !snapshot.IsAvailable)
            {
                if (_remoteBattleActive)
                {
                    CaptureLatestRemoteBattle();
                    _remoteBattleActive = false;
                }
                return;
            }

            bool active = IsRemoteCombatActive(snapshot);
            if (active)
            {
                if (_remoteBattleActive && IsLikelyNewRemoteBattle(_lastRemoteBattleSnapshot, snapshot))
                {
                    CaptureLatestRemoteBattle();
                }

                _remoteBattleActive = true;
                _lastRemoteBattleSnapshot = snapshot;
                return;
            }

            if (_remoteBattleActive)
            {
                CaptureLatestRemoteBattle();
                _remoteBattleActive = false;
                _lastRemoteBattleSnapshot = null;
            }
        }

        private bool CaptureLatestRemoteBattle()
        {
            DamageMeterMpSnapshot snapshot = _lastRemoteBattleSnapshot;
            DamageMeterMpSnapshot current;
            if (DamageMeterMultiplayerApi.TryGetRemoteSnapshot(out current) && current != null && current.IsAvailable && HasRemoteStats(current))
            {
                snapshot = current;
            }

            if (snapshot == null || !snapshot.IsAvailable || !HasRemoteStats(snapshot))
            {
                return false;
            }

            string digest = snapshot.Digest ?? "";
            if (!string.IsNullOrEmpty(digest) && string.Equals(_lastRemoteCapturedDigest, digest, StringComparison.Ordinal))
            {
                return true;
            }

            _runTracker.CaptureRemoteSnapshot(snapshot);
            _runContainsRemoteBattles = true;
            CancelPendingPersistence();
            DeleteCurrentPersistedState();
            _lastRemoteCapturedDigest = digest;
            return true;
        }

        private void CaptureLocalBattle()
        {
            _runTracker.CaptureBattle(
                _tracker,
                _contributionTracker,
                _activeGameCombatCount,
                _activeGameCombatGuid);
        }

        internal static bool IsRemoteCombatActive(DamageMeterMpSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsAvailable || !HasRemoteStats(snapshot))
            {
                return false;
            }

            string state = (snapshot.BattleState ?? "").Trim().ToLowerInvariant();
            if (state == "inactive" || state == "[none]" || state == "none")
            {
                return false;
            }

            return snapshot.IsActive || snapshot.Round > 0 || snapshot.Turn > 0;
        }

        private static bool IsLikelyNewRemoteBattle(DamageMeterMpSnapshot previous, DamageMeterMpSnapshot current)
        {
            if (previous == null || current == null || !HasRemoteStats(previous) || !HasRemoteStats(current))
            {
                return false;
            }

            float previousTotal = previous.PlayerTotalDamage + previous.EnemyTotalDamage;
            float currentTotal = current.PlayerTotalDamage + current.EnemyTotalDamage;
            if (previousTotal > 1f && currentTotal + 1f < previousTotal * 0.5f)
            {
                return true;
            }

            return previous.Round > 1 && current.Round <= 1 && current.Turn <= 1 &&
                !string.Equals(previous.Digest, current.Digest, StringComparison.Ordinal);
        }

        private static bool HasRemoteStats(DamageMeterMpSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return false;
            }

            return HasAnyRemoteRows(snapshot.Heroes) || HasAnyRemoteRows(snapshot.Enemies) || HasAnyRemoteContribution(snapshot.Contributions);
        }

        private static bool HasAnyRemoteRows(System.Collections.Generic.IList<DamageMeterMpActorStats> rows)
        {
            if (rows == null) return false;
            for (int i = 0; i < rows.Count; i++)
            {
                DamageMeterMpActorStats s = rows[i];
                if (s == null) continue;
                if (s.TotalDamageDealt > 0.01f ||
                    s.DotDamageDealt > 0.01f ||
                    s.TotalDamageReceived > 0.01f ||
                    s.RawDamageReceived > 0.01f ||
                    s.OverkillDamageDealt > 0.01f ||
                    s.TotalHealingDone > 0.01f ||
                    s.TotalHealingReceived > 0.01f ||
                    s.TotalStressReceived > 0.01f ||
                    s.Kills > 0 ||
                    s.Crits > 0 ||
                    s.IncomingAttacks > 0 ||
                    s.AvoidedAttacks > 0 ||
                    s.DodgeAvoids > 0 ||
                    s.MissAvoids > 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasAnyRemoteContribution(System.Collections.Generic.IList<DamageMeterMpContributionStats> rows)
        {
            if (rows == null) return false;
            for (int i = 0; i < rows.Count; i++)
            {
                DamageMeterMpContributionStats s = rows[i];
                if (s == null) continue;
                if (s.BonusDamage > 0.01f ||
                    s.VulnerableDamage > 0.01f ||
                    s.ShieldPrevented > 0.01f ||
                    s.GuardProtected > 0.01f ||
                    s.DotDamagePrevented > 0.01f ||
                    s.ComboApplied > 0 ||
                    s.ComboConsumed > 0)
                {
                    return true;
                }
            }
            return false;
        }

        private void OnGUI()
        {
            UiInputBlocker.ClearRects();
            if (!_eventManagerReady && !DamageMeterMultiplayerApi.HasRecentRemoteSnapshot()) return;
            if (_overlayHidden) return;
            if (_ui.IsVisible) _ui.Draw();
            if (_runUi.IsVisible) _runUi.Draw();
        }

        private void ExportReport()
        {
            try
            {
                IReadOnlyList<DamageTracker.ActorStats> playerStats;
                IReadOnlyList<DamageTracker.ActorStats> enemyStats;
                IReadOnlyList<ContributionTracker.ContributionStats> contributionStats;
                float playerTotal;
                float enemyTotal;
                if (_battleActive)
                {
                    _tracker.RefreshSnapshot();
                    _contributionTracker.RefreshSnapshot();
                    playerStats = _tracker.PlayerStats;
                    enemyStats = _tracker.EnemyStats;
                    contributionStats = _contributionTracker.PlayerStats;
                    playerTotal = _tracker.PlayerTotalDamage;
                    enemyTotal = _tracker.EnemyTotalDamage;
                }
                else if (_lastCompletedBattleSnapshot != null)
                {
                    playerStats = _lastCompletedBattleSnapshot.PlayerStats;
                    enemyStats = _lastCompletedBattleSnapshot.EnemyStats;
                    contributionStats = _lastCompletedBattleSnapshot.ContributionStats;
                    playerTotal = _lastCompletedBattleSnapshot.PlayerTotalDamage;
                    enemyTotal = _lastCompletedBattleSnapshot.EnemyTotalDamage;
                }
                else
                {
                    Log.LogInfo("No current or retained battle data to export.");
                    return;
                }

                string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = System.IO.Path.Combine(GetExportDirectory(), $"DD2_Report_{timestamp}.txt");

                using (var writer = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8))
                {
                    writer.WriteLine(DmText.T("reportTitle"));
                    writer.WriteLine(DmText.Format("generated", System.DateTime.Now));
                    writer.WriteLine();

                    // Heroes section
                    writer.WriteLine(DmText.T("sectionHeroes"));
                    writer.WriteLine(DmText.Format("totalDamage", playerTotal));
                    writer.WriteLine($"{DmText.T("name"),-22} {DmText.T("dmg"),8} {"(DOT)",7} {DmText.T("ovk"),7} {DmText.T("rawTkn"),10} {DmText.T("healOut"),7} {DmText.T("healIn"),7} {DmText.T("kills"),6} {DmText.T("crits"),6} {DmText.T("avoidCount"),7} {DmText.T("comboApplied"),8} {"%DMG",6}");
                    writer.WriteLine(new string('-', 110));
                    if (playerStats != null)
                    {
                        foreach (var s in playerStats)
                        {
                            float pct = playerTotal > 0 ? s.TotalDamageDealt / playerTotal * 100f : 0f;
                            string dotStr = s.DotDamageDealt > 0.5f ? $"({s.DotDamageDealt:F0})" : "-";
                            string ovkStr = s.OverkillDamageDealt > 0.5f ? $"{s.OverkillDamageDealt:F0}" : "-";
                            string takenStr = UiUtil.FormatDamageTaken(s.RawDamageReceived, s.TotalDamageReceived);
                            string avoidStr = s.AvoidedAttacks > 0 ? s.AvoidedAttacks.ToString() : "-";
                            int comboApplied = GetComboAppliedForActor(contributionStats, s);
                            writer.WriteLine($"{s.ActorName,-22} {s.TotalDamageDealt,8:F0} {dotStr,7} {ovkStr,7} {takenStr,10} {s.TotalHealingDone,7:F0} {s.TotalHealingReceived,7:F0} {s.Kills,6} {s.Crits,6} {avoidStr,7} {comboApplied,8} {pct,5:F1}%");
                        }
                    }
                    writer.WriteLine();

                    WriteSkillStressHealReport(writer, playerStats);

                    // Enemies section
                    writer.WriteLine(DmText.T("sectionEnemies"));
                    writer.WriteLine(DmText.Format("totalDamage", enemyTotal));
                    writer.WriteLine($"{DmText.T("name"),-22} {DmText.T("dmg"),8} {"(DOT)",7} {DmText.T("ovk"),7} {DmText.T("rawTkn"),10} {DmText.T("healOut"),7} {DmText.T("healIn"),7} {DmText.T("kills"),6} {DmText.T("crits"),6} {DmText.T("avoidCount"),7} {"%DMG",6}");
                    writer.WriteLine(new string('-', 101));
                    if (enemyStats != null)
                    {
                        foreach (var s in enemyStats)
                        {
                            float pct = enemyTotal > 0 ? s.TotalDamageDealt / enemyTotal * 100f : 0f;
                            string dotStr = s.DotDamageDealt > 0.5f ? $"({s.DotDamageDealt:F0})" : "-";
                            string ovkStr = s.OverkillDamageDealt > 0.5f ? $"{s.OverkillDamageDealt:F0}" : "-";
                            string takenStr = UiUtil.FormatDamageTaken(s.RawDamageReceived, s.TotalDamageReceived);
                            string avoidStr = s.AvoidedAttacks > 0 ? s.AvoidedAttacks.ToString() : "-";
                            writer.WriteLine($"{s.ActorName,-22} {s.TotalDamageDealt,8:F0} {dotStr,7} {ovkStr,7} {takenStr,10} {s.TotalHealingDone,7:F0} {s.TotalHealingReceived,7:F0} {s.Kills,6} {s.Crits,6} {avoidStr,7} {pct,5:F1}%");
                        }
                    }
                    writer.WriteLine();

                    bool hasContribution = false;
                    float totalContribution = 0f;
                    if (contributionStats != null)
                    {
                        foreach (var s in contributionStats)
                        {
                            totalContribution += s.TotalContribution;
                            if (s.TotalContribution > 0.01f || s.ComboConsumed > 0)
                                hasContribution = true;
                        }
                    }
                    writer.WriteLine(DmText.T("contribution"));
                    writer.WriteLine($"{DmText.T("name"),-22} {DmText.T("contrib"),8} {DmText.T("dmgPlus"),8} {DmText.T("vulnerable"),8} {DmText.T("shield"),8} {DmText.T("guard"),8} {DmText.T("dotPrevented"),8} {DmText.T("comboConsumed"),8} {DmText.T("pct"),6}");
                    writer.WriteLine(new string('-', 92));
                    if (hasContribution)
                    {
                        foreach (var s in contributionStats)
                        {
                            if (s.TotalContribution <= 0.01f && s.ComboConsumed <= 0) continue;
                            float pct = totalContribution > 0 ? s.TotalContribution / totalContribution * 100f : 0f;
                            writer.WriteLine($"{s.ActorName,-22} {s.TotalContribution,8:F1} {s.BonusDamage,8:F1} {s.VulnerableDamage,8:F1} {s.ShieldPrevented,8:F1} {s.GuardProtected,8:F1} {s.DotDamagePrevented,8:F1} {s.ComboConsumed,8} {pct,5:F1}%");
                        }
                    }
                    else
                    {
                        writer.WriteLine(DmText.T("noContribution"));
                    }
                    writer.WriteLine();

                    // Combat log section
                    writer.WriteLine(DmText.T("sectionBattleLog"));
                    var entries = _logTracker.Entries;
                    foreach (var entry in entries)
                    {
                        if (entry is CombatLogTracker.RoundHeader rh)
                        {
                            writer.WriteLine(DmText.Format("round", rh.Round));
                        }
                        else if (entry is CombatLogTracker.LogEntry le)
                        {
                            string src = string.IsNullOrEmpty(le.SourceName) ? "" : le.SourceName;
                            string tgt = le.TargetName ?? "?";
                            string action = DmText.ActionLabel(le.ActionType, le.Value, le.DotType);
                            string extra = !string.IsNullOrEmpty(le.Extra) ? $" {le.Extra}" : "";
                            string skill = !string.IsNullOrEmpty(le.SkillId) ? $" [{le.SkillId}]" : "";
                            writer.WriteLine($"  {src,-22} {action,-16} -> {tgt,-22}{skill}{extra}");
                        }
                    }

                    var statusTotals = _logTracker.GetStatusTotalsSnapshot();
                    if (_logTracker.HasStatusLogEntries())
                    {
                        writer.WriteLine();
                        if (statusTotals.HasAny)
                        {
                            writer.WriteLine(DmText.T("sectionStatusSummary"));
                            writer.WriteLine(DmText.Format("statusSummary",
                                statusTotals.PlayerBuffApplied,
                                statusTotals.PlayerDebuffApplied,
                                statusTotals.PlayerStatusRemoved,
                                statusTotals.PlayerStatusConsumed,
                                statusTotals.EnemyBuffApplied,
                                statusTotals.EnemyDebuffApplied,
                                statusTotals.EnemyStatusRemoved,
                                statusTotals.EnemyStatusConsumed));
                            writer.WriteLine();
                        }
                        writer.WriteLine(DmText.T("sectionStatusLog"));
                        foreach (var entry in _logTracker.StatusEntries)
                        {
                            if (entry is CombatLogTracker.RoundHeader rh)
                            {
                                writer.WriteLine(DmText.Format("round", rh.Round));
                            }
                            else if (entry is CombatLogTracker.LogEntry le)
                            {
                                string src = string.IsNullOrEmpty(le.SourceName) ? "" : le.SourceName;
                                string tgt = le.TargetName ?? "?";
                                string action = DmText.ActionLabel(le.ActionType, le.Value, le.DotType);
                                string extra = !string.IsNullOrEmpty(le.Extra) ? $" {le.Extra}" : "";
                                string skill = !string.IsNullOrEmpty(le.SkillId) ? $" [{le.SkillId}]" : "";
                                writer.WriteLine($"  {src,-22} {action,-16} -> {tgt,-22}{skill}{extra}");
                            }
                        }
                    }
                }

                Log.LogInfo($"Report exported to: {path}");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Export failed: {ex.Message}");
            }
        }

        private static int GetComboAppliedForActor(IReadOnlyList<ContributionTracker.ContributionStats> stats, DamageTracker.ActorStats actor)
        {
            if (stats == null || actor == null) return 0;
            for (int i = 0; i < stats.Count; i++)
            {
                ContributionTracker.ContributionStats row = stats[i];
                if (row == null) continue;
                if (row.ActorGuid == actor.ActorGuid)
                {
                    return row.ComboApplied;
                }
            }

            for (int i = 0; i < stats.Count; i++)
            {
                ContributionTracker.ContributionStats row = stats[i];
                if (row == null) continue;
                if (!string.IsNullOrEmpty(row.ActorName) &&
                    !string.IsNullOrEmpty(actor.ActorName) &&
                    string.Equals(row.ActorName, actor.ActorName, StringComparison.OrdinalIgnoreCase))
                {
                    return row.ComboApplied;
                }
            }

            return 0;
        }

        private static void WriteSkillStressHealReport(System.IO.StreamWriter writer, IReadOnlyList<DamageTracker.ActorStats> playerStats)
        {
            if (!HasSkillStressHealStats(playerStats)) return;

            writer.WriteLine(DmText.T("sectionSkillStressHeal"));
            writer.WriteLine($"{DmText.T("name"),-22} {DmText.T("skillStressHealCount"),8} {DmText.T("skillStressHeal"),8}");
            writer.WriteLine(new string('-', 42));
            for (int i = 0; i < playerStats.Count; i++)
            {
                DamageTracker.ActorStats s = playerStats[i];
                if (s == null || (s.SkillStressHealReceived <= 0.01f && s.SkillStressHealReceivedCount <= 0)) continue;
                writer.WriteLine($"{s.ActorName,-22} {s.SkillStressHealReceivedCount,8} {s.SkillStressHealReceived,8:F1}");
            }
            writer.WriteLine();
        }

        private static bool HasSkillStressHealStats(IReadOnlyList<DamageTracker.ActorStats> playerStats)
        {
            if (playerStats == null) return false;
            for (int i = 0; i < playerStats.Count; i++)
            {
                DamageTracker.ActorStats s = playerStats[i];
                if (s != null && (s.SkillStressHealReceived > 0.01f || s.SkillStressHealReceivedCount > 0))
                {
                    return true;
                }
            }
            return false;
        }

        private void ExportRunCsv()
        {
            try
            {
                DamageMeterMpSnapshot remote;
                bool remoteMode = DamageMeterMultiplayerApi.TryGetRemoteSnapshot(out remote);
                bool includeLocalBattle = !remoteMode && _battleActive;
                bool includeRemoteBattle = remoteMode && IsRemoteCombatActive(remote);
                DamageTracker currentTracker = includeLocalBattle ? _tracker : null;
                ContributionTracker currentContribution = includeLocalBattle ? _contributionTracker : null;
                DamageMeterMpSnapshot currentRemote = includeRemoteBattle ? remote : null;
                int battleCount = _runTracker.GetBattleCount(currentTracker, currentContribution, currentRemote);
                if (battleCount == 0)
                {
                    Log.LogInfo("No run data to export.");
                    return;
                }
                string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = System.IO.Path.Combine(GetExportDirectory(), $"DD2_Run_{timestamp}.csv");
                string stressPath = System.IO.Path.Combine(GetExportDirectory(), $"DD2_Run_{timestamp}_StressRelief.csv");
                _runTracker.ExportCsv(path, currentTracker, currentContribution, currentRemote);
                _runTracker.ExportStressReliefCsv(stressPath, currentTracker, currentContribution, currentRemote);
                Log.LogInfo($"Run CSV exported to: {path}");
                Log.LogInfo($"Stress relief CSV exported to: {stressPath}");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"ExportRunCsv failed: {ex.Message}");
            }
        }

        private string GetExportDirectory()
        {
            string fallback = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            string configured = _exportDirectory.Value?.Trim();
            if (!string.IsNullOrEmpty(configured)) configured = configured.Trim('"');
            string directory = string.IsNullOrWhiteSpace(configured)
                ? fallback
                : Environment.ExpandEnvironmentVariables(configured);

            if (!System.IO.Path.IsPathRooted(directory))
            {
                directory = System.IO.Path.Combine(fallback, directory);
            }

            System.IO.Directory.CreateDirectory(directory);
            return directory;
        }

        private void OnDestroy()
        {
            // Capture last battle if recording
            if (_runTracker.IsRecording)
            {
                if (!CaptureLatestRemoteBattle() && _battleActive)
                {
                    CaptureLocalBattle();
                }
            }
            _harmony?.UnpatchSelf();
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }
            Log.LogInfo($"{PluginName} unloaded.");
        }
    }
}
