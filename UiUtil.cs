using UnityEngine;

namespace DD2DamageMeter
{
    internal static class DamageMeterUiSettings
    {
        private const int DefaultFontSize = 11;
        private const float DefaultScale = 1f;
        private static System.Func<int> _fontSizeGetter;
        private static System.Func<float> _scaleGetter;

        public static void Configure(System.Func<int> fontSizeGetter, System.Func<float> scaleGetter)
        {
            _fontSizeGetter = fontSizeGetter;
            _scaleGetter = scaleGetter;
        }

        public static int FontSize => Mathf.Clamp(_fontSizeGetter?.Invoke() ?? DefaultFontSize, 8, 28);

        public static float CustomScale => Mathf.Clamp(_scaleGetter?.Invoke() ?? DefaultScale, 0.5f, 3f);

        public static float FontScale => FontSize / (float)DefaultFontSize;

        public static float LayoutScale => Mathf.Max(1f, FontScale);

        public static float OverlayScale => Mathf.Max(1f, Screen.height / 1080f) * CustomScale;

        public static int Version => FontSize * 1000 + Mathf.RoundToInt(CustomScale * 100f);

        public static int Font(int baseFontSize)
        {
            return Mathf.Max(1, Mathf.RoundToInt(baseFontSize * FontScale));
        }

        public static float Size(float value)
        {
            return value * LayoutScale;
        }
    }

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
