# SarnautCore client

The SarnautCore game client uses Godot 4.7.2 .NET and C#. C++ GDExtensions are reserved for measured hot paths. The Lua addon interface will come later.

The current milestone includes a development hub, the session shell (login, character select, character create), an Asset Viewer, and a 3D Zone Walkabout backed by baked native content.

## Development quickstart

Install Godot 4.7.2 stable .NET and a .NET 10 SDK. Then run:

```powershell
dotnet build SarnautCore.sln
godot --editor --path .
```

The default scene opens a small development hub. Choose **Asset Viewer** to inspect mounted native Godot content, **Zone Walkabout** for the offline walkabout, or **Play** for the session shell.

### The session shell

**Play** opens login, then character select, then character create — three view-model and scene pairs backed by the `Session` autoload, which carries the account, its token and the chosen character across scene changes.

Everything decidable lives in `src/SarnautCore.Shell`, a plain-C# assembly with no Godot reference: the account client, the three view models, the character-name rule, and the screen-flow state machine. A Godot headless smoke needs the private baked content and libmsquic and cannot run in CI, so anything left in a `Control` subclass would ship untested. `tests/SarnautCore.Shell.Tests` covers all of it and runs on every push.

The screens talk to the account service over HTTP (`POST /v1/accounts`, `POST /v1/sessions`, the character routes, `POST /v1/tickets`, `GET /v1/chargen/options`; ADR 0030). Set `SARNAUT_AUTH_ADDRESS` to move it off `http://127.0.0.1:8083`.

The race and class list, the starting kit and the spawn come entirely from the server's chargen table (ADR 0032). There is no built-in option list and no fallback: a client that invented one would offer a race the server refuses to create.

Passwords and tokens are carried in `Secret`, whose every conversion returns `[redacted]`; reaching the characters requires `Reveal()`. That mirrors the server's rule (ADR 0030 §5) and is what keeps a credential out of a log by construction rather than by habit.

#### Interface theme

`gui/theme/custom` names the converted Allods widget theme. That file is MY.GAMES-derived, lives only in the gitignored `converted/` tree, and embeds absolute source paths in its metadata, so it is never committed. `UiTheme` loads it through the converted-scene loader — its font references are written against the converter's own root — and falls back to a theme built in code when the tree is absent. Godot itself also tries the raw file before any autoload runs and logs that it could not load it; that message is expected, and the fallback is what a fresh clone renders with.

### Zone Walkabout

The Boot menu defaults the zone field to `Inst_LeagueStart`, the classic League tutorial map. Choose **Zone Walkabout** to assemble its baked native terrain and statics.

Controls are named input actions in `project.godot`'s `[input]` section, so they are rebindable and correct on a non-QWERTY layout. The defaults:

| Action | Default | Meaning |
|---|---|---|
| `move_forward` / `move_back` / `move_left` / `move_right` | `W` `S` `A` `D` | move |
| `move_jump` | `Space` | jump while walking, rise while flying |
| `move_descend` | `Q`, `Ctrl` | descend while flying |
| `move_sprint` | `Shift` | fly faster |
| `move_toggle_fly` | `F` | toggle walk/fly mode |
| `camera_orbit` / `camera_zoom_in` / `camera_zoom_out` | right mouse, wheel | orbit and zoom a preview camera; right mouse also recaptures the cursor in the zone |
| `target_nearest` | `Tab` | cycle targets outwards from the player |
| `target_click` | left mouse | target the entity under the cursor |
| `interact` | `E` | interact |
| `journal` | `J` | journal |
| `inventory` | `I` | inventory |
| `ui_cancel` | `Esc` | release the mouse; press it again to leave the zone |

`terrain-manifest.json` is the sole terrain inventory. It names each baked Godot scene and its world origin. The runtime rejects a missing, partial, or incompatible aggregate; there is no converted or flat terrain fallback.

Zone lighting runs two retail-faithful models split by render layer (`DynamicEntityLighting`). Statics whose geometry authors `vertexBakedLight` carry their offline bake — lightvrt vertex colors on placed objects, the two-term lightmap combine on terrain — rendered unshaded, and are demoted off the runtime-lit receiver layer so no runtime light can double-light them. Dynamic entities and unbaked props stay on the receiver layer and get the dynamic model: the zone's authored ambient at its 1x base, the authored sun, the map's placed `client.Scene.LightComponent` point lights (converted to `X_Y_MapRegion.xdb.lights.json` companions, colored with the zone's `PointLightColor`, culled to the receiver layer), and a per-character `SampledEntityLight` that reads the terrain lightmap under the entity (`BakedLightProbe`) and carries the baked combine's surplus — so characters go warm amber inside torch-lit halls and cool blue in astral shade. The League tutorial ships zero placed lights (its Maya sources never shipped), which makes the sampled term the zone's only authored local-light record.

Walkabout loads the selected native character scene and crossfades between `idle` and `run` from controller movement state. The camera follows from a third-person spring arm. Offline NPCs come from `character-placements.json`, which names 24 canonical character keys and their baked world transforms. Replicated entities resolve the same native character manifest; an unknown online content id becomes a labelled capsule.

### Online walkabout

Entering the world is now part of the session shell rather than a toggle on the hub: choose a character and press **Enter the world**. Character select mints the opaque single-use shard ticket, and the zone scene presents it in `EnterZoneRequest` (ADR 0030 §2). The walkabout joins the chargen option's spawn zone, sends the controller's world-space movement at 20 Hz, and renders authoritative snapshots with a 125 ms interpolation delay. The spawn the server answers with is authoritative and the client snaps to it. **Zone Walkabout** on the hub is the offline controller, with no shard involved.

#### Server entities

The shard decides what exists. Online, `ZoneLoader` counts the map's authored mob placements but does not draw them (`spawn_npc_visuals` is off), because a placement is where a mob *may* spawn and not proof that one is there; the offline walkabout turns it back on because there is no shard to ask. Every replicated entity is one `NetworkEntityVisual` under `NetworkEntities`, carrying its model, an `Area3D` on the entity physics layer for picking, a billboard nameplate and a health bar. `ZoneNetworkLoop.Entities` is the registry: it answers *entity by id* and, through `TryTargetAtScreenPoint`, *entity under the cursor*. `Tab` cycles targets outwards from the player and wraps.

The online walkabout also mounts the gameplay HUD: target frame, one-slot ability bar, pooled floating damage, death feedback, loot, bags, quest log/tracker and quest dialogue. `1` casts, `E` interacts, `I` opens bags and `J` opens the quest log. One focus owner arbitrates the pointer and windows: `Esc` closes the top window, then releases a captured pointer, then leaves walkabout; right-click recaptures it. The widgets use converted Ingame forms when present and code-built frames when `converted/` is absent. Their addon-facing model and event contract is in [`docs/gameplay-hud-addon-surface.md`](docs/gameplay-hud-addon-surface.md).

Nameplates come from `name_key` and never from a display string on the wire (ADR 0007). A resolved locale entry is shown unchanged. On a locale miss, file-shaped keys are slugged, while classic `Creatures/<family>/Instances/...` keys fall back to the creature family. For example, `Rat1_1_Name.txt` reads `Rat  (2)` and an internal corridor-zombie resource path reads `Zombie Warrior  (2)`.

`EntitySnapshot.content_id` binds to a native character scene through `characters/manifest.json` in the private baked content. Unknown ids render as labelled capsules at their authoritative positions. Capsules remain useful for asset-free tests, but the visual gate requires native scenes for every replicated player and creature.

Set `SARNAUT_SERVER_ADDRESS` before starting Godot to change the endpoint default. Online mode accepts the shard's ephemeral self-signed certificate for local development. Production certificate validation remains future work.

`pack_id` is a gate rather than a version banner (ADR 0029): a shard with a pack refuses a client that names a different one, and refuses a client that names none unless it was started with `content.allow_unverified_pack`. Point `SARNAUT_CONTENT_PACK` at the pack directory and the client reads `pack_id` out of its `manifest.json`, the same way the shard does; `SARNAUT_CONTENT_PACK_ID` states it outright. Neither is required — an empty id is what a client with no content has, and the shard decides whether that is welcome.

The networking assembly uses `System.Net.Quic`, backed by MsQuic on Windows 11 and Windows Server 2022 or later. .NET 10 exposes QUIC streams but not QUIC datagram send or receive calls. SarnautCore therefore uses SAR-19's ordered QUIC-stream fallback for `ClientMoveIntent` and `SnapshotBatch`. The stream framing is a 4-byte big-endian payload length followed by protobuf bytes. The copied protocol files under `src/SarnautCore.Network/Proto` match the server's `proto/sarnaut/v1` wire definitions and add only the C# namespace option.

Client-side prediction is intentionally left behind the `WalkaboutController.NetworkControlled` seam. SAR-20 displays interpolated authoritative state without predicting ahead of the server.

### Mount baked content

[ADR 0040](https://github.com/SarnautCore/docs/blob/main/adr/0040-materialized-native-content-architecture.md) makes content materialization an offline maintainer pipeline. The runtime does not convert source files, users do not run a local bake, and `ao-godot-converter` is private build machinery rather than a product component.

Maintainers mount an existing private content workspace into a development checkout with:

```powershell
./scripts/mount-assets.ps1 -AssetRoot E:\SarnautCore\assets `
    -ContentRoot E:\SarnautCore\content-staging\league-slice
```

The script mounts `content/league-slice` from the exact `-ContentRoot` path and rejects a normal directory or a junction aimed elsewhere. Omit the parameter only for the local `content-staging/league-slice` default. A compiled-only gate must pass its compiler output explicitly. Baked product content is stored privately in Perforce `//content/main`; source inputs remain separate in `//assets/main`. A public code checkout intentionally contains neither tree.

### Command-line checks

```powershell
dotnet build SarnautCore.sln
dotnet test SarnautCore.sln
./scripts/test-visual-gate-diagnostics.ps1
./scripts/visual-gate.ps1 -Godot <path to godot_console> `
    -CompiledContentRoot E:\SarnautCore\.tmp\compiled-native\league-slice
godot_console --headless --import --path .
godot_console --headless --path . --scene res://tests/asset_viewer_smoke.tscn
godot_console --headless --path . --scene res://tests/zone_walkabout_smoke.tscn
godot_console --headless --path . --scene res://tests/entity_binding_smoke.tscn
godot_console --path . --scene res://tests/directional_lighting_probe.tscn
dotnet run --project tools/SarnautCore.EntityBench -c Release -- --entities 288
```

The Asset Viewer smoke scene expects the private native-content mount and checks one Godot scene, one Godot resource, path confinement, and unsupported-file rejection. The Zone Walkabout smoke scene expects the private League-slice content mount and prints the native terrain, static, and character placement counts. These files are intentionally absent from the public Git repository.

The directional-lighting probe needs the compiled native League content and a Forward+ display. It compares the production scene with shadows on, shadows off and the sun off, then fails on crushed blacks, washed highlights or missing rendered shadows. Set `SARNAUT_LIGHTING_PROBE_PREFIX` to an absolute path to keep its three PNG frames.

The visual gate now has 17 probes: the original 16 plus standing validation of authored LODs for all 40 native character identities. It also pins the 53-case animation census. The zone-presentation probe requires the compiled native `.scn`, exact League environment and sun values, the three-part camera-centered sky, and the baked ambient/direct probe colors. Every probe must print `result=PASS`; the gate rejects leaks, unexpected `ERROR` diagnostics, and reduced coverage from either output stream. Captures taken at animated p4 and p5 moments are comparison evidence. They do not need to be byte-identical or pixel-identical unless the capture fixes animation time and every other render input. Compare composition and bounded image metrics when the frames are not deterministic.

The Entity Binding smoke scene needs neither: it binds snapshots to visuals, picks one with a ray and retires one, and prints whether it ran on native models or on capsules. Run it both ways. `SarnautCore.EntityBench` compares the entity update against the pre-registry loop; see `tools/SarnautCore.EntityBench/RESULTS.md`.

For an asset-free end-to-end network check, keep the `server` repository beside this one and use the session smoke below. Adding `-EntityProbe` runs `res://tests/zone_online_probe.tscn`: it signs in through the same view models the screens use, enters the live shard, and then checks what the client actually drew. It requires one visual per replicated entity, a controller at the shard's authoritative position, a nameplate and health bar, and matching `Tab` and screen-point targeting.

```powershell
./scripts/m2-session-smoke.ps1 -EntityProbe -Godot <path to godot_console>
```

The script starts the production shard with a temporary synthetic content fixture and runs `tools/SarnautCore.NetSmoke`. It passes only after the client joins, sends movement, and observes its authoritative position advance. The smoke is kept as a local cross-repository check because the two repositories publish independently and Linux runners require a separately installed `libmsquic`. The pure C# protocol, session and interpolation tests run in this repository's CI.

For the client session slice, register, create a character, select it, enter the zone at the character's own spawn, then sign in again and land on the saved position. Start `infra/compose` and run:

```powershell
./scripts/m2-session-smoke.ps1 -ServerRepo ../server
```

It boots auth and a shard, drives the client's own view models rather than a test double, and fails if the shard's spawn disagrees with `GET /v1/chargen/options` or if any output carries a password, an email address or a token.

Add `-GameplayProbe` to drive the gameplay slice through the gameplay view models: target a live mob, cast to a killing blow, populate and take its loot offer, and verify the authoritative inventory update. Quest state, objective counters, refusals, and turn-in rewards use the server's `QuestStateUpdate` payload; reliable spawn/despawn events own entity lifetime while snapshots update known entities.

The server's `scripts/m2-slice-smoke.ps1` is the canonical complete headless chain for login, character creation, movement, combat, loot, quest turn-in, disconnect, and state verification after reconnect. This client script remains the transport, session-view-model, and Godot rendering proof; it does not duplicate the server's gameplay driver. Follow the [M2 Godot demo runbook](https://github.com/SarnautCore/docs/blob/main/specs/world/m2-demo-runbook.md) for the real private pack and human walkthrough.

## About SarnautCore

This repository is part of SarnautCore, a fan-driven, non-commercial, open-source recreation kit for Allods Online.

The project charter and the architecture decision records live in [SarnautCore/docs](https://github.com/SarnautCore/docs). Read those before opening a pull request here. ADR 0040 governs the public-code/private-content split and the offline bake architecture.

## Clean-room posture

SarnautCore is built clean-room. Engine and server code are public; every derived content byte stays in the project's private distribution. The public client repository contains no runtime converter and no original-format reader.

## License

AGPL-3.0. See [LICENSE](LICENSE).
