using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using EnemyBehaviorApi.Schema;

namespace EnemyBehaviorApi.Discovery
{
    /// <summary>
    /// One rule mapping a PlayMaker action type name to evidence about what the state
    /// containing it is doing.
    /// </summary>
    public sealed class ActionSignal
    {
        public Regex Pattern { get; }

        /// <summary>Evidence this state is an attack. Negative evidence is allowed and used.</summary>
        public float Attack { get; }

        /// <summary>Evidence this state is locomotion.</summary>
        public float Movement { get; }

        public AttackShape Shape { get; }

        public MovementMode Mode { get; }

        public string[] Tags { get; }

        /// <summary>
        /// How strongly this signal's <see cref="Shape"/>/<see cref="Mode"/> hint should be
        /// trusted against a competing one. Highest wins, regardless of action order.
        /// </summary>
        public float HintStrength { get; }

        public ActionSignal(string pattern, float attack, float movement,
                            AttackShape shape = AttackShape.Unknown,
                            MovementMode mode = MovementMode.Unknown,
                            float hintStrength = 1f,
                            params string[] tags)
        {
            Pattern = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            Attack = attack;
            Movement = movement;
            Shape = shape;
            Mode = mode;
            HintStrength = hintStrength;
            Tags = tags ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// The evidence table discovery classifies against, calibrated against a recorded
    /// Moss Grotto session (Bone Thumper, MossBone Fly, MossBone Crawler, MossBone Cocoon).
    /// </summary>
    /// <remarks>
    /// The first version of this table was reasoned from action names in the DLL and was
    /// badly wrong, because it assumed Silksong declares hero damage through FSM actions
    /// like <c>SetDamageHero</c>. It does not. Across every enemy measured, that action
    /// appears zero times. Silksong encodes an attack as:
    ///
    ///   animation + a damage collider toggled on + velocity, and often a collision listener
    ///
    /// with the damage itself living on a persistent <c>DamageHero</c> component on a child
    /// object. So the load-bearing signals here are <c>SetCollider</c> /
    /// <c>ActivateGameObject</c> aimed at a *toggled* damaging child (resolved structurally
    /// in <see cref="DiscoveryEngine"/>, not by name), and <c>Collision2dEvent</c> paired
    /// with velocity - the shape of every ram, drill and rolling charge in the game.
    ///
    /// The other correction: Team Cherry's enemies are thick with perception and branching
    /// actions - <c>CheckAlertRange</c>, <c>CheckCanSeeHero</c>, <c>FloatTestToBool</c>,
    /// <c>BoolAllTrue</c>, <c>CheckHeroPerformanceRegion</c>. These appear in every kind of
    /// state and must sit at or below zero, or they drag everything toward whichever
    /// verdict they were mistakenly attached to. The first table let <c>FaceObject</c> alone
    /// (weight 1.0, threshold 1.0) classify any state that turns to face you as movement,
    /// which is why "Turn" swamped the first survey.
    /// </remarks>
    public static class SignalTable
    {
        /// <summary>
        /// Weight a state must reach to be classified at all.
        /// </summary>
        /// <remarks>
        /// Deliberately above the weight of any single weak signal. At 1.0 with FaceObject
        /// at 1.0, one facing action was enough to call a state movement; the measured
        /// result was 14 of 19 classified states coming back "Turn".
        /// </remarks>
        public const float ClassificationThreshold = 1.6f;

        public static readonly IReadOnlyList<ActionSignal> Signals = new List<ActionSignal>
        {
            // ---- Attack: damage delivery -------------------------------------------
            // Kept for completeness and for enemies that do use them. Measured frequency
            // in Moss Grotto: zero. Do not rely on these alone.
            new ActionSignal(@"^SetDamageHero", 3.5f, 0f, AttackShape.Melee, MovementMode.Unknown, 3f, "damages-hero"),
            new ActionSignal(@"^DamageHeroDirectly", 4f, 0f, AttackShape.Direct, MovementMode.Unknown, 3f, "damages-hero"),
            new ActionSignal(@"Projectile", 3f, 0f, AttackShape.Projectile, MovementMode.Unknown, 3f, "spawns-projectile"),
            new ActionSignal(@"^FireAtTarget", 3.5f, 0f, AttackShape.Projectile, MovementMode.Unknown, 3f, "spawns-projectile", "targeted"),
            new ActionSignal(@"^SpawnObjectFromGlobalPool|^SpawnFromPool|^SpawnObjectFromPool", 1.5f, 0f, AttackShape.Summon, MovementMode.Unknown, 2f, "spawns-object"),
            new ActionSignal(@"^CanHeroBeGrabbed|^SetIsGrabbing", 3f, 0f, AttackShape.Direct, MovementMode.Unknown, 3f, "grab"),

            // Toggling a collider is how Silksong actually arms a hitbox. Weak on its own
            // because it also toggles terrain and detection colliders; DiscoveryEngine
            // upgrades it when the target is a child that really damages the hero.
            new ActionSignal(@"^SetCollider|^ActivateBoxCollider2D", 0.7f, 0f, AttackShape.Melee, MovementMode.Unknown, 0.5f, "toggles-collider"),
            new ActionSignal(@"^ActivateGameObject|^ActivateAllChildren", 0.3f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0.2f, "toggles-object"),

            // A state that moves and listens for a hit is a ram. This is the Drill, the
            // Roll and the Slam - the most common attack shape in the game and the one the
            // first table missed entirely.
            new ActionSignal(@"^Collision2dEvent|^CollisionEvent", 2f, 0.5f, AttackShape.Melee, MovementMode.Dash, 2f, "collision-driven"),
            new ActionSignal(@"^DoCameraShake|^CameraShake|Rumble", 0.8f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0.2f, "impactful"),
            new ActionSignal(@"^PlayParticleEmitter|^StartRoarEmitter", 0.4f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0.2f, "effects"),

            // ---- Movement -----------------------------------------------------------
            // Strong hints so they beat FaceObject regardless of action ordering.
            new ActionSignal(@"^ChaseObject", 0f, 3.5f, AttackShape.Unknown, MovementMode.Chase, 3f, "targeted"),
            new ActionSignal(@"^DistanceFly", 0f, 3.5f, AttackShape.Unknown, MovementMode.Fly, 3f, "airborne", "targeted"),
            new ActionSignal(@"^DirectlyFlyTo|^FlyTowards|^IdleBuzz", 0f, 2f, AttackShape.Unknown, MovementMode.Fly, 2.5f, "airborne"),
            new ActionSignal(@"^DistanceWalk", 0f, 3.5f, AttackShape.Unknown, MovementMode.Walk, 3f),
            new ActionSignal(@"^ControllerMove|^ControllerSimpleMove", 0f, 3f, AttackShape.Unknown, MovementMode.Walk, 2.5f),
            new ActionSignal(@"^ControllerJump|^JumpTranslateTo", 0f, 2.5f, AttackShape.Unknown, MovementMode.Jump, 2.5f, "airborne"),
            new ActionSignal(@"^SetVelocity|^SetVelocity2d|^SetVelocityByScale", 0f, 2.2f, AttackShape.Unknown, MovementMode.Unknown, 1f, "sets-velocity"),
            new ActionSignal(@"^AccelerateTo|^AccelerateVelocity", 0f, 2f, AttackShape.Unknown, MovementMode.Dash, 1.5f, "sets-velocity"),
            new ActionSignal(@"^AddForce", 0f, 2f, AttackShape.Unknown, MovementMode.Dash, 1.5f),
            new ActionSignal(@"^Decelerate", 0f, 0.5f, AttackShape.Unknown, MovementMode.Unknown, 0.3f, "committing"),
            new ActionSignal(@"^AnimatePositionTo|^AnimateXPositionTo|^AnimateYPositionTo|^AnimateRigidBody2DPositionTo", 0f, 2f, AttackShape.Unknown, MovementMode.Unknown, 1f),
            new ActionSignal(@"^SetPositionToObject|^SetPosition$|^SetTransformPosition", 0f, 1.2f, AttackShape.Unknown, MovementMode.Teleport, 1.5f),
            new ActionSignal(@"^ConstrainMovement|^ClampPosition|^ClampVelocity", 0f, 0.8f, AttackShape.Unknown, MovementMode.Unknown, 0.3f),

            // Locomotion driven by a C# component rather than by FSM velocity actions.
            // Walker and Crawler are MonoBehaviours; the FSM only switches them on and off,
            // so these states contain no velocity action at all and scored nothing. This is
            // why MossBone Crawler came back with zero movements despite crawling around.
            new ActionSignal(@"^StartWalker|^StopWalker|^WalkLeftRight|^Walker|^SetWalker", 0f, 2.5f, AttackShape.Unknown, MovementMode.Walk, 2.5f, "component-driven"),
            new ActionSignal(@"^StartCrawler|^StopCrawler|^Crawler", 0f, 2.5f, AttackShape.Unknown, MovementMode.Walk, 2.5f, "component-driven"),
            new ActionSignal(@"^ObjectJitter|^Jitter", 0f, 1f, AttackShape.Unknown, MovementMode.Idle, 0.8f),

            // A trigger listener on a moving enemy is the same ram shape as Collision2dEvent.
            new ActionSignal(@"^Trigger2dEvent|^TriggerEvent", 1.5f, 0.3f, AttackShape.Melee, MovementMode.Unknown, 1.2f, "collision-driven"),
            new ActionSignal(@"^SetInvincible|^SetIsInvincible", 0f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0f, "defensive"),

            // Facing is the single most over-weighted signal in the first table. It is a
            // hint about *orientation*, present in attacks and patrols alike, and it must
            // never be enough to classify a state on its own.
            new ActionSignal(@"^FaceObject|^FaceDirection|^FaceAngle|^LookAt|^FlipScale", 0f, 0.35f, AttackShape.Unknown, MovementMode.Turn, 0.4f, "faces-target"),

            // ---- Perception and branching: at or below zero -------------------------
            // These are what a state does to decide, not to act. They are everywhere, so
            // any positive weight here leaks into every verdict.
            new ActionSignal(@"^CheckAlertRange|^CheckCanSeeHero|^CheckHeroPerformanceRegion|^CheckTouching", 0f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0f, "perception"),
            new ActionSignal(@"^CheckCollisionSide|^CheckCollisionSideEnter|^GetCollisionInfo", 0f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0f, "perception"),
            new ActionSignal(@"^GetVelocity|^GetPosition|^GetAngularVelocity|^GetScale", 0f, -1f, AttackShape.Unknown, MovementMode.Unknown, 0f),
            new ActionSignal(@"^SendRandomEvent|^RandomEvent", -1.5f, -1.5f, AttackShape.Unknown, MovementMode.Unknown, 0f, "decision"),
            new ActionSignal(@"^FloatCompare|^IntCompare|^BoolTest|^FloatTest|^BoolAllTrue|^BoolTestMulti|^FloatOperator|^FloatAdd|^FloatMultiply|^SetFloatValue|^GetDistance", 0f, -0.4f, AttackShape.Unknown, MovementMode.Unknown, 0f, "branching"),
            new ActionSignal(@"^SendEventByName|^SendMessage|^NextFrameEvent|^FindChild|^FindNamedChild|^SendEventToRegister", 0f, -0.3f, AttackShape.Unknown, MovementMode.Unknown, 0f),

            // Plumbing and perception found by auditing 124 unrecognised action types in a
            // second survey. Unweighted actions are not harmless: they leave a state's score
            // to be decided by whichever one or two actions the table happened to know,
            // which is how a patrol state ended up scoring below an idle.
            new ActionSignal(@"^SetBoolValue|^SetFsmBool|^SetFsmFloat|^SetFsmInt|^GetOwner|^GameObjectIsNull|^StringCompare|^PlayerDataBoolTest|^CallMethodProper|^RandomFloat|^Tk2dSpriteGetId|^ArrayGet", 0f, -0.2f, AttackShape.Unknown, MovementMode.Unknown, 0f, "branching"),
            new ActionSignal(@"^CheckIsCharacterGrounded|^RayCast2d|^RayCast|^CreateNoise|^EnemySingControl", 0f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0f, "perception"),
            new ActionSignal(@"^SetMeshRenderer|^SetLayer|^SetParent|^SetRotation|^Tk2dSprite|^Tk2dPlayFrame|^StopParticleEmitter|^SetScale|^FlipScale", 0f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0f, "presentation"),

            // ---- Lifecycle: strongly negative ---------------------------------------
            // SimulateDeath is unambiguous, and the first table let a death state classify
            // as confident movement because nothing here outweighed its velocity actions.
            new ActionSignal(@"^SimulateDeath|^SetIsKinematic2d|^RecycleSelf|^DestroySelf|^Deactivate", -3f, -3f, AttackShape.Unknown, MovementMode.Unknown, 0f, "lifecycle"),
            new ActionSignal(@"^CheckAlertRangeByName|^SetAlertRange", -1f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0f, "perception", "dormant"),

            // ---- Presentation: no classification weight -----------------------------
            new ActionSignal(@"^Wait$|^WaitRandom", 0f, 0f, AttackShape.Unknown, MovementMode.Idle, 0.2f, "waits"),
            new ActionSignal(@"^Tk2dPlayAnimation|^PlayAnimation|^Tk2dWatchAnimationEvents", 0f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0f, "animates"),
            new ActionSignal(@"Audio|^PlayAudioEvent|^SetAudioSource", 0f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0f, "audio"),
        };

        /// <summary>
        /// State-name evidence, applied on top of action evidence at a lower weight.
        /// </summary>
        /// <remarks>
        /// Team Cherry names states honestly - "Attack Antic", "Slam", "Roll", "Idle" - so
        /// this is real signal. It stays below action evidence because they also name them
        /// "Bonk?" and "Slam L?", and a trailing question mark reliably marks a branch
        /// state rather than a behaviour.
        /// </remarks>
        public static readonly IReadOnlyList<ActionSignal> StateNameSignals = new List<ActionSignal>
        {
            new ActionSignal(@"(?i)\b(attack|strike|swipe|slash|stab|bite|slam|smash|thump|drill|bonk)\b", 1.8f, 0f, AttackShape.Melee, MovementMode.Unknown, 1.5f),
            new ActionSignal(@"(?i)\b(shoot|fire|throw|spit|launch|volley)\b", 1.8f, 0f, AttackShape.Projectile, MovementMode.Unknown, 2f),
            new ActionSignal(@"(?i)\b(summon|spawn)\b", 1.2f, 0f, AttackShape.Summon, MovementMode.Unknown, 2f),
            new ActionSignal(@"(?i)\b(antic|telegraph|windup|wind up|charge up|prepare|tell)\b", 1f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0.5f, "telegraph"),
            new ActionSignal(@"(?i)\b(recover|recovery|cooldown|end)\b", 0f, 0f, AttackShape.Unknown, MovementMode.Unknown, 0.3f, "recovery"),
            new ActionSignal(@"(?i)\b(walk|patrol|roam|wander)\b", 0f, 1.5f, AttackShape.Unknown, MovementMode.Walk, 1.5f),
            new ActionSignal(@"(?i)\b(fly|hover|float|drift|buzz)\b", 0f, 1.5f, AttackShape.Unknown, MovementMode.Fly, 1.5f, "airborne"),
            new ActionSignal(@"(?i)\b(chase|pursue|follow|track|get above)\b", 0f, 1.5f, AttackShape.Unknown, MovementMode.Chase, 1.8f, "targeted"),
            new ActionSignal(@"(?i)\b(roll|dash|lunge|charge|rush|dive|ram)\b", 1f, 1.5f, AttackShape.Melee, MovementMode.Dash, 1.8f),
            new ActionSignal(@"(?i)\b(jump|leap|hop)\b", 0f, 1.5f, AttackShape.Unknown, MovementMode.Jump, 1.5f, "airborne"),
            new ActionSignal(@"(?i)\b(idle|wait|pause|rest)\b", 0f, 1.2f, AttackShape.Unknown, MovementMode.Idle, 1.5f),
            new ActionSignal(@"(?i)\b(teleport|blink|warp|vanish)\b", 0f, 1.5f, AttackShape.Unknown, MovementMode.Teleport, 1.8f),
            new ActionSignal(@"(?i)\b(turn|flip|face)\b", 0f, 0.8f, AttackShape.Unknown, MovementMode.Turn, 0.8f),
            new ActionSignal(@"(?i)\b(stun|stunned|recoil|knockback|flinch|interrupt)\b", 0f, 0.5f, AttackShape.Unknown, MovementMode.Displaced, 1f, "reactive"),
            new ActionSignal(@"(?i)\b(wall|collide|land|landed)\b", 0f, 0.5f, AttackShape.Unknown, MovementMode.Unknown, 0.3f, "reactive"),

            // Lifecycle. "Extract" is Team Cherry's despawn state and appears on most
            // Moss Grotto enemies; it classified as confident movement before this.
            new ActionSignal(@"(?i)\b(death|die|dead|dying|corpse|extract|despawn)\b", -3f, -3f, AttackShape.Unknown, MovementMode.Unknown, 0f, "lifecycle"),
            new ActionSignal(@"(?i)\b(init|initialise|initialize|dormant|sleep|asleep|caged|inert|wake|awake|startle|alert)\b", -2f, -2f, AttackShape.Unknown, MovementMode.Unknown, 0f, "lifecycle"),

            // A trailing '?' is Team Cherry's convention for a branch state - "Bonk?",
            // "Slam L?", "Turn?". They were the hottest unclassified states in the survey
            // and they are decisions, not behaviours.
            new ActionSignal(@"\?\s*$", -1.5f, -1.5f, AttackShape.Unknown, MovementMode.Unknown, 0f, "decision", "branch-state"),
        };
    }
}
