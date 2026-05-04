using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DD2DamageMeter
{
    // Stores snapshots of ActorStats at the end of each battle for run-level aggregation
    public class RunStatsTracker
    {
        public class BattleSnapshot
        {
            public int BattleIndex;
            public DateTime Timestamp;
            public List<DamageTracker.ActorStats> PlayerStats;
            public List<DamageTracker.ActorStats> EnemyStats;
            public List<ContributionTracker.ContributionStats> ContributionStats;
            public float PlayerTotalDamage;
            public float EnemyTotalDamage;
        }

        // Accumulated merged stats across all recorded battles
        public class MergedStats
        {
            public string ActorName;
            public uint ActorGuid;
            public int TeamIndex;
            public int BattlesSeen;
            public float TotalDamageDealt;
            public float TotalDamageReceived;
            public float RawDamageReceived;
            public float OverkillDamageDealt;
            public float TotalHealingDone;
            public float TotalHealingReceived;
            public float TotalStressReceived;
            public int Kills;
            public int Crits;
            public int IncomingAttacks;
            public int AvoidedAttacks;
            public int DodgeAvoids;
            public int MissAvoids;
            public float DotDamageDealt;
            public float DotDamageReceived;
            public float BonusDamageContribution;
            public float ShieldContribution;
            public float GuardContribution;
            public int ShieldWasted;
            public float TotalContribution => BonusDamageContribution + ShieldContribution + GuardContribution;
        }

        private readonly List<BattleSnapshot> _snapshots = new List<BattleSnapshot>();
        private readonly object _lock = new object();
        private bool _isRecording;
        private int _battleCounter;

        public bool IsRecording => _isRecording;
        public int BattleCount => _snapshots.Count;

        public int GetBattleCount(DamageTracker currentTracker = null, ContributionTracker currentContribution = null)
        {
            lock (_lock)
            {
                int count = _snapshots.Count;
                if (TryCreateCurrentSnapshot(currentTracker, currentContribution, out _)) count++;
                return count;
            }
        }

        public void ToggleRecording()
        {
            lock (_lock)
            {
                if (_isRecording)
                {
                    StopRecording();
                }
                else
                {
                    StartRecording();
                }
            }
        }

        public void StartRecording()
        {
            lock (_lock)
            {
                if (_isRecording) return;
                _snapshots.Clear();
                _battleCounter = 0;
                _isRecording = true;
                Plugin.Log.LogInfo("RunStatsTracker: Started recording.");
            }
        }

        public void StopRecording()
        {
            lock (_lock)
            {
                if (!_isRecording) return;
                _isRecording = false;
                Plugin.Log.LogInfo($"RunStatsTracker: Stopped recording ({_snapshots.Count} battles captured).");
            }
        }

        public void CaptureBattle(DamageTracker tracker, ContributionTracker contributionTracker = null)
        {
            if (!_isRecording) return;
            try
            {
                lock (_lock)
                {
                    if (!TryCreateCurrentSnapshot(tracker, contributionTracker, out var snapshot)) return;
                    snapshot.BattleIndex = ++_battleCounter;
                    _snapshots.Add(snapshot);
                    Plugin.Log.LogInfo($"RunStatsTracker: Captured battle #{snapshot.BattleIndex}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"RunStatsTracker.CaptureBattle error: {ex.Message}");
            }
        }

        private bool TryCreateCurrentSnapshot(DamageTracker tracker, ContributionTracker contributionTracker, out BattleSnapshot snapshot)
        {
            snapshot = null;
            if (!_isRecording || tracker == null) return false;

            tracker.RefreshSnapshot();
            contributionTracker?.RefreshSnapshot();
            var playerStats = DeepCopyStats(tracker.PlayerStats);
            var enemyStats = DeepCopyStats(tracker.EnemyStats);
            var contributionStats = DeepCopyContributionStats(contributionTracker?.PlayerStats);
            if (!HasAnyStats(playerStats) && !HasAnyStats(enemyStats) && !HasAnyContributionStats(contributionStats)) return false;

            snapshot = new BattleSnapshot
            {
                BattleIndex = _battleCounter + 1,
                Timestamp = DateTime.Now,
                PlayerStats = playerStats,
                EnemyStats = enemyStats,
                ContributionStats = contributionStats,
                PlayerTotalDamage = tracker.PlayerTotalDamage,
                EnemyTotalDamage = tracker.EnemyTotalDamage
            };
            return true;
        }

        private static bool HasAnyStats(List<DamageTracker.ActorStats> stats)
        {
            if (stats == null) return false;
            for (int i = 0; i < stats.Count; i++)
            {
                var s = stats[i];
                if (s.TotalDamageDealt > 0.01f ||
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
                    s.MissAvoids > 0 ||
                    s.DotDamageDealt > 0.01f ||
                    s.DotDamageReceived > 0.01f)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasAnyContributionStats(List<ContributionTracker.ContributionStats> stats)
        {
            if (stats == null) return false;
            for (int i = 0; i < stats.Count; i++)
            {
                var s = stats[i];
                if (s.BonusDamage > 0.01f ||
                    s.ShieldPrevented > 0.01f ||
                    s.GuardProtected > 0.01f ||
                    s.ShieldWasted > 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static List<DamageTracker.ActorStats> DeepCopyStats(IReadOnlyList<DamageTracker.ActorStats> source)
        {
            var result = new List<DamageTracker.ActorStats>();
            if (source == null) return result;
            for (int i = 0; i < source.Count; i++)
            {
                var s = source[i];
                result.Add(new DamageTracker.ActorStats
                {
                    ActorGuid = s.ActorGuid,
                    ActorName = s.ActorName,
                    TeamIndex = s.TeamIndex,
                    TotalDamageDealt = s.TotalDamageDealt,
                    TotalDamageReceived = s.TotalDamageReceived,
                    RawDamageReceived = s.RawDamageReceived,
                    OverkillDamageDealt = s.OverkillDamageDealt,
                    TotalHealingDone = s.TotalHealingDone,
                    TotalHealingReceived = s.TotalHealingReceived,
                    TotalStressReceived = s.TotalStressReceived,
                    Kills = s.Kills,
                    Crits = s.Crits,
                    IncomingAttacks = s.IncomingAttacks,
                    AvoidedAttacks = s.AvoidedAttacks,
                    DodgeAvoids = s.DodgeAvoids,
                    MissAvoids = s.MissAvoids,
                    DotDamageDealt = s.DotDamageDealt,
                    DotDamageReceived = s.DotDamageReceived
                });
            }
            return result;
        }

        private static List<ContributionTracker.ContributionStats> DeepCopyContributionStats(IReadOnlyList<ContributionTracker.ContributionStats> source)
        {
            var result = new List<ContributionTracker.ContributionStats>();
            if (source == null) return result;
            for (int i = 0; i < source.Count; i++)
            {
                var s = source[i];
                result.Add(new ContributionTracker.ContributionStats
                {
                    ActorGuid = s.ActorGuid,
                    ActorName = s.ActorName,
                    TeamIndex = s.TeamIndex,
                    BonusDamage = s.BonusDamage,
                    ShieldPrevented = s.ShieldPrevented,
                    GuardProtected = s.GuardProtected,
                    ShieldWasted = s.ShieldWasted
                });
            }
            return result;
        }

        // Merge all snapshots into aggregated stats
        public (List<MergedStats> players, List<MergedStats> enemies) GetMergedStats(DamageTracker currentTracker = null, ContributionTracker currentContribution = null)
        {
            lock (_lock)
            {
                var playerMap = new Dictionary<string, MergedStats>();
                var enemyMap = new Dictionary<string, MergedStats>();

                foreach (var snap in _snapshots)
                {
                    MergeTeam(snap.PlayerStats, playerMap);
                    MergeTeam(snap.EnemyStats, enemyMap);
                    MergeContributionTeam(snap.ContributionStats, playerMap);
                }
                if (TryCreateCurrentSnapshot(currentTracker, currentContribution, out var currentSnapshot))
                {
                    MergeTeam(currentSnapshot.PlayerStats, playerMap);
                    MergeTeam(currentSnapshot.EnemyStats, enemyMap);
                    MergeContributionTeam(currentSnapshot.ContributionStats, playerMap);
                }

                var players = new List<MergedStats>(playerMap.Values);
                var enemies = new List<MergedStats>(enemyMap.Values);
                players.Sort((a, b) => b.TotalDamageDealt.CompareTo(a.TotalDamageDealt));
                enemies.Sort((a, b) => b.TotalDamageDealt.CompareTo(a.TotalDamageDealt));
                return (players, enemies);
            }
        }

        private List<BattleSnapshot> GetSnapshotsForRead(DamageTracker currentTracker, ContributionTracker currentContribution)
        {
            var snapshots = new List<BattleSnapshot>(_snapshots);
            if (TryCreateCurrentSnapshot(currentTracker, currentContribution, out var currentSnapshot))
                snapshots.Add(currentSnapshot);
            return snapshots;
        }

        private void MergeTeam(List<DamageTracker.ActorStats> stats, Dictionary<string, MergedStats> map)
        {
            if (stats == null) return;
            foreach (var s in stats)
            {
                string key = s.ActorName ?? $"#{s.ActorGuid}";
                if (!map.TryGetValue(key, out var merged))
                {
                    merged = new MergedStats
                    {
                        ActorName = key,
                        ActorGuid = s.ActorGuid,
                        TeamIndex = s.TeamIndex,
                    };
                    map[key] = merged;
                }
                merged.BattlesSeen++;
                merged.TotalDamageDealt += s.TotalDamageDealt;
                merged.TotalDamageReceived += s.TotalDamageReceived;
                merged.RawDamageReceived += s.RawDamageReceived;
                merged.OverkillDamageDealt += s.OverkillDamageDealt;
                merged.TotalHealingDone += s.TotalHealingDone;
                merged.TotalHealingReceived += s.TotalHealingReceived;
                merged.TotalStressReceived += s.TotalStressReceived;
                merged.Kills += s.Kills;
                merged.Crits += s.Crits;
                merged.IncomingAttacks += s.IncomingAttacks;
                merged.AvoidedAttacks += s.AvoidedAttacks;
                merged.DodgeAvoids += s.DodgeAvoids;
                merged.MissAvoids += s.MissAvoids;
                merged.DotDamageDealt += s.DotDamageDealt;
                merged.DotDamageReceived += s.DotDamageReceived;
            }
        }

        private void MergeContributionTeam(List<ContributionTracker.ContributionStats> stats, Dictionary<string, MergedStats> map)
        {
            if (stats == null) return;
            foreach (var s in stats)
            {
                if (s.BonusDamage <= 0.01f && s.ShieldPrevented <= 0.01f && s.GuardProtected <= 0.01f && s.ShieldWasted <= 0) continue;
                string key = s.ActorName ?? $"#{s.ActorGuid}";
                if (!map.TryGetValue(key, out var merged))
                {
                    merged = new MergedStats
                    {
                        ActorName = key,
                        ActorGuid = s.ActorGuid,
                        TeamIndex = s.TeamIndex,
                        BattlesSeen = 1
                    };
                    map[key] = merged;
                }
                merged.BonusDamageContribution += s.BonusDamage;
                merged.ShieldContribution += s.ShieldPrevented;
                merged.GuardContribution += s.GuardProtected;
                merged.ShieldWasted += s.ShieldWasted;
            }
        }

        public void ExportCsv(string filePath, DamageTracker currentTracker = null, ContributionTracker currentContribution = null)
        {
            try
            {
                List<BattleSnapshot> snapshots;
                List<MergedStats> players;
                List<MergedStats> enemies;
                lock (_lock)
                {
                    snapshots = GetSnapshotsForRead(currentTracker, currentContribution);

                    var playerMap = new Dictionary<string, MergedStats>();
                    var enemyMap = new Dictionary<string, MergedStats>();
                    foreach (var snap in snapshots)
                    {
                        MergeTeam(snap.PlayerStats, playerMap);
                        MergeTeam(snap.EnemyStats, enemyMap);
                        MergeContributionTeam(snap.ContributionStats, playerMap);
                    }

                    players = new List<MergedStats>(playerMap.Values);
                    enemies = new List<MergedStats>(enemyMap.Values);
                    players.Sort((a, b) => b.TotalDamageDealt.CompareTo(a.TotalDamageDealt));
                    enemies.Sort((a, b) => b.TotalDamageDealt.CompareTo(a.TotalDamageDealt));
                }

                using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    // Header
                    writer.WriteLine("=== Run Stats CSV Export ===");
                    writer.WriteLine($"Battles Recorded: {snapshots.Count}");
                    writer.WriteLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine();

                    // Heroes
                    writer.WriteLine("--- Heroes ---");
                    writer.WriteLine("Name,Battles,TotalDMG,DOT_DMG,OVK_DMG,RawDMG_Taken,ActualDMG_Taken,HealingDone,HealingReceived,Stress,Kills,Crits,AvoidRate,AvoidChecks,AvoidedAttacks,DodgeAvoids,MissAvoids");
                    foreach (var s in players)
                    {
                        writer.WriteLine($"\"{s.ActorName}\",{s.BattlesSeen},{s.TotalDamageDealt:F0},{s.DotDamageDealt:F0},{s.OverkillDamageDealt:F0},{s.RawDamageReceived:F0},{s.TotalDamageReceived:F0},{s.TotalHealingDone:F0},{s.TotalHealingReceived:F0},{s.TotalStressReceived:F1},{s.Kills},{s.Crits},{UiUtil.GetAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks):F1},{s.IncomingAttacks},{s.AvoidedAttacks},{s.DodgeAvoids},{s.MissAvoids}");
                    }
                    writer.WriteLine();

                    // Enemies
                    writer.WriteLine("--- Enemies ---");
                    writer.WriteLine("Name,Battles,TotalDMG,DOT_DMG,OVK_DMG,RawDMG_Taken,ActualDMG_Taken,HealingDone,HealingReceived,Stress,Kills,Crits,AvoidRate,AvoidChecks,AvoidedAttacks,DodgeAvoids,MissAvoids");
                    foreach (var s in enemies)
                    {
                        writer.WriteLine($"\"{s.ActorName}\",{s.BattlesSeen},{s.TotalDamageDealt:F0},{s.DotDamageDealt:F0},{s.OverkillDamageDealt:F0},{s.RawDamageReceived:F0},{s.TotalDamageReceived:F0},{s.TotalHealingDone:F0},{s.TotalHealingReceived:F0},{s.TotalStressReceived:F1},{s.Kills},{s.Crits},{UiUtil.GetAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks):F1},{s.IncomingAttacks},{s.AvoidedAttacks},{s.DodgeAvoids},{s.MissAvoids}");
                    }
                    writer.WriteLine();

                    // Contribution
                    writer.WriteLine("--- Contribution ---");
                    writer.WriteLine("Name,TotalContribution,BonusDamage,ShieldPrevented,GuardProtected,ShieldWasted,ContributionPct");
                    var contributionRows = new List<MergedStats>(players);
                    contributionRows.Sort((a, b) => b.TotalContribution.CompareTo(a.TotalContribution));
                    float totalContribution = 0f;
                    foreach (var s in contributionRows) totalContribution += s.TotalContribution;
                    foreach (var s in contributionRows)
                    {
                        if (s.TotalContribution <= 0.01f && s.ShieldWasted <= 0) continue;
                        float pct = totalContribution > 0f ? s.TotalContribution / totalContribution * 100f : 0f;
                        writer.WriteLine($"\"{s.ActorName}\",{s.TotalContribution:F1},{s.BonusDamageContribution:F1},{s.ShieldContribution:F1},{s.GuardContribution:F1},{s.ShieldWasted},{pct:F1}");
                    }
                    writer.WriteLine();

                    // Per-battle breakdown
                    writer.WriteLine("--- Per Battle Breakdown ---");
                    foreach (var snap in snapshots)
                    {
                        writer.WriteLine($"Battle #{snap.BattleIndex} ({snap.Timestamp:HH:mm:ss})");
                        writer.WriteLine("Team,Name,DMG,DOT_DMG,OVK_DMG,RawDMG_Taken,ActualDMG_Taken,HealingDone,HealingReceived,Kills,Crits,AvoidRate,AvoidChecks,AvoidedAttacks,DodgeAvoids,MissAvoids");
                        if (snap.PlayerStats != null)
                        {
                            foreach (var s in snap.PlayerStats)
                                writer.WriteLine($"Hero,\"{s.ActorName}\",{s.TotalDamageDealt:F0},{s.DotDamageDealt:F0},{s.OverkillDamageDealt:F0},{s.RawDamageReceived:F0},{s.TotalDamageReceived:F0},{s.TotalHealingDone:F0},{s.TotalHealingReceived:F0},{s.Kills},{s.Crits},{UiUtil.GetAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks):F1},{s.IncomingAttacks},{s.AvoidedAttacks},{s.DodgeAvoids},{s.MissAvoids}");
                        }
                        if (snap.EnemyStats != null)
                        {
                            foreach (var s in snap.EnemyStats)
                                writer.WriteLine($"Enemy,\"{s.ActorName}\",{s.TotalDamageDealt:F0},{s.DotDamageDealt:F0},{s.OverkillDamageDealt:F0},{s.RawDamageReceived:F0},{s.TotalDamageReceived:F0},{s.TotalHealingDone:F0},{s.TotalHealingReceived:F0},{s.Kills},{s.Crits},{UiUtil.GetAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks):F1},{s.IncomingAttacks},{s.AvoidedAttacks},{s.DodgeAvoids},{s.MissAvoids}");
                        }
                        if (snap.ContributionStats != null && HasAnyContributionStats(snap.ContributionStats))
                        {
                            writer.WriteLine("Contribution");
                            writer.WriteLine("Name,TotalContribution,BonusDamage,ShieldPrevented,GuardProtected,ShieldWasted");
                            var rows = new List<ContributionTracker.ContributionStats>(snap.ContributionStats);
                            rows.Sort((a, b) => b.TotalContribution.CompareTo(a.TotalContribution));
                            foreach (var s in rows)
                            {
                                if (s.TotalContribution <= 0.01f && s.ShieldWasted <= 0) continue;
                                writer.WriteLine($"\"{s.ActorName}\",{s.TotalContribution:F1},{s.BonusDamage:F1},{s.ShieldPrevented:F1},{s.GuardProtected:F1},{s.ShieldWasted}");
                            }
                        }
                        writer.WriteLine();
                    }
                }
                Plugin.Log.LogInfo($"RunStatsTracker: CSV exported to {filePath}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"RunStatsTracker.ExportCsv error: {ex.Message}");
            }
        }
    }
}
