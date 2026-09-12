using System;
using System.Collections.Generic;
using EnemyBehaviorApi.Discovery;
using EnemyBehaviorApi.Schema;
using HutongGames.PlayMaker;
using UnityEngine;

namespace EnemyBehaviorApi.Runtime
{
    /// <summary>
    /// One live enemy in the scene: the GameObject, the profile describing its kind, and
    /// the resolved FSM components authority acts through.
    /// </summary>
    /// <remarks>
    /// The distinction between this and <see cref="EnemyProfile"/> is the one that makes
    /// per-instance authority work. A profile describes every Roachfeeder in the game; an
    /// instance is the one in front of you. Claims are held against instances, so two mods
    /// can own two different Roachfeeders without either noticing the other.
    /// </remarks>
    public sealed class EnemyInstance
    {
        /// <summary>Unity's instance id for the root GameObject. Unique for the object's lifetime.</summary>
        public int InstanceId { get; }

        public GameObject GameObject { get; }

        public EnemyProfile Profile { get; }

        public HealthManager Health { get; }

        /// <summary>FSM components on this instance, keyed by <c>path/FsmName</c>.</summary>
        public IReadOnlyDictionary<string, PlayMakerFSM> Fsms { get; }

        /// <summary>Instance ids of every FSM-owning GameObject here. The fast path for the Harmony gate.</summary>
        public IReadOnlyCollection<int> FsmOwnerIds { get; }

        /// <summary>False once Unity has destroyed the object.</summary>
        public bool IsAlive => GameObject != null;

        internal EnemyInstance(GameObject go, EnemyProfile profile)
        {
            GameObject = go;
            InstanceId = go.GetInstanceID();
            Profile = profile;
            Health = go.GetComponent<HealthManager>();

            var fsms = new Dictionary<string, PlayMakerFSM>(StringComparer.Ordinal);
            var ownerIds = new HashSet<int>();

            foreach (var component in go.GetComponentsInChildren<PlayMakerFSM>(includeInactive: true))
            {
                if (component == null) continue;
                string path = EnemyIdentity.RelativePath(go.transform, component.transform);
                string key = (path.Length == 0 ? "" : path + "/") + component.FsmName;

                // An object can carry two FSMs with the same name. First wins; the loser is
                // still reachable through the component list, just not by key.
                if (!fsms.ContainsKey(key)) fsms[key] = component;
                ownerIds.Add(component.gameObject.GetInstanceID());
            }

            Fsms = fsms;
            FsmOwnerIds = ownerIds;
        }

        /// <summary>The FSM a <see cref="StateRef"/> points at, or null if it is not on this instance.</summary>
        public PlayMakerFSM ResolveFsm(StateRef stateRef)
        {
            if (stateRef == null) return null;
            string key = (stateRef.FsmPath.Length == 0 ? "" : stateRef.FsmPath + "/") + stateRef.FsmName;
            return Fsms.TryGetValue(key, out var fsm) ? fsm : null;
        }

        /// <summary>The FSM named <c>path/FsmName</c>, or null.</summary>
        public PlayMakerFSM ResolveFsm(string fsmKey) =>
            fsmKey != null && Fsms.TryGetValue(fsmKey, out var fsm) ? fsm : null;

        /// <summary>The FSM discovery picked as this enemy's driver, or null if it has none.</summary>
        public PlayMakerFSM PrimaryFsm => ResolveFsm(Profile?.PrimaryFsm);

        /// <summary>Name of the state the given FSM is currently in, or null.</summary>
        public string ActiveStateOf(StateRef stateRef) => ResolveFsm(stateRef)?.ActiveStateName;

        public override string ToString() =>
            $"{Profile?.EnemyId ?? "?"}#{InstanceId}" + (IsAlive ? "" : " (destroyed)");
    }
}
