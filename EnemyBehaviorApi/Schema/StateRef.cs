using System;

namespace EnemyBehaviorApi.Schema
{
    /// <summary>
    /// Addresses one PlayMaker state on one enemy, stably enough to survive a respawn.
    /// </summary>
    /// <remarks>
    /// An enemy's FSMs are not always on its root object - a boss will hang its attack
    /// FSMs off children. <see cref="FsmPath"/> is the transform path relative to the
    /// enemy root ("" for the root itself), which is what makes a <see cref="StateRef"/>
    /// resolvable against a freshly spawned instance rather than only the one it
    /// was discovered on.
    /// </remarks>
    [Serializable]
    public sealed class StateRef : IEquatable<StateRef>
    {
        /// <summary>Transform path of the FSM's GameObject relative to the enemy root. Empty for the root.</summary>
        public string FsmPath { get; }

        /// <summary><c>PlayMakerFSM.FsmName</c>. Not unique on its own - an enemy can carry several FSMs named "Control".</summary>
        public string FsmName { get; }

        /// <summary><c>FsmState.Name</c>.</summary>
        public string StateName { get; }

        public StateRef(string fsmPath, string fsmName, string stateName)
        {
            FsmPath = fsmPath ?? string.Empty;
            FsmName = fsmName ?? string.Empty;
            StateName = stateName ?? string.Empty;
        }

        /// <summary>The form used as a JSON key in annotation files: <c>path/FsmName/StateName</c>.</summary>
        public string Key => (FsmPath.Length == 0 ? "" : FsmPath + "/") + FsmName + "/" + StateName;

        public bool Equals(StateRef other) =>
            other != null &&
            string.Equals(FsmPath, other.FsmPath, StringComparison.Ordinal) &&
            string.Equals(FsmName, other.FsmName, StringComparison.Ordinal) &&
            string.Equals(StateName, other.StateName, StringComparison.Ordinal);

        public override bool Equals(object obj) => Equals(obj as StateRef);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = FsmPath.GetHashCode();
                h = (h * 397) ^ FsmName.GetHashCode();
                return (h * 397) ^ StateName.GetHashCode();
            }
        }

        public override string ToString() => Key;

        /// <summary>Parses the <see cref="Key"/> form. Returns null if it has fewer than two segments.</summary>
        public static StateRef Parse(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            int lastSlash = key.LastIndexOf('/');
            if (lastSlash < 0) return null;
            string stateName = key.Substring(lastSlash + 1);
            string head = key.Substring(0, lastSlash);

            int fsmSlash = head.LastIndexOf('/');
            return fsmSlash < 0
                ? new StateRef(string.Empty, head, stateName)
                : new StateRef(head.Substring(0, fsmSlash), head.Substring(fsmSlash + 1), stateName);
        }
    }
}
