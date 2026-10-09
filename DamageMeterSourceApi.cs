using System;
using Assets.Code.Actor;
using Assets.Code.Effect;
using Assets.Code.Source;

namespace DD2DamageMeter
{
    public static class DamageMeterSourceApi
    {
        public static bool TryGetDotTickShares(
            EffectApplyCombinedResult result,
            out uint[] sourceActorGuids,
            out string[] sourceTypeNames,
            out string[] sourceIds,
            out string[] skillIds,
            out string[] dotIds,
            out string[] dotTypes,
            out float[] rawAmounts)
        {
            sourceActorGuids = Array.Empty<uint>();
            sourceTypeNames = Array.Empty<string>();
            sourceIds = Array.Empty<string>();
            skillIds = Array.Empty<string>();
            dotIds = Array.Empty<string>();
            dotTypes = Array.Empty<string>();
            rawAmounts = Array.Empty<float>();

            var shares = DotTickCaptureStore.GetShares(result);
            if (shares.Count == 0) return false;

            int count = shares.Count;
            sourceActorGuids = new uint[count];
            sourceTypeNames = new string[count];
            sourceIds = new string[count];
            skillIds = new string[count];
            dotIds = new string[count];
            dotTypes = new string[count];
            rawAmounts = new float[count];
            for (int i = 0; i < count; i++)
            {
                DotTickCapture share = shares[i];
                sourceActorGuids[i] = share.SourceActorGuid;
                sourceTypeNames[i] = share.SourceTypeName ?? "";
                sourceIds[i] = share.SourceId ?? "";
                skillIds[i] = share.SkillId ?? "";
                dotIds[i] = share.DotId ?? "";
                dotTypes[i] = share.DotType ?? "";
                rawAmounts[i] = share.RawAmount;
            }
            return true;
        }

        public static bool TryResolveDotSource(
            ActorInstance targetActor,
            string dotId,
            string dotType,
            uint currentSourceActorGuid,
            SourceType sourceType,
            string sourceId,
            out uint sourceActorGuid,
            out string sourceTypeName,
            out string resolvedSourceId)
        {
            sourceActorGuid = 0;
            sourceTypeName = "";
            resolvedSourceId = "";
            DamageTracker tracker = Plugin.Instance?.Tracker;
            if (tracker == null || !tracker.TryResolveDotSource(
                targetActor,
                dotId,
                dotType,
                currentSourceActorGuid,
                sourceType,
                sourceId,
                out sourceActorGuid,
                out sourceTypeName,
                out resolvedSourceId))
            {
                return false;
            }

            return true;
        }
    }
}
