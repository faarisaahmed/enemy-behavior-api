using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EnemyBehaviorApi.Schema;
using Newtonsoft.Json;

namespace EnemyBehaviorApi.Annotations
{
    /// <summary>
    /// Loads annotation files and applies them over a freshly discovered profile.
    /// </summary>
    /// <remarks>
    /// The point of keeping this separate from <c>DiscoveryEngine</c> is that a boss should
    /// never require a code change. Reflection gets most enemies right; bosses are
    /// hand-built, often spread over several FSMs, and reliably confuse a generic
    /// classifier. Correcting them in JSON means the reflection engine stays general
    /// instead of accumulating an if-chain of special cases - which is the failure mode
    /// this whole design exists to avoid.
    /// </remarks>
    public sealed class AnnotationStore
    {
        private readonly Dictionary<string, EnemyAnnotation> _byEnemy = new Dictionary<string, EnemyAnnotation>(StringComparer.Ordinal);
        private readonly Action<string> _log;

        public AnnotationStore(Action<string> log = null) => _log = log ?? (_ => { });

        public int Count => _byEnemy.Count;

        /// <summary>Enemy ids that have annotations.</summary>
        public IEnumerable<string> AnnotatedEnemies => _byEnemy.Keys;

        /// <summary>
        /// Loads every <c>*.json</c> in <paramref name="directory"/>, in filename order.
        /// Missing directory is fine - annotations are optional.
        /// </summary>
        public void LoadDirectory(string directory)
        {
            _byEnemy.Clear();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                _log($"no annotation directory at '{directory}' - running on reflection alone");
                return;
            }

            foreach (string path in Directory.GetFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                try { LoadFile(path); }
                catch (Exception e) { _log($"annotation file '{Path.GetFileName(path)}' failed to load: {e.Message}"); }
            }

            _log($"loaded annotations for {_byEnemy.Count} enemy id(s)");
        }

        public void LoadFile(string path)
        {
            var document = JsonConvert.DeserializeObject<AnnotationDocument>(File.ReadAllText(path));
            if (document?.Enemies == null) return;

            if (!SchemaVersion.IsCompatible(document.Version))
                _log($"annotation file '{Path.GetFileName(path)}' declares schema v{document.Version}, this build is v{SchemaVersion.Current} - applying anyway");

            foreach (var entry in document.Enemies)
            {
                // Last file wins on a whole-enemy conflict. Merging two partial
                // annotations for one enemy field-by-field would be quietly
                // unpredictable; taking the later file wholesale at least fails loudly.
                if (_byEnemy.ContainsKey(entry.Key))
                    _log($"'{Path.GetFileName(path)}' replaces existing annotation for '{entry.Key}'");
                _byEnemy[entry.Key] = entry.Value;
            }
        }

        public EnemyAnnotation For(string enemyId) =>
            enemyId != null && _byEnemy.TryGetValue(enemyId, out var annotation) ? annotation : null;

        /// <summary>
        /// Applies annotations to <paramref name="profile"/> in place. No-op when the enemy
        /// has none.
        /// </summary>
        public void Apply(EnemyProfile profile)
        {
            var annotation = For(profile?.EnemyId);
            if (annotation == null) return;

            profile.IsAnnotated = true;

            if (annotation.IgnoreFsms?.Count > 0) DropIgnoredFsms(profile, annotation.IgnoreFsms);
            if (!string.IsNullOrEmpty(annotation.PrimaryFsm)) profile.PrimaryFsm = annotation.PrimaryFsm;

            foreach (var entry in annotation.States ?? new Dictionary<string, StateAnnotation>())
                ApplyState(profile, entry.Key, entry.Value);

            foreach (string key in annotation.DecisionStates ?? new List<string>())
            {
                var stateRef = StateRef.Parse(key);
                if (stateRef != null && !profile.DecisionStates.Contains(stateRef))
                    profile.DecisionStates.Add(stateRef);
            }

            foreach (var entry in annotation.Parameters ?? new Dictionary<string, ParameterAnnotation>())
            {
                var parameter = profile.FindParameter(entry.Key);
                if (parameter == null)
                {
                    profile.Warnings.Add($"annotation names unknown parameter '{entry.Key}'");
                    continue;
                }
                if (!string.IsNullOrEmpty(entry.Value.DisplayName)) parameter.Name = entry.Value.DisplayName;
                parameter.Min = entry.Value.Min ?? parameter.Min;
                parameter.Max = entry.Value.Max ?? parameter.Max;
            }
        }

        private static void DropIgnoredFsms(EnemyProfile profile, List<string> ignored)
        {
            bool IsIgnored(StateRef s)
            {
                string fsmKey = (s.FsmPath.Length == 0 ? "" : s.FsmPath + "/") + s.FsmName;
                return ignored.Contains(fsmKey, StringComparer.Ordinal) ||
                       ignored.Contains(s.FsmName, StringComparer.Ordinal);
            }

            profile.Actions.RemoveAll(a => IsIgnored(a.State));
            profile.Movements.RemoveAll(m => IsIgnored(m.State));
            profile.DecisionStates.RemoveAll(IsIgnored);
            profile.GlobalParameters.RemoveAll(p =>
                ignored.Contains((p.FsmPath.Length == 0 ? "" : p.FsmPath + "/") + p.FsmName, StringComparer.Ordinal) ||
                ignored.Contains(p.FsmName, StringComparer.Ordinal));
        }

        private void ApplyState(EnemyProfile profile, string stateKey, StateAnnotation annotation)
        {
            var stateRef = StateRef.Parse(stateKey);
            if (stateRef == null)
            {
                profile.Warnings.Add($"annotation key '{stateKey}' is not a valid state reference");
                return;
            }

            // Refuse to create a behaviour for a state the enemy does not have. An
            // annotation outranks the classifier by design - it is stamped Confidence 1.0 -
            // so an unvalidated one injects fabricated certainty that no consumer can tell
            // apart from a real finding. A typo, or a file written against another build,
            // should warn rather than silently invent an attack.
            bool stateExists = profile.AllStates.Count == 0 ||
                               profile.AllStates.Any(st => string.Equals(st.Key, stateRef.Key, StringComparison.Ordinal));

            if (!stateExists)
            {
                profile.Warnings.Add(
                    $"annotation names state '{stateKey}', which is not on this enemy - ignored. " +
                    "Check EnemyProfile.AllStates for the real keys.");
                return;
            }

            var action = profile.Actions.FirstOrDefault(a => a.State.Equals(stateRef));
            var movement = profile.Movements.FirstOrDefault(m => m.State.Equals(stateRef));
            string classify = annotation.Classify?.Trim().ToLowerInvariant();

            switch (classify)
            {
                case "none":
                    profile.Actions.RemoveAll(a => a.State.Equals(stateRef));
                    profile.Movements.RemoveAll(m => m.State.Equals(stateRef));
                    return;

                case "attack":
                    if (movement != null) profile.Movements.Remove(movement);
                    movement = null;
                    action = action ?? Promote<ActionDescriptor>(profile, stateRef, "attack");
                    break;

                case "movement":
                    if (action != null) profile.Actions.Remove(action);
                    action = null;
                    movement = movement ?? Promote<MovementDescriptor>(profile, stateRef, "move");
                    break;

                case "both":
                    action = action ?? Promote<ActionDescriptor>(profile, stateRef, "attack");
                    movement = movement ?? Promote<MovementDescriptor>(profile, stateRef, "move");
                    break;

                case null:
                case "":
                    break;

                default:
                    profile.Warnings.Add($"annotation for '{stateKey}' has unknown classify value '{annotation.Classify}'");
                    break;
            }

            if (action != null) ApplyCommon(action, annotation);
            if (movement != null) ApplyCommon(movement, annotation);

            if (action != null)
            {
                if (!string.IsNullOrEmpty(annotation.Shape) &&
                    Enum.TryParse(annotation.Shape, ignoreCase: true, out AttackShape shape))
                    action.Shape = shape;
                if (annotation.DamageAmount.HasValue) action.DamageAmount = annotation.DamageAmount.Value;
                if (!string.IsNullOrEmpty(annotation.TelegraphFor)) action.TelegraphFor = annotation.TelegraphFor;
            }

            if (movement != null)
            {
                if (!string.IsNullOrEmpty(annotation.Mode) &&
                    Enum.TryParse(annotation.Mode, ignoreCase: true, out MovementMode mode))
                    movement.Mode = mode;
                if (annotation.Speed.HasValue) movement.Speed = annotation.Speed.Value;
            }
        }

        private static void ApplyCommon(BehaviorDescriptor descriptor, StateAnnotation annotation)
        {
            if (!string.IsNullOrEmpty(annotation.DisplayName)) descriptor.DisplayName = annotation.DisplayName;

            foreach (string tag in annotation.Tags ?? new List<string>())
            {
                if (!descriptor.Tags.Contains(tag)) descriptor.Tags.Add(tag);
            }

            foreach (string trigger in annotation.TriggerEvents ?? new List<string>())
            {
                if (!descriptor.TriggerEvents.Contains(trigger)) descriptor.TriggerEvents.Add(trigger);
            }

            // Anything a human asserted is certain by definition; the whole reason the file
            // exists is that the classifier was not.
            descriptor.Confidence = 1f;
            descriptor.Source = descriptor.Source == DiscoverySource.Manual
                ? DiscoverySource.Manual
                : DiscoverySource.Annotated;
        }

        /// <summary>
        /// Creates a descriptor for a state discovery classified as nothing, so an
        /// annotation can declare a behaviour the engine missed entirely.
        /// </summary>
        private static T Promote<T>(EnemyProfile profile, StateRef stateRef, string idPrefix) where T : BehaviorDescriptor, new()
        {
            var descriptor = new T
            {
                Id = idPrefix + ":" + stateRef.Key,
                State = stateRef,
                DisplayName = stateRef.StateName,
                Confidence = 1f,
                Source = DiscoverySource.Manual,
            };

            if (descriptor is ActionDescriptor a) profile.Actions.Add(a);
            else if (descriptor is MovementDescriptor m) profile.Movements.Add(m);

            return descriptor;
        }
    }
}
