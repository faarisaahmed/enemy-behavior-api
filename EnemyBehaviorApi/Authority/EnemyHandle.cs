using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using EnemyBehaviorApi.Runtime;
using EnemyBehaviorApi.Schema;
using HutongGames.PlayMaker;
using UnityEngine;

namespace EnemyBehaviorApi.Authority
{
    /// <summary>
    /// The working implementation of <see cref="IEnemyHandle"/>. Created by
    /// <see cref="AuthorityRegistry.Claim"/>, never directly.
    /// </summary>
    public sealed class EnemyHandle : IEnemyHandle
    {
        /// <summary>A value this handle changed, and what it was before.</summary>
        private readonly struct Revert
        {
            public readonly Action Undo;
            public readonly string Description;
            public Revert(Action undo, string description) { Undo = undo; Description = description; }
        }

        private readonly AuthorityRegistry _registry;
        private readonly List<Revert> _reverts = new List<Revert>();
        private readonly Dictionary<string, object> _originalValues = new Dictionary<string, object>(StringComparer.Ordinal);
        private bool _disposed;

        public EnemyInstance Enemy { get; }
        public EnemyProfile Profile => Enemy?.Profile;
        public AuthorityTier Tier { get; private set; }
        public string OwnerId { get; }
        public OverrideClaim Claim { get; private set; }

        /// <summary>What the holder originally asked for, so a downgraded claim can be promoted later.</summary>
        internal AuthorityTier RequestedTier { get; }

        public bool IsValid => !_disposed && Enemy != null && Enemy.IsAlive;

        public event EventHandler<StateChangedEventArgs> StateChanged;

        internal EnemyHandle(AuthorityRegistry registry, EnemyInstance enemy, string ownerId,
                             AuthorityTier granted, OverridePolicy policy)
        {
            _registry = registry;
            Enemy = enemy;
            OwnerId = ownerId;
            Tier = granted;
            RequestedTier = granted;

            if (granted == AuthorityTier.Override) Claim = new OverrideClaim(ownerId, enemy.Profile, policy);
        }

        /// <summary>Upgrades a downgraded claim when the incumbent releases. Registry-only.</summary>
        internal void PromoteToOverride()
        {
            if (_disposed) return;
            Tier = AuthorityTier.Override;
            Claim = Claim ?? new OverrideClaim(OwnerId, Profile, OverridePolicy.SuppressDecisions);
        }

        internal void RaiseStateChanged(StateChangedEventArgs args)
        {
            try { StateChanged?.Invoke(this, args); }
            catch (Exception e) { Debug.LogError($"[EnemyBehaviorApi] '{OwnerId}' threw handling a state change: {e}"); }
        }

        public OverridePolicy Policy
        {
            get => Claim?.Policy ?? OverridePolicy.SuppressDecisions;
            set { if (Claim != null) Claim.Policy = value; }
        }

        public BehaviorDescriptor CurrentBehavior
        {
            get
            {
                if (!IsValid || Profile == null) return null;

                // Several FSMs are live at once; the one that matters is whichever is
                // currently sitting in a state we classified.
                foreach (var entry in Enemy.Fsms)
                {
                    string active = entry.Value?.ActiveStateName;
                    if (string.IsNullOrEmpty(active)) continue;

                    var match = Profile.AllBehaviors().FirstOrDefault(b =>
                        b.State.StateName == active &&
                        ((b.State.FsmPath.Length == 0 ? "" : b.State.FsmPath + "/") + b.State.FsmName) == entry.Key);

                    if (match != null) return match;
                }
                return null;
            }
        }

        // ---- Influence ------------------------------------------------------------------

        public bool TryGetParameter(string parameterId, out float value)
        {
            value = 0f;
            if (!IsValid) return false;

            var parameter = Profile?.FindParameter(parameterId);
            if (parameter == null) return false;

            object raw = ReadRaw(parameter);
            if (raw == null) return false;

            try
            {
                value = Convert.ToSingle(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception) { return false; }
        }

        public bool TrySetParameter(string parameterId, float value)
        {
            if (!Require(AuthorityTier.Influence, nameof(TrySetParameter))) return false;

            var parameter = Profile?.FindParameter(parameterId);
            if (parameter == null) return false;

            // Advisory bounds from the annotation file, when someone bothered to state
            // them. Clamping rather than refusing: a mod asking for a speed of 500 wants
            // "as fast as this enemy goes", not an error.
            if (parameter.Min.HasValue) value = Math.Max(parameter.Min.Value, value);
            if (parameter.Max.HasValue) value = Math.Min(parameter.Max.Value, value);

            object original = ReadRaw(parameter);
            if (original == null) return false;

            if (!_originalValues.ContainsKey(parameterId))
            {
                _originalValues[parameterId] = original;
                var captured = original;
                _reverts.Add(new Revert(() => WriteRaw(parameter, captured), $"parameter {parameterId}"));
            }

            object converted = ConvertFor(parameter.Kind, value);
            if (converted == null) return false;

            bool ok = WriteRaw(parameter, converted);
            if (ok) NoteWriter(parameterId);
            return ok;
        }

        public bool TrySetActionEnabled(string behaviorId, int actionIndex, bool enabled)
        {
            if (!Require(AuthorityTier.Influence, nameof(TrySetActionEnabled))) return false;

            var behavior = Profile?.FindBehavior(behaviorId);
            var action = ResolveAction(behavior?.State, actionIndex);
            if (action == null) return false;

            bool original = action.Enabled;
            if (original == enabled) return true;

            _reverts.Add(new Revert(() => action.Enabled = original, $"action {behaviorId}[{actionIndex}].Enabled"));
            action.Enabled = enabled;
            return true;
        }

        // ---- Override -------------------------------------------------------------------

        public bool Fire(string behaviorId)
        {
            if (!Require(AuthorityTier.Override, nameof(Fire))) return false;

            var behavior = Profile?.FindBehavior(behaviorId);
            if (behavior == null)
            {
                Debug.LogWarning($"[EnemyBehaviorApi] '{OwnerId}' fired unknown behaviour '{behaviorId}' on {Enemy}");
                return false;
            }

            var fsm = Enemy.ResolveFsm(behavior.State);
            if (fsm?.Fsm == null) return false;

            // Firing the enemy's own event is always better than forcing the state: entry
            // guards run, the FSM's bookkeeping stays consistent, and anything else
            // listening for that event hears it. Only use it if the FSM can actually act on
            // it from where it is, otherwise the event is swallowed and nothing happens.
            string usable = behavior.TriggerEvents.FirstOrDefault(e => CanHandleNow(fsm.Fsm, e));

            bool fired = false;
            FsmTransitionGate.Drive(() =>
            {
                if (usable != null)
                {
                    fsm.Fsm.Event(usable);
                    fired = true;
                }
                else if (fsm.Fsm.GetState(behavior.State.StateName) != null)
                {
                    // No route in from here. Force it, and say so: this is where a
                    // wind-up gets skipped and an attack comes out with no tell.
                    fsm.Fsm.SetState(behavior.State.StateName);
                    fired = true;
                }
            });

            return fired;
        }

        public bool SendEvent(string eventName, string fsmKey = null)
        {
            if (!Require(AuthorityTier.Override, nameof(SendEvent))) return false;
            if (string.IsNullOrEmpty(eventName)) return false;

            var fsm = fsmKey == null ? Enemy.PrimaryFsm : Enemy.ResolveFsm(fsmKey);
            if (fsm?.Fsm == null) return false;

            FsmTransitionGate.Drive(() => fsm.Fsm.Event(eventName));
            return true;
        }

        public void SetSuppressionActive(bool active)
        {
            if (!Require(AuthorityTier.Override, nameof(SetSuppressionActive))) return;
            Claim.SuppressionActive = active;
        }

        // ---- Plumbing -------------------------------------------------------------------

        /// <summary>
        /// Whether the FSM would act on this event right now - a transition out of the
        /// active state, or a global transition.
        /// </summary>
        private static bool CanHandleNow(Fsm fsm, string eventName)
        {
            if (fsm.ActiveState?.Transitions?.Any(t => t.EventName == eventName) == true) return true;
            return fsm.GlobalTransitions?.Any(t => t.EventName == eventName) == true;
        }

        private FsmStateAction ResolveAction(StateRef stateRef, int index)
        {
            if (stateRef == null) return null;

            FsmState state = Enemy.ResolveFsm(stateRef)?.Fsm?.GetState(stateRef.StateName);
            if (state == null) return null;
            if (!state.ActionsLoaded)
            {
                try { state.LoadActions(); } catch (Exception) { return null; }
            }

            var actions = state.Actions;
            return actions != null && index >= 0 && index < actions.Length ? actions[index] : null;
        }

        private object ReadRaw(ParameterRef parameter)
        {
            try
            {
                if (parameter.Source == ParameterSource.FsmVariable)
                {
                    var variables = Enemy.ResolveFsm(parameter.FsmKey())?.Fsm?.Variables;
                    return variables?.GetVariable(parameter.VariableName)?.RawValue;
                }

                var action = ResolveAction(parameter.State, parameter.ActionIndex);
                if (action == null || !TypeMatches(action, parameter)) return null;

                object field = action.GetType().GetField(parameter.FieldName, BindingFlags.Public | BindingFlags.Instance)
                                     ?.GetValue(action);
                return field is NamedVariable named ? named.RawValue : field;
            }
            catch (Exception) { return null; }
        }

        private bool WriteRaw(ParameterRef parameter, object value)
        {
            try
            {
                if (parameter.Source == ParameterSource.FsmVariable)
                {
                    var variable = Enemy.ResolveFsm(parameter.FsmKey())?.Fsm?.Variables?.GetVariable(parameter.VariableName);
                    if (variable == null) return false;
                    variable.SafeAssign(value);
                    return true;
                }

                var action = ResolveAction(parameter.State, parameter.ActionIndex);
                if (action == null || !TypeMatches(action, parameter)) return false;

                var field = action.GetType().GetField(parameter.FieldName, BindingFlags.Public | BindingFlags.Instance);
                if (field == null) return false;

                if (field.GetValue(action) is NamedVariable named)
                {
                    named.SafeAssign(value);
                    return true;
                }

                field.SetValue(action, Convert.ChangeType(value, field.FieldType, CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Guards against an action index that no longer points where discovery thought.
        /// </summary>
        /// <remarks>
        /// A <see cref="ParameterRef"/> addresses an action by position, which is stable
        /// for a given build but not across one. Checking the type name before writing
        /// turns "a game update silently moved this action" from a corrupted enemy into a
        /// failed call.
        /// </remarks>
        private static bool TypeMatches(FsmStateAction action, ParameterRef parameter) =>
            parameter.ActionTypeName == null || action.GetType().Name == parameter.ActionTypeName;

        private static object ConvertFor(ParameterKind kind, float value)
        {
            switch (kind)
            {
                case ParameterKind.Float: return value;
                case ParameterKind.Int: return (int)Math.Round(value);
                case ParameterKind.Bool: return value != 0f;
                default: return null; // Vector2 needs the two-component setter, not this one.
            }
        }

        private void NoteWriter(string parameterId)
        {
            foreach (var other in _registry.ActiveHandles.OfType<EnemyHandle>())
            {
                if (ReferenceEquals(other, this) || other.Enemy?.InstanceId != Enemy.InstanceId) continue;
                if (other._originalValues.ContainsKey(parameterId))
                    _registry.ReportParameterConflict(Enemy, parameterId, OwnerId, other.OwnerId);
            }
        }

        private bool Require(AuthorityTier needed, string operation)
        {
            if (!IsValid) return false;
            if (Tier >= needed) return true;

            Debug.LogWarning(
                $"[EnemyBehaviorApi] '{OwnerId}' called {operation} on {Enemy} at {Tier} tier, needs {needed}");
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Reverse order: a later write may depend on an earlier one having happened.
            for (int i = _reverts.Count - 1; i >= 0; i--)
            {
                try { _reverts[i].Undo(); }
                catch (Exception e) { Debug.LogWarning($"[EnemyBehaviorApi] could not revert {_reverts[i].Description}: {e.Message}"); }
            }

            _reverts.Clear();
            _originalValues.Clear();
            StateChanged = null;
            Claim = null;
            _registry.Release(this);
        }
    }

    internal static class ParameterRefExtensions
    {
        /// <summary>The <c>path/FsmName</c> key a parameter's FSM is registered under.</summary>
        public static string FsmKey(this ParameterRef parameter) =>
            (string.IsNullOrEmpty(parameter.FsmPath) ? "" : parameter.FsmPath + "/") + parameter.FsmName;
    }
}
