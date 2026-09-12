using System;

namespace EnemyBehaviorApi.Schema
{
    /// <summary>How the enemy moves in a given state.</summary>
    public enum MovementMode
    {
        /// <summary>Classified as movement, but the mechanism is unclear.</summary>
        Unknown,

        /// <summary>Stationary on purpose - an idle, a wait, a guard pose.</summary>
        Idle,

        /// <summary>Ground movement under its own speed, no target.</summary>
        Walk,

        /// <summary>Airborne movement, no target.</summary>
        Fly,

        /// <summary>Actively closing on the player.</summary>
        Chase,

        /// <summary>A committed burst - dash, lunge, charge. Usually not steerable mid-move.</summary>
        Dash,

        /// <summary>A ballistic hop or leap.</summary>
        Jump,

        /// <summary>Position set discontinuously.</summary>
        Teleport,

        /// <summary>Pushed by something else - knockback, recoil, a conveyor.</summary>
        Displaced,

        /// <summary>Turning to face without translating.</summary>
        Turn,
    }

    /// <summary>
    /// A way the enemy moves. One per PlayMaker state that discovery classified as
    /// locomotion.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="ActionDescriptor"/> because the two answer different
    /// questions and a consumer nearly always wants one or the other. A state can be both
    /// - a charge that damages on contact - in which case it appears in both lists on the
    /// same <see cref="EnemyProfile"/>, sharing a <see cref="BehaviorDescriptor.State"/>.
    /// </remarks>
    [Serializable]
    public sealed class MovementDescriptor : BehaviorDescriptor
    {
        public MovementMode Mode { get; set; } = MovementMode.Unknown;

        /// <summary>True when the state tracks the player - as opposed to moving along a fixed path.</summary>
        public bool IsTargeted { get; set; }

        /// <summary>True when the enemy leaves the ground here.</summary>
        public bool IsAirborne { get; set; }

        /// <summary>
        /// Speed the state sets up, if an action made it readable. Zero means "not found".
        /// Units are Unity units per second and only comparable within one enemy.
        /// </summary>
        public float Speed { get; set; }

        /// <summary>
        /// True when this state also damages on contact - a charge, a body slam. The same
        /// state will have an <see cref="ActionDescriptor"/> on the profile too.
        /// </summary>
        public bool DamagesOnContact { get; set; }

        public override string ToString() => $"Movement:{Id} ({Mode}, conf {Confidence:0.00})";
    }
}
