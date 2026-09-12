using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using EnemyBehaviorApi;
using EnemyBehaviorApi.Authority;
using EnemyBehaviorApi.Runtime;
using EnemyBehaviorApi.Schema;
using UnityEngine;

namespace EnemyBehaviorApi.Example
{
    /// <summary>
    /// A worked example of all three tiers. Press F9 to cycle: off, Observe, Influence,
    /// Override.
    /// </summary>
    /// <remarks>
    /// This is what a consuming mod looks like end to end. It never names an enemy, never
    /// names a state, and never touches PlayMaker - everything it does comes out of the
    /// discovered profile, which is the point of the API.
    /// </remarks>
    [BepInPlugin(Guid, "Enemy Behavior API Example", "0.1.0")]
    [BepInDependency(Plugin.Guid)]
    public sealed class ExamplePlugin : BaseUnityPlugin
    {
        public const string Guid = "com.example.enemybehavior.sample";

        private enum Demo { Off, Observe, Influence, Override }

        private readonly Dictionary<int, IEnemyHandle> _handles = new Dictionary<int, IEnemyHandle>();
        private Demo _mode = Demo.Off;
        private float _nextAttackAt;

        private void Awake()
        {
            // Off unless explicitly asked for. This demo writes to enemies and drives them,
            // which is the opposite of what a survey pass wants, and it would fight
            // SurveyPlugin over the same hotkey.
            var enable = Config.Bind("Demo", "EnableTierDemo", false,
                "Turns on the three-tier demo (F12). Leave this off while surveying - Influence and Override " +
                "both change what the enemy does, which invalidates a survey's runtime data.");

            if (!enable.Value)
            {
                enabled = false;
                return;
            }

            // Refuse to run against a schema this was not written for, rather than
            // misreading a profile and producing nonsense.
            if (!SchemaVersion.IsCompatible(SchemaVersion.Current))
            {
                Logger.LogError($"schema v{EnemyBehavior.Version} is not one this mod understands");
                enabled = false;
                return;
            }

            // Claim enemies as they spawn. Handles are per instance, so this is one claim
            // per enemy in the room, not one claim on "Roachfeeders".
            EnemyBehavior.EnemyAppeared += OnEnemyAppeared;
            EnemyBehavior.EnemyGone += enemy => _handles.Remove(enemy.InstanceId);

            Logger.LogInfo("tier demo armed - F12 cycles Observe / Influence / Override");
        }

        private void OnDestroy()
        {
            EnemyBehavior.EnemyAppeared -= OnEnemyAppeared;
            ReleaseAll();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F12))
            {
                _mode = (Demo)(((int)_mode + 1) % 4);
                Logger.LogInfo($"mode: {_mode}");

                ReleaseAll();
                foreach (var enemy in EnemyBehavior.ActiveEnemies.ToList()) OnEnemyAppeared(enemy);
            }

            if (_mode == Demo.Override && Time.time >= _nextAttackAt)
            {
                _nextAttackAt = Time.time + 2f;
                foreach (var handle in _handles.Values) ForceAnAttack(handle);
            }
        }

        private void OnEnemyAppeared(EnemyInstance enemy)
        {
            if (_mode == Demo.Off || _handles.ContainsKey(enemy.InstanceId)) return;

            AuthorityTier tier;
            switch (_mode)
            {
                case Demo.Influence: tier = AuthorityTier.Influence; break;
                case Demo.Override: tier = AuthorityTier.Override; break;
                default: tier = AuthorityTier.Observe; break;
            }

            // Another mod may already own this enemy. Worth knowing before asking.
            string incumbent = EnemyBehavior.OverrideHolder(enemy);
            if (tier == AuthorityTier.Override && incumbent != null)
                Logger.LogInfo($"{enemy} is already driven by '{incumbent}' - asking anyway, expecting Influence");

            var handle = EnemyBehavior.Claim(enemy, Guid, tier);
            if (handle == null) return;

            if (handle.Tier < tier)
                Logger.LogWarning($"wanted {tier} on {enemy}, got {handle.Tier}");

            _handles[enemy.InstanceId] = handle;
            handle.StateChanged += OnStateChanged;

            LogProfile(handle.Profile);
            if (handle.Tier >= AuthorityTier.Influence) MakeItFaster(handle);
        }

        // ---- Observe --------------------------------------------------------------------

        private void OnStateChanged(object sender, StateChangedEventArgs e)
        {
            // Most state changes are FSM plumbing with no descriptor. Only the classified
            // ones are worth reacting to.
            if (e.Behavior == null) return;

            string who = e.Enemy.Profile.EnemyId;
            string driven = e.WasDriven ? " (driven by us)" : "";

            switch (e.Behavior)
            {
                case ActionDescriptor attack:
                    Logger.LogInfo($"{who}: attacking with {attack.DisplayName} " +
                                   $"[{attack.Shape}, {attack.DamageAmount} dmg, conf {attack.Confidence:0.00}]{driven}");
                    break;
                case MovementDescriptor move:
                    Logger.LogInfo($"{who}: moving - {move.DisplayName} " +
                                   $"[{move.Mode}{(move.IsTargeted ? ", tracking you" : "")}]{driven}");
                    break;
            }
        }

        private void LogProfile(EnemyProfile profile)
        {
            Logger.LogInfo($"--- {profile.EnemyId} ({profile.MaxHealth} hp, " +
                           $"{(profile.IsAnnotated ? "annotated" : "reflected")}) ---");

            foreach (var attack in profile.Actions.OrderByDescending(a => a.Confidence))
                Logger.LogInfo($"  attack   {attack.DisplayName,-24} {attack.Shape,-11} conf {attack.Confidence:0.00}  {attack.Id}");

            foreach (var move in profile.Movements.OrderByDescending(m => m.Confidence))
                Logger.LogInfo($"  movement {move.DisplayName,-24} {move.Mode,-11} conf {move.Confidence:0.00}  {move.Id}");

            if (profile.Warnings.Count > 0)
                Logger.LogWarning($"  {profile.Warnings.Count} discovery warning(s) - first: {profile.Warnings[0]}");
        }

        // ---- Influence ------------------------------------------------------------------

        /// <summary>
        /// Speeds every movement state up by half, leaving the enemy in charge of when to
        /// use them.
        /// </summary>
        private void MakeItFaster(IEnemyHandle handle)
        {
            int changed = 0;

            foreach (var parameter in handle.Profile.Movements
                         .SelectMany(m => m.Parameters)
                         .Where(p => p.Kind == ParameterKind.Float &&
                                     p.Name.IndexOf("speed", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                if (!handle.TryGetParameter(parameter.Id, out float current) || current == 0f) continue;
                if (handle.TrySetParameter(parameter.Id, current * 1.5f)) changed++;
            }

            if (changed > 0) Logger.LogInfo($"{handle.Enemy}: sped up {changed} movement parameter(s)");
        }

        // ---- Override -------------------------------------------------------------------

        /// <summary>
        /// Makes the enemy use its most damaging attack on a timer instead of choosing for
        /// itself.
        /// </summary>
        private void ForceAnAttack(IEnemyHandle handle)
        {
            if (handle.Tier < AuthorityTier.Override || !handle.IsValid) return;

            // Skip wind-ups: firing the strike directly would give an attack with no tell,
            // which reads as the game cheating rather than as a harder fight.
            var attack = handle.Profile.Actions
                .Where(a => a.TelegraphFor == null && a.Confidence >= 0.5f)
                .OrderByDescending(a => a.DamageAmount)
                .ThenByDescending(a => a.Confidence)
                .FirstOrDefault();

            if (attack == null) return;
            if (handle.Fire(attack.Id)) Logger.LogInfo($"{handle.Enemy}: forced {attack.DisplayName}");
        }

        private void ReleaseAll()
        {
            foreach (var handle in _handles.Values) handle.Dispose();
            _handles.Clear();
        }
    }
}
