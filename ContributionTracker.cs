using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Actor.ActorContainer;
using Assets.Code.Buff;
using Assets.Code.Buff.Events;
using Assets.Code.Combat;
using Assets.Code.Combat.Events;
using Assets.Code.Dot;
using Assets.Code.Dot.Events;
using Assets.Code.Effect;
using Assets.Code.Library;
using Assets.Code.Rules;
using Assets.Code.Skill.Events;
using Assets.Code.Source;
using Assets.Code.Token;
using Assets.Code.Token.Events;
using Assets.Code.Utils;
using UnityEngine;

namespace DD2DamageMeter
{
    public class ContributionTracker
    {
        public class ContributionStats
        {
            public uint ActorGuid;
            public string ActorName;
            public int TeamIndex;
            public float BonusDamage;
            public float VulnerableDamage;
            public float ShieldPrevented;
            public float GuardProtected;
            public float DotDamagePrevented;
            public int ShieldWasted;
            public int ComboApplied;
            public int ComboConsumed;
            public float TotalContribution => BonusDamage + VulnerableDamage + ShieldPrevented + GuardProtected + DotDamagePrevented;
        }

        private enum ContributionKind
        {
            DamageBonus,
            Vulnerable,
            Shield,
            Guard
        }

        private enum DamageAmplifierGroupKind
        {
            PerformerDamage,
            TargetDamageTaken,
            Crit
        }

        private class ActiveEffect
        {
            public uint TargetGuid;
            public uint ProviderGuid;
            public string EffectId;
            public string SourceId;
            public ContributionKind Kind;
            public float DamageBonusPct;
            public bool Used;
            public bool IsBuff;
            public bool IsFloor;
            public int FloorPlacementId;
        }

        private class DamageAmplifierSource
        {
            public ActiveEffect Effect;
            public DamageAmplifierGroupKind GroupKind;
            public float BonusPct;
        }

        private class DamageAmplifierGroup
        {
            public DamageAmplifierGroupKind Kind;
            public float Multiplier = 1f;
            public readonly List<DamageAmplifierSource> Sources = new List<DamageAmplifierSource>();

            public float WeightSum
            {
                get
                {
                    float sum = 0f;
                    for (int i = 0; i < Sources.Count; i++)
                        sum += Mathf.Max(0f, Sources[i].BonusPct);
                    return sum;
                }
            }
        }

        private class StatusSourceHint
        {
            public uint TargetGuid;
            public string EffectId;
            public string Operation;
            public uint SourceGuid;
            public SourceType SourceType;
            public string SourceId;
            public int Round;
        }

        private class GuardedDot
        {
            public uint TargetGuid;
            public uint ProviderGuid;
            public uint SourceActorGuid;
            public string DotId;
            public string DotType;
            public string SourceId;
            public SourceType SourceType;
            public int Count;
        }

        private class ActiveDotSnapshot
        {
            public uint TargetGuid;
            public string DotId;
            public string DotType;
            public int RemainingTurns;
            public float DamagePerTick;
        }

        private class ActiveCombo
        {
            public uint TargetGuid;
            public uint ProviderGuid;
            public string SourceId;
            public int Round;
        }

        private class PendingComboConsume
        {
            public uint TargetGuid;
            public uint ConsumerGuid;
            public string SkillId;
            public int Round;
        }

        private struct ProjectedHealth
        {
            public int Frame;
            public float Hp;
        }

        private const int MaxStatusHints = 64;

        private readonly object _lock = new object();
        private readonly Dictionary<uint, ContributionStats> _stats = new Dictionary<uint, ContributionStats>();
        private readonly List<ActiveEffect> _activeEffects = new List<ActiveEffect>();
        private readonly Dictionary<uint, List<ActiveEffect>> _pendingDamageEffects = new Dictionary<uint, List<ActiveEffect>>();
        private readonly Dictionary<uint, List<ActiveEffect>> _pendingVulnerableEffects = new Dictionary<uint, List<ActiveEffect>>();
        private readonly Dictionary<uint, List<ActiveEffect>> _pendingShieldEffects = new Dictionary<uint, List<ActiveEffect>>();
        private readonly Dictionary<uint, List<ActiveEffect>> _pendingGuardEffects = new Dictionary<uint, List<ActiveEffect>>();
        private readonly List<GuardedDot> _pendingGuardedDots = new List<GuardedDot>();
        private readonly List<GuardedDot> _activeGuardedDots = new List<GuardedDot>();
        private readonly List<GuardedDot> _expiredGuardedDots = new List<GuardedDot>();
        private readonly Dictionary<uint, ProjectedHealth> _dotProjectedHp = new Dictionary<uint, ProjectedHealth>();
        private readonly Dictionary<uint, List<ActiveDotSnapshot>> _activeDotSnapshots = new Dictionary<uint, List<ActiveDotSnapshot>>();
        private readonly List<StatusSourceHint> _statusHints = new List<StatusSourceHint>();
        private readonly FloorEffectSourceTracker _floorEffectSources;
        private readonly Dictionary<uint, ActiveCombo> _activeCombos = new Dictionary<uint, ActiveCombo>();
        private readonly List<PendingComboConsume> _pendingComboConsumes = new List<PendingComboConsume>();

        private ContributionStats[] _playerSnapshot = Array.Empty<ContributionStats>();
        private bool _snapshotDirty = true;
        private int _currentRound;

        public IReadOnlyList<ContributionStats> PlayerStats => _playerSnapshot;

        public ContributionTracker() : this(null)
        {
        }

        internal ContributionTracker(FloorEffectSourceTracker floorEffectSources)
        {
            _floorEffectSources = floorEffectSources ?? new FloorEffectSourceTracker();
        }

        public void OnBattleBegin(EventBattleBegin evt)
        {
            Reset();
        }

        public void OnBattleStartRound(EventBattleStartRound evt)
        {
            lock (_lock)
            {
                _currentRound = evt.m_Round;
            }
        }

        public void OnSkillFinalizeResults(EventSkillFinalizeResults evt)
        {
            try
            {
                lock (_lock)
                {
                    _pendingGuardedDots.Clear();
                    CacheFinalConsumedDamageEffects(evt);
                    foreach (var ar in evt.ActorResults)
                    {
                        if (ar == null) continue;
                        CacheStatusHints(ar, evt.SkillId ?? "");
                    }
                    CacheComboConsumptionHints(evt);

                    var projectedHp = new Dictionary<uint, float>();
                    foreach (var ar in evt.ActorResults)
                    {
                        if (ar == null) continue;
                        if (DamageTracker.IsCorpseActorPublic(ar.m_TargetActorGuid)) continue;

                        float hpBefore = 0f;
                        bool hasHpBefore = ar.IsDamaging || ar.IsBlocked;
                        if (hasHpBefore)
                            hpBefore = GetProjectedHpBefore(ar.m_TargetActorGuid, Mathf.Max(ar.HealthDamage, ar.BaseHealthDamage), projectedHp);

                        if (ar.IsDamaging)
                        {
                            TrackDirectDamageAmplifierContribution(ar, hpBefore);
                            float effectiveDamage = Mathf.Min(ar.HealthDamage, Mathf.Max(0f, hpBefore));
                            projectedHp[ar.m_TargetActorGuid] = Mathf.Max(0f, hpBefore - effectiveDamage);
                        }

                        if (ar.IsDamaging || ar.IsBlocked)
                        {
                            if (!TrackGuardContribution(ar, hpBefore))
                                TrackShieldContribution(ar, hpBefore);
                        }
                        TrackGuardedDotApplications(ar);
                    }

                    _pendingDamageEffects.Clear();
                    _pendingVulnerableEffects.Clear();
                    _pendingShieldEffects.Clear();
                    _pendingGuardEffects.Clear();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ContributionTracker.OnSkillFinalizeResults error: {ex.Message}");
            }
        }

        public void OnDotAdded(EventDotAdded evt)
        {
            try
            {
                lock (_lock)
                {
                    if (evt.m_Actor == null || evt.m_DotDefinition == null) return;
                    ActivatePendingGuardedDot(
                        evt.m_Actor.ActorGuid,
                        evt.m_DotDefinition.m_Id ?? "",
                        evt.m_DotDefinition.m_Type ?? "",
                        evt.m_SourceType,
                        evt.m_SourceId
                    );
                    SyncDotSnapshots(evt.m_Actor);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ContributionTracker.OnDotAdded error: {ex.Message}");
            }
        }

        public void OnDotRemoved(EventDotRemoved evt)
        {
            try
            {
                lock (_lock)
                {
                    if (evt.Actor == null || evt.Dot == null) return;
                    var removed = new GuardedDot
                    {
                        TargetGuid = evt.Actor.ActorGuid,
                        DotId = evt.Dot.m_Id ?? "",
                        DotType = evt.Dot.m_Type ?? "",
                        Count = 1
                    };

                    if (IsSourceType(evt.Source, "duration"))
                        AddGuardedDot(_expiredGuardedDots, removed);
                    else
                        RemoveActiveGuardedDot(removed.TargetGuid, removed.DotId, removed.DotType, null, null, 1);

                    TrackDotDamagePrevention(evt);
                    SyncDotSnapshots(evt.Actor);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ContributionTracker.OnDotRemoved error: {ex.Message}");
            }
        }

        public void OnDotApplied(EventDotApplied evt)
        {
            try
            {
                lock (_lock)
                {
                    uint targetGuid = evt.m_actorGuid;
                    string dotType = evt.m_dotType ?? "";
                    var result = evt.m_effectApplyCombinedResult;
                    if (result == null)
                    {
                        ApplyExpiredGuardedDots(targetGuid, dotType);
                        SyncDotSnapshotsByGuid(targetGuid);
                        return;
                    }

                    if (DamageTracker.IsCorpseActorPublic(targetGuid))
                    {
                        ApplyExpiredGuardedDots(targetGuid, dotType);
                        SyncDotSnapshotsByGuid(targetGuid);
                        return;
                    }

                    float healthChange = result.HealthChange;
                    if (healthChange < -0.01f)
                    {
                        float rawDotDamage = -healthChange;
                        float effectiveDamage = GetEffectiveDotDamage(targetGuid, rawDotDamage);
                        CountGuardedDotContribution(targetGuid, dotType, result, effectiveDamage);
                    }

                    ApplyExpiredGuardedDots(targetGuid, dotType);
                    SyncDotSnapshotsByGuid(targetGuid);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ContributionTracker.OnDotApplied error: {ex.Message}");
            }
        }

        public void OnTokenAdded(EventTokenAdded evt)
        {
            try
            {
                lock (_lock)
                {
                    var token = GetTokenDefinition(evt.m_TokenId);
                    if (token == null) return;
                    TrackComboAdded(evt, token);
                    TrackVulnerableAdded(evt, token);

                    float bonusPct = GetTokenContributionBonusPct(token);
                    bool isDamageBonus = bonusPct > 0.0001f || IsDamageBonusToken(token) || IsCritToken(token);
                    bool isShield = IsShieldToken(token);
                    bool isGuard = IsGuardToken(token);
                    if (!isDamageBonus && !isShield && !isGuard) return;

                    uint sourceGuid = 0;
                    string sourceId = evt.m_SourceId ?? "";
                    FloorEffectSourceTracker.SourceMarker floorMarker = null;
                    var hint = ConsumeStatusHint(evt.m_ActorGuid, evt.m_TokenId, "ADD", evt.m_SourceId);
                    if (hint != null && IsContributionSource(hint.SourceType, hint.SourceId))
                    {
                        sourceGuid = hint.SourceGuid;
                        sourceId = hint.SourceId ?? sourceId;
                    }

                    if (sourceGuid == 0 &&
                        IsContributionSource(evt.m_SourceType, evt.m_SourceId) &&
                        TryResolveTokenSource(evt.m_ActorGuid, evt.m_TokenId, evt.m_SourceType, evt.m_SourceId, out var resolvedGuid, out var resolvedSourceId))
                    {
                        sourceGuid = resolvedGuid;
                        sourceId = resolvedSourceId ?? sourceId;
                    }

                    if (!IsEligibleFriendlyExternalSource(sourceGuid, evt.m_ActorGuid) &&
                        _floorEffectSources.TryResolveTokenSource(evt.m_ActorGuid, evt.m_TokenId, evt.m_SourceType, evt.m_SourceId, out floorMarker))
                    {
                        sourceGuid = floorMarker.ProviderGuid;
                        if (!string.IsNullOrEmpty(floorMarker.SourceId)) sourceId = floorMarker.SourceId;
                    }

                    if (!IsEligibleFriendlyExternalSource(sourceGuid, evt.m_ActorGuid)) return;

                    int amount = Math.Max(1, evt.m_AddAmount);
                    for (int i = 0; i < amount; i++)
                    {
                        if (isDamageBonus && bonusPct > 0.0001f)
                            AddActiveEffect(evt.m_ActorGuid, sourceGuid, evt.m_TokenId, sourceId, ContributionKind.DamageBonus, bonusPct, false, floorMarker);
                        if (isShield)
                            AddActiveEffect(evt.m_ActorGuid, sourceGuid, evt.m_TokenId, sourceId, ContributionKind.Shield, 0f, false, floorMarker);
                        if (isGuard)
                            AddActiveEffect(evt.m_ActorGuid, sourceGuid, evt.m_TokenId, sourceId, ContributionKind.Guard, 0f, false, floorMarker);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ContributionTracker.OnTokenAdded error: {ex.Message}");
            }
        }

        public void OnTokenConsumed(EventTokenConsumed evt)
        {
            try
            {
                if (evt.m_TokenConsumeType == TokenConsumeType.UNTRACKED) return;
                lock (_lock)
                {
                    var token = GetTokenDefinition(evt.m_TokenId);
                    if (token == null) return;

                    if (IsVulnerableToken(token) && !IsPlayerTeam(evt.m_ActorGuid))
                    {
                        var effect = PopActiveEffect(evt.m_ActorGuid, evt.m_TokenId, ContributionKind.Vulnerable);
                        if (effect != null) AddPending(_pendingVulnerableEffects, evt.m_ActorGuid, effect);
                        return;
                    }

                    if (!IsPlayerTeam(evt.m_ActorGuid)) return;

                    if (IsDamageBonusToken(token) || IsCritToken(token) || GetTokenContributionBonusPct(token) > 0.0001f)
                    {
                        var effect = PopActiveEffect(evt.m_ActorGuid, evt.m_TokenId, ContributionKind.DamageBonus);
                        if (effect != null) AddPending(_pendingDamageEffects, evt.m_ActorGuid, effect);
                    }

                    if (IsShieldToken(token))
                    {
                        var effect = PopActiveEffect(evt.m_ActorGuid, evt.m_TokenId, ContributionKind.Shield);
                        if (effect != null) AddPending(_pendingShieldEffects, evt.m_ActorGuid, effect);
                    }

                    if (IsGuardToken(token))
                    {
                        var effect = PopActiveEffect(evt.m_ActorGuid, evt.m_TokenId, ContributionKind.Guard);
                        if (effect != null) AddPending(_pendingGuardEffects, evt.m_ActorGuid, effect);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ContributionTracker.OnTokenConsumed error: {ex.Message}");
            }
        }

        public void OnTokenRemoved(EventTokenRemoved evt)
        {
            try
            {
                lock (_lock)
                {
                    if (evt.Actor == null || evt.Token == null) return;
                    uint targetGuid = evt.Actor.ActorGuid;
                    if (IsComboToken(evt.Token))
                    {
                        TrackComboRemoved(evt);
                        if (!IsPlayerTeam(targetGuid)) return;
                    }
                    if (IsVulnerableToken(evt.Token))
                    {
                        TrackVulnerableRemoved(evt);
                        if (!IsPlayerTeam(targetGuid)) return;
                    }
                    if (!IsPlayerTeam(targetGuid)) return;

                    bool combatRemoval = IsSourceType(evt.Source, "combat");
                    bool transferRemoval = IsSourceType(evt.Source, "locked_team_position_transfer");
                    if (IsDamageBonusToken(evt.Token) || IsCritToken(evt.Token) || GetTokenContributionBonusPct(evt.Token) > 0.0001f)
                    {
                        var damageEffect = PopActiveEffect(targetGuid, evt.Token.Id, ContributionKind.DamageBonus);
                        if (damageEffect != null)
                        {
                            if (combatRemoval || transferRemoval)
                                AddPending(_pendingDamageEffects, targetGuid, damageEffect);
                            else
                                _snapshotDirty = true;
                        }
                        return;
                    }

                    if (IsShieldToken(evt.Token))
                    {
                        var shieldEffect = PopActiveEffect(targetGuid, evt.Token.Id, ContributionKind.Shield);
                        if (shieldEffect == null) return;

                        if (combatRemoval || transferRemoval)
                        {
                            AddPending(_pendingShieldEffects, targetGuid, shieldEffect);
                        }
                        else if (!shieldEffect.Used)
                        {
                            shieldEffect.Used = true;
                        }
                    }

                    if (IsGuardToken(evt.Token))
                    {
                        var guardEffect = PopActiveEffect(targetGuid, evt.Token.Id, ContributionKind.Guard);
                        if (guardEffect != null && (combatRemoval || transferRemoval))
                            AddPending(_pendingGuardEffects, targetGuid, guardEffect);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ContributionTracker.OnTokenRemoved error: {ex.Message}");
            }
        }

        public void OnBuffAdded(EventBuffAdded evt)
        {
            try
            {
                lock (_lock)
                {
                    if (evt.Buff == null) return;

                    float damagePct = GetDamageBonusPct(evt.Buff);
                    bool isShieldBuff = GetDamageReductionPct(evt.Buff) > 0.0001f;
                    uint providerGuid = evt.PerformerActorGuid;
                    string sourceId = evt.SourceId ?? "";
                    bool markedFloorBuff = _floorEffectSources.TryResolveBuffSource(evt.TargetActorGuid, evt.Buff.Id, evt.SourceType, evt.SourceId, out var floorMarker);
                    if (markedFloorBuff)
                    {
                        if (floorMarker.ProviderGuid != 0) providerGuid = floorMarker.ProviderGuid;
                        if (!string.IsNullOrEmpty(floorMarker.SourceId)) sourceId = floorMarker.SourceId;
                    }

                    if (!IsEligibleFriendlyExternalSource(providerGuid, evt.TargetActorGuid)) return;

                    if (damagePct > 0.0001f)
                    {
                        AddActiveEffect(evt.TargetActorGuid, providerGuid, evt.Buff.Id, sourceId, ContributionKind.DamageBonus, damagePct, true, markedFloorBuff ? floorMarker : null);
                    }

                    if (isShieldBuff)
                    {
                        AddActiveEffect(evt.TargetActorGuid, providerGuid, evt.Buff.Id, sourceId, ContributionKind.Shield, 0f, true, markedFloorBuff ? floorMarker : null);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ContributionTracker.OnBuffAdded error: {ex.Message}");
            }
        }

        public void OnBuffRemoved(EventBuffRemoved evt)
        {
            try
            {
                lock (_lock)
                {
                    if (evt.Buff == null) return;
                    bool transferRemoval = IsSourceType(evt.Source, "locked_team_position_transfer");
                    var damageEffect = PopActiveEffect(evt.ActorGuid, evt.Buff.Id, ContributionKind.DamageBonus, false);
                    var shieldEffect = PopActiveEffect(evt.ActorGuid, evt.Buff.Id, ContributionKind.Shield, false);
                    if (damageEffect != null)
                    {
                        if (transferRemoval)
                            AddPending(_pendingDamageEffects, evt.ActorGuid, damageEffect);
                        else
                            _snapshotDirty = true;
                    }

                    if (shieldEffect != null)
                    {
                        if (transferRemoval)
                            AddPending(_pendingShieldEffects, evt.ActorGuid, shieldEffect);
                        else if (!shieldEffect.Used)
                            shieldEffect.Used = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ContributionTracker.OnBuffRemoved error: {ex.Message}");
            }
        }

        public void RefreshSnapshot()
        {
            lock (_lock)
            {
                if (!_snapshotDirty) return;
                var players = new List<ContributionStats>();
                foreach (var kvp in _stats)
                    players.Add(Clone(kvp.Value));
                players.Sort(CompareContributionRows);
                _playerSnapshot = players.ToArray();
                _snapshotDirty = false;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _stats.Clear();
                _activeEffects.Clear();
                _pendingDamageEffects.Clear();
                _pendingVulnerableEffects.Clear();
                _pendingShieldEffects.Clear();
                _pendingGuardEffects.Clear();
                _pendingGuardedDots.Clear();
                _activeGuardedDots.Clear();
                _expiredGuardedDots.Clear();
                _dotProjectedHp.Clear();
                _activeDotSnapshots.Clear();
                _statusHints.Clear();
                _floorEffectSources.Reset();
                _activeCombos.Clear();
                _pendingComboConsumes.Clear();
                _playerSnapshot = Array.Empty<ContributionStats>();
                _snapshotDirty = true;
                _currentRound = 0;
            }
        }

        private void TrackDirectDamageAmplifierContribution(Assets.Code.Skill.SkillCalculation.ActorResult ar, float hpBefore)
        {
            uint performerGuid = ar.m_PerformerActorGuid;
            uint targetGuid = ar.m_TargetActorGuid;
            if (performerGuid == 0 || targetGuid == 0 || performerGuid == targetGuid) return;
            if (!IsPlayerTeam(performerGuid) || IsPlayerTeam(targetGuid)) return;

            float totalDamage = Mathf.Max(0f, ar.HealthDamage);
            float effectiveDamage = Mathf.Min(totalDamage, Mathf.Max(0f, hpBefore));
            if (totalDamage <= 0.0001f || effectiveDamage <= 0.0001f) return;

            var groups = BuildDamageAmplifierGroups(
                GetDamageEffectsForActor(performerGuid),
                GetVulnerableEffectsForTarget(targetGuid),
                GetActorResultCritScore(ar));
            if (groups.Count == 0) return;

            var groupContributions = CalculateShapleyGroupContributions(groups, totalDamage, effectiveDamage);

            bool changed = false;
            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                float groupContribution = groupContributions[groupIndex];
                if (groupContribution <= 0.0001f) continue;

                DamageAmplifierGroup group = groups[groupIndex];
                float weightSum = group.WeightSum;
                if (weightSum <= 0.0001f) continue;

                for (int sourceIndex = 0; sourceIndex < group.Sources.Count; sourceIndex++)
                {
                    DamageAmplifierSource source = group.Sources[sourceIndex];
                    if (source?.Effect == null || source.Effect.ProviderGuid == 0) continue;
                    float sourceWeight = Mathf.Max(0f, source.BonusPct);
                    if (sourceWeight <= 0.0001f) continue;

                    float share = groupContribution * sourceWeight / weightSum;
                    if (share <= 0.0001f) continue;

                    var stats = GetOrCreate(source.Effect.ProviderGuid);
                    if (group.Kind == DamageAmplifierGroupKind.TargetDamageTaken)
                        stats.VulnerableDamage += share;
                    else
                        stats.BonusDamage += share;
                    source.Effect.Used = true;
                    changed = true;
                }
            }

            if (changed)
                _snapshotDirty = true;
        }

        private static List<DamageAmplifierGroup> BuildDamageAmplifierGroups(List<ActiveEffect> damageEffects, List<ActiveEffect> vulnerableEffects, int critScore)
        {
            var groups = new List<DamageAmplifierGroup>();
            AddDamageAmplifierSources(groups, damageEffects, DamageAmplifierGroupKind.PerformerDamage, critScore);
            AddDamageAmplifierSources(groups, vulnerableEffects, DamageAmplifierGroupKind.TargetDamageTaken, critScore);
            return groups;
        }

        private static void AddDamageAmplifierSources(List<DamageAmplifierGroup> groups, List<ActiveEffect> effects, DamageAmplifierGroupKind defaultGroupKind, int critScore)
        {
            if (effects == null || effects.Count == 0) return;

            for (int i = 0; i < effects.Count; i++)
            {
                ActiveEffect effect = effects[i];
                if (effect == null || effect.ProviderGuid == 0) continue;

                DamageAmplifierGroupKind kind = defaultGroupKind;
                float bonusPct = Mathf.Max(0f, effect.DamageBonusPct);
                if (effect.Kind == ContributionKind.Vulnerable)
                {
                    kind = DamageAmplifierGroupKind.TargetDamageTaken;
                    if (bonusPct <= 0.0001f)
                        bonusPct = GetVulnerableDamageBonusPct(effect.EffectId);
                }
                else if (IsCritTokenId(effect.EffectId))
                {
                    kind = DamageAmplifierGroupKind.Crit;
                    if (bonusPct <= 0.0001f)
                        bonusPct = GetCritDamageBonusPct(critScore);
                }

                if (bonusPct <= 0.0001f) continue;

                DamageAmplifierGroup group = FindOrAddGroup(groups, kind);
                group.Sources.Add(new DamageAmplifierSource
                {
                    Effect = effect,
                    GroupKind = kind,
                    BonusPct = bonusPct
                });
            }

            for (int i = 0; i < groups.Count; i++)
            {
                DamageAmplifierGroup group = groups[i];
                if (group.Sources.Count == 0) continue;

                if (group.Kind == DamageAmplifierGroupKind.Crit)
                {
                    float max = 0f;
                    for (int sourceIndex = 0; sourceIndex < group.Sources.Count; sourceIndex++)
                        max = Mathf.Max(max, group.Sources[sourceIndex].BonusPct);
                    group.Multiplier = 1f + max;
                }
                else
                {
                    group.Multiplier = 1f + group.WeightSum;
                }
            }
        }

        private static DamageAmplifierGroup FindOrAddGroup(List<DamageAmplifierGroup> groups, DamageAmplifierGroupKind kind)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].Kind == kind) return groups[i];
            }

            var group = new DamageAmplifierGroup { Kind = kind };
            groups.Add(group);
            return group;
        }

        private static float[] CalculateShapleyGroupContributions(List<DamageAmplifierGroup> groups, float totalDamage, float effectiveDamage)
        {
            int count = groups.Count;
            var contributions = new float[count];
            if (count == 0) return contributions;

            float trackedMultiplier = 1f;
            for (int i = 0; i < count; i++)
                trackedMultiplier *= Mathf.Max(0.0001f, groups[i].Multiplier);
            if (trackedMultiplier <= 0.0001f) return contributions;

            float baselineDamage = totalDamage / trackedMultiplier;
            int subsetCount = 1 << count;
            var values = new float[subsetCount];
            for (int mask = 0; mask < subsetCount; mask++)
            {
                float multiplier = 1f;
                for (int i = 0; i < count; i++)
                {
                    if ((mask & (1 << i)) != 0)
                        multiplier *= Mathf.Max(0.0001f, groups[i].Multiplier);
                }
                values[mask] = Mathf.Min(baselineDamage * multiplier, effectiveDamage);
            }

            float factorialN = Factorial(count);
            for (int i = 0; i < count; i++)
            {
                float contribution = 0f;
                int bit = 1 << i;
                for (int mask = 0; mask < subsetCount; mask++)
                {
                    if ((mask & bit) != 0) continue;
                    int subsetSize = CountBits(mask);
                    float weight = Factorial(subsetSize) * Factorial(count - subsetSize - 1) / factorialN;
                    contribution += weight * (values[mask | bit] - values[mask]);
                }
                contributions[i] = Mathf.Max(0f, contribution);
            }
            return contributions;
        }

        private static int CountBits(int value)
        {
            int count = 0;
            while (value != 0)
            {
                value &= value - 1;
                count++;
            }
            return count;
        }

        private static float Factorial(int value)
        {
            float result = 1f;
            for (int i = 2; i <= value; i++)
                result *= i;
            return result;
        }

        private void CacheFinalConsumedDamageEffects(EventSkillFinalizeResults evt)
        {
            var skillResult = GetSkillResult(evt);
            if (skillResult == null) return;

            foreach (uint actorGuid in skillResult.GetTokensToRemoveActorGuids())
            {
                var tokenInstances = skillResult.GetTokensToRemoveTokenInstances(actorGuid);
                if (tokenInstances == null) continue;

                foreach (var tokenInstance in tokenInstances)
                {
                    var token = tokenInstance?.Definition;
                    if (token == null) continue;
                    uint tokenSourceGuid = tokenInstance.SourceActorGuid;
                    string tokenSourceId = tokenInstance.SourceId ?? "";
                    if (_floorEffectSources.TryGetSource(tokenInstance, out var tokenMarker))
                    {
                        tokenSourceGuid = tokenMarker.ProviderGuid;
                        if (!string.IsNullOrEmpty(tokenMarker.SourceId))
                            tokenSourceId = tokenMarker.SourceId;
                    }
                    else if (_floorEffectSources.TryResolveTokenSource(actorGuid, token.Id, tokenInstance.SourceType, tokenInstance.SourceId, out tokenMarker))
                    {
                        tokenSourceGuid = tokenMarker.ProviderGuid;
                        if (!string.IsNullOrEmpty(tokenMarker.SourceId))
                            tokenSourceId = tokenMarker.SourceId;
                    }

                    if (IsVulnerableToken(token))
                    {
                        if (!IsPlayerTeam(actorGuid) && IsPlayerTeam(tokenSourceGuid))
                        {
                            var effect = new ActiveEffect
                            {
                                TargetGuid = actorGuid,
                                ProviderGuid = tokenSourceGuid,
                                EffectId = token.Id ?? "",
                                SourceId = tokenSourceId,
                                Kind = ContributionKind.Vulnerable,
                                IsBuff = false
                            };
                            ApplyFloorMarker(effect, tokenMarker);
                            AddConsumedEffectFromSkillResult(_pendingVulnerableEffects, actorGuid, effect);
                        }
                        continue;
                    }

                    if (!IsPlayerTeam(actorGuid)) continue;
                    float bonusPct = GetTokenContributionBonusPct(token);
                    if (bonusPct <= 0.0001f) continue;
                    if (!IsDamageBonusToken(token) && !IsCritToken(token) && bonusPct <= 0.0001f) continue;
                    if (!IsEligibleFriendlyExternalSource(tokenSourceGuid, actorGuid)) continue;

                    var consumedEffect = new ActiveEffect
                    {
                        TargetGuid = actorGuid,
                        ProviderGuid = tokenSourceGuid,
                        EffectId = token.Id ?? "",
                        SourceId = tokenSourceId,
                        Kind = ContributionKind.DamageBonus,
                        DamageBonusPct = bonusPct,
                        IsBuff = false
                    };
                    ApplyFloorMarker(consumedEffect, tokenMarker);
                    AddConsumedEffectFromSkillResult(_pendingDamageEffects, actorGuid, consumedEffect);
                }
            }
        }

        private static Assets.Code.Skill.SkillCalculation.SkillResult GetSkillResult(EventSkillFinalizeResults evt)
        {
            try
            {
                if (evt == null) return null;
                EnsureSkillResultReflection();
                return _skillResultField?.GetValue(evt) as Assets.Code.Skill.SkillCalculation.SkillResult;
            }
            catch { return null; }
        }

        private static void EnsureSkillResultReflection()
        {
            if (_skillResultReflectionInit) return;
            _skillResultReflectionInit = true;
            _skillResultField = typeof(EventSkillFinalizeResults).GetField("m_SkillResult", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private void TrackShieldContribution(Assets.Code.Skill.SkillCalculation.ActorResult ar, float hpBefore)
        {
            uint performerGuid = ar.m_PerformerActorGuid;
            uint targetGuid = ar.m_TargetActorGuid;
            if (targetGuid == 0 || !IsPlayerTeam(targetGuid) || IsPlayerTeam(performerGuid)) return;

            float rawEffective = Mathf.Min(Mathf.Max(0f, ar.BaseHealthDamage), Mathf.Max(0f, hpBefore));
            float actualEffective = Mathf.Min(Mathf.Max(0f, ar.HealthDamage), Mathf.Max(0f, hpBefore));
            float prevented = Mathf.Max(0f, rawEffective - actualEffective);

            List<ActiveEffect> effects;
            if (!_pendingShieldEffects.TryGetValue(targetGuid, out effects) || effects.Count == 0)
            {
                effects = GetActiveShieldEffectsForActor(targetGuid);
            }
            else
            {
                var capped = new List<ActiveEffect>();
                AddEffectsWithFloorCaps(capped, effects);
                effects = capped;
            }

            if (effects.Count == 0) return;

            if (prevented <= 0.0001f)
            {
                for (int i = 0; i < effects.Count; i++)
                    effects[i].Used = true;
                return;
            }

            float share = prevented / effects.Count;
            for (int i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                var stats = GetOrCreate(effect.ProviderGuid);
                stats.ShieldPrevented += share;
                effect.Used = true;
            }
            _snapshotDirty = true;
        }

        private bool TrackGuardContribution(Assets.Code.Skill.SkillCalculation.ActorResult ar, float hpBefore)
        {
            if (!ar.IsGuarding) return false;

            uint guarderGuid = ar.m_TargetActorGuid;
            uint guardedGuid = ar.m_GuardingActorGuid;
            uint attackerGuid = ar.m_PerformerActorGuid;
            if (guarderGuid == 0 || guardedGuid == 0 || guarderGuid == guardedGuid) return false;
            if (!IsPlayerTeam(guarderGuid) || !IsPlayerTeam(guardedGuid) || IsPlayerTeam(attackerGuid)) return false;

            var guardEffects = GetPendingGuardEffects(guardedGuid, guarderGuid);
            if (guardEffects.Count == 0 && IsSkillSource(ar.m_GuardingSourceType))
            {
                guardEffects.Add(new ActiveEffect
                {
                    TargetGuid = guardedGuid,
                    ProviderGuid = guarderGuid,
                    EffectId = "guard",
                    Kind = ContributionKind.Guard
                });
            }
            if (guardEffects.Count == 0) return false;

            float rawEffective = Mathf.Min(Mathf.Max(0f, ar.BaseHealthDamage), Mathf.Max(0f, hpBefore));
            float actualEffective = Mathf.Min(Mathf.Max(0f, ar.HealthDamage), Mathf.Max(0f, hpBefore));
            float prevented = Mathf.Max(0f, rawEffective - actualEffective);

            float guardShare = actualEffective / guardEffects.Count;
            float shieldShare = prevented / guardEffects.Count;
            for (int i = 0; i < guardEffects.Count; i++)
            {
                var effect = guardEffects[i];
                var stats = GetOrCreate(effect.ProviderGuid);
                if (guardShare > 0.0001f) stats.GuardProtected += guardShare;
                if (shieldShare > 0.0001f) stats.ShieldPrevented += shieldShare;
                effect.Used = true;
            }

            MarkPendingShieldsUsed(guarderGuid);
            _snapshotDirty = true;
            return true;
        }

        private void TrackGuardedDotApplications(Assets.Code.Skill.SkillCalculation.ActorResult ar)
        {
            if (!TryGetGuardContext(ar, out var guarderGuid, out var guardedGuid, out var attackerGuid)) return;
            if (!HasGuardContributionSource(guardedGuid, guarderGuid, ar.m_GuardingSourceType)) return;
            if (ar.m_AppliedEffectsOutputContainer == null) return;

            foreach (var output in ar.m_AppliedEffectsOutputContainer.Outputs)
            {
                if (output == null || output.m_TargetActor == null) continue;
                if (output.m_TargetActor.ActorGuid != guarderGuid) continue;

                uint sourceActorGuid = output.m_PerformerActor != null ? output.m_PerformerActor.ActorGuid : attackerGuid;
                if (sourceActorGuid == 0 || sourceActorGuid != attackerGuid) continue;

                foreach (var effect in output.EffectInstancesToApply)
                {
                    if (effect?.EffectDefinition == null || !effect.EffectDefinition.HasDotAdd) continue;
                    var dot = GetDotDefinition(effect.EffectDefinition.m_DotAddId);
                    if (dot == null) continue;
                    int amount = Math.Max(1, effect.DotAddAmount);
                    AddGuardedDot(_pendingGuardedDots, new GuardedDot
                    {
                        TargetGuid = guarderGuid,
                        ProviderGuid = guarderGuid,
                        SourceActorGuid = sourceActorGuid,
                        DotId = dot.m_Id ?? "",
                        DotType = dot.m_Type ?? "",
                        SourceType = effect.SourceType,
                        SourceId = effect.SourceId ?? "",
                        Count = amount
                    });
                }
            }
        }

        private bool TryGetGuardContext(Assets.Code.Skill.SkillCalculation.ActorResult ar, out uint guarderGuid, out uint guardedGuid, out uint attackerGuid)
        {
            guarderGuid = 0;
            guardedGuid = 0;
            attackerGuid = 0;
            if (ar == null || !ar.IsGuarding) return false;

            guarderGuid = ar.m_TargetActorGuid;
            guardedGuid = ar.m_GuardingActorGuid;
            attackerGuid = ar.m_PerformerActorGuid;
            if (guarderGuid == 0 || guardedGuid == 0 || attackerGuid == 0 || guarderGuid == guardedGuid) return false;
            return IsPlayerTeam(guarderGuid) && IsPlayerTeam(guardedGuid) && !IsPlayerTeam(attackerGuid);
        }

        private bool HasGuardContributionSource(uint guardedGuid, uint guarderGuid, SourceType guardingSourceType)
        {
            if (GetPendingGuardEffects(guardedGuid, guarderGuid).Count > 0) return true;
            return IsSkillSource(guardingSourceType);
        }

        private void ActivatePendingGuardedDot(uint targetGuid, string dotId, string dotType, SourceType sourceType, string sourceId)
        {
            for (int i = 0; i < _pendingGuardedDots.Count; i++)
            {
                var pending = _pendingGuardedDots[i];
                if (!GuardedDotMatches(pending, targetGuid, dotId, dotType, sourceType, sourceId)) continue;

                AddGuardedDot(_activeGuardedDots, new GuardedDot
                {
                    TargetGuid = pending.TargetGuid,
                    ProviderGuid = pending.ProviderGuid,
                    SourceActorGuid = pending.SourceActorGuid,
                    DotId = pending.DotId,
                    DotType = pending.DotType,
                    SourceType = pending.SourceType,
                    SourceId = pending.SourceId,
                    Count = 1
                });

                pending.Count--;
                if (pending.Count <= 0)
                    _pendingGuardedDots.RemoveAt(i);
                return;
            }
        }

        private void CountGuardedDotContribution(uint targetGuid, string dotType, Assets.Code.Effect.EffectApplyCombinedResult result, float effectiveDamage)
        {
            if (effectiveDamage <= 0.0001f) return;
            var (performerGuids, sourceIds) = ExtractDotTickSources(result);
            int totalTickUnits = Math.Max(1, Math.Max(performerGuids.Count, sourceIds.Count));
            var shares = new Dictionary<uint, int>();
            int guardedTickUnits = 0;

            for (int i = 0; i < _activeGuardedDots.Count; i++)
            {
                var active = _activeGuardedDots[i];
                if (active.TargetGuid != targetGuid) continue;
                if (!DotTypeMatches(active.DotType, dotType)) continue;
                if (!PerformerMatches(active.SourceActorGuid, performerGuids)) continue;
                if (!SourceIdMatchesAny(active.SourceId, sourceIds)) continue;

                int count = Math.Max(0, active.Count);
                if (count <= 0) continue;
                guardedTickUnits += count;
                if (!shares.ContainsKey(active.ProviderGuid)) shares[active.ProviderGuid] = 0;
                shares[active.ProviderGuid] += count;
            }

            if (guardedTickUnits <= 0) return;
            int matchedGuardedTickUnits = guardedTickUnits;
            int countedGuardedTickUnits = Math.Min(matchedGuardedTickUnits, totalTickUnits);
            float guardedDamage = effectiveDamage * countedGuardedTickUnits / totalTickUnits;

            foreach (var kvp in shares)
            {
                float share = guardedDamage * kvp.Value / matchedGuardedTickUnits;
                if (share <= 0.0001f) continue;
                var stats = GetOrCreate(kvp.Key);
                stats.GuardProtected += share;
            }
            _snapshotDirty = true;
        }

        private void ApplyExpiredGuardedDots(uint targetGuid, string dotType)
        {
            for (int i = _expiredGuardedDots.Count - 1; i >= 0; i--)
            {
                var expired = _expiredGuardedDots[i];
                if (expired.TargetGuid != targetGuid) continue;
                if (!DotTypeMatches(expired.DotType, dotType)) continue;
                RemoveActiveGuardedDot(expired.TargetGuid, expired.DotId, expired.DotType, null, null, expired.Count);
                _expiredGuardedDots.RemoveAt(i);
            }
        }

        private float GetEffectiveDotDamage(uint targetGuid, float rawDamage)
        {
            if (rawDamage <= 0f) return 0f;
            int frame = Time.frameCount;
            float hp;
            if (_dotProjectedHp.TryGetValue(targetGuid, out var projected) && projected.Frame == frame)
            {
                hp = projected.Hp;
            }
            else
            {
                hp = DamageTracker.TryResolveHpRawPublic(targetGuid, out var resolvedHp) ? Mathf.Max(0f, resolvedHp) : rawDamage;
            }

            float effective = Mathf.Min(rawDamage, Mathf.Max(0f, hp));
            _dotProjectedHp[targetGuid] = new ProjectedHealth { Frame = frame, Hp = Mathf.Max(0f, hp - effective) };
            return effective;
        }

        private void TrackDotDamagePrevention(EventDotRemoved evt)
        {
            if (evt?.Actor == null || evt.Dot == null) return;
            uint targetGuid = evt.Actor.ActorGuid;
            uint sourceGuid = evt.SourceActorGuid;
            string dotId = evt.Dot.m_Id ?? "";
            string dotType = evt.Dot.m_Type ?? "";
            ActiveDotSnapshot snapshot = PopActiveDotSnapshot(targetGuid, dotId, dotType);

            if (snapshot == null) return;
            if (snapshot.DamagePerTick <= 0.0001f || snapshot.RemainingTurns <= 0) return;
            if (evt.Dot.IsHoT) return;
            if (sourceGuid == 0 || !IsPlayerTeam(sourceGuid) || !IsPlayerTeam(targetGuid)) return;
            if (!IsSkillSource(evt.Source, evt.SourceId)) return;

            float prevented = snapshot.DamagePerTick * snapshot.RemainingTurns;
            if (prevented <= 0.0001f) return;

            var stats = GetOrCreate(sourceGuid);
            stats.DotDamagePrevented += prevented;
            _snapshotDirty = true;
        }

        private void SyncDotSnapshotsByGuid(uint actorGuid)
        {
            var actor = TryResolveActor(actorGuid);
            if (actor != null) SyncDotSnapshots(actor);
            else _activeDotSnapshots.Remove(actorGuid);
        }

        private void SyncDotSnapshots(ActorInstance actor)
        {
            if (actor == null || actor.ActorGuid == 0)
                return;

            uint targetGuid = actor.ActorGuid;
            if (actor.TeamIndex != 0 || actor.DotContainer == null)
            {
                _activeDotSnapshots.Remove(targetGuid);
                return;
            }

            var snapshots = new List<ActiveDotSnapshot>();
            try
            {
                var dots = actor.DotContainer.GetInstances();
                if (dots != null)
                {
                    foreach (var dot in dots)
                    {
                        if (dot?.Definition == null) continue;
                        if (dot.Definition.IsHoT) continue;

                        int remainingTurns = Mathf.Max(0, dot.GetDurationAmount());
                        if (remainingTurns <= 0) continue;

                        float damagePerTick = EstimateDotDamagePerTick(actor, dot);
                        if (damagePerTick <= 0.0001f) continue;

                        snapshots.Add(new ActiveDotSnapshot
                        {
                            TargetGuid = targetGuid,
                            DotId = dot.Definition.m_Id ?? "",
                            DotType = dot.Definition.m_Type ?? "",
                            RemainingTurns = remainingTurns,
                            DamagePerTick = damagePerTick
                        });
                    }
                }
            }
            catch
            {
            }

            if (snapshots.Count > 0)
                _activeDotSnapshots[targetGuid] = snapshots;
            else
                _activeDotSnapshots.Remove(targetGuid);
        }

        private ActiveDotSnapshot PopActiveDotSnapshot(uint targetGuid, string dotId, string dotType)
        {
            if (!_activeDotSnapshots.TryGetValue(targetGuid, out var snapshots) || snapshots == null)
                return null;

            int fallbackIndex = -1;
            for (int i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                if (snapshot == null) continue;

                bool idMatches = !string.IsNullOrEmpty(snapshot.DotId) &&
                                 !string.IsNullOrEmpty(dotId) &&
                                 string.Equals(snapshot.DotId, dotId, StringComparison.OrdinalIgnoreCase);
                bool typeMatches = DotTypeMatches(snapshot.DotType, dotType);
                if (!idMatches && !typeMatches) continue;

                if (idMatches)
                {
                    snapshots.RemoveAt(i);
                    if (snapshots.Count == 0) _activeDotSnapshots.Remove(targetGuid);
                    return snapshot;
                }

                if (fallbackIndex < 0) fallbackIndex = i;
            }

            if (fallbackIndex >= 0)
            {
                var snapshot = snapshots[fallbackIndex];
                snapshots.RemoveAt(fallbackIndex);
                if (snapshots.Count == 0) _activeDotSnapshots.Remove(targetGuid);
                return snapshot;
            }

            return null;
        }

        private static float EstimateDotDamagePerTick(ActorInstance targetActor, DotInstance dot)
        {
            if (targetActor == null || dot?.Definition == null || dot.Definition.m_Effects == null)
                return 0f;

            float total = 0f;
            foreach (var effect in dot.Definition.m_Effects)
            {
                if (effect == null || !effect.HasHealthDamage) continue;

                float hpMax = 0f;
                try
                {
                    hpMax = targetActor.GetHpMax(effect.m_IncludeWoundInMaxHp, false);
                }
                catch
                {
                    hpMax = 0f;
                }

                float damage = 0f;
                damage += GetDotApplyValue(effect.m_HealthDamageAmount, effect.m_HealthDamageAmountRange, dot.m_EffectValueChange, dot.m_EffectValueMultiplier);

                float damagePct = GetDotApplyValue(effect.m_HealthDamagePercent, effect.m_HealthDamagePercentRange, dot.m_EffectValueChange, dot.m_EffectValueMultiplier);
                if (damagePct > 0f && hpMax > 0f)
                    damage += hpMax * damagePct;

                float downToPct = GetDotApplyValue(effect.m_HealthDamageDownToPercent, effect.m_HealthDamageDownToPercentRange, dot.m_EffectValueChange, dot.m_EffectValueMultiplier);
                if (downToPct > 0f && hpMax > 0f)
                    damage += Mathf.Max(targetActor.HpRaw - Assets.Code.Math.MathUtils.Round(downToPct * hpMax), 0f);

                if (damage > 0f)
                    total += Assets.Code.Math.MathUtils.Round(damage);
            }

            return Mathf.Max(0f, total);
        }

        private static float GetDotApplyValue(float effectValue, float effectRange, float valueChange, float valueMultiplier)
        {
            if (Mathf.Abs(effectValue) <= 0.000001f && effectRange <= 0f)
                return 0f;

            float value = effectValue + valueChange;
            if (effectRange > 0f)
                value += effectRange * 0.5f;

            return value * valueMultiplier;
        }

        private static void AddGuardedDot(List<GuardedDot> list, GuardedDot dot)
        {
            if (dot == null || dot.Count <= 0) return;
            for (int i = 0; i < list.Count; i++)
            {
                var existing = list[i];
                if (existing.TargetGuid != dot.TargetGuid ||
                    existing.ProviderGuid != dot.ProviderGuid ||
                    existing.SourceActorGuid != dot.SourceActorGuid ||
                    !string.Equals(existing.DotId ?? "", dot.DotId ?? "", StringComparison.OrdinalIgnoreCase) ||
                    !DotTypeMatches(existing.DotType, dot.DotType) ||
                    !SourceTypeMatches(existing.SourceType, dot.SourceType) ||
                    !string.Equals(existing.SourceId ?? "", dot.SourceId ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                existing.Count += dot.Count;
                return;
            }
            list.Add(dot);
        }

        private void RemoveActiveGuardedDot(uint targetGuid, string dotId, string dotType, List<uint> performerGuids, List<string> sourceIds, int count)
        {
            int remaining = Math.Max(1, count);
            for (int i = _activeGuardedDots.Count - 1; i >= 0 && remaining > 0; i--)
            {
                var active = _activeGuardedDots[i];
                if (active.TargetGuid != targetGuid) continue;
                if (!DotIdOrTypeMatches(active, dotId, dotType)) continue;
                if (!PerformerMatches(active.SourceActorGuid, performerGuids)) continue;
                if (!SourceIdMatchesAny(active.SourceId, sourceIds)) continue;

                int used = Math.Min(active.Count, remaining);
                active.Count -= used;
                remaining -= used;
                if (active.Count <= 0)
                    _activeGuardedDots.RemoveAt(i);
            }
        }

        private static bool GuardedDotMatches(GuardedDot dot, uint targetGuid, string dotId, string dotType, SourceType sourceType, string sourceId)
        {
            return dot.TargetGuid == targetGuid &&
                   DotIdOrTypeMatches(dot, dotId, dotType) &&
                   SourceTypeMatches(dot.SourceType, sourceType) &&
                   SourceIdMatches(dot.SourceId, sourceId);
        }

        private static bool DotIdOrTypeMatches(GuardedDot dot, string dotId, string dotType)
        {
            return (!string.IsNullOrEmpty(dot.DotId) && !string.IsNullOrEmpty(dotId) &&
                   string.Equals(dot.DotId, dotId, StringComparison.OrdinalIgnoreCase)) ||
                   DotTypeMatches(dot.DotType, dotType);
        }

        private static bool DotTypeMatches(string left, string right)
        {
            return !string.IsNullOrEmpty(left) &&
                   !string.IsNullOrEmpty(right) &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SourceTypeMatches(SourceType left, SourceType right)
        {
            if (left == null || right == null) return true;
            try
            {
                return string.Equals(left.GetName(), right.GetName(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool PerformerMatches(uint sourceActorGuid, List<uint> performerGuids)
        {
            return performerGuids == null ||
                   performerGuids.Count == 0 ||
                   performerGuids.Contains(sourceActorGuid);
        }

        private static bool SourceIdMatchesAny(string sourceId, List<string> sourceIds)
        {
            if (sourceIds == null || sourceIds.Count == 0 || string.IsNullOrEmpty(sourceId)) return true;
            for (int i = 0; i < sourceIds.Count; i++)
            {
                if (SourceIdMatches(sourceId, sourceIds[i])) return true;
            }
            return false;
        }

        private static (List<uint> performerGuids, List<string> sourceIds) ExtractDotTickSources(Assets.Code.Effect.EffectApplyCombinedResult result)
        {
            var performerGuids = new List<uint>();
            var sourceIds = new List<string>();
            try
            {
                EnsureDotResultReflection();
                if (_dotChangeAmountsField == null) return (performerGuids, sourceIds);
                var changeAmounts = _dotChangeAmountsField.GetValue(result) as System.Collections.IDictionary;
                if (changeAmounts == null) return (performerGuids, sourceIds);
                foreach (var entry in changeAmounts)
                {
                    var valueProp = entry.GetType().GetProperty("Value");
                    if (valueProp == null) continue;
                    var changeAmount = valueProp.GetValue(entry);
                    if (changeAmount == null) continue;

                    var guids = _dotPerformerGuidsField?.GetValue(changeAmount) as System.Collections.IList;
                    if (guids != null)
                    {
                        foreach (var item in guids)
                        {
                            if (item is uint guid) performerGuids.Add(guid);
                        }
                    }

                    var ids = _dotSourceIdsField?.GetValue(changeAmount) as System.Collections.IList;
                    if (ids != null)
                    {
                        foreach (var item in ids)
                        {
                            if (item is string id) sourceIds.Add(id ?? "");
                        }
                    }
                }
            }
            catch { }
            return (performerGuids, sourceIds);
        }

        private static void EnsureDotResultReflection()
        {
            if (_dotResultReflectionInit) return;
            _dotResultReflectionInit = true;
            var resultType = typeof(Assets.Code.Effect.EffectApplyCombinedResult);
            _dotChangeAmountsField = resultType.GetField("m_ChangeAmounts", BindingFlags.NonPublic | BindingFlags.Instance);
            var changeAmountType = resultType.GetNestedType("ChangeAmount", BindingFlags.NonPublic);
            if (changeAmountType == null) return;
            _dotPerformerGuidsField = changeAmountType.GetField("m_PerformerActorGuids", BindingFlags.Public | BindingFlags.Instance);
            _dotSourceIdsField = changeAmountType.GetField("m_SourceIds", BindingFlags.Public | BindingFlags.Instance);
        }

        private List<ActiveEffect> GetDamageEffectsForActor(uint actorGuid)
        {
            var result = new List<ActiveEffect>();
            bool hasPendingConsumedToken = false;
            if (_pendingDamageEffects.TryGetValue(actorGuid, out var pending))
            {
                AddAmplifierEffectsWithConsumeCaps(result, pending);
                hasPendingConsumedToken = pending.Count > 0;
            }

            List<ActiveEffect> active = null;
            for (int i = 0; i < _activeEffects.Count; i++)
            {
                var effect = _activeEffects[i];
                if (effect.TargetGuid == actorGuid &&
                    effect.Kind == ContributionKind.DamageBonus &&
                    (!hasPendingConsumedToken || (effect.IsBuff && !IsConsumeBuffOfPendingToken(effect, pending))))
                {
                    if (active == null) active = new List<ActiveEffect>();
                    active.Add(effect);
                }
            }

            AddAmplifierEffectsWithConsumeCaps(result, active);
            return result;
        }

        private List<ActiveEffect> GetVulnerableEffectsForTarget(uint actorGuid)
        {
            var result = new List<ActiveEffect>();
            bool hasPendingConsumedToken = false;
            if (_pendingVulnerableEffects.TryGetValue(actorGuid, out var pending))
            {
                AddAmplifierEffectsWithConsumeCaps(result, pending);
                hasPendingConsumedToken = pending.Count > 0;
            }

            List<ActiveEffect> active = null;
            for (int i = 0; i < _activeEffects.Count; i++)
            {
                var effect = _activeEffects[i];
                if (effect.TargetGuid == actorGuid &&
                    effect.Kind == ContributionKind.Vulnerable &&
                    (!hasPendingConsumedToken || (effect.IsBuff && !IsConsumeBuffOfPendingToken(effect, pending))))
                {
                    if (active == null) active = new List<ActiveEffect>();
                    active.Add(effect);
                }
            }
            AddAmplifierEffectsWithConsumeCaps(result, active);
            return result;
        }

        private static bool IsConsumeBuffOfPendingToken(ActiveEffect buffEffect, List<ActiveEffect> pendingTokens)
        {
            if (buffEffect == null || !buffEffect.IsBuff || pendingTokens == null || pendingTokens.Count == 0)
                return false;

            string buffId = buffEffect.EffectId ?? "";
            if (string.IsNullOrEmpty(buffId)) return false;

            for (int i = 0; i < pendingTokens.Count; i++)
            {
                var pending = pendingTokens[i];
                if (pending == null || pending.IsBuff) continue;
                var token = GetTokenDefinition(pending.EffectId);
                if (token?.ConsumeBuffs == null) continue;

                try
                {
                    foreach (var buff in token.ConsumeBuffs)
                    {
                        if (buff == null) continue;
                        if (string.Equals(buff.Id ?? "", buffId, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { }
            }

            return false;
        }

        private static void AddAmplifierEffectsWithConsumeCaps(List<ActiveEffect> result, List<ActiveEffect> effects)
        {
            if (result == null || effects == null || effects.Count == 0) return;

            Dictionary<string, List<ActiveEffect>> capped = null;
            List<ActiveEffect> uncapped = null;
            for (int i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                if (!TryGetConsumeCapKey(effect, out var key))
                {
                    if (uncapped == null) uncapped = new List<ActiveEffect>();
                    uncapped.Add(effect);
                    continue;
                }

                if (capped == null)
                    capped = new Dictionary<string, List<ActiveEffect>>(StringComparer.OrdinalIgnoreCase);
                if (!capped.TryGetValue(key, out var list))
                {
                    list = new List<ActiveEffect>();
                    capped[key] = list;
                }
                list.Add(effect);
            }

            AddEffectsWithFloorCaps(result, FilterTokenConsumeBuffDuplicates(uncapped, capped));

            if (capped == null) return;

            foreach (var kvp in capped)
            {
                var list = kvp.Value;
                list.Sort(CompareConsumePriorityDescending);
                int limit = GetConsumeCapLimit(list);
                for (int i = 0; i < list.Count && i < limit; i++)
                    result.Add(list[i]);
            }
        }

        private static List<ActiveEffect> FilterTokenConsumeBuffDuplicates(
            List<ActiveEffect> effects,
            Dictionary<string, List<ActiveEffect>> cappedTokenEffects)
        {
            if (effects == null || effects.Count == 0 || cappedTokenEffects == null || cappedTokenEffects.Count == 0)
                return effects;

            List<ActiveEffect> filtered = null;
            for (int i = 0; i < effects.Count; i++)
            {
                ActiveEffect effect = effects[i];
                bool skip = effect != null && effect.IsBuff && IsConsumeBuffOfAnyToken(effect, cappedTokenEffects);
                if (!skip)
                {
                    if (filtered != null)
                        filtered.Add(effect);
                    continue;
                }

                if (filtered == null)
                {
                    filtered = new List<ActiveEffect>();
                    for (int j = 0; j < i; j++)
                        filtered.Add(effects[j]);
                }
            }

            return filtered ?? effects;
        }

        private static bool IsConsumeBuffOfAnyToken(ActiveEffect buffEffect, Dictionary<string, List<ActiveEffect>> cappedTokenEffects)
        {
            if (buffEffect == null || cappedTokenEffects == null) return false;
            foreach (var kvp in cappedTokenEffects)
            {
                var tokens = kvp.Value;
                if (tokens == null) continue;
                for (int i = 0; i < tokens.Count; i++)
                {
                    if (IsConsumeBuffOfToken(buffEffect, tokens[i]))
                        return true;
                }
            }

            return false;
        }

        private static bool IsConsumeBuffOfToken(ActiveEffect buffEffect, ActiveEffect tokenEffect)
        {
            if (buffEffect == null || tokenEffect == null || !buffEffect.IsBuff || tokenEffect.IsBuff) return false;
            if (buffEffect.TargetGuid != tokenEffect.TargetGuid) return false;
            if (buffEffect.Kind != tokenEffect.Kind) return false;
            if (buffEffect.ProviderGuid != 0 && tokenEffect.ProviderGuid != 0 && buffEffect.ProviderGuid != tokenEffect.ProviderGuid) return false;
            if (buffEffect.IsFloor && tokenEffect.IsFloor &&
                buffEffect.FloorPlacementId > 0 &&
                tokenEffect.FloorPlacementId > 0 &&
                buffEffect.FloorPlacementId != tokenEffect.FloorPlacementId) return false;

            string buffId = buffEffect.EffectId ?? "";
            if (string.IsNullOrEmpty(buffId)) return false;

            var token = GetTokenDefinition(tokenEffect.EffectId);
            if (token?.ConsumeBuffs == null) return false;
            try
            {
                foreach (var buff in token.ConsumeBuffs)
                {
                    if (buff == null) continue;
                    if (string.Equals(buff.Id ?? "", buffId, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static void AddEffectsWithFloorCaps(List<ActiveEffect> result, List<ActiveEffect> effects)
        {
            if (result == null || effects == null || effects.Count == 0) return;

            HashSet<string> seenFloorEffects = null;
            for (int i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                if (TryGetFloorCapKey(effect, out var key))
                {
                    if (seenFloorEffects == null)
                        seenFloorEffects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!seenFloorEffects.Add(key))
                        continue;
                }

                result.Add(effect);
            }
        }

        private static bool TryGetConsumeCapKey(ActiveEffect effect, out string key)
        {
            key = null;
            if (effect == null || effect.IsBuff) return false;
            if (IsCritTokenId(effect.EffectId)) return false;

            var token = GetTokenDefinition(effect.EffectId);
            if (token == null) return false;

            try
            {
                if (token.GetHasType(TokenType.SKILL_CALCULATE_DAMAGE_BUFF))
                {
                    key = effect.Kind == ContributionKind.Vulnerable
                        ? "target:skill_calculate_damage_buff"
                        : "performer:skill_calculate_damage_buff";
                    return true;
                }

                if (token.GetHasType(TokenType.SKILL_DAMAGE_BUFF))
                {
                    key = effect.Kind == ContributionKind.Vulnerable
                        ? "target:skill_damage_buff"
                        : "performer:skill_damage_buff";
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool TryGetFloorCapKey(ActiveEffect effect, out string key)
        {
            key = null;
            if (effect == null || !effect.IsFloor || effect.FloorPlacementId <= 0) return false;

            key = string.Concat(
                effect.FloorPlacementId.ToString(),
                ":",
                ((int)effect.Kind).ToString(),
                ":",
                effect.EffectId ?? "",
                ":",
                effect.SourceId ?? "");
            return true;
        }

        private static int GetConsumeCapLimit(List<ActiveEffect> effects)
        {
            if (effects == null || effects.Count == 0) return 0;

            int limit = 1;
            for (int i = 0; i < effects.Count; i++)
            {
                var token = GetTokenDefinition(effects[i]?.EffectId);
                if (token != null && token.m_ConsumeLimit > 0)
                    limit = Math.Min(limit, token.m_ConsumeLimit);
            }
            return Math.Max(1, limit);
        }

        private static int CompareConsumePriorityDescending(ActiveEffect a, ActiveEffect b)
        {
            int priorityA = GetTokenConsumePriority(a?.EffectId);
            int priorityB = GetTokenConsumePriority(b?.EffectId);
            return priorityB.CompareTo(priorityA);
        }

        private static int GetTokenConsumePriority(string tokenId)
        {
            try
            {
                return GetTokenDefinition(tokenId)?.m_ConsumePriority ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private List<ActiveEffect> GetActiveShieldEffectsForActor(uint actorGuid)
        {
            var candidates = new List<ActiveEffect>();
            for (int i = 0; i < _activeEffects.Count; i++)
            {
                var effect = _activeEffects[i];
                if (effect.TargetGuid == actorGuid && effect.Kind == ContributionKind.Shield)
                    candidates.Add(effect);
            }

            var result = new List<ActiveEffect>();
            AddEffectsWithFloorCaps(result, candidates);
            return result;
        }

        private List<ActiveEffect> GetPendingGuardEffects(uint guardedGuid, uint guarderGuid)
        {
            var result = new List<ActiveEffect>();
            if (!_pendingGuardEffects.TryGetValue(guardedGuid, out var pending)) return result;
            for (int i = 0; i < pending.Count; i++)
            {
                var effect = pending[i];
                if (effect.ProviderGuid == guarderGuid)
                    result.Add(effect);
            }
            return result;
        }

        private void MarkPendingShieldsUsed(uint actorGuid)
        {
            if (!_pendingShieldEffects.TryGetValue(actorGuid, out var effects)) return;
            for (int i = 0; i < effects.Count; i++)
                effects[i].Used = true;
        }

        private void AddActiveEffect(
            uint targetGuid,
            uint providerGuid,
            string effectId,
            string sourceId,
            ContributionKind kind,
            float damageBonusPct,
            bool isBuff,
            FloorEffectSourceTracker.SourceMarker floorMarker = null)
        {
            var effect = new ActiveEffect
            {
                TargetGuid = targetGuid,
                ProviderGuid = providerGuid,
                EffectId = effectId ?? "",
                SourceId = sourceId ?? "",
                Kind = kind,
                DamageBonusPct = damageBonusPct,
                IsBuff = isBuff
            };
            ApplyFloorMarker(effect, floorMarker);
            _activeEffects.Add(effect);
            GetOrCreate(providerGuid);
            _snapshotDirty = true;
        }

        private static void ApplyFloorMarker(ActiveEffect effect, FloorEffectSourceTracker.SourceMarker marker)
        {
            if (effect == null || marker == null || marker.ProviderGuid == 0) return;
            effect.IsFloor = marker.PlacementId > 0;
            effect.FloorPlacementId = marker.PlacementId;
            if (effect.ProviderGuid == 0)
                effect.ProviderGuid = marker.ProviderGuid;
            if (string.IsNullOrEmpty(effect.SourceId))
                effect.SourceId = !string.IsNullOrEmpty(marker.SourceId) ? marker.SourceId : marker.SkillId;
        }

        private ActiveEffect PopActiveEffect(uint targetGuid, string effectId, ContributionKind kind, bool allowFallback = true)
        {
            for (int i = 0; i < _activeEffects.Count; i++)
            {
                var effect = _activeEffects[i];
                if (effect.TargetGuid != targetGuid || effect.Kind != kind) continue;
                if (!string.Equals(effect.EffectId, effectId ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                _activeEffects.RemoveAt(i);
                return effect;
            }

            if (!allowFallback)
                return null;

            for (int i = 0; i < _activeEffects.Count; i++)
            {
                var effect = _activeEffects[i];
                if (effect.TargetGuid == targetGuid && effect.Kind == kind)
                {
                    _activeEffects.RemoveAt(i);
                    return effect;
                }
            }
            return null;
        }

        private ActiveEffect PopActiveEffect(uint targetGuid, string effectId, ContributionKind kind, uint providerGuid, string sourceId)
        {
            for (int i = 0; i < _activeEffects.Count; i++)
            {
                var effect = _activeEffects[i];
                if (effect.TargetGuid != targetGuid || effect.Kind != kind) continue;
                if (providerGuid != 0 && effect.ProviderGuid != providerGuid) continue;
                if (!string.Equals(effect.EffectId ?? "", effectId ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                if (!SourceIdMatches(effect.SourceId, sourceId)) continue;
                _activeEffects.RemoveAt(i);
                return effect;
            }

            for (int i = 0; i < _activeEffects.Count; i++)
            {
                var effect = _activeEffects[i];
                if (effect.TargetGuid != targetGuid || effect.Kind != kind) continue;
                if (providerGuid != 0 && effect.ProviderGuid != providerGuid) continue;
                if (!string.Equals(effect.EffectId ?? "", effectId ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                _activeEffects.RemoveAt(i);
                return effect;
            }

            return null;
        }

        private static void AddPending(Dictionary<uint, List<ActiveEffect>> map, uint actorGuid, ActiveEffect effect)
        {
            if (!map.TryGetValue(actorGuid, out var list))
            {
                list = new List<ActiveEffect>();
                map[actorGuid] = list;
            }
            list.Add(effect);
        }

        private void AddConsumedEffectFromSkillResult(Dictionary<uint, List<ActiveEffect>> map, uint actorGuid, ActiveEffect effect)
        {
            if (effect == null) return;
            if (map.TryGetValue(actorGuid, out var list) && HasEquivalentEffect(list, effect)) return;

            ActiveEffect active = PopActiveEffect(
                effect.TargetGuid,
                effect.EffectId,
                effect.Kind,
                effect.ProviderGuid,
                effect.SourceId);

            if (active != null)
            {
                if (active.DamageBonusPct <= 0.0001f)
                    active.DamageBonusPct = effect.DamageBonusPct;
                if (active.ProviderGuid == 0)
                    active.ProviderGuid = effect.ProviderGuid;
                if (string.IsNullOrEmpty(active.SourceId))
                    active.SourceId = effect.SourceId;
                if (!active.IsFloor && effect.IsFloor)
                {
                    active.IsFloor = true;
                    active.FloorPlacementId = effect.FloorPlacementId;
                }
                AddPending(map, actorGuid, active);
                return;
            }

            AddPending(map, actorGuid, effect);
        }

        private void AddPendingUnique(Dictionary<uint, List<ActiveEffect>> map, uint actorGuid, ActiveEffect effect)
        {
            if (effect == null) return;
            if (HasEquivalentEffect(_activeEffects, effect)) return;
            if (map.TryGetValue(actorGuid, out var list) && HasEquivalentEffect(list, effect)) return;
            AddPending(map, actorGuid, effect);
        }

        private static bool HasEquivalentEffect(List<ActiveEffect> effects, ActiveEffect candidate)
        {
            if (effects == null || candidate == null) return false;
            for (int i = 0; i < effects.Count; i++)
            {
                if (IsEquivalentEffect(effects[i], candidate)) return true;
            }
            return false;
        }

        private static bool IsEquivalentEffect(ActiveEffect existing, ActiveEffect candidate)
        {
            if (existing == null || candidate == null) return false;
            if (existing.TargetGuid != candidate.TargetGuid) return false;
            if (existing.ProviderGuid != candidate.ProviderGuid) return false;
            if (existing.Kind != candidate.Kind) return false;
            if (existing.IsFloor || candidate.IsFloor)
            {
                if (existing.IsFloor != candidate.IsFloor) return false;
                if (existing.FloorPlacementId != candidate.FloorPlacementId) return false;
            }

            string existingEffectId = existing.EffectId ?? "";
            string candidateEffectId = candidate.EffectId ?? "";
            if (!string.IsNullOrEmpty(existingEffectId) || !string.IsNullOrEmpty(candidateEffectId))
                return string.Equals(existingEffectId, candidateEffectId, StringComparison.OrdinalIgnoreCase);

            return string.Equals(existing.SourceId ?? "", candidate.SourceId ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private ContributionStats GetOrCreate(uint guid)
        {
            if (_stats.TryGetValue(guid, out var existing))
            {
                if (string.IsNullOrEmpty(existing.ActorName) || existing.ActorName.StartsWith("Actor_", StringComparison.OrdinalIgnoreCase))
                {
                    string resolved = DamageTracker.TryResolveNamePublic(guid);
                    if (!string.IsNullOrEmpty(resolved)) existing.ActorName = resolved;
                }
                return existing;
            }

            string name = DamageTracker.TryResolveNamePublic(guid) ?? $"Actor_{guid}";
            var stats = new ContributionStats
            {
                ActorGuid = guid,
                ActorName = name,
                TeamIndex = 0
            };
            _stats[guid] = stats;
            return stats;
        }

        private static ContributionStats Clone(ContributionStats s)
        {
            return new ContributionStats
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
            };
        }

        private static int CompareContributionRows(ContributionStats a, ContributionStats b)
        {
            int result = b.TotalContribution.CompareTo(a.TotalContribution);
            if (result != 0) return result;
            result = b.VulnerableDamage.CompareTo(a.VulnerableDamage);
            if (result != 0) return result;
            result = b.ComboConsumed.CompareTo(a.ComboConsumed);
            if (result != 0) return result;
            result = b.ComboApplied.CompareTo(a.ComboApplied);
            if (result != 0) return result;
            return string.Compare(a.ActorName, b.ActorName, StringComparison.OrdinalIgnoreCase);
        }

        private void CacheComboConsumptionHints(EventSkillFinalizeResults evt)
        {
            try
            {
                if (evt?.ActorResults == null) return;
                string skillId = evt.SkillId ?? "";
                foreach (var ar in evt.ActorResults)
                {
                    if (ar == null || !ar.IsCombo) continue;

                    uint consumerGuid = ar.m_PerformerActorGuid;
                    uint targetGuid = ar.m_TargetActorGuid;
                    if (consumerGuid == 0 || targetGuid == 0) continue;
                    if (!IsPlayerTeam(consumerGuid) || IsPlayerTeam(targetGuid)) continue;
                    if (!_activeCombos.ContainsKey(targetGuid)) continue;

                    AddPendingComboConsume(targetGuid, consumerGuid, skillId);
                }
            }
            catch { }
        }

        private void AddPendingComboConsume(uint targetGuid, uint consumerGuid, string skillId)
        {
            for (int i = 0; i < _pendingComboConsumes.Count; i++)
            {
                var pending = _pendingComboConsumes[i];
                if (pending.TargetGuid == targetGuid && pending.ConsumerGuid == consumerGuid)
                {
                    pending.SkillId = skillId ?? "";
                    pending.Round = _currentRound;
                    return;
                }
            }

            _pendingComboConsumes.Add(new PendingComboConsume
            {
                TargetGuid = targetGuid,
                ConsumerGuid = consumerGuid,
                SkillId = skillId ?? "",
                Round = _currentRound
            });
        }

        private PendingComboConsume ConsumePendingComboConsume(uint targetGuid, uint consumerGuid, string sourceId)
        {
            PendingComboConsume wildcard = null;
            for (int i = _pendingComboConsumes.Count - 1; i >= 0; i--)
            {
                var pending = _pendingComboConsumes[i];
                if (_currentRound - pending.Round > 1)
                {
                    _pendingComboConsumes.RemoveAt(i);
                    continue;
                }

                if (pending.TargetGuid != targetGuid) continue;
                if (consumerGuid != 0 && pending.ConsumerGuid != 0 && pending.ConsumerGuid != consumerGuid) continue;
                if (!SourceIdMatches(pending.SkillId, sourceId))
                {
                    if (wildcard == null)
                        wildcard = pending;
                    continue;
                }

                _pendingComboConsumes.RemoveAt(i);
                return pending;
            }

            if (wildcard != null)
                _pendingComboConsumes.Remove(wildcard);
            return wildcard;
        }

        private void TrackVulnerableAdded(EventTokenAdded evt, TokenDefinition token)
        {
            if (!IsVulnerableToken(token)) return;

            uint targetGuid = evt.m_ActorGuid;
            if (targetGuid == 0 || IsPlayerTeam(targetGuid)) return;

            uint sourceGuid = 0;
            string sourceId = evt.m_SourceId ?? "";
            var hint = ConsumeStatusHint(targetGuid, evt.m_TokenId, "ADD", evt.m_SourceId);
            if (hint != null && IsSkillSource(hint.SourceType, hint.SourceId))
            {
                sourceGuid = hint.SourceGuid;
                sourceId = hint.SourceId ?? sourceId;
            }

            if (sourceGuid == 0 &&
                IsSkillSource(evt.m_SourceType, evt.m_SourceId) &&
                TryResolveTokenSource(targetGuid, evt.m_TokenId, evt.m_SourceType, evt.m_SourceId, out var resolvedGuid, out var resolvedSourceId))
            {
                sourceGuid = resolvedGuid;
                sourceId = resolvedSourceId ?? sourceId;
            }

            if (sourceGuid == 0 &&
                _floorEffectSources.TryResolveTokenSource(targetGuid, evt.m_TokenId, evt.m_SourceType, evt.m_SourceId, out var floorMarker))
            {
                sourceGuid = floorMarker.ProviderGuid;
                if (!string.IsNullOrEmpty(floorMarker.SourceId)) sourceId = floorMarker.SourceId;
            }

            if (sourceGuid == 0 || !IsPlayerTeam(sourceGuid)) return;

            int amount = Math.Max(1, evt.m_AddAmount);
            for (int i = 0; i < amount; i++)
                AddActiveEffect(targetGuid, sourceGuid, evt.m_TokenId, sourceId, ContributionKind.Vulnerable, 0f, false);
        }

        private void TrackVulnerableRemoved(EventTokenRemoved evt)
        {
            uint targetGuid = evt.Actor != null ? evt.Actor.ActorGuid : 0;
            if (targetGuid == 0) return;

            var removed = PopActiveEffect(targetGuid, evt.Token.Id, ContributionKind.Vulnerable);
            if (IsPlayerTeam(targetGuid)) return;

            // Re-applying Vulnerable can remove one instance after the token limit is
            // enforced. If a Vulnerable token still remains, keep attribution aligned
            // with the surviving token instance instead of dropping the contribution.
            bool resolved = _floorEffectSources.TryResolveTokenSource(targetGuid, evt.Token.Id, evt.Source, evt.SourceId, out var floorMarker);
            uint sourceGuid = resolved ? floorMarker.ProviderGuid : 0;
            string sourceId = resolved ? floorMarker.SourceId : evt.SourceId;
            if (!resolved)
                resolved = TryResolveTokenSource(targetGuid, evt.Token.Id, evt.Source, evt.SourceId, out sourceGuid, out sourceId);

            if (resolved && sourceGuid != 0 && IsPlayerTeam(sourceGuid))
            {
                AddActiveEffect(targetGuid, sourceGuid, evt.Token.Id, sourceId, ContributionKind.Vulnerable, 0f, false);
            }
            else if (removed != null)
            {
                _snapshotDirty = true;
            }
        }

        private void TrackComboAdded(EventTokenAdded evt, TokenDefinition token)
        {
            if (!IsComboToken(token)) return;

            uint targetGuid = evt.m_ActorGuid;
            if (targetGuid == 0 || IsPlayerTeam(targetGuid)) return;

            uint sourceGuid = 0;
            string sourceId = evt.m_SourceId ?? "";
            var hint = ConsumeStatusHint(targetGuid, evt.m_TokenId, "ADD", evt.m_SourceId);
            if (hint != null && IsSkillSource(hint.SourceType, hint.SourceId))
            {
                sourceGuid = hint.SourceGuid;
                sourceId = hint.SourceId ?? sourceId;
            }

            if (sourceGuid == 0 &&
                IsSkillSource(evt.m_SourceType, evt.m_SourceId) &&
                TryResolveTokenSource(targetGuid, evt.m_TokenId, evt.m_SourceType, evt.m_SourceId, out var resolvedGuid, out var resolvedSourceId))
            {
                sourceGuid = resolvedGuid;
                sourceId = resolvedSourceId ?? sourceId;
            }

            if (sourceGuid == 0 &&
                _floorEffectSources.TryResolveTokenSource(targetGuid, evt.m_TokenId, evt.m_SourceType, evt.m_SourceId, out var floorMarker))
            {
                sourceGuid = floorMarker.ProviderGuid;
                if (!string.IsNullOrEmpty(floorMarker.SourceId)) sourceId = floorMarker.SourceId;
            }

            if (sourceGuid == 0 || !IsPlayerTeam(sourceGuid)) return;

            int amount = Math.Max(1, evt.m_AddAmount);
            var stats = GetOrCreate(sourceGuid);
            stats.ComboApplied += amount;
            _activeCombos[targetGuid] = new ActiveCombo
            {
                TargetGuid = targetGuid,
                ProviderGuid = sourceGuid,
                SourceId = sourceId ?? "",
                Round = _currentRound
            };
            _snapshotDirty = true;
        }

        private void TrackComboRemoved(EventTokenRemoved evt)
        {
            uint targetGuid = evt.Actor != null ? evt.Actor.ActorGuid : 0;
            if (targetGuid == 0) return;

            if (IsPlayerTeam(targetGuid))
            {
                _activeCombos.Remove(targetGuid);
                return;
            }

            bool consumerIsPlayer = evt.SourceActorGuid != 0 && IsPlayerTeam(evt.SourceActorGuid);
            var pending = consumerIsPlayer
                ? ConsumePendingComboConsume(targetGuid, evt.SourceActorGuid, evt.SourceId)
                : null;

            if (pending != null && _activeCombos.TryGetValue(targetGuid, out var combo) && combo.ProviderGuid != 0 && IsPlayerTeam(combo.ProviderGuid))
            {
                var stats = GetOrCreate(combo.ProviderGuid);
                stats.ComboConsumed++;
                _activeCombos.Remove(targetGuid);
                _snapshotDirty = true;
                return;
            }

            // Re-applying combo over an existing combo removes the old token before the
            // replacement is kept, but it is not an effective combo for our stats.
            // Keep the original owner in that case so a later real combo consume still
            // credits the first effective application.
            if (!IsSkillSource(evt.Source, evt.SourceId) || !consumerIsPlayer)
            {
                _activeCombos.Remove(targetGuid);
            }
        }

        private void CacheStatusHints(Assets.Code.Skill.SkillCalculation.ActorResult ar, string fallbackSkillId)
        {
            try
            {
                if (ar?.m_AppliedEffectsOutputContainer == null) return;
                foreach (var output in ar.m_AppliedEffectsOutputContainer.Outputs)
                {
                    if (output == null || output.m_TargetActor == null) continue;
                    uint targetGuid = output.m_TargetActor.ActorGuid;
                    uint sourceGuid = output.m_PerformerActor != null ? output.m_PerformerActor.ActorGuid : ar.m_PerformerActorGuid;

                    foreach (var effect in output.EffectInstancesToApply)
                    {
                        if (effect?.EffectDefinition == null) continue;
                        string sourceId = !string.IsNullOrEmpty(effect.SourceId) ? effect.SourceId : fallbackSkillId;
                        var def = effect.EffectDefinition;

                        if (effect.TokenAddAmount > 0)
                        {
                            AddStatusHint(targetGuid, def.m_TokenAddId, "ADD", sourceGuid, effect.SourceType, sourceId);
                            if (!string.IsNullOrEmpty(def.m_TokenAddTag))
                                AddStatusHint(targetGuid, null, "ADD", sourceGuid, effect.SourceType, sourceId);
                        }

                        if (effect.TokenConvertAmount > 0 && !string.IsNullOrEmpty(def.m_TokenConvertToId))
                            AddStatusHint(targetGuid, def.m_TokenConvertToId, "ADD", sourceGuid, effect.SourceType, sourceId);

                        if (effect.TokenCopyAmount > 0 && def.m_TokenCopyTags != null && def.m_TokenCopyTags.Count > 0)
                            AddStatusHint(targetGuid, null, "ADD", sourceGuid, effect.SourceType, sourceId);

                        if (effect.TokenInvertAmount > 0 && def.m_TokenInvertIds != null && def.m_TokenInvertIds.Count > 0)
                            AddStatusHint(targetGuid, null, "ADD", sourceGuid, effect.SourceType, sourceId);
                    }
                }
            }
            catch { }
        }

        private void AddStatusHint(uint targetGuid, string effectId, string operation, uint sourceGuid, SourceType sourceType, string sourceId)
        {
            if (targetGuid == 0 || sourceGuid == 0) return;
            _statusHints.Add(new StatusSourceHint
            {
                TargetGuid = targetGuid,
                EffectId = effectId ?? "",
                Operation = operation ?? "",
                SourceGuid = sourceGuid,
                SourceType = sourceType,
                SourceId = sourceId ?? "",
                Round = _currentRound
            });
            while (_statusHints.Count > MaxStatusHints)
                _statusHints.RemoveAt(0);
        }

        private StatusSourceHint ConsumeStatusHint(uint targetGuid, string effectId, string operation, string sourceId)
        {
            StatusSourceHint wildcard = null;
            for (int i = _statusHints.Count - 1; i >= 0; i--)
            {
                var hint = _statusHints[i];
                if (_currentRound - hint.Round > 1)
                {
                    _statusHints.RemoveAt(i);
                    continue;
                }

                if (hint.TargetGuid != targetGuid || !string.Equals(hint.Operation, operation, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!SourceIdMatches(hint.SourceId, sourceId))
                    continue;

                bool exact = !string.IsNullOrEmpty(hint.EffectId) &&
                             string.Equals(hint.EffectId, effectId ?? "", StringComparison.OrdinalIgnoreCase);
                if (exact)
                {
                    _statusHints.RemoveAt(i);
                    return hint;
                }

                if (wildcard == null && string.IsNullOrEmpty(hint.EffectId))
                    wildcard = hint;
            }

            if (wildcard != null)
                _statusHints.Remove(wildcard);
            return wildcard;
        }

        private static bool SourceIdMatches(string hintSourceId, string eventSourceId)
        {
            return string.IsNullOrEmpty(hintSourceId) ||
                   string.IsNullOrEmpty(eventSourceId) ||
                   string.Equals(hintSourceId, eventSourceId, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetActorResultCritScore(Assets.Code.Skill.SkillCalculation.ActorResult actorResult)
        {
            try
            {
                if (actorResult == null || !actorResult.IsCrit) return 0;
                EnsureActorResultCritReflection();
                var crit = _actorResultCritField?.GetValue(actorResult) as Assets.Code.Skill.SkillCalculation.ActorResult.Crit;
                return Mathf.Max(crit?.m_CritScore ?? 1, 1);
            }
            catch
            {
                return actorResult != null && actorResult.IsCrit ? 1 : 0;
            }
        }

        private static void EnsureActorResultCritReflection()
        {
            if (_actorResultCritReflectionInit) return;
            _actorResultCritReflectionInit = true;
            _actorResultCritField = typeof(Assets.Code.Skill.SkillCalculation.ActorResult)
                .GetField("m_Crit", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private static float GetProjectedHpBefore(uint targetGuid, float fallbackDamage, Dictionary<uint, float> projectedHp)
        {
            if (projectedHp.TryGetValue(targetGuid, out var hp))
                return Mathf.Max(0f, hp);
            if (DamageTracker.TryResolveHpRawPublic(targetGuid, out var resolvedHp))
                hp = Mathf.Max(0f, resolvedHp);
            else
                hp = Mathf.Max(0f, fallbackDamage);
            projectedHp[targetGuid] = hp;
            return hp;
        }

        private static bool IsEligibleFriendlyExternalSource(uint sourceGuid, uint targetGuid)
        {
            return sourceGuid != 0 &&
                   targetGuid != 0 &&
                   sourceGuid != targetGuid &&
                   IsPlayerTeam(sourceGuid) &&
                   IsPlayerTeam(targetGuid);
        }

        private static bool IsSkillSource(SourceType sourceType, string sourceId = null)
        {
            if (sourceType == null) return false;
            if (IsExcludedSkillSourceId(sourceId)) return false;
            return IsSourceType(sourceType, "skill") ||
                   IsSourceType(sourceType, "skill_buff") ||
                   IsSourceType(sourceType, "skill_actor");
        }

        private static bool IsContributionSource(SourceType sourceType, string sourceId = null)
        {
            if (sourceType == null) return false;
            if (IsExcludedSkillSourceId(sourceId)) return false;
            return IsSkillSource(sourceType, sourceId) ||
                   IsSourceType(sourceType, "locked_team_position_transfer");
        }

        private static bool IsExcludedSkillSourceId(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId)) return false;
            string lower = sourceId.ToLowerInvariant();
            return lower.Contains("item") ||
                   lower.Contains("trinket") ||
                   lower.Contains("inventory") ||
                   lower.Contains("stagecoach");
        }

        private static bool IsSourceType(SourceType sourceType, string expected)
        {
            if (sourceType == null) return false;
            try
            {
                return string.Equals(sourceType.GetName(), expected, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(sourceType.ToString(), expected, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static TokenDefinition GetTokenDefinition(string tokenId)
        {
            if (string.IsNullOrEmpty(tokenId)) return null;
            try
            {
                return SingletonMonoBehaviour<Library<string, TokenDefinition>>.Instance?.GetLibraryElement(tokenId);
            }
            catch { return null; }
        }

        private static bool IsComboToken(TokenDefinition token)
        {
            return token != null &&
                   string.Equals(token.Id ?? "", "combo", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVulnerableToken(TokenDefinition token)
        {
            return token != null &&
                   string.Equals(token.Id ?? "", "vulnerable", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolveTokenSource(uint actorGuid, string tokenId, SourceType sourceType, string eventSourceId, out uint sourceActorGuid, out string sourceId)
        {
            sourceActorGuid = 0;
            sourceId = eventSourceId ?? "";
            try
            {
                var actor = TryResolveActor(actorGuid);
                if (actor?.TokenContainer == null) return false;

                var tokenInstance = FindNewestTokenInstance(actor, tokenId, sourceType, eventSourceId);
                if (tokenInstance == null) return false;

                sourceActorGuid = tokenInstance.SourceActorGuid;
                sourceId = tokenInstance.SourceId ?? sourceId;
                return sourceActorGuid != 0;
            }
            catch { return false; }
        }

        private static TokenInstance FindNewestTokenInstance(ActorInstance actor, string tokenId, SourceType sourceType, string sourceId)
        {
            try
            {
                var instances = actor.TokenContainer.GetInstances(token =>
                    token != null &&
                    token.Definition != null &&
                    string.Equals(token.Definition.Id ?? "", tokenId ?? "", StringComparison.OrdinalIgnoreCase) &&
                    SourceTypeMatches(token.SourceType, sourceType) &&
                    SourceIdMatches(token.SourceId, sourceId));

                if (instances != null && instances.Count > 0)
                    return instances[instances.Count - 1];

                instances = actor.TokenContainer.GetInstances(token =>
                    token != null &&
                    token.Definition != null &&
                    string.Equals(token.Definition.Id ?? "", tokenId ?? "", StringComparison.OrdinalIgnoreCase) &&
                    token.SourceActorGuid != 0);

                if (instances != null && instances.Count > 0)
                    return instances[instances.Count - 1];
            }
            catch { }
            return null;
        }

        private static DotDefinition GetDotDefinition(string dotId)
        {
            if (string.IsNullOrEmpty(dotId)) return null;
            try
            {
                return SingletonMonoBehaviour<Library<string, DotDefinition>>.Instance?.GetLibraryElement(dotId);
            }
            catch { return null; }
        }

        private static bool IsDamageBonusToken(TokenDefinition token)
        {
            try
            {
                return token != null &&
                       (token.GetHasType(TokenType.SKILL_DAMAGE_BUFF) ||
                        token.GetHasType(TokenType.SKILL_CALCULATE_DAMAGE_BUFF));
            }
            catch { return false; }
        }

        private static bool IsCritToken(TokenDefinition token)
        {
            try
            {
                if (token == null) return false;
                return token.GetHasType(TokenType.CRIT) || IsCritTokenId(token.Id);
            }
            catch { return false; }
        }

        private static bool IsCritTokenId(string tokenId)
        {
            return string.Equals(tokenId ?? "", "crit", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tokenId ?? "", "crit_plus", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsShieldToken(TokenDefinition token)
        {
            try
            {
                return token != null &&
                       token.GetHasType(TokenType.ON_DAMAGING_BLOCKED);
            }
            catch { return false; }
        }

        private static bool IsGuardToken(TokenDefinition token)
        {
            try
            {
                return token != null &&
                       (token.GetHasType(TokenType.GUARD) ||
                        token.GetHasType(TokenType.GUARDING));
            }
            catch { return false; }
        }

        private static float GetDamageBonusPct(TokenDefinition token)
        {
            if (token == null) return 0f;
            float pct = 0f;
            try
            {
                if (token.ConsumeBuffs != null)
                {
                    foreach (var buff in token.ConsumeBuffs)
                        pct += GetDamageBonusPct(buff);
                }
            }
            catch { }

            if (pct > 0.0001f) return pct;

            string id = token.Id ?? "";
            if (string.Equals(id, "strength", StringComparison.OrdinalIgnoreCase)) return 0.5f;
            if (string.Equals(id, "strength_plus", StringComparison.OrdinalIgnoreCase)) return 0.75f;
            return 0f;
        }

        private static float GetTokenContributionBonusPct(TokenDefinition token)
        {
            float pct = GetDamageBonusPct(token);
            if (pct > 0.0001f) return pct;
            return IsCritToken(token) ? GetCritDamageBonusPct() : 0f;
        }

        private static float GetVulnerableDamageBonusPct(string tokenId)
        {
            var token = GetTokenDefinition(tokenId);
            if (token?.ConsumeBuffs != null)
            {
                float pct = 0f;
                try
                {
                    foreach (var buff in token.ConsumeBuffs)
                        pct += GetDamageTakenBonusPct(buff);
                }
                catch { }
                if (pct > 0.0001f) return pct;
            }
            return 0.5f;
        }

        private static float GetDamageTakenBonusPct(BuffDefinition buff)
        {
            if (buff?.ActorDataStats?.StatContainer == null) return 0f;
            try
            {
                var stats = buff.ActorDataStats.StatContainer;
                if (!stats.GetHasStat(ActorStatType.HEALTH_DAMAGE_RECEIVED_PERCENT)) return 0f;
                return Mathf.Max(0f, GetStatAddValueIncludingSubstats(stats, ActorStatType.HEALTH_DAMAGE_RECEIVED_PERCENT));
            }
            catch { return 0f; }
        }

        private static float GetCritDamageBonusPct(int critScore = 1)
        {
            try
            {
                var multipliers = RulesManager.GetRules<CombatRules>()?.m_CritDamageMultiplications;
                if (multipliers != null && multipliers.Count > 0)
                {
                    int index = Mathf.Clamp(critScore, 0, multipliers.Count - 1);
                    return Mathf.Max(0f, multipliers[index] - 1f);
                }
            }
            catch { }
            return 0.5f;
        }

        private static float GetDamageBonusPct(BuffDefinition buff)
        {
            if (buff?.ActorDataStats?.StatContainer == null) return 0f;
            float pct = 0f;
            try
            {
                var stats = buff.ActorDataStats.StatContainer;
                if (stats.GetHasStat(ActorStatType.HEALTH_DAMAGE_DEALT_PERCENT))
                    pct += Mathf.Max(0f, GetStatAddValueIncludingSubstats(stats, ActorStatType.HEALTH_DAMAGE_DEALT_PERCENT));
                if (stats.GetHasStat(ActorStatType.HEALTH_DAMAGE_DEALT_MULT_PERCENT))
                {
                    float multTotal = GetStatTotalIncludingSubstats(stats, ActorStatType.HEALTH_DAMAGE_DEALT_MULT_PERCENT);
                    if (multTotal > 1f) pct += multTotal - 1f;
                }
            }
            catch { }
            return pct;
        }

        private static float GetDamageReductionPct(BuffDefinition buff)
        {
            if (buff?.ActorDataStats?.StatContainer == null) return 0f;
            try
            {
                var stats = buff.ActorDataStats.StatContainer;
                if (!stats.GetHasStat(ActorStatType.HEALTH_DAMAGE_RECEIVED_PERCENT)) return 0f;
                float total = GetStatTotalIncludingSubstats(stats, ActorStatType.HEALTH_DAMAGE_RECEIVED_PERCENT);
                return Mathf.Max(0f, 1f - total);
            }
            catch { return 0f; }
        }

        private static float GetStatAddValueIncludingSubstats(Assets.Code.Stat.IReadOnlyStatContainer<ActorStatType> stats, ActorStatType statType)
        {
            if (stats == null || statType == null) return 0f;
            string[] subStats = stats.GetStatSubStatKeys(statType);
            if (subStats != null && subStats.Length > 0)
                return stats.GetStatAddValue(statType, subStats);
            return stats.GetStatAddValue(statType, (string)null);
        }

        private static float GetStatTotalIncludingSubstats(Assets.Code.Stat.IReadOnlyStatContainer<ActorStatType> stats, ActorStatType statType)
        {
            if (stats == null || statType == null) return statType != null ? statType.m_BaseValue : 0f;
            string[] subStats = stats.GetStatSubStatKeys(statType);
            if (subStats != null && subStats.Length > 0)
                return stats.GetStatTotal(statType, subStats, true);
            return stats.GetStatTotal(statType, (string)null, true);
        }

        private static bool IsPlayerTeam(uint guid)
        {
            try
            {
                var actor = TryResolveActor(guid);
                return actor != null && actor.TeamIndex == 0;
            }
            catch { }
            return false;
        }

        private static ActorInstance TryResolveActor(uint guid)
        {
            try
            {
                if (!_libraryReflectionInit)
                {
                    _libraryReflectionInit = true;
                    InitLibraryTeamReflection();
                }
                if (_teamLibraryInstance == null || _getTeamLibraryElement == null) return null;
                return _getTeamLibraryElement.Invoke(_teamLibraryInstance, new object[] { guid }) as ActorInstance;
            }
            catch { return null; }
        }

        private static object _teamLibraryInstance;
        private static MethodInfo _getTeamLibraryElement;
        private static bool _libraryReflectionInit;
        private static FieldInfo _skillResultField;
        private static bool _skillResultReflectionInit;
        private static FieldInfo _dotChangeAmountsField;
        private static FieldInfo _dotPerformerGuidsField;
        private static FieldInfo _dotSourceIdsField;
        private static bool _dotResultReflectionInit;
        private static FieldInfo _actorResultCritField;
        private static bool _actorResultCritReflectionInit;

        private static void InitLibraryTeamReflection()
        {
            try
            {
                Type genericLibDef = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        genericLibDef = asm.GetType("Assets.Code.Library.Library`2");
                        if (genericLibDef != null) break;
                    }
                    catch { }
                }
                if (genericLibDef == null) return;
                var libraryType = genericLibDef.MakeGenericType(typeof(uint), typeof(ActorInstance));
                var di = libraryType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (di != null) _teamLibraryInstance = di.GetValue(null);
                if (_teamLibraryInstance == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "PlayFab") continue;
                        try
                        {
                            foreach (var t in asm.GetTypes())
                            {
                                if (!t.IsGenericTypeDefinition || t.GetGenericArguments().Length != 1) continue;
                                if (t.Name != "Singleton`1" && t.Name != "SingletonMonoBehaviour`1") continue;
                                try
                                {
                                    var cs = t.MakeGenericType(libraryType);
                                    var ip = cs.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                                    if (ip != null) _teamLibraryInstance = ip.GetValue(null);
                                    if (_teamLibraryInstance != null) break;
                                }
                                catch { }
                            }
                            if (_teamLibraryInstance != null) break;
                        }
                        catch { }
                    }
                }
                if (_teamLibraryInstance != null)
                    _getTeamLibraryElement = libraryType.GetMethod("GetLibraryElement", new Type[] { typeof(uint) });
            }
            catch { }
        }
    }
}
