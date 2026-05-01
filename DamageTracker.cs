using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Actor.Events;
using Assets.Code.Combat.Events;
using Assets.Code.Events;
using Assets.Code.Library;
using Assets.Code.Skill.Events;
using Assets.Code.Utils;
using UnityEngine;

namespace DD2DamageMeter
{
    public class DamageTracker
    {
        public class ActorStats
        {
            public uint ActorGuid;
            public string ActorName;
            public int TeamIndex;
            public float TotalDamageDealt;
            public float TotalDamageReceived;
            public float RawDamageReceived; // Pre-shield/block damage, plus unreduced DOT damage
            public float OverkillDamageDealt;
            public float TotalHealingDone;
            public float TotalHealingReceived;
            public float TotalStressReceived;
            public int Kills;
            public int Crits;
            public int IncomingAttacks;
            public int AvoidedAttacks;
            public int DodgeAvoids;
            public int MissAvoids;
            // DOT tracking
            public float DotDamageDealt;
            public float DotDamageReceived;
        }

        // DOT source tracking: (targetGuid, dotDefId) -> performerGuid
        private readonly Dictionary<(uint target, string dotDefId), uint> _dotPerformerCache = new Dictionary<(uint, string), uint>();
        private readonly Dictionary<uint, ProjectedHealth> _dotProjectedHp = new Dictionary<uint, ProjectedHealth>();
        // Boss phase detection: guid -> last known name
        private readonly Dictionary<uint, string> _lastKnownName = new Dictionary<uint, string>();

        private Dictionary<uint, ActorStats> _stats = new Dictionary<uint, ActorStats>();
        private readonly object _lock = new object();
        private ActorStats[] _playerSnapshot = Array.Empty<ActorStats>();
        private ActorStats[] _enemySnapshot = Array.Empty<ActorStats>();
        private volatile bool _snapshotDirty = true;
        private static readonly Dictionary<uint, string> _nameCache = new Dictionary<uint, string>();

        public IReadOnlyList<ActorStats> PlayerStats => _playerSnapshot;
        public IReadOnlyList<ActorStats> EnemyStats => _enemySnapshot;
        public float PlayerTotalDamage { get; private set; }
        public float EnemyTotalDamage { get; private set; }

        private struct ProjectedHealth
        {
            public int Frame;
            public float Hp;
        }

        public void OnBattleBegin(EventBattleBegin evt)
        {
            Reset();
            Plugin.Log.LogInfo("DamageTracker: Auto-reset on battle begin.");
        }

        public void OnBattleStartRound(Assets.Code.Combat.Events.EventBattleStartRound evt)
        {
            try
            {
                lock (_lock)
                {
                    RefreshEnemyNames();
                }
            }
            catch { }
        }

        private void RefreshEnemyNames()
        {
            foreach (var kvp in _stats)
            {
                uint guid = kvp.Key;
                ActorStats stats = kvp.Value;
                if (stats.TeamIndex == 0) continue;
                if (IsCorpseActor(guid, null)) continue;

                _nameCache.Remove(guid);
                string newName = TryResolveName(guid);
                if (IsCorpseName(newName))
                {
                    _nameCache.Remove(guid);
                    continue;
                }

                if (newName != null && newName != stats.ActorName)
                {
                    Plugin.Log.LogInfo($"Name changed: {stats.ActorName} -> {newName} (guid={guid})");
                    if (!_lastKnownName.ContainsKey(guid))
                        _lastKnownName[guid] = stats.ActorName;
                    string oldName = _lastKnownName.ContainsKey(guid) ? _lastKnownName[guid] : stats.ActorName;
                    if (newName != oldName)
                    {
                        stats.ActorName = newName + " (P" + CountPhaseChanges(guid, newName) + ")";
                        _lastKnownName[guid] = newName;
                    }
                    else
                    {
                        stats.ActorName = newName;
                    }
                }
            }
            _snapshotDirty = true;
        }

        private static bool IsCorpseName(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                   (value.IndexOf("corpse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    value.IndexOf("尸体", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private int CountPhaseChanges(uint guid, string currentName)
        {
            int count = 1;
            foreach (var kvp in _stats)
            {
                if (kvp.Key == guid) continue;
                if (kvp.Value.ActorName.StartsWith(currentName) || currentName.StartsWith(kvp.Value.ActorName.Split('(')[0].Trim()))
                    count++;
            }
            return count;
        }

        public void OnDotAdded(Assets.Code.Dot.Events.EventDotAdded evt)
        {
            try
            {
                lock (_lock)
                {
                    if (evt.m_Actor == null || evt.m_DotDefinition == null) return;
                }
            }
            catch { }
        }

        public void OnDotApplied(Assets.Code.Dot.Events.EventDotApplied evt)
        {
            try
            {
                lock (_lock)
                {
                    if (evt.m_effectApplyCombinedResult == null) return;
                    float healthChange = evt.m_effectApplyCombinedResult.HealthChange;
                    uint targetGuid = evt.m_actorGuid;
                    uint performerGuid = ExtractPerformerGuid(evt.m_effectApplyCombinedResult);

                    if (healthChange < -0.01f) // DOT damage
                    {
                        float rawDotDmg = -healthChange;
                        float dotDmg = GetEffectiveDotDamage(targetGuid, rawDotDmg);
                        float overkill = Math.Max(0f, rawDotDmg - dotDmg);
                        var targetStats = GetOrCreate(targetGuid, -1);
                        targetStats.DotDamageReceived += dotDmg;
                        targetStats.TotalDamageReceived += dotDmg;
                        targetStats.RawDamageReceived += rawDotDmg;
                        // Exclude self-damage DOTs from damage dealt
                        if (performerGuid != 0 && performerGuid != targetGuid)
                        {
                            var performerStats = GetOrCreate(performerGuid, -1);
                            performerStats.DotDamageDealt += dotDmg;
                            performerStats.TotalDamageDealt += dotDmg;
                            performerStats.OverkillDamageDealt += overkill;
                        }
                        UpdateAndMarkDirty();
                    }
                    else if (healthChange > 0.01f) // HoT heal
                    {
                        float healAmt = healthChange;
                        var targetStats = GetOrCreate(targetGuid, -1);
                        targetStats.TotalHealingReceived += healAmt;
                        if (performerGuid != 0)
                        {
                            var performerStats = GetOrCreate(performerGuid, -1);
                            performerStats.TotalHealingDone += healAmt;
                        }
                        UpdateAndMarkDirty();
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnDotApplied error: {ex.Message}"); }
        }

        private static FieldInfo _trackerChangeAmountsField;
        private static FieldInfo _trackerPerformerGuidsField;
        private static bool _trackerReflectionInit;

        private uint ExtractPerformerGuid(Assets.Code.Effect.EffectApplyCombinedResult result)
        {
            try
            {
                if (!_trackerReflectionInit)
                {
                    _trackerReflectionInit = true;
                    var resultType = typeof(Assets.Code.Effect.EffectApplyCombinedResult);
                    _trackerChangeAmountsField = resultType.GetField("m_ChangeAmounts", BindingFlags.NonPublic | BindingFlags.Instance);
                    var changeAmountType = resultType.GetNestedType("ChangeAmount", BindingFlags.NonPublic);
                    if (changeAmountType != null)
                        _trackerPerformerGuidsField = changeAmountType.GetField("m_PerformerActorGuids", BindingFlags.Public | BindingFlags.Instance);
                }
                if (_trackerChangeAmountsField == null || _trackerPerformerGuidsField == null) return 0;
                var changeAmounts = _trackerChangeAmountsField.GetValue(result) as System.Collections.IDictionary;
                if (changeAmounts == null) return 0;
                foreach (var entry in changeAmounts)
                {
                    var valueProp = entry.GetType().GetProperty("Value");
                    if (valueProp == null) continue;
                    var changeAmount = valueProp.GetValue(entry);
                    if (changeAmount == null) continue;
                    var guids = _trackerPerformerGuidsField.GetValue(changeAmount) as System.Collections.IList;
                    if (guids != null && guids.Count > 0 && guids[0] is uint guid)
                        return guid;
                }
            }
            catch { }
            return 0;
        }

        private float GetEffectiveDotDamage(uint targetGuid, float rawDamage)
        {
            if (rawDamage <= 0f) return 0f;
            int frame = Time.frameCount;
            float hp;
            if (_dotProjectedHp.TryGetValue(targetGuid, out var projected) && projected.Frame == frame)
            {
                hp = projected.Hp;
            }
            else
            {
                hp = TryResolveHpRawPublic(targetGuid, out var resolvedHp) ? Mathf.Max(0f, resolvedHp) : rawDamage;
            }

            float effective = Mathf.Min(rawDamage, Mathf.Max(0f, hp));
            _dotProjectedHp[targetGuid] = new ProjectedHealth { Frame = frame, Hp = Mathf.Max(0f, hp - effective) };
            return effective;
        }

        private static float GetEffectiveDamageBeforeApply(uint targetGuid, float rawDamage, Dictionary<uint, float> projectedHp)
        {
            if (rawDamage <= 0f) return 0f;
            float hp;
            if (!projectedHp.TryGetValue(targetGuid, out hp))
            {
                hp = TryResolveHpRawPublic(targetGuid, out var resolvedHp) ? Mathf.Max(0f, resolvedHp) : rawDamage;
            }

            float effective = Mathf.Min(rawDamage, Mathf.Max(0f, hp));
            projectedHp[targetGuid] = Mathf.Max(0f, hp - effective);
            return effective;
        }

        private static float GetEffectiveDamageAfterEvent(uint targetGuid, float rawDamage)
        {
            if (rawDamage <= 0f) return 0f;
            if (!TryResolveHpRawPublic(targetGuid, out var hpAfter)) return rawDamage;
            return Mathf.Min(rawDamage, Mathf.Max(0f, hpAfter + rawDamage));
        }

        public void OnHealthDamage(EventActorHealthDamage evt)
        {
            try
            {
                lock (_lock)
                {
                    string srcName = evt.m_SourceType?.ToString() ?? "";
                    if (srcName.ToLowerInvariant().Contains("dot")) return;
                    var s = GetOrCreate(evt.m_ActorGuid, evt.m_TeamIndex);
                    s.TotalDamageReceived += GetEffectiveDamageAfterEvent(evt.m_ActorGuid, evt.m_HealthDamage);
                    UpdateAndMarkDirty();
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnHealthDamage error: {ex.Message}"); }
        }

        public void OnStressDamage(EventStressDamage evt)
        {
            try { lock (_lock) { var s = GetOrCreate(evt.m_ActorGuid, evt.m_TeamIndex); s.TotalStressReceived += evt.m_StressDamageAmount; UpdateAndMarkDirty(); } }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnStressDamage error: {ex.Message}"); }
        }

        public void OnSkillFinalizeResults(EventSkillFinalizeResults evt)
        {
            try
            {
                lock (_lock)
                {
                    uint pid = evt.PerformerGuid;
                    int pt = evt.m_PerformerTeamIndex;
                    GetOrCreate(pid, pt);
                    var projectedHp = new Dictionary<uint, float>();
                    foreach (var ar in evt.ActorResults)
                    {
                        if (ar == null) continue;
                        uint arPid = ar.m_PerformerActorGuid;
                        uint arTid = ar.m_TargetActorGuid;
                        var ps = GetOrCreate(arPid, -1);
                        if (arPid == pid && pt >= 0 && ps.TeamIndex < 0) ps.TeamIndex = pt;
                        TrackAvoidance(ar, arTid);
                        if (ar.IsCrit) ps.Crits++;
                        // Exclude self-damage from damage dealt (performer == target)
                        bool isSelfDamage = (arPid == arTid);
                        if (ar.IsDamaging)
                        {
                            float effectiveDamage = GetEffectiveDamageBeforeApply(arTid, ar.HealthDamage, projectedHp);
                            if (!isSelfDamage)
                            {
                                ps.TotalDamageDealt += effectiveDamage;
                                ps.OverkillDamageDealt += Math.Max(0f, ar.HealthDamage - effectiveDamage);
                            }
                        }
                        // Track raw (pre-shield) damage received on target
                        // BaseHealthDamage excludes negative multipliers (block/shield tokens)
                        // HealthDamage is the actual damage after shield reduction
                        if (ar.IsDamaging || ar.IsBlocked)
                        {
                            var ts = GetOrCreate(arTid, ar.m_TargetTeamIndex);
                            float rawDmg = ar.BaseHealthDamage; // pre-shield damage
                            if (rawDmg > 0f) ts.RawDamageReceived += rawDmg;
                        }
                        if (ar.IsHealthHeal) { ps.TotalHealingDone += ar.HealthHeal; GetOrCreate(arTid, ar.m_TargetTeamIndex).TotalHealingReceived += ar.HealthHeal; }
                    }
                    UpdateAndMarkDirty();
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnSkillFinalizeResults error: {ex.Message}"); }
        }

        private void TrackAvoidance(Assets.Code.Skill.SkillCalculation.ActorResult ar, uint targetGuid)
        {
            if (ar.IsFriendly) return;
            bool isAvoided = ar.IsDodge || ar.IsMiss;
            bool hasAvoidanceCheck = isAvoided || HasAvoidanceCheck(ar);
            if (!hasAvoidanceCheck) return;

            var targetStats = GetOrCreate(targetGuid, ar.m_TargetTeamIndex);
            targetStats.IncomingAttacks++;
            if (ar.IsDodge)
            {
                targetStats.AvoidedAttacks++;
                targetStats.DodgeAvoids++;
            }
            else if (ar.IsMiss)
            {
                targetStats.AvoidedAttacks++;
                targetStats.MissAvoids++;
            }
        }

        private static bool HasAvoidanceCheck(Assets.Code.Skill.SkillCalculation.ActorResult ar)
        {
            const float epsilon = 0.0001f;
            return TryGetHitChance(ar, "m_PerformerHit", out var performerChance) && performerChance < 1f - epsilon ||
                   TryGetHitChance(ar, "m_TargetHit", out var targetChance) && targetChance < 1f - epsilon;
        }

        private static bool TryGetHitChance(Assets.Code.Skill.SkillCalculation.ActorResult ar, string fieldName, out float chance)
        {
            chance = 1f;
            try
            {
                FieldInfo resultField = GetActorResultHitField(fieldName);
                if (resultField == null) return false;

                object hit = resultField.GetValue(ar);
                if (hit == null) return false;

                FieldInfo chanceField = GetHitChanceField(hit.GetType());
                if (chanceField == null) return false;

                chance = (float)chanceField.GetValue(hit);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static FieldInfo GetActorResultHitField(string fieldName)
        {
            if (!_avoidanceReflectionInit)
            {
                _avoidanceReflectionInit = true;
                var resultType = typeof(Assets.Code.Skill.SkillCalculation.ActorResult);
                _performerHitField = resultType.GetField("m_PerformerHit", BindingFlags.NonPublic | BindingFlags.Instance);
                _targetHitField = resultType.GetField("m_TargetHit", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            return fieldName == "m_PerformerHit" ? _performerHitField : _targetHitField;
        }

        private static FieldInfo GetHitChanceField(Type hitType)
        {
            if (_hitChanceField == null && hitType != null)
                _hitChanceField = hitType.GetField("m_ToHitChance", BindingFlags.Public | BindingFlags.Instance);
            return _hitChanceField;
        }

        private static bool _avoidanceReflectionInit;
        private static FieldInfo _performerHitField;
        private static FieldInfo _targetHitField;
        private static FieldInfo _hitChanceField;

        public void OnActorDeath(EventActorDeath evt)
        {
            try
            {
                lock (_lock)
                {
                    GetOrCreate(evt.m_DyingActorGuid, evt.m_DyingActorTeamIndex);
                    if (!ShouldCountKill(evt))
                    {
                        _snapshotDirty = true;
                        return;
                    }

                    var countedKillers = new HashSet<uint>();
                    foreach (uint kg in evt.m_KillingActorGuids)
                    {
                        if (kg == 0 || kg == evt.m_DyingActorGuid || !countedKillers.Add(kg)) continue;
                        if (IsCorpseActor(kg, null)) continue;
                        int killerTeam = GetKnownTeamIndex(kg);
                        int dyingTeam = evt.m_DyingActorTeamIndex >= 0 ? evt.m_DyingActorTeamIndex : GetKnownTeamIndex(evt.m_DyingActorGuid);
                        if (killerTeam >= 0 && dyingTeam >= 0 && killerTeam == dyingTeam) continue;
                        GetOrCreate(kg, -1).Kills++;
                    }
                    _snapshotDirty = true;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"OnActorDeath error: {ex.Message}"); }
        }

        private static bool ShouldCountKill(EventActorDeath evt)
        {
            if (evt == null) return false;
            if (IsCorpseActor(evt.m_DyingActorGuid, evt.m_DyingActorDataId)) return false;
            return evt.m_KillingActorGuids != null && evt.m_KillingActorGuids.Count > 0;
        }

        private static bool IsCorpseActor(uint guid, string actorDataId)
        {
            try
            {
                var actor = TryResolveActor(guid);
                if (actor != null && actor.ContainsTag(CommonActorTags.TAG_CORPSE)) return true;
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(actorDataId))
                {
                    var actorClass = SingletonMonoBehaviour<Library<string, ActorDataClass>>.Instance?.GetLibraryElement(actorDataId);
                    if (actorClass != null && actorClass.ContainsTag(CommonActorTags.TAG_CORPSE)) return true;
                }
            }
            catch { }

            return !string.IsNullOrEmpty(actorDataId) &&
                   IsCorpseName(actorDataId);
        }

        private int GetKnownTeamIndex(uint guid)
        {
            if (_stats.TryGetValue(guid, out var stats) && stats.TeamIndex >= 0) return stats.TeamIndex;
            return ResolveTeamIndex(guid);
        }

        private void UpdateAndMarkDirty() { UpdateTeamTotals(); _snapshotDirty = true; }

        private ActorStats GetOrCreate(uint guid, int teamIndex)
        {
            if (_stats.TryGetValue(guid, out var existing))
            {
                if (teamIndex >= 0 && existing.TeamIndex < 0) existing.TeamIndex = teamIndex;
                if (existing.ActorName.StartsWith("Actor_") && !IsCorpseActor(guid, null))
                {
                    string resolvedName = TryResolveName(guid);
                    if (!IsCorpseName(resolvedName)) existing.ActorName = resolvedName ?? existing.ActorName;
                }
                return existing;
            }
            string name = !IsCorpseActor(guid, null) ? TryResolveName(guid) : null;
            if (IsCorpseName(name)) name = null;
            name = name ?? $"Actor_{guid}";
            int resolvedTeam = teamIndex;
            if (resolvedTeam < 0) resolvedTeam = ResolveTeamIndex(guid);
            var stats = new ActorStats { ActorGuid = guid, ActorName = name, TeamIndex = resolvedTeam >= 0 ? resolvedTeam : 1 };
            _stats[guid] = stats;
            return stats;
        }

        private static int ResolveTeamIndex(uint guid)
        {
            try
            {
                if (!_libraryReflectionInit) { _libraryReflectionInit = true; InitLibraryReflection(); }
                if (_libraryInstance == null || _getLibraryElement == null) return -1;
                var actor = _getLibraryElement.Invoke(_libraryInstance, new object[] { guid }) as ActorInstance;
                if (actor != null) return actor.TeamIndex;
            }
            catch { }
            return -1;
        }

        public static string TryResolveNamePublic(uint guid) => TryResolveName(guid);

        public static bool TryResolveHpRawPublic(uint guid, out float hp)
        {
            hp = 0f;
            try
            {
                var actor = TryResolveActor(guid);
                if (actor == null) return false;
                hp = actor.HpRaw;
                return true;
            }
            catch { return false; }
        }

        private static string TryResolveName(uint guid)
        {
            if (_nameCache.TryGetValue(guid, out var cached)) return cached;
            try
            {
                var actor = TryResolveActor(guid);
                if (actor == null) return null;
                string id = actor.ActorDataId;
                if (string.IsNullOrEmpty(id) && actor.ActorDataClass != null) id = actor.ActorDataClass.Id;
                if (!string.IsNullOrEmpty(id)) { string n = CleanName(id); _nameCache[guid] = n; return n; }
            }
            catch { }
            return null;
        }

        private static object _libraryInstance;
        private static MethodInfo _getLibraryElement;
        private static bool _libraryReflectionInit;

        private static ActorInstance TryResolveActor(uint guid)
        {
            if (!_libraryReflectionInit) { _libraryReflectionInit = true; InitLibraryReflection(); }
            if (_libraryInstance == null || _getLibraryElement == null) return null;
            return _getLibraryElement.Invoke(_libraryInstance, new object[] { guid }) as ActorInstance;
        }

        private static void InitLibraryReflection()
        {
            try
            {
                Plugin.Log.LogInfo("InitLibraryReflection: scanning...");
                Type genericLibDef = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { genericLibDef = asm.GetType("Assets.Code.Library.Library`2"); if (genericLibDef != null) break; } catch { }
                }
                if (genericLibDef == null) { Plugin.Log.LogWarning("  Library`2 not found!"); return; }
                var libraryType = genericLibDef.MakeGenericType(typeof(uint), typeof(ActorInstance));
                var di = libraryType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (di != null) _libraryInstance = di.GetValue(null);
                if (_libraryInstance == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "PlayFab") continue;
                        try
                        {
                            foreach (var t in asm.GetTypes())
                            {
                                if (!t.IsGenericTypeDefinition || t.GetGenericArguments().Length != 1) continue;
                                if (t.Name != "Singleton`1" && t.Name != "SingletonMonoBehaviour`1") continue;
                                try
                                {
                                    var cs = t.MakeGenericType(libraryType);
                                    var ip = cs.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                                    if (ip != null) { _libraryInstance = ip.GetValue(null); }
                                    if (_libraryInstance != null) break;
                                }
                                catch { }
                            }
                            if (_libraryInstance != null) break;
                        }
                        catch (ReflectionTypeLoadException ex2)
                        {
                            foreach (var t in ex2.Types)
                            {
                                if (t == null || !t.IsGenericTypeDefinition || t.GetGenericArguments().Length != 1) continue;
                                if (t.Name != "Singleton`1" && t.Name != "SingletonMonoBehaviour`1") continue;
                                try
                                {
                                    var cs = t.MakeGenericType(libraryType);
                                    var ip = cs.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                                    if (ip != null) { _libraryInstance = ip.GetValue(null); }
                                    if (_libraryInstance != null) break;
                                }
                                catch { }
                            }
                            if (_libraryInstance != null) break;
                        }
                    }
                }
                if (_libraryInstance == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            foreach (var t in asm.GetTypes())
                            {
                                if (!t.IsClass || !t.IsAbstract) continue;
                                try
                                {
                                    var p = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                                    if (p != null && p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == genericLibDef)
                                    {
                                        _libraryInstance = p.GetValue(null);
                                        if (_libraryInstance != null) break;
                                    }
                                }
                                catch { }
                            }
                            if (_libraryInstance != null) break;
                        }
                        catch { }
                    }
                }
                if (_libraryInstance != null) _getLibraryElement = libraryType.GetMethod("GetLibraryElement", new Type[] { typeof(uint) });
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"InitLibraryReflection error: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static string CleanName(string rawId)
        {
            if (string.IsNullOrEmpty(rawId)) return rawId;
            var parts = rawId.Split('_');
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpperInvariant(parts[i][0]));
                if (parts[i].Length > 1) sb.Append(parts[i].Substring(1));
            }
            return sb.ToString();
        }

        private void UpdateTeamTotals()
        {
            float pd = 0f, ed = 0f;
            foreach (var kvp in _stats) { if (kvp.Value.TeamIndex == 0) pd += kvp.Value.TotalDamageDealt; else ed += kvp.Value.TotalDamageDealt; }
            PlayerTotalDamage = pd; EnemyTotalDamage = ed;
        }

        public void RefreshSnapshot()
        {
            if (!_snapshotDirty) return;
            var players = new List<ActorStats>(); var enemies = new List<ActorStats>();
            foreach (var kvp in _stats) { if (kvp.Value.TeamIndex == 0) players.Add(kvp.Value); else enemies.Add(kvp.Value); }
            players.Sort((a, b) => b.TotalDamageDealt.CompareTo(a.TotalDamageDealt));
            enemies.Sort((a, b) => b.TotalDamageDealt.CompareTo(a.TotalDamageDealt));
            _playerSnapshot = players.ToArray(); _enemySnapshot = enemies.ToArray(); _snapshotDirty = false;
        }

        public void Reset()
        {
            lock (_lock)
            {
                _stats = new Dictionary<uint, ActorStats>();
                _dotPerformerCache.Clear();
                _dotProjectedHp.Clear();
                _lastKnownName.Clear();
                _playerSnapshot = Array.Empty<ActorStats>();
                _enemySnapshot = Array.Empty<ActorStats>();
                PlayerTotalDamage = 0f; EnemyTotalDamage = 0f; _snapshotDirty = true;
            }
        }
    }
}
