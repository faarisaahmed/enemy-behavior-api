using System;
using System.Collections.Generic;

namespace EnemyBehaviorApi.Schema
{
    /// <summary>
    /// A flat record of one PlayMaker state, classified or not.
    /// </summary>
    /// <remarks>
    /// <see cref="EnemyProfile"/> only carries descriptors for states the classifier
    /// recognised, which leaves two gaps this closes. An annotation author cannot write
    /// <c>"classify": "attack"</c> for a missed state without knowing its exact key, and
    /// nobody can tell whether a wrong verdict came from bad weights or from the state
    /// never being scanned. Both need the complete list, so the profile carries it.
    /// </remarks>
    [Serializable]
    public sealed class StateSummary
    {
        /// <summary><see cref="StateRef.Key"/> - the form annotation files use.</summary>
        public string Key { get; set; }

        public string FsmName { get; set; }

        public string StateName { get; set; }

        /// <summary>Action type names, in execution order. The raw evidence.</summary>
        public List<string> ActionTypes { get; set; } = new List<string>();

        /// <summary>Events that lead into this state.</summary>
        public List<string> IncomingEvents { get; set; } = new List<string>();

        /// <summary>Outgoing transitions, as <c>EVENT -&gt; State</c>.</summary>
        public List<string> Transitions { get; set; } = new List<string>();

        /// <summary>Id of the descriptor built from this state, or null if it was not classified.</summary>
        public string ActionId { get; set; }

        /// <summary>Id of the movement descriptor built from this state, if any.</summary>
        public string MovementId { get; set; }

        /// <summary>True when the classifier produced nothing for this state.</summary>
        public bool IsUnclassified => ActionId == null && MovementId == null;

        /// <summary>Attack score the classifier computed, for tuning.</summary>
        public float AttackScore { get; set; }

        /// <summary>Movement score the classifier computed, for tuning.</summary>
        public float MovementScore { get; set; }

        public bool IsStartState { get; set; }

        public bool IsDecisionState { get; set; }

        public override string ToString() => Key + (IsUnclassified ? " (unclassified)" : "");
    }
}
