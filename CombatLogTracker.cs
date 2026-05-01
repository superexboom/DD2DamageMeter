using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Assets.Code.Actor;
using Assets.Code.Actor.Events;
using Assets.Code.Buff;
using Assets.Code.Buff.Events;
using Assets.Code.Combat.Events;
using Assets.Code.Dot.Events;
using Assets.Code.Effect;
using Assets.Code.Events;
using Assets.Code.Library;
using Assets.Code.Skill.Events;
using Assets.Code.Source;
using Assets.Code.Token;
using Assets.Code.Token.Events;
using Assets.Code.Utils;
using UnityEngine;

namespace DD2DamageMeter
{
    public class CombatLogTracker
    {
        public class LogEntry
        {
            public int Round;
            public string SourceName;
            public string TargetName;
            public bool SourceIsPlayer;
            public bool TargetIsPlayer;
            public string ActionType; // "DMG", "CRIT", "HEAL", "DOT", "DEATH", "STRESS", "KILL", status actions
            public float Value;
            public string SkillId;
            public string Extra;
            public string DotType; // e.g. "Bleed", "Burn", "Blight"
            public string EffectId;
            public string EffectName;
            public string EffectDetail;
            public int EffectAmount;
            public float OverkillDamage;
        }

        public class RoundHeader
        {
            public int Round;
        }

        public class StatusTotals
        {
            public int PlayerBuffApplied;
            public int PlayerDebuffApplied;
            public int EnemyBuffApplied;
            public int EnemyDebuffApplied;
            public int PlayerStatusRemoved;
            public int EnemyStatusRemoved;
            public int PlayerStatusConsumed;
            public int EnemyStatusConsumed;

            public bool HasAny =>
                PlayerBuffApplied + PlayerDebuffApplied + EnemyBuffApplied + EnemyDebuffApplied +
                PlayerStatusRemoved + EnemyStatusRemoved + PlayerStatusConsumed + EnemyStatusConsumed > 0;

            public StatusTotals Clone()
            {
                return new StatusTotals
                {
                    PlayerBuffApplied = PlayerBuffApplied,
                    PlayerDebuffApplied = PlayerDebuffApplied,
                    EnemyBuffApplied = EnemyBuffApplied,
                    EnemyDebuffApplied = EnemyDebuffApplied,
                    PlayerStatusRemoved = PlayerStatusRemoved,
                    EnemyStatusRemoved = EnemyStatusRemoved,
                    PlayerStatusConsumed = PlayerStatusConsumed,
                    EnemyStatusConsumed = EnemyStatusConsumed
                };
            }

            public void Clear()
            {
                PlayerBuffApplied = 0;
                PlayerDebuffApplied = 0;
                EnemyBuffApplied = 0;
                EnemyDebuffApplied = 0;
                PlayerStatusRemoved = 0;
                EnemyStatusRemoved = 0;
                PlayerStatusConsumed = 0;
                EnemyStatusConsumed = 0;
            }
        }

        public List<object> Entries { get; private set; } = new List<object>();
        public List<object> StatusEntries { get; private set; } = new List<object>();
        private int _currentRound;
        private readonly object _lock = new object();
        private volatile bool _dirty = true;
        private volatile bool _statusDirty = true;
        private readonly StatusTotals _statusTotals = new StatusTotals();
        private readonly List<StatusSourceHint> _statusHints = new List<StatusSourceHint>();
        private readonly Dictionary<uint, ProjectedHealth> _dotProjectedHp = new Dictionary<uint, ProjectedHealth>();
        private const int MaxStatusHints = 120;

        // DOT tracking: cache dot instances to resolve performers
        // Key: (targetGuid, dotDefinitionId), Value: cached source info
        private readonly Dictionary<(uint target, string dotDefId), DotSourceInfo> _dotSourceCache = 
            new Dictionary<(uint, string), DotSourceInfo>();
        
        // Pending DOT ticks: EventDotApplied fires BEFORE EventActorHealthDamage
        // We store resolved DOT info here so HealthDamage handler can use it
        private readonly Dictionary<(uint target, string dotType), DotResolvedInfo> _pendingDotTicks =
            new Dictionary<(uint, string), DotResolvedInfo>();
        
        // Reflection fields for EffectApplyCombinedResult
        private static FieldInfo _changeAmountsField;
        private static FieldInfo _performerGuidsField;
        private static FieldInfo _sourceIdsField;
        private static bool _reflectionInit;

        private struct DotSourceInfo
        {
            public string DotType;      // display name like "Bleed", "Burn"
            public string DotDefId;     // definition id
            public string SourceType;   // source type string
            public string SourceId;     // source skill id
            public List<uint> PerformerGuids; // performers from EffectApplyCombinedResult
        }

        private struct DotResolvedInfo
        {
            public string DotType;
            public uint PerformerGuid;
            public string SourceName;
            public bool SourceIsPlayer;
            public string SkillId;
            public float Value;
        }

        private class StatusSourceHint
        {
            public uint TargetGuid;
            public string EffectId;
            public string Operation;
            public uint SourceGuid;
            public string SourceName;
            public bool SourceIsPlayer;
            public string SourceId;
            public int Round;
        }

        private struct ProjectedHealth
        {
            public int Frame;
            public float Hp;
        }

        public bool IsDirty => _dirty;
        public void ClearDirty() => _dirty = false;
        public bool IsStatusDirty => _statusDirty;
        public void ClearStatusDirty() => _statusDirty = false;
        public StatusTotals GetStatusTotalsSnapshot()
        {
            lock (_lock) return _statusTotals.Clone();
        }

        public bool HasStatusLogEntries()
        {
            lock (_lock)
            {
                for (int i = 0; i < StatusEntries.Count; i++)
                    if (StatusEntries[i] is LogEntry) return true;
                return false;
            }
        }

        private static string ToDisplayName(string dotType)
        {
            if (string.IsNullOrEmpty(dotType)) return "DOT";
            // e.g. "bleed" -> "Bleed", "blight" -> "Blight"
            return char.ToUpperInvariant(dotType[0]) + dotType.Substring(1);
        }

        private static void EnsureReflection()
        {
            if (_reflectionInit) return;
            _reflectionInit = true;
            try
            {
                var resultType = typeof(EffectApplyCombinedResult);
                _changeAmountsField = resultType.GetField("m_ChangeAmounts", BindingFlags.NonPublic | BindingFlags.Instance);
                
                // Get the private ChangeAmount type
                var changeAmountType = resultType.GetNestedType("ChangeAmount", BindingFlags.NonPublic);
                if (changeAmountType != null)
                {
                    _performerGuidsField = changeAmountType.GetField("m_PerformerActorGuids", BindingFlags.Public | BindingFlags.Instance);
                    _sourceIdsField = changeAmountType.GetField("m_SourceIds", BindingFlags.Public | BindingFlags.Instance);
                }
                
                Plugin.Log.LogInfo($"CombatLogTracker reflection: ChangeAmounts={_changeAmountsField != null}, PerformerGuids={_performerGuidsField != null}, SourceIds={_sourceIdsField != null}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CombatLogTracker reflection init failed: {ex.Message}");
            }
        }

        private (float healthChange, List<uint> performerGuids, List<string> sourceIds) ExtractFromCombinedResult(EffectApplyCombinedResult result)
        {
            EnsureReflection();
            float healthChange = result.HealthChange; // Use public property
            var performerGuids = new List<uint>();
            var sourceIds = new List<string>();
            
            if (_changeAmountsField != null)
            {
                try
                {
                    var changeAmounts = _changeAmountsField.GetValue(result) as IDictionary;
                    if (changeAmounts != null)
                    {
                        foreach (var entry in changeAmounts)
                        {
                            var valueProp = entry.GetType().GetProperty("Value");
                            if (valueProp == null) continue;
                            var changeAmount = valueProp.GetValue(entry);
                            if (changeAmount == null) continue;
                            
                            if (_performerGuidsField != null)
                            {
                                var guids = _performerGuidsField.GetValue(changeAmount) as IList;
                                if (guids != null)
                                {
                                    foreach (var g in guids)
                                        if (g is uint guid) performerGuids.Add(guid);
                                }
                            }
                            
                            if (_sourceIdsField != null)
                            {
                                var ids = _sourceIdsField.GetValue(changeAmount) as IList;
                                if (ids != null)
                                {
                                    foreach (var id in ids)
                                        if (id is string sid) sourceIds.Add(sid);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"ExtractFromCombinedResult error: {ex.Message}");
                }
            }
            
            return (healthChange, performerGuids, sourceIds);
        }

        public void OnBattleBegin(EventBattleBegin evt)
        {
            lock (_lock)
            {
                Entries.Clear();
                StatusEntries.Clear();
                _currentRound = 0;
                _dotSourceCache.Clear();
                _pendingDotTicks.Clear();
                _statusHints.Clear();
                _dotProjectedHp.Clear();
                _statusTotals.Clear();
                _dirty = true;
                _statusDirty = true;
            }
        }

        public void OnBattleStartRound(EventBattleStartRound evt)
        {
            lock (_lock)
            {
                _currentRound = evt.m_Round;
                Entries.Add(new RoundHeader { Round = _currentRound });
                StatusEntries.Add(new RoundHeader { Round = _currentRound });
                _dirty = true;
                _statusDirty = true;
            }
        }

        public void OnSkillFinalizeResults(EventSkillFinalizeResults evt)
        {
            try
            {
                lock (_lock)
                {
                    uint pid = evt.PerformerGuid;
                    int pt = evt.m_PerformerTeamIndex;
                    string pName = ResolveName(pid);
                    bool pIsPlayer = (pt == 0);
                    string skillId = evt.SkillId ?? "";
                    var projectedHp = new Dictionary<uint, float>();

                    foreach (var ar in evt.ActorResults)
                    {
                        if (ar == null) continue;
                        // Use ar.m_PerformerActorGuid to support act-out results
                        uint arPid = ar.m_PerformerActorGuid;
                        string pNameResolved = (arPid == pid) ? pName : ResolveName(arPid);
                        bool pIsPlayerResolved = (arPid == pid) ? pIsPlayer : IsPlayerTeam(arPid);

                        string tName = ResolveName(ar.m_TargetActorGuid);
                        bool tIsPlayer = (ar.m_TargetTeamIndex == 0);

                        CacheStatusHints(ar, skillId);

                        if (ar.IsDamaging)
                        {
                            float rawDmg = ar.HealthDamage;
                            float dmg = GetEffectiveDamage(ar.m_TargetActorGuid, rawDmg, projectedHp);
                            float overkill = Mathf.Max(0f, rawDmg - dmg);
                            string actOutTag = ar.IsActOut ? " (ActOut)" : "";
                            string extra = JoinExtras(ar.IsRiposte ? "(Riposte)" : "", FormatOverkill(overkill));
                            Entries.Add(new LogEntry
                            {
                                Round = _currentRound, SourceName = pNameResolved + actOutTag, TargetName = tName,
                                SourceIsPlayer = pIsPlayerResolved, TargetIsPlayer = tIsPlayer,
                                ActionType = ar.IsCrit ? "CRIT" : "DMG", Value = dmg,
                                SkillId = skillId, Extra = extra, OverkillDamage = overkill
                            });
                            _dirty = true;
                        }

                        if (ar.IsHealthHeal && ar.HealthHeal > 0)
                        {
                            Entries.Add(new LogEntry
                            {
                                Round = _currentRound, SourceName = pNameResolved, TargetName = tName,
                                SourceIsPlayer = pIsPlayerResolved, TargetIsPlayer = tIsPlayer,
                                ActionType = "HEAL", Value = ar.HealthHeal,
                                SkillId = skillId, Extra = ar.IsHealthHealCrit ? "(CRIT)" : ""
                            });
                            _dirty = true;
                        }

                        if (ar.IsKill)
                        {
                            Entries.Add(new LogEntry
                            {
                                Round = _currentRound, SourceName = pNameResolved, TargetName = tName,
                                SourceIsPlayer = pIsPlayerResolved, TargetIsPlayer = tIsPlayer,
                                ActionType = "KILL", Value = 0,
                                SkillId = skillId, Extra = ""
                            });
                            _dirty = true;
                        }
                    }
                }
            }
            catch { }
        }

        private static float GetEffectiveDamage(uint targetGuid, float rawDamage, Dictionary<uint, float> projectedHp)
        {
            if (rawDamage <= 0f) return 0f;
            float hp;
            if (!projectedHp.TryGetValue(targetGuid, out hp))
            {
                hp = DamageTracker.TryResolveHpRawPublic(targetGuid, out var resolvedHp) ? Mathf.Max(0f, resolvedHp) : rawDamage;
            }

            float effective = Mathf.Min(rawDamage, Mathf.Max(0f, hp));
            projectedHp[targetGuid] = Mathf.Max(0f, hp - effective);
            return effective;
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

        private static string FormatOverkill(float overkill)
        {
            return overkill > 0.5f ? $"(OVK {overkill:F0})" : "";
        }

        private static string JoinExtras(string first, string second)
        {
            if (string.IsNullOrEmpty(first)) return second ?? "";
            if (string.IsNullOrEmpty(second)) return first;
            return first + " " + second;
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
                    string sourceName = sourceGuid != 0 ? ResolveName(sourceGuid) : "[Status]";
                    bool sourceIsPlayer = sourceGuid != 0 && IsPlayerTeam(sourceGuid);

                    foreach (var effect in output.EffectInstancesToApply)
                    {
                        if (effect?.EffectDefinition == null) continue;
                        string sourceId = !string.IsNullOrEmpty(effect.SourceId) ? effect.SourceId : fallbackSkillId;
                        var def = effect.EffectDefinition;

                        if (effect.TokenAddAmount > 0)
                        {
                            AddStatusHint(targetGuid, def.m_TokenAddId, "ADD", sourceGuid, sourceName, sourceIsPlayer, sourceId);
                            if (!string.IsNullOrEmpty(def.m_TokenAddTag))
                                AddStatusHint(targetGuid, null, "ADD", sourceGuid, sourceName, sourceIsPlayer, sourceId);
                        }

                        if (effect.TokenConvertAmount > 0 && !string.IsNullOrEmpty(def.m_TokenConvertToId))
                            AddStatusHint(targetGuid, def.m_TokenConvertToId, "ADD", sourceGuid, sourceName, sourceIsPlayer, sourceId);

                        if (effect.TokenCopyAmount > 0 && def.m_TokenCopyTags != null && def.m_TokenCopyTags.Count > 0)
                            AddStatusHint(targetGuid, null, "ADD", sourceGuid, sourceName, sourceIsPlayer, sourceId);

                        if (effect.TokenInvertAmount > 0 && def.m_TokenInvertIds != null && def.m_TokenInvertIds.Count > 0)
                            AddStatusHint(targetGuid, null, "ADD", sourceGuid, sourceName, sourceIsPlayer, sourceId);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"CacheStatusHints skipped: {ex.Message}");
            }
        }

        private void AddStatusHint(uint targetGuid, string effectId, string operation, uint sourceGuid, string sourceName, bool sourceIsPlayer, string sourceId)
        {
            if (targetGuid == 0 || sourceGuid == 0) return;
            _statusHints.Add(new StatusSourceHint
            {
                TargetGuid = targetGuid,
                EffectId = effectId ?? "",
                Operation = operation ?? "",
                SourceGuid = sourceGuid,
                SourceName = sourceName,
                SourceIsPlayer = sourceIsPlayer,
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

        public void OnTokenAdded(EventTokenAdded evt)
        {
            try
            {
                lock (_lock)
                {
                    var token = GetTokenDefinition(evt.m_TokenId);
                    if (!ShouldLogToken(token, evt.m_IsUiValid, evt.m_IsPopTextValid)) return;

                    var hint = ConsumeStatusHint(evt.m_ActorGuid, evt.m_TokenId, "ADD", evt.m_SourceId);
                    string sourceName = hint != null ? hint.SourceName : FormatSourceLabel(evt.m_SourceType);
                    bool sourceIsPlayer = hint != null && hint.SourceIsPlayer;
                    string skillId = hint != null && !string.IsNullOrEmpty(hint.SourceId) ? hint.SourceId : evt.m_SourceId;

                    AddStatusEntry(sourceName, sourceIsPlayer, evt.m_ActorGuid, GetStatusAction(token, "ADD"),
                        evt.m_TokenId, GetTokenName(evt.m_TokenId), GetTokenDetail(evt.m_TokenId), evt.m_AddAmount,
                        skillId, "ADD", token?.IsPositive == true, token?.IsNegative == true);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnTokenAdded error: {ex.Message}"); }
        }

        public void OnTokenRemoved(EventTokenRemoved evt)
        {
            try
            {
                lock (_lock)
                {
                    if (evt.Actor == null || evt.Token == null) return;
                    if (!ShouldLogToken(evt.Token, true, true)) return;
                    uint targetGuid = evt.Actor.ActorGuid;
                    string sourceName = evt.SourceActorGuid != 0 ? ResolveName(evt.SourceActorGuid) : FormatSourceLabel(evt.Source);
                    bool sourceIsPlayer = evt.SourceActorGuid != 0 && IsPlayerTeam(evt.SourceActorGuid);

                    AddStatusEntry(sourceName, sourceIsPlayer, targetGuid, GetStatusAction(evt.Token, "REMOVE"),
                        evt.Token.Id, GetTokenName(evt.Token.Id), GetTokenDetail(evt.Token.Id), 1, evt.SourceId,
                        "REMOVE", evt.Token.IsPositive, evt.Token.IsNegative);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnTokenRemoved error: {ex.Message}"); }
        }

        public void OnTokenConsumed(EventTokenConsumed evt)
        {
            try
            {
                lock (_lock)
                {
                    var token = GetTokenDefinition(evt.m_TokenId);
                    if (!ShouldLogToken(token, true, true)) return;

                    AddStatusEntry("[Consume]", false, evt.m_ActorGuid, GetStatusAction(token, "CONSUME"),
                        evt.m_TokenId, GetTokenName(evt.m_TokenId), GetTokenDetail(evt.m_TokenId), 1, "",
                        "CONSUME", token?.IsPositive == true, token?.IsNegative == true,
                        evt.m_TokenConsumeType.ToString());
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnTokenConsumed error: {ex.Message}"); }
        }

        public void OnTokenReplaced(EventTokenReplaced evt)
        {
            try
            {
                lock (_lock)
                {
                    var removed = GetTokenDefinition(evt.m_ReplaceRemoveTokenId);
                    var added = GetTokenDefinition(evt.m_ReplaceAddTokenId);
                    if (!ShouldLogToken(removed, evt.m_IsUiValid, evt.m_IsPopTextValid) &&
                        !ShouldLogToken(added, evt.m_IsUiValid, evt.m_IsPopTextValid)) return;

                    var hint = ConsumeStatusHint(evt.m_ActorGuid, evt.m_CauseTokenId, "ADD", evt.m_SourceId);
                    string sourceName = hint != null ? hint.SourceName : FormatSourceLabel(evt.m_SourceType);
                    bool sourceIsPlayer = hint != null && hint.SourceIsPlayer;
                    string skillId = hint != null && !string.IsNullOrEmpty(hint.SourceId) ? hint.SourceId : evt.m_SourceId;
                    string effectName = $"{GetTokenName(evt.m_ReplaceRemoveTokenId)} -> {GetTokenName(evt.m_ReplaceAddTokenId)}";
                    string detail = JoinDetails(GetTokenDetail(evt.m_ReplaceRemoveTokenId), GetTokenDetail(evt.m_ReplaceAddTokenId));

                    AddStatusEntry(sourceName, sourceIsPlayer, evt.m_ActorGuid, "TOKEN~",
                        evt.m_ReplaceAddTokenId, effectName, detail, 1, skillId, "REPLACE",
                        added?.IsPositive == true, added?.IsNegative == true);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnTokenReplaced error: {ex.Message}"); }
        }

        public void OnTokenNegated(EventTokenNegated evt)
        {
            try
            {
                lock (_lock)
                {
                    var negated = GetTokenDefinition(evt.m_NegatedTokenId);
                    var cause = GetTokenDefinition(evt.m_CauseTokenId);
                    if (!ShouldLogToken(negated, evt.m_IsUiValid, evt.m_IsPopTextValid) &&
                        !ShouldLogToken(cause, evt.m_IsUiValid, evt.m_IsPopTextValid)) return;

                    var hint = ConsumeStatusHint(evt.m_ActorGuid, evt.m_CauseTokenId, "ADD", evt.m_SourceId);
                    string sourceName = hint != null ? hint.SourceName : FormatSourceLabel(evt.m_SourceType);
                    bool sourceIsPlayer = hint != null && hint.SourceIsPlayer;
                    string skillId = hint != null && !string.IsNullOrEmpty(hint.SourceId) ? hint.SourceId : evt.m_SourceId;
                    string effectName = $"{GetTokenName(evt.m_CauseTokenId)} negates {GetTokenName(evt.m_NegatedTokenId)}";
                    string detail = GetTokenDetail(evt.m_NegatedTokenId);

                    AddStatusEntry(sourceName, sourceIsPlayer, evt.m_ActorGuid, "TOKENx",
                        evt.m_NegatedTokenId, effectName, detail, 1, skillId, "NEGATE",
                        negated?.IsPositive == true, negated?.IsNegative == true);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnTokenNegated error: {ex.Message}"); }
        }

        public void OnBuffAdded(EventBuffAdded evt)
        {
            try
            {
                lock (_lock)
                {
                    if (!ShouldLogBuff(evt.Buff)) return;
                    string sourceName = evt.PerformerActorGuid != 0 ? ResolveName(evt.PerformerActorGuid) : FormatSourceLabel(evt.SourceType);
                    bool sourceIsPlayer = evt.PerformerActorGuid != 0 && IsPlayerTeam(evt.PerformerActorGuid);
                    bool isPositive = evt.Buff.IsEligibleToShowAsBuffCombatUi || evt.Buff.IsEligibleToShowAsBuffPopText;
                    bool isNegative = evt.Buff.IsEligibleToShowAsDebuffCombatUi || evt.Buff.IsEligibleToShowAsDebuffPopText;

                    AddStatusEntry(sourceName, sourceIsPlayer, evt.TargetActorGuid, GetBuffAction(isPositive, isNegative, "ADD"),
                        evt.Buff.Id, PrettyId(evt.Buff.Id), GetBuffDetail(evt.Buff), 1, evt.SourceId, "ADD", isPositive, isNegative);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnBuffAdded error: {ex.Message}"); }
        }

        public void OnBuffRemoved(EventBuffRemoved evt)
        {
            try
            {
                lock (_lock)
                {
                    if (!ShouldLogBuff(evt.Buff)) return;
                    string sourceName = evt.SourceActorGuid != 0 ? ResolveName(evt.SourceActorGuid) : FormatSourceLabel(evt.Source);
                    bool sourceIsPlayer = evt.SourceActorGuid != 0 && IsPlayerTeam(evt.SourceActorGuid);
                    bool isPositive = evt.Buff.IsEligibleToShowAsBuffCombatUi || evt.Buff.IsEligibleToShowAsBuffPopText;
                    bool isNegative = evt.Buff.IsEligibleToShowAsDebuffCombatUi || evt.Buff.IsEligibleToShowAsDebuffPopText;

                    AddStatusEntry(sourceName, sourceIsPlayer, evt.ActorGuid, GetBuffAction(isPositive, isNegative, "REMOVE"),
                        evt.Buff.Id, PrettyId(evt.Buff.Id), GetBuffDetail(evt.Buff), 1, evt.SourceId, "REMOVE", isPositive, isNegative);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnBuffRemoved error: {ex.Message}"); }
        }

        private void AddStatusEntry(string sourceName, bool sourceIsPlayer, uint targetGuid, string actionType,
            string effectId, string effectName, string effectDetail, int amount, string skillId, string operation,
            bool isPositive, bool isNegative, string operationDetail = null)
        {
            bool targetIsPlayer = IsPlayerTeam(targetGuid);
            string extra = BuildStatusExtra(effectName, effectDetail, amount, operationDetail);
            StatusEntries.Add(new LogEntry
            {
                Round = _currentRound,
                SourceName = sourceName,
                TargetName = ResolveName(targetGuid),
                SourceIsPlayer = sourceIsPlayer,
                TargetIsPlayer = targetIsPlayer,
                ActionType = actionType,
                Value = amount,
                SkillId = skillId ?? "",
                Extra = extra,
                EffectId = effectId ?? "",
                EffectName = effectName ?? "",
                EffectDetail = effectDetail ?? "",
                EffectAmount = amount
            });
            UpdateStatusTotals(targetIsPlayer, isPositive, isNegative, operation);
            _statusDirty = true;
        }

        private void UpdateStatusTotals(bool targetIsPlayer, bool isPositive, bool isNegative, string operation)
        {
            if (string.Equals(operation, "ADD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(operation, "REPLACE", StringComparison.OrdinalIgnoreCase))
            {
                if (isPositive)
                {
                    if (targetIsPlayer) _statusTotals.PlayerBuffApplied++;
                    else _statusTotals.EnemyBuffApplied++;
                }
                else if (isNegative)
                {
                    if (targetIsPlayer) _statusTotals.PlayerDebuffApplied++;
                    else _statusTotals.EnemyDebuffApplied++;
                }
            }
            else if (string.Equals(operation, "CONSUME", StringComparison.OrdinalIgnoreCase))
            {
                if (targetIsPlayer) _statusTotals.PlayerStatusConsumed++;
                else _statusTotals.EnemyStatusConsumed++;
            }
            else
            {
                if (targetIsPlayer) _statusTotals.PlayerStatusRemoved++;
                else _statusTotals.EnemyStatusRemoved++;
            }
        }

        private static string BuildStatusExtra(string effectName, string detail, int amount, string operationDetail)
        {
            string name = string.IsNullOrEmpty(effectName) ? "Status" : effectName;
            if (amount > 1) name += $" x{amount}";
            if (!string.IsNullOrEmpty(operationDetail)) name += $" ({operationDetail})";
            if (!string.IsNullOrEmpty(detail)) name += $" - {detail}";
            return name;
        }

        private static string GetStatusAction(TokenDefinition token, string operation)
        {
            bool positive = token?.IsPositive == true;
            bool negative = token?.IsNegative == true;
            if (string.Equals(operation, "ADD", StringComparison.OrdinalIgnoreCase))
            {
                if (positive) return "BUFF+";
                if (negative) return "DEBUFF+";
                return "TOKEN+";
            }
            if (string.Equals(operation, "CONSUME", StringComparison.OrdinalIgnoreCase))
            {
                if (positive) return "BUFF!";
                if (negative) return "DEBUFF!";
                return "TOKEN!";
            }

            if (positive) return "BUFF-";
            if (negative) return "DEBUFF-";
            return "TOKEN-";
        }

        private static string GetBuffAction(bool isPositive, bool isNegative, string operation)
        {
            if (string.Equals(operation, "ADD", StringComparison.OrdinalIgnoreCase))
            {
                if (isPositive) return "BUFF+";
                if (isNegative) return "DEBUFF+";
                return "STATUS+";
            }
            if (isPositive) return "BUFF-";
            if (isNegative) return "DEBUFF-";
            return "STATUS-";
        }

        private static bool ShouldLogToken(TokenDefinition token, bool isUiValid, bool isPopTextValid)
        {
            if (!isUiValid && !isPopTextValid) return false;
            if (token == null) return true;
            if (token.IsHidden) return false;
            return token.ShowName || token.ShowDescription || token.IsPositive || token.IsNegative;
        }

        private static bool ShouldLogBuff(BuffDefinition buff)
        {
            if (buff == null) return false;
            return buff.IsEligibleToShowAsCombatUi || buff.IsEligibleToShowAsPopText;
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

        private static string GetTokenName(string tokenId)
        {
            if (string.IsNullOrEmpty(tokenId)) return "Token";
            try
            {
                return CleanText(TokenDescription.GetNameString(tokenId), PrettyId(tokenId));
            }
            catch { return PrettyId(tokenId); }
        }

        private static string GetTokenDetail(string tokenId)
        {
            if (string.IsNullOrEmpty(tokenId)) return "";
            try
            {
                return CleanText(TokenDescription.GetDetailString(tokenId), "");
            }
            catch { return ""; }
        }

        private static string GetBuffDetail(BuffDefinition buff)
        {
            if (buff == null) return "";
            try
            {
                return CleanText(buff.GetContentDescription(), "");
            }
            catch { return ""; }
        }

        private static string FormatSourceLabel(SourceType sourceType)
        {
            string source = sourceType != null ? sourceType.GetName() : "status";
            return $"[{PrettyId(source)}]";
        }

        private static string PrettyId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string text = id.Replace('_', ' ').Replace('-', ' ');
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        private static readonly Regex RichTextRegex = new Regex("<.*?>", RegexOptions.Compiled);

        private static string CleanText(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback ?? "";
            string text = RichTextRegex.Replace(value, "");
            text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            text = text.Trim();
            if (string.IsNullOrEmpty(text)) return fallback ?? "";
            if (!string.IsNullOrEmpty(fallback) && text.Contains("_") && text.Contains(fallback.Replace(" ", "_")))
                return fallback;
            return text;
        }

        private static string JoinDetails(string first, string second)
        {
            if (string.IsNullOrEmpty(first)) return second ?? "";
            if (string.IsNullOrEmpty(second)) return first;
            if (first == second) return first;
            return first + " | " + second;
        }

        public void OnDotAdded(EventDotAdded evt)
        {
            try
            {
                lock (_lock)
                {
                    if (evt.m_Actor == null || evt.m_DotDefinition == null) return;
                    uint targetGuid = evt.m_Actor.ActorGuid;
                    string dotDefId = evt.m_DotDefinition.m_Id ?? "";
                    string dotType = evt.m_DotDefinition.m_Type ?? "";
                    
                    // We'll resolve performer from EventSkillFinalizeResults or keep source info
                    var info = new DotSourceInfo
                    {
                        DotType = ToDisplayName(dotType),
                        DotDefId = dotDefId,
                        SourceType = evt.m_SourceType?.ToString() ?? "",
                        SourceId = evt.m_SourceId ?? "",
                        PerformerGuids = null // Will be filled from SkillFinalizeResults
                    };
                    
                    _dotSourceCache[(targetGuid, dotDefId)] = info;
                    Plugin.Log.LogDebug($"DotAdded: target={targetGuid}, defId={dotDefId}, type={dotType}, source={info.SourceId}");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnDotAdded error: {ex.Message}"); }
        }

        public void OnDotApplied(EventDotApplied evt)
        {
            try
            {
                lock (_lock)
                {
                    EnsureReflection();
                    uint targetGuid = evt.m_actorGuid;
                    string dotType = evt.m_dotType ?? "unknown";
                    var result = evt.m_effectApplyCombinedResult;
                    
                    if (result == null) return;
                    
                    var (healthChange, performerGuids, sourceIds) = ExtractFromCombinedResult(result);
                    
                    float rawAmount = Math.Abs(healthChange);
                    
                    // Try to find cached source info for this dot
                    string displayDotType = ToDisplayName(dotType);
                    uint bestPerformerGuid = 0;
                    string sourceName = "[DOT]";
                    bool sourceIsPlayer = false;
                    string skillId = "";
                    
                    // Search cache for matching dot type on this target
                    foreach (var kvp in _dotSourceCache)
                    {
                        if (kvp.Key.target == targetGuid && 
                            kvp.Value.DotType.Equals(displayDotType, StringComparison.OrdinalIgnoreCase))
                        {
                            skillId = kvp.Value.SourceId;
                            break;
                        }
                    }
                    
                    // Get performer from EffectApplyCombinedResult (via reflection)
                    if (performerGuids.Count > 0)
                    {
                        bestPerformerGuid = performerGuids[0];
                        sourceName = ResolveName(bestPerformerGuid);
                        // Determine if performer is player (team index 0)
                        sourceIsPlayer = IsPlayerTeam(bestPerformerGuid);
                    }
                    
                    // Store in pending ticks for HealthDamage handler
                    var resolved = new DotResolvedInfo
                    {
                        DotType = displayDotType,
                        PerformerGuid = bestPerformerGuid,
                        SourceName = sourceName,
                        SourceIsPlayer = sourceIsPlayer,
                        SkillId = skillId,
                        Value = rawAmount
                    };
                    _pendingDotTicks[(targetGuid, dotType)] = resolved;
                    
                    // Log both DOT damage and HoT heal
                    bool isDamage = result.HealthChange < -0.01f;
                    bool isHeal = result.HealthChange > 0.01f;
                    if (rawAmount > 0.01f)
                    {
                        float displayAmount = rawAmount;
                        float overkill = 0f;
                        if (isDamage)
                        {
                            displayAmount = GetEffectiveDotDamage(targetGuid, rawAmount);
                            overkill = Mathf.Max(0f, rawAmount - displayAmount);
                        }
                        string tName = ResolveName(targetGuid);
                        bool tIsPlayer = (evt.m_effectApplyCombinedResult?.HealthChange < 0) ? false : IsPlayerTeam(targetGuid);
                        // HealthChange < 0 means damage dealt TO target, so target receives it
                        tIsPlayer = IsPlayerTeam(targetGuid);
                        
                        string tName2 = ResolveName(targetGuid);
                        bool tIsPlayer2 = IsPlayerTeam(targetGuid);
                        Entries.Add(new LogEntry
                        {
                            Round = _currentRound, 
                            SourceName = sourceName, 
                            TargetName = tName2,
                            SourceIsPlayer = sourceIsPlayer, 
                            TargetIsPlayer = tIsPlayer2,
                            ActionType = isDamage ? "DOT" : "HEAL", 
                            Value = displayAmount,
                            SkillId = skillId, 
                            Extra = JoinExtras(isHeal ? "(HoT)" : "", FormatOverkill(overkill)),
                            DotType = isDamage ? displayDotType : "",
                            OverkillDamage = overkill
                        });
                        _dirty = true;
                        
                        Plugin.Log.LogDebug($"DotApplied: {sourceName} -> {tName}: {displayDotType} {displayAmount:F1} HP (skill={skillId}, ovk={overkill:F1})");
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnDotApplied error: {ex.Message}"); }
        }

        public void OnHealthDamage(EventActorHealthDamage evt)
        {
            try
            {
                lock (_lock)
                {
                    string srcName = evt.m_SourceType?.ToString() ?? "";
                    if (srcName.ToLowerInvariant().Contains("dot"))
                    {
                        // DOT HP damage is already logged by OnDotApplied with full type/performer info
                        // No need to add duplicate entry here
                        return;
                    }
                }
            }
            catch { }
        }

        private static bool IsPlayerTeam(uint guid)
        {
            try
            {
                if (!_libraryReflectionInit) { _libraryReflectionInit = true; InitLibraryTeamReflection(); }
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
                // Reuse DamageTracker's cached reflection if available
                // Otherwise do our own scan (lightweight)
                Type genericLibDef = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { genericLibDef = asm.GetType("Assets.Code.Library.Library`2"); if (genericLibDef != null) break; } catch { }
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
                                    if (ip != null) { _teamLibraryInstance = ip.GetValue(null); }
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

        public void OnStressDamage(EventStressDamage evt)
        {
            try
            {
                lock (_lock)
                {
                    string tName = ResolveName(evt.m_ActorGuid);
                    bool tIsPlayer = (evt.m_TeamIndex == 0);
                    Entries.Add(new LogEntry
                    {
                        Round = _currentRound, SourceName = "[Stress]", TargetName = tName,
                        SourceIsPlayer = false, TargetIsPlayer = tIsPlayer,
                        ActionType = "STRESS", Value = evt.m_StressDamageAmount,
                        SkillId = "", Extra = ""
                    });
                    _dirty = true;
                }
            }
            catch { }
        }

        public void OnActorDeath(EventActorDeath evt)
        {
            try
            {
                lock (_lock)
                {
                    string tName = ResolveName(evt.m_DyingActorGuid);
                    bool tIsPlayer = (evt.m_DyingActorTeamIndex == 0);
                    Entries.Add(new LogEntry
                    {
                        Round = _currentRound, SourceName = "", TargetName = tName,
                        SourceIsPlayer = false, TargetIsPlayer = tIsPlayer,
                        ActionType = "DEATH", Value = 0,
                        SkillId = "", Extra = evt.m_DeathType?.ToString() ?? ""
                    });
                    _dirty = true;
                }
            }
            catch { }
        }

        private static string ResolveName(uint guid)
        {
            return DamageTracker.TryResolveNamePublic(guid) ?? $"#{guid}";
        }
    }
}
