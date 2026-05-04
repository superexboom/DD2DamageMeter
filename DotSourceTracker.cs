using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Dot;
using Assets.Code.Dot.Events;
using Assets.Code.Effect;
using Assets.Code.Source;
using UnityEngine;

namespace DD2DamageMeter
{
    internal sealed class DotSourceTracker
    {
        internal sealed class Share
        {
            public uint SourceActorGuid;
            public string SourceId;
            public float RawAmount;
            public float EffectiveAmount;
            public float OverkillAmount;
        }

        internal delegate uint SourceOverride(ActorInstance targetActor, string dotId, string dotType, uint currentSourceActorGuid, SourceType sourceType, string sourceId);

        private sealed class ActiveDot
        {
            public uint TargetGuid;
            public uint SourceActorGuid;
            public string DotId;
            public string DotType;
            public SourceType SourceType;
            public string SourceId;
            public int Count;
            public float Weight;
        }

        private readonly List<ActiveDot> _activeDots = new List<ActiveDot>();
        private readonly List<ActiveDot> _expiredDots = new List<ActiveDot>();

        public void Reset()
        {
            _activeDots.Clear();
            _expiredDots.Clear();
        }

        public void OnDotAdded(EventDotAdded evt, SourceOverride sourceOverride = null)
        {
            if (evt?.m_Actor == null || evt.m_DotDefinition == null) return;

            uint targetGuid = evt.m_Actor.ActorGuid;
            DotInstance instance = FindNewestDotInstance(evt.m_Actor, evt.m_DotDefinition, evt.m_SourceType, evt.m_SourceId);
            uint sourceActorGuid = instance != null ? instance.SourceActorGuid : 0;
            SourceType sourceType = instance != null ? instance.SourceType : evt.m_SourceType;
            string sourceId = instance != null ? instance.SourceId : evt.m_SourceId;
            string dotId = evt.m_DotDefinition.m_Id ?? "";
            string dotType = evt.m_DotDefinition.m_Type ?? "";

            uint overrideGuid = sourceOverride != null
                ? sourceOverride(evt.m_Actor, dotId, dotType, sourceActorGuid, sourceType, sourceId)
                : 0;
            if (overrideGuid != 0)
                sourceActorGuid = overrideGuid;

            AddDot(_activeDots, new ActiveDot
            {
                TargetGuid = targetGuid,
                SourceActorGuid = sourceActorGuid,
                DotId = dotId,
                DotType = dotType,
                SourceType = sourceType,
                SourceId = sourceId ?? "",
                Count = 1,
                Weight = EstimateTickWeight(instance, evt.m_Actor)
            });
        }

        public void OnDotRemoved(EventDotRemoved evt)
        {
            if (evt?.Actor == null || evt.Dot == null) return;

            ActiveDot removed = FindRemovedActiveDot(evt.Actor, evt.Dot) ?? new ActiveDot
            {
                TargetGuid = evt.Actor.ActorGuid,
                DotId = evt.Dot.m_Id ?? "",
                DotType = evt.Dot.m_Type ?? "",
                Count = 1,
                Weight = 1f
            };

            if (IsSourceType(evt.Source, "duration"))
                AddDot(_expiredDots, removed);
            else
                RemoveActiveDot(removed);
        }

        public List<Share> GetShares(uint targetGuid, string dotType, EffectApplyCombinedResult result, float rawAmount, float effectiveAmount)
        {
            var matching = new List<ActiveDot>();
            for (int i = 0; i < _activeDots.Count; i++)
            {
                ActiveDot dot = _activeDots[i];
                if (dot.TargetGuid != targetGuid) continue;
                if (!DotTypeMatches(dot.DotType, dotType)) continue;
                matching.Add(dot);
            }

            if (matching.Count == 0 || !HasUsableSource(matching))
                return BuildFallbackShares(result, rawAmount, effectiveAmount);

            var grouped = new Dictionary<uint, ShareAccumulator>();
            float totalWeight = 0f;
            for (int i = 0; i < matching.Count; i++)
            {
                ActiveDot dot = matching[i];
                float weight = Mathf.Max(0.0001f, dot.Weight);
                totalWeight += weight;

                if (!grouped.TryGetValue(dot.SourceActorGuid, out var acc))
                {
                    acc = new ShareAccumulator { SourceActorGuid = dot.SourceActorGuid, SourceId = dot.SourceId ?? "" };
                    grouped[dot.SourceActorGuid] = acc;
                }

                if (string.IsNullOrEmpty(acc.SourceId) && !string.IsNullOrEmpty(dot.SourceId))
                    acc.SourceId = dot.SourceId;
                acc.Weight += weight;
            }

            return BuildShares(grouped.Values, totalWeight, rawAmount, effectiveAmount);
        }

        public void ApplyExpired(uint targetGuid, string dotType)
        {
            for (int i = _expiredDots.Count - 1; i >= 0; i--)
            {
                ActiveDot expired = _expiredDots[i];
                if (expired.TargetGuid != targetGuid) continue;
                if (!DotTypeMatches(expired.DotType, dotType)) continue;
                RemoveActiveDot(expired);
                _expiredDots.RemoveAt(i);
            }
        }

        private static List<Share> BuildFallbackShares(EffectApplyCombinedResult result, float rawAmount, float effectiveAmount)
        {
            var performerGuids = ExtractPerformerGuids(result);
            var grouped = new Dictionary<uint, ShareAccumulator>();
            if (performerGuids.Count == 0)
            {
                grouped[0] = new ShareAccumulator { SourceActorGuid = 0, Weight = 1f };
            }
            else
            {
                for (int i = 0; i < performerGuids.Count; i++)
                {
                    uint guid = performerGuids[i];
                    if (!grouped.ContainsKey(guid))
                        grouped[guid] = new ShareAccumulator { SourceActorGuid = guid, Weight = 1f };
                }
            }

            return BuildShares(grouped.Values, grouped.Count > 0 ? grouped.Count : 1f, rawAmount, effectiveAmount);
        }

        private static List<Share> BuildShares(IEnumerable<ShareAccumulator> accumulators, float totalWeight, float rawAmount, float effectiveAmount)
        {
            var shares = new List<Share>();
            if (totalWeight <= 0f) totalWeight = 1f;
            float overkillAmount = Mathf.Max(0f, rawAmount - effectiveAmount);

            foreach (var acc in accumulators)
            {
                float ratio = Mathf.Max(0f, acc.Weight) / totalWeight;
                shares.Add(new Share
                {
                    SourceActorGuid = acc.SourceActorGuid,
                    SourceId = acc.SourceId ?? "",
                    RawAmount = rawAmount * ratio,
                    EffectiveAmount = effectiveAmount * ratio,
                    OverkillAmount = overkillAmount * ratio
                });
            }

            return shares;
        }

        private sealed class ShareAccumulator
        {
            public uint SourceActorGuid;
            public string SourceId;
            public float Weight;
        }

        private static bool HasUsableSource(List<ActiveDot> dots)
        {
            for (int i = 0; i < dots.Count; i++)
            {
                if (dots[i].SourceActorGuid != 0) return true;
            }
            return false;
        }

        private ActiveDot FindRemovedActiveDot(ActorInstance actor, DotDefinition dot)
        {
            if (actor == null || dot == null) return null;
            uint targetGuid = actor.ActorGuid;
            for (int i = 0; i < _activeDots.Count; i++)
            {
                ActiveDot active = _activeDots[i];
                if (active.TargetGuid != targetGuid) continue;
                if (!DotIdOrTypeMatches(active, dot.m_Id ?? "", dot.m_Type ?? "")) continue;

                int currentCount = CountCurrentInstances(actor, active);
                int alreadyExpired = CountEquivalent(_expiredDots, active);
                if (currentCount + alreadyExpired < active.Count)
                    return CloneOne(active);
            }
            return null;
        }

        private static ActiveDot CloneOne(ActiveDot dot)
        {
            int count = Math.Max(1, dot.Count);
            return new ActiveDot
            {
                TargetGuid = dot.TargetGuid,
                SourceActorGuid = dot.SourceActorGuid,
                DotId = dot.DotId,
                DotType = dot.DotType,
                SourceType = dot.SourceType,
                SourceId = dot.SourceId,
                Count = 1,
                Weight = Mathf.Max(0.0001f, dot.Weight / count)
            };
        }

        private static int CountEquivalent(List<ActiveDot> dots, ActiveDot candidate)
        {
            int count = 0;
            for (int i = 0; i < dots.Count; i++)
            {
                ActiveDot dot = dots[i];
                if (Equivalent(dot, candidate))
                    count += dot.Count;
            }
            return count;
        }

        private static int CountCurrentInstances(ActorInstance actor, ActiveDot active)
        {
            try
            {
                var instances = actor.DotContainer.GetInstances(dot => DotInstanceMatches(dot, active, true));
                return instances?.Count ?? 0;
            }
            catch { return 0; }
        }

        private static DotInstance FindNewestDotInstance(ActorInstance actor, DotDefinition definition, SourceType sourceType, string sourceId)
        {
            try
            {
                var instances = actor.DotContainer.GetInstances(dot =>
                    dot != null &&
                    dot.Definition != null &&
                    DotDefinitionMatches(dot.Definition, definition) &&
                    SourceTypeMatches(dot.SourceType, sourceType) &&
                    SourceIdMatches(dot.SourceId, sourceId));

                if (instances != null && instances.Count > 0)
                    return instances[instances.Count - 1];

                instances = actor.DotContainer.GetInstances(dot =>
                    dot != null &&
                    dot.Definition != null &&
                    DotDefinitionMatches(dot.Definition, definition) &&
                    dot.SourceActorGuid != 0);

                if (instances != null && instances.Count > 0)
                    return instances[instances.Count - 1];
            }
            catch { }
            return null;
        }

        private static void AddDot(List<ActiveDot> dots, ActiveDot dot)
        {
            if (dot == null || dot.Count <= 0) return;
            dot.Weight = Mathf.Max(0.0001f, dot.Weight);

            for (int i = 0; i < dots.Count; i++)
            {
                if (!Equivalent(dots[i], dot)) continue;
                dots[i].Count += dot.Count;
                dots[i].Weight += dot.Weight;
                return;
            }

            dots.Add(dot);
        }

        private void RemoveActiveDot(ActiveDot filter)
        {
            int remaining = Math.Max(1, filter.Count);
            for (int i = _activeDots.Count - 1; i >= 0 && remaining > 0; i--)
            {
                ActiveDot active = _activeDots[i];
                if (!ActiveDotMatches(active, filter)) continue;

                int used = Math.Min(active.Count, remaining);
                float weightUsed = active.Count > 0 ? active.Weight * used / active.Count : active.Weight;
                active.Count -= used;
                active.Weight = Mathf.Max(0f, active.Weight - weightUsed);
                remaining -= used;

                if (active.Count <= 0)
                    _activeDots.RemoveAt(i);
            }
        }

        private static bool ActiveDotMatches(ActiveDot active, ActiveDot filter)
        {
            if (active.TargetGuid != filter.TargetGuid) return false;
            if (!DotIdOrTypeMatches(active, filter.DotId, filter.DotType)) return false;

            bool hasSpecificSource = filter.SourceActorGuid != 0 ||
                                     filter.SourceType != null ||
                                     !string.IsNullOrEmpty(filter.SourceId);
            if (!hasSpecificSource) return true;
            return Equivalent(active, filter);
        }

        private static bool Equivalent(ActiveDot left, ActiveDot right)
        {
            return left.TargetGuid == right.TargetGuid &&
                   left.SourceActorGuid == right.SourceActorGuid &&
                   DotIdOrTypeMatches(left, right.DotId, right.DotType) &&
                   SourceTypeMatches(left.SourceType, right.SourceType) &&
                   string.Equals(left.SourceId ?? "", right.SourceId ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static bool DotInstanceMatches(DotInstance dot, ActiveDot active, bool exactSource)
        {
            if (dot == null || dot.Definition == null) return false;
            if (!DotDefinitionIdOrTypeMatches(dot.Definition, active.DotId, active.DotType)) return false;
            if (!exactSource) return true;
            return dot.SourceActorGuid == active.SourceActorGuid &&
                   SourceTypeMatches(dot.SourceType, active.SourceType) &&
                   string.Equals(dot.SourceId ?? "", active.SourceId ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static bool DotDefinitionMatches(DotDefinition left, DotDefinition right)
        {
            if (left == null || right == null) return false;
            return string.Equals(left.m_Id ?? "", right.m_Id ?? "", StringComparison.OrdinalIgnoreCase) ||
                   DotTypeMatches(left.m_Type, right.m_Type);
        }

        private static bool DotDefinitionIdOrTypeMatches(DotDefinition dot, string dotId, string dotType)
        {
            if (dot == null) return false;
            return (!string.IsNullOrEmpty(dot.m_Id) &&
                    !string.IsNullOrEmpty(dotId) &&
                    string.Equals(dot.m_Id, dotId, StringComparison.OrdinalIgnoreCase)) ||
                   DotTypeMatches(dot.m_Type, dotType);
        }

        private static bool DotIdOrTypeMatches(ActiveDot dot, string dotId, string dotType)
        {
            return (!string.IsNullOrEmpty(dot.DotId) &&
                    !string.IsNullOrEmpty(dotId) &&
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
            return string.Equals(SourceTypeName(left), SourceTypeName(right), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSourceType(SourceType sourceType, string expected)
        {
            if (sourceType == null) return false;
            return string.Equals(SourceTypeName(sourceType), expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string SourceTypeName(SourceType sourceType)
        {
            if (sourceType == null) return "";
            try { return sourceType.GetName(); }
            catch { return sourceType.ToString(); }
        }

        private static bool SourceIdMatches(string left, string right)
        {
            return string.IsNullOrEmpty(left) ||
                   string.IsNullOrEmpty(right) ||
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static float EstimateTickWeight(DotInstance dot, ActorInstance targetActor)
        {
            if (dot?.Definition?.m_Effects == null) return 1f;
            float total = 0f;
            for (int i = 0; i < dot.Definition.m_Effects.Count; i++)
            {
                EffectDefinition effect = dot.Definition.m_Effects[i];
                if (effect == null) continue;
                total += EstimateHealthAmount(effect, dot.m_EffectValueChange, dot.m_EffectValueMultiplier, targetActor);
            }
            return Mathf.Max(0.0001f, total);
        }

        private static float EstimateHealthAmount(EffectDefinition effect, float valueChange, float valueMultiplier, ActorInstance targetActor)
        {
            float amount = 0f;
            amount += ApplyValue(effect.m_HealthDamageAmount, effect.m_HealthDamageAmountRange, valueChange, valueMultiplier);
            amount += ApplyValue(effect.m_HealthHealAmount, effect.m_HealthHealAmountRange, valueChange, valueMultiplier);

            try
            {
                if (targetActor != null)
                {
                    float hpMax = targetActor.GetHpMax(effect.m_IncludeWoundInMaxHp, false);
                    amount += hpMax * ApplyValue(effect.m_HealthDamagePercent, effect.m_HealthDamagePercentRange, valueChange, valueMultiplier);
                    amount += hpMax * ApplyValue(effect.m_HealthHealPercent, effect.m_HealthHealPercentRange, valueChange, valueMultiplier);
                }
            }
            catch { }

            return Mathf.Max(0f, amount);
        }

        private static float ApplyValue(float amount, float range, float valueChange, float valueMultiplier)
        {
            float maxAmount = amount + Mathf.Max(0f, range);
            if (maxAmount <= 0f) return 0f;
            return Mathf.Max(0f, (maxAmount + valueChange) * valueMultiplier);
        }

        private static List<uint> ExtractPerformerGuids(EffectApplyCombinedResult result)
        {
            var performerGuids = new List<uint>();
            try
            {
                EnsureReflection();
                if (_changeAmountsField == null || _performerGuidsField == null) return performerGuids;
                var changeAmounts = _changeAmountsField.GetValue(result) as IDictionary;
                if (changeAmounts == null) return performerGuids;

                foreach (var entry in changeAmounts)
                {
                    var valueProp = entry.GetType().GetProperty("Value");
                    if (valueProp == null) continue;
                    var changeAmount = valueProp.GetValue(entry);
                    if (changeAmount == null) continue;

                    var guids = _performerGuidsField.GetValue(changeAmount) as IList;
                    if (guids == null) continue;
                    foreach (var item in guids)
                    {
                        if (item is uint guid && !performerGuids.Contains(guid))
                            performerGuids.Add(guid);
                    }
                }
            }
            catch { }
            return performerGuids;
        }

        private static void EnsureReflection()
        {
            if (_reflectionInit) return;
            _reflectionInit = true;
            var resultType = typeof(EffectApplyCombinedResult);
            _changeAmountsField = resultType.GetField("m_ChangeAmounts", BindingFlags.NonPublic | BindingFlags.Instance);
            var changeAmountType = resultType.GetNestedType("ChangeAmount", BindingFlags.NonPublic);
            if (changeAmountType == null) return;
            _performerGuidsField = changeAmountType.GetField("m_PerformerActorGuids", BindingFlags.Public | BindingFlags.Instance);
        }

        private static FieldInfo _changeAmountsField;
        private static FieldInfo _performerGuidsField;
        private static bool _reflectionInit;
    }
}
