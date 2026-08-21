# Upscaled texture preference

How the client prefers Real-ESRGAN 4x variants over converted originals, why the
variants are not imported by Godot, and what that costs. Companion to ADR 0015
(icon upscaling), which established that upscaled art is a *variant* and the
converted original stays canonical.

## The seam

`SarnautCore.UpscaledTextures` (`src/visual/UpscaledTextures.cs`) is the only
place the choice is made. Everything else asks it and falls back on its own.

| Entry point | Purpose |
|---|---|
| `MapPath(convertedPath, root)` | Pure path arithmetic, no filesystem. |
| `ResolvePath(convertedPath)` | `MapPath` + a cached existence probe; `""` when there is no variant or the preference is off. |
| `Load(convertedPath)` | The variant as a `Texture2D`, or `null` to mean "keep what you had". |
| `Retexture(object)` | Swaps every `Texture2D` reachable from a loaded node or resource. |
| `IsUpscaled(texture)` | Whether this exact texture came from the batch (used by probes to measure coverage). |
| `StatsLine()` | One machine-readable line of hits/misses/resident bytes. |
| `Release()` | Hands the decoded variants back before the engine shuts down (see below). |

Call sites, all of which fall back to the original on `null`:

- `ConvertedSkinnedMesh.LoadTexture` — `.skmesh` body atlas and per-surface albedo.
- `ConvertedSceneLoader.LoadResource<T>` — `Retexture` on every converted `.tres`.
  This is the big one: converted zone props reach their materials through
  `ImporterMesh`, so it carries most of the coverage.
- `ConvertedImporterMesh` — `Retexture` on the `ArrayMesh` that `GetMesh()`
  returns, because `GetMesh` may copy surface materials.
- `ZoneLoader` static visual objects, `ConvertedChrome` / `ConvertedHudChrome`,
  `KaniaFemaleWarrior.LoadTexture`, `GameplayHudControl` portrait mask,
  `AssetViewer.PreviewTexture`.

Terrain is deliberately untouched: `ZoneLoader`'s splat-layer load is not routed,
and `MapPath` refuses `Maps/` anyway.

## The path rule

The batch mirrored the converted tree under a per-asset-class root rather than
reproducing its shape, so this is not a prefix swap:

```
converted/assets/classic-1.1/assets/<TopDir>/<rest>
  -> upscaled/<topdir lowercased>/classic-1.1/<rest>
```

Two irregularities:

- `Interface/**` drops the converter's class suffix — `Close.(UITexture).png`
  was written as `Close.png`.
- `Interface/Icons/**` was batched as its own class, `icons/`.

Classes the batch covered: `characters`, `client`, `creatures`, `icons`,
`interface`, `items`, `ships`, `spells`, `world`. Everything else
(`Maps`, `SFX`, `System`, `Editor`, `Mechanics`, `Mods`, `fonts`) keeps originals.

This rule reproduces **all 25,730 completed entries across the eight manifests
with zero mismatches**, which is why the manifests do not ship with the client:
presence on disk is the authority, checked once per path and cached.

## Why the variants are not imported

The upscaled tree is mounted at `res://upscaled/assets` (a junction to
`assets/upscaled`) with a **`.gdignore`** in `upscaled/`, so Godot never scans or
imports it. Variants are decoded through `Image.LoadFromFile` +
`ImageTexture.CreateFromImage` — the same route `ConvertedSkinnedMesh` already
used for an unimported converted tree.

The alternative was to let Godot import the tree the way `converted/` is
imported. Rejected on measurement:

- 27,249 PNGs, **35.6 GB** on disk (the converted tree is 29.3 GB for 49,573 PNGs).
- `.godot/imported` is already 23.7 GB for the converted tree, whose textures
  import at `compress/mode=0` (lossless). Importing 16x-the-texels art the same
  way would add tens of GB and hours of one-time import.
- It would write 27,249 `.import` sidecars into `assets/upscaled`.

The runtime route costs nothing on disk, needs no import step, works on a fresh
checkout, and gives per-texture control over mipmaps and compression that the
importer's global defaults do not. Its one limitation is that a variant cannot be
named in an `ext_resource` path, which is why scene and resource loads are fixed
up with `Retexture` after the fact rather than by rewriting the patched `.tscn`
text.

## Shutdown: the finalizer trap

`Retexture` reads properties off every node and resource it walks. Each
`Variant.AsGodotObject()` leaves a managed wrapper behind, and for a `RefCounted`
that wrapper has a finalizer. Left to the GC those finalizers can run *after*
Godot has torn down its native side, and `godotsharp_internal_refcounted_disposed`
then faults the process with `0xC000001D` — after the probe has already printed
`result=PASS`, so it shows up only as a bad exit code.

`SessionHost._ExitTree` therefore calls `UpscaledTextures.Release()`, which drains
the queue with `GC.Collect()` + `GC.WaitForPendingFinalizers()` while the engine
is still up. That autoload outlives every scene, which makes it the one place
that can.

It deliberately does **not** dispose the cached `ImageTexture`s. That was the
first attempt and it looked safe — a material still holding a texture keeps the
native object alive — but disposing releases the C# binding's GCHandle while
Godot still expects it, and the later native destructor trips
`FATAL: Condition "gchandle.is_released()" is true`. It fixed
`gameplay_hud_layout_smoke` and broke `canonical_player_grounding_probe`.
Letting the normal shutdown path release the textures is simpler and correct.

None of this was theoretical: each variant failed the gate, and both reproduced
deterministically with the preference on and never with it off. Anything that
adds a new object walk needs to stay behind the same drain.

## Memory

4x linear scale is 16x the texels. Measured across a full League zone load
(`Inst_LeagueStart`, 4 terrain tiles / 36 visual objects, RTX 4070 Super):

| configuration | texture mem | video mem | decode | zone load |
|---|---|---|---|---|
| originals only | 202 MB | 417 MB | — | 31 s |
| upscaled, uncompressed | 1934 MB | 2150 MB | 5.2 s | — |
| upscaled + S3TC | **429 MB** | 643 MB | 8.5 s | 39 s |
| upscaled + S3TC + mipmaps | 529 MB | 807 MB | 10.1 s | 43 s |

Shipped: **S3TC on, mipmaps off** — 429 MB, 2.1x the originals' texture memory.

Uncompressed 4x art costs 9.6x the originals and is not shippable. Runtime block
compression buys nearly all of it back for a one-time decode cost that is cached
for the process lifetime.

## Why mipmaps are off

The converted originals import with `mipmaps/generate=false`, so off is parity,
not a regression. It is also what the measurements want. Mean `|Laplacian|`
(high-frequency detail) over fixed regions of the standard interior frame:

| region | originals | upscaled, no mips | upscaled + mips |
|---|---|---|---|
| near floor | 4.015 | **8.090** | 3.448 |
| right carved wall | 5.308 | **6.500** | 3.151 |
| left curtain trim | 7.837 | **9.710** | 5.254 |

A 4x texture minified to the same screen size selects roughly mip 2 — the
original resolution again, then filtered. Mipmaps therefore *erase* the benefit
the upscale exists to deliver, and cost 100 MB to do it. The honest fix is a
negative `rendering/textures/default_filters/texture_mipmap_bias` or anisotropic
filtering so a mip chain keeps the detail it is meant to protect; until then the
originals' own no-mipmap behaviour is the baseline and 4x art beats it.

Aliasing in motion is the known trade, and it is the trade the originals already
make.

## Turning it off

For debugging, originals-only:

- `project.godot` → `sarnaut/visual/prefer_upscaled_textures=false`, or
- `SARNAUT_UPSCALED_TEXTURES=0` for a single run.

The two memory knobs override the same way, for measurement:
`SARNAUT_UPSCALED_VRAM_COMPRESSION` and `SARNAUT_UPSCALED_MIPMAPS` (`1`/`0`).

## Batch dispositions

- **3 skipped** (`items`) and **36 excluded** (34 `world`, 1 `items`, 1 `spells`)
  have no output and fall back to originals automatically.
- **132 `creatures` entries logged `invalidated`** ("blank output"). The manifest
  is append-only and every one of them was re-run: the final status for all 941
  distinct creature sources is `completed`, and spot checks of the retried files
  show 500-600 sampled colours, not the solid blank that was rejected. No
  denylist is needed, and the probe asserts one of them is not blank.
- Terrain layer atlases were excluded by the batch (separate lane).

## Verification

`tests/upscaled_texture_probe.tscn` pins the path rule, checks a variant decodes
at 4x through the `.gdignore`, checks the retried creature texture is not blank,
checks terrain keeps originals, checks the disable toggle suppresses everything,
and reports coverage and VRAM from a real zone load. It is deliberately **not**
in `visual-gate.ps1`, which stays at its 16 rendering probes.

Coverage on `Inst_LeagueStart`: **1559 of 1568 textured surfaces (99.4%)**. The
remaining 9 are runtime-built textures with no source path (the Kania starting-kit
atlas), whose inputs already went through the seam individually.

The HUD is covered too: `gameplay_hud_layout_smoke` reports 414 substitutions
across its three viewports, from the interface and icons classes.

Known gap: `Theme` resources expose their icons through methods rather than
properties, so a converted theme's own icons are not walked. In practice the HUD
textures come through `ConvertedChrome` / `ConvertedHudChrome`, which are walked
after instantiation, so this has not cost coverage.
