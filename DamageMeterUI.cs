using System;
using System.Collections.Generic;
using UnityEngine;

namespace DD2DamageMeter
{
    public class DamageMeterUI
    {
        // ── Tab system ──
        public enum Tab { Stats, CombatLog, BuffLog }
        private Tab _currentTab = Tab.Stats;

        // Per-tab preferred sizes (base values, processed through U())
        private float[] _tabWidths = { 760f, 760f, 760f };
        private float[] _tabHeights = { 350f, 430f, 450f };
        private float[] _tabMinWidths = { 620f, 560f, 560f };
        private float[] _tabMinHeights = { 200f, 220f, 240f };

        // ── Constants ──
        private const float ROW_HEIGHT = 22f;
        private const float RESIZE_HANDLE = 16f;
        private const float EDGE_MARGIN = 10f;
        private const float HEADER_HEIGHT = 20f;
        private const float TAB_BAR_HEIGHT = 24f;
        private const float TOOLBAR_HEIGHT = 28f; // buttons + thin separator
        private const float SUB_TAB_HEIGHT = 24f;
        private const float SETTINGS_PANEL_HEIGHT = 72f; // content + thin separator
        private const float CHROME_HEIGHT = 30f;
        private const float SCROLL_BOTTOM_MARGIN = 8f;

        // Fixed widths for non-name columns (indices 1-10)
        private static readonly string[] ColKeys = { "name", "dmg", "dot", "rawTkn", "healOut", "healIn", "kills", "crits", "avoidCount", "comboApplied", "contrib", "pct" };
        private static readonly float[] FixedColWidths = { 56f, 42f, 68f, 50f, 54f, 38f, 38f, 58f, 54f, 62f, 48f };

        // ── References ──
        private readonly DamageTracker _tracker;
        private readonly ContributionTracker _contributionTracker;
        private CombatLogUI _logUi;
        private StatusLogUI _statusLogUi;
        private List<DisplayActorStats> _retainedPlayerStats;
        private List<DisplayActorStats> _retainedEnemyStats;
        private List<DisplayContributionStats> _retainedContributionStats;

        // ── Window state ──
        private Rect _windowRect = new Rect(210f, 10f, 760f, 350f);
        private float _windowWidth = 760f;
        private float _windowHeight = 350f;
        private bool _showPlayerTeam = true;
        private Vector2 _scrollPos;
        private bool _isResizing;
        private Vector2 _resizeStart;
        private float _resizeStartW, _resizeStartH;

        // ── Styles ──
        private GUIStyle _headerStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _toggleStyle;
        private GUIStyle _windowStyle;
        private GUIStyle _totalStyle;
        private GUIStyle _checkStyle;
        private GUIStyle _tabActiveStyle;
        private GUIStyle _tabInactiveStyle;
        private bool _stylesInitialized;
        private int _styleVersion = -1;

        // ── Textures ──
        private Texture2D _windowBgTex;
        private Texture2D _headerBgTex;
        private Texture2D _rowAltTex;
        private Texture2D _heroRowTex;
        private Texture2D _enemyRowTex;
        private Texture2D _tabActiveTex;
        private Texture2D _tabInactiveTex;
        private Texture2D _toolbarBgTex;
        private Texture2D _settingsBgTex;

        // ── Settings panel ──
        private bool _showSettings;
        private bool _exportDirectoryEditInitialized;
        private string _exportDirectoryEdit = "";
        private string _settingsMessage = "";

        // ── Remote ──
        private bool _remoteMode;
        private DamageMeterMpSnapshot _remoteSnapshot;

        // ── Scale ──
        private float _scaleFactor = 1f;
        private int _lastScreenHeight;
        private int _lastUiSettingsVersion;

        // ── Callbacks ──
        public bool IsVisible { get; set; } = true;
        public Action OnToggleRecording;
        public Action OnShowRunStats;
        public Action OnExportCsv;
        public Func<bool> IsRecording;
        public Func<int> BattleCount;
        public Func<bool> IsAutoRecordingEnabled;
        public Action<bool> OnAutoRecordingChanged;
        public Func<bool> IsAutoShowInBattleEnabled;
        public Action<bool> OnAutoShowInBattleChanged;
        public Func<bool> IsAutoShowOutsideBattleEnabled;
        public Action<bool> OnAutoShowOutsideBattleChanged;
        public Func<string> GetExportDirectory;
        public Action<string> OnExportDirectoryChanged;
        public Func<string> GetLanguage;
        public Action<string> OnLanguageChanged;

        public DamageMeterUI(DamageTracker tracker, ContributionTracker contributionTracker = null,
            CombatLogUI logUi = null, StatusLogUI statusLogUi = null)
        {
            _tracker = tracker;
            _contributionTracker = contributionTracker;
            _logUi = logUi;
            _statusLogUi = statusLogUi;
        }

        internal void RetainCurrentStats()
        {
            _tracker.RefreshSnapshot();
            _contributionTracker?.RefreshSnapshot();
            _retainedPlayerStats = BuildLocalActorRows(_tracker.PlayerStats);
            _retainedEnemyStats = BuildLocalActorRows(_tracker.EnemyStats);
            _retainedContributionStats = BuildLocalContributionRows(_contributionTracker?.PlayerStats);
        }

        internal void ClearRetainedStats()
        {
            _retainedPlayerStats = null;
            _retainedEnemyStats = null;
            _retainedContributionStats = null;
        }

        public void SwitchToTab(Tab tab)
        {
            if (tab == _currentTab) return;
            // Save current tab's user-adjusted size
            _tabWidths[(int)_currentTab] = _windowWidth;
            _tabHeights[(int)_currentTab] = _windowHeight;
            _currentTab = tab;
            _windowWidth = Mathf.Max(U(_tabMinWidths[(int)tab]), _tabWidths[(int)tab]);
            _windowHeight = Mathf.Max(U(_tabMinHeights[(int)tab]), _tabHeights[(int)tab]);
            _windowRect.width = _windowWidth;
            _windowRect.height = _windowHeight;
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
            int settingsVersion = DamageMeterUiSettings.Version;
            if (_stylesInitialized && _styleVersion == settingsVersion) return;

            // ── Textures: DD2 gothic warm palette, all semi-transparent ──
            _windowBgTex = MakeTex(2, 2, new Color(0.06f, 0.05f, 0.04f, 0.55f));
            _headerBgTex = MakeTex(2, 2, new Color(0.12f, 0.10f, 0.06f, 0.3f));
            _rowAltTex = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.08f));
            _heroRowTex = MakeTex(2, 2, new Color(0.15f, 0.3f, 0.6f, 0.06f));
            _enemyRowTex = MakeTex(2, 2, new Color(0.6f, 0.15f, 0.15f, 0.06f));
            _tabActiveTex = MakeTex(2, 2, new Color(0.3f, 0.5f, 0.8f, 0.2f));
            _tabInactiveTex = MakeTex(2, 2, new Color(0.2f, 0.2f, 0.2f, 0.15f));
            _toolbarBgTex = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.15f));
            _settingsBgTex = MakeTex(2, 2, new Color(0.1f, 0.08f, 0.05f, 0.25f));

            // ── Window style ──
            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _windowBgTex;
            _windowStyle.onNormal.background = _windowBgTex;
            _windowStyle.focused.background = _windowBgTex;
            _windowStyle.onFocused.background = _windowBgTex;
            _windowStyle.normal.textColor = new Color(0.9f, 0.85f, 0.7f);
            _windowStyle.fontSize = F(13);
            _windowStyle.fontStyle = FontStyle.Bold;
            _windowStyle.padding = new RectOffset((int)U(6), (int)U(6), (int)U(22), (int)U(4));

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = F(12),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.95f, 0.85f, 0.4f) }
            };

            _totalStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = F(12),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.95f, 0.85f, 0.4f) }
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = F(11),
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white },
                clipping = TextClipping.Overflow
            };

            _valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = F(11),
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };

            _toggleStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = F(11),
                fontStyle = FontStyle.Bold
            };

            _checkStyle = new GUIStyle(GUI.skin.toggle)
            {
                fontSize = F(11),
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                onNormal = { textColor = Color.white },
                hover = { textColor = Color.white },
                onHover = { textColor = Color.white }
            };

            _tabActiveStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = F(11),
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.9f, 1f), background = _tabActiveTex },
                hover = { textColor = Color.white, background = _tabActiveTex }
            };

            _tabInactiveStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = F(11),
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f), background = _tabInactiveTex },
                hover = { textColor = Color.white, background = _tabInactiveTex }
            };

            _styleVersion = settingsVersion;
            _stylesInitialized = true;
        }

        private float[] GetColWidths()
        {
            float fixedW = 0f;
            foreach (var w in FixedColWidths) fixedW += U(w);
            float nameW = _windowWidth - U(EDGE_MARGIN) * 2 - U(30f) - fixedW;
            if (nameW < U(100f)) nameW = U(100f);
            float[] widths = new float[ColKeys.Length];
            widths[0] = nameW;
            for (int i = 0; i < FixedColWidths.Length; i++) widths[i + 1] = U(FixedColWidths[i]);
            return widths;
        }

        public void Draw()
        {
            InitStyles();
            UpdateScaleFactor();

            // Apply per-tab minimums
            int tabIdx = (int)_currentTab;
            _windowWidth = Mathf.Max(U(_tabMinWidths[tabIdx]), _windowWidth);
            _windowHeight = Mathf.Max(U(_tabMinHeights[tabIdx]), _windowHeight);
            _windowRect.width = _windowWidth;
            _windowRect.height = _windowHeight;

            _remoteMode = DamageMeterMultiplayerApi.TryGetRemoteSnapshot(out _remoteSnapshot);
            if (!_remoteMode)
            {
                _tracker.RefreshSnapshot();
                _contributionTracker?.RefreshSnapshot();
            }

            var prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scaleFactor, _scaleFactor, 1f));

            string title = _remoteMode
                ? $"{DmText.T("damageMeterTitle")}  [{DmText.T("remoteHost")}]  [{DmText.T("hideHint")}]"
                : $"{DmText.T("damageMeterTitle")}  [{DmText.T("hideHint")}]  [{DmText.T("resetHint")}]  [{DmText.T("exportHint")}]";
            _windowRect = GUI.Window(729001, _windowRect, DrawWindow, title, _windowStyle);
            _windowRect = UiUtil.ClampToScreen(_windowRect, _scaleFactor);
            UiInputBlocker.RegisterRect(_windowRect, _scaleFactor);

            GUI.matrix = prevMatrix;
            HandleResize();
        }

        private void HandleResize()
        {
            Event e = Event.current;
            float mx = e.mousePosition.x / _scaleFactor;
            float my = e.mousePosition.y / _scaleFactor;
            float resizeHandle = U(RESIZE_HANDLE);
            Rect resizeRect = new Rect(_windowRect.xMax - resizeHandle, _windowRect.yMax - resizeHandle, resizeHandle, resizeHandle);
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
                _windowWidth = Mathf.Max(U(_tabMinWidths[(int)_currentTab]), _resizeStartW + (mx - _resizeStart.x));
                _windowHeight = Mathf.Max(U(_tabMinHeights[(int)_currentTab]), _resizeStartH + (my - _resizeStart.y));
                _windowRect.width = _windowWidth;
                _windowRect.height = _windowHeight;
                _windowRect = UiUtil.ClampToScreen(_windowRect, _scaleFactor);
                e.Use();
            }
            else if (_isResizing && e.type == EventType.MouseUp)
            {
                _isResizing = false;
                // Save resized dimensions to current tab
                _tabWidths[(int)_currentTab] = _windowWidth;
                _tabHeights[(int)_currentTab] = _windowHeight;
            }
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();
            {
                DrawTabBar();
                DrawToolbar();

                float settingsExtra = _showSettings ? U(SETTINGS_PANEL_HEIGHT) : 0f;
                float contentH = _windowHeight - U(TAB_BAR_HEIGHT) - U(TOOLBAR_HEIGHT) - U(CHROME_HEIGHT) - settingsExtra - U(SCROLL_BOTTOM_MARGIN);
                if (contentH < U(60f)) contentH = U(60f);

                switch (_currentTab)
                {
                    case Tab.Stats:
                        DrawStatsContent(contentH);
                        break;
                    case Tab.CombatLog:
                        if (_logUi != null)
                            _logUi.DrawTabContent(contentH);
                        break;
                    case Tab.BuffLog:
                        if (_statusLogUi != null)
                            _statusLogUi.DrawTabContent(contentH);
                        break;
                }

                if (_showSettings) DrawSettingsPanel();
            }
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, _windowWidth, _windowHeight - U(RESIZE_HANDLE)));
        }

        private void DrawTabBar()
        {
            GUILayout.BeginHorizontal();
            {
                Color prevBg = GUI.backgroundColor;

                GUI.backgroundColor = Color.white;
                if (GUILayout.Button(DmText.T("tabStats"), _currentTab == Tab.Stats ? _tabActiveStyle : _tabInactiveStyle, GUILayout.Width(U(70))))
                    SwitchToTab(Tab.Stats);
                if (GUILayout.Button(DmText.T("log"), _currentTab == Tab.CombatLog ? _tabActiveStyle : _tabInactiveStyle, GUILayout.Width(U(70))))
                    SwitchToTab(Tab.CombatLog);
                if (!_remoteMode && _statusLogUi != null)
                {
                    if (GUILayout.Button(DmText.T("tabBuff"), _currentTab == Tab.BuffLog ? _tabActiveStyle : _tabInactiveStyle, GUILayout.Width(U(70))))
                        SwitchToTab(Tab.BuffLog);
                }

                GUILayout.FlexibleSpace();

                GUI.backgroundColor = new Color(0.6f, 0.8f, 0.6f);
                if (GUILayout.Button(DmText.T("runStats"), _toggleStyle, GUILayout.Width(U(85))))
                    OnShowRunStats?.Invoke();
                GUI.backgroundColor = prevBg;
            }
            GUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            GUILayout.BeginHorizontal();
            {
                Color prevBg = GUI.backgroundColor;
                bool recording = IsRecording?.Invoke() ?? false;

                // Recording button with pulse
                if (recording)
                {
                    float pulse = 0.7f + 0.3f * Mathf.Sin(Time.realtimeSinceStartup * 3f);
                    GUI.backgroundColor = new Color(0.9f * pulse, 0.2f, 0.2f);
                }
                else
                {
                    GUI.backgroundColor = new Color(0.4f, 0.4f, 0.4f);
                }
                string recLabel = recording ? DmText.Format("recording", BattleCount?.Invoke() ?? 0) : DmText.T("recordRun");
                if (GUILayout.Button(recLabel, _toggleStyle, GUILayout.Width(U(150))))
                    OnToggleRecording?.Invoke();

                if (_remoteMode && _remoteSnapshot != null)
                {
                    GUI.backgroundColor = prevBg;
                    GUILayout.Label($"r{_remoteSnapshot.Round}/t{_remoteSnapshot.Turn} {_remoteSnapshot.BattleState}", _labelStyle);
                }
                else
                {
                    // Auto-recording toggle
                    GUI.backgroundColor = prevBg;
                    bool autoRecording = IsAutoRecordingEnabled?.Invoke() ?? false;
                    bool nextAutoRecording = GUILayout.Toggle(autoRecording, DmText.T("autoRec"), _checkStyle, GUILayout.Width(U(95)));
                    if (nextAutoRecording != autoRecording) OnAutoRecordingChanged?.Invoke(nextAutoRecording);
                }

                GUILayout.FlexibleSpace();

                // Export CSV
                GUI.backgroundColor = new Color(0.6f, 0.7f, 0.9f);
                if (GUILayout.Button(DmText.T("exportCsv"), _toggleStyle, GUILayout.Width(U(95))))
                    OnExportCsv?.Invoke();

                // Settings toggle
                GUI.backgroundColor = _showSettings ? new Color(0.5f, 0.6f, 0.8f) : new Color(0.35f, 0.35f, 0.4f);
                if (GUILayout.Button(DmText.T("tabSettings"), _toggleStyle, GUILayout.Width(U(55))))
                {
                    _showSettings = !_showSettings;
                    if (_showSettings) LoadExportDirectoryEdit();
                }

                // Language toggle
                GUI.backgroundColor = new Color(0.45f, 0.55f, 0.5f);
                if (GUILayout.Button(DmText.T("langToggle"), _toggleStyle, GUILayout.Width(U(45))))
                    OnLanguageChanged?.Invoke(DmText.ToggleLanguageValue());

                GUI.backgroundColor = prevBg;
            }
            GUILayout.EndHorizontal();

            // Thin separator line below toolbar
            Rect sepRect = GUILayoutUtility.GetRect(_windowWidth, U(1f));
            GUI.DrawTexture(sepRect, _toolbarBgTex);
            GUILayout.Space(U(3f));
        }

        private void DrawStatsContent(float contentH)
        {
            bool remoteMode = _remoteMode && _remoteSnapshot != null;

            // Sub-tab row (Heroes / Enemies)
            GUILayout.BeginHorizontal();
            {
                Color prevBg = GUI.backgroundColor;
                GUI.backgroundColor = _showPlayerTeam ? new Color(0.3f, 0.6f, 0.9f) : new Color(0.4f, 0.4f, 0.4f);
                if (GUILayout.Button(DmText.T("heroes"), _toggleStyle, GUILayout.Width(_windowWidth / 2f - U(12))))
                    _showPlayerTeam = true;
                GUI.backgroundColor = !_showPlayerTeam ? new Color(0.9f, 0.3f, 0.3f) : new Color(0.4f, 0.4f, 0.4f);
                if (GUILayout.Button(DmText.T("enemies"), _toggleStyle, GUILayout.Width(_windowWidth / 2f - U(12))))
                    _showPlayerTeam = false;
                GUI.backgroundColor = prevBg;
            }
            GUILayout.EndHorizontal();

            if (remoteMode && !_remoteSnapshot.IsAvailable)
            {
                GUILayout.Label(DmText.Format("remoteUnavailable", _remoteSnapshot.UnavailableReason ?? DmText.T("unknown")), _labelStyle);
                return;
            }

            List<DisplayActorStats> stats = remoteMode
                ? BuildRemoteActorRows(_showPlayerTeam ? _remoteSnapshot.Heroes : _remoteSnapshot.Enemies)
                : (_showPlayerTeam ? _retainedPlayerStats : _retainedEnemyStats) ??
                  BuildLocalActorRows(_showPlayerTeam ? _tracker.PlayerStats : _tracker.EnemyStats);
            float totalDmg = 0f;
            for (int i = 0; i < stats.Count; i++) totalDmg += stats[i].TotalDamageDealt;
            GUILayout.Label(DmText.Format("totalDamage", totalDmg), _totalStyle);

            float[] cw = GetColWidths();

            // Column headers
            Rect headerRect = GUILayoutUtility.GetRect(_windowWidth - U(RESIZE_HANDLE), U(HEADER_HEIGHT));
            GUI.color = new Color(1f, 1f, 1f, 1f);
            GUI.DrawTexture(new Rect(headerRect.x, headerRect.y, headerRect.width, headerRect.height), _headerBgTex);
            GUI.color = Color.white;

            float hx = headerRect.x + U(EDGE_MARGIN);
            for (int i = 0; i < ColKeys.Length; i++)
            {
                GUI.Label(new Rect(hx, headerRect.y, cw[i], headerRect.height), DmText.T(ColKeys[i]), _headerStyle);
                hx += cw[i];
            }

            // Scroll area
            float scrollH = contentH - U(SUB_TAB_HEIGHT) - U(20f) - U(HEADER_HEIGHT);
            if (scrollH < U(60f)) scrollH = U(60f);
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
        }

        private void LoadExportDirectoryEdit()
        {
            _exportDirectoryEdit = GetExportDirectory?.Invoke() ?? "";
            _exportDirectoryEditInitialized = true;
            _settingsMessage = "";
        }

        private void DrawSettingsPanel()
        {
            if (!_exportDirectoryEditInitialized) LoadExportDirectoryEdit();

            // Thin separator line above settings
            Rect sepRect = GUILayoutUtility.GetRect(_windowWidth, U(1f));
            GUI.DrawTexture(sepRect, _toolbarBgTex);
            GUILayout.Space(U(3f));

            GUILayout.BeginHorizontal();
            {
                bool showInBattle = IsAutoShowInBattleEnabled?.Invoke() ?? true;
                bool nextShowInBattle = GUILayout.Toggle(showInBattle, DmText.T("autoShowInBattle"), _checkStyle, GUILayout.Width(U(190)));
                if (nextShowInBattle != showInBattle) OnAutoShowInBattleChanged?.Invoke(nextShowInBattle);

                bool showOutsideBattle = IsAutoShowOutsideBattleEnabled?.Invoke() ?? false;
                bool nextShowOutsideBattle = GUILayout.Toggle(showOutsideBattle, DmText.T("autoShowOutsideBattle"), _checkStyle, GUILayout.Width(U(210)));
                if (nextShowOutsideBattle != showOutsideBattle) OnAutoShowOutsideBattleChanged?.Invoke(nextShowOutsideBattle);
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(U(3f));

            GUILayout.BeginHorizontal();
            {
                GUILayout.Label(DmText.T("exportDirectory"), _headerStyle, GUILayout.Width(U(90)));
                _exportDirectoryEdit = GUILayout.TextField(_exportDirectoryEdit ?? "", GUILayout.Width(Mathf.Max(U(200), _windowWidth - U(280))));
                if (GUILayout.Button(DmText.T("save"), _toggleStyle, GUILayout.Width(U(55))))
                {
                    OnExportDirectoryChanged?.Invoke(_exportDirectoryEdit ?? "");
                    _settingsMessage = DmText.T("saved");
                }
                if (GUILayout.Button(DmText.T("reset"), _toggleStyle, GUILayout.Width(U(55))))
                {
                    _exportDirectoryEdit = "";
                    OnExportDirectoryChanged?.Invoke("");
                    _settingsMessage = DmText.T("default");
                }
            }
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_settingsMessage))
            {
                GUILayout.Label(_settingsMessage, _labelStyle);
            }
        }

        private void DrawActorRow(DisplayActorStats s, float teamTotalDmg, float maxDmg, float[] cw, int rowIndex)
        {
            float dmgPct = teamTotalDmg > 0 ? s.TotalDamageDealt / teamTotalDmg * 100f : 0f;
            float barPct = maxDmg > 0 ? s.TotalDamageDealt / maxDmg : 0f;
            Rect row = GUILayoutUtility.GetRect(_windowWidth - U(RESIZE_HANDLE), U(ROW_HEIGHT));

            // Team-tinted row background (very subtle)
            GUI.DrawTexture(new Rect(row.x, row.y, row.width, row.height), _showPlayerTeam ? _heroRowTex : _enemyRowTex);
            // Alternate row overlay for readability
            if (rowIndex % 2 == 1)
            {
                GUI.DrawTexture(new Rect(row.x, row.y, row.width, row.height), _rowAltTex);
            }

            // Damage bar
            Color bc = _showPlayerTeam ? new Color(0.2f, 0.4f, 0.8f, 0.3f) : new Color(0.8f, 0.2f, 0.2f, 0.3f);
            if (barPct > 0f)
            {
                GUI.color = bc;
                GUI.DrawTexture(new Rect(row.x + U(EDGE_MARGIN), row.y, (row.width - U(EDGE_MARGIN) * 2) * barPct, row.height), Texture2D.whiteTexture);
                GUI.color = Color.white;
            }

            float x = row.x + U(EDGE_MARGIN), y = row.y, h = row.height;
            string dn = s.ActorName ?? $"#{s.ActorGuid}";
            GUI.Label(new Rect(x, y, cw[0], h), dn, _labelStyle); x += cw[0];
            GUI.Label(new Rect(x, y, cw[1], h), $"{s.TotalDamageDealt:F0}", _valueStyle); x += cw[1];
            GUI.Label(new Rect(x, y, cw[2], h), s.DotDamageDealt > 0 ? $"{s.DotDamageDealt:F0}" : "-", _valueStyle); x += cw[2];
            string takenStr = UiUtil.FormatDamageTaken(s.RawDamageReceived, s.TotalDamageReceived);
            GUI.Label(new Rect(x, y, cw[3], h), takenStr, _valueStyle); x += cw[3];
            GUI.Label(new Rect(x, y, cw[4], h), s.TotalHealingDone > 0 ? $"{s.TotalHealingDone:F0}" : "-", _valueStyle); x += cw[4];
            GUI.Label(new Rect(x, y, cw[5], h), s.TotalHealingReceived > 0 ? $"{s.TotalHealingReceived:F0}" : "-", _valueStyle); x += cw[5];
            GUI.Label(new Rect(x, y, cw[6], h), s.Kills > 0 ? $"{s.Kills}" : "-", _valueStyle); x += cw[6];
            GUI.Label(new Rect(x, y, cw[7], h), s.Crits > 0 ? $"{s.Crits}" : "-", _valueStyle); x += cw[7];
            GUI.Label(new Rect(x, y, cw[8], h), s.AvoidedAttacks > 0 ? $"{s.AvoidedAttacks}" : "-", _valueStyle); x += cw[8];
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
                : GetLocalContributionRows();
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

            GUILayout.Space(U(8));
            GUILayout.Label(DmText.T("contribution"), _totalStyle);

            float contribW = U(62f);
            float bonusW = U(54f);
            float vulnerableW = U(52f);
            float shieldW = U(56f);
            float guardW = U(52f);
            float dotPreventedW = U(62f);
            float comboConsumedW = U(58f);
            float pctW = U(46f);
            float nameW = _windowWidth - U(EDGE_MARGIN) * 2 - U(30f) - contribW - bonusW - vulnerableW - shieldW - guardW - dotPreventedW - comboConsumedW - pctW;
            if (nameW < U(120f)) nameW = U(120f);

            Rect headerRect = GUILayoutUtility.GetRect(_windowWidth - U(RESIZE_HANDLE), U(HEADER_HEIGHT));
            GUI.DrawTexture(new Rect(headerRect.x, headerRect.y, headerRect.width, headerRect.height), _headerBgTex);
            float hx = headerRect.x + U(EDGE_MARGIN);
            GUI.Label(new Rect(hx, headerRect.y, nameW, headerRect.height), DmText.T("name"), _headerStyle); hx += nameW;
            GUI.Label(new Rect(hx, headerRect.y, contribW, headerRect.height), DmText.T("contrib"), _headerStyle); hx += contribW;
            GUI.Label(new Rect(hx, headerRect.y, bonusW, headerRect.height), DmText.T("dmgPlus"), _headerStyle); hx += bonusW;
            GUI.Label(new Rect(hx, headerRect.y, vulnerableW, headerRect.height), DmText.T("vulnerableShort"), _headerStyle); hx += vulnerableW;
            GUI.Label(new Rect(hx, headerRect.y, shieldW, headerRect.height), DmText.T("shield"), _headerStyle); hx += shieldW;
            GUI.Label(new Rect(hx, headerRect.y, guardW, headerRect.height), DmText.T("guard"), _headerStyle); hx += guardW;
            GUI.Label(new Rect(hx, headerRect.y, dotPreventedW, headerRect.height), DmText.T("dotPrevented"), _headerStyle); hx += dotPreventedW;
            GUI.Label(new Rect(hx, headerRect.y, comboConsumedW, headerRect.height), DmText.T("comboConsumed"), _headerStyle); hx += comboConsumedW;
            GUI.Label(new Rect(hx, headerRect.y, pctW, headerRect.height), DmText.T("pct"), _headerStyle);

            int drawn = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var s = rows[i];
                if (s.TotalContribution <= 0.01f && s.ComboConsumed <= 0) continue;
                Rect row = GUILayoutUtility.GetRect(_windowWidth - U(RESIZE_HANDLE), U(ROW_HEIGHT));
                GUI.DrawTexture(new Rect(row.x, row.y, row.width, row.height), _heroRowTex);
                if (drawn % 2 == 1) GUI.DrawTexture(new Rect(row.x, row.y, row.width, row.height), _rowAltTex);

                float x = row.x + U(EDGE_MARGIN), y = row.y, h = row.height;
                string nm = s.ActorName ?? $"#{s.ActorGuid}";
                GUI.Label(new Rect(x, y, nameW, h), nm, _labelStyle); x += nameW;
                GUI.Label(new Rect(x, y, contribW, h), s.TotalContribution > 0 ? $"{s.TotalContribution:F1}" : "-", _valueStyle); x += contribW;
                GUI.Label(new Rect(x, y, bonusW, h), s.BonusDamage > 0 ? $"{s.BonusDamage:F1}" : "-", _valueStyle); x += bonusW;
                GUI.Label(new Rect(x, y, vulnerableW, h), s.VulnerableDamage > 0 ? $"{s.VulnerableDamage:F1}" : "-", _valueStyle); x += vulnerableW;
                GUI.Label(new Rect(x, y, shieldW, h), s.ShieldPrevented > 0 ? $"{s.ShieldPrevented:F1}" : "-", _valueStyle); x += shieldW;
                GUI.Label(new Rect(x, y, guardW, h), s.GuardProtected > 0 ? $"{s.GuardProtected:F1}" : "-", _valueStyle); x += guardW;
                GUI.Label(new Rect(x, y, dotPreventedW, h), s.DotDamagePrevented > 0 ? $"{s.DotDamagePrevented:F1}" : "-", _valueStyle); x += dotPreventedW;
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
                : GetLocalContributionRows();
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

        private List<DisplayContributionStats> GetLocalContributionRows()
        {
            return _retainedContributionStats ??
                   BuildLocalContributionRows(_contributionTracker == null ? null : _contributionTracker.PlayerStats);
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
                    DotDamagePrevented = s.DotDamagePrevented,
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
                    DotDamagePrevented = s.DotDamagePrevented,
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
            public float DotDamagePrevented;
            public int ShieldWasted;
            public int ComboApplied;
            public int ComboConsumed;
            public float TotalContribution;
        }
    }
}
