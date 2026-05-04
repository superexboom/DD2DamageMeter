using System;
using System.Collections.Generic;
using Assets.Code.Actor;
using Assets.Code.Actor.ActorContainer;
using Assets.Code.Dot;
using Assets.Code.Dot.Events;
using Assets.Code.Effect;
using Assets.Code.Library;
using Assets.Code.Skill.Events;
using Assets.Code.Source;
using Assets.Code.Utils;

namespace DD2DamageMeter
{
    internal sealed class FloorDotSourceTracker
    {
        private sealed class FloorDotSource
        {
            public int TeamIndex;
            public int TeamPosition;
            public uint ProviderGuid;
            public string SkillId;
            public string SourceId;
            public string FloorEffectId;
            public string FloorDotId;
        }

        private readonly List<FloorDotSource> _sources = new List<FloorDotSource>();

        public void Reset()
        {
            _sources.Clear();
        }

        public void OnSkillFinalizeResults(EventSkillFinalizeResults evt, Func<uint, bool> isPlayerTeam)
        {
            if (evt == null || evt.ActorResults == null || isPlayerTeam == null) return;

            string skillId = evt.SkillId ?? "";
            foreach (var ar in evt.ActorResults)
            {
                if (ar?.m_AppliedEffectsOutputContainer == null) continue;

                foreach (var output in ar.m_AppliedEffectsOutputContainer.Outputs)
                {
                    if (output == null || output.m_TargetActor == null) continue;
                    var targetActor = output.m_TargetActor;
                    if (targetActor.TeamIndex == 0) continue;

                    uint providerGuid = output.m_PerformerActor != null
                        ? output.m_PerformerActor.ActorGuid
                        : ar.m_PerformerActorGuid;
                    if (providerGuid == 0 || !isPlayerTeam(providerGuid)) continue;

                    foreach (var effect in output.EffectInstancesToApply)
                    {
                        var def = effect?.EffectDefinition;
                        if (def == null || !def.m_IsLockedTeamPosition) continue;

                        string sourceId = !string.IsNullOrEmpty(effect.SourceId) ? effect.SourceId : skillId;
                        bool controlledBurn = IsControlledBurnId(skillId) ||
                                              IsControlledBurnId(sourceId) ||
                                              IsControlledBurnId(def.m_Id);
                        bool burnFloor = IsBurnDot(def.m_DotAddId);
                        if (!controlledBurn && !burnFloor) continue;

                        AddOrUpdate(targetActor.TeamIndex, targetActor.TeamPosition, providerGuid, skillId, sourceId, def.m_Id, def.m_DotAddId);
                    }
                }
            }
        }

        public void OnDotAdded(EventDotAdded evt, Func<uint, bool> isPlayerTeam)
        {
            if (evt?.m_Actor == null || evt.m_DotDefinition == null || isPlayerTeam == null) return;

            try
            {
                var instances = evt.m_Actor.DotContainer.GetInstances(dot =>
                    dot != null &&
                    dot.Definition != null &&
                    DotDefinitionMatches(dot.Definition, evt.m_DotDefinition) &&
                    dot.IsLockedTeamPosition);

                if (instances == null || instances.Count == 0) return;
                var instance = instances[instances.Count - 1];
                if (instance.SourceActorGuid == 0 || !isPlayerTeam(instance.SourceActorGuid)) return;

                string sourceId = instance.SourceId ?? evt.m_SourceId ?? "";
                if (!IsControlledBurnId(sourceId) && !IsControlledBurnId(instance.Definition?.Id) && !IsBurnDot(instance.Definition?.m_Id))
                    return;

                AddOrUpdate(evt.m_Actor.TeamIndex, evt.m_Actor.TeamPosition, instance.SourceActorGuid, sourceId, sourceId, instance.Definition?.Id, instance.Definition?.m_Id);
            }
            catch { }
        }

        public uint ResolveDotSource(ActorInstance targetActor, string dotId, string dotType, uint currentSourceActorGuid, SourceType sourceType, string sourceId, Func<uint, bool> isPlayerTeam)
        {
            if (targetActor == null || isPlayerTeam == null) return 0;
            if (!IsBurnDot(dotId, dotType)) return 0;
            if (!LooksLikeFloorTickSource(sourceType, sourceId)) return 0;

            uint targetGuid = targetActor.ActorGuid;
            if (currentSourceActorGuid != 0 && currentSourceActorGuid != targetGuid && isPlayerTeam(currentSourceActorGuid))
                return 0;

            for (int i = _sources.Count - 1; i >= 0; i--)
            {
                var source = _sources[i];
                if (source.TeamIndex != targetActor.TeamIndex || source.TeamPosition != targetActor.TeamPosition) continue;
                if (source.ProviderGuid == 0 || source.ProviderGuid == targetGuid || !isPlayerTeam(source.ProviderGuid)) continue;

                if (!HasLiveFloorInstance(targetActor, source))
                {
                    _sources.RemoveAt(i);
                    continue;
                }

                return source.ProviderGuid;
            }

            return 0;
        }

        private void AddOrUpdate(int teamIndex, int teamPosition, uint providerGuid, string skillId, string sourceId, string floorEffectId, string floorDotId)
        {
            if (providerGuid == 0 || teamIndex < 0 || teamPosition < 0) return;

            for (int i = _sources.Count - 1; i >= 0; i--)
            {
                var existing = _sources[i];
                if (existing.TeamIndex == teamIndex &&
                    existing.TeamPosition == teamPosition &&
                    existing.ProviderGuid == providerGuid &&
                    SourceIdMatches(existing.SourceId, sourceId))
                {
                    _sources.RemoveAt(i);
                }
            }

            _sources.Add(new FloorDotSource
            {
                TeamIndex = teamIndex,
                TeamPosition = teamPosition,
                ProviderGuid = providerGuid,
                SkillId = skillId ?? "",
                SourceId = sourceId ?? "",
                FloorEffectId = floorEffectId ?? "",
                FloorDotId = floorDotId ?? ""
            });
        }

        private static bool HasLiveFloorInstance(ActorInstance actor, FloorDotSource source)
        {
            try
            {
                IReadOnlyList<IActorContainerInstance> instances = actor.GetLockedTeamPositionActorContainerInstances();
                if (instances == null || instances.Count == 0) return false;

                for (int i = 0; i < instances.Count; i++)
                {
                    var instance = instances[i];
                    if (instance == null || instance.LockedTeamPosition != source.TeamPosition) continue;

                    string instanceId = instance.ActorContainerDefinition?.Id ?? "";
                    string instanceSourceId = instance.SourceId ?? "";
                    uint instanceSourceGuid = GetSourceActorGuid(instance);
                    if (instanceSourceGuid == source.ProviderGuid &&
                        (NonEmptyIdMatches(instanceSourceId, source.SourceId) ||
                         NonEmptyIdMatches(instanceId, source.FloorDotId) ||
                         NonEmptyIdMatches(instanceId, source.FloorEffectId) ||
                         IsControlledBurnId(instanceSourceId) ||
                         IsControlledBurnId(instanceId)))
                    {
                        return true;
                    }

                    if ((IsControlledBurnId(instanceSourceId) || IsControlledBurnId(instanceId)) &&
                        (source.ProviderGuid != 0 || IsControlledBurnId(source.SourceId) || IsControlledBurnId(source.SkillId)))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool DotDefinitionMatches(DotDefinition left, DotDefinition right)
        {
            if (left == null || right == null) return false;
            return string.Equals(left.m_Id ?? "", right.m_Id ?? "", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(left.m_Type ?? "", right.m_Type ?? "", StringComparison.OrdinalIgnoreCase);
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

        private static bool IsBurnDot(string dotId, string dotType = null)
        {
            if (!string.IsNullOrEmpty(dotType) && ContainsId(dotType, "burn")) return true;
            if (ContainsId(dotId, "burn")) return true;

            try
            {
                if (!string.IsNullOrEmpty(dotId))
                {
                    var dot = SingletonMonoBehaviour<Library<string, DotDefinition>>.Instance?.GetLibraryElement(dotId);
                    if (dot != null && ContainsId(dot.m_Type, "burn")) return true;
                }
            }
            catch { }

            return false;
        }

        private static bool IsControlledBurnId(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return ContainsId(value, "controlled") && ContainsId(value, "burn");
        }

        private static bool LooksLikeFloorTickSource(SourceType sourceType, string sourceId)
        {
            if (IsControlledBurnId(sourceId)) return true;
            if (ContainsId(sourceId, "burn") && IsSourceType(sourceType, "dot")) return true;
            if (string.IsNullOrEmpty(sourceId) &&
                (IsSourceType(sourceType, "dot") ||
                 IsSourceType(sourceType, "buff") ||
                 IsSourceType(sourceType, "locked_team_position_transfer")))
            {
                return true;
            }
            return false;
        }

        private static bool IsSourceType(SourceType sourceType, string expected)
        {
            if (sourceType == null) return false;
            try { return string.Equals(sourceType.GetName(), expected, StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(sourceType.ToString(), expected, StringComparison.OrdinalIgnoreCase); }
        }

        private static bool ContainsId(string value, string needle)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool SourceIdMatches(string left, string right)
        {
            return string.IsNullOrEmpty(left) ||
                   string.IsNullOrEmpty(right) ||
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool NonEmptyIdMatches(string left, string right)
        {
            return !string.IsNullOrEmpty(left) &&
                   !string.IsNullOrEmpty(right) &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
