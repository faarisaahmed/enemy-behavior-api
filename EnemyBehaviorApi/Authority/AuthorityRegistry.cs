using System;
using System.Collections.Generic;
using System.Linq;
using EnemyBehaviorApi.Runtime;
using EnemyBehaviorApi.Schema;

namespace EnemyBehaviorApi.Authority
{
    /// <summary>
    /// Who is allowed to do what to which enemy. The arbiter that makes "several mods, one
    /// scene" work.
    /// </summary>
    /// <remarks>
    /// Authority is scoped per enemy instance, never per enemy kind and never globally.
    /// That is the design's central bet: a combat-overhaul mod driving the boss and a pet
    /// mod charming one husk in the same room are not in conflict, and nothing here should
    /// make them think they are. The only exclusive resource is Override on a single
    /// instance, and that is exclusive because two mods firing transitions into one FSM
    /// produces behaviour neither asked for.
    /// </remarks>
    public sealed class AuthorityRegistry
    {
        private readonly Dictionary<int, List<EnemyHandle>> _handles = new Dictionary<int, List<EnemyHandle>>();
        private readonly Dictionary<int, EnemyHandle> _overrides = new Dictionary<int, EnemyHandle>();
        private readonly Action<string> _log;

        public AuthorityRegistry(Action<string> log = null) => _log = log ?? (_ => { });

        /// <summary>Raised when an Override claim is granted or released. For diagnostics and mod-vs-mod UIs.</summary>
        public event Action<EnemyInstance, string, bool> OverrideChanged;

        /// <summary>Handles currently held against any enemy.</summary>
        public IEnumerable<IEnemyHandle> ActiveHandles => _handles.Values.SelectMany(v => v);

        /// <summary>The Override claim on an enemy, or null if nobody holds one.</summary>
        public OverrideClaim ActiveOverride(int enemyInstanceId) =>
            _overrides.TryGetValue(enemyInstanceId, out var handle) && handle.IsValid ? handle.Claim : null;

        /// <summary>Id of the mod holding Override on an enemy, or null.</summary>
        public string OverrideHolder(int enemyInstanceId) => ActiveOverride(enemyInstanceId)?.OwnerId;

        /// <summary>
        /// Claims authority over one enemy.
        /// </summary>
        /// <remarks>
        /// Returns a handle at the highest tier available, which may be lower than asked
        /// for: if another mod holds Override, an Override request is granted as Influence
        /// rather than refused outright. A mod that cannot work at a lower tier should
        /// check <see cref="IEnemyHandle.Tier"/> and dispose. Silently degrading beats
        /// returning null, because the common case - two mods interested in the same boss -
        /// should leave the second one still able to observe and tune.
        /// </remarks>
        public IEnemyHandle Claim(EnemyInstance enemy, string ownerId, AuthorityTier requested,
                                  OverridePolicy policy = OverridePolicy.SuppressDecisions)
        {
            if (enemy == null || !enemy.IsAlive) return null;
            if (string.IsNullOrEmpty(ownerId)) throw new ArgumentException("every handle needs an owner id", nameof(ownerId));

            AuthorityTier granted = requested;

            if (requested == AuthorityTier.Override && _overrides.TryGetValue(enemy.InstanceId, out var incumbent) && incumbent.IsValid)
            {
                granted = AuthorityTier.Influence;
                _log($"'{ownerId}' asked for Override on {enemy} but '{incumbent.OwnerId}' holds it - granted Influence");
            }

            var handle = new EnemyHandle(this, enemy, ownerId, granted, policy);

            if (!_handles.TryGetValue(enemy.InstanceId, out var list))
                _handles[enemy.InstanceId] = list = new List<EnemyHandle>();
            list.Add(handle);

            if (granted == AuthorityTier.Override)
            {
                _overrides[enemy.InstanceId] = handle;
                Raise(enemy, ownerId, true);
            }

            return handle;
        }

        internal void Release(EnemyHandle handle)
        {
            if (handle?.Enemy == null) return;
            int id = handle.Enemy.InstanceId;

            if (_handles.TryGetValue(id, out var list))
            {
                list.Remove(handle);
                if (list.Count == 0) _handles.Remove(id);
            }

            if (_overrides.TryGetValue(id, out var current) && ReferenceEquals(current, handle))
            {
                _overrides.Remove(id);
                Raise(handle.Enemy, handle.OwnerId, false);

                // Hand Override to whoever was waiting - a mod that asked for it and got
                // Influence instead. Without this, releasing a claim leaves an enemy under
                // nobody's control even though someone wanted it.
                var waiting = list?.FirstOrDefault(h => h.IsValid && h.RequestedTier == AuthorityTier.Override);
                if (waiting != null)
                {
                    waiting.PromoteToOverride();
                    _overrides[id] = waiting;
                    _log($"Override on {handle.Enemy} passed to '{waiting.OwnerId}'");
                    Raise(handle.Enemy, waiting.OwnerId, true);
                }
            }
        }

        /// <summary>Drops every handle on an enemy. Called when the instance goes away.</summary>
        internal void ReleaseAll(EnemyInstance enemy)
        {
            if (enemy == null || !_handles.TryGetValue(enemy.InstanceId, out var list)) return;
            foreach (var handle in list.ToList()) handle.Dispose();
            _handles.Remove(enemy.InstanceId);
            _overrides.Remove(enemy.InstanceId);
        }

        /// <summary>
        /// Reports that two Influence-tier handles are writing the same parameter.
        /// </summary>
        /// <remarks>
        /// Not prevented, only reported. Influence is explicitly the non-exclusive tier, and
        /// two mods both wanting an enemy faster is a reasonable thing to happen; last
        /// write wins. It is worth a log line because the result is otherwise a mystery to
        /// whichever author looks first.
        /// </remarks>
        internal void ReportParameterConflict(EnemyInstance enemy, string parameterId, string writer, string previousWriter)
        {
            if (writer == previousWriter) return;
            _log($"'{writer}' and '{previousWriter}' both write '{parameterId}' on {enemy} - last write wins");
        }

        /// <summary>Fans a state change out to every handle watching that enemy.</summary>
        internal void NotifyStateChanged(EnemyInstance enemy, StateRef state, string previousState, bool wasDriven)
        {
            if (enemy == null || !_handles.TryGetValue(enemy.InstanceId, out var list) || list.Count == 0) return;

            // A state can be both an attack and a movement - a charge that damages on
            // contact. Taking only the first descriptor meant consumers never saw the other
            // one, and a survey counted the pair as entered 12x and 0x for the same state.
            var behaviors = enemy.Profile?.AllBehaviors().Where(b => b.State.Equals(state)).ToList()
                            ?? new List<BehaviorDescriptor>();

            // Always raise at least once, so unclassified transitions still reach observers.
            if (behaviors.Count == 0) behaviors.Add(null);

            foreach (var behavior in behaviors)
            {
                var args = new StateChangedEventArgs(enemy, state, previousState, behavior, wasDriven);

                // Snapshot: a subscriber is allowed to dispose its handle in the callback.
                foreach (var handle in list.ToArray())
                {
                    if (handle.IsValid) handle.RaiseStateChanged(args);
                }
            }
        }

        private void Raise(EnemyInstance enemy, string ownerId, bool claimed)
        {
            try { OverrideChanged?.Invoke(enemy, ownerId, claimed); }
            catch (Exception e) { _log($"an OverrideChanged subscriber threw: {e}"); }
        }
    }
}
