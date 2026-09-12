using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using EnemyBehaviorApi.Authority;
using EnemyBehaviorApi.Runtime;
using EnemyBehaviorApi.Schema;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EnemyBehaviorApi.Survey
{
    /// <summary>
    /// A read-only reconnaissance pass: claim every enemy at Observe, record what discovery
    /// thinks it is, record what it actually does, and write both to a file.
    /// </summary>
    /// <remarks>
    /// This exists to calibrate <c>SignalTable</c>, and it is deliberately Observe-only.
    /// The most valuable thing a survey collects is how often each state actually fires,
    /// and both higher tiers destroy exactly that: Override replaces the enemy's choices
    /// with the mod's, and Influence changes the numbers the profile just recorded. A
    /// survey that writes to the enemy is measuring itself.
    ///
    /// Inert until toggled. Nothing happens on launch.
    /// </remarks>
    [BepInPlugin(Guid, "Enemy Behavior Survey", "0.1.0")]
    [BepInDependency(Plugin.Guid)]
    public sealed class SurveyPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.example.enemybehavior.survey";

        /// <summary>Everything learned about one enemy kind, static and observed.</summary>
        private sealed class EnemySurvey
        {
            public string EnemyId;
            public int MaxHealth;
            public bool IsAnnotated;
            public string PrimaryFsm;
            public List<string> ScenesSeen = new List<string>();
            public int InstancesSeen;
            public List<string> ScannedFsms = new List<string>();
            public List<string> DecisionStates = new List<string>();
            public List<string> Warnings = new List<string>();

            public List<SurveyedBehavior> Attacks = new List<SurveyedBehavior>();
            public List<SurveyedBehavior> Movements = new List<SurveyedBehavior>();

            /// <summary>
            /// States seen at runtime that discovery classified as nothing. The richest
            /// source of missed signals - if an enemy spends its whole fight in a state
            /// the table ignored, the table is wrong.
            /// </summary>
            public Dictionary<string, int> UnclassifiedStateEntries = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>
            /// Every state discovery scanned but did not classify, with the actions in it
            /// and the scores it fell short by.
            /// </summary>
            /// <remarks>
            /// The first survey recorded only a name and a hit count for these, which was
            /// not enough to fix anything: "Control/Bonk? entered 13x" says a state matters
            /// without saying what is in it. This carries the evidence.
            /// </remarks>
            public List<UnclassifiedState> UnclassifiedStates = new List<UnclassifiedState>();

            [JsonIgnore] public readonly HashSet<string> SceneSet = new HashSet<string>(StringComparer.Ordinal);
            [JsonIgnore] public readonly HashSet<int> Instances = new HashSet<int>();
        }

        /// <summary>A scanned state the classifier produced nothing for.</summary>
        private sealed class UnclassifiedState
        {
            public string Key;
            public float AttackScore;
            public float MovementScore;
            public bool IsDecisionState;
            public List<string> ActionTypes = new List<string>();
            public List<string> Transitions = new List<string>();
            public int TimesEntered;
        }

        /// <summary>A descriptor plus how often it was actually entered.</summary>
        private sealed class SurveyedBehavior
        {
            public string Id;
            public string DisplayName;
            public string State;
            public string Kind;
            public float Confidence;
            public string Tier;
            public string Source;
            public List<string> Tags = new List<string>();
            public List<string> ActionTypes = new List<string>();
            public List<string> TriggerEvents = new List<string>();
            public int DamageAmount;
            public float Speed;
            public string TelegraphFor;
            public int ParameterCount;

            /// <summary>Times this state was entered while the survey was running.</summary>
            public int TimesEntered;
        }

        private readonly Dictionary<int, IEnemyHandle> _handles = new Dictionary<int, IEnemyHandle>();
        private readonly Dictionary<string, EnemySurvey> _surveys = new Dictionary<string, EnemySurvey>(StringComparer.Ordinal);

        private const float AutosaveIntervalSeconds = 30f;

        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<KeyCode> _dumpKey;
        private ConfigEntry<bool> _autoStart;
        private bool _active;
        private bool _pendingAutoStart;
        private bool _dirty;
        private string _outputPath;

        private void Awake()
        {
            if (!SchemaVersion.IsCompatible(SchemaVersion.Current))
            {
                Logger.LogError($"schema v{EnemyBehavior.Version} is not one this survey understands");
                enabled = false;
                return;
            }

            _autoStart = Config.Bind("Survey", "AutoStart", true,
                "Start recording as soon as the game loads, with no keypress. The survey only reads - it never " +
                "writes to an enemy - so leaving it on does not change how anything behaves.");

            _toggleKey = Config.Bind("Survey", "ToggleKey", KeyCode.F9,
                "Stops and restarts the survey by hand. Only needed if AutoStart is off, or to stop early.");
            _dumpKey = Config.Bind("Survey", "DumpKey", KeyCode.F10,
                "Writes the survey file immediately, without stopping. The file is also written on every " +
                "scene change, so a crash costs you at most one room.");

            // One file per session. The survey only holds this session's sightings in
            // memory, so a single fixed filename means launching a second save silently
            // overwrites the first run - which is exactly what happens when you sweep one
            // area, quit, and load a different save to sweep another.
            string dir = Path.Combine(Path.GetDirectoryName(Info.Location) ?? ".", "surveys");
            try { Directory.CreateDirectory(dir); }
            catch (Exception e) { Logger.LogError($"could not create '{dir}': {e.Message}"); }

            _outputPath = Path.Combine(dir, $"enemy-survey-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            SceneManager.activeSceneChanged += OnSceneChanged;

            // Defer the actual start: BepInDependency orders plugin loading, but waiting
            // for IsReady costs nothing and survives a load-order change.
            _pendingAutoStart = _autoStart.Value;

            Logger.LogInfo(_pendingAutoStart
                ? $"survey will start recording automatically. Output: {_outputPath}"
                : $"survey idle - press {_toggleKey.Value} to start. Output: {_outputPath}");
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnSceneChanged;
            if (_active) Stop();
        }

        private void Update()
        {
            if (_pendingAutoStart && EnemyBehavior.IsReady)
            {
                _pendingAutoStart = false;
                Start();
            }

            if (Input.GetKeyDown(_toggleKey.Value))
            {
                if (_active) Stop(); else Start();
            }
            else if (Input.GetKeyDown(_dumpKey.Value) && _active)
            {
                Write();
                Logger.LogInfo($"survey written ({_surveys.Count} enemy kinds so far)");
            }
        }

        // ---- Lifecycle ------------------------------------------------------------------

        private void Start()
        {
            _active = true;
            EnemyBehavior.EnemyAppeared += OnEnemyAppeared;
            EnemyBehavior.EnemyGone += OnEnemyGone;

            // Enemies already in the room were registered before we started listening.
            foreach (var enemy in EnemyBehavior.ActiveEnemies.ToList()) OnEnemyAppeared(enemy);

            StartCoroutine(AutosaveLoop());

            Logger.LogInfo($"=== SURVEY ON === watching {_handles.Count} enemy instance(s) in '{SceneManager.GetActiveScene().name}'");
        }

        private void Stop()
        {
            _active = false;
            EnemyBehavior.EnemyAppeared -= OnEnemyAppeared;
            EnemyBehavior.EnemyGone -= OnEnemyGone;

            foreach (var handle in _handles.Values) handle.Dispose();
            _handles.Clear();

            Write();
            LogSummary();
            Logger.LogInfo($"=== SURVEY OFF === {_surveys.Count} enemy kind(s) written to {_outputPath}");
        }

        private void OnSceneChanged(Scene from, Scene to)
        {
            if (_active) Write();
        }

        /// <summary>
        /// Writes periodically while recording, so a crash costs at most half a minute.
        /// </summary>
        /// <remarks>
        /// Scene-change writes alone are not enough: a boss fight happens entirely inside
        /// one scene, so the single most valuable stretch of a session is exactly the part
        /// that would never be flushed.
        /// </remarks>
        private System.Collections.IEnumerator AutosaveLoop()
        {
            var wait = new WaitForSeconds(AutosaveIntervalSeconds);
            while (_active)
            {
                yield return wait;
                if (_dirty) Write();
            }
        }

        // ---- Collection -----------------------------------------------------------------

        private void OnEnemyAppeared(EnemyInstance enemy)
        {
            if (!_active || _handles.ContainsKey(enemy.InstanceId)) return;

            // Observe only. This survey never writes to an enemy - see the class remarks.
            var handle = EnemyBehavior.Claim(enemy, Guid, AuthorityTier.Observe);
            if (handle == null) return;

            _handles[enemy.InstanceId] = handle;
            handle.StateChanged += OnStateChanged;

            RecordSighting(enemy);
        }

        private void OnEnemyGone(EnemyInstance enemy) => _handles.Remove(enemy.InstanceId);

        private void RecordSighting(EnemyInstance enemy)
        {
            var profile = enemy.Profile;
            if (profile == null) return;

            if (!_surveys.TryGetValue(profile.EnemyId, out var survey))
            {
                survey = BuildSurvey(profile);
                _surveys[profile.EnemyId] = survey;

                Logger.LogInfo($"[{survey.EnemyId}] {survey.MaxHealth} hp | " +
                               $"{survey.Attacks.Count} attack(s), {survey.Movements.Count} movement(s) | " +
                               $"{(profile.IsAnnotated ? "annotated" : "reflected")}" +
                               (survey.Warnings.Count > 0 ? $" | {survey.Warnings.Count} warning(s)" : ""));
            }

            string scene = SceneManager.GetActiveScene().name;
            if (survey.SceneSet.Add(scene)) survey.ScenesSeen.Add(scene);
            if (survey.Instances.Add(enemy.InstanceId)) survey.InstancesSeen = survey.Instances.Count;
            _dirty = true;
        }

        private static EnemySurvey BuildSurvey(EnemyProfile profile)
        {
            var survey = new EnemySurvey
            {
                EnemyId = profile.EnemyId,
                MaxHealth = profile.MaxHealth,
                IsAnnotated = profile.IsAnnotated,
                PrimaryFsm = profile.PrimaryFsm,
                ScannedFsms = new List<string>(profile.ScannedFsms),
                DecisionStates = profile.DecisionStates.Select(s => s.Key).ToList(),
                Warnings = new List<string>(profile.Warnings),
            };

            foreach (var state in profile.AllStates.Where(st => st.IsUnclassified))
            {
                survey.UnclassifiedStates.Add(new UnclassifiedState
                {
                    Key = state.Key,
                    AttackScore = state.AttackScore,
                    MovementScore = state.MovementScore,
                    IsDecisionState = state.IsDecisionState,
                    ActionTypes = new List<string>(state.ActionTypes),
                    Transitions = new List<string>(state.Transitions),
                });
            }

            foreach (var attack in profile.Actions)
            {
                var entry = Describe(attack, attack.Shape.ToString());
                entry.DamageAmount = attack.DamageAmount;
                entry.TelegraphFor = attack.TelegraphFor;
                survey.Attacks.Add(entry);
            }

            foreach (var move in profile.Movements)
            {
                var entry = Describe(move, move.Mode.ToString());
                entry.Speed = move.Speed;
                survey.Movements.Add(entry);
            }

            return survey;
        }

        private static SurveyedBehavior Describe(BehaviorDescriptor behavior, string kind) => new SurveyedBehavior
        {
            Id = behavior.Id,
            DisplayName = behavior.DisplayName,
            State = behavior.State.Key,
            Kind = kind,
            Confidence = behavior.Confidence,
            Tier = behavior.Tier.ToString(),
            Source = behavior.Source.ToString(),
            Tags = new List<string>(behavior.Tags),
            // The raw evidence the classification was made from. This is the field to read
            // when a verdict looks wrong.
            ActionTypes = new List<string>(behavior.ActionTypes),
            TriggerEvents = new List<string>(behavior.TriggerEvents),
            ParameterCount = behavior.Parameters.Count,
        };

        private void OnStateChanged(object sender, StateChangedEventArgs e)
        {
            var profile = e.Enemy.Profile;
            if (profile == null || !_surveys.TryGetValue(profile.EnemyId, out var survey)) return;

            if (e.Behavior == null)
            {
                // Unclassified. Counting these is the point - a state the enemy lives in
                // that discovery ignored is a hole in the signal table.
                string key = e.State.Key;
                survey.UnclassifiedStateEntries.TryGetValue(key, out int seen);
                survey.UnclassifiedStateEntries[key] = seen + 1;

                var unclassified = survey.UnclassifiedStates.FirstOrDefault(u => u.Key == key);
                if (unclassified != null) unclassified.TimesEntered++;
                _dirty = true;
                return;
            }

            var entry = survey.Attacks.Concat(survey.Movements).FirstOrDefault(b => b.Id == e.Behavior.Id);
            if (entry != null) entry.TimesEntered++;
            _dirty = true;
        }

        // ---- Output ---------------------------------------------------------------------

        private void Write()
        {
            try
            {
                var payload = new
                {
                    schemaVersion = EnemyBehavior.Version,
                    capturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    scene = SceneManager.GetActiveScene().name,
                    enemyKinds = _surveys.Count,
                    enemies = _surveys.Values.OrderBy(s => s.EnemyId, StringComparer.Ordinal).ToList(),
                };

                File.WriteAllText(_outputPath, JsonConvert.SerializeObject(payload, Formatting.Indented));
                _dirty = false;
            }
            catch (Exception e)
            {
                Logger.LogError($"could not write survey to '{_outputPath}': {e.Message}");
            }
        }

        private void LogSummary()
        {
            foreach (var survey in _surveys.Values.OrderBy(s => s.EnemyId, StringComparer.Ordinal))
            {
                Logger.LogInfo($"--- {survey.EnemyId} ({string.Join(", ", survey.ScenesSeen)}) ---");

                foreach (var entry in survey.Attacks.Concat(survey.Movements)
                             .OrderByDescending(b => b.TimesEntered).ThenByDescending(b => b.Confidence))
                {
                    Logger.LogInfo($"  {entry.Kind,-11} {entry.DisplayName,-22} " +
                                   $"{entry.Tier,-10} conf {entry.Confidence:0.00}  entered {entry.TimesEntered}x");
                }

                // A never-entered high-confidence attack and a hot unclassified state are
                // the two shapes that mean the table needs work.
                var missed = survey.UnclassifiedStateEntries.OrderByDescending(kvp => kvp.Value).Take(5).ToList();
                if (missed.Count > 0)
                    Logger.LogWarning($"  unclassified but active: {string.Join(", ", missed.Select(kvp => $"{kvp.Key} x{kvp.Value}"))}");
            }
        }
    }
}
