using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using EnemyBehaviorApi.Schema;

namespace EnemyBehaviorApi.Authority
{
    /// <summary>
    /// The live Override claim on one enemy instance. Holds the precomputed lookups the
    /// Harmony gate consults, so the gate itself does no work beyond two set probes.
    /// </summary>
    public sealed class OverrideClaim
    {
        /// <summary>
        /// States that must always be allowed through, no matter the policy.
        /// </summary>
        /// <remarks>
        /// Without this an Override holder can make an enemy immortal by accident: block
        /// every transition and the death state never runs, so the enemy takes lethal
        /// damage and stands there. Matching on name is crude, but these names are
        /// consistent across the game and being wrong in the permissive direction only
        /// costs a mod some control, while being wrong the other way breaks the fight.
        /// </remarks>
        private static readonly Regex AlwaysAllowedStates = new Regex(
            @"(?i)\b(death|die|dead|dying|corpse|destroy|stun|stunned|hit|recoil|knockback|land|landed|fall|return|reset|init|idle)\b",
            RegexOptions.Compiled);

        private readonly HashSet<string> _decisionStates = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _extraAllowed = new HashSet<string>(StringComparer.Ordinal);

        public string OwnerId { get; }

        public OverridePolicy Policy { get; set; }

        /// <summary>Whether the claim is currently vetoing. Toggled by <c>IEnemyHandle.SetSuppressionActive</c>.</summary>
        public bool SuppressionActive { get; set; } = true;

        public OverrideClaim(string ownerId, EnemyProfile profile, OverridePolicy policy)
        {
            OwnerId = ownerId;
            Policy = policy;

            foreach (var stateRef in profile?.DecisionStates ?? new List<StateRef>())
                _decisionStates.Add(stateRef.FsmName + "/" + stateRef.StateName);

            // Any state tagged lifecycle or reactive is the enemy responding to the world
            // rather than choosing, so it passes even under SuppressAll.
            foreach (var behavior in profile?.AllBehaviors() ?? Array.Empty<BehaviorDescriptor>())
            {
                if (behavior.HasTag("lifecycle") || behavior.HasTag("reactive"))
                    _extraAllowed.Add(behavior.State.StateName);
            }
        }

        /// <summary>True when the FSM is leaving a state whose job is choosing what comes next.</summary>
        public bool IsDecisionState(string fsmName, string stateName) =>
            stateName != null && _decisionStates.Contains(fsmName + "/" + stateName);

        /// <summary>True when a transition into this state must be let through regardless of policy.</summary>
        public bool IsAlwaysAllowed(string stateName) =>
            stateName != null && (_extraAllowed.Contains(stateName) || AlwaysAllowedStates.IsMatch(stateName));

        /// <summary>Adds a state name that should always pass, for a case the defaults miss.</summary>
        public void AllowState(string stateName)
        {
            if (!string.IsNullOrEmpty(stateName)) _extraAllowed.Add(stateName);
        }
    }
}
