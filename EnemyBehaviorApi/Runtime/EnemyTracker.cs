using System.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EnemyBehaviorApi.Runtime
{
    /// <summary>
    /// Notices enemies appearing and disappearing, so consumers do not have to.
    /// </summary>
    /// <remarks>
    /// Registration is lazy on purpose. Discovery walks a whole FSM graph with reflection,
    /// and doing that for every enemy in every scene whether or not a mod cares is waste.
    /// So this only registers an enemy when someone is listening - see
    /// <see cref="EnemyBehavior.EnemyAppeared"/> - and otherwise leaves the work until a
    /// mod calls <see cref="EnemyBehavior.Claim(GameObject, string, Authority.AuthorityTier, Authority.OverridePolicy)"/> on something.
    /// </remarks>
    public sealed class EnemyTracker : MonoBehaviour
    {
        private const float PruneIntervalSeconds = 1f;

        private static EnemyRegistry _registry;
        internal static bool AutoRegister { get; set; }

        internal static void Install(EnemyRegistry registry)
        {
            _registry = registry;
            var host = new GameObject("EnemyBehaviorApi.Tracker");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<EnemyTracker>();
        }

        private void OnEnable()
        {
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            StartCoroutine(PruneLoop());
        }

        private void OnDisable() => SceneManager.sceneUnloaded -= OnSceneUnloaded;

        // Every enemy in a scene goes away with it, and an instance id is only unique for
        // as long as the object lives, so a stale entry could later collide with a
        // genuinely different object.
        private static void OnSceneUnloaded(Scene scene) => _registry?.Clear();

        private static IEnumerator PruneLoop()
        {
            var wait = new WaitForSeconds(PruneIntervalSeconds);
            while (true)
            {
                yield return wait;
                _registry?.PruneDestroyed();
            }
        }

        /// <summary>
        /// Registers enemies as the game builds them.
        /// </summary>
        /// <remarks>
        /// <c>HealthManager.Start</c> is the right hook: it runs on everything damageable,
        /// which is as close to "is an enemy" as the game gets without a per-enemy list,
        /// and by Start the object's FSMs have initialised so discovery reads a live graph.
        /// </remarks>
        [HarmonyPatch(typeof(HealthManager), "Start")]
        internal static class HealthManagerStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(HealthManager __instance)
            {
                if (!AutoRegister || _registry == null || __instance == null) return;
                _registry.Register(__instance.gameObject);
            }
        }
    }
}
