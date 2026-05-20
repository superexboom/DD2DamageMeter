using System;
using UnityEngine;

namespace DD2DamageMeter
{
    internal static class DmText
    {
        private static string _configuredLanguage = "auto";

        public static string ConfiguredLanguage => _configuredLanguage;

        public static bool IsChinese
        {
            get
            {
                if (IsZh(_configuredLanguage)) return true;
                if (IsEn(_configuredLanguage)) return false;
                return Application.systemLanguage == SystemLanguage.Chinese ||
                       Application.systemLanguage == SystemLanguage.ChineseSimplified ||
                       Application.systemLanguage == SystemLanguage.ChineseTraditional;
            }
        }

        public static void SetLanguage(string language)
        {
            _configuredLanguage = string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim();
        }

        public static string ToggleLanguageValue()
        {
            return IsChinese ? "en" : "zh";
        }

        public static string LanguageDisplay()
        {
            return IsChinese ? "中文" : "English";
        }

        public static string T(string key)
        {
            return IsChinese ? Zh(key) : En(key);
        }

        public static string Format(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }

        public static string ActionLabel(string actionType, float value, string dotType)
        {
            string action = actionType ?? "";
            if (IsChinese)
            {
                switch (action)
                {
                    case "CRIT": return $"暴击 {value:F0}";
                    case "DMG": return $"-{value:F0}";
                    case "HEAL": return $"+{value:F0}";
                    case "DOT": return string.IsNullOrEmpty(dotType) ? $"持续伤害 {value:F0}" : $"{dotType} {value:F0}";
                    case "KILL": return "击杀";
                    case "DEATH": return "死亡";
                    case "STRESS": return $"压力 {value:F1}";
                    case "BUFF+": return "增益 +";
                    case "BUFF-": return "增益 -";
                    case "BUFF!": return "增益消耗";
                    case "DEBUFF+": return "减益 +";
                    case "DEBUFF-": return "减益 -";
                    case "DEBUFF!": return "减益消耗";
                    case "TOKEN+": return "Token +";
                    case "TOKEN-": return "Token -";
                    case "TOKEN!": return "Token 消耗";
                    case "TOKEN~": return "替换";
                    case "TOKENx": return "抵消";
                    case "STATUS+": return "状态 +";
                    case "STATUS-": return "状态 -";
                    default: return action;
                }
            }

            switch (action)
            {
                case "CRIT": return $"CRIT {value:F0}";
                case "DMG": return $"-{value:F0}";
                case "HEAL": return $"+{value:F0}";
                case "DOT": return string.IsNullOrEmpty(dotType) ? $"DOT {value:F0}" : $"{dotType} {value:F0}";
                case "KILL": return "KILL";
                case "DEATH": return "DEATH";
                case "STRESS": return $"STRESS {value:F1}";
                case "BUFF+": return "BUFF +";
                case "BUFF-": return "BUFF -";
                case "BUFF!": return "BUFF USED";
                case "DEBUFF+": return "DEBUFF +";
                case "DEBUFF-": return "DEBUFF -";
                case "DEBUFF!": return "DEBUFF USED";
                case "TOKEN+": return "TOKEN +";
                case "TOKEN-": return "TOKEN -";
                case "TOKEN!": return "TOKEN USED";
                case "TOKEN~": return "SWAP";
                case "TOKENx": return "NEGATE";
                case "STATUS+": return "STATUS +";
                case "STATUS-": return "STATUS -";
                default: return action;
            }
        }

        private static bool IsZh(string value)
        {
            return value != null &&
                   (value.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("zh-", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("cn", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsEn(string value)
        {
            return value != null &&
                   (value.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("en-", StringComparison.OrdinalIgnoreCase));
        }

        private static string En(string key)
        {
            switch (key)
            {
                case "pluginLoading": return "DD2 Damage Meter v{0} loading...";
                case "settingsLoaded": return "Settings loaded: AutoStartRecording={0}, ExportDirectory='{1}', Language='{2}'";
                case "languageChanged": return "Language changed to {0}.";
                case "damageMeterTitle": return "<b>DD2 Damage Meter</b>";
                case "remoteHost": return "Remote Host";
                case "hideHint": return "F2 Hide";
                case "resetHint": return "F3 Reset";
                case "exportHint": return "F4 Export";
                case "heroes": return "Heroes";
                case "enemies": return "Enemies";
                case "log": return "Log";
                case "recording": return "Recording ({0})";
                case "recordRun": return "Record Run";
                case "runStats": return "Run Stats";
                case "exportCsv": return "Export CSV";
                case "exportDir": return "Export Dir";
                case "autoRec": return "Auto Rec";
                case "languageButton": return "Lang: {0}";
                case "remoteUnavailable": return "Remote DamageMeter unavailable: {0}";
                case "unknown": return "unknown";
                case "totalDamage": return "Total Damage: {0:F0}";
                case "statsResetEachBattle": return "Stats reset each battle";
                case "contribution": return "Contribution";
                case "settingsTitle": return "DD2 Damage Meter Settings";
                case "exportDirectory": return "Export Directory";
                case "language": return "Language";
                case "save": return "Save";
                case "reset": return "Reset";
                case "saved": return "Saved";
                case "default": return "Default";
                case "runStatsTitle": return "<b>Run Stats</b>";
                case "noBattles": return "No battles recorded yet. Use Record button to start.";
                case "close": return "Close";
                case "heroesRecorded": return "--- Heroes ({0} battles{1}) ---";
                case "remoteSuffix": return ", remote";
                case "enemiesHeader": return "--- Enemies ---";
                case "battleLogTitle": return "<b>Battle Log</b>";
                case "remoteCombatLog": return "Remote host combat log (bufflog not synced)";
                case "buffDebuff": return "Buff/Debuff";
                case "noCombatLog": return "No combat log yet...";
                case "noRemoteSnapshot": return "No remote DamageMeter snapshot yet...";
                case "noRemoteCombatLog": return "No remote combat log yet...";
                case "round": return "--- Round {0} ---";
                case "buffLogTitle": return "<b>Buff/Debuff Log</b>";
                case "noStatusLog": return "No buff/debuff log yet...";
                case "statusSummary": return "Hero B+{0} D+{1} -{2} Used {3}   |   Enemy B+{4} D+{5} -{6} Used {7}";
                case "reportTitle": return "=== DD2 Damage Meter Report ===";
                case "generated": return "Generated: {0:yyyy-MM-dd HH:mm:ss}";
                case "sectionHeroes": return "--- Heroes ---";
                case "sectionEnemies": return "--- Enemies ---";
                case "sectionBattleLog": return "--- Battle Log ---";
                case "sectionStatusSummary": return "--- Buff/Debuff Summary ---";
                case "sectionStatusLog": return "--- Buff/Debuff Log ---";
                case "noContribution": return "No contribution recorded.";
                case "csvTitle": return "=== Run Stats CSV Export ===";
                case "battlesRecorded": return "Battles Recorded: {0}";
                case "exported": return "Exported: {0:yyyy-MM-dd HH:mm:ss}";
                case "csvHeroesHeader": return "Name,Battles,TotalDMG,DOT_DMG,OVK_DMG,RawDMG_Taken,ActualDMG_Taken,HealingDone,HealingReceived,Stress,Kills,Crits,AvoidRate,AvoidChecks,AvoidedAttacks,DodgeAvoids,MissAvoids,ComboApplied";
                case "csvContributionHeader": return "Name,TotalContribution,BonusDamage,VulnerableDamage,ShieldPrevented,GuardProtected,ComboConsumed,ContributionPct";
                case "csvPerBattle": return "--- Per Battle Breakdown ---";
                case "csvBattle": return "Battle #{0} ({1:HH:mm:ss})";
                case "csvTeamHeader": return "Team,Name,DMG,DOT_DMG,OVK_DMG,RawDMG_Taken,ActualDMG_Taken,HealingDone,HealingReceived,Kills,Crits,AvoidRate,AvoidChecks,AvoidedAttacks,DodgeAvoids,MissAvoids,ComboApplied";
                case "csvHero": return "Hero";
                case "csvEnemy": return "Enemy";
                case "name": return "Name";
                case "bat": return "Bat";
                case "dmg": return "DMG";
                case "dot": return "DOT";
                case "ovk": return "OVK";
                case "rawTkn": return "RawTkn";
                case "healOut": return "Heal+";
                case "healIn": return "HealIn";
                case "kills": return "Kills";
                case "crits": return "Crits";
                case "avoidPct": return "Avoid%";
                case "pct": return "%";
                case "contrib": return "Contrib";
                case "dmgPlus": return "Dmg+";
                case "vulnerable": return "Vuln";
                case "vulnerableShort": return "Vuln";
                case "shield": return "Shield";
                case "guard": return "Guard";
                case "waste": return "Waste";
                case "wasteShort": return "W";
                case "comboApplied": return "Combo+";
                case "comboConsumed": return "Combo!";
                default: return key;
            }
        }

        private static string Zh(string key)
        {
            switch (key)
            {
                case "pluginLoading": return "DD2 伤害统计 v{0} 加载中...";
                case "settingsLoaded": return "设置已加载：自动录制={0}，导出目录='{1}'，语言='{2}'";
                case "languageChanged": return "语言已切换为 {0}。";
                case "damageMeterTitle": return "<b>DD2 伤害统计</b>";
                case "remoteHost": return "远端主机";
                case "hideHint": return "F2 隐藏";
                case "resetHint": return "F3 重置";
                case "exportHint": return "F4 导出";
                case "heroes": return "我方";
                case "enemies": return "敌方";
                case "log": return "日志";
                case "recording": return "录制中 ({0})";
                case "recordRun": return "录制本局";
                case "runStats": return "本局统计";
                case "exportCsv": return "导出 CSV";
                case "exportDir": return "导出目录";
                case "autoRec": return "自动录制";
                case "languageButton": return "语言：{0}";
                case "remoteUnavailable": return "远端 DamageMeter 不可用：{0}";
                case "unknown": return "未知";
                case "totalDamage": return "总伤害：{0:F0}";
                case "statsResetEachBattle": return "每场战斗会自动重置统计";
                case "contribution": return "贡献";
                case "settingsTitle": return "DD2 伤害统计设置";
                case "exportDirectory": return "导出目录";
                case "language": return "语言";
                case "save": return "保存";
                case "reset": return "重置";
                case "saved": return "已保存";
                case "default": return "默认";
                case "runStatsTitle": return "<b>本局统计</b>";
                case "noBattles": return "还没有录制战斗。使用录制按钮开始。";
                case "close": return "关闭";
                case "heroesRecorded": return "--- 我方（{0} 场战斗{1}）---";
                case "remoteSuffix": return "，远端";
                case "enemiesHeader": return "--- 敌方 ---";
                case "battleLogTitle": return "<b>战斗日志</b>";
                case "remoteCombatLog": return "远端主机战斗日志（不同步 BuffLog）";
                case "buffDebuff": return "Buff/Debuff";
                case "noCombatLog": return "暂无战斗日志...";
                case "noRemoteSnapshot": return "暂无远端 DamageMeter 快照...";
                case "noRemoteCombatLog": return "暂无远端战斗日志...";
                case "round": return "--- 第 {0} 轮 ---";
                case "buffLogTitle": return "<b>Buff/Debuff 日志</b>";
                case "noStatusLog": return "暂无 Buff/Debuff 日志...";
                case "statusSummary": return "我方 增益+{0} 减益+{1} 移除{2} 消耗{3}   |   敌方 增益+{4} 减益+{5} 移除{6} 消耗{7}";
                case "reportTitle": return "=== DD2 伤害统计报告 ===";
                case "generated": return "生成时间：{0:yyyy-MM-dd HH:mm:ss}";
                case "sectionHeroes": return "--- 我方 ---";
                case "sectionEnemies": return "--- 敌方 ---";
                case "sectionBattleLog": return "--- 战斗日志 ---";
                case "sectionStatusSummary": return "--- Buff/Debuff 汇总 ---";
                case "sectionStatusLog": return "--- Buff/Debuff 日志 ---";
                case "noContribution": return "没有记录到贡献。";
                case "csvTitle": return "=== 本局统计 CSV 导出 ===";
                case "battlesRecorded": return "录制战斗数：{0}";
                case "exported": return "导出时间：{0:yyyy-MM-dd HH:mm:ss}";
                case "csvHeroesHeader": return "名称,战斗数,总伤害,DOT伤害,溢出伤害,理论承伤,实际承伤,造成治疗,受到治疗,压力,击杀,暴击,闪避率,受击判定,闪避次数,闪避Token,致盲Miss,有效Combo";
                case "csvContributionHeader": return "名称,总贡献,增伤贡献,易伤贡献,减伤贡献,守护贡献,Combo消耗贡献,贡献占比";
                case "csvPerBattle": return "--- 单场明细 ---";
                case "csvBattle": return "第 {0} 场（{1:HH:mm:ss}）";
                case "csvTeamHeader": return "队伍,名称,伤害,DOT伤害,溢出伤害,理论承伤,实际承伤,造成治疗,受到治疗,击杀,暴击,闪避率,受击判定,闪避次数,闪避Token,致盲Miss,有效Combo";
                case "csvHero": return "我方";
                case "csvEnemy": return "敌方";
                case "name": return "名称";
                case "bat": return "战";
                case "dmg": return "伤害";
                case "dot": return "DOT";
                case "ovk": return "溢出";
                case "rawTkn": return "承伤";
                case "healOut": return "治疗";
                case "healIn": return "受疗";
                case "kills": return "击杀";
                case "crits": return "暴击";
                case "avoidPct": return "闪避%";
                case "pct": return "%";
                case "contrib": return "贡献";
                case "dmgPlus": return "增伤";
                case "vulnerable": return "易伤";
                case "vulnerableShort": return "易伤";
                case "shield": return "护盾";
                case "guard": return "守护";
                case "waste": return "浪费";
                case "wasteShort": return "废";
                case "comboApplied": return "Combo+";
                case "comboConsumed": return "Combo!";
                default: return key;
            }
        }
    }
}
