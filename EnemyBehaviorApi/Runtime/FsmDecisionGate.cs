using System;
using EnemyBehaviorApi.Authority;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

namespace EnemyBehaviorApi.Runtime
{
    /// <summary>
    /// Where Override-tier suppression happens: the point at which PlayMaker decides to
    /// take a transition, before anything is queued.
    /// </summary>
    /// <remarks>
    /// <c>Fsm.DoTransition</c> returns a bool and runs before <c>switchToState</c> is set,
    /// so refusing here simply means the transition never happens - no pending state, no
    /// retry, nothing left inconsistent.
    ///
    /// The obvious-looking alternative, vetoing <c>Fsm.SwitchState</c>, hard-freezes the
    /// game: <c>UpdateStateChanges</c> loops `while (IsSwitchingState) SwitchState(...)`,
    /// and <c>IsSwitchingState</c> stays true until a completed switch clears the pending
    /// state. Blocking it spins the main thread forever. See <see cref="FsmTransitionGate"/>,
    /// which now only observes.
    ///
    /// One consequence worth knowing: <c>Fsm.SetState</c> does not route through here, so an
    /// enemy that drives itself with SetState is not suppressed even under SuppressAll.
    /// That is the safe direction to be wrong in - a little less control, rather than a
    /// frozen game.
    /// </remarks>
    [HarmonyPatch(typeof(Fsm), "DoTransition")]
    public static class FsmDecisionGate
    {
        private static EnemyRegistry _registry;
        private static AuthorityRegistry _authority;
        private static bool _armed;

        internal static void Install(EnemyRegistry registry, AuthorityRegistry authority)
        {
            _registry = registry;
            _authority = authority;
            registry.EnemyRegistered += _ => _armed = true;
            registry.EnemyUnregistered += _ => _armed = !registry.IsEmpty;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Fsm __instance, FsmTransition transition, ref bool __result)
        {
            if (!_armed || transition == null || FsmTransitionGate.IsDriving) return true;

            GameObject owner = __instance?.GameObject;
            if (owner == null) return true;

            EnemyInstance enemy = _registry.ByFsmOwner(owner.GetInstanceID());
            if (enemy == null) return true;

            var claim = _authority.ActiveOverride(enemy.InstanceId);
            if (claim == null || !claim.SuppressionActive) return true;

            // Destination matters more than origin for the passlist: death, stun and recoil
            // have to get through or an overridden enemy becomes unkillable.
            string destination = transition.ToFsmState?.Name ?? transition.ToState;
            if (claim.IsAlwaysAllowed(destination)) return true;

            bool veto = claim.Policy == OverridePolicy.SuppressAll ||
                        claim.IsDecisionState(__instance.Name, __instance.ActiveStateName);

            if (!veto) return true;

            __result = false;
            return false;
        }
    }
}
