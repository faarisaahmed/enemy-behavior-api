# Enemy Behavior API

> **⚠️ In development — not ready to build a mod on.**
>
> The discovery engine works and is measured against real play sessions. The **Observe**
> tier is tested. **Influence and Override have never once executed in game** - see
> [Status](#status) before depending on anything here. The schema is **not frozen**;
> expect breaking changes.
>
> What would help most right now: run the survey plugin and send back the JSON it writes.
> That is what the classifier is calibrated from, and more enemies means a better one.

A BepInEx library for Hollow Knight: Silksong that reads any enemy's PlayMaker FSMs by
reflection, works out which states are attacks and which are movement, and hands that to
other mods as a structured schema they can observe, tune, or drive.

No per-enemy list. Point it at a GameObject and it tells you what that enemy can do.


## Status

Measured across two recorded sessions: 18 enemy kinds, 16 scenes, 2 bosses (Moss Grotto,
The Marrow, Bell Beasts).

| Area | State |
|---|---|
| Enemy discovery, FSM scanning, state/action/parameter extraction | **Works** - measured |
| Per-instance handles, authority arbitration, event stream | **Works** - measured |
| Harmony gate on `Fsm.SwitchState` | **Works** - no crashes across 18 enemy kinds |
| Attack/movement classification | **Usable, imperfect** - calibrated against real sessions |
| Decision-state detection (for Override) | **Works** - finds branch states on bosses |
| Speed extraction | **Partial** - 59/152 movements resolve a speed |
| Damage extraction | **Poor** - 48/53 attacks still report 0 |
| **Influence tier** (parameter writes, revert-on-dispose) | **NEVER EXECUTED** |
| **Override tier** (`Fire`, transition veto, passlist) | **NEVER EXECUTED** |

The two untested tiers are untested because every session so far ran Observe-only. The
risk worth naming: if the Override passlist is wrong, a mod holding Override could stop an
enemy's death transition and make it effectively immortal. Do not ship a mod built on
Override until that path has been exercised.

### Known-wrong things

- `DamageAmount` is 0 for most attacks. Silksong keeps hero damage on persistent
  `DamageHero` components rather than in FSM actions, and resolving which child a state
  arms is still unreliable.
- Contact-damage enemies (Bone Roller) report **0 attacks**, correctly by the current
  definition - they have no attack *state*, they simply hurt to touch. The schema has no
  way to express that yet.
- Some enemies move via C# `Walker`/`Crawler` components that FSMs only start and stop, so
  there are no velocity actions to read. Handled by signal, not yet verified in game.
- Classification is calibrated on early-game enemies. Late-game and Act 3 enemies are
  entirely unmeasured.

## Helping

The single most useful contribution is a survey file.

1. Drop both DLLs in `BepInEx/plugins/` (see [Install](#install))
2. Play normally - the survey records by itself and writes every 30 seconds
3. Send the JSON from `BepInEx/plugins/EnemyBehaviorApi.Survey/surveys/`

It is strictly read-only: it never writes to an enemy, so nothing behaves differently
while it runs. Areas and bosses not listed under [Status](#status) are the most valuable.

If a classification looks obviously wrong for an enemy you know, say so - that judgement is
the ground truth the classifier gets tuned against.

## Install

Requires [BepInEx](https://github.com/BepInEx/BepInEx) for Silksong.

1. Download from [Releases](../../releases)
2. `EnemyBehaviorApi.dll` → `BepInEx/plugins/EnemyBehaviorApi/`
3. `EnemyBehaviorApi.Survey.dll` → `BepInEx/plugins/EnemyBehaviorApi.Survey/`
4. Launch. The survey starts on its own; `BepInEx/LogOutput.log` confirms it.

`EnemyBehaviorApi.Example.dll` is optional and **not needed to help with data**. It
demonstrates all three tiers, which means it writes to and drives enemies through paths
nothing has tested. It is off unless you set `EnableTierDemo = true`, and you should leave
it that way for now.

The survey ships as a separate assembly so "this cannot touch your enemies" is a property
of the binary rather than a promise in a readme - it contains no call to any write-capable
API method, which you can check yourself with any IL disassembler.

Config lands in `BepInEx/config/` after the first run. `VerboseLogging = true` on the API
is worth setting - without it, discovery messages go to `LogDebug`, which BepInEx's disk
log excludes by default.

## Why it works

Silksong builds enemy behaviour out of PlayMaker FSMs assembled from roughly 2,200 typed
action classes. Those class names describe mechanisms rather than creatures - `ChaseObject`,
`DistanceFly`, `SetDamageHero`, `SpawnObjectFromGlobalPool` - and the same few dozen recur
across every enemy in the game. So "what is this state doing?" is answerable by looking at
which actions it holds, without knowing anything about the enemy it belongs to.

Two facts about PlayMaker do most of the work:

- **`Fsm.SwitchState` is the only way a state change happens.** Transitions, global
  transitions, `SetState`, the `FINISHED` event - all of it funnels through that one
  method. A single Harmony patch there gives both the Observe tier's event stream and the
  Override tier's veto.
- **`FsmStateAction.Enabled` is public and settable, and action fields are plain
  reflection targets.** That is the Influence tier: change the numbers a designer typed in,
  without touching the enemy's decision-making.

## The three tiers

| Tier | Can | Exclusive? |
|---|---|---|
| **Observe** | Read the profile, subscribe to state changes | No |
| **Influence** | Tune speeds, timings, damage, branch weights | No |
| **Override** | Suppress the enemy's decisions and fire transitions | **Yes, per instance** |

Authority is scoped to one **enemy instance**, never to a kind of enemy and never globally.
A combat-overhaul mod driving the boss and a pet mod charming one husk in the same room
are not in conflict, and nothing in the API makes them think they are.

Override is the only exclusive claim, because two mods firing transitions into one FSM
produces behaviour neither asked for. Asking for Override on an enemy someone else holds
gets you Influence instead of a refusal - check `handle.Tier` on the result. When the
incumbent releases, the claim passes to whoever was waiting.

### What Override actually suppresses

`OverridePolicy.SuppressDecisions` (the default) vetoes only transitions **leaving a
decision state** - the states holding a `SendRandomEvent` or fanning out to several
attacks. Everything else runs normally: attacks complete, recoveries play, hit reactions
fire, the enemy still dies when killed. It simply stops choosing, and waits for you.

That is usually what "take control" should mean. The enemy still looks and feels like
itself, because its animations, timings and interruptibility all live in states you are not
touching.

`OverridePolicy.SuppressAll` vetoes everything you did not fire. Total puppetry - and you
now own reacting to being hit, too.

Either way, a small passlist (death, stun, recoil, land, idle…) always gets through.
Without it, an Override holder makes the enemy immortal by accident: block every
transition and the death state never runs.

## Using it

```csharp
[BepInDependency("com.faaris.enemybehaviorapi")]
public class MyMod : BaseUnityPlugin
{
    void Awake()
    {
        EnemyBehavior.EnemyAppeared += enemy =>
        {
            // Keep the handle alive for as long as you want the events. Disposing it here
            // would unsubscribe before the enemy had done anything.
            var handle = EnemyBehavior.Claim(enemy, "com.example.mymod", AuthorityTier.Observe);
            _handles[enemy.InstanceId] = handle;

            foreach (var attack in handle.Profile.Actions)
                Logger.LogInfo($"{attack.DisplayName}: {attack.Shape}, {attack.DamageAmount} dmg");

            handle.StateChanged += (_, e) =>
            {
                if (e.Behavior is ActionDescriptor a)
                    Logger.LogInfo($"incoming: {a.DisplayName}");
            };
        };

        EnemyBehavior.EnemyGone += enemy => _handles.Remove(enemy.InstanceId);
    }
}
```

Influence - the enemy still decides, it just decides faster:

```csharp
var handle = EnemyBehavior.Claim(enemyGo, MyGuid, AuthorityTier.Influence);

foreach (var p in handle.Profile.Movements.SelectMany(m => m.Parameters)
                        .Where(p => p.Name.Contains("speed")))
{
    handle.TryGetParameter(p.Id, out float current);
    handle.TrySetParameter(p.Id, current * 1.5f);
}
```

Override - you decide:

```csharp
var handle = EnemyBehavior.Claim(enemyGo, MyGuid, AuthorityTier.Override);

var attack = handle.Profile.Actions
    .Where(a => a.TelegraphFor == null)      // skip wind-ups; fire the real entry point
    .OrderByDescending(a => a.DamageAmount)
    .First();

handle.Fire(attack.Id);
```

**Always dispose the handle.** Disposing reverts every parameter and action-enable that
handle wrote, releases Override, and unsubscribes. A handle left on an enemy that dies gets
cleaned up with the instance - but until then it holds Override against everyone else.

`Samples/ExampleMod` is a working mod exercising all three tiers; press F9 in game to
cycle.

## Schema

`ActionDescriptor` and `MovementDescriptor` are separate types, linked back to an
`EnemyProfile` by `EnemyId`, so a mod that only cares about attacks never filters a mixed
list. A state that is both - a charge that damages on contact - appears in both lists over
the same `StateRef`.

Every descriptor carries a `Confidence` from 0 to 1 and the raw `ActionTypes` the
classification was made from. Low confidence means the signals were close, which is honest:
those are the charge-attacks and the dash-with-a-hitbox that genuinely are both. Filter on
it rather than trusting every descriptor equally.

`SchemaVersion.Current` goes up on any schema change; `MinimumCompatible` goes up only on a
breaking one. Check `SchemaVersion.IsCompatible` at startup.

## Annotations

Reflection gets ordinary enemies right. Bosses are hand-built, spread across several FSMs,
and reliably confuse a generic classifier - so they get corrected in JSON rather than in
code. That is the whole point: the reflection engine stays general instead of accumulating
an if-chain of special cases.

Drop `*.json` in `BepInEx/plugins/EnemyBehaviorApi/annotations/`. Every file is loaded and
merged in filename order, so a mod can ship its own corrections without editing anyone
else's. Keys are the enemy id, then `path/FsmName/StateName`.

```json
{
  "version": 1,
  "enemies": {
    "Silk Boss": {
      "primaryFsm": "Control",
      "ignoreFsms": ["Damage Response", "Audio"],
      "decisionStates": ["Control/Choose Attack"],
      "states": {
        "Control/Lunge": {
          "classify": "both",
          "displayName": "Lunging Bite",
          "shape": "Melee",
          "mode": "Dash",
          "note": "Hitbox is on an always-active child, so there was no ActivateGameObject to find."
        },
        "Control/Flinch": { "classify": "none" }
      },
      "parameters": {
        "var:Control/Attack Cooldown": { "min": 0.2, "max": 6.0 }
      }
    }
  }
}
```

Every field is optional - a null leaves discovery's answer alone, so files stay small and
record only the disagreement. `"classify": "none"` removes a false positive;
`"classify": "attack"` on a state discovery ignored creates the descriptor from scratch.
See `annotations/example.json` for every field with commentary.

While writing annotations, `EnemyBehavior.ReloadAnnotations(dir)` rebuilds profiles without
a restart.

## Building

Needs the .NET SDK and a local Silksong install with BepInEx.

```bash
dotnet build EnemyBehaviorApi/EnemyBehaviorApi.csproj
```

Output is copied into `BepInEx/plugins/EnemyBehaviorApi/`. The game path defaults to a
macOS GOG install - point it somewhere else with a gitignored `LocalPaths.props` beside
`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <GamePath>C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight Silksong</GamePath>
  </PropertyGroup>
</Project>
```

## Layout

```
EnemyBehaviorApi/
  EnemyBehavior.cs        the public API - start here
  Plugin.cs               BepInEx entry point, wires everything together
  Schema/                 versioned types consumers bind against
  Discovery/              FSM scanning, the signal table, classification
  Annotations/            JSON corrections
  Authority/              tiers, handles, per-instance claims
  Runtime/                live enemy registry, the Harmony gate
Samples/SurveyMod/        read-only recorder - what to run to help
Samples/ExampleMod/       a working consumer, all three tiers (writes to enemies)
annotations/example.json  every annotation field, commented
docs/                     the action-class list this build was written against
```

## Known limits

- **Identity is name-based.** Enemy ids come from the prefab name with Unity's `(Clone)`
  and ` (1)` suffixes stripped. Two genuinely different enemies sharing a name need an
  annotation to tell apart.
- **`PrimaryFsm` is a heuristic** - whichever FSM holds the most classified states. Right
  for enemies with one `Control` FSM plus small helpers; bosses with several real behaviour
  FSMs need `primaryFsm` set.
- **Parameters address actions by index.** Stable within a build, not across one. Writes
  check the action's type name first, so a game update turns a moved action into a failed
  call rather than a corrupted enemy.
- **The classifier is calibrated against one session** (Moss Grotto: Bone Thumper,
  MossBone Fly, MossBone Crawler, MossBone Cocoon). It is measured, not guessed - but four
  enemy kinds is a small sample, and no boss is in it. Expect to keep adjusting
  `SignalTable`; `EnemyProfile.AllStates` carries the score behind every verdict so you can
  see exactly why something was missed.
- **A sustained attack's damage is armed one state earlier.** Discovery propagates a live
  hitbox one transition hop, which covers the usual wind-up-then-commit shape. An attack
  that stays armed across three or more states will read as movement after the second.
- **Annotations outrank the classifier and are stamped `Confidence = 1.0`.** A state key
  that does not exist is now rejected with a warning rather than silently inventing a
  behaviour, but a *wrong* annotation on a real state is still taken at face value.
