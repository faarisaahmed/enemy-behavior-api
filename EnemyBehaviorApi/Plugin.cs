using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EnemyBehaviorApi.Annotations;
using EnemyBehaviorApi.Authority;
using EnemyBehaviorApi.Discovery;
using EnemyBehaviorApi.Runtime;
using HarmonyLib;

namespace EnemyBehaviorApi
{
    /// <summary>
    /// BepInEx entry point. Wires the pieces together and gets out of the way - all the
    /// behaviour lives in <see cref="EnemyBehavior"/>.
    /// </summary>
    [BepInPlugin(Guid, "Enemy Behavior API", "0.1.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.faaris.enemybehaviorapi";

        internal static ManualLogSource Log { get; private set; }

        private ConfigEntry<bool> _autoRegister;
        private ConfigEntry<bool> _verbose;

        private void Awake()
        {
            Log = Logger;

            _autoRegister = Config.Bind(
                "General", "AutoRegisterEnemies", true,
                "Discover every enemy as it spawns. Off means enemies are only discovered when a mod claims one, " +
                "which is cheaper but means EnemyAppeared never fires.");

            _verbose = Config.Bind(
                "General", "VerboseLogging", false,
                "Log every discovery and authority change. Useful when writing annotations, noisy otherwise.");

            void Report(string message)
            {
                if (_verbose.Value) Log.LogInfo(message);
                else Log.LogDebug(message);
            }

            string annotationDirectory = Path.Combine(Path.GetDirectoryName(Info.Location) ?? ".", "annotations");

            var annotations = new AnnotationStore(Report);
            annotations.LoadDirectory(annotationDirectory);

            var discovery = new DiscoveryEngine(annotations, Report);
            var registry = new EnemyRegistry(discovery, Report);
            var authority = new AuthorityRegistry(msg => Log.LogWarning(msg));

            FsmTransitionGate.Install(registry, authority, msg => Log.LogError(msg));
            FsmDecisionGate.Install(registry, authority);
            EnemyTracker.AutoRegister = _autoRegister.Value;
            EnemyTracker.Install(registry);

            EnemyBehavior.Initialize(discovery, registry, authority, annotations);

            new Harmony(Guid).PatchAll();

            Log.LogInfo($"Enemy Behavior API ready - schema v{EnemyBehavior.Version}, " +
                        $"{annotations.Count} annotated enemy id(s) from '{annotationDirectory}'");
        }
    }
}
