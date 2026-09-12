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
    /// of it. One patch here covers everything an enemy can do, which is why this API does
    /// not need to know anything about individual enemies.
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

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Fsm __instance, FsmState toState, out GateState __state)
        {
            __state = default;
            if (!_armed || toState == null) return true;

            GameObject owner = __instance?.GameObject;
            if (owner == null) return true;

            EnemyInstance enemy = _registry.ByFsmOwner(owner.GetInstanceID());
            if (enemy == null) return true;

            __state.Enemy = enemy;
            __state.PreviousState = __instance.ActiveStateName;
            __state.WasDriven = _driving;
            __state.Allowed = true;

            var claim = _authority.ActiveOverride(enemy.InstanceId);
            if (claim == null || !claim.SuppressionActive || _driving) return true;

            // At this point the enemy is under someone else's control and is trying to move
            // on its own. Whether that is allowed is the whole question of the Override
            // tier.
            if (claim.IsAlwaysAllowed(toState.Name)) return true;

            bool veto = claim.Policy == OverridePolicy.SuppressAll ||
                        claim.IsDecisionState(__instance.Name, __instance.ActiveStateName);

            if (!veto) return true;

            __state.Allowed = false;
            return false;
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
