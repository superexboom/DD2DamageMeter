using UnityEngine;

namespace DD2DamageMeter
{
    internal static class UiUtil
    {
        public static Rect ClampToScreen(Rect rect, float scaleFactor)
        {
            float scale = Mathf.Max(0.001f, scaleFactor);
            float screenW = Screen.width / scale;
            float screenH = Screen.height / scale;
            if (screenW <= 0f || screenH <= 0f) return rect;

            if (rect.width <= screenW)
                rect.x = Mathf.Clamp(rect.x, 0f, screenW - rect.width);
            else
                rect.x = Mathf.Clamp(rect.x, screenW - rect.width, 0f);

            if (rect.height <= screenH)
                rect.y = Mathf.Clamp(rect.y, 0f, screenH - rect.height);
            else
                rect.y = Mathf.Clamp(rect.y, screenH - rect.height, 0f);

            return rect;
        }

        public static string FormatDamageTaken(float rawDamageReceived, float actualDamageReceived)
        {
            if (rawDamageReceived > 0f && rawDamageReceived > actualDamageReceived + 0.5f)
                return $"{rawDamageReceived:F0}({actualDamageReceived:F0})";
            return $"{actualDamageReceived:F0}";
        }

        public static float GetAvoidanceRate(int avoidedAttacks, int incomingAttacks)
        {
            if (incomingAttacks <= 0) return 0f;
            return avoidedAttacks / (float)incomingAttacks * 100f;
        }

        public static string FormatAvoidanceRate(int avoidedAttacks, int incomingAttacks)
        {
            if (incomingAttacks <= 0) return "-";
            return $"{GetAvoidanceRate(avoidedAttacks, incomingAttacks):F1}%";
        }
    }
}
