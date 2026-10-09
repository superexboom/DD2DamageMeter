using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DD2DamageMeter
{
    /// <summary>
    /// Tracks all visible IMGUI window rects (in screen space) and provides
    /// a query for whether the mouse cursor is currently hovering over any
    /// plugin window.  Harmony patches on <see cref="Input"/> mouse-button
    /// methods use this to selectively suppress game input only when the
    /// cursor is inside a plugin window — clicks outside windows still reach
    /// the game normally.
    /// </summary>
    public static class UiInputBlocker
    {
        // Screen-space rects (origin bottom-left, matching Input.mousePosition).
        private static readonly List<Rect> _screenRects = new List<Rect>();

        /// <summary>
        /// Clear the rect list.  Should be called at the start of every
        /// OnGUI pass so that only currently-visible windows are registered.
        /// </summary>
        public static void ClearRects()
        {
            _screenRects.Clear();
        }

        /// <summary>
        /// Register a GUI-space window rect (top-left origin, y-down) by
        /// converting it to screen space (bottom-left origin, y-up).
        /// </summary>
        /// <param name="guiRect">Window rect in GUI coordinate space.</param>
        /// <param name="scaleFactor">The GUI.matrix scale factor applied to the window.</param>
        public static void RegisterRect(Rect guiRect, float scaleFactor)
        {
            float scale = Mathf.Max(0.001f, scaleFactor);
            // GUI space: origin top-left, y increases downward.
            // Screen space (Input.mousePosition): origin bottom-left, y increases upward.
            float screenX = guiRect.x * scale;
            float screenY = Screen.height - (guiRect.y + guiRect.height) * scale;
            float screenW = guiRect.width * scale;
            float screenH = guiRect.height * scale;
            _screenRects.Add(new Rect(screenX, screenY, screenW, screenH));
        }

        /// <summary>
        /// Returns true if the current mouse position (from Input.mousePosition)
        /// falls within any registered window rect.
        /// </summary>
        public static bool IsMouseOverAnyWindow()
        {
            if (_screenRects.Count == 0) return false;
            Vector3 mousePos = Input.mousePosition;
            for (int i = 0; i < _screenRects.Count; i++)
            {
                if (_screenRects[i].Contains(mousePos))
                    return true;
            }
            return false;
        }
    }

    // ── Harmony patches: suppress mouse-button input only when the cursor
    //    is hovering over a plugin window.  All three overloads (Down / Held / Up)
    //    are patched so that drag-start, drag-hold, and release are all covered.

    [HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetMouseButtonDown))]
    public static class Patch_Input_GetMouseButtonDown
    {
        static bool Prefix(ref bool __result, int button)
        {
            if (UiInputBlocker.IsMouseOverAnyWindow())
            {
                __result = false;
                return false; // skip original method
            }
            return true; // run original method
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetMouseButton))]
    public static class Patch_Input_GetMouseButton
    {
        static bool Prefix(ref bool __result, int button)
        {
            if (UiInputBlocker.IsMouseOverAnyWindow())
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetMouseButtonUp))]
    public static class Patch_Input_GetMouseButtonUp
    {
        static bool Prefix(ref bool __result, int button)
        {
            if (UiInputBlocker.IsMouseOverAnyWindow())
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
