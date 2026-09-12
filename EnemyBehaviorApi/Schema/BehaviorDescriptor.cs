using System;
using System.Collections.Generic;

namespace EnemyBehaviorApi.Schema
{
    /// <summary>How a descriptor came to exist. Consumers that only trust curated data filter on this.</summary>
    public enum DiscoverySource
    {
        /// <summary>Classified by the reflection engine from the actions in the state.</summary>
        Reflected,

        /// <summary>Reflected, then corrected by an annotation file.</summary>
        Annotated,

        /// <summary>Declared entirely by an annotation file - discovery found nothing here.</summary>
        Manual,
    }

    /// <summary>How much weight to put on a descriptor, derived from <see cref="BehaviorDescriptor.Confidence"/>.</summary>
    public enum ConfidenceTier
    {
        /// <summary>Signals barely favoured this reading. Verify before acting on it.</summary>
        Tentative,

        /// <summary>A clear but not overwhelming margin.</summary>
        Probable,

        /// <summary>Strong, unambiguous evidence, or a human annotation.</summary>
        Confirmed,
    }

    /// <summary>
    /// One discovered behaviour: a state the enemy can be in, plus how to get it there.
    /// Shared by <see cref="ActionDescriptor"/> and <see cref="MovementDescriptor"/>.
    /// </summary>
    [Serializable]
    public abstract class BehaviorDescriptor
    {
        /// <summary>
        /// Stable id, unique within the owning <see cref="EnemyProfile"/>. Derived from
        /// <see cref="State"/>, so it survives a re-scan as long as the FSM is unchanged.
        /// This is what an annotation file keys on and what consumers pass to the API.
        /// </summary>
        public string Id { get; set; }

        /// <summary>The PlayMaker state that *is* this behaviour.</summary>
        public StateRef State { get; set; }

        /// <summary>Short human label. Defaults to the state name; annotations usually improve it.</summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Events that, sent to this FSM, move it into <see cref="State"/> - collected from
        /// every transition pointing at the state, plus the FSM's global transitions.
        /// Override-tier callers prefer these over a raw SetState, because firing the
        /// enemy's own event lets its entry actions and any guard states run normally.
        /// </summary>
        public List<string> TriggerEvents { get; set; } = new List<string>();

        /// <summary>
        /// States this one can leave to. Lets a consumer see, for instance, that an attack
        /// always returns through a recovery state.
        /// </summary>
        public List<string> ExitStates { get; set; } = new List<string>();

        /// <summary>Numbers inside this state that Influence-tier consumers may tune.</summary>
        public List<ParameterRef> Parameters { get; set; } = new List<ParameterRef>();

        /// <summary>
        /// The PlayMaker action type names found in the state, in order. The raw evidence
        /// behind the classification - useful when a consumer wants to do its own
        /// reasoning, and the first thing to look at when a classification is wrong.
        /// </summary>
        public List<string> ActionTypes { get; set; } = new List<string>();

        /// <summary>
        /// Free-form tags, lowercase. Discovery emits a few ("telegraph", "looping",
        /// "spawns-projectile"); annotations may add any others.
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// 0..1. How strongly the signals favoured this classification over the
        /// alternatives. Anything from an annotation is 1.
        /// </summary>
        public float Confidence { get; set; }

        public DiscoverySource Source { get; set; } = DiscoverySource.Reflected;

        /// <summary>
        /// <see cref="Confidence"/> bucketed, so a consumer can filter without picking a
        /// threshold and without treating a 0.07 guess the same as a certainty.
        /// </summary>
        /// <remarks>
        /// Worth stating plainly, because it is easy to read the wrong thing into a high
        /// number: confidence measures how decisively the signals favoured this reading over
        /// the alternative, not whether the reading is correct. An early build returned
        /// <c>Turn</c> at confidence 1.0 for a state that was unambiguously a chase - the
        /// evidence was one-sided, and one-sidedly wrong. Treat this as "how much did the
        /// classifier hedge", and treat a disagreement with what you see in game as a
        /// reason to write an annotation.
        /// </remarks>
        public ConfidenceTier Tier =>
            Confidence >= 0.75f ? ConfidenceTier.Confirmed :
            Confidence >= 0.4f ? ConfidenceTier.Probable :
            ConfidenceTier.Tentative;

        public bool HasTag(string tag) => Tags != null && Tags.Contains(tag);
    }
}
