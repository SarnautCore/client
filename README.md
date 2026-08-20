# SarnautCore client

The SarnautCore game client uses Godot 4.7.2 .NET and C#. C++ GDExtensions are reserved for measured hot paths. The Lua addon interface will come later.

The current milestone includes a development hub, the session shell (login, character select, character create), an Asset Viewer, and a 3D Zone Walkabout for locally converted Allods assets.

## Development quickstart

Install Godot 4.7.2 stable .NET and a .NET 10 SDK. Then run:

```powershell
dotnet build SarnautCore.sln
godot --editor --path .
```

The default scene opens a small development hub. Choose **Asset Viewer** to browse supported files below `converted/`, **Walk a converted zone** for the offline walkabout, or **Play** for the session shell.

### The session shell

**Play** opens login, then character select, then character create — three view-model and scene pairs backed by the `Session` autoload, which carries the account, its token and the chosen character across scene changes.

Everything decidable lives in `src/SarnautCore.Shell`, a plain-C# assembly with no Godot reference: the account client, the three view models, the character-name rule, and the screen-flow state machine. A Godot headless smoke needs converted assets and libmsquic and cannot run in CI, so anything left in a `Control` subclass would ship untested. `tests/SarnautCore.Shell.Tests` covers all of it and runs on every push.

The screens talk to the account service over HTTP (`POST /v1/accounts`, `POST /v1/sessions`, the character routes, `POST /v1/tickets`, `GET /v1/chargen/options`; ADR 0030). Set `SARNAUT_AUTH_ADDRESS` to move it off `http://127.0.0.1:8083`.

The race and class list, the starting kit and the spawn come entirely from the server's chargen table (ADR 0032). There is no built-in option list and no fallback: a client that invented one would offer a race the server refuses to create.

Passwords and tokens are carried in `Secret`, whose every conversion returns `[redacted]`; reaching the characters requires `Reveal()`. That mirrors the server's rule (ADR 0030 §5) and is what keeps a credential out of a log by construction rather than by habit.

#### Interface theme

`gui/theme/custom` names the converted Allods widget theme. That file is MY.GAMES-derived, lives only in the gitignored `converted/` tree, and embeds absolute source paths in its metadata, so it is never committed. `UiTheme` loads it through the converted-scene loader — its font references are written against the converter's own root — and falls back to a theme built in code when the tree is absent. Godot itself also tries the raw file before any autoload runs and logs that it could not load it; that message is expected, and the fallback is what a fresh clone renders with.

### Zone Walkabout

The Boot menu defaults the zone field to `Inst_LeagueStart`, the classic League tutorial map. Choose **Zone Walkabout** to assemble the map from converted terrain OBJ files and MapRegion placement JSON.

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

The loader currently applies the dominant converted terrain-layer texture to each terrain tile. Splat and light maps are present in the conversion, but full layered terrain materials are pending converter support.

Walkabout loads the classic Elf male skinned scene for the local player and crossfades between its `idle` and `run` clips from controller movement state. The camera follows from a third-person spring arm. Server-object placements resolve `MobWorld` and spawn-table references into the zone's converted character or creature scene, then play `idle`; NPC scene loading keeps locomotion plus attack, hit and death families while stripping unrelated emotes. A missing or incomplete converted model becomes a colored capsule and is counted in `NpcPlaceholderCount` and `NpcModelFailures`.

### Online walkabout

Entering the world is now part of the session shell rather than a toggle on the hub: choose a character and press **Enter the world**. Character select mints the opaque single-use shard ticket, and the zone scene presents it in `EnterZoneRequest` (ADR 0030 §2). The walkabout joins the chargen option's spawn zone, sends the controller's world-space movement at 20 Hz, and renders authoritative snapshots with a 125 ms interpolation delay. The spawn the server answers with is authoritative and the client snaps to it. **Walk a converted zone** on the hub is the offline controller, unchanged and with no shard involved.

#### Server entities

The shard decides what exists. Online, `ZoneLoader` counts the map's authored mob placements but does not draw them (`spawn_npc_visuals` is off), because a placement is where a mob *may* spawn and not proof that one is there; the offline walkabout turns it back on because there is no shard to ask. Every replicated entity is one `NetworkEntityVisual` under `NetworkEntities`, carrying its model, an `Area3D` on the entity physics layer for picking, a billboard nameplate and a health bar. `ZoneNetworkLoop.Entities` is the registry: it answers *entity by id* and, through `TryTargetAtScreenPoint`, *entity under the cursor*. `Tab` cycles targets outwards from the player and wraps.

The online walkabout also mounts the gameplay HUD: target frame, one-slot ability bar, pooled floating damage, death feedback, loot, bags, quest log/tracker and quest dialogue. `1` casts, `E` interacts, `I` opens bags and `J` opens the quest log. One focus owner arbitrates the pointer and windows: `Esc` closes the top window, then releases a captured pointer, then leaves walkabout; right-click recaptures it. The widgets use converted Ingame forms when present and code-built frames when `converted/` is absent. Their addon-facing model and event contract is in [`docs/gameplay-hud-addon-surface.md`](docs/gameplay-hud-addon-surface.md).

Nameplates come from `name_key` and never from the wire (ADR 0007). When there is no locale string the key is slugged — `Rat1_1_Name.txt` reads `Rat  (2)` — so a nameplate is always readable and never a raw key.

`EntitySnapshot.content_id` binds to a converted model through `converted/assets/<ruleset>/entity_models.json`, a manifest derived from extracted content that lives with the converted assets and is never committed here. Write it with:

```powershell
./scripts/build-entity-models.ps1 -DataRepo ..\data
```

Without the manifest, or without `converted/` at all, every entity renders as a labelled capsule at its authoritative position. That is a supported way to run the client, and it is what CI builds.

Set `SARNAUT_SERVER_ADDRESS` before starting Godot to change the endpoint default. Online mode accepts the shard's ephemeral self-signed certificate for local development. Production certificate validation remains future work.

`pack_id` is a gate rather than a version banner (ADR 0029): a shard with a pack refuses a client that names a different one, and refuses a client that names none unless it was started with `content.allow_unverified_pack`. Point `SARNAUT_CONTENT_PACK` at the pack directory and the client reads `pack_id` out of its `manifest.json`, the same way the shard does; `SARNAUT_CONTENT_PACK_ID` states it outright. Neither is required — an empty id is what a client with no content has, and the shard decides whether that is welcome.

The networking assembly uses `System.Net.Quic`, backed by MsQuic on Windows 11 and Windows Server 2022 or later. .NET 10 exposes QUIC streams but not QUIC datagram send or receive calls. SarnautCore therefore uses SAR-19's ordered QUIC-stream fallback for `ClientMoveIntent` and `SnapshotBatch`. The stream framing is a 4-byte big-endian payload length followed by protobuf bytes. The copied protocol files under `src/SarnautCore.Network/Proto` match the server's `proto/sarnaut/v1` wire definitions and add only the C# namespace option.

Client-side prediction is intentionally left behind the `WalkaboutController.NetworkControlled` seam. SAR-20 displays interpolated authoritative state without predicting ahead of the server.

### Convert local assets

`converted/` is the user-local mount point and Git ignores its contents. Point `ao-godot-converter` at that directory:

```powershell
Set-Location E:\allods\Dev\ao-godot-converter
cargo run --release -- convert --version 14.1 --output E:\SarnautCore\client\converted
```

Replace `14.1` with the converter profile for your own game installation. Return to this repository and click **Refresh converted/** in the viewer after conversion. The tree includes `.png`, `.tres`, `.tscn`, and `.skmesh` files. Images open in a fitted 2D preview. Mesh resources, 3D scenes, and converter meshes open in an orbitable 3D viewport. Drag with the left mouse button to orbit and use the wheel to zoom.

The client contains the converter's C# runtime resource classes and skinned-mesh loader. The loader path matches the path written into converted scenes.

### Command-line checks

```powershell
dotnet build SarnautCore.sln
dotnet test SarnautCore.sln
godot_console --headless --import --path .
godot_console --headless --path . --scene res://tests/asset_viewer_smoke.tscn
godot_console --headless --path . --scene res://tests/zone_walkabout_smoke.tscn
godot_console --headless --path . --scene res://tests/entity_binding_smoke.tscn
dotnet run --project tools/SarnautCore.EntityBench -c Release -- --entities 288
```

The Asset Viewer smoke scene expects local samples under `converted/samples/`. The Zone Walkabout smoke scene expects the classic conversion below `converted/assets/classic-1.1/` and prints the imported terrain and placement counts. These files are intentionally absent from Git because converted game assets must remain local.

The Entity Binding smoke scene needs neither: it binds snapshots to visuals, picks one with a ray and retires one, and prints whether it ran on converted models or on capsules. Run it both ways. `SarnautCore.EntityBench` compares the entity update against the pre-registry loop; see `tools/SarnautCore.EntityBench/RESULTS.md`.

For an asset-free end-to-end network check, keep the `server` repository beside this one and run:

```powershell
../server/scripts/sar20-client-smoke.ps1 -ClientRepository .
```

For the same rig with the real zone scene on top of it, adding `-EntityProbe` runs `res://tests/zone_online_probe.tscn`: it signs in through the same view models the screens use, enters the live shard, and then checks what the client actually drew — one visual per replicated entity, the controller standing where the shard says, a nameplate and a health bar on each, and `Tab` and a screen-point pick agreeing on the same entity id.

```powershell
./scripts/m2-session-smoke.ps1 -EntityProbe -Godot <path to godot_console>
```

The script starts the production shard with a temporary synthetic content fixture and runs `tools/SarnautCore.NetSmoke`. It passes only after the client joins, sends movement, and observes its authoritative position advance. The smoke is kept as a local cross-repository check because the two repositories publish independently and Linux runners require a separately installed `libmsquic`. The pure C# protocol, session and interpolation tests run in this repository's CI.

For the whole session slice — register, create a character, select it, enter the zone at the character's own spawn, then sign in again and land on the saved position — start `infra/compose` and run:

```powershell
./scripts/m2-session-smoke.ps1 -ServerRepo ../server
```

It boots auth and a shard, drives the client's own view models rather than a test double, and fails if the shard's spawn disagrees with `GET /v1/chargen/options` or if any output carries a password, an email address or a token.

Add `-GameplayProbe` to drive the gameplay slice through the gameplay view models: target a live mob, cast to a killing blow, populate and take its loot offer, and verify the authoritative inventory update. Quest state, objective counters, refusals, and turn-in rewards use the server's `QuestStateUpdate` payload; reliable spawn/despawn events own entity lifetime while snapshots update known entities.

## About SarnautCore

This repository is part of SarnautCore, a fan-driven, non-commercial, open-source recreation kit for Allods Online.

The project charter and the architecture decision records live in [SarnautCore/docs](https://github.com/SarnautCore/docs). Read those before opening a pull request here.

## Clean-room posture

SarnautCore is built clean-room. This project never distributes game assets or data owned by MY.GAMES. The client ships as an empty shell; you supply the content from your own copy of the game, converted locally.

## License

AGPL-3.0. See [LICENSE](LICENSE).
