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
godot_console --headless --import --path .
godot_console --headless --path . --scene res://tests/asset_viewer_smoke.tscn
godot_console --headless --path . --scene res://tests/zone_walkabout_smoke.tscn
```

The Asset Viewer smoke scene expects local samples under `converted/samples/`. The Zone Walkabout smoke scene expects the classic conversion below `converted/assets/classic-1.1/` and prints the imported terrain and placement counts. These files are intentionally absent from Git because converted game assets must remain local.

## About SarnautCore

This repository is part of SarnautCore, a fan-driven, non-commercial, open-source recreation kit for Allods Online.

The project charter and the architecture decision records live in [SarnautCore/docs](https://github.com/SarnautCore/docs). Read those before opening a pull request here.

## Clean-room posture

SarnautCore is built clean-room. This project never distributes game assets or data owned by MY.GAMES. The client ships as an empty shell; you supply the content from your own copy of the game, converted locally.

## License

AGPL-3.0. See [LICENSE](LICENSE).
