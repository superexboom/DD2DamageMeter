using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Actor.ActorContainer;
using Assets.Code.Buff;
using Assets.Code.Buff.Events;
using Assets.Code.Combat.Events;
using Assets.Code.Dot;
using Assets.Code.Dot.Events;
using Assets.Code.Effect;
using Assets.Code.Library;
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
            public int ShieldWasted;
            public int ComboApplied;
            public int ComboConsumed;
            public float TotalContribution => BonusDamage + VulnerableDamage + ShieldPrevented + GuardProtected;
        }

        private enum ContributionKind
        {
            DamageBonus,
            Vulnerable,
            Shield,
            Guard
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

        private class FloorSource
        {
            public uint TargetGuid;
            public uint ProviderGuid;
            public int TeamIndex = -1;
            public int TeamPosition = -1;
            public string BuffId;
            public string SkillId;
            public string SourceId;
            public bool DirectDamageBonus;
            public bool DirectShield;
            public readonly List<string> FloorEffectIds = new List<string>();
            public readonly List<string> DamageTokenIds = new List<string>();
            public readonly List<string> DamageTokenTags = new List<string>();
            public readonly List<string> DamageBuffIds = new List<string>();
            public readonly List<string> ShieldTokenIds = new List<string>();
            public readonly List<string> ShieldTokenTags = new List<string>();
            public readonly List<string> ShieldBuffIds = new List<string>();
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
        private readonly List<StatusSourceHint> _statusHints = new List<StatusSourceHint>();
        private readonly List<FloorSource> _floorSources = new List<FloorSource>();
        private readonly Dictionary<uint, ActiveCombo> _activeCombos = new Dictionary<uint, ActiveCombo>();
        private readonly List<PendingComboConsume> _pendingComboConsumes = new List<PendingComboConsume>();

        private ContributionStats[] _playerSnapshot = Array.Empty<ContributionStats>();
        private bool _snapshotDirty = true;
        private int _currentRound;

        public IReadOnlyList<ContributionStats> PlayerStats => _playerSnapshot;

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
                    RecordFloorContributionSources(evt);

                    var projectedHp = new Dictionary<uint, float>();
                    foreach (var ar in evt.ActorResults)
                    {
                        if (ar == null) continue;

                        float hpBefore = 0f;
                        bool hasHpBefore = ar.IsDamaging || ar.IsBlocked;
                        if (hasHpBefore)
                            hpBefore = GetProjectedHpBefore(ar.m_TargetActorGuid, Mathf.Max(ar.HealthDamage, ar.BaseHealthDamage), projectedHp);

                        if (ar.IsDamaging)
                        {
                            TrackDamageBonusContribution(ar, hpBefore);
                            TrackVulnerableContribution(ar, hpBefore);
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

                    float bonusPct = GetDamageBonusPct(token);
                    bool isDamageBonus = bonusPct > 0.0001f || IsDamageBonusToken(token);
                    bool isShield = IsShieldToken(token);
                    bool isGuard = IsGuardToken(token);
                    if (!isDamageBonus && !isShield && !isGuard) return;

                    uint sourceGuid = 0;
                    string sourceId = evt.m_SourceId ?? "";
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
                        TryResolveFloorTokenSource(evt.m_ActorGuid, evt.m_TokenId, evt.m_SourceType, evt.m_SourceId, isDamageBonus, isShield, out var floorGuid, out var floorSourceId))
                    {
                        sourceGuid = floorGuid;
                        if (!string.IsNullOrEmpty(floorSourceId)) sourceId = floorSourceId;
                    }

                    if (!IsEligibleFriendlyExternalSource(sourceGuid, evt.m_ActorGuid)) return;

                    int amount = Math.Max(1, evt.m_AddAmount);
                    for (int i = 0; i < amount; i++)
                    {
                        if (isDamageBonus && bonusPct > 0.0001f)
                            AddActiveEffect(evt.m_ActorGuid, sourceGuid, evt.m_TokenId, sourceId, ContributionKind.DamageBonus, bonusPct, false);
                        if (isShield)
                            AddActiveEffect(evt.m_ActorGuid, sourceGuid, evt.m_TokenId, sourceId, ContributionKind.Shield, 0f, false);
                        if (isGuard)
                            AddActiveEffect(evt.m_ActorGuid, sourceGuid, evt.m_TokenId, sourceId, ContributionKind.Guard, 0f, false);
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

                    if (IsDamageBonusToken(token) || GetDamageBonusPct(token) > 0.0001f)
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
                    if ((combatRemoval || transferRemoval) && (IsDamageBonusToken(evt.Token) || GetDamageBonusPct(evt.Token) > 0.0001f))
                    {
                        var damageEffect = PopActiveEffect(targetGuid, evt.Token.Id, ContributionKind.DamageBonus);
                        if (damageEffect != null) AddPending(_pendingDamageEffects, targetGuid, damageEffect);
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
                    bool lockedFloorBuff = TryResolveLockedBuffSource(evt.TargetActorGuid, evt.Buff.Id, out var lockedProviderGuid, out var lockedSourceId);
                    if (lockedFloorBuff)
                    {
                        if (lockedProviderGuid != 0) providerGuid = lockedProviderGuid;
                        if (!string.IsNullOrEmpty(lockedSourceId)) sourceId = lockedSourceId;
                    }

                    bool allowedSource = IsContributionSource(evt.SourceType, evt.SourceId) || lockedFloorBuff;
                    if ((!allowedSource || !IsEligibleFriendlyExternalSource(providerGuid, evt.TargetActorGuid)) &&
                        (damagePct > 0.0001f || isShieldBuff) &&
                        TryResolveFloorBuffSource(evt.TargetActorGuid, evt.Buff.Id, evt.SourceType, evt.SourceId, damagePct > 0.0001f, isShieldBuff, out var floorProviderGuid, out var floorSourceId))
                    {
                        providerGuid = floorProviderGuid;
                        if (!string.IsNullOrEmpty(floorSourceId)) sourceId = floorSourceId;
                    }

                    if (!IsEligibleFriendlyExternalSource(providerGuid, evt.TargetActorGuid)) return;

                    if (lockedFloorBuff)
                        AddOrUpdateFloorSource(evt.TargetActorGuid, providerGuid, evt.Buff, sourceId);

                    if (damagePct > 0.0001f)
                    {
                        AddActiveEffect(evt.TargetActorGuid, providerGuid, evt.Buff.Id, sourceId, ContributionKind.DamageBonus, damagePct, true);
                    }

                    if (isShieldBuff)
                    {
                        AddActiveEffect(evt.TargetActorGuid, providerGuid, evt.Buff.Id, sourceId, ContributionKind.Shield, 0f, true);
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
                    if (!transferRemoval)
                        RemoveFloorSource(evt.ActorGuid, evt.Buff.Id);
                    var damageEffect = PopActiveEffect(evt.ActorGuid, evt.Buff.Id, ContributionKind.DamageBonus);
                    var shieldEffect = PopActiveEffect(evt.ActorGuid, evt.Buff.Id, ContributionKind.Shield);
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
                _statusHints.Clear();
                _floorSources.Clear();
                _activeCombos.Clear();
                _pendingComboConsumes.Clear();
                _playerSnapshot = Array.Empty<ContributionStats>();
                _snapshotDirty = true;
                _currentRound = 0;
            }
        }

        private void TrackDamageBonusContribution(Assets.Code.Skill.SkillCalculation.ActorResult ar, float hpBefore)
        {
            uint performerGuid = ar.m_PerformerActorGuid;
            uint targetGuid = ar.m_TargetActorGuid;
            if (performerGuid == 0 || targetGuid == 0 || performerGuid == targetGuid) return;
            if (!IsPlayerTeam(performerGuid) || IsPlayerTeam(targetGuid)) return;

            var effects = GetDamageEffectsForActor(performerGuid);
            if (effects.Count == 0)
                effects = GetCurrentFloorDamageEffectsForActor(performerGuid);
            if (effects.Count == 0) return;

            float totalBonusPct = 0f;
            for (int i = 0; i < effects.Count; i++)
                totalBonusPct += Mathf.Max(0f, effects[i].DamageBonusPct);
            if (totalBonusPct <= 0.0001f) return;

            float effectiveWithBuff = Mathf.Min(ar.HealthDamage, Mathf.Max(0f, hpBefore));
            float damageWithoutSkillBuff = ar.HealthDamage / (1f + totalBonusPct);
            float effectiveWithoutBuff = Mathf.Min(damageWithoutSkillBuff, Mathf.Max(0f, hpBefore));
            float contribution = Mathf.Max(0f, effectiveWithBuff - effectiveWithoutBuff);
            if (contribution <= 0.0001f) return;

            for (int i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                float pct = Mathf.Max(0f, effect.DamageBonusPct);
                if (pct <= 0f) continue;
                float share = contribution * (pct / totalBonusPct);
                var stats = GetOrCreate(effect.ProviderGuid);
                stats.BonusDamage += share;
                effect.Used = true;
            }
            _snapshotDirty = true;
        }

        private void TrackVulnerableContribution(Assets.Code.Skill.SkillCalculation.ActorResult ar, float hpBefore)
        {
            uint performerGuid = ar.m_PerformerActorGuid;
            uint targetGuid = ar.m_TargetActorGuid;
            if (performerGuid == 0 || targetGuid == 0 || performerGuid == targetGuid) return;
            if (!IsPlayerTeam(performerGuid) || IsPlayerTeam(targetGuid)) return;

            var effects = GetVulnerableEffectsForTarget(targetGuid);
            if (effects.Count == 0) return;

            float effectiveDamage = Mathf.Min(Mathf.Max(0f, ar.HealthDamage), Mathf.Max(0f, hpBefore));
            float contribution = effectiveDamage / 3f;
            if (contribution <= 0.0001f) return;

            float share = contribution / effects.Count;
            for (int i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                if (effect == null || effect.ProviderGuid == 0 || !IsPlayerTeam(effect.ProviderGuid)) continue;
                var stats = GetOrCreate(effect.ProviderGuid);
                stats.VulnerableDamage += share;
                effect.Used = true;
            }
            _snapshotDirty = true;
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
                    if (IsVulnerableToken(token))
                    {
                        if (!IsPlayerTeam(actorGuid) && IsPlayerTeam(tokenInstance.SourceActorGuid))
                        {
                            AddPendingUnique(_pendingVulnerableEffects, actorGuid, new ActiveEffect
                            {
                                TargetGuid = actorGuid,
                                ProviderGuid = tokenInstance.SourceActorGuid,
                                EffectId = token.Id ?? "",
                                SourceId = tokenInstance.SourceId ?? "",
                                Kind = ContributionKind.Vulnerable,
                                IsBuff = false
                            });
                        }
                        continue;
                    }

                    if (!IsPlayerTeam(actorGuid)) continue;
                    float bonusPct = GetDamageBonusPct(token);
                    if (bonusPct <= 0.0001f) continue;
                    if (!IsDamageBonusToken(token) && bonusPct <= 0.0001f) continue;
                    if (!IsEligibleFriendlyExternalSource(tokenInstance.SourceActorGuid, actorGuid)) continue;

                    AddPendingUnique(_pendingDamageEffects, actorGuid, new ActiveEffect
                    {
                        TargetGuid = actorGuid,
                        ProviderGuid = tokenInstance.SourceActorGuid,
                        EffectId = token.Id ?? "",
                        SourceId = tokenInstance.SourceId ?? "",
                        Kind = ContributionKind.DamageBonus,
                        DamageBonusPct = bonusPct,
                        IsBuff = false
                    });
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
            if (effects.Count == 0)
                effects = GetCurrentFloorShieldEffectsForActor(targetGuid);

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
            for (int i = 0; i < _activeEffects.Count; i++)
            {
                var effect = _activeEffects[i];
                if (effect.TargetGuid == actorGuid && effect.Kind == ContributionKind.DamageBonus)
                    result.Add(effect);
            }
            if (_pendingDamageEffects.TryGetValue(actorGuid, out var pending))
                result.AddRange(pending);
            return result;
        }

        private List<ActiveEffect> GetVulnerableEffectsForTarget(uint actorGuid)
        {
            var result = new List<ActiveEffect>();
            if (_pendingVulnerableEffects.TryGetValue(actorGuid, out var pending))
                result.AddRange(pending);
            if (result.Count > 0) return result;

            for (int i = 0; i < _activeEffects.Count; i++)
            {
                var effect = _activeEffects[i];
                if (effect.TargetGuid == actorGuid && effect.Kind == ContributionKind.Vulnerable)
                {
                    result.Add(effect);
                    break;
                }
            }
            return result;
        }

        private List<ActiveEffect> GetActiveShieldEffectsForActor(uint actorGuid)
        {
            var result = new List<ActiveEffect>();
            for (int i = 0; i < _activeEffects.Count; i++)
            {
                var effect = _activeEffects[i];
                if (effect.TargetGuid == actorGuid && effect.Kind == ContributionKind.Shield)
                    result.Add(effect);
            }
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

        private List<ActiveEffect> GetCurrentFloorDamageEffectsForActor(uint actorGuid)
        {
            return GetCurrentFloorEffectsForActor(actorGuid, ContributionKind.DamageBonus);
        }

        private List<ActiveEffect> GetCurrentFloorShieldEffectsForActor(uint actorGuid)
        {
            return GetCurrentFloorEffectsForActor(actorGuid, ContributionKind.Shield);
        }

        private List<ActiveEffect> GetCurrentFloorEffectsForActor(uint actorGuid, ContributionKind kind)
        {
            var result = new List<ActiveEffect>();
            try
            {
                var actor = TryResolveActor(actorGuid);
                if (actor == null) return result;

                var instances = actor.GetLockedTeamPositionActorContainerInstances();
                if (instances == null || instances.Count == 0) return result;

                for (int i = 0; i < instances.Count; i++)
                {
                    var instance = instances[i];
                    if (instance == null || instance.LockedTeamPosition != actor.TeamPosition) continue;

                    var token = instance.ActorContainerDefinition as TokenDefinition;
                    var buff = instance.ActorContainerDefinition as BuffDefinition;
                    string effectId = instance.ActorContainerDefinition?.Id ?? "";
                    float bonusPct = 0f;
                    bool matchesKind = false;

                    if (kind == ContributionKind.DamageBonus)
                    {
                        bonusPct = token != null ? GetDamageBonusPct(token) : GetDamageBonusPct(buff);
                        matchesKind = bonusPct > 0.0001f;
                    }
                    else if (kind == ContributionKind.Shield)
                    {
                        matchesKind = token != null ? IsShieldToken(token) : GetDamageReductionPct(buff) > 0.0001f;
                    }

                    if (!matchesKind) continue;

                    if (!TryResolveFloorProviderForInstance(actor.ActorGuid, instance, token, buff, kind, out var providerGuid, out var sourceId))
                        continue;

                    result.Add(new ActiveEffect
                    {
                        TargetGuid = actor.ActorGuid,
                        ProviderGuid = providerGuid,
                        EffectId = effectId,
                        SourceId = sourceId ?? "",
                        Kind = kind,
                        DamageBonusPct = bonusPct,
                        IsBuff = buff != null
                    });
                    GetOrCreate(providerGuid);
                }
            }
            catch { }
            return result;
        }

        private bool TryResolveFloorProviderForInstance(uint actorGuid, IActorContainerInstance instance, TokenDefinition token, BuffDefinition buff, ContributionKind kind, out uint providerGuid, out string sourceId)
        {
            providerGuid = 0;
            sourceId = instance?.SourceId ?? "";
            if (instance == null) return false;

            uint directGuid = GetSourceActorGuid(instance);
            if (IsEligibleFriendlyExternalSource(directGuid, actorGuid))
            {
                providerGuid = directGuid;
                return true;
            }

            bool isDamageBonus = kind == ContributionKind.DamageBonus;
            bool isShield = kind == ContributionKind.Shield;

            if (token != null &&
                TryResolveFloorTokenSource(actorGuid, token.Id, instance.SourceType, instance.SourceId, isDamageBonus, isShield, out var floorTokenGuid, out var floorTokenSourceId))
            {
                providerGuid = floorTokenGuid;
                if (!string.IsNullOrEmpty(floorTokenSourceId)) sourceId = floorTokenSourceId;
                return IsEligibleFriendlyExternalSource(providerGuid, actorGuid);
            }

            if (buff != null &&
                TryResolveFloorBuffSource(actorGuid, buff.Id, instance.SourceType, instance.SourceId, isDamageBonus, isShield, out var floorBuffGuid, out var floorBuffSourceId))
            {
                providerGuid = floorBuffGuid;
                if (!string.IsNullOrEmpty(floorBuffSourceId)) sourceId = floorBuffSourceId;
                return IsEligibleFriendlyExternalSource(providerGuid, actorGuid);
            }

            return false;
        }

        private void MarkPendingShieldsUsed(uint actorGuid)
        {
            if (!_pendingShieldEffects.TryGetValue(actorGuid, out var effects)) return;
            for (int i = 0; i < effects.Count; i++)
                effects[i].Used = true;
        }

        private void AddActiveEffect(uint targetGuid, uint providerGuid, string effectId, string sourceId, ContributionKind kind, float damageBonusPct, bool isBuff)
        {
            _activeEffects.Add(new ActiveEffect
            {
                TargetGuid = targetGuid,
                ProviderGuid = providerGuid,
                EffectId = effectId ?? "",
                SourceId = sourceId ?? "",
                Kind = kind,
                DamageBonusPct = damageBonusPct,
                IsBuff = isBuff
            });
            GetOrCreate(providerGuid);
            _snapshotDirty = true;
        }

        private ActiveEffect PopActiveEffect(uint targetGuid, string effectId, ContributionKind kind)
        {
            for (int i = 0; i < _activeEffects.Count; i++)
            {
                var effect = _activeEffects[i];
                if (effect.TargetGuid != targetGuid || effect.Kind != kind) continue;
                if (!string.Equals(effect.EffectId, effectId ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                _activeEffects.RemoveAt(i);
                return effect;
            }

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

        private static void AddPending(Dictionary<uint, List<ActiveEffect>> map, uint actorGuid, ActiveEffect effect)
        {
            if (!map.TryGetValue(actorGuid, out var list))
            {
                list = new List<ActiveEffect>();
                map[actorGuid] = list;
            }
            list.Add(effect);
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
            if (TryResolveTokenSource(targetGuid, evt.Token.Id, evt.Source, evt.SourceId, out var sourceGuid, out var sourceId) &&
                sourceGuid != 0 &&
                IsPlayerTeam(sourceGuid))
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

        private void RecordFloorContributionSources(EventSkillFinalizeResults evt)
        {
            try
            {
                if (evt?.ActorResults == null) return;
                string skillId = evt.SkillId ?? "";

                foreach (var ar in evt.ActorResults)
                {
                    if (ar?.m_AppliedEffectsOutputContainer == null) continue;

                    foreach (var output in ar.m_AppliedEffectsOutputContainer.Outputs)
                    {
                        if (output == null || output.m_TargetActor == null) continue;
                        var targetActor = output.m_TargetActor;
                        if (!IsPlayerTeam(targetActor.ActorGuid)) continue;

                        uint providerGuid = output.m_PerformerActor != null
                            ? output.m_PerformerActor.ActorGuid
                            : ar.m_PerformerActorGuid;
                        if (providerGuid == 0 || !IsPlayerTeam(providerGuid)) continue;

                        foreach (var effect in output.EffectInstancesToApply)
                        {
                            var def = effect?.EffectDefinition;
                            if (def == null || !def.m_IsLockedTeamPosition) continue;

                            string sourceId = !string.IsNullOrEmpty(effect.SourceId) ? effect.SourceId : skillId;
                            if (!TryCreateFloorSource(
                                    targetActor.ActorGuid,
                                    providerGuid,
                                    targetActor.TeamIndex,
                                    targetActor.TeamPosition,
                                    def,
                                    sourceId,
                                    skillId,
                                    out var floorSource))
                            {
                                continue;
                            }

                            AddOrUpdateFloorSource(floorSource);
                        }
                    }
                }
            }
            catch { }
        }

        private void AddOrUpdateFloorSource(uint targetGuid, uint providerGuid, BuffDefinition buff, string sourceId)
        {
            var actor = TryResolveActor(targetGuid);
            int teamIndex = actor != null ? actor.TeamIndex : -1;
            int teamPosition = actor != null ? actor.TeamPosition : -1;
            if (!TryCreateFloorSource(targetGuid, providerGuid, teamIndex, teamPosition, buff, sourceId, out var floorSource)) return;
            AddOrUpdateFloorSource(floorSource);
        }

        private void AddOrUpdateFloorSource(FloorSource floorSource)
        {
            if (floorSource == null || floorSource.ProviderGuid == 0) return;
            for (int i = _floorSources.Count - 1; i >= 0; i--)
            {
                if (IsSameFloorPlacement(_floorSources[i], floorSource))
                    _floorSources.RemoveAt(i);
            }
            _floorSources.Add(floorSource);
            GetOrCreate(floorSource.ProviderGuid);
        }

        private void RemoveFloorSource(uint targetGuid, string buffId)
        {
            for (int i = _floorSources.Count - 1; i >= 0; i--)
            {
                var floorSource = _floorSources[i];
                if (!string.Equals(floorSource.BuffId ?? "", buffId ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                if (floorSource.TargetGuid == targetGuid)
                {
                    _floorSources.RemoveAt(i);
                    continue;
                }

                var actor = TryResolveActor(targetGuid);
                if (actor != null &&
                    floorSource.TeamIndex == actor.TeamIndex &&
                    floorSource.TeamPosition == actor.TeamPosition)
                {
                    _floorSources.RemoveAt(i);
                }
            }
        }

        private bool TryResolveFloorTokenSource(uint targetGuid, string tokenId, SourceType eventSourceType, string eventSourceId, bool isDamageBonus, bool isShield, out uint providerGuid, out string sourceId)
        {
            providerGuid = 0;
            sourceId = "";
            var token = GetTokenDefinition(tokenId);

            for (int i = _floorSources.Count - 1; i >= 0; i--)
            {
                var floorSource = _floorSources[i];
                var actor = TryResolveActor(targetGuid);
                if (!FloorSourceMatchesCurrentActor(floorSource, actor, targetGuid)) continue;
                if (actor != null && floorSource.TeamPosition >= 0 && !HasLiveFloorInstance(actor, floorSource)) continue;
                bool sourceMatches = FloorEventMatches(floorSource, eventSourceType, eventSourceId);

                bool matchesDamage = isDamageBonus &&
                                     (FloorSourceMatchesToken(floorSource.DamageTokenIds, floorSource.DamageTokenTags, token, tokenId) ||
                                      (sourceMatches && IsKnownFloorSource(floorSource)));
                bool matchesShield = isShield &&
                                     (FloorSourceMatchesToken(floorSource.ShieldTokenIds, floorSource.ShieldTokenTags, token, tokenId) ||
                                      (sourceMatches && IsKnownFloorSource(floorSource)));
                if (!matchesDamage && !matchesShield) continue;

                providerGuid = floorSource.ProviderGuid;
                sourceId = floorSource.SourceId ?? "";
                return providerGuid != 0;
            }

            return false;
        }

        private bool TryResolveFloorBuffSource(uint targetGuid, string buffId, SourceType eventSourceType, string eventSourceId, bool isDamageBonus, bool isShield, out uint providerGuid, out string sourceId)
        {
            providerGuid = 0;
            sourceId = "";

            for (int i = _floorSources.Count - 1; i >= 0; i--)
            {
                var floorSource = _floorSources[i];
                var actor = TryResolveActor(targetGuid);
                if (!FloorSourceMatchesCurrentActor(floorSource, actor, targetGuid)) continue;
                if (actor != null && floorSource.TeamPosition >= 0 && !HasLiveFloorInstance(actor, floorSource)) continue;
                bool sourceMatches = FloorEventMatches(floorSource, eventSourceType, eventSourceId);

                bool matchesDamage = isDamageBonus &&
                                     (ContainsIgnoreCase(floorSource.DamageBuffIds, buffId) ||
                                      (sourceMatches && IsKnownFloorSource(floorSource)));
                bool matchesShield = isShield &&
                                     (ContainsIgnoreCase(floorSource.ShieldBuffIds, buffId) ||
                                      (sourceMatches && IsKnownFloorSource(floorSource)));
                if (!matchesDamage && !matchesShield) continue;

                providerGuid = floorSource.ProviderGuid;
                sourceId = floorSource.SourceId ?? "";
                return providerGuid != 0;
            }

            return false;
        }

        private static bool TryCreateFloorSource(uint targetGuid, uint providerGuid, int teamIndex, int teamPosition, BuffDefinition buff, string sourceId, out FloorSource floorSource)
        {
            floorSource = null;
            if (targetGuid == 0 || providerGuid == 0 || buff == null) return false;

            var created = new FloorSource
            {
                TargetGuid = targetGuid,
                ProviderGuid = providerGuid,
                TeamIndex = teamIndex,
                TeamPosition = teamPosition,
                BuffId = buff.Id ?? "",
                SourceId = sourceId ?? "",
                DirectDamageBonus = GetDamageBonusPct(buff) > 0.0001f,
                DirectShield = GetDamageReductionPct(buff) > 0.0001f
            };

            CollectFloorSourceCapabilities(created, buff);
            if (!FloorSourceHasContribution(created) && !IsKnownFloorSource(created)) return false;

            floorSource = created;
            return true;
        }

        private static bool TryCreateFloorSource(uint targetGuid, uint providerGuid, int teamIndex, int teamPosition, EffectDefinition effect, string sourceId, string skillId, out FloorSource floorSource)
        {
            floorSource = null;
            if (targetGuid == 0 || providerGuid == 0 || effect == null) return false;

            var created = new FloorSource
            {
                TargetGuid = targetGuid,
                ProviderGuid = providerGuid,
                TeamIndex = teamIndex,
                TeamPosition = teamPosition,
                BuffId = "",
                SkillId = skillId ?? "",
                SourceId = sourceId ?? ""
            };
            AddUnique(created.FloorEffectIds, effect.m_Id);

            if (effect.m_TokenAddAmount > 0 || effect.m_TokenAddAmountRange > 0)
                RegisterFloorTokenCapability(created, effect.m_TokenAddId, effect.m_TokenAddTag);
            if (effect.m_TokenConvertAmount > 0 || effect.m_TokenConvertAmountRange > 0)
                RegisterFloorTokenCapability(created, effect.m_TokenConvertToId, null);

            foreach (var addedBuff in effect.Buffs)
            {
                if (addedBuff == null) continue;
                if (string.IsNullOrEmpty(created.BuffId))
                    created.BuffId = addedBuff.Id ?? "";

                float damagePct = GetDamageBonusPct(addedBuff);
                float reductionPct = GetDamageReductionPct(addedBuff);
                created.DirectDamageBonus |= damagePct > 0.0001f;
                created.DirectShield |= reductionPct > 0.0001f;

                if (damagePct > 0.0001f)
                    AddUnique(created.DamageBuffIds, addedBuff.Id);
                if (reductionPct > 0.0001f)
                    AddUnique(created.ShieldBuffIds, addedBuff.Id);

                CollectFloorSourceCapabilities(created, addedBuff);
            }

            if (!FloorSourceHasContribution(created) && !IsKnownFloorSource(created)) return false;

            floorSource = created;
            return true;
        }

        private static bool IsSameFloorPlacement(FloorSource existing, FloorSource candidate)
        {
            if (existing == null || candidate == null) return false;
            bool samePosition = existing.TeamIndex >= 0 &&
                                candidate.TeamIndex >= 0 &&
                                existing.TeamIndex == candidate.TeamIndex &&
                                existing.TeamPosition == candidate.TeamPosition;
            bool sameTarget = existing.TeamIndex < 0 &&
                              candidate.TeamIndex < 0 &&
                              existing.TargetGuid == candidate.TargetGuid;
            if (!samePosition && !sameTarget) return false;

            if (NonEmptyIdMatches(existing.BuffId, candidate.BuffId)) return true;
            if (NonEmptyListMatches(existing.FloorEffectIds, candidate.FloorEffectIds)) return true;
            if (NonEmptyIdMatches(existing.SourceId, candidate.SourceId)) return true;
            return IsKnownFloorSource(existing) && IsKnownFloorSource(candidate);
        }

        private static bool FloorSourceMatchesCurrentActor(FloorSource floorSource, ActorInstance actor, uint targetGuid)
        {
            if (floorSource == null) return false;
            if (floorSource.TeamIndex >= 0 && floorSource.TeamPosition >= 0)
            {
                return actor != null &&
                       actor.TeamIndex == floorSource.TeamIndex &&
                       actor.TeamPosition == floorSource.TeamPosition;
            }
            return floorSource.TargetGuid == targetGuid;
        }

        private static bool HasLiveFloorInstance(ActorInstance actor, FloorSource floorSource)
        {
            try
            {
                if (actor == null || floorSource == null) return false;
                IReadOnlyList<IActorContainerInstance> instances = actor.GetLockedTeamPositionActorContainerInstances();
                if (instances == null || instances.Count == 0) return false;

                for (int i = 0; i < instances.Count; i++)
                {
                    var instance = instances[i];
                    if (instance == null || instance.LockedTeamPosition != floorSource.TeamPosition) continue;

                    string instanceId = instance.ActorContainerDefinition?.Id ?? "";
                    string instanceSourceId = instance.SourceId ?? "";
                    uint instanceSourceGuid = GetSourceActorGuid(instance);
                    bool sameProvider = instanceSourceGuid == 0 ||
                                        floorSource.ProviderGuid == 0 ||
                                        instanceSourceGuid == floorSource.ProviderGuid;
                    if (!sameProvider) continue;

                    if (NonEmptyIdMatches(instanceId, floorSource.BuffId) ||
                        NonEmptyIdMatches(instanceSourceId, floorSource.SourceId) ||
                        NonEmptyIdMatches(instanceSourceId, floorSource.SkillId) ||
                        ContainsIgnoreCase(floorSource.FloorEffectIds, instanceId) ||
                        ContainsIgnoreCase(floorSource.FloorEffectIds, instanceSourceId))
                    {
                        return true;
                    }

                    if (IsKnownFloorSource(floorSource) &&
                        (IsKnownFloorId(instanceId) || IsKnownFloorId(instanceSourceId) || sameProvider))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static uint GetSourceActorGuid(IActorContainerInstance instance)
        {
            try
            {
                var prop = instance?.GetType().GetProperty("SourceActorGuid");
                if (prop == null) return 0;
                object value = prop.GetValue(instance, null);
                return value is uint guid ? guid : 0;
            }
            catch { return 0; }
        }

        private static void CollectFloorSourceCapabilities(FloorSource floorSource, BuffDefinition buff)
        {
            try
            {
                var actorDataEffects = buff?.ActorDataEffects;
                if (actorDataEffects == null) return;

                foreach (var effectGroup in actorDataEffects.EffectGroups)
                {
                    if (effectGroup == null || effectGroup.SourceEffects == null) continue;
                    foreach (var sourceEffect in effectGroup.SourceEffects)
                    {
                        var effect = sourceEffect?.Definition;
                        if (effect == null) continue;

                        if (effect.m_TokenAddAmount > 0 || effect.m_TokenAddAmountRange > 0)
                            RegisterFloorTokenCapability(floorSource, effect.m_TokenAddId, effect.m_TokenAddTag);
                        if (effect.m_TokenConvertAmount > 0 || effect.m_TokenConvertAmountRange > 0)
                            RegisterFloorTokenCapability(floorSource, effect.m_TokenConvertToId, null);

                        foreach (var addedBuff in effect.Buffs)
                        {
                            if (addedBuff == null) continue;
                            if (GetDamageBonusPct(addedBuff) > 0.0001f)
                                AddUnique(floorSource.DamageBuffIds, addedBuff.Id);
                            if (GetDamageReductionPct(addedBuff) > 0.0001f)
                                AddUnique(floorSource.ShieldBuffIds, addedBuff.Id);
                        }
                    }
                }
            }
            catch { }
        }

        private static void RegisterFloorTokenCapability(FloorSource floorSource, string tokenId, string tokenTag)
        {
            if (!string.IsNullOrEmpty(tokenId))
            {
                var token = GetTokenDefinition(tokenId);
                if (IsDamageBonusToken(token) || GetDamageBonusPct(token) > 0.0001f)
                    AddUnique(floorSource.DamageTokenIds, tokenId);
                if (IsShieldToken(token))
                    AddUnique(floorSource.ShieldTokenIds, tokenId);
            }

            if (!string.IsNullOrEmpty(tokenTag))
            {
                bool damageTag = false;
                bool shieldTag = false;
                try
                {
                    var tokens = SingletonMonoBehaviour<Library<string, TokenDefinition>>.Instance?.GetLibraryElements();
                    if (tokens != null)
                    {
                        foreach (var token in tokens)
                        {
                            if (!TokenHasTag(token, tokenTag)) continue;
                            damageTag |= IsDamageBonusToken(token) || GetDamageBonusPct(token) > 0.0001f;
                            shieldTag |= IsShieldToken(token);
                        }
                    }
                }
                catch { }

                string lowerTag = tokenTag.ToLowerInvariant();
                damageTag |= lowerTag.Contains("strength") || lowerTag.Contains("damage");
                shieldTag |= lowerTag.Contains("block");

                if (damageTag) AddUnique(floorSource.DamageTokenTags, tokenTag);
                if (shieldTag) AddUnique(floorSource.ShieldTokenTags, tokenTag);
            }
        }

        private static bool FloorSourceHasContribution(FloorSource floorSource)
        {
            return floorSource.DirectDamageBonus ||
                   floorSource.DirectShield ||
                   floorSource.DamageTokenIds.Count > 0 ||
                   floorSource.DamageTokenTags.Count > 0 ||
                   floorSource.DamageBuffIds.Count > 0 ||
                   floorSource.ShieldTokenIds.Count > 0 ||
                   floorSource.ShieldTokenTags.Count > 0 ||
                   floorSource.ShieldBuffIds.Count > 0;
        }

        private static bool FloorSourceMatchesToken(List<string> tokenIds, List<string> tokenTags, TokenDefinition token, string tokenId)
        {
            if (ContainsIgnoreCase(tokenIds, tokenId)) return true;
            for (int i = 0; i < tokenTags.Count; i++)
            {
                if (TokenHasTag(token, tokenTags[i])) return true;
            }
            return false;
        }

        private static bool TryResolveLockedBuffSource(uint actorGuid, string buffId, out uint providerGuid, out string sourceId)
        {
            providerGuid = 0;
            sourceId = "";
            try
            {
                var actor = TryResolveActor(actorGuid);
                if (actor?.BuffContainer == null) return false;

                var buffInstance = FindNewestBuffInstance(actor, buffId, true);
                if (buffInstance == null) return false;

                providerGuid = buffInstance.SourceActorGuid;
                sourceId = buffInstance.SourceId ?? "";
                return true;
            }
            catch { return false; }
        }

        private static BuffInstance FindNewestBuffInstance(ActorInstance actor, string buffId, bool requireLocked)
        {
            try
            {
                var instances = actor.BuffContainer.GetInstances(buff =>
                    buff != null &&
                    buff.Definition != null &&
                    string.Equals(buff.Definition.Id ?? "", buffId ?? "", StringComparison.OrdinalIgnoreCase) &&
                    (!requireLocked || buff.IsLockedTeamPosition));

                if (instances != null && instances.Count > 0)
                    return instances[instances.Count - 1];
            }
            catch { }
            return null;
        }

        private static bool TokenHasTag(TokenDefinition token, string tag)
        {
            if (token == null || token.Tags == null || string.IsNullOrEmpty(tag)) return false;
            for (int i = 0; i < token.Tags.Count; i++)
            {
                if (string.Equals(token.Tags[i] ?? "", tag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsKnownFloorSource(FloorSource floorSource)
        {
            string id = ((floorSource.BuffId ?? "") + " " +
                         (floorSource.SkillId ?? "") + " " +
                         (floorSource.SourceId ?? "") + " " +
                         string.Join(" ", floorSource.FloorEffectIds.ToArray())).ToLowerInvariant();
            return id.Contains("consecration") ||
                   id.Contains("consecrate") ||
                   id.Contains("fortitude") ||
                   IsKnownLightFloorId(id);
        }

        private static bool FloorEventMatches(FloorSource floorSource, SourceType eventSourceType, string eventSourceId)
        {
            if (floorSource == null) return false;
            if (!string.IsNullOrEmpty(eventSourceId))
            {
                if (!string.IsNullOrEmpty(floorSource.SourceId) && SourceIdMatches(floorSource.SourceId, eventSourceId))
                    return true;
                if (NonEmptyIdMatches(floorSource.BuffId, eventSourceId) ||
                    NonEmptyIdMatches(floorSource.SkillId, eventSourceId) ||
                    ContainsIgnoreCase(floorSource.FloorEffectIds, eventSourceId))
                {
                    return true;
                }
                return IsKnownFloorId(eventSourceId);
            }

            return IsSourceType(eventSourceType, "locked_team_position_transfer") ||
                   IsSourceType(eventSourceType, "buff") ||
                   IsSourceType(eventSourceType, "skill_buff");
        }

        private static bool IsKnownFloorId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string lower = id.ToLowerInvariant();
            return lower.Contains("consecration") ||
                   lower.Contains("consecrate") ||
                   lower.Contains("fortitude") ||
                   IsKnownLightFloorId(lower);
        }

        private static bool IsKnownLightFloorId(string lowerId)
        {
            if (string.IsNullOrEmpty(lowerId)) return false;
            string normalized = lowerId.Replace('_', ' ').Replace('-', ' ');
            return string.Equals(lowerId, "light", StringComparison.OrdinalIgnoreCase) ||
                   lowerId.EndsWith("_light", StringComparison.OrdinalIgnoreCase) ||
                   lowerId.Contains("_light_") ||
                   lowerId.EndsWith("-light", StringComparison.OrdinalIgnoreCase) ||
                   lowerId.Contains("-light-") ||
                   normalized.Contains("blessing light") ||
                   normalized.Contains("blessing of light");
        }

        private static void AddUnique(List<string> list, string value)
        {
            if (string.IsNullOrEmpty(value) || ContainsIgnoreCase(list, value)) return;
            list.Add(value);
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            if (list == null || string.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i] ?? "", value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool NonEmptyIdMatches(string left, string right)
        {
            return !string.IsNullOrEmpty(left) &&
                   !string.IsNullOrEmpty(right) &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool NonEmptyListMatches(List<string> left, List<string> right)
        {
            if (left == null || right == null) return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (ContainsIgnoreCase(right, left[i])) return true;
            }
            return false;
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
