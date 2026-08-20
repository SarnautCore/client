# SarnautCore client

The SarnautCore game client uses Godot 4.7.2 .NET and C#. C++ GDExtensions are reserved for measured hot paths. The Lua addon interface will come later.

The current milestone includes a boot menu, an Asset Viewer, and a 3D Zone Walkabout for locally converted Allods assets.

## Development quickstart

Install Godot 4.7.2 stable .NET and a .NET 10 SDK. Then run:

```powershell
dotnet build SarnautCore.sln
godot --editor --path .
```

The default scene opens a small boot menu. Choose **Asset Viewer** to browse supported files below `converted/`.

### Zone Walkabout

The Boot menu defaults the zone field to `Inst_LeagueStart`, the classic League tutorial map. Choose **Zone Walkabout** to assemble the map from converted terrain OBJ files and MapRegion placement JSON.

Controls:

- `WASD`: move
- Mouse: look
- `Space`: jump while walking, rise while flying
- `Q`: descend while flying
- `Shift`: fly faster
- `F`: toggle walk/fly mode
- `Esc`: release the mouse; press it again to return to Boot

The loader currently applies the dominant converted terrain-layer texture to each terrain tile. Splat and light maps are present in the conversion, but full layered terrain materials are pending converter support.

Walkabout loads the classic Elf male skinned scene for the local player and crossfades between its `idle` and `run` clips from controller movement state. The camera follows from a third-person spring arm. Server-object placements resolve `MobWorld` and spawn-table references into the zone's converted character or creature scene, then play `idle`; NPC scene loading keeps only `idle`, `run`, and `walk` resources to avoid loading unrelated combat and emote clips. A missing or incomplete converted model becomes a colored capsule and is counted in `NpcPlaceholderCount` and `NpcModelFailures`.

### Online walkabout

Start the shard, enable **Online mode** on the Boot menu, and leave the endpoint at `127.0.0.1:4242`. The walkabout then joins network zone `InstLeague1`, sends the controller's world-space WASD input at 20 Hz, and renders authoritative snapshots with a 125 ms interpolation delay. The existing `Walker` node represents the local player. Other players and NPCs appear as colored capsules. Disable the toggle to use the original offline controller unchanged.

Set `SARNAUT_SERVER_ADDRESS` before starting Godot to change the endpoint default. Online mode accepts the shard's ephemeral self-signed certificate for local development. Production certificate validation remains future work.

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
dotnet test tests/SarnautCore.Network.Tests/SarnautCore.Network.Tests.csproj
godot_console --headless --import --path .
godot_console --headless --path . --scene res://tests/asset_viewer_smoke.tscn
godot_console --headless --path . --scene res://tests/zone_walkabout_smoke.tscn
```

The Asset Viewer smoke scene expects local samples under `converted/samples/`. The Zone Walkabout smoke scene expects the classic conversion below `converted/assets/classic-1.1/` and prints the imported terrain and placement counts. These files are intentionally absent from Git because converted game assets must remain local.

For an asset-free end-to-end network check, keep the `server` repository beside this one and run:

```powershell
../server/scripts/sar20-client-smoke.ps1 -ClientRepository .
```

The script starts the production shard with a temporary synthetic content fixture and runs `tools/SarnautCore.NetSmoke`. It passes only after the client joins, sends movement, and observes its authoritative position advance. The smoke is kept as a local cross-repository check because the two repositories publish independently and Linux runners require a separately installed `libmsquic`. The pure C# protocol and interpolation tests run in this repository's CI.

## About SarnautCore

This repository is part of SarnautCore, a fan-driven, non-commercial, open-source recreation kit for Allods Online.

The project charter and the architecture decision records live in [SarnautCore/docs](https://github.com/SarnautCore/docs). Read those before opening a pull request here.

## Clean-room posture

SarnautCore is built clean-room. This project never distributes game assets or data owned by MY.GAMES. The client ships as an empty shell; you supply the content from your own copy of the game, converted locally.

## License

AGPL-3.0. See [LICENSE](LICENSE).
