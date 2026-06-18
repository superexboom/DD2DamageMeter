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
        private int _lastUiSettingsVersion;
        private int _styleVersion = -1;

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
            int settingsVersion = DamageMeterUiSettings.Version;
            if (Screen.height == _lastScreenHeight && settingsVersion == _lastUiSettingsVersion) return;
            _lastScreenHeight = Screen.height;
            _lastUiSettingsVersion = settingsVersion;
            _scaleFactor = DamageMeterUiSettings.OverlayScale;
        }

        private static float U(float value) => DamageMeterUiSettings.Size(value);

        private static int F(int baseFontSize) => DamageMeterUiSettings.Font(baseFontSize);

        private void Init()
        {
            int settingsVersion = DamageMeterUiSettings.Version;
            if (_init && _styleVersion == settingsVersion) return;

            _windowBgTex = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.35f));

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _windowBgTex;
            _windowStyle.onNormal.background = _windowBgTex;
            _windowStyle.focused.background = _windowBgTex;
            _windowStyle.onFocused.background = _windowBgTex;
            _windowStyle.normal.textColor = new Color(0.9f, 0.85f, 0.7f);
            _windowStyle.fontSize = F(13);
            _windowStyle.fontStyle = FontStyle.Bold;
            _windowStyle.padding = new RectOffset((int)U(6), (int)U(6), (int)U(22), (int)U(4));

            _round = new GUIStyle(GUI.skin.label) { fontSize = F(12), fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.95f, 0.85f, 0.4f) } };
            _buff = new GUIStyle(GUI.skin.label) { fontSize = F(11), fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.35f, 0.9f, 0.95f) } };
            _debuff = new GUIStyle(GUI.skin.label) { fontSize = F(11), fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.45f, 0.85f) } };
            _status = new GUIStyle(GUI.skin.label) { fontSize = F(11), fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.9f, 0.85f, 0.55f) } };
            _summary = new GUIStyle(GUI.skin.label) { fontSize = F(11), normal = { textColor = new Color(0.75f, 0.78f, 0.82f) } };
            _pn = new GUIStyle(GUI.skin.label) { fontSize = F(11), fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.4f, 0.7f, 1f) } };
            _en = new GUIStyle(GUI.skin.label) { fontSize = F(11), fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.4f, 0.4f) } };
            _nm = new GUIStyle(GUI.skin.label) { fontSize = F(11), normal = { textColor = new Color(0.8f, 0.8f, 0.8f) } };
            _resizeStyle = new GUIStyle(GUI.skin.label) { fontSize = F(11), normal = { textColor = new Color(0.6f, 0.6f, 0.6f, 0.8f) } };
            _styleVersion = settingsVersion;
            _init = true;
        }

        public void Draw()
        {
            Init();
            UpdateScaleFactor();
            _w = Mathf.Max(U(MIN_W), _w);
            _h = Mathf.Max(U(MIN_H), _h);
            _rect.width = _w;
            _rect.height = _h;

            var prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scaleFactor, _scaleFactor, 1f));

            _rect = GUI.Window(729004, _rect, Win, DmText.T("buffLogTitle"), _windowStyle);
            _rect = UiUtil.ClampToScreen(_rect, _scaleFactor);

            GUI.matrix = prevMatrix;

            var e = Event.current;
            float mx = e.mousePosition.x / _scaleFactor;
            float my = e.mousePosition.y / _scaleFactor;
            float resizeHandle = U(RH);
            var rr = new Rect(_rect.xMax - resizeHandle, _rect.yMax - resizeHandle, resizeHandle, resizeHandle);
            if (e.type == EventType.MouseDown && e.button == 0 && rr.Contains(new Vector2(mx, my))) { _rs = true; _rsS = new Vector2(mx, my); _rsW = _w; _rsH = _h; e.Use(); }
            else if (_rs && e.type == EventType.MouseDrag) { _w = Mathf.Max(U(MIN_W), _rsW + (mx - _rsS.x)); _h = Mathf.Max(U(MIN_H), _rsH + (my - _rsS.y)); _rect.width = _w; _rect.height = _h; _rect = UiUtil.ClampToScreen(_rect, _scaleFactor); e.Use(); }
            else if (_rs && e.type == EventType.MouseUp) _rs = false;
        }

        private void Win(int id)
        {
            DrawStatusSummary();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(_h - U(48f)));
            {
                var entries = _tracker.StatusEntries;
                bool hasStatusEntry = false;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i] is CombatLogTracker.LogEntry) { hasStatusEntry = true; break; }
                }

                if (!hasStatusEntry) { GUILayout.Label(DmText.T("noStatusLog"), _nm); }
                else
                {
                    if (_tracker.IsStatusDirty) { _scroll.y = float.MaxValue; _tracker.ClearStatusDirty(); }
                    for (int i = 0; i < entries.Count; i++)
                    {
                        if (entries[i] is CombatLogTracker.RoundHeader rh) GUILayout.Label(DmText.Format("round", rh.Round), _round);
                        else if (entries[i] is CombatLogTracker.LogEntry le) DrawEntry(le);
                    }
                }
            }
            GUILayout.EndScrollView();
            GUI.Label(new Rect(_w - U(RH) - U(2), _h - U(RH) - U(2), U(RH), U(RH)), "\u255a", _resizeStyle);
            GUI.DragWindow(new Rect(0, 0, _w, _h - U(RH)));
        }

        private void DrawStatusSummary()
        {
            var totals = _tracker.GetStatusTotalsSnapshot();
            if (!totals.HasAny) return;
            GUILayout.Label(
                DmText.Format("statusSummary",
                    totals.PlayerBuffApplied,
                    totals.PlayerDebuffApplied,
                    totals.PlayerStatusRemoved,
                    totals.PlayerStatusConsumed,
                    totals.EnemyBuffApplied,
                    totals.EnemyDebuffApplied,
                    totals.EnemyStatusRemoved,
                    totals.EnemyStatusConsumed),
                _summary);
        }

        private void DrawEntry(CombatLogTracker.LogEntry le)
        {
            GUILayout.BeginHorizontal();
            {
                if (!string.IsNullOrEmpty(le.SourceName))
                {
                    if (le.SourceName.StartsWith("[")) GUILayout.Label(le.SourceName, _status, GUILayout.Width(U(70)));
                    else GUILayout.Label(le.SourceName, le.SourceIsPlayer ? _pn : _en, GUILayout.Width(U(110)));
                }
                else GUILayout.Space(U(110));

                GUIStyle s; string t;
                switch (le.ActionType)
                {
                    case "BUFF+":
                    case "BUFF-":
                    case "BUFF!": s = _buff; break;
                    case "DEBUFF+":
                    case "DEBUFF-":
                    case "DEBUFF!": s = _debuff; break;
                    case "TOKEN+":
                    case "TOKEN-":
                    case "TOKEN!":
                    case "TOKEN~":
                    case "TOKENx":
                    case "STATUS+":
                    case "STATUS-": s = _status; break;
                    default: s = _nm; break;
                }
                t = DmText.ActionLabel(le.ActionType, le.Value, le.DotType);
                GUILayout.Label(t, s, GUILayout.Width(U(92)));
                GUILayout.Label("->", _nm, GUILayout.Width(U(20)));
                GUILayout.Label(le.TargetName ?? "?", le.TargetIsPlayer ? _pn : _en, GUILayout.Width(U(110)));

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
