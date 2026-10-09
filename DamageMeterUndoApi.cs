using System;

namespace DD2DamageMeter
{
    /// <summary>
    /// Optional integration surface for combat rewind plugins. Calls are safe when
    /// no rewind is in progress and have no effect on ordinary recording.
    /// </summary>
    public static class DamageMeterUndoApi
    {
        public const int ApiVersion = 1;

        public static long RestoreEpoch { get; private set; }

        public static bool IsRestoreInProgress => Plugin.Instance != null && Plugin.Instance.IsUndoRestoreInProgress;

        public static bool BeginUndoRestore(string restoreId, long epoch)
        {
            if (Plugin.Instance == null) return false;
            RestoreEpoch = epoch;
            Plugin.Instance.BeginUndoRestore(restoreId, epoch);
            return true;
        }

        public static bool BeginUndoRestore(string restoreId)
        {
            return BeginUndoRestore(restoreId, RestoreEpoch + 1L);
        }

        public static bool BeginRestore(string restoreId, long epoch)
        {
            return BeginUndoRestore(restoreId, epoch);
        }

        public static bool EndUndoRestore(
            string restoreId,
            int combatCount,
            Guid combatGuid,
            bool combatActive,
            bool success)
        {
            if (Plugin.Instance == null) return false;
            Plugin.Instance.EndUndoRestore(restoreId, combatCount, combatGuid, combatActive, success);
            return true;
        }

        public static bool EndUndoRestore(string restoreId, bool success)
        {
            return EndUndoRestore(restoreId, 0, Guid.Empty, true, success);
        }

        public static bool EndRestore(string restoreId, bool success)
        {
            return EndUndoRestore(restoreId, success);
        }
    }
}
