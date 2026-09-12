using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace EnemyBehaviorApi.Discovery
{
    /// <summary>Raw, unclassified facts about one PlayMaker state.</summary>
    public sealed class ScannedState
    {
        public string FsmPath;
        public string FsmName;
        public string StateName;

        /// <summary>Action type names, in execution order.</summary>
        public List<string> ActionTypes = new List<string>();

        /// <summary>The live action instances, parallel to <see cref="ActionTypes"/>. Not serialised.</summary>
        public List<FsmStateAction> Actions = new List<FsmStateAction>();

        /// <summary>Outgoing transitions as event name -> destination state.</summary>
        public List<KeyValuePair<string, string>> Transitions = new List<KeyValuePair<string, string>>();

        /// <summary>Events that lead *into* this state, gathered across the whole FSM.</summary>
        public List<string> IncomingEvents = new List<string>();

        /// <summary>States that transition into this one.</summary>
        public List<string> IncomingStates = new List<string>();

        public bool IsStartState;

        /// <summary>
        /// True when a state that transitions into this one arms a damaging hitbox, and
        /// this state does not disarm it.
        /// </summary>
        /// <remarks>
        /// A committed attack spans several states - Bone Thumper arms its collider in
        /// "Start Roll", then rolls and slams for as long as the player lets it. Only the
        /// first state contains the arming action, so judging each state alone makes the
        /// damaging part of the attack look like plain locomotion.
        /// </remarks>
        public bool InheritsArmedHitbox;

        /// <summary>Child objects this state switches ON. Arming a hitbox is attack evidence.</summary>
        public List<string> ArmedObjects = new List<string>();

        /// <summary>Child objects this state switches OFF. Disarming is the end of an attack, or sleep.</summary>
        public List<string> DisarmedObjects = new List<string>();
    }

    /// <summary>Raw, unclassified facts about one PlayMakerFSM.</summary>
    public sealed class ScannedFsm
    {
        public PlayMakerFSM Component;
        public string FsmPath;
        public string FsmName;
        public string StartState;
        public List<ScannedState> States = new List<ScannedState>();
        public List<KeyValuePair<string, string>> GlobalTransitions = new List<KeyValuePair<string, string>>();

        public string Key => (FsmPath.Length == 0 ? "" : FsmPath + "/") + FsmName;
    }

    /// <summary>
    /// Walks an enemy's PlayMaker FSMs and reports what is actually in them. No judgement
    /// about meaning - that is <see cref="BehaviorClassifier"/>'s job.
    /// </summary>
    public static class FsmScanner
    {
        /// <summary>Fields on an action worth reporting as tunable parameters.</summary>
        private static readonly Type[] TunableFsmTypes =
        {
            typeof(FsmFloat), typeof(FsmInt), typeof(FsmBool), typeof(FsmVector2),
        };

        /// <summary>
        /// Scans every FSM on <paramref name="root"/> and its children.
        /// </summary>
        /// <param name="root">The enemy root. Paths in the result are relative to it.</param>
        /// <param name="warnings">Appended to on non-fatal trouble; scanning continues.</param>
        public static List<ScannedFsm> Scan(GameObject root, List<string> warnings)
        {
            var result = new List<ScannedFsm>();
            if (root == null) return result;

            // Include inactive children: enemies routinely park attack FSMs on objects
            // that are disabled until the attack runs, and those are exactly the states a
            // consumer most wants to know about.
            var components = root.GetComponentsInChildren<PlayMakerFSM>(includeInactive: true);

            foreach (var fsm in components)
            {
                try
                {
                    var scanned = ScanOne(root, fsm, warnings);
                    if (scanned != null) result.Add(scanned);
                }
                catch (Exception e)
                {
                    warnings?.Add($"FSM '{fsm?.FsmName}' on '{fsm?.gameObject?.name}' threw while scanning: {e.Message}");
                }
            }

            return result;
        }

        private static ScannedFsm ScanOne(GameObject root, PlayMakerFSM component, List<string> warnings)
        {
            Fsm fsm = component?.Fsm;
            if (fsm == null) return null;

            var scanned = new ScannedFsm
            {
                Component = component,
                FsmPath = EnemyIdentity.RelativePath(root.transform, component.transform),
                FsmName = component.FsmName ?? string.Empty,
                StartState = fsm.StartState,
            };

            FsmState[] states = fsm.States;
            if (states == null) return scanned;

            // Pass 1: the states themselves.
            foreach (var state in states)
            {
                if (state == null) continue;
                try
                {
                    scanned.States.Add(ScanState(scanned, state));
                }
                catch (Exception e)
                {
                    warnings?.Add($"state '{scanned.FsmName}/{state.Name}' threw while scanning: {e.Message}");
                }
            }

            // Pass 2: reverse the transition graph. A state's entry points are what an
            // Override caller needs, and they only exist as outgoing edges on other states.
            var byName = scanned.States.ToDictionary(s => s.StateName, s => s, StringComparer.Ordinal);
            foreach (var from in scanned.States)
            {
                foreach (var transition in from.Transitions)
                {
                    if (!byName.TryGetValue(transition.Value, out var to)) continue;
                    if (!to.IncomingEvents.Contains(transition.Key)) to.IncomingEvents.Add(transition.Key);
                    if (!to.IncomingStates.Contains(from.StateName)) to.IncomingStates.Add(from.StateName);
                }
            }

            // Global transitions fire from any state, which makes them the most reliable
            // way to drive an FSM from outside - no need to know where it currently is.
            foreach (var gt in fsm.GlobalTransitions ?? Array.Empty<FsmTransition>())
            {
                if (gt == null) continue;
                string eventName = gt.EventName ?? string.Empty;
                scanned.GlobalTransitions.Add(new KeyValuePair<string, string>(eventName, gt.ToState));
                if (byName.TryGetValue(gt.ToState ?? string.Empty, out var to) && !to.IncomingEvents.Contains(eventName))
                    to.IncomingEvents.Add(eventName);
            }

            foreach (var state in scanned.States)
                state.IsStartState = string.Equals(state.StateName, scanned.StartState, StringComparison.Ordinal);

            return scanned;
        }

        private static ScannedState ScanState(ScannedFsm owner, FsmState state)
        {
            var scanned = new ScannedState
            {
                FsmPath = owner.FsmPath,
                FsmName = owner.FsmName,
                StateName = state.Name ?? string.Empty,
            };

            // PlayMaker keeps actions serialised in ActionData and only materialises them
            // on demand. Scanning an enemy that has not run yet - which is the common case,
            // since we want the profile before the fight starts - finds an empty Actions
            // array unless we ask for them first.
            if (!state.ActionsLoaded)
            {
                try { state.LoadActions(); }
                catch (Exception) { /* falls through to whatever Actions holds */ }
            }

            foreach (var action in state.Actions ?? Array.Empty<FsmStateAction>())
            {
                if (action == null) continue;
                scanned.Actions.Add(action);
                scanned.ActionTypes.Add(action.GetType().Name);
                CollectToggledObjects(action, scanned.ArmedObjects, scanned.DisarmedObjects);
            }

            foreach (var transition in state.Transitions ?? Array.Empty<FsmTransition>())
            {
                if (transition == null) continue;
                scanned.Transitions.Add(
                    new KeyValuePair<string, string>(transition.EventName ?? string.Empty, transition.ToState ?? string.Empty));
            }

            return scanned;
        }

        /// <summary>
        /// Pulls GameObject targets out of a toggle-style action, split by whether the
        /// action turns them on or off.
        /// </summary>
        /// <remarks>
        /// The direction is the whole point. <c>ActivateGameObject</c> and
        /// <c>SetCollider</c> both carry a bool saying which way they go, and reading only
        /// the target - as the first version did - makes "switch the hitbox off and go to
        /// sleep" indistinguishable from "swing". Measured result: Bone Thumper's
        /// "Start Sleep" and "Caged Sleep" both classified as melee attacks.
        /// </remarks>
        private static void CollectToggledObjects(FsmStateAction action, List<string> armed, List<string> disarmed)
        {
            string typeName = action.GetType().Name;
            if (typeName.IndexOf("Activate", StringComparison.Ordinal) < 0 &&
                typeName.IndexOf("SetCollider", StringComparison.Ordinal) < 0 &&
                typeName.IndexOf("SetGameObject", StringComparison.Ordinal) < 0)
                return;

            var fields = action.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);

            // Default to "on": most toggle actions in the game arm rather than disarm, and
            // an unreadable flag should not silently invert the meaning.
            bool turningOn = true;
            foreach (var field in fields)
            {
                if (field.Name.IndexOf("activate", StringComparison.OrdinalIgnoreCase) < 0 &&
                    field.Name.IndexOf("active", StringComparison.OrdinalIgnoreCase) < 0 &&
                    field.Name.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    object raw = field.GetValue(action);
                    if (raw is FsmBool fsmBool && !fsmBool.UsesVariable) { turningOn = fsmBool.Value; break; }
                    if (raw is bool plain) { turningOn = plain; break; }
                }
                catch (Exception) { /* keep the default */ }
            }

            var into = turningOn ? armed : disarmed;

            foreach (var field in fields)
            {
                object value;
                try { value = field.GetValue(action); }
                catch (Exception) { continue; }

                switch (value)
                {
                    case FsmGameObject fsmGo when fsmGo.Value != null:
                        into.Add(fsmGo.Value.name);
                        break;
                    case FsmOwnerDefault owner:
                        // "Use Owner" leaves GameObject null and means the FSM's own object.
                        // Skipping that case found only 4 armed hitboxes across 18 enemies,
                        // because enemies overwhelmingly toggle a collider on themselves or
                        // on the object the FSM lives on.
                        string target = owner.GameObject?.Value != null
                            ? owner.GameObject.Value.name
                            : action.Owner?.name;
                        if (!string.IsNullOrEmpty(target)) into.Add(target);
                        break;
                    case GameObject go when go != null:
                        into.Add(go.name);
                        break;
                }
            }
        }

        /// <summary>
        /// The action fields worth exposing as Influence-tier parameters: the numbers a
        /// designer would have typed into the PlayMaker inspector.
        /// </summary>
        public static IEnumerable<FieldInfo> TunableFields(FsmStateAction action)
        {
            if (action == null) yield break;
            foreach (var field in action.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsStatic || field.IsInitOnly) continue;
                if (TunableFsmTypes.Contains(field.FieldType) ||
                    field.FieldType == typeof(float) || field.FieldType == typeof(int) || field.FieldType == typeof(bool))
                    yield return field;
            }
        }
    }
}
