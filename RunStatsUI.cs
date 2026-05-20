using System.Collections.Generic;
using UnityEngine;

namespace DD2DamageMeter
{
    public class RunStatsUI
    {
        private readonly RunStatsTracker _runTracker;
        private readonly DamageTracker _damageTracker;
        private readonly ContributionTracker _contributionTracker;
        private Rect _rect = new Rect(100f, 100f, 780f, 450f);
        private Vector2 _scroll;
        private GUIStyle _headerStyle, _labelStyle, _valueStyle, _titleStyle, _windowStyle, _resizeStyle;
        private bool _init;
        private float _w = 780f, _h = 450f;
        private bool _rs; private Vector2 _rsS; private float _rsW, _rsH;
        private const float RESIZE_HANDLE = 16f;
        private const float EDGE_MARGIN = 8f;
        private const float HEADER_HEIGHT = 18f;
        private const float ROW_HEIGHT = 18f;

        // Textures
        private Texture2D _windowBgTex;
        private Texture2D _headerBgTex;
        private Texture2D _rowAltTex;

        // Fixed column widths (everything except Name)
        private const float COL_BATTLES = 44f;
        private const float COL_DMG = 60f;
        private const float COL_DOT = 50f;
        private const float COL_OVK = 48f;
        private const float COL_TAKEN = 72f;
        private const float COL_HEAL_OUT = 50f;
        private const float COL_HEAL_IN = 54f;
        private const float COL_KILLS = 40f;
        private const float COL_CRITS = 40f;
        private const float COL_AVOID = 62f;
        private const float COL_COMBO_APPLIED = 54f;
        private const float COL_PCT = 42f;
        private const float COL_CONTRIB = 68f;
        private const float COL_BONUS = 64f;
        private const float COL_VULN = 58f;
        private const float COL_SHIELD = 64f;
        private const float COL_GUARD = 58f;
        private const float COL_CONTRIB_PCT = 46f;

        private static readonly string[] HeaderKeys = { "name", "bat", "dmg", "dot", "ovk", "rawTkn", "healOut", "healIn", "kills", "crits", "avoidPct", "comboApplied", "pct" };
        private static readonly float[] FixedWidths = { 0f, COL_BATTLES, COL_DMG, COL_DOT, COL_OVK, COL_TAKEN, COL_HEAL_OUT, COL_HEAL_IN, COL_KILLS, COL_CRITS, COL_AVOID, COL_COMBO_APPLIED, COL_PCT };

        // Scale
        private float _scaleFactor = 1f;
        private int _lastScreenHeight;

        public bool IsVisible { get; set; }

        public RunStatsUI(RunStatsTracker t, DamageTracker damageTracker, ContributionTracker contributionTracker = null)
        {
            _runTracker = t;
            _damageTracker = damageTracker;
            _contributionTracker = contributionTracker;
        }

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
            _headerBgTex = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.18f));
            _rowAltTex = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.1f));

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _windowBgTex;
            _windowStyle.onNormal.background = _windowBgTex;
            _windowStyle.focused.background = _windowBgTex;
            _windowStyle.onFocused.background = _windowBgTex;
            _windowStyle.normal.textColor = new Color(0.9f, 0.85f, 0.7f);
            _windowStyle.fontSize = 13;
            _windowStyle.fontStyle = FontStyle.Bold;
            _windowStyle.padding = new RectOffset(6, 6, 22, 4);

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.95f, 0.85f, 0.4f) }
            };
            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.9f, 0.8f, 0.3f) }
            };
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white },
                clipping = TextClipping.Overflow
            };
            _valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };
            _resizeStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f, 0.8f) }
            };
            _init = true;
        }

        private float GetNameWidth()
        {
            float contentW = _w - 30f;
            float fixedTotal = COL_BATTLES + COL_DMG + COL_DOT + COL_OVK + COL_TAKEN + COL_HEAL_OUT + COL_HEAL_IN + COL_KILLS + COL_CRITS + COL_AVOID + COL_COMBO_APPLIED + COL_PCT;
            float nameW = contentW - fixedTotal - EDGE_MARGIN * 2;
            return nameW < 80f ? 80f : nameW;
        }

        public void Draw()
        {
            Init();
            UpdateScaleFactor();

            var prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scaleFactor, _scaleFactor, 1f));

            _rect = GUI.Window(729003, _rect, Win, DmText.T("runStatsTitle"), _windowStyle);
            _rect = UiUtil.ClampToScreen(_rect, _scaleFactor);

            GUI.matrix = prevMatrix;

            var e = Event.current;
            float mx = e.mousePosition.x / _scaleFactor;
            float my = e.mousePosition.y / _scaleFactor;
            var rr = new Rect(_rect.xMax - RESIZE_HANDLE, _rect.yMax - RESIZE_HANDLE, RESIZE_HANDLE, RESIZE_HANDLE);
            if (e.type == EventType.MouseDown && e.button == 0 && rr.Contains(new Vector2(mx, my))) { _rs = true; _rsS = new Vector2(mx, my); _rsW = _w; _rsH = _h; e.Use(); }
            else if (_rs && e.type == EventType.MouseDrag) { _w = Mathf.Max(640, _rsW + (mx - _rsS.x)); _h = Mathf.Max(300, _rsH + (my - _rsS.y)); _rect.width = _w; _rect.height = _h; _rect = UiUtil.ClampToScreen(_rect, _scaleFactor); e.Use(); }
            else if (_rs && e.type == EventType.MouseUp) _rs = false;
        }

        private void Win(int id)
        {
            bool remoteMode = DamageMeterMultiplayerApi.TryGetRemoteSnapshot(out var remoteSnapshot);
            DamageTracker currentTracker = remoteMode ? null : _damageTracker;
            ContributionTracker currentContribution = remoteMode ? null : _contributionTracker;
            int battleCount = _runTracker.GetBattleCount(currentTracker, currentContribution, remoteMode ? remoteSnapshot : null);
            if (battleCount == 0)
            {
                GUILayout.Label(DmText.T("noBattles"), _labelStyle);
                if (GUILayout.Button(DmText.T("close"), GUILayout.Width(60))) IsVisible = false;
                GUI.DragWindow(new Rect(0, 0, _w, _h - RESIZE_HANDLE));
                return;
            }

            var (players, enemies) = _runTracker.GetMergedStats(currentTracker, currentContribution, remoteMode ? remoteSnapshot : null);
            float scrollH = _h - 40f;
            float nameW = GetNameWidth();

            // Update FixedWidths[0] with computed name width
            FixedWidths[0] = nameW;

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(scrollH));
            {
                GUILayout.Label(DmText.Format("heroesRecorded", battleCount, remoteMode ? DmText.T("remoteSuffix") : string.Empty), _titleStyle);
                DrawHeader(nameW);
                float totalPDmg = 0; foreach (var s in players) totalPDmg += s.TotalDamageDealt;
                int rowIdx = 0;
                foreach (var s in players) DrawMergedRow(s, totalPDmg, nameW, rowIdx++);
                GUILayout.Space(8);

                GUILayout.Label(DmText.T("enemiesHeader"), _titleStyle);
                DrawHeader(nameW);
                float totalEDmg = 0; foreach (var s in enemies) totalEDmg += s.TotalDamageDealt;
                rowIdx = 0;
                foreach (var s in enemies) DrawMergedRow(s, totalEDmg, nameW, rowIdx++);

                var contributionRows = GetContributionRows(players, out var totalContribution);
                if (contributionRows.Count > 0)
                {
                    GUILayout.Space(8);
                    GUILayout.Label(DmText.T("contribution"), _titleStyle);
                    float contributionNameW = GetContributionNameWidth();
                    DrawContributionHeader(contributionNameW);
                    rowIdx = 0;
                    foreach (var s in contributionRows)
                        DrawContributionRow(s, totalContribution, contributionNameW, rowIdx++);
                }
            }
            GUILayout.EndScrollView();

            if (GUILayout.Button(DmText.T("close"), GUILayout.Width(60))) IsVisible = false;
            GUI.Label(new Rect(_w - RESIZE_HANDLE - 2, _h - RESIZE_HANDLE - 2, RESIZE_HANDLE, RESIZE_HANDLE), "\u255a", _resizeStyle);
            GUI.DragWindow(new Rect(0, 0, _w, _h - RESIZE_HANDLE));
        }

        private void DrawHeader(float nameW)
        {
            Rect headerRect = GUILayoutUtility.GetRect(_w - RESIZE_HANDLE, HEADER_HEIGHT);

            // Draw header background
            GUI.DrawTexture(new Rect(headerRect.x, headerRect.y, headerRect.width, headerRect.height), _headerBgTex);

            float x = headerRect.x + EDGE_MARGIN;
            // Name column (left-aligned in header)
            GUI.Label(new Rect(x, headerRect.y, nameW, headerRect.height), DmText.T(HeaderKeys[0]), _headerStyle);
            x += nameW;
            // Fixed columns (center-aligned)
            for (int i = 1; i < HeaderKeys.Length; i++)
            {
                GUI.Label(new Rect(x, headerRect.y, FixedWidths[i], headerRect.height), DmText.T(HeaderKeys[i]), _headerStyle);
                x += FixedWidths[i];
            }
        }

        private void DrawMergedRow(RunStatsTracker.MergedStats s, float totalDmg, float nameW, int rowIndex)
        {
            Rect row = GUILayoutUtility.GetRect(_w - RESIZE_HANDLE, ROW_HEIGHT);

            // Alternate row background
            if (rowIndex % 2 == 1)
            {
                GUI.DrawTexture(new Rect(row.x, row.y, row.width, row.height), _rowAltTex);
            }

            float x = row.x + EDGE_MARGIN, y = row.y, h = row.height;

            string nm = s.ActorName ?? "?";
            if (nm.Length > 30) nm = nm.Substring(0, 28) + "..";
            GUI.Label(new Rect(x, y, nameW, h), nm, _labelStyle); x += nameW;
            GUI.Label(new Rect(x, y, COL_BATTLES, h), $"{s.BattlesSeen}", _valueStyle); x += COL_BATTLES;
            GUI.Label(new Rect(x, y, COL_DMG, h), $"{s.TotalDamageDealt:F0}", _valueStyle); x += COL_DMG;
            GUI.Label(new Rect(x, y, COL_DOT, h), s.DotDamageDealt > 0 ? $"{s.DotDamageDealt:F0}" : "-", _valueStyle); x += COL_DOT;
            GUI.Label(new Rect(x, y, COL_OVK, h), s.OverkillDamageDealt > 0 ? $"{s.OverkillDamageDealt:F0}" : "-", _valueStyle); x += COL_OVK;
            GUI.Label(new Rect(x, y, COL_TAKEN, h), UiUtil.FormatDamageTaken(s.RawDamageReceived, s.TotalDamageReceived), _valueStyle); x += COL_TAKEN;
            GUI.Label(new Rect(x, y, COL_HEAL_OUT, h), s.TotalHealingDone > 0 ? $"{s.TotalHealingDone:F0}" : "-", _valueStyle); x += COL_HEAL_OUT;
            GUI.Label(new Rect(x, y, COL_HEAL_IN, h), s.TotalHealingReceived > 0 ? $"{s.TotalHealingReceived:F0}" : "-", _valueStyle); x += COL_HEAL_IN;
            GUI.Label(new Rect(x, y, COL_KILLS, h), s.Kills > 0 ? $"{s.Kills}" : "-", _valueStyle); x += COL_KILLS;
            GUI.Label(new Rect(x, y, COL_CRITS, h), s.Crits > 0 ? $"{s.Crits}" : "-", _valueStyle); x += COL_CRITS;
            GUI.Label(new Rect(x, y, COL_AVOID, h), UiUtil.FormatAvoidanceRate(s.AvoidedAttacks, s.IncomingAttacks), _valueStyle); x += COL_AVOID;
            GUI.Label(new Rect(x, y, COL_COMBO_APPLIED, h), s.ComboApplied > 0 ? $"{s.ComboApplied}" : "-", _valueStyle); x += COL_COMBO_APPLIED;
            float pct = totalDmg > 0 ? s.TotalDamageDealt / totalDmg * 100f : 0f;
            GUI.Label(new Rect(x, y, COL_PCT, h), $"{pct:F1}%", _valueStyle);
        }

        private float GetContributionNameWidth()
        {
            float contentW = _w - 30f;
            float fixedTotal = COL_CONTRIB + COL_BONUS + COL_VULN + COL_SHIELD + COL_GUARD + 58f + COL_CONTRIB_PCT;
            float nameW = contentW - fixedTotal - EDGE_MARGIN * 2;
            return nameW < 120f ? 120f : nameW;
        }

        private static List<RunStatsTracker.MergedStats> GetContributionRows(List<RunStatsTracker.MergedStats> players, out float totalContribution)
        {
            var rows = new List<RunStatsTracker.MergedStats>();
            totalContribution = 0f;
            if (players == null) return rows;
            foreach (var s in players)
            {
                if (s.TotalContribution <= 0.01f && s.ComboConsumed <= 0) continue;
                rows.Add(s);
                totalContribution += s.TotalContribution;
            }
            rows.Sort((a, b) =>
            {
                int result = b.TotalContribution.CompareTo(a.TotalContribution);
                if (result != 0) return result;
                result = b.VulnerableDamageContribution.CompareTo(a.VulnerableDamageContribution);
                if (result != 0) return result;
                result = b.ComboConsumed.CompareTo(a.ComboConsumed);
                if (result != 0) return result;
                return string.Compare(a.ActorName, b.ActorName, System.StringComparison.CurrentCultureIgnoreCase);
            });
            return rows;
        }

        private void DrawContributionHeader(float nameW)
        {
            Rect headerRect = GUILayoutUtility.GetRect(_w - RESIZE_HANDLE, HEADER_HEIGHT);
            GUI.DrawTexture(new Rect(headerRect.x, headerRect.y, headerRect.width, headerRect.height), _headerBgTex);

            float x = headerRect.x + EDGE_MARGIN;
            GUI.Label(new Rect(x, headerRect.y, nameW, headerRect.height), DmText.T("name"), _headerStyle); x += nameW;
            GUI.Label(new Rect(x, headerRect.y, COL_CONTRIB, headerRect.height), DmText.T("contrib"), _headerStyle); x += COL_CONTRIB;
            GUI.Label(new Rect(x, headerRect.y, COL_BONUS, headerRect.height), DmText.T("dmgPlus"), _headerStyle); x += COL_BONUS;
            GUI.Label(new Rect(x, headerRect.y, COL_VULN, headerRect.height), DmText.T("vulnerable"), _headerStyle); x += COL_VULN;
            GUI.Label(new Rect(x, headerRect.y, COL_SHIELD, headerRect.height), DmText.T("shield"), _headerStyle); x += COL_SHIELD;
            GUI.Label(new Rect(x, headerRect.y, COL_GUARD, headerRect.height), DmText.T("guard"), _headerStyle); x += COL_GUARD;
            GUI.Label(new Rect(x, headerRect.y, 58f, headerRect.height), DmText.T("comboConsumed"), _headerStyle); x += 58f;
            GUI.Label(new Rect(x, headerRect.y, COL_CONTRIB_PCT, headerRect.height), DmText.T("pct"), _headerStyle);
        }

        private void DrawContributionRow(RunStatsTracker.MergedStats s, float totalContribution, float nameW, int rowIndex)
        {
            Rect row = GUILayoutUtility.GetRect(_w - RESIZE_HANDLE, ROW_HEIGHT);
            if (rowIndex % 2 == 1)
                GUI.DrawTexture(new Rect(row.x, row.y, row.width, row.height), _rowAltTex);

            float x = row.x + EDGE_MARGIN, y = row.y, h = row.height;
            string nm = s.ActorName ?? "?";
            if (nm.Length > 30) nm = nm.Substring(0, 28) + "..";
            float pct = totalContribution > 0f ? s.TotalContribution / totalContribution * 100f : 0f;

            GUI.Label(new Rect(x, y, nameW, h), nm, _labelStyle); x += nameW;
            GUI.Label(new Rect(x, y, COL_CONTRIB, h), s.TotalContribution > 0 ? $"{s.TotalContribution:F1}" : "-", _valueStyle); x += COL_CONTRIB;
            GUI.Label(new Rect(x, y, COL_BONUS, h), s.BonusDamageContribution > 0 ? $"{s.BonusDamageContribution:F1}" : "-", _valueStyle); x += COL_BONUS;
            GUI.Label(new Rect(x, y, COL_VULN, h), s.VulnerableDamageContribution > 0 ? $"{s.VulnerableDamageContribution:F1}" : "-", _valueStyle); x += COL_VULN;
            GUI.Label(new Rect(x, y, COL_SHIELD, h), s.ShieldContribution > 0 ? $"{s.ShieldContribution:F1}" : "-", _valueStyle); x += COL_SHIELD;
            GUI.Label(new Rect(x, y, COL_GUARD, h), s.GuardContribution > 0 ? $"{s.GuardContribution:F1}" : "-", _valueStyle); x += COL_GUARD;
            GUI.Label(new Rect(x, y, 58f, h), s.ComboConsumed > 0 ? $"{s.ComboConsumed}" : "-", _valueStyle); x += 58f;
            GUI.Label(new Rect(x, y, COL_CONTRIB_PCT, h), $"{pct:F1}%", _valueStyle);
        }
    }
}
