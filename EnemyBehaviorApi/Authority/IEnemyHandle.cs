using System;
using EnemyBehaviorApi.Runtime;
using EnemyBehaviorApi.Schema;

namespace EnemyBehaviorApi.Authority
{
    /// <summary>Fired when an enemy an observer is watching changes PlayMaker state.</summary>
    public sealed class StateChangedEventArgs : EventArgs
    {
        public EnemyInstance Enemy { get; }

        /// <summary>The state being entered.</summary>
        public StateRef State { get; }

        /// <summary>Name of the state being left, or null if the FSM is starting.</summary>
        public string PreviousState { get; }

        /// <summary>
        /// The behaviour this state corresponds to, if it is one discovery classified. Null
        /// for the FSM's plumbing states, which are most of them.
        /// </summary>
        public BehaviorDescriptor Behavior { get; }

        /// <summary>True when the transition was fired by an Override holder rather than the enemy.</summary>
        public bool WasDriven { get; }

        internal StateChangedEventArgs(EnemyInstance enemy, StateRef state, string previousState,
                                       BehaviorDescriptor behavior, bool wasDriven)
        {
            Enemy = enemy;
            State = state;
            PreviousState = previousState;
            Behavior = behavior;
            WasDriven = wasDriven;
        }
    }

    /// <summary>
    /// A consumer's claim on one enemy instance. Everything a mod does to an enemy goes
    /// through one of these.
    /// </summary>
    /// <remarks>
    /// Dispose it when you are done. Disposing reverts every parameter this handle wrote,
    /// releases Override so another mod can claim the enemy, and unsubscribes. A handle
    /// left undisposed on an enemy that dies is cleaned up with the instance, so leaking
    /// one is survivable - but it will hold Override against other mods until then.
    /// </remarks>
    public interface IEnemyHandle : IDisposable
    {
        /// <summary>The enemy this handle is scoped to. One instance, not one kind.</summary>
        EnemyInstance Enemy { get; }

        /// <summary>What discovery knows about this enemy's kind.</summary>
        EnemyProfile Profile { get; }

        /// <summary>The tier this handle was granted. May be lower than what was asked for.</summary>
        AuthorityTier Tier { get; }

        /// <summary>Id of the mod holding this handle, for conflict reporting.</summary>
        string OwnerId { get; }

        /// <summary>False once disposed, or once the enemy is destroyed.</summary>
        bool IsValid { get; }

        // ---- Observe ----------------------------------------------------------------

        /// <summary>
        /// Raised when this enemy enters a new PlayMaker state, on any FSM discovery
        /// scanned. Fires on the main thread, inside the transition, so do not block.
        /// </summary>
        event EventHandler<StateChangedEventArgs> StateChanged;

        /// <summary>The behaviour this enemy is executing right now, or null if it is in an unclassified state.</summary>
        BehaviorDescriptor CurrentBehavior { get; }

        // ---- Influence --------------------------------------------------------------

        /// <summary>Reads a parameter's live value. Available at every tier.</summary>
        /// <returns>False if the parameter cannot be resolved on this instance.</returns>
        bool TryGetParameter(string parameterId, out float value);

        /// <summary>
        /// Writes a parameter. Requires <see cref="AuthorityTier.Influence"/> or above.
        /// </summary>
        /// <remarks>
        /// The original value is recorded on the first write and restored on
        /// <see cref="IDisposable.Dispose"/>, so an Influence-tier mod cannot permanently
        /// alter an enemy by forgetting to clean up.
        /// </remarks>
        /// <returns>False if the tier is too low or the parameter cannot be resolved.</returns>
        bool TrySetParameter(string parameterId, float value);

        /// <summary>
        /// Enables or disables one action inside a behaviour's state - the way to remove a
        /// single part of an attack without removing the attack.
        /// </summary>
        /// <remarks>
        /// Reverted on dispose like a parameter write. Disabling actions is blunt: the
        /// state still runs and still transitions out, it just does less. Disabling the
        /// action that fires the state's exit event will hang the FSM there.
        /// </remarks>
        bool TrySetActionEnabled(string behaviorId, int actionIndex, bool enabled);

        // ---- Override ---------------------------------------------------------------

        /// <summary>
        /// Makes the enemy perform a discovered behaviour now. Requires
        /// <see cref="AuthorityTier.Override"/>.
        /// </summary>
        /// <remarks>
        /// Prefers firing one of the behaviour's own <see cref="BehaviorDescriptor.TriggerEvents"/>,
        /// which lets the enemy's guards and entry actions run as designed. Falls back to a
        /// direct state switch when no trigger event applies from where the FSM currently
        /// is - blunter, and more likely to skip a wind-up.
        /// </remarks>
        /// <returns>False if the tier is too low, or the behaviour is not on this enemy.</returns>
        bool Fire(string behaviorId);

        /// <summary>
        /// Sends a raw PlayMaker event to one of this enemy's FSMs. Requires
        /// <see cref="AuthorityTier.Override"/>. The escape hatch for events discovery did
        /// not surface.
        /// </summary>
        /// <param name="eventName">The PlayMaker event to send.</param>
        /// <param name="fsmKey">
        /// <c>path/FsmName</c>. Null uses <see cref="EnemyProfile.PrimaryFsm"/>.
        /// </param>
        bool SendEvent(string eventName, string fsmKey = null);

        /// <summary>
        /// Hands the enemy back its own decision-making without releasing the handle - a
        /// pause, rather than a release. Requires <see cref="AuthorityTier.Override"/>.
        /// </summary>
        void SetSuppressionActive(bool active);

        /// <summary>What this handle's Override claim suppresses. Settable while held.</summary>
        OverridePolicy Policy { get; set; }
    }
}
