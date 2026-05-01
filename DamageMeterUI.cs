using System;
using UnityEngine;

namespace DD2DamageMeter
{
    public class DamageMeterUI
    {
        private float _windowWidth = 760f;
        private float _windowHeight = 300f;
        private const float MIN_WIDTH = 620f;
        private const float MIN_HEIGHT = 200f;
        private const float ROW_HEIGHT = 22f;
        private const float RESIZE_HANDLE = 16f;
        private const float EDGE_MARGIN = 10f;
        private const float HEADER_HEIGHT = 20f;

        private readonly DamageTracker _tracker;
        private readonly ContributionTracker _contributionTracker;
        private Rect _windowRect = new Rect(10f, 10f, 760f, 300f);
        private bool _showPlayerTeam = true;
        private Vector2 _scrollPos;
        private bool _isResizing;
        private Vector2 _resizeStart;
        private float _resizeStartW, _resizeStartH;

        private GUIStyle _headerStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _toggleStyle;
        private GUIStyle _windowStyle;
        private GUIStyle _totalStyle;
        private GUIStyle _resizeStyle;
        private bool _stylesInitialized;

        // Textures for semi-transparent backgrounds
        private Texture2D _windowBgTex;
        private Texture2D _headerBgTex;
        private Texture2D _rowAltTex;

        private static readonly string[] ColNames = { "Name", "DMG", "DOT", "RawTkn", "Heal+", "HealIn", "Kills", "Crits", "Avoid%", "Contrib", "%DMG" };
        // Fixed widths for non-name columns (indices 1-10)
        private static readonly float[] FixedColWidths = { 56f, 42f, 68f, 50f, 54f, 38f, 38f, 58f, 62f, 48f };

        public bool IsVisible { get; set; } = true;
        public Action OnToggleLog;
        public Action OnToggleRecording;
        public Action OnShowRunStats;
        public Action OnExportCsv;
        public Func<bool> IsRecording;
        public Func<int> BattleCount;

        // Scale factor based on screen resolution
        private float _scaleFactor = 1f;
        private int _lastScreenHeight;

        public DamageMeterUI(DamageTracker tracker, ContributionTracker contributionTracker = null)
        {
            _tracker = tracker;
            _contributionTracker = contributionTracker;
        }

        private void UpdateScaleFactor()
        {
            if (Screen.height == _lastScreenHeight) return;
            _lastScreenHeight = Screen.height;
            // Base design at 1080p; scale up for higher resolutions
            _scaleFactor = Mathf.Max(1f, Screen.height / 1080f);
        }

        private Texture2D MakeTex(int width, int height, Color color)
        {
            var pix = new Color[width * height];
            for (int i = 0; i < pix.Length; i++) pix[i] = color;
            var tex = new Texture2D(width, height);
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            // Create semi-transparent textures
            _windowBgTex = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.35f));
            _headerBgTex = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.18f));
            _rowAltTex = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.1f));

            // Window style with semi-transparent background
            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _windowBgTex;
            _windowStyle.onNormal.background = _windowBgTex;
            _windowStyle.focused.background = _windowBgTex;
            _windowStyle.onFocused.background = _windowBgTex;
            _windowStyle.normal.textColor = new Color(0.9f, 0.85f, 0.7f);
            _windowStyle.fontSize = 13;
            _windowStyle.fontStyle = FontStyle.Bold;
            _windowStyle.padding = new RectOffset(6, 6, 22, 4);

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.95f, 0.85f, 0.4f) }
            };

            _totalStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.95f, 0.85f, 0.4f) }
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white },
                clipping = TextClipping.Overflow
            };

            _valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };

            _toggleStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };

            _resizeStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f, 0.8f) }
            };

            _stylesInitialized = true;
        }

        private float[] GetColWidths()
        {
            float fixedW = 0f;
            foreach (var w in FixedColWidths) fixedW += w;
            // Subtract margins + window chrome padding + scrollbar + safety
            float nameW = _windowWidth - EDGE_MARGIN * 2 - 30f - fixedW;
            if (nameW < 100f) nameW = 100f;
            float[] widths = new float[ColNames.Length];
            widths[0] = nameW;
            for (int i = 0; i < FixedColWidths.Length; i++) widths[i + 1] = FixedColWidths[i];
            return widths;
        }

        public void Draw()
        {
            InitStyles();
            UpdateScaleFactor();
            _tracker.RefreshSnapshot();
            _contributionTracker?.RefreshSnapshot();

            // Apply scale matrix
            var prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scaleFactor, _scaleFactor, 1f));

            // Adjust window rect for scaled coordinates
            _windowRect = GUI.Window(729001, _windowRect, DrawWindow, "<b>DD2 Damage Meter</b>  [F2] Hide  [F3] Reset  [F4] Export", _windowStyle);
            _windowRect = UiUtil.ClampToScreen(_windowRect, _scaleFactor);

            GUI.matrix = prevMatrix;
            HandleResize();
        }

        private void HandleResize()
        {
            Event e = Event.current;
            // Scale mouse position for resize detection
            float mx = e.mousePosition.x / _scaleFactor;
            float my = e.mousePosition.y / _scaleFactor;
            Rect resizeRect = new Rect(_windowRect.xMax - RESIZE_HANDLE, _windowRect.yMax - RESIZE_HANDLE, RESIZE_HANDLE, RESIZE_HANDLE);
            if (e.type == EventType.MouseDown && e.button == 0 && resizeRect.Contains(new Vector2(mx, my)))
            {
                _isResizing = true;
                _resizeStart = new Vector2(mx, my);
                _resizeStartW = _windowWidth;
                _resizeStartH = _windowHeight;
                e.Use();
            }
            else if (_isResizing && e.type == EventType.MouseDrag)
            {
                _windowWidth = Mathf.Max(MIN_WIDTH, _resizeStartW + (mx - _resizeStart.x));
                _windowHeight = Mathf.Max(MIN_HEIGHT, _resizeStartH + (my - _resizeStart.y));
                _windowRect.width = _windowWidth;
                _windowRect.height = _windowHeight;
                _windowRect = UiUtil.ClampToScreen(_windowRect, _scaleFactor);
                e.Use();
            }
            else if (_isResizing && e.type == EventType.MouseUp)
            {
                _isResizing = false;
            }
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();
            {
                // Tab buttons row
                GUILayout.BeginHorizontal();
                {
                    Color prevBg = GUI.backgroundColor;
                    GUI.backgroundColor = _showPlayerTeam ? new Color(0.3f, 0.6f, 0.9f) : new Color(0.4f, 0.4f, 0.4f);
                    if (GUILayout.Button("Heroes", _toggleStyle, GUILayout.Width(_windowWidth / 2f - 12))) _showPlayerTeam = true;
                    GUI.backgroundColor = !_showPlayerTeam ? new Color(0.9f, 0.3f, 0.3f) : new Color(0.4f, 0.4f, 0.4f);
                    if (GUILayout.Button("Enemies", _toggleStyle, GUILayout.Width(_windowWidth / 2f - 56))) _showPlayerTeam = false;
                    GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
                    if (GUILayout.Button("Log", _toggleStyle, GUILayout.Width(40))) OnToggleLog?.Invoke();
                    GUI.backgroundColor = prevBg;
                }
                GUILayout.EndHorizontal();

                // Action buttons row
                GUILayout.BeginHorizontal();
                {
                    bool recording = IsRecording?.Invoke() ?? false;
                    Color prevBg2 = GUI.backgroundColor;
                    GUI.backgroundColor = recording ? new Color(0.9f, 0.3f, 0.3f) : new Color(0.4f, 0.4f, 0.4f);
                    string recLabel = recording ? $"Recording ({BattleCount?.Invoke() ?? 0})" : "Record Run";
                    if (GUILayout.Button(recLabel, _toggleStyle, GUILayout.Width(160))) OnToggleRecording?.Invoke();
                    GUI.backgroundColor = new Color(0.6f, 0.8f, 0.6f);
                    if (GUILayout.Button("Run Stats", _toggleStyle, GUILayout.Width(90))) OnShowRunStats?.Invoke();
                    GUI.backgroundColor = new Color(0.6f, 0.7f, 0.9f);
                    if (GUILayout.Button("Export CSV", _toggleStyle, GUILayout.Width(100))) OnExportCsv?.Invoke();
                    GUI.backgroundColor = prevBg2;
                }
                GUILayout.EndHorizontal();

                var stats = _showPlayerTeam ? _tracker.PlayerStats : _tracker.EnemyStats;
                float totalDmg = _showPlayerTeam ? _tracker.PlayerTotalDamage : _tracker.EnemyTotalDamage;
                GUILayout.Label($"Total Damage: {totalDmg:F0}", _totalStyle);

                float[] cw = GetColWidths();

                // Draw column headers using manual Rect positioning (same as data rows)
                Rect headerRect = GUILayoutUtility.GetRect(_windowWidth - RESIZE_HANDLE, HEADER_HEIGHT);
                // Draw header background
                GUI.color = new Color(1f, 1f, 1f, 1f);
                GUI.DrawTexture(new Rect(headerRect.x, headerRect.y, headerRect.width, headerRect.height), _headerBgTex);
                GUI.color = Color.white;

                float hx = headerRect.x + EDGE_MARGIN;
                for (int i = 0; i < ColNames.Length; i++)
                {
                    GUI.Label(new Rect(hx, headerRect.y, cw[i], headerRect.height), ColNames[i], _headerStyle);
                    hx += cw[i];
                }

                // Scroll area
                float scrollH = _windowHeight - 150f;
                if (scrollH < 60f) scrollH = 60f;
                _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(scrollH));
                {
                    if (stats == null || stats.Count == 0)
                    {
                        GUILayout.Label("Stats reset each battle", _labelStyle);
                    }
                    else
                    {
                        float maxDmg = 1f;
                        foreach (var s in stats) if (s.TotalDamageDealt > maxDmg) maxDmg = s.TotalDamageDealt;
                        for (int i = 0; i < stats.Count; i++) DrawActorRow(stats[i], totalDmg > 0 ? totalDmg : 1f, maxDmg, cw, i);
                    }
                    if (_showPlayerTeam) DrawContributionSection();
                }
                GUILayout.EndScrollView();

                // Resize handle
                GUI.Label(new Rect(_windowWidth - RESIZE_HANDLE - 2, _windowHeight - RESIZE_HANDLE - 2, RESIZE_HANDLE, RESIZE_HANDLE), "\u255a", _resizeStyle);
            }
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, _windowWidth, _windowHeight - RESIZE_HANDLE));
        }

        private void DrawActorRow(DamageTracker.ActorStats s, float teamTotalDmg, float maxDmg, float[] cw, int rowIndex)
        {
            float dmgPct = teamTotalDmg > 0 ? s.TotalDamageDealt / teamTotalDmg * 100f : 0f;
            float barPct = maxDmg > 0 ? s.TotalDamageDealt / maxDmg : 0f;
            Rect row = GUILayoutUtility.GetRect(_windowWidth - RESIZE_HANDLE, ROW_HEIGHT);

            // Alternate row background for readability
            if (rowIndex % 2 == 1)
            {
                GUI.DrawTexture(new Rect(row.x, row.y, row.width, row.height), _rowAltTex);
            }

            // Damage bar
            Color bc = _showPlayerTeam ? new Color(0.2f, 0.4f, 0.8f, 0.35f) : new Color(0.8f, 0.2f, 0.2f, 0.35f);
            if (barPct > 0f)
            {
                GUI.color = bc;
                GUI.DrawTexture(new Rect(row.x + EDGE_MARGIN, row.y, (row.width - EDGE_MARGIN * 2) * barPct, row.height), Texture2D.whiteTexture);
                GUI.color = Color.white;
            }

            float x = row.x + EDGE_MARGIN, y = row.y, h = row.height;
            string dn = s.ActorName ?? $"#{s.ActorGuid}";
            GUI.Label(new Rect(x, y, cw[0], h), dn, _labelStyle); x += cw[0];
            GUI.Label(new Rect(x, y, cw[1], h), $"{s.TotalDamageDealt:F0}", _valueStyle); x += cw[1];
            GUI.Label(new Rect(x, y, cw[2], h), s.DotDamageDealt > 0 ? $"{s.DotDamageDealt:F0}" : "-", _valueStyle); x += cw[2];
            // Show raw (pre-shield) damage; if shielded, show "raw(actual)" format
            string takenStr = UiUtil.FormatDamageTaken(s.RawDamageReceived, s.TotalDamageReceived);
            GUI.Label(new Rect(x, y, cw[3], h), takenStr, _valueStyle); x += cw[3];
            GUI.Label(new Rect(x, y, cw[4], h), s.TotalHealingDone > 0 ? $"{s.TotalHealingDone:F0}" : "-", _valueStyle); x += cw[4];
            GUI.Label(new Rect(x, y, cw[5], h), s.TotalHealingReceived > 0 ? $"{s.TotalHealingReceived:F0}" : "-", _valueStyle); x += cw[5];
            GUI.Label(new Rect(x, y, cw[6], h), s.Kills > 0 ? $"{s.Kills}" : "-", _valueStyle); x += cw[6];
            GUI.Label(new Rect(x, y, cw[7], h), s.Crits > 0 ? $"{s.Crits}" : "-", _valueStyle); x += cw[7];
            GUI.Label(new Rect(x, y, cw[8], h), UiUtil.FormatAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks), _valueStyle); x += cw[8];
            float contribution = GetContributionTotal(s.ActorGuid, s.ActorName);
            GUI.Label(new Rect(x, y, cw[9], h), _showPlayerTeam && contribution > 0.01f ? $"{contribution:F1}" : "-", _valueStyle); x += cw[9];
            GUI.Label(new Rect(x, y, cw[10], h), $"{dmgPct:F1}%", _valueStyle);
        }

        private void DrawContributionSection()
        {
            if (_contributionTracker == null) return;
            var rows = _contributionTracker.PlayerStats;
            if (rows == null || rows.Count == 0) return;

            float total = 0f;
            bool hasAny = false;
            for (int i = 0; i < rows.Count; i++)
            {
                total += rows[i].TotalContribution;
                if (rows[i].TotalContribution > 0.01f || rows[i].ShieldWasted > 0)
                    hasAny = true;
            }
            if (!hasAny) return;

            GUILayout.Space(8);
            GUILayout.Label("Contribution", _totalStyle);

            const float contribW = 62f;
            const float bonusW = 54f;
            const float shieldW = 56f;
            const float guardW = 52f;
            const float wasteW = 40f;
            const float pctW = 46f;
            float nameW = _windowWidth - EDGE_MARGIN * 2 - 30f - contribW - bonusW - shieldW - guardW - wasteW - pctW;
            if (nameW < 120f) nameW = 120f;

            Rect headerRect = GUILayoutUtility.GetRect(_windowWidth - RESIZE_HANDLE, HEADER_HEIGHT);
            GUI.DrawTexture(new Rect(headerRect.x, headerRect.y, headerRect.width, headerRect.height), _headerBgTex);
            float hx = headerRect.x + EDGE_MARGIN;
            GUI.Label(new Rect(hx, headerRect.y, nameW, headerRect.height), "Name", _headerStyle); hx += nameW;
            GUI.Label(new Rect(hx, headerRect.y, contribW, headerRect.height), "Contrib", _headerStyle); hx += contribW;
            GUI.Label(new Rect(hx, headerRect.y, bonusW, headerRect.height), "Dmg+", _headerStyle); hx += bonusW;
            GUI.Label(new Rect(hx, headerRect.y, shieldW, headerRect.height), "Shield", _headerStyle); hx += shieldW;
            GUI.Label(new Rect(hx, headerRect.y, guardW, headerRect.height), "Guard", _headerStyle); hx += guardW;
            GUI.Label(new Rect(hx, headerRect.y, wasteW, headerRect.height), "W", _headerStyle); hx += wasteW;
            GUI.Label(new Rect(hx, headerRect.y, pctW, headerRect.height), "%", _headerStyle);

            int drawn = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var s = rows[i];
                if (s.TotalContribution <= 0.01f && s.ShieldWasted <= 0) continue;
                Rect row = GUILayoutUtility.GetRect(_windowWidth - RESIZE_HANDLE, ROW_HEIGHT);
                if (drawn % 2 == 1) GUI.DrawTexture(new Rect(row.x, row.y, row.width, row.height), _rowAltTex);

                float x = row.x + EDGE_MARGIN, y = row.y, h = row.height;
                string nm = s.ActorName ?? $"#{s.ActorGuid}";
                GUI.Label(new Rect(x, y, nameW, h), nm, _labelStyle); x += nameW;
                GUI.Label(new Rect(x, y, contribW, h), s.TotalContribution > 0 ? $"{s.TotalContribution:F1}" : "-", _valueStyle); x += contribW;
                GUI.Label(new Rect(x, y, bonusW, h), s.BonusDamage > 0 ? $"{s.BonusDamage:F1}" : "-", _valueStyle); x += bonusW;
                GUI.Label(new Rect(x, y, shieldW, h), s.ShieldPrevented > 0 ? $"{s.ShieldPrevented:F1}" : "-", _valueStyle); x += shieldW;
                GUI.Label(new Rect(x, y, guardW, h), s.GuardProtected > 0 ? $"{s.GuardProtected:F1}" : "-", _valueStyle); x += guardW;
                GUI.Label(new Rect(x, y, wasteW, h), s.ShieldWasted > 0 ? $"{s.ShieldWasted}" : "-", _valueStyle); x += wasteW;
                float pct = total > 0f ? s.TotalContribution / total * 100f : 0f;
                GUI.Label(new Rect(x, y, pctW, h), $"{pct:F1}%", _valueStyle);
                drawn++;
            }
        }

        private float GetContributionTotal(uint actorGuid, string actorName)
        {
            if (_contributionTracker == null) return 0f;
            var rows = _contributionTracker.PlayerStats;
            if (rows == null) return 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                var s = rows[i];
                if (s.ActorGuid == actorGuid) return s.TotalContribution;
                if (!string.IsNullOrEmpty(actorName) && string.Equals(s.ActorName, actorName, StringComparison.OrdinalIgnoreCase))
                    return s.TotalContribution;
            }
            return 0f;
        }
    }
}
