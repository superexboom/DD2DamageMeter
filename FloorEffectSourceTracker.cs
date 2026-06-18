using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Actor.ActorContainer;
using Assets.Code.Buff;
using Assets.Code.Buff.Events;
using Assets.Code.Dot;
using Assets.Code.Dot.Events;
using Assets.Code.Effect;
using Assets.Code.Library;
using Assets.Code.Skill.Events;
using Assets.Code.Source;
using Assets.Code.Token;
using Assets.Code.Token.Events;
using Assets.Code.Utils;

namespace DD2DamageMeter
{
    internal sealed class FloorEffectSourceTracker
    {
        internal enum StatusKind
        {
            Token,
            Buff,
            Dot
        }

        internal sealed class SourceMarker
        {
            public uint ProviderGuid;
            public string SourceId;
            public string SkillId;
            public string FloorEffectId;
            public int PlacementId;
            public StatusKind Kind;
            public string StatusId;
        }

        private sealed class FloorPlacement
        {
            public int PlacementId;
            public int Round;
            public uint ProviderGuid;
            public uint TargetGuid;
            public int TeamIndex = -1;
            public int TeamPosition = -1;
            public string SkillId;
            public string SourceId;
            public string FloorEffectId;
            public readonly List<string> TokenIds = new List<string>();
            public readonly List<string> TokenTags = new List<string>();
            public readonly List<string> BuffIds = new List<string>();
            public readonly List<string> DotIds = new List<string>();
            public readonly List<string> DotTypes = new List<string>();
        }

        private const int PlacementKeepRounds = 8;

        private readonly object _lock = new object();
        private readonly List<FloorPlacement> _placements = new List<FloorPlacement>();
        private readonly Dictionary<object, SourceMarker> _markers = new Dictionary<object, SourceMarker>();
        private int _nextPlacementId = 1;
        private int _currentRound;
        private static object _actorLibraryInstance;
        private static MethodInfo _getActorLibraryElement;
        private static bool _actorLibraryReflectionInit;

        public void Reset()
        {
            lock (_lock)
            {
                _placements.Clear();
                _markers.Clear();
                _nextPlacementId = 1;
                _currentRound = 0;
            }
        }

        public void OnBattleStartRound(int round)
        {
            lock (_lock)
            {
                _currentRound = Math.Max(0, round);
                PrunePlacements();
            }
        }

        public void OnSkillFinalizeResults(EventSkillFinalizeResults evt)
        {
            try
            {
                if (evt?.ActorResults == null) return;
                lock (_lock)
                {
                    string skillId = evt.SkillId ?? "";
                    foreach (var ar in evt.ActorResults)
                    {
                        if (ar?.m_AppliedEffectsOutputContainer == null) continue;
                        foreach (var output in ar.m_AppliedEffectsOutputContainer.Outputs)
                        {
                            if (output == null || output.m_TargetActor == null) continue;
                            ActorInstance targetActor = output.m_TargetActor;
                            uint providerGuid = output.m_PerformerActor != null
                                ? output.m_PerformerActor.ActorGuid
                                : ar.m_PerformerActorGuid;
                            if (providerGuid == 0) continue;

                            foreach (var effect in output.EffectInstancesToApply)
                            {
                                EffectDefinition def = effect?.EffectDefinition;
                                if (def == null || !def.m_IsLockedTeamPosition) continue;

                                var placement = new FloorPlacement
                                {
                                    PlacementId = _nextPlacementId++,
                                    Round = Math.Max(1, _currentRound),
                                    ProviderGuid = providerGuid,
                                    TargetGuid = targetActor.ActorGuid,
                                    TeamIndex = targetActor.TeamIndex,
                                    TeamPosition = targetActor.TeamPosition,
                                    SkillId = skillId,
                                    SourceId = !string.IsNullOrEmpty(effect.SourceId) ? effect.SourceId : skillId,
                                    FloorEffectId = def.m_Id ?? ""
                                };
                                CollectCapabilities(placement, def);
                                _placements.Add(placement);
                            }
                        }
                    }
                    PrunePlacements();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"FloorEffectSourceTracker.OnSkillFinalizeResults skipped: {ex.Message}");
            }
        }

        public void OnTokenAdded(EventTokenAdded evt)
        {
            try
            {
                if (evt == null || string.IsNullOrEmpty(evt.m_TokenId)) return;
                ActorInstance actor = TryResolveActor(evt.m_ActorGuid);
                if (actor?.TokenContainer == null) return;
                TokenDefinition token = GetTokenDefinition(evt.m_TokenId);
                if (token == null) return;

                lock (_lock)
                {
                    FloorPlacement placement = FindPlacement(actor, StatusKind.Token, evt.m_TokenId, token, evt.m_SourceType, evt.m_SourceId);
                    if (placement == null)
                    {
                        var added = new EventTokenAddedSurrogate(evt.m_ActorGuid, evt.m_TokenId, evt.m_SourceType, evt.m_SourceId);
                        placement = TryCreatePlacementFromNewestLockedToken(actor, added, token);
                    }
                    if (placement == null) return;
                    CollectCapabilities(placement, token);

                    int remaining = Math.Max(1, evt.m_AddAmount);
                    var instances = GetTokenInstances(actor, evt.m_TokenId, evt.m_SourceType, evt.m_SourceId, true);
                    if (instances == null || instances.Count == 0)
                        instances = GetTokenInstances(actor, evt.m_TokenId, evt.m_SourceType, evt.m_SourceId, false);

                    BindNewestInstances(instances, remaining, placement, StatusKind.Token, evt.m_TokenId);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"FloorEffectSourceTracker.OnTokenAdded skipped: {ex.Message}");
            }
        }

        public void OnTokenReplaced(EventTokenReplaced evt)
        {
            try
            {
                if (evt == null || string.IsNullOrEmpty(evt.m_ReplaceAddTokenId)) return;
                ActorInstance actor = TryResolveActor(evt.m_ActorGuid);
                if (actor?.TokenContainer == null) return;
                TokenDefinition token = GetTokenDefinition(evt.m_ReplaceAddTokenId);
                if (token == null) return;

                lock (_lock)
                {
                    FloorPlacement placement = FindPlacement(actor, StatusKind.Token, evt.m_ReplaceAddTokenId, token, evt.m_SourceType, evt.m_SourceId);
                    if (placement == null)
                    {
                        var added = new EventTokenAddedSurrogate(evt.m_ActorGuid, evt.m_ReplaceAddTokenId, evt.m_SourceType, evt.m_SourceId);
                        placement = TryCreatePlacementFromNewestLockedToken(actor, added, token);
                    }
                    if (placement == null) return;
                    CollectCapabilities(placement, token);

                    var instances = GetTokenInstances(actor, evt.m_ReplaceAddTokenId, evt.m_SourceType, evt.m_SourceId, true);
                    if (instances == null || instances.Count == 0)
                        instances = GetTokenInstances(actor, evt.m_ReplaceAddTokenId, evt.m_SourceType, evt.m_SourceId, false);

                    BindNewestInstances(instances, 1, placement, StatusKind.Token, evt.m_ReplaceAddTokenId);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"FloorEffectSourceTracker.OnTokenReplaced skipped: {ex.Message}");
            }
        }

        public void OnBuffAdded(EventBuffAdded evt)
        {
            try
            {
                if (evt?.Buff == null) return;
                ActorInstance actor = TryResolveActor(evt.TargetActorGuid);
                if (actor?.BuffContainer == null) return;

                lock (_lock)
                {
                    FloorPlacement placement = FindPlacement(actor, StatusKind.Buff, evt.Buff.Id, null, evt.SourceType, evt.SourceId);
                    if (placement == null)
                        placement = TryCreatePlacementFromNewestLockedBuff(actor, evt);
                    if (placement == null) return;
                    CollectCapabilities(placement, evt.Buff);

                    var instances = GetBuffInstances(actor, evt.Buff.Id, evt.SourceType, evt.SourceId, true);
                    if (instances == null || instances.Count == 0)
                        instances = GetBuffInstances(actor, evt.Buff.Id, evt.SourceType, evt.SourceId, false);

                    BindNewestInstances(instances, 1, placement, StatusKind.Buff, evt.Buff.Id);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"FloorEffectSourceTracker.OnBuffAdded skipped: {ex.Message}");
            }
        }

        public void OnDotAdded(EventDotAdded evt)
        {
            try
            {
                if (evt?.m_Actor == null || evt.m_DotDefinition == null) return;
                ActorInstance actor = evt.m_Actor;
                if (actor.DotContainer == null) return;

                lock (_lock)
                {
                    FloorPlacement placement = FindPlacement(actor, StatusKind.Dot, evt.m_DotDefinition.m_Id, null, evt.m_SourceType, evt.m_SourceId, evt.m_DotDefinition.m_Type);
                    if (placement == null)
                        placement = TryCreatePlacementFromNewestLockedDot(actor, evt);
                    if (placement == null) return;

                    var instances = GetDotInstances(actor, evt.m_DotDefinition, evt.m_SourceType, evt.m_SourceId, true);
                    if (instances == null || instances.Count == 0)
                        instances = GetDotInstances(actor, evt.m_DotDefinition, evt.m_SourceType, evt.m_SourceId, false);

                    BindNewestInstances(instances, 1, placement, StatusKind.Dot, evt.m_DotDefinition.m_Id);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"FloorEffectSourceTracker.OnDotAdded skipped: {ex.Message}");
            }
        }

        public bool TryGetSource(IActorContainerInstance instance, out SourceMarker marker)
        {
            marker = null;
            if (instance == null) return false;
            lock (_lock)
            {
                return _markers.TryGetValue(instance, out marker) && marker != null && marker.ProviderGuid != 0;
            }
        }

        public bool TryResolveTokenSource(uint targetGuid, string tokenId, SourceType sourceType, string sourceId, out SourceMarker marker)
        {
            marker = null;
            ActorInstance actor = TryResolveActor(targetGuid);
            if (actor?.TokenContainer == null) return false;
            lock (_lock)
            {
                var instance = FindNewestMarkedToken(actor, tokenId, sourceType, sourceId);
                if (instance != null && _markers.TryGetValue(instance, out marker) && marker.ProviderGuid != 0)
                    return true;

                var placement = FindPlacement(actor, StatusKind.Token, tokenId, GetTokenDefinition(tokenId), sourceType, sourceId);
                if (placement == null || placement.ProviderGuid == 0) return false;
                marker = CreateMarker(placement, StatusKind.Token, tokenId);
                return true;
            }
        }

        public bool TryResolveBuffSource(uint targetGuid, string buffId, SourceType sourceType, string sourceId, out SourceMarker marker)
        {
            marker = null;
            ActorInstance actor = TryResolveActor(targetGuid);
            if (actor?.BuffContainer == null) return false;
            lock (_lock)
            {
                var instance = FindNewestMarkedBuff(actor, buffId, sourceType, sourceId);
                if (instance != null && _markers.TryGetValue(instance, out marker) && marker.ProviderGuid != 0)
                    return true;

                var placement = FindPlacement(actor, StatusKind.Buff, buffId, null, sourceType, sourceId);
                if (placement == null || placement.ProviderGuid == 0) return false;
                marker = CreateMarker(placement, StatusKind.Buff, buffId);
                return true;
            }
        }

        public uint ResolveDotSource(ActorInstance targetActor, string dotId, string dotType, uint currentSourceActorGuid, SourceType sourceType, string sourceId)
        {
            if (targetActor?.DotContainer == null) return 0;
            lock (_lock)
            {
                var instance = FindNewestMarkedDot(targetActor, dotId, dotType, sourceType, sourceId);
                SourceMarker marker = null;
                if (instance != null)
                    _markers.TryGetValue(instance, out marker);

                if (marker == null || marker.ProviderGuid == 0)
                {
                    var placement = FindPlacement(targetActor, StatusKind.Dot, dotId, null, sourceType, sourceId, dotType);
                    if (placement != null && placement.ProviderGuid != 0)
                        marker = CreateMarker(placement, StatusKind.Dot, dotId);
                }

                if (marker == null || marker.ProviderGuid == 0)
                    return 0;

                uint targetGuid = targetActor.ActorGuid;
                if (currentSourceActorGuid != 0 && currentSourceActorGuid != targetGuid && currentSourceActorGuid == marker.ProviderGuid)
                    return 0;

                return marker.ProviderGuid;
            }
        }

        private void BindNewestInstances<T>(IReadOnlyList<T> instances, int amount, FloorPlacement placement, StatusKind kind, string statusId)
            where T : class, IActorContainerInstance
        {
            if (instances == null || instances.Count == 0 || placement == null) return;
            int remaining = Math.Max(1, amount);
            for (int i = instances.Count - 1; i >= 0 && remaining > 0; i--)
            {
                T instance = instances[i];
                if (instance == null || _markers.ContainsKey(instance)) continue;
                _markers[instance] = new SourceMarker
                {
                    ProviderGuid = placement.ProviderGuid,
                    SourceId = !string.IsNullOrEmpty(placement.SourceId) ? placement.SourceId : placement.SkillId,
                    SkillId = placement.SkillId ?? "",
                    FloorEffectId = placement.FloorEffectId ?? "",
                    PlacementId = placement.PlacementId,
                    Kind = kind,
                    StatusId = statusId ?? ""
                };
                remaining--;
            }
        }

        private static SourceMarker CreateMarker(FloorPlacement placement, StatusKind kind, string statusId)
        {
            if (placement == null) return null;
            return new SourceMarker
            {
                ProviderGuid = placement.ProviderGuid,
                SourceId = !string.IsNullOrEmpty(placement.SourceId) ? placement.SourceId : placement.SkillId,
                SkillId = placement.SkillId ?? "",
                FloorEffectId = placement.FloorEffectId ?? "",
                PlacementId = placement.PlacementId,
                Kind = kind,
                StatusId = statusId ?? ""
            };
        }

        private FloorPlacement FindPlacement(ActorInstance actor, StatusKind kind, string statusId, TokenDefinition token, SourceType sourceType, string sourceId, string dotType = null)
        {
            if (actor == null) return null;
            PrunePlacements();

            FloorPlacement sourceMatch = null;
            for (int i = _placements.Count - 1; i >= 0; i--)
            {
                FloorPlacement placement = _placements[i];
                if (!PlacementMatchesActor(placement, actor)) continue;

                bool explicitMatch = false;
                if (kind == StatusKind.Token)
                    explicitMatch = TokenMatches(placement, token, statusId) && PlacementSourceMatches(placement, sourceType, sourceId);
                else if (kind == StatusKind.Buff)
                    explicitMatch = ContainsIgnoreCase(placement.BuffIds, statusId) && PlacementSourceMatches(placement, sourceType, sourceId);
                else if (kind == StatusKind.Dot)
                    explicitMatch = ContainsIgnoreCase(placement.DotIds, statusId) ||
                                    (ContainsIgnoreCase(placement.DotTypes, dotType) && PlacementSourceMatches(placement, sourceType, sourceId));

                if (explicitMatch) return placement;
                if (sourceMatch == null && PlacementSourceMatches(placement, sourceType, sourceId))
                    sourceMatch = placement;
            }

            return sourceMatch;
        }

        private static bool PlacementMatchesActor(FloorPlacement placement, ActorInstance actor)
        {
            if (placement == null || actor == null) return false;
            if (placement.TeamIndex >= 0 && placement.TeamPosition >= 0)
                return actor.TeamIndex == placement.TeamIndex && actor.TeamPosition == placement.TeamPosition;
            return placement.TargetGuid == actor.ActorGuid;
        }

        private static bool PlacementSourceMatches(FloorPlacement placement, SourceType sourceType, string sourceId)
        {
            if (placement == null) return false;
            if (!string.IsNullOrEmpty(sourceId))
            {
                return NonEmptyIdMatches(placement.SourceId, sourceId) ||
                       NonEmptyIdMatches(placement.SkillId, sourceId) ||
                       NonEmptyIdMatches(placement.FloorEffectId, sourceId) ||
                       ContainsIgnoreCase(placement.TokenIds, sourceId) ||
                       ContainsIgnoreCase(placement.BuffIds, sourceId) ||
                       ContainsIgnoreCase(placement.DotIds, sourceId);
            }

            return IsSourceType(sourceType, "locked_team_position_transfer") ||
                   IsSourceType(sourceType, "buff") ||
                   IsSourceType(sourceType, "skill_buff");
        }

        private static bool TokenMatches(FloorPlacement placement, TokenDefinition token, string tokenId)
        {
            if (ContainsIgnoreCase(placement.TokenIds, tokenId)) return true;
            if (token?.Tags == null) return false;
            for (int i = 0; i < placement.TokenTags.Count; i++)
            {
                string tag = placement.TokenTags[i];
                if (string.IsNullOrEmpty(tag)) continue;
                for (int j = 0; j < token.Tags.Count; j++)
                {
                    if (string.Equals(token.Tags[j] ?? "", tag, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private void PrunePlacements()
        {
            if (_currentRound <= 0) return;
            for (int i = _placements.Count - 1; i >= 0; i--)
            {
                if (_currentRound - _placements[i].Round > PlacementKeepRounds)
                    _placements.RemoveAt(i);
            }
        }

        private static void CollectCapabilities(FloorPlacement placement, EffectDefinition effect)
        {
            if (placement == null || effect == null) return;

            AddUnique(placement.TokenIds, effect.m_TokenAddId);
            AddUnique(placement.TokenTags, effect.m_TokenAddTag);
            AddUnique(placement.TokenIds, effect.m_TokenConvertToId);
            AddUnique(placement.DotIds, effect.m_DotAddId);
            AddDotType(placement, effect.m_DotAddId);

            CollectCapabilities(placement, GetTokenDefinition(effect.m_TokenAddId));
            CollectCapabilities(placement, GetTokenDefinition(effect.m_TokenConvertToId));

            foreach (BuffDefinition buff in effect.Buffs)
            {
                if (buff == null) continue;
                AddUnique(placement.BuffIds, buff.Id);
                CollectCapabilities(placement, buff);
            }
        }

        private static void CollectCapabilities(FloorPlacement placement, BuffDefinition buff)
        {
            try
            {
                CollectCapabilities(placement, buff?.ActorDataEffects);
            }
            catch { }
        }

        private static void CollectCapabilities(FloorPlacement placement, TokenDefinition token)
        {
            try
            {
                if (placement == null || token == null) return;
                AddUnique(placement.TokenIds, token.Id);
                CollectCapabilities(placement, token.ActorDataEffects);
                CollectCapabilities(placement, token.DataExternalBuffs);
            }
            catch { }
        }

        private static void CollectCapabilities(FloorPlacement placement, DataExternalBuffs externalBuffs)
        {
            try
            {
                if (placement == null || externalBuffs == null) return;
                var buffs = externalBuffs.GetBuffs();
                if (buffs == null) return;
                for (int i = 0; i < buffs.Count; i++)
                {
                    BuffDefinition buff = buffs[i];
                    if (buff == null) continue;
                    AddUnique(placement.BuffIds, buff.Id);
                    CollectCapabilities(placement, buff);
                }
            }
            catch { }
        }

        private static void CollectCapabilities(FloorPlacement placement, ActorDataEffects actorDataEffects)
        {
            try
            {
                if (actorDataEffects == null) return;
                foreach (var effectGroup in actorDataEffects.EffectGroups)
                {
                    if (effectGroup == null || effectGroup.SourceEffects == null) continue;
                    foreach (var sourceEffect in effectGroup.SourceEffects)
                    {
                        EffectDefinition effect = sourceEffect?.Definition;
                        if (effect == null) continue;
                        CollectCapabilities(placement, effect);
                    }
                }
            }
            catch { }
        }

        private static IReadOnlyList<TokenInstance> GetTokenInstances(ActorInstance actor, string tokenId, SourceType sourceType, string sourceId, bool exactSource)
        {
            try
            {
                return actor?.TokenContainer?.GetInstances(instance =>
                    instance != null &&
                    instance.Definition != null &&
                    string.Equals(instance.Definition.Id ?? "", tokenId ?? "", StringComparison.OrdinalIgnoreCase) &&
                    (!exactSource || (SourceTypeMatches(instance.SourceType, sourceType) && SourceIdMatches(instance.SourceId, sourceId))));
            }
            catch { return null; }
        }

        private static IReadOnlyList<BuffInstance> GetBuffInstances(ActorInstance actor, string buffId, SourceType sourceType, string sourceId, bool exactSource)
        {
            try
            {
                return actor?.BuffContainer?.GetInstances(instance =>
                    instance != null &&
                    instance.Definition != null &&
                    string.Equals(instance.Definition.Id ?? "", buffId ?? "", StringComparison.OrdinalIgnoreCase) &&
                    (!exactSource || (SourceTypeMatches(instance.SourceType, sourceType) && SourceIdMatches(instance.SourceId, sourceId))));
            }
            catch { return null; }
        }

        private static IReadOnlyList<DotInstance> GetDotInstances(ActorInstance actor, DotDefinition definition, SourceType sourceType, string sourceId, bool exactSource)
        {
            try
            {
                return actor?.DotContainer?.GetInstances(instance =>
                    instance != null &&
                    instance.Definition != null &&
                    DotDefinitionMatches(instance.Definition, definition) &&
                    (!exactSource || (SourceTypeMatches(instance.SourceType, sourceType) && SourceIdMatches(instance.SourceId, sourceId))));
            }
            catch { return null; }
        }

        private static void AddDotType(FloorPlacement placement, string dotId)
        {
            if (string.IsNullOrEmpty(dotId)) return;
            try
            {
                DotDefinition dot = SingletonMonoBehaviour<Library<string, DotDefinition>>.Instance?.GetLibraryElement(dotId);
                AddUnique(placement.DotTypes, dot?.m_Type);
            }
            catch { }
        }

        private TokenInstance FindNewestMarkedToken(ActorInstance actor, string tokenId, SourceType sourceType, string sourceId)
        {
            try
            {
                var instances = actor.TokenContainer.GetInstances(instance =>
                    instance != null &&
                    instance.Definition != null &&
                    string.Equals(instance.Definition.Id ?? "", tokenId ?? "", StringComparison.OrdinalIgnoreCase) &&
                    SourceTypeMatches(instance.SourceType, sourceType) &&
                    SourceIdMatches(instance.SourceId, sourceId));
                for (int i = instances.Count - 1; i >= 0; i--)
                {
                    if (_markers.ContainsKey(instances[i])) return instances[i];
                }

                instances = actor.TokenContainer.GetInstances(instance =>
                    instance != null &&
                    instance.Definition != null &&
                    string.Equals(instance.Definition.Id ?? "", tokenId ?? "", StringComparison.OrdinalIgnoreCase));
                for (int i = instances.Count - 1; i >= 0; i--)
                {
                    if (_markers.ContainsKey(instances[i])) return instances[i];
                }
            }
            catch { }
            return null;
        }

        private BuffInstance FindNewestMarkedBuff(ActorInstance actor, string buffId, SourceType sourceType, string sourceId)
        {
            try
            {
                var instances = actor.BuffContainer.GetInstances(instance =>
                    instance != null &&
                    instance.Definition != null &&
                    string.Equals(instance.Definition.Id ?? "", buffId ?? "", StringComparison.OrdinalIgnoreCase) &&
                    SourceTypeMatches(instance.SourceType, sourceType) &&
                    SourceIdMatches(instance.SourceId, sourceId));
                for (int i = instances.Count - 1; i >= 0; i--)
                {
                    if (_markers.ContainsKey(instances[i])) return instances[i];
                }

                instances = actor.BuffContainer.GetInstances(instance =>
                    instance != null &&
                    instance.Definition != null &&
                    string.Equals(instance.Definition.Id ?? "", buffId ?? "", StringComparison.OrdinalIgnoreCase));
                for (int i = instances.Count - 1; i >= 0; i--)
                {
                    if (_markers.ContainsKey(instances[i])) return instances[i];
                }
            }
            catch { }
            return null;
        }

        private DotInstance FindNewestMarkedDot(ActorInstance actor, string dotId, string dotType, SourceType sourceType, string sourceId)
        {
            try
            {
                var instances = actor.DotContainer.GetInstances(instance =>
                    instance != null &&
                    instance.Definition != null &&
                    DotDefinitionIdOrTypeMatches(instance.Definition, dotId, dotType) &&
                    SourceTypeMatches(instance.SourceType, sourceType) &&
                    SourceIdMatches(instance.SourceId, sourceId));
                for (int i = instances.Count - 1; i >= 0; i--)
                {
                    if (_markers.ContainsKey(instances[i])) return instances[i];
                }

                instances = actor.DotContainer.GetInstances(instance =>
                    instance != null &&
                    instance.Definition != null &&
                    DotDefinitionIdOrTypeMatches(instance.Definition, dotId, dotType));
                for (int i = instances.Count - 1; i >= 0; i--)
                {
                    if (_markers.ContainsKey(instances[i])) return instances[i];
                }
            }
            catch { }
            return null;
        }

        private FloorPlacement TryCreatePlacementFromNewestLockedDot(ActorInstance actor, EventDotAdded evt)
        {
            try
            {
                if (actor?.DotContainer == null || evt?.m_DotDefinition == null) return null;
                var instances = actor.DotContainer.GetInstances(instance =>
                    instance != null &&
                    instance.Definition != null &&
                    instance.IsLockedTeamPosition &&
                    DotDefinitionMatches(instance.Definition, evt.m_DotDefinition) &&
                    SourceTypeMatches(instance.SourceType, evt.m_SourceType) &&
                    SourceIdMatches(instance.SourceId, evt.m_SourceId));

                if (instances == null || instances.Count == 0)
                    instances = actor.DotContainer.GetInstances(instance =>
                        instance != null &&
                        instance.Definition != null &&
                        instance.IsLockedTeamPosition &&
                        DotDefinitionMatches(instance.Definition, evt.m_DotDefinition));

                if (instances == null || instances.Count == 0) return null;
                DotInstance dot = instances[instances.Count - 1];
                if (dot.SourceActorGuid == 0) return null;

                var placement = new FloorPlacement
                {
                    PlacementId = _nextPlacementId++,
                    Round = Math.Max(1, _currentRound),
                    ProviderGuid = dot.SourceActorGuid,
                    TargetGuid = actor.ActorGuid,
                    TeamIndex = actor.TeamIndex,
                    TeamPosition = dot.LockedTeamPosition >= 0 ? dot.LockedTeamPosition : actor.TeamPosition,
                    SkillId = dot.SourceId ?? evt.m_SourceId ?? "",
                    SourceId = dot.SourceId ?? evt.m_SourceId ?? "",
                    FloorEffectId = dot.Definition?.m_Id ?? evt.m_DotDefinition.m_Id ?? ""
                };
                AddUnique(placement.DotIds, evt.m_DotDefinition.m_Id);
                AddUnique(placement.DotTypes, evt.m_DotDefinition.m_Type);
                _placements.Add(placement);
                return placement;
            }
            catch { return null; }
        }

        private FloorPlacement TryCreatePlacementFromNewestLockedBuff(ActorInstance actor, EventBuffAdded evt)
        {
            try
            {
                if (actor?.BuffContainer == null || evt?.Buff == null) return null;
                var instances = actor.BuffContainer.GetInstances(instance =>
                    instance != null &&
                    instance.Definition != null &&
                    instance.IsLockedTeamPosition &&
                    string.Equals(instance.Definition.Id ?? "", evt.Buff.Id ?? "", StringComparison.OrdinalIgnoreCase) &&
                    SourceTypeMatches(instance.SourceType, evt.SourceType) &&
                    SourceIdMatches(instance.SourceId, evt.SourceId));

                if (instances == null || instances.Count == 0)
                    instances = actor.BuffContainer.GetInstances(instance =>
                        instance != null &&
                        instance.Definition != null &&
                        instance.IsLockedTeamPosition &&
                        string.Equals(instance.Definition.Id ?? "", evt.Buff.Id ?? "", StringComparison.OrdinalIgnoreCase));

                if (instances == null || instances.Count == 0) return null;
                BuffInstance buff = instances[instances.Count - 1];
                if (buff.SourceActorGuid == 0) return null;

                var placement = new FloorPlacement
                {
                    PlacementId = _nextPlacementId++,
                    Round = Math.Max(1, _currentRound),
                    ProviderGuid = buff.SourceActorGuid,
                    TargetGuid = actor.ActorGuid,
                    TeamIndex = actor.TeamIndex,
                    TeamPosition = buff.LockedTeamPosition >= 0 ? buff.LockedTeamPosition : actor.TeamPosition,
                    SkillId = buff.SourceId ?? evt.SourceId ?? "",
                    SourceId = buff.SourceId ?? evt.SourceId ?? "",
                    FloorEffectId = buff.Definition?.Id ?? evt.Buff.Id ?? ""
                };
                AddUnique(placement.BuffIds, evt.Buff.Id);
                CollectCapabilities(placement, evt.Buff);
                _placements.Add(placement);
                return placement;
            }
            catch { return null; }
        }

        private FloorPlacement TryCreatePlacementFromNewestLockedToken(ActorInstance actor, ITokenAddEvent evt, TokenDefinition token)
        {
            try
            {
                if (actor?.TokenContainer == null || evt == null || token == null) return null;
                var instances = actor.TokenContainer.GetInstances(instance =>
                    instance != null &&
                    instance.Definition != null &&
                    instance.IsLockedTeamPosition &&
                    string.Equals(instance.Definition.Id ?? "", evt.TokenId ?? "", StringComparison.OrdinalIgnoreCase) &&
                    SourceTypeMatches(instance.SourceType, evt.SourceType) &&
                    SourceIdMatches(instance.SourceId, evt.SourceId));

                if (instances == null || instances.Count == 0)
                    instances = actor.TokenContainer.GetInstances(instance =>
                        instance != null &&
                        instance.Definition != null &&
                        instance.IsLockedTeamPosition &&
                        string.Equals(instance.Definition.Id ?? "", evt.TokenId ?? "", StringComparison.OrdinalIgnoreCase));

                if (instances == null || instances.Count == 0) return null;
                TokenInstance tokenInstance = instances[instances.Count - 1];
                if (tokenInstance.SourceActorGuid == 0) return null;

                var placement = new FloorPlacement
                {
                    PlacementId = _nextPlacementId++,
                    Round = Math.Max(1, _currentRound),
                    ProviderGuid = tokenInstance.SourceActorGuid,
                    TargetGuid = actor.ActorGuid,
                    TeamIndex = actor.TeamIndex,
                    TeamPosition = tokenInstance.LockedTeamPosition >= 0 ? tokenInstance.LockedTeamPosition : actor.TeamPosition,
                    SkillId = tokenInstance.SourceId ?? evt.SourceId ?? "",
                    SourceId = tokenInstance.SourceId ?? evt.SourceId ?? "",
                    FloorEffectId = tokenInstance.Definition?.Id ?? evt.TokenId ?? ""
                };
                AddUnique(placement.TokenIds, evt.TokenId);
                CollectCapabilities(placement, token);
                _placements.Add(placement);
                return placement;
            }
            catch { return null; }
        }

        private interface ITokenAddEvent
        {
            uint ActorGuid { get; }
            string TokenId { get; }
            SourceType SourceType { get; }
            string SourceId { get; }
        }

        private sealed class EventTokenAddedSurrogate : ITokenAddEvent
        {
            public EventTokenAddedSurrogate(uint actorGuid, string tokenId, SourceType sourceType, string sourceId)
            {
                ActorGuid = actorGuid;
                TokenId = tokenId;
                SourceType = sourceType;
                SourceId = sourceId;
            }

            public uint ActorGuid { get; }
            public string TokenId { get; }
            public SourceType SourceType { get; }
            public string SourceId { get; }
        }

        private static ActorInstance TryResolveActor(uint guid)
        {
            try
            {
                if (guid == 0) return null;
                ActorInstance actor = null;
                try
                {
                    actor = SingletonMonoBehaviour<Library<uint, ActorInstance>>.Instance?.GetLibraryElement(guid);
                }
                catch { }
                if (actor != null) return actor;

                EnsureActorLibraryReflection();
                return _getActorLibraryElement?.Invoke(_actorLibraryInstance, new object[] { guid }) as ActorInstance;
            }
            catch { return null; }
        }

        private static void EnsureActorLibraryReflection()
        {
            if (_actorLibraryReflectionInit) return;
            _actorLibraryReflectionInit = true;
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
                var instanceProperty = libraryType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceProperty != null)
                    _actorLibraryInstance = instanceProperty.GetValue(null);

                if (_actorLibraryInstance == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "PlayFab") continue;
                        try
                        {
                            foreach (var type in asm.GetTypes())
                            {
                                if (!type.IsGenericTypeDefinition || type.GetGenericArguments().Length != 1) continue;
                                if (type.Name != "Singleton`1" && type.Name != "SingletonMonoBehaviour`1") continue;
                                try
                                {
                                    var singletonType = type.MakeGenericType(libraryType);
                                    var singletonInstance = singletonType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                                    if (singletonInstance != null)
                                        _actorLibraryInstance = singletonInstance.GetValue(null);
                                    if (_actorLibraryInstance != null) break;
                                }
                                catch { }
                            }
                            if (_actorLibraryInstance != null) break;
                        }
                        catch { }
                    }
                }

                _getActorLibraryElement = libraryType.GetMethod("GetLibraryElement", new[] { typeof(uint) });
            }
            catch { }
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

        private static bool DotDefinitionMatches(DotDefinition left, DotDefinition right)
        {
            if (left == null || right == null) return false;
            return string.Equals(left.m_Id ?? "", right.m_Id ?? "", StringComparison.OrdinalIgnoreCase) ||
                   DotTypeMatches(left.m_Type, right.m_Type);
        }

        private static bool DotDefinitionIdOrTypeMatches(DotDefinition dot, string dotId, string dotType)
        {
            if (dot == null) return false;
            return NonEmptyIdMatches(dot.m_Id, dotId) || DotTypeMatches(dot.m_Type, dotType);
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

        private static bool SourceIdMatches(string left, string right)
        {
            return string.IsNullOrEmpty(left) ||
                   string.IsNullOrEmpty(right) ||
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
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

        private static bool NonEmptyIdMatches(string left, string right)
        {
            return !string.IsNullOrEmpty(left) &&
                   !string.IsNullOrEmpty(right) &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddUnique(List<string> list, string value)
        {
            if (list == null || string.IsNullOrEmpty(value)) return;
            if (!ContainsIgnoreCase(list, value)) list.Add(value);
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
    }
}
