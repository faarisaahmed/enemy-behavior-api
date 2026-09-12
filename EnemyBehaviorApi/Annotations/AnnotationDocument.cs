using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace EnemyBehaviorApi.Annotations
{
    /// <summary>
    /// One annotation file. Corrections for one or more enemies, keyed by enemy id.
    /// </summary>
    /// <remarks>
    /// Files live in <c>BepInEx/plugins/EnemyBehaviorApi/annotations/</c>; every
    /// <c>*.json</c> there is loaded and merged, so a mod can ship its own corrections
    /// without editing anyone else's. Merge order is filename order, last writer wins on a
    /// conflict - which is worth knowing if two files annotate the same boss.
    /// </remarks>
    public sealed class AnnotationDocument
    {
        /// <summary>Schema version this file was written against. Mismatches are reported, not fatal.</summary>
        [JsonProperty("version")]
        public int Version { get; set; } = Schema.SchemaVersion.Current;

        /// <summary>Free text for whoever reads the file next. Ignored by the loader.</summary>
        [JsonProperty("description")]
        public string Description { get; set; }

        /// <summary>Keyed by <see cref="Schema.EnemyProfile.EnemyId"/>.</summary>
        [JsonProperty("enemies")]
        public Dictionary<string, EnemyAnnotation> Enemies { get; set; } = new Dictionary<string, EnemyAnnotation>(StringComparer.Ordinal);
    }

    /// <summary>Corrections for one enemy.</summary>
    public sealed class EnemyAnnotation
    {
        /// <summary>Overrides the auto-picked primary FSM. Format: <c>path/FsmName</c>, or just <c>FsmName</c> on the root.</summary>
        [JsonProperty("primaryFsm")]
        public string PrimaryFsm { get; set; }

        /// <summary>
        /// FSMs to skip entirely. The usual reason is a hit-response or audio FSM whose
        /// states keep classifying as behaviour and clutter the profile.
        /// </summary>
        [JsonProperty("ignoreFsms")]
        public List<string> IgnoreFsms { get; set; } = new List<string>();

        /// <summary>
        /// Per-state corrections, keyed by <see cref="Schema.StateRef.Key"/>
        /// (<c>path/FsmName/StateName</c>, or <c>FsmName/StateName</c> on the root).
        /// </summary>
        [JsonProperty("states")]
        public Dictionary<string, StateAnnotation> States { get; set; } = new Dictionary<string, StateAnnotation>(StringComparer.Ordinal);

        /// <summary>
        /// Extra decision states to suppress under Override, for choosers the engine did
        /// not recognise. Same key format as <see cref="States"/>.
        /// </summary>
        [JsonProperty("decisionStates")]
        public List<string> DecisionStates { get; set; } = new List<string>();

        /// <summary>Bounds and renames for parameters, keyed by <see cref="Schema.ParameterRef.Id"/>.</summary>
        [JsonProperty("parameters")]
        public Dictionary<string, ParameterAnnotation> Parameters { get; set; } = new Dictionary<string, ParameterAnnotation>(StringComparer.Ordinal);
    }

    /// <summary>
    /// What to do with one state. Every field is optional; a null leaves discovery's answer
    /// alone, which keeps files small and means they only record the disagreement.
    /// </summary>
    public sealed class StateAnnotation
    {
        /// <summary>
        /// <c>attack</c>, <c>movement</c>, <c>both</c>, or <c>none</c>. <c>none</c> removes
        /// the state from the profile - the fix for a false positive.
        /// </summary>
        [JsonProperty("classify")]
        public string Classify { get; set; }

        /// <summary>Human label for the descriptor.</summary>
        [JsonProperty("displayName")]
        public string DisplayName { get; set; }

        /// <summary>One of <see cref="Schema.AttackShape"/>, case-insensitive.</summary>
        [JsonProperty("shape")]
        public string Shape { get; set; }

        /// <summary>One of <see cref="Schema.MovementMode"/>, case-insensitive.</summary>
        [JsonProperty("mode")]
        public string Mode { get; set; }

        /// <summary>Tags to add. Existing tags are kept.</summary>
        [JsonProperty("tags")]
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// Events that reach this state, for entry points the transition graph does not
        /// show - a global event another FSM broadcasts, say. Added to the discovered ones.
        /// </summary>
        [JsonProperty("triggerEvents")]
        public List<string> TriggerEvents { get; set; } = new List<string>();

        /// <summary>Descriptor id of the attack this state is a wind-up for.</summary>
        [JsonProperty("telegraphFor")]
        public string TelegraphFor { get; set; }

        [JsonProperty("damageAmount")]
        public int? DamageAmount { get; set; }

        [JsonProperty("speed")]
        public float? Speed { get; set; }

        /// <summary>Why this correction exists. Ignored by the loader; read by the next person.</summary>
        [JsonProperty("note")]
        public string Note { get; set; }
    }

    /// <summary>Advisory bounds on a parameter. Discovery never infers these.</summary>
    public sealed class ParameterAnnotation
    {
        [JsonProperty("displayName")]
        public string DisplayName { get; set; }

        [JsonProperty("min")]
        public float? Min { get; set; }

        [JsonProperty("max")]
        public float? Max { get; set; }

        [JsonProperty("note")]
        public string Note { get; set; }
    }
}
