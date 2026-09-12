namespace EnemyBehaviorApi.Authority
{
    /// <summary>
    /// How much a consumer is asking to do to an enemy. Each tier includes everything the
    /// ones below it can do.
    /// </summary>
    public enum AuthorityTier
    {
        /// <summary>
        /// Read the profile, subscribe to state changes. Never writes. Always granted, any
        /// number of mods at once.
        /// </summary>
        Observe = 0,

        /// <summary>
        /// Tune existing numbers - speeds, wait times, damage, branch weights - while the
        /// enemy keeps making its own decisions. Granted to any number of mods at once,
        /// with conflicts on the same parameter reported rather than prevented.
        /// </summary>
        Influence = 1,

        /// <summary>
        /// Decide what the enemy does. Suppresses its own decision-making and lets the
        /// holder fire transitions directly. Exclusive: one mod at a time, per enemy
        /// instance.
        /// </summary>
        Override = 2,
    }

    /// <summary>What an Override claim suppresses.</summary>
    public enum OverridePolicy
    {
        /// <summary>
        /// Veto only transitions leaving a decision state. The enemy still executes
        /// attacks, recoveries, hit reactions and death normally - it just stops choosing,
        /// and waits in its decision state until the holder fires something.
        /// </summary>
        /// <remarks>
        /// The default, and what "take control" should usually mean. It leaves the enemy
        /// looking and behaving like itself: animations, timings and interruptibility all
        /// still work, because all of that lives in states we are not touching.
        /// </remarks>
        SuppressDecisions = 0,

        /// <summary>
        /// Veto every transition the holder did not fire. Total puppetry - nothing moves
        /// unless driven. Anything the enemy would normally do for itself, including
        /// reacting to being hit, stops happening, so the holder has to drive all of it.
        /// </summary>
        SuppressAll = 1,
    }
}
