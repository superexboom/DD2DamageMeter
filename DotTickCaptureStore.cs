using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Assets.Code.Actor;
using Assets.Code.Dot;
using Assets.Code.Effect;
using HarmonyLib;

namespace DD2DamageMeter
{
    internal sealed class DotTickCapture
    {
        public uint SourceActorGuid;
        public string SourceTypeName;
        public string SourceId;
        public string SkillId;
        public string DotId;
        public string DotType;
        public float RawAmount;
    }

    internal static class DotTickCaptureStore
    {
        private sealed class Bucket
        {
            public readonly List<DotTickCapture> Shares = new List<DotTickCapture>();
        }

        private static readonly ConditionalWeakTable<EffectApplyCombinedResult, Bucket> Captures =
            new ConditionalWeakTable<EffectApplyCombinedResult, Bucket>();

        public static void Record(DotInstance dot, ActorInstance targetActor, EffectApplyCombinedResult result, float healthChange)
        {
            if (dot?.Definition == null || result == null || healthChange >= -0.0001f) return;

            uint sourceActorGuid = dot.SourceActorGuid;
            string sourceTypeName = SourceTypeName(dot.SourceType);
            string sourceId = dot.SourceId ?? "";
            string skillId = sourceId;

            FloorEffectSourceTracker floorSources = Plugin.Instance?.FloorEffectSources;
            if (floorSources != null && floorSources.TryGetSource(dot, out var marker))
            {
                sourceActorGuid = marker.ProviderGuid != 0 ? marker.ProviderGuid : sourceActorGuid;
                sourceTypeName = "floor";
                sourceId = FirstNonEmpty(marker.SourceId, sourceId);
                skillId = FirstNonEmpty(marker.SkillId, marker.SourceId, skillId);
            }
            else
            {
                DamageTracker tracker = Plugin.Instance?.Tracker;
                if (tracker != null && tracker.TryResolveDotSource(
                    targetActor,
                    dot.Definition.m_Id ?? "",
                    dot.Definition.m_Type ?? "",
                    sourceActorGuid,
                    dot.SourceType,
                    sourceId,
                    out var resolvedGuid,
                    out var resolvedType,
                    out var resolvedId))
                {
                    if (resolvedGuid != 0) sourceActorGuid = resolvedGuid;
                    if (!string.IsNullOrEmpty(resolvedType)) sourceTypeName = resolvedType;
                    if (!string.IsNullOrEmpty(resolvedId)) sourceId = resolvedId;
                    if (string.IsNullOrEmpty(skillId)) skillId = resolvedId;
                }
            }

            var capture = new DotTickCapture
            {
                SourceActorGuid = sourceActorGuid,
                SourceTypeName = sourceTypeName,
                SourceId = sourceId,
                SkillId = skillId ?? "",
                DotId = dot.Definition.m_Id ?? "",
                DotType = dot.Definition.m_Type ?? "",
                RawAmount = -healthChange
            };

            Bucket bucket = Captures.GetValue(result, _ => new Bucket());
            lock (bucket.Shares)
            {
                bucket.Shares.Add(capture);
            }
        }

        public static List<DotTickCapture> GetShares(EffectApplyCombinedResult result)
        {
            var shares = new List<DotTickCapture>();
            if (result == null || !Captures.TryGetValue(result, out var bucket)) return shares;

            lock (bucket.Shares)
            {
                for (int i = 0; i < bucket.Shares.Count; i++)
                {
                    DotTickCapture source = bucket.Shares[i];
                    shares.Add(new DotTickCapture
                    {
                        SourceActorGuid = source.SourceActorGuid,
                        SourceTypeName = source.SourceTypeName,
                        SourceId = source.SourceId,
                        SkillId = source.SkillId,
                        DotId = source.DotId,
                        DotType = source.DotType,
                        RawAmount = source.RawAmount
                    });
                }
            }
            return shares;
        }

        private static string SourceTypeName(Assets.Code.Source.SourceType sourceType)
        {
            if (sourceType == null) return "dot";
            try { return sourceType.GetName(); }
            catch { return "dot"; }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrEmpty(values[i])) return values[i];
            }
            return "";
        }
    }

    [HarmonyPatch(typeof(DotInstance), nameof(DotInstance.Apply), new[]
    {
        typeof(ActorInstance),
        typeof(EffectApplyCombinedResult)
    })]
    internal static class DotInstanceApplyCapturePatch
    {
        private static void Prefix(EffectApplyCombinedResult effectApplyCombinedResult, out float __state)
        {
            __state = effectApplyCombinedResult?.HealthChange ?? 0f;
        }

        private static void Postfix(
            DotInstance __instance,
            ActorInstance targetActor,
            EffectApplyCombinedResult effectApplyCombinedResult,
            float __state)
        {
            if (effectApplyCombinedResult == null) return;
            DotTickCaptureStore.Record(__instance, targetActor, effectApplyCombinedResult, effectApplyCombinedResult.HealthChange - __state);
        }
    }
}
