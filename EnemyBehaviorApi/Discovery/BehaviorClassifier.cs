using System;
using System.Collections.Generic;
using System.Linq;
using EnemyBehaviorApi.Schema;

namespace EnemyBehaviorApi.Discovery
{
    /// <summary>The verdict on one scanned state.</summary>
    public sealed class Classification
    {
        public float AttackScore;
        public float MovementScore;
        public AttackShape Shape = AttackShape.Unknown;
        public MovementMode Mode = MovementMode.Unknown;
        public readonly HashSet<string> Tags = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Strength of the signal that set <see cref="Shape"/>, so a stronger one can replace it.</summary>
        internal float ShapeHintStrength;

        /// <summary>Strength of the signal that set <see cref="Mode"/>.</summary>
        internal float ModeHintStrength;

        /// <summary>True when the state clears the bar for an <see cref="ActionDescriptor"/>.</summary>
        public bool IsAttack => AttackScore >= SignalTable.ClassificationThreshold;

        /// <summary>True when the state clears the bar for a <see cref="MovementDescriptor"/>.</summary>
        public bool IsMovement => MovementScore >= SignalTable.ClassificationThreshold;

        /// <summary>
        /// 0..1 confidence in whichever reading won, from how decisively it beat the other.
        /// </summary>
        /// <remarks>
        /// A state scoring 4 attack / 0 movement is a confident attack. One scoring 2 / 1.8
        /// is a coin flip and should say so - those are the charge-attacks and the
        /// dash-with-a-hitbox, which genuinely are both, and a consumer deserves to know
        /// the engine is unsure rather than being handed a crisp wrong answer.
        /// </remarks>
        public float ConfidenceFor(bool attack)
        {
            float mine = attack ? AttackScore : MovementScore;
            float other = attack ? MovementScore : AttackScore;
            if (mine <= 0f) return 0f;

            // Saturating in strength, discounted by how close the runner-up got.
            float strength = Math.Min(1f, mine / 4f);
            float margin = other <= 0f ? 1f : Math.Min(1f, mine / (mine + other) * 2f - 0.5f);
            return Math.Max(0f, Math.Min(1f, strength * Math.Max(0.2f, margin)));
        }
    }

    /// <summary>
    /// Scores a scanned state against <see cref="SignalTable"/> to decide whether it is an
    /// attack, locomotion, both, or neither.
    /// </summary>
    public static class BehaviorClassifier
    {
        /// <summary>
        /// Weight applied to state-name evidence relative to action evidence. Names help but
        /// lie; what the actions do is the ground truth.
        /// </summary>
        private const float NameWeight = 0.6f;

        /// <summary>
        /// Extra attack weight when an activate-style action switches *on* a child that
        /// carries a real hero-damaging component and is not permanently active.
        /// </summary>
        private const float ConfirmedHitboxWeight = 2.5f;

        /// <summary>Attack weight for a state that inherits a hitbox armed by its predecessor.</summary>
        private const float InheritedHitboxWeight = 1.5f;

        /// <param name="state">The scanned state to judge.</param>
        /// <param name="damagingChildNames">
        /// Names of descendants that carry a <c>DamageHero</c> *and* are toggled rather than
        /// permanently on. Lets a bare ActivateGameObject be read as "arms its hitbox"
        /// instead of "turns on a glow" - without every state that touches the enemy's
        /// always-on body collider reading as an attack.
        /// </param>
        public static Classification Classify(ScannedState state, ISet<string> damagingChildNames)
        {
            var result = new Classification();
            if (state == null) return result;

            var seen = new Dictionary<ActionSignal, int>();
            foreach (string actionType in state.ActionTypes)
                Apply(result, actionType, SignalTable.Signals, 1f, seen);

            Apply(result, state.StateName, SignalTable.StateNameSignals, NameWeight);

            // Promote the weak activate signal when the object being switched on is known
            // to hurt the player. This is what separates a melee state from a vfx state,
            // and it is structural rather than name-based, so it works on states called
            // "State 7".
            if (damagingChildNames != null && damagingChildNames.Count > 0)
            {
                // Arming a hitbox is an attack. Disarming one is the end of an attack, or
                // going to sleep - the first table ignored the direction and classified
                // "Start Sleep" and "Caged Sleep" as melee attacks because they switch the
                // body collider off.
                foreach (string armed in state.ArmedObjects)
                {
                    if (!damagingChildNames.Contains(armed)) continue;
                    result.AttackScore += ConfirmedHitboxWeight;
                    result.Tags.Add("damages-hero");
                    result.Tags.Add("arms-hitbox");
                    if (ConfirmedHitboxWeight > result.ShapeHintStrength)
                    {
                        result.Shape = AttackShape.Melee;
                        result.ShapeHintStrength = ConfirmedHitboxWeight;
                    }
                    break;
                }

                // Worth less than arming one directly: inheritance is inferred from the
                // transition graph, so it is a weaker claim than seeing the action itself.
                if (state.InheritsArmedHitbox)
                {
                    result.AttackScore += InheritedHitboxWeight;
                    result.Tags.Add("hitbox-live");
                    if (InheritedHitboxWeight > result.ShapeHintStrength)
                    {
                        result.Shape = AttackShape.Melee;
                        result.ShapeHintStrength = InheritedHitboxWeight;
                    }
                }

                foreach (string disarmed in state.DisarmedObjects)
                {
                    if (!damagingChildNames.Contains(disarmed)) continue;
                    result.Tags.Add("disarms-hitbox");
                    break;
                }
            }

            // A state with nothing but a Wait and a transition is an idle, not a mystery.
            if (!result.IsAttack && !result.IsMovement &&
                result.Tags.Contains("waits") && state.ActionTypes.Count <= 2)
            {
                result.MovementScore = SignalTable.ClassificationThreshold;
                result.Mode = MovementMode.Idle;
            }

            if (result.IsAttack) result.Shape = RefineShape(result, state);
            if (result.IsMovement && result.Mode == MovementMode.Unknown) result.Mode = InferMode(result, state);

            return result;
        }

        private static void Apply(Classification into, string subject, IReadOnlyList<ActionSignal> signals, float weight)
        {
            Apply(into, subject, signals, weight, null);
        }

        /// <summary>
        /// Scores one subject against the table, with diminishing returns on a signal that
        /// has already matched.
        /// </summary>
        /// <param name="seen">
        /// Per-signal match counts across the whole state, or null to treat each call
        /// independently.
        /// </param>
        /// <remarks>
        /// Evidence should not scale with repetition. A state holding four
        /// <c>FloatTestToBool</c> actions is not four times as much a decision state as one
        /// holding a single one - but with flat weights it scored as though it were, and the
        /// accumulated penalty buried real signals underneath. Measured case: Bone Thumper's
        /// "Roll", its most-used attack at 14 entries, carries eight branching and
        /// perception actions that together outweighed its velocity actions and left it
        /// classified as nothing at all.
        /// </remarks>
        private static void Apply(Classification into, string subject, IReadOnlyList<ActionSignal> signals,
                                  float weight, Dictionary<ActionSignal, int> seen)
        {
            foreach (var signal in signals)
            {
                if (!signal.Pattern.IsMatch(subject)) continue;

                // 1, 1/2, 1/3, ... for the second and later matches of the same signal.
                float repeat = 1f;
                if (seen != null)
                {
                    seen.TryGetValue(signal, out int already);
                    repeat = 1f / (1 + already);
                    seen[signal] = already + 1;
                }
                float scale = weight * repeat;

                into.AttackScore += signal.Attack * scale;
                into.MovementScore += signal.Movement * scale;

                // Strongest hint wins, not the first one seen. Measured failure: "Get
                // Above" lists FaceObject before ChaseObjectV2, so first-match-wins called
                // a 27x-entered chase state a "Turn" at full confidence.
                float strength = signal.HintStrength * weight;

                if (signal.Shape != AttackShape.Unknown && strength > into.ShapeHintStrength)
                {
                    into.Shape = signal.Shape;
                    into.ShapeHintStrength = strength;
                }

                if (signal.Mode != MovementMode.Unknown && strength > into.ModeHintStrength)
                {
                    into.Mode = signal.Mode;
                    into.ModeHintStrength = strength;
                }

                foreach (string tag in signal.Tags) into.Tags.Add(tag);
            }
        }

        /// <summary>
        /// Last-resort shape when no signal carried a strong enough hint.
        /// </summary>
        /// <remarks>
        /// Deliberately thin now that hints are resolved by strength rather than by action
        /// order. The old name-matching override pass existed to paper over first-match-wins
        /// and is no longer needed.
        /// </remarks>
        private static AttackShape RefineShape(Classification result, ScannedState state)
        {
            if (result.Shape != AttackShape.Unknown) return result.Shape;
            if (result.Tags.Contains("spawns-object")) return AttackShape.Summon;
            if (result.Tags.Contains("arms-hitbox") || result.Tags.Contains("collision-driven")) return AttackShape.Melee;
            return AttackShape.Unknown;
        }

        private static MovementMode InferMode(Classification result, ScannedState state)
        {
            if (result.Tags.Contains("targeted")) return MovementMode.Chase;
            if (result.Tags.Contains("airborne")) return MovementMode.Fly;
            if (result.Tags.Contains("sets-velocity")) return MovementMode.Walk;
            return MovementMode.Unknown;
        }

        /// <summary>
        /// True when the state's job is choosing what the enemy does next rather than doing
        /// anything itself. These are what Override-tier authority suppresses.
        /// </summary>
        /// <remarks>
        /// Two shapes qualify. A state holding a <c>SendRandomEvent</c> is a chooser by
        /// construction. A state that does nothing but branch - no classified behaviour, no
        /// side effects, but three or more ways out - is one in practice; that is how a
        /// hand-built decision tree looks in PlayMaker.
        /// </remarks>
        public static bool IsDecisionState(ScannedState state, Classification classification)
        {
            if (state == null) return false;

            if (state.ActionTypes.Any(t =>
                    t.StartsWith("SendRandomEvent", StringComparison.Ordinal) ||
                    t.StartsWith("RandomEvent", StringComparison.Ordinal)))
                return true;

            // Team Cherry marks branch states with a trailing '?'. These were the hottest
            // unclassified states in the survey - "Bonk?" x13, "Slam L?" x12 - and they are
            // choosers, which makes them exactly what Override needs to suppress.
            if (classification.Tags.Contains("branch-state")) return true;

            bool branches = state.Transitions.Count >= 3;
            bool doesNothing = !classification.IsAttack && !classification.IsMovement;
            return branches && doesNothing && classification.Tags.Contains("decision");
        }
    }
}
