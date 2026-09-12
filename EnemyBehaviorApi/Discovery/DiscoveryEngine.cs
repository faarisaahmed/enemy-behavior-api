using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using EnemyBehaviorApi.Annotations;
using EnemyBehaviorApi.Schema;
using HutongGames.PlayMaker;
using UnityEngine;

namespace EnemyBehaviorApi.Discovery
{
    /// <summary>
    /// Builds an <see cref="EnemyProfile"/> from a live enemy GameObject: scan its FSMs,
    /// classify every state, extract tunable parameters, then let annotations correct the
    /// result.
    /// </summary>
    /// <remarks>
    /// Profiles are cached per enemy id. Discovery walks an entire FSM graph with
    /// reflection, which is not something to do mid-fight for every spawned husk, and the
    /// result describes the prefab so re-running it on a second instance would produce the
    /// same answer.
    /// </remarks>
    public sealed class DiscoveryEngine
    {
        private readonly Dictionary<string, EnemyProfile> _cache = new Dictionary<string, EnemyProfile>(StringComparer.Ordinal);
        private readonly AnnotationStore _annotations;
        private readonly Action<string> _log;

        public DiscoveryEngine(AnnotationStore annotations, Action<string> log = null)
        {
            _annotations = annotations;
            _log = log ?? (_ => { });
        }

        /// <summary>Profiles built so far this session.</summary>
        public IReadOnlyCollection<EnemyProfile> CachedProfiles => _cache.Values;

        /// <summary>Throws away cached profiles. Called when annotation files are reloaded.</summary>
        public void InvalidateCache() => _cache.Clear();

        /// <summary>
        /// The profile for this enemy, building it on first sight.
        /// </summary>
        /// <returns>Null if <paramref name="enemy"/> is null or carries no PlayMaker FSM.</returns>
        public EnemyProfile GetOrDiscover(GameObject enemy)
        {
            if (enemy == null) return null;

            string id = EnemyIdentity.For(enemy);
            if (id == null) return null;

            if (_cache.TryGetValue(id, out var cached))
            {
                // Re-scan when this instance carries FSMs the cached profile never saw.
                // Enemies do not always have their full component set by the time
                // HealthManager.Start runs - MossBone Crawler was first profiled with only
                // its "Noise Reaction" FSM present, so its actual behaviour was invisible
                // and the profile was cached that way for the rest of the session.
                if (!HasUnseenFsms(enemy, cached)) return cached;
                _log($"re-scanning {id}: this instance has FSMs the cached profile lacked");
                _cache.Remove(id);
            }

            var profile = Discover(enemy, id);
            if (profile == null) return null;

            _cache[id] = profile;
            _log($"discovered {profile}");
            return profile;
        }

        /// <summary>True when the live object has an FSM the cached profile did not record.</summary>
        private static bool HasUnseenFsms(GameObject enemy, EnemyProfile cached)
        {
            foreach (var component in enemy.GetComponentsInChildren<PlayMakerFSM>(includeInactive: true))
            {
                if (component == null) continue;
                string path = EnemyIdentity.RelativePath(enemy.transform, component.transform);
                string key = (path.Length == 0 ? "" : path + "/") + component.FsmName;
                if (!cached.ScannedFsms.Contains(key)) return true;
            }
            return false;
        }

        private EnemyProfile Discover(GameObject enemy, string enemyId)
        {
            var warnings = new List<string>();
            var fsms = FsmScanner.Scan(enemy, warnings);
            if (fsms.Count == 0) return null;

            var profile = new EnemyProfile
            {
                EnemyId = enemyId,
                SourceObjectName = enemy.name,
                DiscoveredInScene = enemy.scene.name,
                ScannedFsms = fsms.Select(f => f.Key).ToList(),
                Warnings = warnings,
            };

            var health = enemy.GetComponent<HealthManager>();
            if (health != null)
            {
                profile.HasHealthManager = true;
                profile.MaxHealth = health.hp;
            }

            // Only children that some state actually arms count as hitbox evidence. The
            // enemy's permanent body-contact damager would otherwise mark every state that
            // touches it as an attack, which is what produced "Caged Sleep: Melee".
            var damagers = FindDamagingChildren(enemy);
            var toggledDamagers = ToggledDamagers(damagers, fsms);
            PropagateArmedHitboxes(fsms, toggledDamagers);

            // Descriptor ids must be unique across both lists and stable across re-scans.
            // Keying on the state's address gives both; the counter only breaks ties when
            // an FSM has two states with the same name, which PlayMaker permits.
            var usedIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var fsm in fsms)
            {
                foreach (var state in fsm.States)
                {
                    var verdict = BehaviorClassifier.Classify(state, toggledDamagers);
                    var stateRef = new StateRef(state.FsmPath, state.FsmName, state.StateName);

                    if (BehaviorClassifier.IsDecisionState(state, verdict))
                        profile.DecisionStates.Add(stateRef);

                    // A state can be both - a charge that damages on contact produces an
                    // ActionDescriptor and a MovementDescriptor over the same StateRef.
                    ActionDescriptor action = null;
                    MovementDescriptor movement = null;

                    if (verdict.IsAttack)
                    {
                        action = BuildAction(state, stateRef, verdict, usedIds, damagers);
                        profile.Actions.Add(action);
                    }

                    if (verdict.IsMovement)
                    {
                        movement = BuildMovement(state, stateRef, verdict, usedIds);
                        profile.Movements.Add(movement);
                    }

                    profile.AllStates.Add(new StateSummary
                    {
                        Key = stateRef.Key,
                        FsmName = state.FsmName,
                        StateName = state.StateName,
                        ActionTypes = new List<string>(state.ActionTypes),
                        IncomingEvents = new List<string>(state.IncomingEvents),
                        Transitions = state.Transitions.Select(t => t.Key + " -> " + t.Value).ToList(),
                        ActionId = action?.Id,
                        MovementId = movement?.Id,
                        AttackScore = verdict.AttackScore,
                        MovementScore = verdict.MovementScore,
                        IsStartState = state.IsStartState,
                        IsDecisionState = BehaviorClassifier.IsDecisionState(state, verdict),
                    });
                }

                profile.GlobalParameters.AddRange(ExtractFsmVariables(fsm));
            }

            LinkTelegraphsAndRecoveries(profile, fsms);
            profile.PrimaryFsm = ChoosePrimaryFsm(profile, fsms);

            _annotations?.Apply(profile);
            return profile;
        }

        /// <summary>
        /// Descendants carrying a hero-damaging component, by object name.
        /// </summary>
        /// <remarks>
        /// Silksong puts hero damage on persistent <c>DamageHero</c> components rather than
        /// in FSM actions, so this - not <c>SetDamageHero</c> - is where an attack's damage
        /// number actually lives.
        /// </remarks>
        private static Dictionary<string, DamageHero> FindDamagingChildren(GameObject enemy)
        {
            var found = new Dictionary<string, DamageHero>(StringComparer.Ordinal);
            foreach (var damager in enemy.GetComponentsInChildren<DamageHero>(includeInactive: true))
            {
                if (damager == null) continue;
                string name = damager.gameObject.name;
                if (!found.ContainsKey(name)) found[name] = damager;
            }
            return found;
        }

        /// <summary>
        /// The damaging children that some state switches on - the real hitboxes, as
        /// opposed to a permanently active body-contact damager.
        /// </summary>
        /// <remarks>
        /// This distinction is what separates "swings at you" from "is solid and hurts to
        /// touch". Without it, every state that so much as references the body collider
        /// scored as an attack, and Bone Thumper came back with five attacks of which three
        /// were sleeping or waking up.
        /// </remarks>
        private static HashSet<string> ToggledDamagers(Dictionary<string, DamageHero> damagers, List<ScannedFsm> fsms)
        {
            var toggled = new HashSet<string>(StringComparer.Ordinal);
            if (damagers.Count == 0) return toggled;

            foreach (var fsm in fsms)
            foreach (var state in fsm.States)
            foreach (string armed in state.ArmedObjects)
            {
                if (damagers.ContainsKey(armed)) toggled.Add(armed);
            }

            // A damager that starts inactive is a hitbox by construction, even if the
            // action that arms it was not readable.
            foreach (var entry in damagers)
            {
                if (entry.Value != null && !entry.Value.gameObject.activeSelf) toggled.Add(entry.Key);
            }

            return toggled;
        }

        private ActionDescriptor BuildAction(ScannedState state, StateRef stateRef, Classification verdict,
                                             HashSet<string> usedIds, Dictionary<string, DamageHero> damagers)
        {
            var descriptor = new ActionDescriptor
            {
                Id = UniqueId("attack", stateRef, usedIds),
                State = stateRef,
                DisplayName = state.StateName,
                Shape = verdict.Shape,
                Confidence = verdict.ConfidenceFor(attack: true),
                ActionTypes = new List<string>(state.ActionTypes),
                Tags = verdict.Tags.OrderBy(t => t, StringComparer.Ordinal).ToList(),
                TriggerEvents = new List<string>(state.IncomingEvents),
                ExitStates = state.Transitions.Select(t => t.Value).Distinct().ToList(),
                DamageAmount = ReadDamageAmount(state, damagers),
                // No unconditional way out means the state runs until something interrupts
                // it - a sustained beam rather than a one-shot swipe.
                IsSustained = state.Transitions.Count > 0 &&
                              state.Transitions.All(t => !string.Equals(t.Key, "FINISHED", StringComparison.Ordinal)),
            };

            descriptor.Parameters = ExtractStateParameters(state, stateRef, descriptor.Id);
            return descriptor;
        }

        private MovementDescriptor BuildMovement(ScannedState state, StateRef stateRef, Classification verdict, HashSet<string> usedIds)
        {
            var descriptor = new MovementDescriptor
            {
                Id = UniqueId("move", stateRef, usedIds),
                State = stateRef,
                DisplayName = state.StateName,
                Mode = verdict.Mode,
                Confidence = verdict.ConfidenceFor(attack: false),
                ActionTypes = new List<string>(state.ActionTypes),
                Tags = verdict.Tags.OrderBy(t => t, StringComparer.Ordinal).ToList(),
                TriggerEvents = new List<string>(state.IncomingEvents),
                ExitStates = state.Transitions.Select(t => t.Value).Distinct().ToList(),
                IsTargeted = verdict.Tags.Contains("targeted"),
                IsAirborne = verdict.Tags.Contains("airborne"),
                DamagesOnContact = verdict.IsAttack,
                Speed = ReadSpeed(state),
            };

            descriptor.Parameters = ExtractStateParameters(state, stateRef, descriptor.Id);
            return descriptor;
        }

        private static string UniqueId(string prefix, StateRef stateRef, HashSet<string> used)
        {
            string baseId = prefix + ":" + stateRef.Key;
            string id = baseId;
            for (int i = 2; !used.Add(id); i++) id = baseId + "#" + i;
            return id;
        }

        /// <summary>Tunable fields on the actions inside one state.</summary>
        private List<ParameterRef> ExtractStateParameters(ScannedState state, StateRef stateRef, string ownerId)
        {
            var parameters = new List<ParameterRef>();

            for (int i = 0; i < state.Actions.Count; i++)
            {
                var action = state.Actions[i];
                string actionType = state.ActionTypes[i];

                foreach (var field in FsmScanner.TunableFields(action))
                {
                    ParameterKind? kind = KindOf(field.FieldType);
                    if (kind == null) continue;

                    object raw;
                    try { raw = field.GetValue(action); }
                    catch (Exception) { continue; }

                    // An FsmVar bound to a variable is that variable's business, not this
                    // state's - tuning it here would silently change every other state
                    // reading the same variable. Those surface as GlobalParameters instead.
                    if (raw is NamedVariable named && named.UsesVariable) continue;

                    parameters.Add(new ParameterRef
                    {
                        Id = ownerId + "/" + i + "." + field.Name,
                        Name = field.Name,
                        Kind = kind.Value,
                        Source = ParameterSource.ActionField,
                        State = stateRef,
                        FsmPath = stateRef.FsmPath,
                        FsmName = stateRef.FsmName,
                        ActionIndex = i,
                        ActionTypeName = actionType,
                        FieldName = field.Name,
                        DiscoveredValue = Describe(raw),
                    });
                }
            }

            return parameters;
        }

        /// <summary>FSM-wide variables - the numbers shared between states.</summary>
        private static List<ParameterRef> ExtractFsmVariables(ScannedFsm fsm)
        {
            var parameters = new List<ParameterRef>();
            FsmVariables variables = fsm.Component?.Fsm?.Variables;
            if (variables == null) return parameters;

            foreach (var variable in variables.GetAllNamedVariables() ?? Array.Empty<NamedVariable>())
            {
                if (variable == null) continue;
                ParameterKind? kind = KindOf(variable.GetType());
                if (kind == null) continue;

                parameters.Add(new ParameterRef
                {
                    Id = "var:" + fsm.Key + "/" + variable.Name,
                    Name = variable.Name,
                    Kind = kind.Value,
                    Source = ParameterSource.FsmVariable,
                    FsmPath = fsm.FsmPath,
                    FsmName = fsm.FsmName,
                    VariableName = variable.Name,
                    DiscoveredValue = Describe(variable),
                });
            }

            return parameters;
        }

        private static ParameterKind? KindOf(Type type)
        {
            if (type == typeof(FsmFloat) || type == typeof(float)) return ParameterKind.Float;
            if (type == typeof(FsmInt) || type == typeof(int)) return ParameterKind.Int;
            if (type == typeof(FsmBool) || type == typeof(bool)) return ParameterKind.Bool;
            if (type == typeof(FsmVector2)) return ParameterKind.Vector2;
            return null;
        }

        private static string Describe(object value)
        {
            try
            {
                object unwrapped = value is NamedVariable named ? named.RawValue : value;
                return Convert.ToString(unwrapped, CultureInfo.InvariantCulture);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The damage this state deals: from the hitbox it arms, or failing that from a
        /// <c>SetDamageHero*</c> action.
        /// </summary>
        /// <remarks>
        /// Order matters. The first version only looked at <c>SetDamageHero</c> actions and
        /// returned 0 for every attack in the survey, because Moss Grotto enemies contain
        /// no such action - the number is a field on the <c>DamageHero</c> component of the
        /// child the state switches on.
        /// </remarks>
        private static int ReadDamageAmount(ScannedState state, Dictionary<string, DamageHero> damagers)
        {
            foreach (string armed in state.ArmedObjects)
            {
                if (damagers.TryGetValue(armed, out var damager) && damager != null && damager.damageDealt > 0)
                    return damager.damageDealt;
            }

            return ReadDamageFromActions(state);
        }

        /// <summary>Falls back to a <c>SetDamageHero*</c> action, for enemies that use them.</summary>
        private static int ReadDamageFromActions(ScannedState state)
        {
            for (int i = 0; i < state.Actions.Count; i++)
            {
                if (state.ActionTypes[i].IndexOf("DamageHero", StringComparison.Ordinal) < 0) continue;

                foreach (var field in state.Actions[i].GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (field.Name.IndexOf("amount", StringComparison.OrdinalIgnoreCase) < 0 &&
                        field.Name.IndexOf("damage", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    try
                    {
                        object raw = field.GetValue(state.Actions[i]);
                        if (raw is FsmInt fsmInt && !fsmInt.UsesVariable) return fsmInt.Value;
                        if (raw is int plain) return plain;
                    }
                    catch (Exception) { /* next field */ }
                }
            }
            return 0;
        }

        /// <summary>Speed a movement state sets up, in Unity units per second.</summary>
        /// <remarks>
        /// Looking only for a field literally named "speed" returned 0 for every movement
        /// in the first survey. Silksong sets motion with <c>SetVelocity2d</c> and
        /// <c>AccelerateToX</c>, whose tunable fields are <c>x</c>, <c>y</c> and
        /// <c>vector</c> - so the number a consumer wants to scale is there, just not under
        /// that name. Takes the largest magnitude found, since a state that sets both axes
        /// is described better by its dominant component than by an arbitrary one.
        /// </remarks>
        private static float ReadSpeed(ScannedState state)
        {
            float best = 0f;

            for (int i = 0; i < state.Actions.Count; i++)
            {
                if (!VelocityAction.IsMatch(state.ActionTypes[i])) continue;

                foreach (var field in state.Actions[i].GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!SpeedField.IsMatch(field.Name)) continue;

                    try
                    {
                        switch (field.GetValue(state.Actions[i]))
                        {
                            case FsmFloat f when !f.UsesVariable:
                                best = Math.Max(best, Math.Abs(f.Value));
                                break;
                            case FsmVector2 v when !v.UsesVariable:
                                best = Math.Max(best, v.Value.magnitude);
                                break;
                            case float plain:
                                best = Math.Max(best, Math.Abs(plain));
                                break;
                        }
                    }
                    catch (Exception) { /* next field */ }
                }
            }

            return best;
        }

        private static readonly System.Text.RegularExpressions.Regex VelocityAction =
            new System.Text.RegularExpressions.Regex(
                @"^SetVelocity|^AccelerateTo|^AddForce|^Decelerate|^ChaseObject|^DistanceFly|^DistanceWalk|^IdleBuzz",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex SpeedField =
            new System.Text.RegularExpressions.Regex(
                @"(?i)^(speed|x|y|vector|velocity|magnitude|force|target|accel\w*)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Marks states that inherit a live hitbox from the state that transitioned into
        /// them.
        /// </summary>
        /// <remarks>
        /// One hop only, and deliberately so. Following the chain further would eventually
        /// mark a whole FSM as armed, since nothing guarantees a disarm appears on every
        /// path; one hop covers the common wind-up-then-commit shape without that risk.
        /// </remarks>
        private static void PropagateArmedHitboxes(List<ScannedFsm> fsms, HashSet<string> toggledDamagers)
        {
            if (toggledDamagers.Count == 0) return;

            foreach (var fsm in fsms)
            {
                var armingStates = new HashSet<string>(
                    fsm.States.Where(st => st.ArmedObjects.Any(toggledDamagers.Contains))
                              .Select(st => st.StateName),
                    StringComparer.Ordinal);

                if (armingStates.Count == 0) continue;

                foreach (var state in fsm.States)
                {
                    if (armingStates.Contains(state.StateName)) continue;
                    if (state.DisarmedObjects.Any(toggledDamagers.Contains)) continue;
                    state.InheritsArmedHitbox = state.IncomingStates.Any(armingStates.Contains);
                }
            }
        }

        /// <summary>
        /// Chains wind-ups to the attacks they precede, and attacks to what they recover
        /// into.
        /// </summary>
        /// <remarks>
        /// A state tagged "telegraph" that leads to exactly one attack is that attack's
        /// wind-up. This matters for the Override tier: firing the strike alone skips the
        /// tell, which produces an enemy that is unreadable rather than merely harder, so
        /// the API needs to be able to tell a caller which descriptor is the honest entry
        /// point.
        /// </remarks>
        private static void LinkTelegraphsAndRecoveries(EnemyProfile profile, List<ScannedFsm> fsms)
        {
            var attacksByState = new Dictionary<string, ActionDescriptor>(StringComparer.Ordinal);
            foreach (var attack in profile.Actions)
            {
                if (!attacksByState.ContainsKey(attack.State.Key)) attacksByState[attack.State.Key] = attack;
            }

            foreach (var fsm in fsms)
            {
                foreach (var state in fsm.States)
                {
                    string key = new StateRef(state.FsmPath, state.FsmName, state.StateName).Key;
                    if (!attacksByState.TryGetValue(key, out var descriptor)) continue;

                    var downstreamAttacks = state.Transitions
                        .Select(t => new StateRef(state.FsmPath, state.FsmName, t.Value).Key)
                        .Where(attacksByState.ContainsKey)
                        .Select(k => attacksByState[k])
                        .Distinct()
                        .ToList();

                    if (descriptor.HasTag("telegraph") && downstreamAttacks.Count == 1)
                        descriptor.TelegraphFor = downstreamAttacks[0].Id;

                    // A single exit that isn't itself an attack is the recovery.
                    var exits = state.Transitions.Select(t => t.Value).Distinct().ToList();
                    if (exits.Count == 1 && downstreamAttacks.Count == 0)
                    {
                        var recoveryRef = new StateRef(state.FsmPath, state.FsmName, exits[0]);
                        descriptor.RecoveryState = profile.AllBehaviors()
                            .FirstOrDefault(b => b.State.Equals(recoveryRef))?.Id;
                    }
                }
            }
        }

        /// <summary>
        /// The FSM that drives the enemy: whichever one holds the most classified states.
        /// </summary>
        /// <remarks>
        /// Heuristic, not a fact. Enemies with a "Control" FSM plus several small ones
        /// (hit response, audio, corpse) land on Control every time, which is what makes
        /// the default target for Override-tier transitions usually right. Bosses with
        /// several genuine behaviour FSMs need an annotation to say which.
        /// </remarks>
        private static string ChoosePrimaryFsm(EnemyProfile profile, List<ScannedFsm> fsms)
        {
            if (fsms.Count == 0) return null;
            if (fsms.Count == 1) return fsms[0].Key;

            var counts = profile.AllBehaviors()
                .GroupBy(b => (b.State.FsmPath.Length == 0 ? "" : b.State.FsmPath + "/") + b.State.FsmName)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            return counts.Count == 0
                ? fsms[0].Key
                : counts.OrderByDescending(kvp => kvp.Value).First().Key;
        }
    }
}
