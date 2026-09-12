namespace EnemyBehaviorApi.Schema
{
    /// <summary>
    /// Versioning for the schema types, so a consumer compiled against an older build can
    /// tell whether what it is holding is shaped the way it expects.
    /// </summary>
    /// <remarks>
    /// The contract: <see cref="Current"/> goes up on any change to the schema types.
    /// <see cref="MinimumCompatible"/> goes up only on a change that could break a consumer
    /// - a removed or renamed member, a re-meaning of an existing one. Adding a new
    /// optional member raises <see cref="Current"/> alone. So a consumer is safe when
    /// <c>its compiled version &gt;= MinimumCompatible</c>, which is what
    /// <see cref="IsCompatible"/> checks.
    /// </remarks>
    public static class SchemaVersion
    {
        public const int Current = 1;

        public const int MinimumCompatible = 1;

        /// <summary>
        /// Call this at plugin start with the <see cref="Current"/> value you compiled
        /// against, and refuse to run if it returns false.
        /// </summary>
        public static bool IsCompatible(int consumerVersion) =>
            consumerVersion >= MinimumCompatible && consumerVersion <= Current;
    }
}
