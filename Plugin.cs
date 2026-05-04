using System;
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
        private const string PluginVersion = "1.3.0";

        internal static ManualLogSource Log;
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
        private bool _eventManagerReady;
        private float _checkTimer;
        private bool _battleActive;
        private bool _overlayHidden;
        private bool _autoStartPending;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} v{PluginVersion} loading...");
            DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;

            Config.SaveOnConfigSet = true;
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
            Log.LogInfo($"Settings loaded: AutoStartRecording={_autoStartRecording.Value}, ExportDirectory='{_exportDirectory.Value}'");

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
                if (_runTracker.IsRecording) _runTracker.CaptureBattle(_tracker, _contributionTracker);
                _runTracker.ToggleRecording();
            };
            _ui.OnShowRunStats = () => { _runUi.IsVisible = !_runUi.IsVisible; };
            _ui.OnExportCsv = () => { ExportRunCsv(); };
            _ui.IsRecording = () => _runTracker.IsRecording;
            _ui.BattleCount = () => _runTracker.GetBattleCount(_tracker, _contributionTracker);
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

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{PluginName} loaded. Waiting for EventManager...");
        }

        private void Update()
        {
            HandleHotkeys();

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

        private void OnGUI()
        {
            if (!_eventManagerReady) return;
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
                    writer.WriteLine("=== DD2 Damage Meter Report ===");
                    writer.WriteLine($"Generated: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine();

                    // Heroes section
                    var playerStats = _tracker.PlayerStats;
                    float playerTotal = _tracker.PlayerTotalDamage;
                    writer.WriteLine("--- Heroes ---");
                    writer.WriteLine($"Total Damage: {playerTotal:F0}");
                    writer.WriteLine($"{"Name",-22} {"DMG",8} {"(DOT)",7} {"OVK",7} {"RawTkn",10} {"Heal+",7} {"HealIn",7} {"Kills",6} {"Crits",6} {"Avoid%",7} {"%DMG",6}");
                    writer.WriteLine(new string('-', 101));
                    if (playerStats != null)
                    {
                        foreach (var s in playerStats)
                        {
                            float pct = playerTotal > 0 ? s.TotalDamageDealt / playerTotal * 100f : 0f;
                            string dotStr = s.DotDamageDealt > 0.5f ? $"({s.DotDamageDealt:F0})" : "-";
                            string ovkStr = s.OverkillDamageDealt > 0.5f ? $"{s.OverkillDamageDealt:F0}" : "-";
                            string takenStr = UiUtil.FormatDamageTaken(s.RawDamageReceived, s.TotalDamageReceived);
                            string avoidStr = UiUtil.FormatAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks);
                            writer.WriteLine($"{s.ActorName,-22} {s.TotalDamageDealt,8:F0} {dotStr,7} {ovkStr,7} {takenStr,10} {s.TotalHealingDone,7:F0} {s.TotalHealingReceived,7:F0} {s.Kills,6} {s.Crits,6} {avoidStr,7} {pct,5:F1}%");
                        }
                    }
                    writer.WriteLine();

                    // Enemies section
                    var enemyStats = _tracker.EnemyStats;
                    float enemyTotal = _tracker.EnemyTotalDamage;
                    writer.WriteLine("--- Enemies ---");
                    writer.WriteLine($"Total Damage: {enemyTotal:F0}");
                    writer.WriteLine($"{"Name",-22} {"DMG",8} {"(DOT)",7} {"OVK",7} {"RawTkn",10} {"Heal+",7} {"HealIn",7} {"Kills",6} {"Crits",6} {"Avoid%",7} {"%DMG",6}");
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

                    var contributionStats = _contributionTracker.PlayerStats;
                    bool hasContribution = false;
                    float totalContribution = 0f;
                    if (contributionStats != null)
                    {
                        foreach (var s in contributionStats)
                        {
                            totalContribution += s.TotalContribution;
                            if (s.TotalContribution > 0.01f || s.ShieldWasted > 0)
                                hasContribution = true;
                        }
                    }
                    writer.WriteLine("--- Contribution ---");
                    writer.WriteLine($"{"Name",-22} {"Contrib",8} {"Dmg+",8} {"Shield",8} {"Guard",8} {"Waste",6} {"%",6}");
                    writer.WriteLine(new string('-', 73));
                    if (hasContribution)
                    {
                        foreach (var s in contributionStats)
                        {
                            if (s.TotalContribution <= 0.01f && s.ShieldWasted <= 0) continue;
                            float pct = totalContribution > 0 ? s.TotalContribution / totalContribution * 100f : 0f;
                            writer.WriteLine($"{s.ActorName,-22} {s.TotalContribution,8:F1} {s.BonusDamage,8:F1} {s.ShieldPrevented,8:F1} {s.GuardProtected,8:F1} {s.ShieldWasted,6} {pct,5:F1}%");
                        }
                    }
                    else
                    {
                        writer.WriteLine("No contribution recorded.");
                    }
                    writer.WriteLine();

                    // Combat log section
                    writer.WriteLine("--- Battle Log ---");
                    var entries = _logTracker.Entries;
                    foreach (var entry in entries)
                    {
                        if (entry is CombatLogTracker.RoundHeader rh)
                        {
                            writer.WriteLine($"--- Round {rh.Round} ---");
                        }
                        else if (entry is CombatLogTracker.LogEntry le)
                        {
                            string src = string.IsNullOrEmpty(le.SourceName) ? "" : le.SourceName;
                            string tgt = le.TargetName ?? "?";
                            string action;
                            switch (le.ActionType)
                            {
                                case "DMG": action = $"-{le.Value:F0}"; break;
                                case "CRIT": action = $"CRIT {le.Value:F0}"; break;
                                case "HEAL": action = $"+{le.Value:F0}"; break;
                                case "DOT":
                                    string dotName = string.IsNullOrEmpty(le.DotType) ? "DOT" : le.DotType;
                                    action = $"{dotName} {le.Value:F0}";
                                    break;
                                case "KILL": action = "KILL"; break;
                                case "DEATH": action = "DEATH"; break;
                                case "STRESS": action = $"STRESS {le.Value:F1}"; break;
                                case "BUFF+": action = "BUFF +"; break;
                                case "BUFF-": action = "BUFF -"; break;
                                case "BUFF!": action = "BUFF USED"; break;
                                case "DEBUFF+": action = "DEBUFF +"; break;
                                case "DEBUFF-": action = "DEBUFF -"; break;
                                case "DEBUFF!": action = "DEBUFF USED"; break;
                                case "TOKEN+": action = "TOKEN +"; break;
                                case "TOKEN-": action = "TOKEN -"; break;
                                case "TOKEN!": action = "TOKEN USED"; break;
                                case "TOKEN~": action = "SWAP"; break;
                                case "TOKENx": action = "NEGATE"; break;
                                case "STATUS+": action = "STATUS +"; break;
                                case "STATUS-": action = "STATUS -"; break;
                                default: action = le.ActionType; break;
                            }
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
                            writer.WriteLine("--- Buff/Debuff Summary ---");
                            writer.WriteLine($"Heroes: Buff+ {statusTotals.PlayerBuffApplied}, Debuff+ {statusTotals.PlayerDebuffApplied}, Removed {statusTotals.PlayerStatusRemoved}, Used {statusTotals.PlayerStatusConsumed}");
                            writer.WriteLine($"Enemies: Buff+ {statusTotals.EnemyBuffApplied}, Debuff+ {statusTotals.EnemyDebuffApplied}, Removed {statusTotals.EnemyStatusRemoved}, Used {statusTotals.EnemyStatusConsumed}");
                            writer.WriteLine();
                        }
                        writer.WriteLine("--- Buff/Debuff Log ---");
                        foreach (var entry in _logTracker.StatusEntries)
                        {
                            if (entry is CombatLogTracker.RoundHeader rh)
                            {
                                writer.WriteLine($"--- Round {rh.Round} ---");
                            }
                            else if (entry is CombatLogTracker.LogEntry le)
                            {
                                string src = string.IsNullOrEmpty(le.SourceName) ? "" : le.SourceName;
                                string tgt = le.TargetName ?? "?";
                                string action;
                                switch (le.ActionType)
                                {
                                    case "BUFF+": action = "BUFF +"; break;
                                    case "BUFF-": action = "BUFF -"; break;
                                    case "BUFF!": action = "BUFF USED"; break;
                                    case "DEBUFF+": action = "DEBUFF +"; break;
                                    case "DEBUFF-": action = "DEBUFF -"; break;
                                    case "DEBUFF!": action = "DEBUFF USED"; break;
                                    case "TOKEN+": action = "TOKEN +"; break;
                                    case "TOKEN-": action = "TOKEN -"; break;
                                    case "TOKEN!": action = "TOKEN USED"; break;
                                    case "TOKEN~": action = "SWAP"; break;
                                    case "TOKENx": action = "NEGATE"; break;
                                    case "STATUS+": action = "STATUS +"; break;
                                    case "STATUS-": action = "STATUS -"; break;
                                    default: action = le.ActionType; break;
                                }
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

        private void ExportRunCsv()
        {
            try
            {
                int battleCount = _runTracker.GetBattleCount(_tracker, _contributionTracker);
                if (battleCount == 0)
                {
                    Log.LogInfo("No run data to export.");
                    return;
                }
                string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = System.IO.Path.Combine(GetExportDirectory(), $"DD2_Run_{timestamp}.csv");
                _runTracker.ExportCsv(path, _tracker, _contributionTracker);
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
            if (_battleActive && _runTracker.IsRecording)
            {
                _runTracker.CaptureBattle(_tracker, _contributionTracker);
            }
            _harmony?.UnpatchSelf();
            Log.LogInfo($"{PluginName} unloaded.");
        }
    }
}
