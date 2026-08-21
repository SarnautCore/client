using System;
using System.Collections.Generic;
using Godot;

namespace SarnautCore;

/// <summary>
/// Serves tile 1_2's real coordinate manifest with <c>origin_applied</c>
/// flipped to true, as a converter that double-shifted a tile would.
/// </summary>
public partial class OriginAppliedManifestLoader : ZoneLoader
{
    // This probe pins the converted route's manifest validation. With the
    // native bake mounted, the native path would serve every tile and the
    // corrupted manifest below would never be read, so the native route is
    // disabled to force the converted path.
    protected override bool TryAddNativeTerrainTileImpl(string convertedPath, out string error)
    {
        error = string.Empty;
        return false;
    }

    protected override string ReadTileManifestJson(Node3D tile, string terrainPath)
    {
        string manifest = base.ReadTileManifestJson(tile, terrainPath);
        if (!terrainPath.Contains("/1_2.terrain.", StringComparison.OrdinalIgnoreCase))
        {
            return manifest;
        }

        return manifest.Replace("\"origin_applied\":false", "\"origin_applied\":true", StringComparison.Ordinal);
    }

    protected override bool TryAddLegacyTerrainTile(
        string terrainPath,
        IReadOnlyList<string> layerTextures,
        out string error)
    {
        // The legacy .obj fallback reads its manifest from the file itself and
        // cannot be corrupted through the hook, so the injected tile must not
        // quietly recover through it.
        if (terrainPath.Contains("/1_2.terrain.", StringComparison.OrdinalIgnoreCase))
        {
            error = "Injected: legacy fallback disabled for the origin-applied tile.";
            return false;
        }

        return base.TryAddLegacyTerrainTile(terrainPath, layerTextures, out error);
    }
}
