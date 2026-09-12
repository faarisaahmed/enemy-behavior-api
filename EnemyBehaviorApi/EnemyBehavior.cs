using System;
using System.Collections.Generic;
using System.Linq;
using EnemyBehaviorApi.Annotations;
using EnemyBehaviorApi.Authority;
using EnemyBehaviorApi.Discovery;
using EnemyBehaviorApi.Runtime;
using EnemyBehaviorApi.Schema;
using UnityEngine;

namespace EnemyBehaviorApi
{
    /// <summary>
    /// The public API. Everything an external mod needs is reachable from here.
    /// </summary>
    /// <remarks>
    /// The shape of a consuming mod is the same at every tier:
    ///
    /// <code>
    /// // Observe - watch what an enemy does, change nothing.
    /// var handle = EnemyBehavior.Claim(enemyGo, "com.example.mymod", AuthorityTier.Observe);
    /// handle.StateChanged += (_, e) =>
    /// {
    ///     if (e.Behavior is ActionDescriptor attack)
    ///         Log($"{e.Enemy.Profile.EnemyId} is using {attack.DisplayName} ({attack.Shape})");
    /// };
    ///
    /// // Influence - the enemy still decides, it just decides faster.
    /// var handle = EnemyBehavior.Claim(enemyGo, "com.example.mymod", AuthorityTier.Influence);
    /// foreach (var move in handle.Profile.Movements.Where(m => m.Mode == MovementMode.Chase))
    /// foreach (var speed in move.Parameters.Where(p => p.Name.Contains("speed")))
    /// {
    ///     handle.TryGetParameter(speed.Id, out float current);
    ///     handle.TrySetParameter(speed.Id, current * 1.5f);
    /// }
    ///
    /// // Override - we decide.
    /// var handle = EnemyBehavior.Claim(enemyGo, "com.example.mymod", AuthorityTier.Override);
    /// var hardest = handle.Profile.Actions
    ///     .Where(a => a.TelegraphFor == null)
    ///     .OrderByDescending(a => a.DamageAmount)
    ///     .First();
    /// handle.Fire(hardest.Id);
    ///
    /// handle.Dispose();   // always
    /// </code>
    ///
    /// Claims are per enemy instance, so the handle you get back is about the one enemy you
    /// passed in, and holding Override on it says nothing about any other enemy in the room.
    /// </remarks>
    public static class EnemyBehavior
    {
        private static DiscoveryEngine _discovery;
        private static EnemyRegistry _registry;
        private static AuthorityRegistry _authority;
        private static AnnotationStore _annotations;

        /// <summary>True once the plugin has started. Calls before this are no-ops.</summary>
        public static bool IsReady { get; private set; }

        /// <summary>The schema version this build produces. Check it against your own before use.</summary>
        public static int Version => SchemaVersion.Current;

        /// <summary>Raised when an enemy is registered, which is the moment it becomes claimable.</summary>
        public static event Action<EnemyInstance> EnemyAppeared;

        /// <summary>Raised when an enemy is dropped. Any handle on it has already been disposed.</summary>
        public static event Action<EnemyInstance> EnemyGone;

        /// <summary>Raised when Override on an enemy is claimed (true) or released (false).</summary>
        public static event Action<EnemyInstance, string, bool> OverrideChanged;

        internal static void Initialize(DiscoveryEngine discovery, EnemyRegistry registry,
                                        AuthorityRegistry authority, AnnotationStore annotations)
        {
            _discovery = discovery;
            _registry = registry;
            _authority = authority;
            _annotations = annotations;

            registry.EnemyRegistered += e => EnemyAppeared?.Invoke(e);
            registry.EnemyUnregistered += e =>
            {
                authority.ReleaseAll(e);
                EnemyGone?.Invoke(e);
            };
            authority.OverrideChanged += (e, owner, claimed) => OverrideChanged?.Invoke(e, owner, claimed);

            IsReady = true;
        }

        // ---- Discovery ------------------------------------------------------------------

        /// <summary>
        /// What this enemy's kind can do. Discovers it on first sight, cached after.
        /// </summary>
        /// <returns>Null if the object carries no PlayMaker FSM to read.</returns>
        public static EnemyProfile GetProfile(GameObject enemy) => _discovery?.GetOrDiscover(enemy);

        /// <summary>Profiles built so far. Grows as new kinds of enemy are met.</summary>
        public static IEnumerable<EnemyProfile> KnownProfiles =>
            _discovery?.CachedProfiles ?? Enumerable.Empty<EnemyProfile>();

        /// <summary>Every enemy currently registered in the scene.</summary>
        public static IEnumerable<EnemyInstance> ActiveEnemies =>
            _registry?.All ?? Enumerable.Empty<EnemyInstance>();

        /// <summary>
        /// Registers an enemy the automatic tracker missed - something without a
        /// <c>HealthManager</c>, or spawned in a way the patch does not see.
        /// </summary>
        public static EnemyInstance Track(GameObject enemy) => _registry?.Register(enemy);

        /// <summary>The registered instance for a GameObject, or null.</summary>
        public static EnemyInstance GetInstance(GameObject enemy) => _registry?.Get(enemy);

        // ---- Authority ------------------------------------------------------------------

        /// <summary>
        /// Claims authority over one enemy, registering it if needed.
        /// </summary>
        /// <param name="enemy">The enemy GameObject. Its root, not a child.</param>
        /// <param name="ownerId">
        /// Your mod's id - the BepInEx GUID is the obvious choice. Appears in conflict logs
        /// and tells other mods who holds Override.
        /// </param>
        /// <param name="tier">
        /// What you need. May be granted at a lower tier if another mod already holds
        /// Override here, so check <see cref="IEnemyHandle.Tier"/> on the result.
        /// </param>
        /// <param name="policy">What an Override claim suppresses. Ignored at lower tiers.</param>
        /// <returns>A handle to dispose when finished, or null if the enemy cannot be read.</returns>
        public static IEnemyHandle Claim(GameObject enemy, string ownerId,
                                         AuthorityTier tier = AuthorityTier.Observe,
                                         OverridePolicy policy = OverridePolicy.SuppressDecisions)
        {
            if (!IsReady) return null;
            var instance = _registry.Register(enemy);
            return instance == null ? null : _authority.Claim(instance, ownerId, tier, policy);
        }

        /// <summary>Claims authority over an already-registered instance.</summary>
        public static IEnemyHandle Claim(EnemyInstance enemy, string ownerId,
                                         AuthorityTier tier = AuthorityTier.Observe,
                                         OverridePolicy policy = OverridePolicy.SuppressDecisions) =>
            IsReady ? _authority.Claim(enemy, ownerId, tier, policy) : null;

        /// <summary>
        /// Id of the mod holding Override on this enemy, or null. Worth checking before
        /// asking for Override yourself.
        /// </summary>
        public static string OverrideHolder(EnemyInstance enemy) =>
            enemy == null ? null : _authority?.OverrideHolder(enemy.InstanceId);

        /// <summary>Every handle currently held, across all mods. For diagnostics.</summary>
        public static IEnumerable<IEnemyHandle> ActiveHandles =>
            _authority?.ActiveHandles ?? Enumerable.Empty<IEnemyHandle>();

        // ---- Annotations ----------------------------------------------------------------

        /// <summary>
        /// Reloads annotation files and throws away cached profiles so the next sighting of
        /// each enemy rebuilds against them. Useful while writing annotations.
        /// </summary>
        public static void ReloadAnnotations(string directory)
        {
            _annotations?.LoadDirectory(directory);
            _discovery?.InvalidateCache();
        }

        /// <summary>Enemy ids with annotations loaded.</summary>
        public static IEnumerable<string> AnnotatedEnemies =>
            _annotations?.AnnotatedEnemies ?? Enumerable.Empty<string>();
    }
}
