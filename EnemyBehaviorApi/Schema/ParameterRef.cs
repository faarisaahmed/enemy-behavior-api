using System;

namespace EnemyBehaviorApi.Schema
{
    /// <summary>What kind of number a <see cref="ParameterRef"/> points at.</summary>
    public enum ParameterKind
    {
        Float,
        Int,
        Bool,
        Vector2,
    }

    /// <summary>
    /// Where a tunable number lives. Two places hold them: an FSM variable, or a field on
    /// one action inside one state.
    /// </summary>
    public enum ParameterSource
    {
        /// <summary>An entry in <c>Fsm.Variables</c>. Shared by every state in that FSM.</summary>
        FsmVariable,

        /// <summary>A field on a single <c>FsmStateAction</c>. Scoped to that one state.</summary>
        ActionField,
    }

    /// <summary>
    /// A single number an Influence-tier consumer may read and write - a wait duration, a
    /// dash speed, a spawn count, the weight on a random branch.
    /// </summary>
    /// <remarks>
    /// Discovery records where the number lives, never a cached value: FSM variables are
    /// shared mutable state and an enemy rewrites them constantly. Read through
    /// <c>IEnemyHandle.TryGetParameter</c> at the moment you need the value.
    /// </remarks>
    [Serializable]
    public sealed class ParameterRef
    {
        /// <summary>Stable id, unique within an <see cref="EnemyProfile"/>. Used as the annotation key.</summary>
        public string Id { get; set; }

        /// <summary>Human-readable name, from the FSM variable or the action field.</summary>
        public string Name { get; set; }

        public ParameterKind Kind { get; set; }

        public ParameterSource Source { get; set; }

        /// <summary>The state this parameter belongs to. Null for FSM-wide variables.</summary>
        public StateRef State { get; set; }

        /// <summary>FSM this parameter belongs to, always set.</summary>
        public string FsmPath { get; set; }

        public string FsmName { get; set; }

        /// <summary>For <see cref="ParameterSource.FsmVariable"/>: the variable name.</summary>
        public string VariableName { get; set; }

        /// <summary>For <see cref="ParameterSource.ActionField"/>: index into <c>FsmState.Actions</c>.</summary>
        public int ActionIndex { get; set; } = -1;

        /// <summary>For <see cref="ParameterSource.ActionField"/>: the action's type name, for sanity-checking the index still points where it did.</summary>
        public string ActionTypeName { get; set; }

        /// <summary>For <see cref="ParameterSource.ActionField"/>: the field name on the action.</summary>
        public string FieldName { get; set; }

        /// <summary>The value discovery saw, for reference only. Do not treat as current.</summary>
        public string DiscoveredValue { get; set; }

        /// <summary>
        /// Advisory bounds. Discovery never infers these - they come from the annotation
        /// file. Null means unbounded, and the API will not clamp.
        /// </summary>
        public float? Min { get; set; }

        public float? Max { get; set; }

        public override string ToString() => Id + " (" + Kind + ")";
    }
}
