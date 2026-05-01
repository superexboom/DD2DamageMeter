using UnityEngine;

namespace DD2DamageMeter
{
    public class StatusLogUI
    {
        private float _w = 560f, _h = 420f;
        private const float MIN_W = 420f, MIN_H = 220f, RH = 16f;
        private readonly CombatLogTracker _tracker;
        private Rect _rect = new Rect(640f, 60f, 560f, 420f);
        private Vector2 _scroll;
        private bool _rs; private Vector2 _rsS; private float _rsW, _rsH;
        private GUIStyle _round, _buff, _debuff, _status, _summary, _pn, _en, _nm;
        private GUIStyle _windowStyle, _resizeStyle;
        private bool _init;

        private Texture2D _windowBgTex;
        private float _scaleFactor = 1f;
        private int _lastScreenHeight;

        public bool IsVisible { get; set; }

        public StatusLogUI(CombatLogTracker tracker) { _tracker = tracker; }

        private Texture2D MakeTex(int w, int h, Color c)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = c;
            var tex = new Texture2D(w, h);
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        private void UpdateScaleFactor()
        {
            if (Screen.height == _lastScreenHeight) return;
            _lastScreenHeight = Screen.height;
            _scaleFactor = Mathf.Max(1f, Screen.height / 1080f);
        }

        private void Init()
        {
            if (_init) return;

            _windowBgTex = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.35f));

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _windowBgTex;
            _windowStyle.onNormal.background = _windowBgTex;
            _windowStyle.focused.background = _windowBgTex;
            _windowStyle.onFocused.background = _windowBgTex;
            _windowStyle.normal.textColor = new Color(0.9f, 0.85f, 0.7f);
            _windowStyle.fontSize = 13;
            _windowStyle.fontStyle = FontStyle.Bold;
            _windowStyle.padding = new RectOffset(6, 6, 22, 4);

            _round = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.95f, 0.85f, 0.4f) } };
            _buff = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.35f, 0.9f, 0.95f) } };
            _debuff = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.45f, 0.85f) } };
            _status = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.9f, 0.85f, 0.55f) } };
            _summary = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = new Color(0.75f, 0.78f, 0.82f) } };
            _pn = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.4f, 0.7f, 1f) } };
            _en = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.4f, 0.4f) } };
            _nm = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = new Color(0.8f, 0.8f, 0.8f) } };
            _resizeStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = new Color(0.6f, 0.6f, 0.6f, 0.8f) } };
            _init = true;
        }

        public void Draw()
        {
            Init();
            UpdateScaleFactor();

            var prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scaleFactor, _scaleFactor, 1f));

            _rect = GUI.Window(729004, _rect, Win, "<b>Buff/Debuff Log</b>", _windowStyle);
            _rect = UiUtil.ClampToScreen(_rect, _scaleFactor);

            GUI.matrix = prevMatrix;

            var e = Event.current;
            float mx = e.mousePosition.x / _scaleFactor;
            float my = e.mousePosition.y / _scaleFactor;
            var rr = new Rect(_rect.xMax - RH, _rect.yMax - RH, RH, RH);
            if (e.type == EventType.MouseDown && e.button == 0 && rr.Contains(new Vector2(mx, my))) { _rs = true; _rsS = new Vector2(mx, my); _rsW = _w; _rsH = _h; e.Use(); }
            else if (_rs && e.type == EventType.MouseDrag) { _w = Mathf.Max(MIN_W, _rsW + (mx - _rsS.x)); _h = Mathf.Max(MIN_H, _rsH + (my - _rsS.y)); _rect.width = _w; _rect.height = _h; _rect = UiUtil.ClampToScreen(_rect, _scaleFactor); e.Use(); }
            else if (_rs && e.type == EventType.MouseUp) _rs = false;
        }

        private void Win(int id)
        {
            DrawStatusSummary();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(_h - 48f));
            {
                var entries = _tracker.StatusEntries;
                bool hasStatusEntry = false;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i] is CombatLogTracker.LogEntry) { hasStatusEntry = true; break; }
                }

                if (!hasStatusEntry) { GUILayout.Label("No buff/debuff log yet...", _nm); }
                else
                {
                    if (_tracker.IsStatusDirty) { _scroll.y = float.MaxValue; _tracker.ClearStatusDirty(); }
                    for (int i = 0; i < entries.Count; i++)
                    {
                        if (entries[i] is CombatLogTracker.RoundHeader rh) GUILayout.Label($"--- Round {rh.Round} ---", _round);
                        else if (entries[i] is CombatLogTracker.LogEntry le) DrawEntry(le);
                    }
                }
            }
            GUILayout.EndScrollView();
            GUI.Label(new Rect(_w - RH - 2, _h - RH - 2, RH, RH), "\u255a", _resizeStyle);
            GUI.DragWindow(new Rect(0, 0, _w, _h - RH));
        }

        private void DrawStatusSummary()
        {
            var totals = _tracker.GetStatusTotalsSnapshot();
            if (!totals.HasAny) return;
            GUILayout.Label(
                $"Hero B+{totals.PlayerBuffApplied} D+{totals.PlayerDebuffApplied} -{totals.PlayerStatusRemoved} Used {totals.PlayerStatusConsumed}   |   Enemy B+{totals.EnemyBuffApplied} D+{totals.EnemyDebuffApplied} -{totals.EnemyStatusRemoved} Used {totals.EnemyStatusConsumed}",
                _summary);
        }

        private void DrawEntry(CombatLogTracker.LogEntry le)
        {
            GUILayout.BeginHorizontal();
            {
                if (!string.IsNullOrEmpty(le.SourceName))
                {
                    if (le.SourceName.StartsWith("[")) GUILayout.Label(le.SourceName, _status, GUILayout.Width(70));
                    else GUILayout.Label(le.SourceName, le.SourceIsPlayer ? _pn : _en, GUILayout.Width(110));
                }
                else GUILayout.Space(110);

                GUIStyle s; string t;
                switch (le.ActionType)
                {
                    case "BUFF+": s = _buff; t = "BUFF +"; break;
                    case "BUFF-": s = _buff; t = "BUFF -"; break;
                    case "BUFF!": s = _buff; t = "BUFF USED"; break;
                    case "DEBUFF+": s = _debuff; t = "DEBUFF +"; break;
                    case "DEBUFF-": s = _debuff; t = "DEBUFF -"; break;
                    case "DEBUFF!": s = _debuff; t = "DEBUFF USED"; break;
                    case "TOKEN+": s = _status; t = "TOKEN +"; break;
                    case "TOKEN-": s = _status; t = "TOKEN -"; break;
                    case "TOKEN!": s = _status; t = "TOKEN USED"; break;
                    case "TOKEN~": s = _status; t = "SWAP"; break;
                    case "TOKENx": s = _status; t = "NEGATE"; break;
                    case "STATUS+": s = _status; t = "STATUS +"; break;
                    case "STATUS-": s = _status; t = "STATUS -"; break;
                    default: s = _nm; t = le.ActionType; break;
                }
                GUILayout.Label(t, s, GUILayout.Width(92));
                GUILayout.Label("->", _nm, GUILayout.Width(20));
                GUILayout.Label(le.TargetName ?? "?", le.TargetIsPlayer ? _pn : _en, GUILayout.Width(110));

                string info = le.Extra ?? "";
                if (!string.IsNullOrEmpty(le.SkillId))
                {
                    string sk = le.SkillId; if (sk.Length > 20) sk = sk.Substring(0, 18) + "..";
                    info = sk + " " + info;
                }
                if (!string.IsNullOrEmpty(info)) GUILayout.Label(info.Trim(), _nm);
            }
            GUILayout.EndHorizontal();
        }
    }
}
