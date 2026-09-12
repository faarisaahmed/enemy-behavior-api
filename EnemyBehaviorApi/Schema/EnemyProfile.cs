using System;
using System.Collections.Generic;
using System.Linq;

namespace EnemyBehaviorApi.Schema
{
    /// <summary>
    /// Everything discovery knows about one *kind* of enemy. Built once per enemy id and
    /// cached - profiles describe the prefab, not a live instance, so every Gruz Mother in
    /// the game shares one.
    /// </summary>
    /// <remarks>
    /// Immutable once published. <see cref="Actions"/> and <see cref="Movements"/> are
    /// separate lists deliberately, linked back here by <see cref="EnemyId"/>, so a mod
    /// that only cares about attacks never has to filter a mixed list.
    /// </remarks>
    [Serializable]
    public sealed class EnemyProfile
    {
        /// <summary>The schema version this profile was built against. See <see cref="SchemaVersion"/>.</summary>
        public int Version { get; set; } = SchemaVersion.Current;

        /// <summary>
        /// Stable identity for the enemy kind - the prefab name with Unity's clone and
        /// duplicate suffixes stripped. The key everything else links by.
        /// </summary>
        public string EnemyId { get; set; }

        /// <summary>The unmodified GameObject name discovery first saw, for diagnostics.</summary>
        public string SourceObjectName { get; set; }

        /// <summary>Scene the profile was first discovered in. Diagnostic only - profiles are not scene-scoped.</summary>
        public string DiscoveredInScene { get; set; }

        /// <summary>Starting hp from <c>HealthManager</c>, or 0 if the enemy has none.</summary>
        public int MaxHealth { get; set; }

        /// <summary>True when a <c>HealthManager</c> was found. Some hazards have FSMs but no health.</summary>
        public bool HasHealthManager { get; set; }

        /// <summary>Every FSM discovery scanned, as <c>path/FsmName</c>.</summary>
        public List<string> ScannedFsms { get; set; } = new List<string>();

        /// <summary>
        /// The FSM discovery believes drives this enemy's behaviour - the one with the most
        /// classified states. Where Override-tier transitions are sent by default.
        /// </summary>
        public string PrimaryFsm { get; set; }

        public List<ActionDescriptor> Actions { get; set; } = new List<ActionDescriptor>();

        public List<MovementDescriptor> Movements { get; set; } = new List<MovementDescriptor>();

        /// <summary>
        /// FSM variables that are not tied to a single state - aggression multipliers,
        /// shared cooldowns, phase counters. The broadest Influence-tier surface.
        /// </summary>
        public List<ParameterRef> GlobalParameters { get; set; } = new List<ParameterRef>();

        /// <summary>
        /// States that pick what to do next - the ones holding <c>SendRandomEvent</c> or a
        /// fan-out of conditional branches.
        /// </summary>
        /// <remarks>
        /// These are what Override suppresses. Identifying them is what makes "take control
        /// of this enemy" mean something narrower than "freeze this enemy": the rest of the
        /// FSM keeps running, only its choices are ours.
        /// </remarks>
        public List<StateRef> DecisionStates { get; set; } = new List<StateRef>();

        /// <summary>
        /// Every state on every scanned FSM, classified or not, with the scores behind each
        /// verdict. What you read to find out why something was missed, and what an
        /// annotation author needs in order to name a state that discovery ignored.
        /// </summary>
        public List<StateSummary> AllStates { get; set; } = new List<StateSummary>();

        /// <summary>True if an annotation file contributed to this profile.</summary>
        public bool IsAnnotated { get; set; }

        /// <summary>
        /// Problems hit while scanning - an FSM that threw, a state whose actions would not
        /// load. Non-fatal; the profile is still usable, just less complete.
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        public ActionDescriptor FindAction(string id) => Actions.FirstOrDefault(a => a.Id == id);

        public MovementDescriptor FindMovement(string id) => Movements.FirstOrDefault(m => m.Id == id);

        /// <summary>Looks in both lists. Ids are unique across them.</summary>
        public BehaviorDescriptor FindBehavior(string id) =>
            (BehaviorDescriptor)FindAction(id) ?? FindMovement(id);

        public IEnumerable<BehaviorDescriptor> AllBehaviors() =>
            Actions.Cast<BehaviorDescriptor>().Concat(Movements);

        public ParameterRef FindParameter(string id) =>
            GlobalParameters.FirstOrDefault(p => p.Id == id)
            ?? AllBehaviors().SelectMany(b => b.Parameters).FirstOrDefault(p => p.Id == id);

        public override string ToString() =>
            $"{EnemyId}: {Actions.Count} action(s), {Movements.Count} movement(s)";
    }
}
