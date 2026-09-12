using System;
using System.Collections.Generic;
using System.Linq;
using EnemyBehaviorApi.Discovery;
using UnityEngine;

namespace EnemyBehaviorApi.Runtime
{
    /// <summary>
    /// Every enemy the API currently knows about, and the mapping from an FSM-owning
    /// GameObject back to the enemy it belongs to.
    /// </summary>
    /// <remarks>
    /// The reverse map is what makes the Harmony gate affordable: a PlayMaker transition
    /// hands us an <c>Fsm</c>, and we need to answer "is this an enemy anyone is watching?"
    /// in the time a dictionary lookup takes, on every state change in the game.
    /// </remarks>
    public sealed class EnemyRegistry
    {
        private readonly Dictionary<int, EnemyInstance> _byInstanceId = new Dictionary<int, EnemyInstance>();

        /// <summary>FSM-owning GameObject instance id -> the enemy that object belongs to.</summary>
        private readonly Dictionary<int, EnemyInstance> _byFsmOwnerId = new Dictionary<int, EnemyInstance>();

        private readonly DiscoveryEngine _discovery;
        private readonly Action<string> _log;

        public EnemyRegistry(DiscoveryEngine discovery, Action<string> log = null)
        {
            _discovery = discovery;
            _log = log ?? (_ => { });
        }

        /// <summary>Raised when an enemy is registered. Lets a mod claim an enemy the moment it spawns.</summary>
        public event Action<EnemyInstance> EnemyRegistered;

        /// <summary>Raised when an enemy is dropped, by death or scene change.</summary>
        public event Action<EnemyInstance> EnemyUnregistered;

        public IEnumerable<EnemyInstance> All => _byInstanceId.Values;

        public int Count => _byInstanceId.Count;

        /// <summary>True when there is nothing to gate. Read on every PlayMaker transition.</summary>
        public bool IsEmpty => _byFsmOwnerId.Count == 0;

        /// <summary>
        /// Registers an enemy, discovering its profile if this is the first of its kind.
        /// Idempotent.
        /// </summary>
        /// <returns>The instance, or null if the object has no FSMs to describe.</returns>
        public EnemyInstance Register(GameObject go)
        {
            if (go == null) return null;

            int id = go.GetInstanceID();
            if (_byInstanceId.TryGetValue(id, out var existing)) return existing;

            var profile = _discovery.GetOrDiscover(go);
            if (profile == null) return null;

            var instance = new EnemyInstance(go, profile);
            _byInstanceId[id] = instance;
            foreach (int ownerId in instance.FsmOwnerIds) _byFsmOwnerId[ownerId] = instance;

            try { EnemyRegistered?.Invoke(instance); }
            catch (Exception e) { _log($"an EnemyRegistered subscriber threw: {e}"); }

            return instance;
        }

        public void Unregister(int instanceId)
        {
            if (!_byInstanceId.TryGetValue(instanceId, out var instance)) return;

            _byInstanceId.Remove(instanceId);
            foreach (int ownerId in instance.FsmOwnerIds) _byFsmOwnerId.Remove(ownerId);

            try { EnemyUnregistered?.Invoke(instance); }
            catch (Exception e) { _log($"an EnemyUnregistered subscriber threw: {e}"); }
        }

        public EnemyInstance Get(GameObject go) =>
            go != null && _byInstanceId.TryGetValue(go.GetInstanceID(), out var instance) ? instance : null;

        public EnemyInstance Get(int instanceId) =>
            _byInstanceId.TryGetValue(instanceId, out var instance) ? instance : null;

        /// <summary>The enemy owning the GameObject an FSM lives on. The gate's hot lookup.</summary>
        public EnemyInstance ByFsmOwner(int gameObjectInstanceId) =>
            _byFsmOwnerId.TryGetValue(gameObjectInstanceId, out var instance) ? instance : null;

        /// <summary>
        /// Drops enemies Unity has destroyed.
        /// </summary>
        /// <remarks>
        /// Unity does not tell us when an object is destroyed, and a destroyed enemy leaves
        /// a stale entry that would keep an Override claim alive against a corpse. Cheap
        /// enough to run once a second.
        /// </remarks>
        public int PruneDestroyed()
        {
            var dead = _byInstanceId.Where(kvp => !kvp.Value.IsAlive).Select(kvp => kvp.Key).ToList();
            foreach (int id in dead) Unregister(id);
            return dead.Count;
        }

        /// <summary>Drops everything. Called on scene change.</summary>
        public void Clear()
        {
            foreach (int id in _byInstanceId.Keys.ToList()) Unregister(id);
            _byInstanceId.Clear();
            _byFsmOwnerId.Clear();
        }
    }
}
