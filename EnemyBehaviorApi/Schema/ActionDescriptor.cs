using System;

namespace EnemyBehaviorApi.Schema
{
    /// <summary>
    /// Roughly how an attack reaches the player. Deliberately coarse - it is inferred from
    /// which PlayMaker actions run, and finer distinctions are not reliably inferrable.
    /// </summary>
    public enum AttackShape
    {
        /// <summary>Classified as an attack, but the delivery is unclear.</summary>
        Unknown,

        /// <summary>A damaging collider on the enemy itself: a swipe, a bite, a body slam.</summary>
        Melee,

        /// <summary>Spawns something that travels: a shot, a lobbed object, a spike volley.</summary>
        Projectile,

        /// <summary>A damaging region that persists: a puddle, a gas cloud, a lingering wave.</summary>
        Area,

        /// <summary>Spawns another enemy or a helper object rather than damage directly.</summary>
        Summon,

        /// <summary>Damages the player directly, without a collider - a scripted hit or a grab.</summary>
        Direct,
    }

    /// <summary>
    /// An attack the enemy can perform. One per PlayMaker state that discovery classified
    /// as offensive.
    /// </summary>
    /// <remarks>
    /// Attacks are usually more than one state - a wind-up, the strike, a recovery. Each
    /// becomes its own descriptor, because each is separately triggerable; the link between
    /// them is <see cref="TelegraphFor"/> / <see cref="RecoveryState"/>. An Override-tier
    /// mod that fires only the strike gets an attack with no wind-up, which is a legitimate
    /// thing to want and a common way to make an enemy feel unfair. Prefer firing the
    /// descriptor whose <see cref="TelegraphFor"/> is null.
    /// </remarks>
    [Serializable]
    public sealed class ActionDescriptor : BehaviorDescriptor
    {
        public AttackShape Shape { get; set; } = AttackShape.Unknown;

        /// <summary>
        /// Set when this state looks like a wind-up rather than the strike: it holds the
        /// <see cref="BehaviorDescriptor.Id"/> of the attack it precedes. Null on the strike itself.
        /// </summary>
        public string TelegraphFor { get; set; }

        /// <summary><see cref="BehaviorDescriptor.Id"/> of the state this attack recovers into, if there is a single obvious one.</summary>
        public string RecoveryState { get; set; }

        /// <summary>
        /// Damage the state sets up, if a <c>SetDamageHero*</c> action made it readable.
        /// Zero means "not found", not "harmless".
        /// </summary>
        public int DamageAmount { get; set; }

        /// <summary>
        /// Whether the state loops until interrupted rather than running once - a sustained
        /// beam or a spin. Inferred from the state having no unconditional exit.
        /// </summary>
        public bool IsSustained { get; set; }

        public override string ToString() => $"Attack:{Id} ({Shape}, conf {Confidence:0.00})";
    }
}
