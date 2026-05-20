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
        private const string PluginVersion = "1.4.2";

        internal static ManualLogSource Log;
        internal static Plugin Instance { get; private set; }

        internal static string Version => PluginVersion;

        private Harmony _harmony;
        private DamageTracker _tracker;
        private DamageMeterUI _ui;
        private CombatLogTracker _logTracker;
        private CombatLogUI _logUi;
        private StatusLogUI _statusLogUi;
        private RunStatsTracker _runTracker;
        private RunStatsUI _runUi;
        private ContributionTracker _contributionTracker;
        private ConfigEntry<bool> _autoStartRecording;
        private ConfigEntry<string> _exportDirectory;
        private ConfigEntry<string> _language;
        private bool _eventManagerReady;
        private float _checkTimer;
        private bool _battleActive;
        private bool _overlayHidden;
        private bool _autoStartPending;
        private bool _remoteBattleActive;
        private DamageMeterMpSnapshot _lastRemoteBattleSnapshot;
        private string _lastRemoteCapturedDigest;

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

            _contributionTracker = new ContributionTracker();
            _tracker = new DamageTracker();
            _ui = new DamageMeterUI(_tracker, _contributionTracker);

            _logTracker = new CombatLogTracker();
            _logUi = new CombatLogUI(_logTracker);
            _statusLogUi = new StatusLogUI(_logTracker);

            _runTracker = new RunStatsTracker();
            _runUi = new RunStatsUI(_runTracker, _tracker, _contributionTracker);

            _ui.OnToggleLog = () => { _logUi.IsVisible = !_logUi.IsVisible; };
            _logUi.OnToggleStatusLog = () => { _statusLogUi.IsVisible = !_statusLogUi.IsVisible; };
            _ui.OnToggleRecording = () =>
            {
                bool wasRecording = _runTracker.IsRecording;
                if (wasRecording)
                {
                    if (!CaptureLatestRemoteBattle())
                    {
                        _runTracker.CaptureBattle(_tracker, _contributionTracker);
                    }
                }
                else
                {
                    _remoteBattleActive = false;
                    _lastRemoteBattleSnapshot = null;
                    _lastRemoteCapturedDigest = null;
                }
                _runTracker.ToggleRecording();
            };
            _ui.OnShowRunStats = () => { _runUi.IsVisible = !_runUi.IsVisible; };
            _ui.OnExportCsv = () => { ExportRunCsv(); };
            _ui.IsRecording = () => _runTracker.IsRecording;
            _ui.BattleCount = () =>
            {
                DamageMeterMpSnapshot remote;
                bool remoteMode = DamageMeterMultiplayerApi.TryGetRemoteSnapshot(out remote);
                return _runTracker.GetBattleCount(remoteMode ? null : _tracker, remoteMode ? null : _contributionTracker, remoteMode ? remote : null);
            };
            _ui.IsAutoRecordingEnabled = () => _autoStartRecording.Value;
            _ui.OnAutoRecordingChanged = enabled =>
            {
                _autoStartRecording.Value = enabled;
                Config.Save();
                _autoStartPending = enabled;
                Log.LogInfo($"Auto start recording {(enabled ? "enabled" : "disabled")}.");
                if (enabled && _eventManagerReady)
                {
                    ApplyAutoStartRecording("setting changed");
                }
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

        internal ContributionTracker ContributionTracker => _contributionTracker;

        internal CombatLogTracker LogTracker => _logTracker;

        private void Update()
        {
            HandleHotkeys();
            TrackRemoteDamageMeterRecording();

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
                _tracker.Reset();
                _contributionTracker.Reset();
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
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Actor.Events.EventActorHealthDamage>(evt => _tracker.OnHealthDamage(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Actor.Events.EventActorHealthHeal>(evt => _tracker.OnHealthHeal(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventStressDamage>(evt => _tracker.OnStressDamage(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Skill.Events.EventSkillFinalizeResults>(evt => _tracker.OnSkillFinalizeResults(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Actor.Events.EventActorDeath>(evt => _tracker.OnActorDeath(evt), false, 0);
                Assets.Code.Events.EventManager.AddListener<Assets.Code.Combat.Events.EventBattleBegin>(evt => OnBattleBegin(evt), false, 0);
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

                return true;
            }
            catch { return false; }
        }

        private void OnBattleBegin(Assets.Code.Combat.Events.EventBattleBegin evt)
        {
            // Capture previous battle stats if recording
            if (_battleActive && _runTracker.IsRecording)
            {
                _runTracker.CaptureBattle(_tracker, _contributionTracker);
            }
            _battleActive = true;
            _tracker.OnBattleBegin(evt);
            _contributionTracker.OnBattleBegin(evt);
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
            _lastRemoteCapturedDigest = digest;
            return true;
        }

        private static bool IsRemoteCombatActive(DamageMeterMpSnapshot snapshot)
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
            if (!_eventManagerReady && !DamageMeterMultiplayerApi.HasRecentRemoteSnapshot()) return;
            if (_overlayHidden) return;
            if (_ui.IsVisible) _ui.Draw();
            if (_logUi.IsVisible) _logUi.Draw();
            if (_statusLogUi.IsVisible) _statusLogUi.Draw();
            if (_runUi.IsVisible) _runUi.Draw();
        }

        private void ExportReport()
        {
            try
            {
                _tracker.RefreshSnapshot();
                _contributionTracker.RefreshSnapshot();
                string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = System.IO.Path.Combine(GetExportDirectory(), $"DD2_Report_{timestamp}.txt");

                using (var writer = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8))
                {
                    var contributionStats = _contributionTracker.PlayerStats;
                    writer.WriteLine(DmText.T("reportTitle"));
                    writer.WriteLine(DmText.Format("generated", System.DateTime.Now));
                    writer.WriteLine();

                    // Heroes section
                    var playerStats = _tracker.PlayerStats;
                    float playerTotal = _tracker.PlayerTotalDamage;
                    writer.WriteLine(DmText.T("sectionHeroes"));
                    writer.WriteLine(DmText.Format("totalDamage", playerTotal));
                    writer.WriteLine($"{DmText.T("name"),-22} {DmText.T("dmg"),8} {"(DOT)",7} {DmText.T("ovk"),7} {DmText.T("rawTkn"),10} {DmText.T("healOut"),7} {DmText.T("healIn"),7} {DmText.T("kills"),6} {DmText.T("crits"),6} {DmText.T("avoidPct"),7} {DmText.T("comboApplied"),8} {"%DMG",6}");
                    writer.WriteLine(new string('-', 110));
                    if (playerStats != null)
                    {
                        foreach (var s in playerStats)
                        {
                            float pct = playerTotal > 0 ? s.TotalDamageDealt / playerTotal * 100f : 0f;
                            string dotStr = s.DotDamageDealt > 0.5f ? $"({s.DotDamageDealt:F0})" : "-";
                            string ovkStr = s.OverkillDamageDealt > 0.5f ? $"{s.OverkillDamageDealt:F0}" : "-";
                            string takenStr = UiUtil.FormatDamageTaken(s.RawDamageReceived, s.TotalDamageReceived);
                            string avoidStr = UiUtil.FormatAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks);
                            int comboApplied = GetComboAppliedForActor(contributionStats, s);
                            writer.WriteLine($"{s.ActorName,-22} {s.TotalDamageDealt,8:F0} {dotStr,7} {ovkStr,7} {takenStr,10} {s.TotalHealingDone,7:F0} {s.TotalHealingReceived,7:F0} {s.Kills,6} {s.Crits,6} {avoidStr,7} {comboApplied,8} {pct,5:F1}%");
                        }
                    }
                    writer.WriteLine();

                    // Enemies section
                    var enemyStats = _tracker.EnemyStats;
                    float enemyTotal = _tracker.EnemyTotalDamage;
                    writer.WriteLine(DmText.T("sectionEnemies"));
                    writer.WriteLine(DmText.Format("totalDamage", enemyTotal));
                    writer.WriteLine($"{DmText.T("name"),-22} {DmText.T("dmg"),8} {"(DOT)",7} {DmText.T("ovk"),7} {DmText.T("rawTkn"),10} {DmText.T("healOut"),7} {DmText.T("healIn"),7} {DmText.T("kills"),6} {DmText.T("crits"),6} {DmText.T("avoidPct"),7} {"%DMG",6}");
                    writer.WriteLine(new string('-', 101));
                    if (enemyStats != null)
                    {
                        foreach (var s in enemyStats)
                        {
                            float pct = enemyTotal > 0 ? s.TotalDamageDealt / enemyTotal * 100f : 0f;
                            string dotStr = s.DotDamageDealt > 0.5f ? $"({s.DotDamageDealt:F0})" : "-";
                            string ovkStr = s.OverkillDamageDealt > 0.5f ? $"{s.OverkillDamageDealt:F0}" : "-";
                            string takenStr = UiUtil.FormatDamageTaken(s.RawDamageReceived, s.TotalDamageReceived);
                            string avoidStr = UiUtil.FormatAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks);
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
                    writer.WriteLine($"{DmText.T("name"),-22} {DmText.T("contrib"),8} {DmText.T("dmgPlus"),8} {DmText.T("vulnerable"),8} {DmText.T("shield"),8} {DmText.T("guard"),8} {DmText.T("comboConsumed"),8} {DmText.T("pct"),6}");
                    writer.WriteLine(new string('-', 82));
                    if (hasContribution)
                    {
                        foreach (var s in contributionStats)
                        {
                            if (s.TotalContribution <= 0.01f && s.ComboConsumed <= 0) continue;
                            float pct = totalContribution > 0 ? s.TotalContribution / totalContribution * 100f : 0f;
                            writer.WriteLine($"{s.ActorName,-22} {s.TotalContribution,8:F1} {s.BonusDamage,8:F1} {s.VulnerableDamage,8:F1} {s.ShieldPrevented,8:F1} {s.GuardProtected,8:F1} {s.ComboConsumed,8} {pct,5:F1}%");
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

        private void ExportRunCsv()
        {
            try
            {
                DamageMeterMpSnapshot remote;
                bool remoteMode = DamageMeterMultiplayerApi.TryGetRemoteSnapshot(out remote);
                int battleCount = _runTracker.GetBattleCount(remoteMode ? null : _tracker, remoteMode ? null : _contributionTracker, remoteMode ? remote : null);
                if (battleCount == 0)
                {
                    Log.LogInfo("No run data to export.");
                    return;
                }
                string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = System.IO.Path.Combine(GetExportDirectory(), $"DD2_Run_{timestamp}.csv");
                _runTracker.ExportCsv(path, remoteMode ? null : _tracker, remoteMode ? null : _contributionTracker, remoteMode ? remote : null);
                Log.LogInfo($"Run CSV exported to: {path}");
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
                    _runTracker.CaptureBattle(_tracker, _contributionTracker);
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
