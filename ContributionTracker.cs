using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Buff;
using Assets.Code.Buff.Events;
using Assets.Code.Combat.Events;
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
            public float ShieldPrevented;
            public float GuardProtected;
            public int ShieldWasted;
            public float TotalContribution => BonusDamage + ShieldPrevented + GuardProtected;
        }

        private enum ContributionKind
        {
            DamageBonus,
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

        private const int MaxStatusHints = 64;

        private readonly object _lock = new object();
        private readonly Dictionary<uint, ContributionStats> _stats = new Dictionary<uint, ContributionStats>();
        private readonly List<ActiveEffect> _activeEffects = new List<ActiveEffect>();
        private readonly Dictionary<uint, List<ActiveEffect>> _pendingDamageEffects = new Dictionary<uint, List<ActiveEffect>>();
        private readonly Dictionary<uint, List<ActiveEffect>> _pendingShieldEffects = new Dictionary<uint, List<ActiveEffect>>();
        private readonly Dictionary<uint, List<ActiveEffect>> _pendingGuardEffects = new Dictionary<uint, List<ActiveEffect>>();
        private readonly List<StatusSourceHint> _statusHints = new List<StatusSourceHint>();

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
                    foreach (var ar in evt.ActorResults)
                    {
                        if (ar == null) continue;
                        CacheStatusHints(ar, evt.SkillId ?? "");
                    }

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
                            float effectiveDamage = Mathf.Min(ar.HealthDamage, Mathf.Max(0f, hpBefore));
                            projectedHp[ar.m_TargetActorGuid] = Mathf.Max(0f, hpBefore - effectiveDamage);
                        }

                        if (ar.IsDamaging || ar.IsBlocked)
                        {
                            if (!TrackGuardContribution(ar, hpBefore))
                                TrackShieldContribution(ar, hpBefore);
                        }
                    }

                    FlushUnusedPendingShieldsAsWaste();
                    _pendingDamageEffects.Clear();
                    _pendingShieldEffects.Clear();
                    _pendingGuardEffects.Clear();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ContributionTracker.OnSkillFinalizeResults error: {ex.Message}");
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

                    var hint = ConsumeStatusHint(evt.m_ActorGuid, evt.m_TokenId, "ADD", evt.m_SourceId);
                    if (hint == null || !IsSkillSource(hint.SourceType, hint.SourceId)) return;
                    if (!IsEligibleFriendlyExternalSource(hint.SourceGuid, evt.m_ActorGuid)) return;

                    float bonusPct = GetDamageBonusPct(token);
                    bool isDamageBonus = bonusPct > 0.0001f || IsDamageBonusToken(token);
                    bool isShield = IsShieldToken(token);
                    bool isGuard = IsGuardToken(token);
                    if (!isDamageBonus && !isShield && !isGuard) return;

                    int amount = Math.Max(1, evt.m_AddAmount);
                    for (int i = 0; i < amount; i++)
                    {
                        if (isDamageBonus && bonusPct > 0.0001f)
                            AddActiveEffect(evt.m_ActorGuid, hint.SourceGuid, evt.m_TokenId, hint.SourceId, ContributionKind.DamageBonus, bonusPct, false);
                        if (isShield)
                            AddActiveEffect(evt.m_ActorGuid, hint.SourceGuid, evt.m_TokenId, hint.SourceId, ContributionKind.Shield, 0f, false);
                        if (isGuard)
                            AddActiveEffect(evt.m_ActorGuid, hint.SourceGuid, evt.m_TokenId, hint.SourceId, ContributionKind.Guard, 0f, false);
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
                    if (token == null || !IsPlayerTeam(evt.m_ActorGuid)) return;

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
                    if (!IsPlayerTeam(targetGuid)) return;

                    bool combatRemoval = IsSourceType(evt.Source, "combat");
                    if (combatRemoval && (IsDamageBonusToken(evt.Token) || GetDamageBonusPct(evt.Token) > 0.0001f))
                    {
                        var damageEffect = PopActiveEffect(targetGuid, evt.Token.Id, ContributionKind.DamageBonus);
                        if (damageEffect != null) AddPending(_pendingDamageEffects, targetGuid, damageEffect);
                        return;
                    }

                    if (IsShieldToken(evt.Token))
                    {
                        var shieldEffect = PopActiveEffect(targetGuid, evt.Token.Id, ContributionKind.Shield);
                        if (shieldEffect == null) return;

                        if (combatRemoval)
                        {
                            AddPending(_pendingShieldEffects, targetGuid, shieldEffect);
                        }
                        else if (!shieldEffect.Used)
                        {
                            CountShieldWaste(shieldEffect);
                        }
                    }

                    if (IsGuardToken(evt.Token))
                    {
                        var guardEffect = PopActiveEffect(targetGuid, evt.Token.Id, ContributionKind.Guard);
                        if (guardEffect != null && combatRemoval)
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
                    if (evt.Buff == null || !IsSkillSource(evt.SourceType, evt.SourceId)) return;
                    if (!IsEligibleFriendlyExternalSource(evt.PerformerActorGuid, evt.TargetActorGuid)) return;

                    float damagePct = GetDamageBonusPct(evt.Buff);
                    if (damagePct > 0.0001f)
                    {
                        AddActiveEffect(evt.TargetActorGuid, evt.PerformerActorGuid, evt.Buff.Id, evt.SourceId, ContributionKind.DamageBonus, damagePct, true);
                    }

                    if (GetDamageReductionPct(evt.Buff) > 0.0001f)
                    {
                        AddActiveEffect(evt.TargetActorGuid, evt.PerformerActorGuid, evt.Buff.Id, evt.SourceId, ContributionKind.Shield, 0f, true);
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
                    var damageEffect = PopActiveEffect(evt.ActorGuid, evt.Buff.Id, ContributionKind.DamageBonus);
                    var shieldEffect = PopActiveEffect(evt.ActorGuid, evt.Buff.Id, ContributionKind.Shield);
                    if (shieldEffect != null && !shieldEffect.Used)
                        CountShieldWaste(shieldEffect);
                    if (damageEffect != null) _snapshotDirty = true;
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
                players.Sort((a, b) => b.TotalContribution.CompareTo(a.TotalContribution));
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
                _pendingShieldEffects.Clear();
                _pendingGuardEffects.Clear();
                _statusHints.Clear();
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

            if (effects.Count == 0) return;

            if (prevented <= 0.0001f)
            {
                for (int i = 0; i < effects.Count; i++)
                    CountShieldWaste(effects[i]);
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

        private void FlushUnusedPendingShieldsAsWaste()
        {
            foreach (var kvp in _pendingShieldEffects)
            {
                var effects = kvp.Value;
                for (int i = 0; i < effects.Count; i++)
                {
                    if (!effects[i].Used)
                        CountShieldWaste(effects[i]);
                }
            }
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

        private void CountShieldWaste(ActiveEffect effect)
        {
            if (effect == null || effect.Used) return;
            var stats = GetOrCreate(effect.ProviderGuid);
            stats.ShieldWasted++;
            effect.Used = true;
            _snapshotDirty = true;
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
                ShieldPrevented = s.ShieldPrevented,
                GuardProtected = s.GuardProtected,
                ShieldWasted = s.ShieldWasted
            };
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
                    pct += Mathf.Max(0f, stats.GetStatAddValue(ActorStatType.HEALTH_DAMAGE_DEALT_PERCENT, (string)null));
                if (stats.GetHasStat(ActorStatType.HEALTH_DAMAGE_DEALT_MULT_PERCENT))
                {
                    float multTotal = stats.GetStatTotal(ActorStatType.HEALTH_DAMAGE_DEALT_MULT_PERCENT, (string)null, true);
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
                float total = stats.GetStatTotal(ActorStatType.HEALTH_DAMAGE_RECEIVED_PERCENT, (string)null, true);
                return Mathf.Max(0f, 1f - total);
            }
            catch { return 0f; }
        }

        private static bool IsPlayerTeam(uint guid)
        {
            try
            {
                if (!_libraryReflectionInit)
                {
                    _libraryReflectionInit = true;
                    InitLibraryTeamReflection();
                }
                if (_teamLibraryInstance == null || _getTeamLibraryElement == null) return false;
                var actor = _getTeamLibraryElement.Invoke(_teamLibraryInstance, new object[] { guid }) as ActorInstance;
                return actor != null && actor.TeamIndex == 0;
            }
            catch { }
            return false;
        }

        private static object _teamLibraryInstance;
        private static MethodInfo _getTeamLibraryElement;
        private static bool _libraryReflectionInit;

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
