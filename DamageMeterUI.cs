using System;
using System.Collections.Generic;
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
        private GUIStyle _checkStyle;
        private bool _stylesInitialized;

        // Textures for semi-transparent backgrounds
        private Texture2D _windowBgTex;
        private Texture2D _headerBgTex;
        private Texture2D _rowAltTex;

        private static readonly string[] ColKeys = { "name", "dmg", "dot", "rawTkn", "healOut", "healIn", "kills", "crits", "avoidPct", "comboApplied", "contrib", "pct" };
        // Fixed widths for non-name columns (indices 1-10)
        private static readonly float[] FixedColWidths = { 56f, 42f, 68f, 50f, 54f, 38f, 38f, 58f, 54f, 62f, 48f };

        public bool IsVisible { get; set; } = true;
        public Action OnToggleLog;
        public Action OnToggleRecording;
        public Action OnShowRunStats;
        public Action OnExportCsv;
        public Func<bool> IsRecording;
        public Func<int> BattleCount;
        public Func<bool> IsAutoRecordingEnabled;
        public Action<bool> OnAutoRecordingChanged;
        public Func<string> GetExportDirectory;
        public Action<string> OnExportDirectoryChanged;
        public Func<string> GetLanguage;
        public Action<string> OnLanguageChanged;

        private Rect _settingsRect = new Rect(20f, 320f, 540f, 160f);
        private bool _showSettings;
        private bool _exportDirectoryEditInitialized;
        private string _exportDirectoryEdit = "";
        private string _settingsMessage = "";
        private bool _remoteMode;
        private DamageMeterMpSnapshot _remoteSnapshot;

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

            _checkStyle = new GUIStyle(GUI.skin.toggle)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                onNormal = { textColor = Color.white },
                hover = { textColor = Color.white },
                onHover = { textColor = Color.white }
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
            float[] widths = new float[ColKeys.Length];
            widths[0] = nameW;
            for (int i = 0; i < FixedColWidths.Length; i++) widths[i + 1] = FixedColWidths[i];
            return widths;
        }

        public void Draw()
        {
            InitStyles();
            UpdateScaleFactor();
            _remoteMode = DamageMeterMultiplayerApi.TryGetRemoteSnapshot(out _remoteSnapshot);
            if (!_remoteMode)
            {
                _tracker.RefreshSnapshot();
                _contributionTracker?.RefreshSnapshot();
            }

            // Apply scale matrix
            var prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scaleFactor, _scaleFactor, 1f));

            // Adjust window rect for scaled coordinates
            string title = _remoteMode
                ? $"{DmText.T("damageMeterTitle")}  [{DmText.T("remoteHost")}]  [{DmText.T("hideHint")}]"
                : $"{DmText.T("damageMeterTitle")}  [{DmText.T("hideHint")}]  [{DmText.T("resetHint")}]  [{DmText.T("exportHint")}]";
            _windowRect = GUI.Window(729001, _windowRect, DrawWindow, title, _windowStyle);
            _windowRect = UiUtil.ClampToScreen(_windowRect, _scaleFactor);
            if (_showSettings)
            {
                _settingsRect = GUI.Window(729005, _settingsRect, DrawSettingsWindow, DmText.T("settingsTitle"), _windowStyle);
                _settingsRect = UiUtil.ClampToScreen(_settingsRect, _scaleFactor);
            }

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
            bool remoteMode = _remoteMode && _remoteSnapshot != null;
            GUILayout.BeginVertical();
            {
                // Tab buttons row
                GUILayout.BeginHorizontal();
                {
                    Color prevBg = GUI.backgroundColor;
                    GUI.backgroundColor = _showPlayerTeam ? new Color(0.3f, 0.6f, 0.9f) : new Color(0.4f, 0.4f, 0.4f);
                    if (GUILayout.Button(DmText.T("heroes"), _toggleStyle, GUILayout.Width(_windowWidth / 2f - 12))) _showPlayerTeam = true;
                    GUI.backgroundColor = !_showPlayerTeam ? new Color(0.9f, 0.3f, 0.3f) : new Color(0.4f, 0.4f, 0.4f);
                    if (GUILayout.Button(DmText.T("enemies"), _toggleStyle, GUILayout.Width(_windowWidth / 2f - 56))) _showPlayerTeam = false;
                    GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
                    if (GUILayout.Button(DmText.T("log"), _toggleStyle, GUILayout.Width(40))) OnToggleLog?.Invoke();
                    GUI.backgroundColor = prevBg;
                }
                GUILayout.EndHorizontal();

                // Action buttons row
                GUILayout.BeginHorizontal();
                {
                    Color prevBg2 = GUI.backgroundColor;
                    if (remoteMode)
                    {
                        bool recording = IsRecording?.Invoke() ?? false;
                        GUI.backgroundColor = recording ? new Color(0.9f, 0.3f, 0.3f) : new Color(0.4f, 0.4f, 0.4f);
                        string recLabel = recording ? DmText.Format("recording", BattleCount?.Invoke() ?? 0) : DmText.T("recordRun");
                        if (GUILayout.Button(recLabel, _toggleStyle, GUILayout.Width(150))) OnToggleRecording?.Invoke();
                        GUI.backgroundColor = new Color(0.6f, 0.8f, 0.6f);
                        if (GUILayout.Button(DmText.T("runStats"), _toggleStyle, GUILayout.Width(85))) OnShowRunStats?.Invoke();
                        GUI.backgroundColor = new Color(0.6f, 0.7f, 0.9f);
                        if (GUILayout.Button(DmText.T("exportCsv"), _toggleStyle, GUILayout.Width(95))) OnExportCsv?.Invoke();
                        GUI.backgroundColor = new Color(0.45f, 0.55f, 0.7f);
                        if (GUILayout.Button(DmText.T("exportDir"), _toggleStyle, GUILayout.Width(90)))
                        {
                            _showSettings = !_showSettings;
                            if (_showSettings) LoadExportDirectoryEdit();
                        }
                        GUI.backgroundColor = prevBg2;
                        GUILayout.Label($"r{_remoteSnapshot.Round}/t{_remoteSnapshot.Turn} {_remoteSnapshot.BattleState}", _labelStyle);
                    }
                    else
                    {
                        bool recording = IsRecording?.Invoke() ?? false;
                        GUI.backgroundColor = recording ? new Color(0.9f, 0.3f, 0.3f) : new Color(0.4f, 0.4f, 0.4f);
                        string recLabel = recording ? DmText.Format("recording", BattleCount?.Invoke() ?? 0) : DmText.T("recordRun");
                        if (GUILayout.Button(recLabel, _toggleStyle, GUILayout.Width(150))) OnToggleRecording?.Invoke();
                        GUI.backgroundColor = new Color(0.6f, 0.8f, 0.6f);
                        if (GUILayout.Button(DmText.T("runStats"), _toggleStyle, GUILayout.Width(85))) OnShowRunStats?.Invoke();
                        GUI.backgroundColor = new Color(0.6f, 0.7f, 0.9f);
                        if (GUILayout.Button(DmText.T("exportCsv"), _toggleStyle, GUILayout.Width(95))) OnExportCsv?.Invoke();
                        GUI.backgroundColor = prevBg2;
                        bool autoRecording = IsAutoRecordingEnabled?.Invoke() ?? false;
                        bool nextAutoRecording = GUILayout.Toggle(autoRecording, DmText.T("autoRec"), _checkStyle, GUILayout.Width(95));
                        if (nextAutoRecording != autoRecording) OnAutoRecordingChanged?.Invoke(nextAutoRecording);
                        if (GUILayout.Button(DmText.T("exportDir"), _toggleStyle, GUILayout.Width(90)))
                        {
                            _showSettings = !_showSettings;
                            if (_showSettings) LoadExportDirectoryEdit();
                        }
                    }
                    GUI.backgroundColor = prevBg2;
                }
                GUILayout.EndHorizontal();

                if (remoteMode && !_remoteSnapshot.IsAvailable)
                {
                    GUILayout.Label(DmText.Format("remoteUnavailable", _remoteSnapshot.UnavailableReason ?? DmText.T("unknown")), _labelStyle);
                    GUILayout.EndVertical();
                    GUI.DragWindow(new Rect(0, 0, _windowWidth, _windowHeight - RESIZE_HANDLE));
                    return;
                }

                List<DisplayActorStats> stats = remoteMode
                    ? BuildRemoteActorRows(_showPlayerTeam ? _remoteSnapshot.Heroes : _remoteSnapshot.Enemies)
                    : BuildLocalActorRows(_showPlayerTeam ? _tracker.PlayerStats : _tracker.EnemyStats);
                float totalDmg = remoteMode
                    ? (_showPlayerTeam ? _remoteSnapshot.PlayerTotalDamage : _remoteSnapshot.EnemyTotalDamage)
                    : (_showPlayerTeam ? _tracker.PlayerTotalDamage : _tracker.EnemyTotalDamage);
                GUILayout.Label(DmText.Format("totalDamage", totalDmg), _totalStyle);

                float[] cw = GetColWidths();

                // Draw column headers using manual Rect positioning (same as data rows)
                Rect headerRect = GUILayoutUtility.GetRect(_windowWidth - RESIZE_HANDLE, HEADER_HEIGHT);
                // Draw header background
                GUI.color = new Color(1f, 1f, 1f, 1f);
                GUI.DrawTexture(new Rect(headerRect.x, headerRect.y, headerRect.width, headerRect.height), _headerBgTex);
                GUI.color = Color.white;

                float hx = headerRect.x + EDGE_MARGIN;
                for (int i = 0; i < ColKeys.Length; i++)
                {
                    GUI.Label(new Rect(hx, headerRect.y, cw[i], headerRect.height), DmText.T(ColKeys[i]), _headerStyle);
                    hx += cw[i];
                }

                // Scroll area
                float scrollH = _windowHeight - 150f;
                if (scrollH < 60f) scrollH = 60f;
                _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(scrollH));
                {
                    if (stats == null || stats.Count == 0)
                    {
                        GUILayout.Label(DmText.T("statsResetEachBattle"), _labelStyle);
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

        private void LoadExportDirectoryEdit()
        {
            _exportDirectoryEdit = GetExportDirectory?.Invoke() ?? "";
            _exportDirectoryEditInitialized = true;
            _settingsMessage = "";
        }

        private void DrawSettingsWindow(int id)
        {
            if (!_exportDirectoryEditInitialized) LoadExportDirectoryEdit();

            GUILayout.BeginVertical();
            {
                GUILayout.Label(DmText.T("exportDirectory"), _headerStyle);
                GUILayout.BeginHorizontal();
                {
                    _exportDirectoryEdit = GUILayout.TextField(_exportDirectoryEdit ?? "", GUILayout.Width(400));
                    if (GUILayout.Button(DmText.T("save"), _toggleStyle, GUILayout.Width(55)))
                    {
                        OnExportDirectoryChanged?.Invoke(_exportDirectoryEdit ?? "");
                        _settingsMessage = DmText.T("saved");
                    }
                    if (GUILayout.Button(DmText.T("reset"), _toggleStyle, GUILayout.Width(55)))
                    {
                        _exportDirectoryEdit = "";
                        OnExportDirectoryChanged?.Invoke("");
                        _settingsMessage = DmText.T("default");
                    }
                }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                {
                    GUILayout.Label(DmText.T("language"), _headerStyle, GUILayout.Width(110));
                    string languageLabel = DmText.Format("languageButton", GetLanguage?.Invoke() ?? DmText.LanguageDisplay());
                    if (GUILayout.Button(languageLabel, _toggleStyle, GUILayout.Width(140)))
                    {
                        OnLanguageChanged?.Invoke(DmText.ToggleLanguageValue());
                    }
                }
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(_settingsMessage))
                {
                    GUILayout.Label(_settingsMessage, _labelStyle);
                }
            }
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, _settingsRect.width, _settingsRect.height));
        }

        private void DrawActorRow(DisplayActorStats s, float teamTotalDmg, float maxDmg, float[] cw, int rowIndex)
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
            DisplayContributionStats contributionStats = FindContribution(s.ActorGuid, s.ActorGuidString, s.ActorName);
            float contribution = contributionStats != null ? contributionStats.TotalContribution : 0f;
            int comboApplied = contributionStats != null ? contributionStats.ComboApplied : 0;
            GUI.Label(new Rect(x, y, cw[9], h), _showPlayerTeam && comboApplied > 0 ? $"{comboApplied}" : "-", _valueStyle); x += cw[9];
            GUI.Label(new Rect(x, y, cw[10], h), _showPlayerTeam && contribution > 0.01f ? $"{contribution:F1}" : "-", _valueStyle); x += cw[10];
            GUI.Label(new Rect(x, y, cw[11], h), $"{dmgPct:F1}%", _valueStyle);
        }

        private void DrawContributionSection()
        {
            List<DisplayContributionStats> rows = _remoteMode && _remoteSnapshot != null
                ? BuildRemoteContributionRows(_remoteSnapshot.Contributions)
                : BuildLocalContributionRows(_contributionTracker == null ? null : _contributionTracker.PlayerStats);
            if (rows == null || rows.Count == 0) return;

            float total = 0f;
            bool hasAny = false;
            for (int i = 0; i < rows.Count; i++)
            {
                total += rows[i].TotalContribution;
                if (rows[i].TotalContribution > 0.01f || rows[i].ComboConsumed > 0)
                    hasAny = true;
            }
            if (!hasAny) return;

            GUILayout.Space(8);
            GUILayout.Label(DmText.T("contribution"), _totalStyle);

            const float contribW = 62f;
            const float bonusW = 54f;
            const float vulnerableW = 52f;
            const float shieldW = 56f;
            const float guardW = 52f;
            const float comboConsumedW = 58f;
            const float pctW = 46f;
            float nameW = _windowWidth - EDGE_MARGIN * 2 - 30f - contribW - bonusW - vulnerableW - shieldW - guardW - comboConsumedW - pctW;
            if (nameW < 120f) nameW = 120f;

            Rect headerRect = GUILayoutUtility.GetRect(_windowWidth - RESIZE_HANDLE, HEADER_HEIGHT);
            GUI.DrawTexture(new Rect(headerRect.x, headerRect.y, headerRect.width, headerRect.height), _headerBgTex);
            float hx = headerRect.x + EDGE_MARGIN;
            GUI.Label(new Rect(hx, headerRect.y, nameW, headerRect.height), DmText.T("name"), _headerStyle); hx += nameW;
            GUI.Label(new Rect(hx, headerRect.y, contribW, headerRect.height), DmText.T("contrib"), _headerStyle); hx += contribW;
            GUI.Label(new Rect(hx, headerRect.y, bonusW, headerRect.height), DmText.T("dmgPlus"), _headerStyle); hx += bonusW;
            GUI.Label(new Rect(hx, headerRect.y, vulnerableW, headerRect.height), DmText.T("vulnerableShort"), _headerStyle); hx += vulnerableW;
            GUI.Label(new Rect(hx, headerRect.y, shieldW, headerRect.height), DmText.T("shield"), _headerStyle); hx += shieldW;
            GUI.Label(new Rect(hx, headerRect.y, guardW, headerRect.height), DmText.T("guard"), _headerStyle); hx += guardW;
            GUI.Label(new Rect(hx, headerRect.y, comboConsumedW, headerRect.height), DmText.T("comboConsumed"), _headerStyle); hx += comboConsumedW;
            GUI.Label(new Rect(hx, headerRect.y, pctW, headerRect.height), DmText.T("pct"), _headerStyle);

            int drawn = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var s = rows[i];
                if (s.TotalContribution <= 0.01f && s.ComboConsumed <= 0) continue;
                Rect row = GUILayoutUtility.GetRect(_windowWidth - RESIZE_HANDLE, ROW_HEIGHT);
                if (drawn % 2 == 1) GUI.DrawTexture(new Rect(row.x, row.y, row.width, row.height), _rowAltTex);

                float x = row.x + EDGE_MARGIN, y = row.y, h = row.height;
                string nm = s.ActorName ?? $"#{s.ActorGuid}";
                GUI.Label(new Rect(x, y, nameW, h), nm, _labelStyle); x += nameW;
                GUI.Label(new Rect(x, y, contribW, h), s.TotalContribution > 0 ? $"{s.TotalContribution:F1}" : "-", _valueStyle); x += contribW;
                GUI.Label(new Rect(x, y, bonusW, h), s.BonusDamage > 0 ? $"{s.BonusDamage:F1}" : "-", _valueStyle); x += bonusW;
                GUI.Label(new Rect(x, y, vulnerableW, h), s.VulnerableDamage > 0 ? $"{s.VulnerableDamage:F1}" : "-", _valueStyle); x += vulnerableW;
                GUI.Label(new Rect(x, y, shieldW, h), s.ShieldPrevented > 0 ? $"{s.ShieldPrevented:F1}" : "-", _valueStyle); x += shieldW;
                GUI.Label(new Rect(x, y, guardW, h), s.GuardProtected > 0 ? $"{s.GuardProtected:F1}" : "-", _valueStyle); x += guardW;
                GUI.Label(new Rect(x, y, comboConsumedW, h), s.ComboConsumed > 0 ? $"{s.ComboConsumed}" : "-", _valueStyle); x += comboConsumedW;
                float pct = total > 0f ? s.TotalContribution / total * 100f : 0f;
                GUI.Label(new Rect(x, y, pctW, h), $"{pct:F1}%", _valueStyle);
                drawn++;
            }
        }

        private DisplayContributionStats FindContribution(uint actorGuid, string actorGuidString, string actorName)
        {
            List<DisplayContributionStats> rows = _remoteMode && _remoteSnapshot != null
                ? BuildRemoteContributionRows(_remoteSnapshot.Contributions)
                : BuildLocalContributionRows(_contributionTracker == null ? null : _contributionTracker.PlayerStats);
            if (rows == null) return null;
            for (int i = 0; i < rows.Count; i++)
            {
                var s = rows[i];
                if (s.ActorGuid == actorGuid) return s;
                if (!string.IsNullOrEmpty(actorGuidString) && string.Equals(s.ActorGuidString, actorGuidString, StringComparison.OrdinalIgnoreCase))
                    return s;
                if (!string.IsNullOrEmpty(actorName) && string.Equals(s.ActorName, actorName, StringComparison.OrdinalIgnoreCase))
                    return s;
            }
            return null;
        }

        private static List<DisplayActorStats> BuildLocalActorRows(IReadOnlyList<DamageTracker.ActorStats> rows)
        {
            List<DisplayActorStats> result = new List<DisplayActorStats>();
            if (rows == null) return result;
            for (int i = 0; i < rows.Count; i++)
            {
                DamageTracker.ActorStats s = rows[i];
                if (s == null) continue;
                result.Add(new DisplayActorStats
                {
                    ActorGuid = s.ActorGuid,
                    ActorGuidString = s.ActorGuid.ToString(),
                    ActorName = s.ActorName,
                    TotalDamageDealt = s.TotalDamageDealt,
                    DotDamageDealt = s.DotDamageDealt,
                    TotalDamageReceived = s.TotalDamageReceived,
                    RawDamageReceived = s.RawDamageReceived,
                    TotalHealingDone = s.TotalHealingDone,
                    TotalHealingReceived = s.TotalHealingReceived,
                    Kills = s.Kills,
                    Crits = s.Crits,
                    IncomingAttacks = s.IncomingAttacks,
                    AvoidedAttacks = s.AvoidedAttacks,
                });
            }
            return result;
        }

        private static List<DisplayActorStats> BuildRemoteActorRows(System.Collections.Generic.IList<DamageMeterMpActorStats> rows)
        {
            List<DisplayActorStats> result = new List<DisplayActorStats>();
            if (rows == null) return result;
            for (int i = 0; i < rows.Count; i++)
            {
                DamageMeterMpActorStats s = rows[i];
                if (s == null) continue;
                uint guid;
                uint.TryParse(s.ActorGuid, out guid);
                result.Add(new DisplayActorStats
                {
                    ActorGuid = guid,
                    ActorGuidString = s.ActorGuid,
                    ActorName = s.ActorName,
                    TotalDamageDealt = s.TotalDamageDealt,
                    DotDamageDealt = s.DotDamageDealt,
                    TotalDamageReceived = s.TotalDamageReceived,
                    RawDamageReceived = s.RawDamageReceived,
                    TotalHealingDone = s.TotalHealingDone,
                    TotalHealingReceived = s.TotalHealingReceived,
                    Kills = s.Kills,
                    Crits = s.Crits,
                    IncomingAttacks = s.IncomingAttacks,
                    AvoidedAttacks = s.AvoidedAttacks,
                });
            }
            return result;
        }

        private static List<DisplayContributionStats> BuildLocalContributionRows(IReadOnlyList<ContributionTracker.ContributionStats> rows)
        {
            List<DisplayContributionStats> result = new List<DisplayContributionStats>();
            if (rows == null) return result;
            for (int i = 0; i < rows.Count; i++)
            {
                ContributionTracker.ContributionStats s = rows[i];
                if (s == null) continue;
                result.Add(new DisplayContributionStats
                {
                    ActorGuid = s.ActorGuid,
                    ActorGuidString = s.ActorGuid.ToString(),
                    ActorName = s.ActorName,
                    BonusDamage = s.BonusDamage,
                    VulnerableDamage = s.VulnerableDamage,
                    ShieldPrevented = s.ShieldPrevented,
                    GuardProtected = s.GuardProtected,
                    ShieldWasted = s.ShieldWasted,
                    ComboApplied = s.ComboApplied,
                    ComboConsumed = s.ComboConsumed,
                    TotalContribution = s.TotalContribution,
                });
            }
            return result;
        }

        private static List<DisplayContributionStats> BuildRemoteContributionRows(System.Collections.Generic.IList<DamageMeterMpContributionStats> rows)
        {
            List<DisplayContributionStats> result = new List<DisplayContributionStats>();
            if (rows == null) return result;
            for (int i = 0; i < rows.Count; i++)
            {
                DamageMeterMpContributionStats s = rows[i];
                if (s == null) continue;
                uint guid;
                uint.TryParse(s.ActorGuid, out guid);
                result.Add(new DisplayContributionStats
                {
                    ActorGuid = guid,
                    ActorGuidString = s.ActorGuid,
                    ActorName = s.ActorName,
                    BonusDamage = s.BonusDamage,
                    VulnerableDamage = s.VulnerableDamage,
                    ShieldPrevented = s.ShieldPrevented,
                    GuardProtected = s.GuardProtected,
                    ShieldWasted = s.ShieldWasted,
                    ComboApplied = s.ComboApplied,
                    ComboConsumed = s.ComboConsumed,
                    TotalContribution = s.TotalContribution,
                });
            }
            return result;
        }

        private sealed class DisplayActorStats
        {
            public uint ActorGuid;
            public string ActorGuidString;
            public string ActorName;
            public float TotalDamageDealt;
            public float DotDamageDealt;
            public float TotalDamageReceived;
            public float RawDamageReceived;
            public float TotalHealingDone;
            public float TotalHealingReceived;
            public int Kills;
            public int Crits;
            public int IncomingAttacks;
            public int AvoidedAttacks;
        }

        private sealed class DisplayContributionStats
        {
            public uint ActorGuid;
            public string ActorGuidString;
            public string ActorName;
            public float BonusDamage;
            public float VulnerableDamage;
            public float ShieldPrevented;
            public float GuardProtected;
            public int ShieldWasted;
            public int ComboApplied;
            public int ComboConsumed;
            public float TotalContribution;
        }
    }
}
