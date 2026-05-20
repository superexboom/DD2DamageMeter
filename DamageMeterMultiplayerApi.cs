using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Assets.Code.Actor;
using Assets.Code.Combat;
using Assets.Code.Utils;

namespace DD2DamageMeter
{
    public static class DamageMeterMultiplayerApi
    {
        public const int ApiVersion = 1;
        private const int MaxCombatLogEntries = 24;
        private const double RemoteSnapshotMaxAgeSeconds = 20.0;
        private static readonly object RemoteSnapshotLock = new object();
        private static DamageMeterMpSnapshot _remoteSnapshot;
        private static DateTime _remoteSnapshotUpdatedUtc;

        public static bool TryGetLiveSnapshot(out DamageMeterMpSnapshot snapshot)
        {
            snapshot = null;

            Plugin plugin = Plugin.Instance;
            if (plugin == null)
                return false;

            snapshot = new DamageMeterMpSnapshot
            {
                ApiVersion = ApiVersion,
                ProviderVersion = Plugin.Version,
                Capabilities = "main,contribution,status,combat_log,combo,vulnerable",
                IsAvailable = true,
                IsActive = plugin.IsBattleActive,
            };

            if (!plugin.IsEventManagerReady)
            {
                snapshot.IsAvailable = false;
                snapshot.IsActive = false;
                snapshot.UnavailableReason = "DamageMeter event listeners are not ready.";
                snapshot.Digest = ComputeDigest(snapshot);
                return true;
            }

            FillCombatContext(snapshot);
            FillMainRows(snapshot, plugin.Tracker);
            FillContributionRows(snapshot, plugin.ContributionTracker);
            FillStatusTotals(snapshot, plugin.LogTracker);
            FillCombatLog(snapshot, plugin.LogTracker);
            snapshot.Digest = ComputeDigest(snapshot);
            return true;
        }

        public static bool TryApplyRemoteSnapshot(object source)
        {
            DamageMeterMpSnapshot snapshot = ConvertRemoteSnapshot(source);
            if (snapshot == null)
                return false;

            lock (RemoteSnapshotLock)
            {
                _remoteSnapshot = snapshot;
                _remoteSnapshotUpdatedUtc = DateTime.UtcNow;
            }

            return true;
        }

        public static bool TryGetRemoteSnapshot(out DamageMeterMpSnapshot snapshot)
        {
            lock (RemoteSnapshotLock)
            {
                snapshot = _remoteSnapshot;
                if (snapshot == null)
                    return false;

                if ((DateTime.UtcNow - _remoteSnapshotUpdatedUtc).TotalSeconds > RemoteSnapshotMaxAgeSeconds)
                {
                    snapshot = null;
                    return false;
                }

                return true;
            }
        }

        public static bool HasRecentRemoteSnapshot()
        {
            DamageMeterMpSnapshot snapshot;
            return TryGetRemoteSnapshot(out snapshot);
        }

        private static void FillCombatContext(DamageMeterMpSnapshot snapshot)
        {
            try
            {
                if (!SingletonMonoBehaviour<CombatBhv>.HasInstance(false))
                    return;

                CombatBhv combat = SingletonMonoBehaviour<CombatBhv>.Instance;
                snapshot.Round = combat.CurrentRound;
                snapshot.Turn = combat.CurrentTurn;
                snapshot.BattleState = combat.CurrentBattleState.ToString();

                ActorInstance actor = combat.GetCurrentActor();
                if (actor != null)
                {
                    snapshot.CurrentActorGuid = actor.ActorGuid.ToString(CultureInfo.InvariantCulture);
                    snapshot.CurrentActorName = DamageTracker.TryResolveNamePublic(actor.ActorGuid) ?? actor.ActorDataId;
                }
            }
            catch
            {
            }
        }

        private static void FillMainRows(DamageMeterMpSnapshot snapshot, DamageTracker tracker)
        {
            if (tracker == null)
                return;

            tracker.RefreshSnapshot();
            snapshot.PlayerTotalDamage = tracker.PlayerTotalDamage;
            snapshot.EnemyTotalDamage = tracker.EnemyTotalDamage;
            snapshot.Heroes = tracker.PlayerStats.Select(ToMpActorStats).ToList();
            snapshot.Enemies = tracker.EnemyStats.Select(ToMpActorStats).ToList();
        }

        private static DamageMeterMpActorStats ToMpActorStats(DamageTracker.ActorStats stats)
        {
            if (stats == null)
                return null;

            return new DamageMeterMpActorStats
            {
                ActorGuid = stats.ActorGuid.ToString(CultureInfo.InvariantCulture),
                ActorName = stats.ActorName,
                TeamIndex = stats.TeamIndex,
                TotalDamageDealt = stats.TotalDamageDealt,
                DotDamageDealt = stats.DotDamageDealt,
                TotalDamageReceived = stats.TotalDamageReceived,
                RawDamageReceived = stats.RawDamageReceived,
                OverkillDamageDealt = stats.OverkillDamageDealt,
                TotalHealingDone = stats.TotalHealingDone,
                TotalHealingReceived = stats.TotalHealingReceived,
                TotalStressReceived = stats.TotalStressReceived,
                Kills = stats.Kills,
                Crits = stats.Crits,
                IncomingAttacks = stats.IncomingAttacks,
                AvoidedAttacks = stats.AvoidedAttacks,
                DodgeAvoids = stats.DodgeAvoids,
                MissAvoids = stats.MissAvoids,
            };
        }

        private static void FillContributionRows(DamageMeterMpSnapshot snapshot, ContributionTracker contributionTracker)
        {
            if (contributionTracker == null)
                return;

            contributionTracker.RefreshSnapshot();
            snapshot.Contributions = contributionTracker.PlayerStats.Select(ToMpContributionStats).ToList();
        }

        private static DamageMeterMpContributionStats ToMpContributionStats(ContributionTracker.ContributionStats stats)
        {
            if (stats == null)
                return null;

            return new DamageMeterMpContributionStats
            {
                ActorGuid = stats.ActorGuid.ToString(CultureInfo.InvariantCulture),
                ActorName = stats.ActorName,
                TeamIndex = stats.TeamIndex,
                BonusDamage = stats.BonusDamage,
                VulnerableDamage = stats.VulnerableDamage,
                ShieldPrevented = stats.ShieldPrevented,
                GuardProtected = stats.GuardProtected,
                ShieldWasted = stats.ShieldWasted,
                ComboApplied = stats.ComboApplied,
                ComboConsumed = stats.ComboConsumed,
                TotalContribution = stats.TotalContribution,
            };
        }

        private static void FillStatusTotals(DamageMeterMpSnapshot snapshot, CombatLogTracker logTracker)
        {
            if (logTracker == null)
                return;

            CombatLogTracker.StatusTotals totals = logTracker.GetStatusTotalsSnapshot();
            if (totals == null)
                return;

            snapshot.StatusTotals = new DamageMeterMpStatusTotals
            {
                PlayerBuffApplied = totals.PlayerBuffApplied,
                PlayerDebuffApplied = totals.PlayerDebuffApplied,
                EnemyBuffApplied = totals.EnemyBuffApplied,
                EnemyDebuffApplied = totals.EnemyDebuffApplied,
                PlayerStatusRemoved = totals.PlayerStatusRemoved,
                EnemyStatusRemoved = totals.EnemyStatusRemoved,
                PlayerStatusConsumed = totals.PlayerStatusConsumed,
                EnemyStatusConsumed = totals.EnemyStatusConsumed,
            };
        }

        private static void FillCombatLog(DamageMeterMpSnapshot snapshot, CombatLogTracker logTracker)
        {
            if (logTracker == null)
                return;

            List<object> entries = logTracker.GetRecentEntriesSnapshot(MaxCombatLogEntries);
            List<DamageMeterMpCombatLogEntry> rows = new List<DamageMeterMpCombatLogEntry>();
            int startIndex = Math.Max(0, logTracker.Entries.Count - entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                object entry = entries[i];
                if (entry is CombatLogTracker.RoundHeader round)
                {
                    rows.Add(new DamageMeterMpCombatLogEntry
                    {
                        Index = startIndex + i,
                        EntryType = "round",
                        Round = round.Round,
                    });
                }
                else if (entry is CombatLogTracker.LogEntry log)
                {
                    rows.Add(new DamageMeterMpCombatLogEntry
                    {
                        Index = startIndex + i,
                        EntryType = "entry",
                        Round = log.Round,
                        SourceName = log.SourceName,
                        TargetName = log.TargetName,
                        SourceIsPlayer = log.SourceIsPlayer,
                        TargetIsPlayer = log.TargetIsPlayer,
                        ActionType = log.ActionType,
                        Value = log.Value,
                        SkillId = log.SkillId,
                        Extra = log.Extra,
                        DotType = log.DotType,
                        OverkillDamage = log.OverkillDamage,
                    });
                }
            }

            snapshot.CombatLogEntries = rows;
        }

        private static string ComputeDigest(DamageMeterMpSnapshot snapshot)
        {
            if (snapshot == null)
                return "0000000000000000";

            StringBuilder sb = new StringBuilder();
            sb.Append(snapshot.ApiVersion).Append(':')
                .Append(snapshot.ProviderVersion).Append(':')
                .Append(snapshot.Capabilities).Append(':')
                .Append(snapshot.IsAvailable).Append(':')
                .Append(snapshot.IsActive).Append(':')
                .Append(snapshot.UnavailableReason).Append(':')
                .Append(snapshot.Round).Append(':')
                .Append(snapshot.Turn).Append(':')
                .Append(snapshot.BattleState).Append(':')
                .Append(snapshot.CurrentActorGuid).Append(':')
                .Append(FormatFloat(snapshot.PlayerTotalDamage)).Append(':')
                .Append(FormatFloat(snapshot.EnemyTotalDamage)).Append(':');

            AppendActorRows(sb, snapshot.Heroes);
            sb.Append(':');
            AppendActorRows(sb, snapshot.Enemies);
            sb.Append(':');
            AppendContributionRows(sb, snapshot.Contributions);
            sb.Append(':');
            AppendLogRows(sb, snapshot.CombatLogEntries);
            return ComputeStableDigest(sb.ToString());
        }

        private static void AppendActorRows(StringBuilder sb, IList<DamageMeterMpActorStats> rows)
        {
            if (rows == null)
                return;

            foreach (DamageMeterMpActorStats row in rows)
            {
                if (row == null)
                    continue;

                sb.Append(row.ActorGuid).Append(',')
                    .Append(row.ActorName).Append(',')
                    .Append(row.TeamIndex).Append(',')
                    .Append(FormatFloat(row.TotalDamageDealt)).Append(',')
                    .Append(FormatFloat(row.DotDamageDealt)).Append(',')
                    .Append(FormatFloat(row.TotalDamageReceived)).Append(',')
                    .Append(FormatFloat(row.RawDamageReceived)).Append(',')
                    .Append(FormatFloat(row.OverkillDamageDealt)).Append(',')
                    .Append(FormatFloat(row.TotalHealingDone)).Append(',')
                    .Append(FormatFloat(row.TotalHealingReceived)).Append(',')
                    .Append(FormatFloat(row.TotalStressReceived)).Append(',')
                    .Append(row.Kills).Append(',')
                    .Append(row.Crits).Append(',')
                    .Append(row.IncomingAttacks).Append(',')
                    .Append(row.AvoidedAttacks).Append('|');
            }
        }

        private static void AppendContributionRows(StringBuilder sb, IList<DamageMeterMpContributionStats> rows)
        {
            if (rows == null)
                return;

            foreach (DamageMeterMpContributionStats row in rows)
            {
                if (row == null)
                    continue;

                sb.Append(row.ActorGuid).Append(',')
                    .Append(row.ActorName).Append(',')
                    .Append(row.TeamIndex).Append(',')
                    .Append(FormatFloat(row.BonusDamage)).Append(',')
                    .Append(FormatFloat(row.VulnerableDamage)).Append(',')
                    .Append(FormatFloat(row.ShieldPrevented)).Append(',')
                    .Append(FormatFloat(row.GuardProtected)).Append(',')
                    .Append(row.ShieldWasted).Append(',')
                    .Append(row.ComboApplied).Append(',')
                    .Append(row.ComboConsumed).Append('|');
            }
        }

        private static void AppendLogRows(StringBuilder sb, IList<DamageMeterMpCombatLogEntry> rows)
        {
            if (rows == null)
                return;

            foreach (DamageMeterMpCombatLogEntry row in rows)
            {
                if (row == null)
                    continue;

                sb.Append(row.Index).Append(',')
                    .Append(row.Round).Append(',')
                    .Append(row.EntryType).Append(',')
                    .Append(row.SourceName).Append(',')
                    .Append(row.ActionType).Append(',')
                    .Append(FormatFloat(row.Value)).Append(',')
                    .Append(row.TargetName).Append(',')
                    .Append(row.SkillId).Append(',')
                    .Append(row.Extra).Append(',')
                    .Append(row.DotType).Append('|');
            }
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string ComputeStableDigest(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                string value = text ?? string.Empty;
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 1099511628211UL;
                }

                return hash.ToString("x16");
            }
        }

        private static DamageMeterMpSnapshot ConvertRemoteSnapshot(object source)
        {
            if (source == null)
                return null;

            if (source is DamageMeterMpSnapshot existing)
                return CloneSnapshot(existing);

            DamageMeterMpSnapshot snapshot = new DamageMeterMpSnapshot
            {
                ApiVersion = GetInt(source, "ApiVersion", ApiVersion),
                ProviderVersion = GetString(source, "ProviderVersion", null),
                Capabilities = GetString(source, "Capabilities", null),
                IsAvailable = GetBool(source, "IsAvailable", true),
                IsActive = GetBool(source, "IsActive", false),
                UnavailableReason = GetString(source, "UnavailableReason", null),
                Round = GetInt(source, "Round", 0),
                Turn = GetInt(source, "Turn", 0),
                BattleState = GetString(source, "BattleState", null),
                CurrentActorGuid = GetString(source, "CurrentActorGuid", null),
                CurrentActorName = GetString(source, "CurrentActorName", null),
                PlayerTotalDamage = GetFloat(source, "PlayerTotalDamage", 0f),
                EnemyTotalDamage = GetFloat(source, "EnemyTotalDamage", 0f),
                Heroes = ReadRemoteActorRows(GetFirstPropertyValue(source, "Heroes", "PlayerStats")),
                Enemies = ReadRemoteActorRows(GetFirstPropertyValue(source, "Enemies", "EnemyStats")),
                Contributions = ReadRemoteContributionRows(GetFirstPropertyValue(source, "Contributions", "ContributionStats")),
                StatusTotals = ReadRemoteStatusTotals(GetFirstPropertyValue(source, "StatusTotals")),
                CombatLogEntries = ReadRemoteCombatLogRows(GetFirstPropertyValue(source, "CombatLogEntries", "LogEntries", "CombatLog")),
                Digest = GetString(source, "Digest", null),
            };

            if (string.IsNullOrWhiteSpace(snapshot.Digest))
            {
                snapshot.Digest = ComputeDigest(snapshot);
            }

            return snapshot;
        }

        private static DamageMeterMpSnapshot CloneSnapshot(DamageMeterMpSnapshot source)
        {
            if (source == null)
                return null;

            return new DamageMeterMpSnapshot
            {
                ApiVersion = source.ApiVersion,
                ProviderVersion = source.ProviderVersion,
                Capabilities = source.Capabilities,
                IsAvailable = source.IsAvailable,
                IsActive = source.IsActive,
                UnavailableReason = source.UnavailableReason,
                Round = source.Round,
                Turn = source.Turn,
                BattleState = source.BattleState,
                CurrentActorGuid = source.CurrentActorGuid,
                CurrentActorName = source.CurrentActorName,
                PlayerTotalDamage = source.PlayerTotalDamage,
                EnemyTotalDamage = source.EnemyTotalDamage,
                Heroes = source.Heroes == null ? new List<DamageMeterMpActorStats>() : source.Heroes.Select(CloneActorStats).ToList(),
                Enemies = source.Enemies == null ? new List<DamageMeterMpActorStats>() : source.Enemies.Select(CloneActorStats).ToList(),
                Contributions = source.Contributions == null ? new List<DamageMeterMpContributionStats>() : source.Contributions.Select(CloneContributionStats).ToList(),
                StatusTotals = CloneStatusTotals(source.StatusTotals),
                CombatLogEntries = source.CombatLogEntries == null ? new List<DamageMeterMpCombatLogEntry>() : source.CombatLogEntries.Select(CloneCombatLogEntry).ToList(),
                Digest = source.Digest,
            };
        }

        private static IList<DamageMeterMpActorStats> ReadRemoteActorRows(object value)
        {
            List<DamageMeterMpActorStats> rows = new List<DamageMeterMpActorStats>();
            IEnumerable enumerable = AsEnumerable(value);
            if (enumerable == null)
                return rows;

            foreach (object item in enumerable)
            {
                if (item == null)
                    continue;

                rows.Add(new DamageMeterMpActorStats
                {
                    ActorGuid = GetString(item, "ActorGuid", null),
                    ActorName = GetString(item, "ActorName", null),
                    TeamIndex = GetInt(item, "TeamIndex", -1),
                    TotalDamageDealt = GetFloat(item, "TotalDamageDealt", 0f),
                    DotDamageDealt = GetFloat(item, "DotDamageDealt", 0f),
                    TotalDamageReceived = GetFloat(item, "TotalDamageReceived", 0f),
                    RawDamageReceived = GetFloat(item, "RawDamageReceived", 0f),
                    OverkillDamageDealt = GetFloat(item, "OverkillDamageDealt", 0f),
                    TotalHealingDone = GetFloat(item, "TotalHealingDone", 0f),
                    TotalHealingReceived = GetFloat(item, "TotalHealingReceived", 0f),
                    TotalStressReceived = GetFloat(item, "TotalStressReceived", 0f),
                    Kills = GetInt(item, "Kills", 0),
                    Crits = GetInt(item, "Crits", 0),
                    IncomingAttacks = GetInt(item, "IncomingAttacks", 0),
                    AvoidedAttacks = GetInt(item, "AvoidedAttacks", 0),
                    DodgeAvoids = GetInt(item, "DodgeAvoids", 0),
                    MissAvoids = GetInt(item, "MissAvoids", 0),
                });
            }

            return rows;
        }

        private static IList<DamageMeterMpContributionStats> ReadRemoteContributionRows(object value)
        {
            List<DamageMeterMpContributionStats> rows = new List<DamageMeterMpContributionStats>();
            IEnumerable enumerable = AsEnumerable(value);
            if (enumerable == null)
                return rows;

            foreach (object item in enumerable)
            {
                if (item == null)
                    continue;

                rows.Add(new DamageMeterMpContributionStats
                {
                    ActorGuid = GetString(item, "ActorGuid", null),
                    ActorName = GetString(item, "ActorName", null),
                    TeamIndex = GetInt(item, "TeamIndex", -1),
                    BonusDamage = GetFloat(item, "BonusDamage", 0f),
                    VulnerableDamage = GetFloat(item, "VulnerableDamage", 0f),
                    ShieldPrevented = GetFloat(item, "ShieldPrevented", 0f),
                    GuardProtected = GetFloat(item, "GuardProtected", 0f),
                    ShieldWasted = GetInt(item, "ShieldWasted", 0),
                    ComboApplied = GetInt(item, "ComboApplied", 0),
                    ComboConsumed = GetInt(item, "ComboConsumed", 0),
                    TotalContribution = GetFloat(item, "TotalContribution", 0f),
                });
            }

            return rows;
        }

        private static DamageMeterMpStatusTotals ReadRemoteStatusTotals(object value)
        {
            if (value == null)
                return null;

            return new DamageMeterMpStatusTotals
            {
                PlayerBuffApplied = GetInt(value, "PlayerBuffApplied", 0),
                PlayerDebuffApplied = GetInt(value, "PlayerDebuffApplied", 0),
                EnemyBuffApplied = GetInt(value, "EnemyBuffApplied", 0),
                EnemyDebuffApplied = GetInt(value, "EnemyDebuffApplied", 0),
                PlayerStatusRemoved = GetInt(value, "PlayerStatusRemoved", 0),
                EnemyStatusRemoved = GetInt(value, "EnemyStatusRemoved", 0),
                PlayerStatusConsumed = GetInt(value, "PlayerStatusConsumed", 0),
                EnemyStatusConsumed = GetInt(value, "EnemyStatusConsumed", 0),
            };
        }

        private static IList<DamageMeterMpCombatLogEntry> ReadRemoteCombatLogRows(object value)
        {
            List<DamageMeterMpCombatLogEntry> rows = new List<DamageMeterMpCombatLogEntry>();
            IEnumerable enumerable = AsEnumerable(value);
            if (enumerable == null)
                return rows;

            foreach (object item in enumerable)
            {
                if (item == null)
                    continue;

                rows.Add(new DamageMeterMpCombatLogEntry
                {
                    Index = GetInt(item, "Index", rows.Count),
                    Round = GetInt(item, "Round", 0),
                    EntryType = GetString(item, "EntryType", null),
                    SourceName = GetString(item, "SourceName", null),
                    TargetName = GetString(item, "TargetName", null),
                    SourceIsPlayer = GetBool(item, "SourceIsPlayer", false),
                    TargetIsPlayer = GetBool(item, "TargetIsPlayer", false),
                    ActionType = GetString(item, "ActionType", null),
                    Value = GetFloat(item, "Value", 0f),
                    SkillId = GetString(item, "SkillId", null),
                    Extra = GetString(item, "Extra", null),
                    DotType = GetString(item, "DotType", null),
                    OverkillDamage = GetFloat(item, "OverkillDamage", 0f),
                });
            }

            return rows;
        }

        private static DamageMeterMpActorStats CloneActorStats(DamageMeterMpActorStats source)
        {
            if (source == null)
                return null;

            return new DamageMeterMpActorStats
            {
                ActorGuid = source.ActorGuid,
                ActorName = source.ActorName,
                TeamIndex = source.TeamIndex,
                TotalDamageDealt = source.TotalDamageDealt,
                DotDamageDealt = source.DotDamageDealt,
                TotalDamageReceived = source.TotalDamageReceived,
                RawDamageReceived = source.RawDamageReceived,
                OverkillDamageDealt = source.OverkillDamageDealt,
                TotalHealingDone = source.TotalHealingDone,
                TotalHealingReceived = source.TotalHealingReceived,
                TotalStressReceived = source.TotalStressReceived,
                Kills = source.Kills,
                Crits = source.Crits,
                IncomingAttacks = source.IncomingAttacks,
                AvoidedAttacks = source.AvoidedAttacks,
                DodgeAvoids = source.DodgeAvoids,
                MissAvoids = source.MissAvoids,
            };
        }

        private static DamageMeterMpContributionStats CloneContributionStats(DamageMeterMpContributionStats source)
        {
            if (source == null)
                return null;

            return new DamageMeterMpContributionStats
            {
                ActorGuid = source.ActorGuid,
                ActorName = source.ActorName,
                TeamIndex = source.TeamIndex,
                BonusDamage = source.BonusDamage,
                VulnerableDamage = source.VulnerableDamage,
                ShieldPrevented = source.ShieldPrevented,
                GuardProtected = source.GuardProtected,
                ShieldWasted = source.ShieldWasted,
                ComboApplied = source.ComboApplied,
                ComboConsumed = source.ComboConsumed,
                TotalContribution = source.TotalContribution,
            };
        }

        private static DamageMeterMpStatusTotals CloneStatusTotals(DamageMeterMpStatusTotals source)
        {
            if (source == null)
                return null;

            return new DamageMeterMpStatusTotals
            {
                PlayerBuffApplied = source.PlayerBuffApplied,
                PlayerDebuffApplied = source.PlayerDebuffApplied,
                EnemyBuffApplied = source.EnemyBuffApplied,
                EnemyDebuffApplied = source.EnemyDebuffApplied,
                PlayerStatusRemoved = source.PlayerStatusRemoved,
                EnemyStatusRemoved = source.EnemyStatusRemoved,
                PlayerStatusConsumed = source.PlayerStatusConsumed,
                EnemyStatusConsumed = source.EnemyStatusConsumed,
            };
        }

        private static DamageMeterMpCombatLogEntry CloneCombatLogEntry(DamageMeterMpCombatLogEntry source)
        {
            if (source == null)
                return null;

            return new DamageMeterMpCombatLogEntry
            {
                Index = source.Index,
                Round = source.Round,
                EntryType = source.EntryType,
                SourceName = source.SourceName,
                TargetName = source.TargetName,
                SourceIsPlayer = source.SourceIsPlayer,
                TargetIsPlayer = source.TargetIsPlayer,
                ActionType = source.ActionType,
                Value = source.Value,
                SkillId = source.SkillId,
                Extra = source.Extra,
                DotType = source.DotType,
                OverkillDamage = source.OverkillDamage,
            };
        }

        private static IEnumerable AsEnumerable(object value)
        {
            if (value == null || value is string)
                return null;

            return value as IEnumerable;
        }

        private static object GetFirstPropertyValue(object source, params string[] names)
        {
            if (source == null || names == null)
                return null;

            Type type = source.GetType();
            for (int i = 0; i < names.Length; i++)
            {
                PropertyInfo property = type.GetProperty(names[i], BindingFlags.Public | BindingFlags.Instance);
                if (property != null)
                    return property.GetValue(source, null);

                FieldInfo field = type.GetField(names[i], BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                    return field.GetValue(source);
            }

            return null;
        }

        private static string GetString(object source, string name, string fallback)
        {
            object value = GetFirstPropertyValue(source, name);
            if (value == null)
                return fallback;

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int GetInt(object source, string name, int fallback)
        {
            object value = GetFirstPropertyValue(source, name);
            if (value == null)
                return fallback;

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static float GetFloat(object source, string name, float fallback)
        {
            object value = GetFirstPropertyValue(source, name);
            if (value == null)
                return fallback;

            try
            {
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static bool GetBool(object source, string name, bool fallback)
        {
            object value = GetFirstPropertyValue(source, name);
            if (value == null)
                return fallback;

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }
    }

    public sealed class DamageMeterMpSnapshot
    {
        public DamageMeterMpSnapshot()
        {
            Heroes = new List<DamageMeterMpActorStats>();
            Enemies = new List<DamageMeterMpActorStats>();
            Contributions = new List<DamageMeterMpContributionStats>();
            CombatLogEntries = new List<DamageMeterMpCombatLogEntry>();
        }

        public int ApiVersion { get; set; }
        public string ProviderVersion { get; set; }
        public string Capabilities { get; set; }
        public bool IsAvailable { get; set; }
        public bool IsActive { get; set; }
        public string UnavailableReason { get; set; }
        public int Round { get; set; }
        public int Turn { get; set; }
        public string BattleState { get; set; }
        public string CurrentActorGuid { get; set; }
        public string CurrentActorName { get; set; }
        public float PlayerTotalDamage { get; set; }
        public float EnemyTotalDamage { get; set; }
        public IList<DamageMeterMpActorStats> Heroes { get; set; }
        public IList<DamageMeterMpActorStats> Enemies { get; set; }
        public IList<DamageMeterMpContributionStats> Contributions { get; set; }
        public DamageMeterMpStatusTotals StatusTotals { get; set; }
        public IList<DamageMeterMpCombatLogEntry> CombatLogEntries { get; set; }
        public string Digest { get; set; }
    }

    public sealed class DamageMeterMpActorStats
    {
        public string ActorGuid { get; set; }
        public string ActorName { get; set; }
        public int TeamIndex { get; set; }
        public float TotalDamageDealt { get; set; }
        public float DotDamageDealt { get; set; }
        public float TotalDamageReceived { get; set; }
        public float RawDamageReceived { get; set; }
        public float OverkillDamageDealt { get; set; }
        public float TotalHealingDone { get; set; }
        public float TotalHealingReceived { get; set; }
        public float TotalStressReceived { get; set; }
        public int Kills { get; set; }
        public int Crits { get; set; }
        public int IncomingAttacks { get; set; }
        public int AvoidedAttacks { get; set; }
        public int DodgeAvoids { get; set; }
        public int MissAvoids { get; set; }
    }

    public sealed class DamageMeterMpContributionStats
    {
        public string ActorGuid { get; set; }
        public string ActorName { get; set; }
        public int TeamIndex { get; set; }
        public float BonusDamage { get; set; }
        public float VulnerableDamage { get; set; }
        public float ShieldPrevented { get; set; }
        public float GuardProtected { get; set; }
        public int ShieldWasted { get; set; }
        public int ComboApplied { get; set; }
        public int ComboConsumed { get; set; }
        public float TotalContribution { get; set; }
    }

    public sealed class DamageMeterMpStatusTotals
    {
        public int PlayerBuffApplied { get; set; }
        public int PlayerDebuffApplied { get; set; }
        public int EnemyBuffApplied { get; set; }
        public int EnemyDebuffApplied { get; set; }
        public int PlayerStatusRemoved { get; set; }
        public int EnemyStatusRemoved { get; set; }
        public int PlayerStatusConsumed { get; set; }
        public int EnemyStatusConsumed { get; set; }
    }

    public sealed class DamageMeterMpCombatLogEntry
    {
        public int Index { get; set; }
        public int Round { get; set; }
        public string EntryType { get; set; }
        public string SourceName { get; set; }
        public string TargetName { get; set; }
        public bool SourceIsPlayer { get; set; }
        public bool TargetIsPlayer { get; set; }
        public string ActionType { get; set; }
        public float Value { get; set; }
        public string SkillId { get; set; }
        public string Extra { get; set; }
        public string DotType { get; set; }
        public float OverkillDamage { get; set; }
    }
}
