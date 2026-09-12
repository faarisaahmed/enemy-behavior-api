using System;
using System.Collections.Generic;
using EnemyBehaviorApi.Authority;
using EnemyBehaviorApi.Schema;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

namespace EnemyBehaviorApi.Runtime
{
    /// <summary>
    /// The one place every PlayMaker state change passes through. Observation reads it;
    /// Override vetoes through it.
    /// </summary>
    /// <remarks>
    /// <c>Fsm.SwitchState</c> is the single method PlayMaker funnels all state changes
    /// through - transitions, global transitions, <c>SetState</c>, the FINISHED event, all
    /// of it. One patch here sees everything an enemy can do, which is why this API does
    /// not need to know anything about individual enemies.
    ///
    /// It observes only. Vetoing here hard-freezes the game, and it is worth recording why,
    /// because the method looks like the obvious place to block a transition:
    ///
    ///     UpdateStateChanges:
    ///       loop: SwitchState(switchToState)
    ///             if (IsSwitchingState) goto loop
    ///
    /// <c>IsSwitchingState</c> is just <c>switchToState != null</c>, and only a completed
    /// <c>SwitchState</c> clears it. A prefix returning false leaves the pending state set,
    /// so the loop spins forever on the main thread. Suppression happens in
    /// <see cref="FsmDecisionGate"/> instead, which runs before anything is pending.
    ///
    /// It is also on the hot path for the entire game: every FSM in the scene, every frame
    /// one of them moves. The ordering below is deliberate - a static bool, then a
    /// dictionary lookup, and only then anything that allocates. When no mod has claimed
    /// anything, the cost is the bool.
    /// </remarks>
    [HarmonyPatch(typeof(Fsm), nameof(Fsm.SwitchState))]
    public static class FsmTransitionGate
    {
        /// <summary>Set while a handle is firing a transition, so the gate lets its own writes through.</summary>
        [ThreadStatic] private static bool _driving;

        private static EnemyRegistry _registry;
        private static AuthorityRegistry _authority;
        private static Action<string> _log = _ => { };

        /// <summary>
        /// The fast path. False when nothing is registered, which is the common case for
        /// most of a play session.
        /// </summary>
        private static bool _armed;

        internal static void Install(EnemyRegistry registry, AuthorityRegistry authority, Action<string> log)
        {
            _registry = registry;
            _authority = authority;
            _log = log ?? (_ => { });

            registry.EnemyRegistered += _ => _armed = true;
            registry.EnemyUnregistered += _ => _armed = !registry.IsEmpty;
        }

        /// <summary>True while a handle is firing a transition of its own.</summary>
        internal static bool IsDriving => _driving;

        /// <summary>Runs <paramref name="action"/> with the gate open for transitions we cause.</summary>
        internal static void Drive(Action action)
        {
            bool previous = _driving;
            _driving = true;
            try { action(); }
            finally { _driving = previous; }
        }

        /// <summary>Carried from prefix to postfix so a vetoed transition raises no event.</summary>
        public struct GateState
        {
            public EnemyInstance Enemy;
            public string PreviousState;
            public bool Allowed;
            public bool WasDriven;
        }

        /// <summary>Records what is about to happen. Always lets the switch proceed.</summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(Fsm __instance, FsmState toState, out GateState __state)
        {
            __state = default;
            if (!_armed || toState == null) return;

            GameObject owner = __instance?.GameObject;
            if (owner == null) return;

            EnemyInstance enemy = _registry.ByFsmOwner(owner.GetInstanceID());
            if (enemy == null) return;

            __state.Enemy = enemy;
            __state.PreviousState = __instance.ActiveStateName;
            __state.WasDriven = _driving;
            __state.Allowed = true;
        }

        [HarmonyPostfix]
        public static void Postfix(Fsm __instance, FsmState toState, GateState __state)
        {
            if (__state.Enemy == null || !__state.Allowed) return;

            var stateRef = new StateRef(
                __state.Enemy.Profile == null ? string.Empty : PathOf(__state.Enemy, __instance),
                __instance.Name,
                toState.Name);

            try
            {
                _authority.NotifyStateChanged(__state.Enemy, stateRef, __state.PreviousState, __state.WasDriven);
            }
            catch (Exception e)
            {
                _log($"a state-change subscriber threw on {__state.Enemy}: {e}");
            }
        }

        /// <summary>
        /// Transform path of an FSM relative to its enemy root. Recomputed rather than
        /// cached because a state change is rare enough per enemy that walking a few
        /// parents is cheaper than a dictionary keyed on Fsm identity.
        /// </summary>
        private static string PathOf(EnemyInstance enemy, Fsm fsm)
        {
            GameObject owner = fsm?.GameObject;
            if (owner == null || enemy.GameObject == null) return string.Empty;
            return Discovery.EnemyIdentity.RelativePath(enemy.GameObject.transform, owner.transform);
        }
    }
}
