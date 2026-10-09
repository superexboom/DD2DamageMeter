using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace DD2DamageMeter
{
    // Stores snapshots of ActorStats at the end of each battle for run-level aggregation
    public class RunStatsTracker
    {
        private const int PersistenceSchema = 2;

        public class BattleSnapshot
        {
            public int BattleIndex;
            public DateTime Timestamp;
            public List<DamageTracker.ActorStats> PlayerStats;
            public List<DamageTracker.ActorStats> EnemyStats;
            public List<ContributionTracker.ContributionStats> ContributionStats;
            public List<DamageTracker.SkillStressHealLogEntry> SkillStressHealLog;
            public float PlayerTotalDamage;
            public float EnemyTotalDamage;
            public int GameCombatCount;
            public Guid GameCombatGuid;
        }

        public class PersistedRunState
        {
            public int Schema;
            public uint ProfileGuid;
            public Guid RunGuid;
            public bool IsRecording;
            public int BattleCounter;
            public List<BattleSnapshot> Snapshots = new List<BattleSnapshot>();
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
            public float SkillStressHealReceived;
            public int SkillStressHealReceivedCount;
            public int Kills;
            public int Crits;
            public int IncomingAttacks;
            public int AvoidedAttacks;
            public int DodgeAvoids;
            public int MissAvoids;
            public float DotDamageDealt;
            public float DotDamageReceived;
            public float BonusDamageContribution;
            public float VulnerableDamageContribution;
            public float ShieldContribution;
            public float GuardContribution;
            public float DotDamagePreventedContribution;
            public int ShieldWasted;
            public int ComboApplied;
            public int ComboConsumed;
            public float TotalContribution => BonusDamageContribution + VulnerableDamageContribution + ShieldContribution + GuardContribution + DotDamagePreventedContribution;
        }

        private readonly List<BattleSnapshot> _snapshots = new List<BattleSnapshot>();
        private readonly object _lock = new object();
        private bool _isRecording;
        private int _battleCounter;

        public bool IsRecording => _isRecording;
        public int BattleCount => _snapshots.Count;

        public void ResetForRun(bool startRecording)
        {
            lock (_lock)
            {
                _snapshots.Clear();
                _battleCounter = 0;
                _isRecording = startRecording;
                Plugin.Log.LogInfo($"RunStatsTracker: Run state reset; recording={startRecording}.");
            }
        }

        public void Resume(PersistedRunState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            lock (_lock)
            {
                _snapshots.Clear();
                if (state.Snapshots != null) _snapshots.AddRange(state.Snapshots);

                _battleCounter = state.BattleCounter;
                for (int i = 0; i < _snapshots.Count; i++)
                {
                    BattleSnapshot snapshot = _snapshots[i];
                    if (snapshot != null && snapshot.BattleIndex > _battleCounter)
                        _battleCounter = snapshot.BattleIndex;
                }

                _isRecording = true;
                Plugin.Log.LogInfo($"RunStatsTracker: Resumed recording ({_snapshots.Count} battles restored).");
            }
        }

        public int TrimToCheckpoint(int combatCount, Guid combatGuid, bool combatActive)
        {
            lock (_lock)
            {
                int battleCounter;
                int removed = TrimSnapshotsToCheckpoint(_snapshots, combatCount, combatGuid, combatActive, out battleCounter);
                _battleCounter = battleCounter;
                return removed;
            }
        }

        public static int TrimToCheckpoint(PersistedRunState state, int combatCount, Guid combatGuid, bool combatActive)
        {
            if (state == null || state.Snapshots == null) return 0;

            int battleCounter;
            int removed = TrimSnapshotsToCheckpoint(state.Snapshots, combatCount, combatGuid, combatActive, out battleCounter);
            state.BattleCounter = battleCounter;
            return removed;
        }

        private static int TrimSnapshotsToCheckpoint(
            List<BattleSnapshot> snapshots,
            int combatCount,
            Guid combatGuid,
            bool combatActive,
            out int battleCounter)
        {
            int removed = 0;
            int maxCompletedCombat = combatActive ? combatCount - 1 : combatCount;
            for (int i = snapshots.Count - 1; i >= 0; i--)
            {
                BattleSnapshot snapshot = snapshots[i];
                bool futureCombat = snapshot.GameCombatCount > maxCompletedCombat;
                bool differentFrontier = !combatActive && snapshot.GameCombatCount == combatCount &&
                    snapshot.GameCombatGuid != combatGuid;
                if (!futureCombat && !differentFrontier) continue;

                snapshots.RemoveAt(i);
                removed++;
            }

            battleCounter = 0;
            for (int i = 0; i < snapshots.Count; i++)
            {
                if (snapshots[i].BattleIndex > battleCounter)
                    battleCounter = snapshots[i].BattleIndex;
            }
            return removed;
        }

        public PersistedRunState CreatePersistedState(uint profileGuid, Guid runGuid)
        {
            lock (_lock)
            {
                if (!_isRecording || _snapshots.Count == 0) return null;
                for (int i = 0; i < _snapshots.Count; i++)
                {
                    if (_snapshots[i] == null || _snapshots[i].GameCombatCount <= 0 ||
                        _snapshots[i].GameCombatGuid == Guid.Empty) return null;
                }
                return new PersistedRunState
                {
                    Schema = PersistenceSchema,
                    ProfileGuid = profileGuid,
                    RunGuid = runGuid,
                    IsRecording = true,
                    BattleCounter = _battleCounter,
                    Snapshots = new List<BattleSnapshot>(_snapshots)
                };
            }
        }

        public BattleSnapshot CreateStandaloneSnapshot(
            DamageTracker tracker,
            ContributionTracker contributionTracker = null,
            int gameCombatCount = 0,
            Guid gameCombatGuid = default(Guid))
        {
            lock (_lock)
            {
                BattleSnapshot snapshot;
                if (!TryCreateCurrentSnapshot(tracker, contributionTracker, out snapshot, false)) return null;
                snapshot.GameCombatCount = gameCombatCount;
                snapshot.GameCombatGuid = gameCombatGuid;
                return snapshot;
            }
        }

        public static bool TryReadPersistedState(string filePath, out PersistedRunState state)
        {
            state = null;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;

            try
            {
                using (var reader = new StreamReader(filePath, Encoding.UTF8))
                    state = (PersistedRunState)new XmlSerializer(typeof(PersistedRunState)).Deserialize(reader);
                if (state == null || state.Schema != PersistenceSchema || state.ProfileGuid == 0 ||
                    state.RunGuid == Guid.Empty || !state.IsRecording || state.BattleCounter < 0 || state.Snapshots == null)
                {
                    state = null;
                    return false;
                }
                for (int i = 0; i < state.Snapshots.Count; i++)
                {
                    if (state.Snapshots[i] == null || state.Snapshots[i].BattleIndex <= 0 ||
                        state.Snapshots[i].GameCombatCount <= 0 || state.Snapshots[i].GameCombatGuid == Guid.Empty)
                    {
                        state = null;
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"RunStatsTracker: Could not read resume state: {ex.Message}");
                return false;
            }
        }

        public static bool TryWritePersistedState(string filePath, PersistedRunState state)
        {
            if (string.IsNullOrEmpty(filePath) || state == null) return false;

            string tempPath = filePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                using (var writer = new StreamWriter(tempPath, false, new UTF8Encoding(false)))
                    new XmlSerializer(typeof(PersistedRunState)).Serialize(writer, state);
                if (File.Exists(filePath))
                    File.Replace(tempPath, filePath, null);
                else
                    File.Move(tempPath, filePath);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"RunStatsTracker: Could not write resume state: {ex.Message}");
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                return false;
            }
        }

        public static void DeletePersistedState(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            try
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                string tempPath = filePath + ".tmp";
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"RunStatsTracker: Could not delete resume state: {ex.Message}");
            }
        }

        public int GetBattleCount(
            DamageTracker currentTracker = null,
            ContributionTracker currentContribution = null,
            DamageMeterMpSnapshot currentRemoteSnapshot = null)
        {
            lock (_lock)
            {
                int count = _snapshots.Count;
                if (TryCreateCurrentSnapshot(currentTracker, currentContribution, out _)) count++;
                if (TryCreateRemoteSnapshot(currentRemoteSnapshot, out _)) count++;
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

        public void CaptureBattle(
            DamageTracker tracker,
            ContributionTracker contributionTracker = null,
            int gameCombatCount = 0,
            Guid gameCombatGuid = default(Guid))
        {
            if (!_isRecording) return;
            try
            {
                lock (_lock)
                {
                    if (!TryCreateCurrentSnapshot(tracker, contributionTracker, out var snapshot)) return;
                    snapshot.BattleIndex = ++_battleCounter;
                    snapshot.GameCombatCount = gameCombatCount;
                    snapshot.GameCombatGuid = gameCombatGuid;
                    _snapshots.Add(snapshot);
                    Plugin.Log.LogInfo($"RunStatsTracker: Captured battle #{snapshot.BattleIndex}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"RunStatsTracker.CaptureBattle error: {ex.Message}");
            }
        }

        public void CaptureRemoteSnapshot(DamageMeterMpSnapshot snapshot)
        {
            if (!_isRecording) return;
            try
            {
                lock (_lock)
                {
                    if (!TryCreateRemoteSnapshot(snapshot, out var battleSnapshot)) return;
                    battleSnapshot.BattleIndex = ++_battleCounter;
                    _snapshots.Add(battleSnapshot);
                    Plugin.Log.LogInfo($"RunStatsTracker: Captured remote battle #{battleSnapshot.BattleIndex}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"RunStatsTracker.CaptureRemoteSnapshot error: {ex.Message}");
            }
        }

        private bool TryCreateCurrentSnapshot(
            DamageTracker tracker,
            ContributionTracker contributionTracker,
            out BattleSnapshot snapshot,
            bool requireRecording = true)
        {
            snapshot = null;
            if ((requireRecording && !_isRecording) || tracker == null) return false;

            tracker.RefreshSnapshot();
            contributionTracker?.RefreshSnapshot();
            var playerStats = DeepCopyStats(tracker.PlayerStats);
            var enemyStats = DeepCopyStats(tracker.EnemyStats);
            var contributionStats = DeepCopyContributionStats(contributionTracker?.PlayerStats);
            var stressHealLog = DeepCopySkillStressHealLog(tracker.GetSkillStressHealLogSnapshot());
            if (!HasAnyStats(playerStats) && !HasAnyStats(enemyStats) && !HasAnyContributionStats(contributionStats)) return false;

            snapshot = new BattleSnapshot
            {
                BattleIndex = _battleCounter + 1,
                Timestamp = DateTime.Now,
                PlayerStats = playerStats,
                EnemyStats = enemyStats,
                ContributionStats = contributionStats,
                SkillStressHealLog = stressHealLog,
                PlayerTotalDamage = tracker.PlayerTotalDamage,
                EnemyTotalDamage = tracker.EnemyTotalDamage
            };
            return true;
        }

        private bool TryCreateRemoteSnapshot(DamageMeterMpSnapshot source, out BattleSnapshot snapshot)
        {
            snapshot = null;
            if (!_isRecording || source == null || !source.IsAvailable) return false;

            var playerStats = DeepCopyRemoteStats(source.Heroes);
            var enemyStats = DeepCopyRemoteStats(source.Enemies);
            var contributionStats = DeepCopyRemoteContributionStats(source.Contributions);
            if (!HasAnyStats(playerStats) && !HasAnyStats(enemyStats) && !HasAnyContributionStats(contributionStats)) return false;

            snapshot = new BattleSnapshot
            {
                BattleIndex = _battleCounter + 1,
                Timestamp = DateTime.Now,
                PlayerStats = playerStats,
                EnemyStats = enemyStats,
                ContributionStats = contributionStats,
                SkillStressHealLog = new List<DamageTracker.SkillStressHealLogEntry>(),
                PlayerTotalDamage = source.PlayerTotalDamage,
                EnemyTotalDamage = source.EnemyTotalDamage
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
                    s.SkillStressHealReceived > 0.01f ||
                    s.SkillStressHealReceivedCount > 0 ||
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
                    SkillStressHealReceived = s.SkillStressHealReceived,
                    SkillStressHealReceivedCount = s.SkillStressHealReceivedCount,
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
                    VulnerableDamage = s.VulnerableDamage,
                    ShieldPrevented = s.ShieldPrevented,
                    GuardProtected = s.GuardProtected,
                    DotDamagePrevented = s.DotDamagePrevented,
                    ShieldWasted = s.ShieldWasted,
                    ComboApplied = s.ComboApplied,
                    ComboConsumed = s.ComboConsumed
                });
            }
            return result;
        }

        private static List<DamageTracker.SkillStressHealLogEntry> DeepCopySkillStressHealLog(IReadOnlyList<DamageTracker.SkillStressHealLogEntry> source)
        {
            var result = new List<DamageTracker.SkillStressHealLogEntry>();
            if (source == null) return result;
            for (int i = 0; i < source.Count; i++)
            {
                var s = source[i];
                if (s == null) continue;
                result.Add(new DamageTracker.SkillStressHealLogEntry
                {
                    Sequence = s.Sequence,
                    SourceActorGuid = s.SourceActorGuid,
                    SourceActorName = s.SourceActorName,
                    TargetActorGuid = s.TargetActorGuid,
                    TargetActorName = s.TargetActorName,
                    SkillId = s.SkillId,
                    SkillName = s.SkillName,
                    Amount = s.Amount
                });
            }
            return result;
        }

        private static List<DamageTracker.ActorStats> DeepCopyRemoteStats(IList<DamageMeterMpActorStats> source)
        {
            var result = new List<DamageTracker.ActorStats>();
            if (source == null) return result;
            for (int i = 0; i < source.Count; i++)
            {
                var s = source[i];
                if (s == null) continue;
                result.Add(new DamageTracker.ActorStats
                {
                    ActorGuid = ParseGuid(s.ActorGuid),
                    ActorName = s.ActorName,
                    TeamIndex = s.TeamIndex,
                    TotalDamageDealt = s.TotalDamageDealt,
                    TotalDamageReceived = s.TotalDamageReceived,
                    RawDamageReceived = s.RawDamageReceived,
                    OverkillDamageDealt = s.OverkillDamageDealt,
                    TotalHealingDone = s.TotalHealingDone,
                    TotalHealingReceived = s.TotalHealingReceived,
                    TotalStressReceived = s.TotalStressReceived,
                    SkillStressHealReceived = s.SkillStressHealReceived,
                    SkillStressHealReceivedCount = s.SkillStressHealReceivedCount,
                    Kills = s.Kills,
                    Crits = s.Crits,
                    IncomingAttacks = s.IncomingAttacks,
                    AvoidedAttacks = s.AvoidedAttacks,
                    DodgeAvoids = s.DodgeAvoids,
                    MissAvoids = s.MissAvoids,
                    DotDamageDealt = s.DotDamageDealt,
                    DotDamageReceived = 0f
                });
            }
            return result;
        }

        private static List<ContributionTracker.ContributionStats> DeepCopyRemoteContributionStats(IList<DamageMeterMpContributionStats> source)
        {
            var result = new List<ContributionTracker.ContributionStats>();
            if (source == null) return result;
            for (int i = 0; i < source.Count; i++)
            {
                var s = source[i];
                if (s == null) continue;
                result.Add(new ContributionTracker.ContributionStats
                {
                    ActorGuid = ParseGuid(s.ActorGuid),
                    ActorName = s.ActorName,
                    TeamIndex = s.TeamIndex,
                    BonusDamage = s.BonusDamage,
                    VulnerableDamage = s.VulnerableDamage,
                    ShieldPrevented = s.ShieldPrevented,
                    GuardProtected = s.GuardProtected,
                    DotDamagePrevented = s.DotDamagePrevented,
                    ShieldWasted = s.ShieldWasted,
                    ComboApplied = s.ComboApplied,
                    ComboConsumed = s.ComboConsumed
                });
            }
            return result;
        }

        private static uint ParseGuid(string value)
        {
            uint guid;
            return uint.TryParse(value, out guid) ? guid : 0U;
        }

        // Merge all snapshots into aggregated stats
        public (List<MergedStats> players, List<MergedStats> enemies) GetMergedStats(
            DamageTracker currentTracker = null,
            ContributionTracker currentContribution = null,
            DamageMeterMpSnapshot currentRemoteSnapshot = null)
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
                if (TryCreateRemoteSnapshot(currentRemoteSnapshot, out var currentRemote))
                {
                    MergeTeam(currentRemote.PlayerStats, playerMap);
                    MergeTeam(currentRemote.EnemyStats, enemyMap);
                    MergeContributionTeam(currentRemote.ContributionStats, playerMap);
                }

                var players = new List<MergedStats>(playerMap.Values);
                var enemies = new List<MergedStats>(enemyMap.Values);
                players.Sort((a, b) => b.TotalDamageDealt.CompareTo(a.TotalDamageDealt));
                enemies.Sort((a, b) => b.TotalDamageDealt.CompareTo(a.TotalDamageDealt));
                return (players, enemies);
            }
        }

        private List<BattleSnapshot> GetSnapshotsForRead(
            DamageTracker currentTracker,
            ContributionTracker currentContribution,
            DamageMeterMpSnapshot currentRemoteSnapshot)
        {
            var snapshots = new List<BattleSnapshot>(_snapshots);
            if (TryCreateCurrentSnapshot(currentTracker, currentContribution, out var currentSnapshot))
                snapshots.Add(currentSnapshot);
            if (TryCreateRemoteSnapshot(currentRemoteSnapshot, out var currentRemote))
                snapshots.Add(currentRemote);
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
                merged.SkillStressHealReceived += s.SkillStressHealReceived;
                merged.SkillStressHealReceivedCount += s.SkillStressHealReceivedCount;
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
                if (s.BonusDamage <= 0.01f &&
                    s.VulnerableDamage <= 0.01f &&
                    s.ShieldPrevented <= 0.01f &&
                    s.GuardProtected <= 0.01f &&
                    s.DotDamagePrevented <= 0.01f &&
                    s.ComboApplied <= 0 &&
                    s.ComboConsumed <= 0)
                {
                    continue;
                }
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
                merged.VulnerableDamageContribution += s.VulnerableDamage;
                merged.ShieldContribution += s.ShieldPrevented;
                merged.GuardContribution += s.GuardProtected;
                merged.DotDamagePreventedContribution += s.DotDamagePrevented;
                merged.ComboApplied += s.ComboApplied;
                merged.ComboConsumed += s.ComboConsumed;
            }
        }

        private static int GetComboAppliedForActor(List<ContributionTracker.ContributionStats> stats, DamageTracker.ActorStats actor)
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

        public void ExportCsv(
            string filePath,
            DamageTracker currentTracker = null,
            ContributionTracker currentContribution = null,
            DamageMeterMpSnapshot currentRemoteSnapshot = null)
        {
            try
            {
                List<BattleSnapshot> snapshots;
                List<MergedStats> players;
                List<MergedStats> enemies;
                lock (_lock)
                {
                    snapshots = GetSnapshotsForRead(currentTracker, currentContribution, currentRemoteSnapshot);

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
                    writer.WriteLine(DmText.T("csvTitle"));
                    writer.WriteLine(DmText.Format("battlesRecorded", snapshots.Count));
                    writer.WriteLine(DmText.Format("exported", DateTime.Now));
                    writer.WriteLine();

                    // Heroes
                    writer.WriteLine(DmText.T("sectionHeroes"));
                    writer.WriteLine(DmText.T("csvHeroesHeader"));
                    foreach (var s in players)
                    {
                        writer.WriteLine($"\"{s.ActorName}\",{s.BattlesSeen},{s.TotalDamageDealt:F0},{s.DotDamageDealt:F0},{s.OverkillDamageDealt:F0},{s.RawDamageReceived:F0},{s.TotalDamageReceived:F0},{s.TotalHealingDone:F0},{s.TotalHealingReceived:F0},{s.TotalStressReceived:F1},{s.Kills},{s.Crits},{UiUtil.GetAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks):F1},{s.AvoidedAttacks},{s.IncomingAttacks},{s.DodgeAvoids},{s.MissAvoids},{s.ComboApplied}");
                    }
                    writer.WriteLine();

                    // Enemies
                    writer.WriteLine(DmText.T("sectionEnemies"));
                    writer.WriteLine(DmText.T("csvHeroesHeader"));
                    foreach (var s in enemies)
                    {
                        writer.WriteLine($"\"{s.ActorName}\",{s.BattlesSeen},{s.TotalDamageDealt:F0},{s.DotDamageDealt:F0},{s.OverkillDamageDealt:F0},{s.RawDamageReceived:F0},{s.TotalDamageReceived:F0},{s.TotalHealingDone:F0},{s.TotalHealingReceived:F0},{s.TotalStressReceived:F1},{s.Kills},{s.Crits},{UiUtil.GetAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks):F1},{s.AvoidedAttacks},{s.IncomingAttacks},{s.DodgeAvoids},{s.MissAvoids},{s.ComboApplied}");
                    }
                    writer.WriteLine();

                    // Contribution
                    writer.WriteLine(DmText.T("contribution"));
                    writer.WriteLine(DmText.T("csvContributionHeader"));
                    var contributionRows = new List<MergedStats>(players);
                    contributionRows.Sort((a, b) =>
                    {
                        int result = b.TotalContribution.CompareTo(a.TotalContribution);
                        if (result != 0) return result;
                        result = b.VulnerableDamageContribution.CompareTo(a.VulnerableDamageContribution);
                        if (result != 0) return result;
                        result = b.ComboConsumed.CompareTo(a.ComboConsumed);
                        if (result != 0) return result;
                        return string.Compare(a.ActorName, b.ActorName, StringComparison.CurrentCultureIgnoreCase);
                    });
                    float totalContribution = 0f;
                    foreach (var s in contributionRows) totalContribution += s.TotalContribution;
                    foreach (var s in contributionRows)
                    {
                        if (s.TotalContribution <= 0.01f && s.ComboConsumed <= 0) continue;
                        float pct = totalContribution > 0f ? s.TotalContribution / totalContribution * 100f : 0f;
                        writer.WriteLine($"\"{s.ActorName}\",{s.TotalContribution:F1},{s.BonusDamageContribution:F1},{s.VulnerableDamageContribution:F1},{s.ShieldContribution:F1},{s.GuardContribution:F1},{s.DotDamagePreventedContribution:F1},{s.ComboConsumed},{pct:F1}");
                    }
                    writer.WriteLine();

                    // Per-battle breakdown
                    writer.WriteLine(DmText.T("csvPerBattle"));
                    foreach (var snap in snapshots)
                    {
                        writer.WriteLine(DmText.Format("csvBattle", snap.BattleIndex, snap.Timestamp));
                        writer.WriteLine(DmText.T("csvTeamHeader"));
                        if (snap.PlayerStats != null)
                        {
                            foreach (var s in snap.PlayerStats)
                                writer.WriteLine($"{DmText.T("csvHero")},\"{s.ActorName}\",{s.TotalDamageDealt:F0},{s.DotDamageDealt:F0},{s.OverkillDamageDealt:F0},{s.RawDamageReceived:F0},{s.TotalDamageReceived:F0},{s.TotalHealingDone:F0},{s.TotalHealingReceived:F0},{s.Kills},{s.Crits},{UiUtil.GetAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks):F1},{s.AvoidedAttacks},{s.IncomingAttacks},{s.DodgeAvoids},{s.MissAvoids},{GetComboAppliedForActor(snap.ContributionStats, s)}");
                        }
                        if (snap.EnemyStats != null)
                        {
                            foreach (var s in snap.EnemyStats)
                                writer.WriteLine($"{DmText.T("csvEnemy")},\"{s.ActorName}\",{s.TotalDamageDealt:F0},{s.DotDamageDealt:F0},{s.OverkillDamageDealt:F0},{s.RawDamageReceived:F0},{s.TotalDamageReceived:F0},{s.TotalHealingDone:F0},{s.TotalHealingReceived:F0},{s.Kills},{s.Crits},{UiUtil.GetAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks):F1},{s.AvoidedAttacks},{s.IncomingAttacks},{s.DodgeAvoids},{s.MissAvoids},0");
                        }
                        if (snap.ContributionStats != null && HasAnyContributionStats(snap.ContributionStats))
                        {
                            writer.WriteLine(DmText.T("contribution"));
                            writer.WriteLine(DmText.T("csvContributionHeader"));
                            var rows = new List<ContributionTracker.ContributionStats>(snap.ContributionStats);
                            rows.Sort((a, b) =>
                            {
                                int result = b.TotalContribution.CompareTo(a.TotalContribution);
                                if (result != 0) return result;
                                result = b.VulnerableDamage.CompareTo(a.VulnerableDamage);
                                if (result != 0) return result;
                                result = b.ComboConsumed.CompareTo(a.ComboConsumed);
                                if (result != 0) return result;
                                return string.Compare(a.ActorName, b.ActorName, StringComparison.CurrentCultureIgnoreCase);
                            });
                            float battleContribution = 0f;
                            foreach (var row in rows) battleContribution += row.TotalContribution;
                            foreach (var s in rows)
                            {
                                if (s.TotalContribution <= 0.01f && s.ComboConsumed <= 0) continue;
                                float pct = battleContribution > 0f ? s.TotalContribution / battleContribution * 100f : 0f;
                                writer.WriteLine($"\"{s.ActorName}\",{s.TotalContribution:F1},{s.BonusDamage:F1},{s.VulnerableDamage:F1},{s.ShieldPrevented:F1},{s.GuardProtected:F1},{s.DotDamagePrevented:F1},{s.ComboConsumed},{pct:F1}");
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

        public void ExportStressReliefCsv(
            string filePath,
            DamageTracker currentTracker = null,
            ContributionTracker currentContribution = null,
            DamageMeterMpSnapshot currentRemoteSnapshot = null)
        {
            try
            {
                List<BattleSnapshot> snapshots;
                List<MergedStats> players;
                lock (_lock)
                {
                    snapshots = GetSnapshotsForRead(currentTracker, currentContribution, currentRemoteSnapshot);

                    var playerMap = new Dictionary<string, MergedStats>();
                    foreach (var snap in snapshots)
                    {
                        MergeTeam(snap.PlayerStats, playerMap);
                    }

                    players = new List<MergedStats>(playerMap.Values);
                    players.Sort((a, b) => b.SkillStressHealReceived.CompareTo(a.SkillStressHealReceived));
                }

                using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    writer.WriteLine(DmText.T("stressReliefCsvTitle"));
                    writer.WriteLine(DmText.Format("battlesRecorded", snapshots.Count));
                    writer.WriteLine(DmText.Format("exported", DateTime.Now));
                    writer.WriteLine();

                    writer.WriteLine(DmText.T("sectionSkillStressHeal"));
                    writer.WriteLine(DmText.T("csvSkillStressHealHeader"));
                    foreach (var s in players)
                    {
                        if (s == null || (s.SkillStressHealReceived <= 0.01f && s.SkillStressHealReceivedCount <= 0)) continue;
                        writer.WriteLine($"{Csv(s.ActorName)},{s.BattlesSeen},{s.SkillStressHealReceivedCount},{s.SkillStressHealReceived:F1}");
                    }
                    writer.WriteLine();

                    writer.WriteLine(DmText.T("csvPerBattle"));
                    writer.WriteLine(DmText.T("csvSkillStressHealBattleFileHeader"));
                    foreach (var snap in snapshots)
                    {
                        if (!HasAnySkillStressHealStats(snap.PlayerStats)) continue;
                        foreach (var s in snap.PlayerStats)
                        {
                            if (s == null || (s.SkillStressHealReceived <= 0.01f && s.SkillStressHealReceivedCount <= 0)) continue;
                            writer.WriteLine($"{snap.BattleIndex},{snap.Timestamp:HH:mm:ss},{Csv(s.ActorName)},{s.SkillStressHealReceivedCount},{s.SkillStressHealReceived:F1}");
                        }
                    }
                    writer.WriteLine();

                    writer.WriteLine(DmText.T("sectionSkillStressHealLog"));
                    writer.WriteLine(DmText.T("csvSkillStressHealLogHeader"));
                    foreach (var snap in snapshots)
                    {
                        if (snap.SkillStressHealLog == null) continue;
                        var rows = new List<DamageTracker.SkillStressHealLogEntry>(snap.SkillStressHealLog);
                        rows.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
                        foreach (var entry in rows)
                        {
                            if (entry == null || entry.Amount <= 0.01f) continue;
                            string skill = !string.IsNullOrWhiteSpace(entry.SkillName) ? entry.SkillName : entry.SkillId;
                            writer.WriteLine($"{snap.BattleIndex},{snap.Timestamp:HH:mm:ss},{Csv(entry.SourceActorName)},{Csv(entry.TargetActorName)},{Csv(skill)},{entry.Amount:F1}");
                        }
                    }
                }
                Plugin.Log.LogInfo($"RunStatsTracker: Stress relief CSV exported to {filePath}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"RunStatsTracker.ExportStressReliefCsv error: {ex.Message}");
            }
        }

        private static bool HasAnySkillStressHealStats(List<MergedStats> players)
        {
            if (players == null) return false;
            for (int i = 0; i < players.Count; i++)
            {
                var s = players[i];
                if (s != null && (s.SkillStressHealReceived > 0.01f || s.SkillStressHealReceivedCount > 0))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasAnySkillStressHealStats(List<DamageTracker.ActorStats> players)
        {
            if (players == null) return false;
            for (int i = 0; i < players.Count; i++)
            {
                var s = players[i];
                if (s != null && (s.SkillStressHealReceived > 0.01f || s.SkillStressHealReceivedCount > 0))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Csv(string value)
        {
            value = value ?? string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
