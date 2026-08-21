using System;
using System.Text.Json.Nodes;

namespace SarnautCore;

internal static class NativeTerrainManifestTestMutation
{
    public static string ReplaceCompiledSceneWithMissing(string json, string tileId)
    {
        JsonObject root = ParseRoot(json);
        JsonObject tile = FindTile(root, tileId);
        string runtimeScene = RequireCompiledRuntimeScene(tile, tileId);
        int slash = runtimeScene.LastIndexOf('/');
        string directory = slash >= 0 ? runtimeScene[..(slash + 1)] : string.Empty;
        tile["runtime_scene"] = $"{directory}missing_terrain.scn";
        return root.ToJsonString();
    }

    public static string DuplicateCompiledScene(string json, string sourceTileId, string targetTileId)
    {
        JsonObject root = ParseRoot(json);
        JsonObject source = FindTile(root, sourceTileId);
        JsonObject target = FindTile(root, targetTileId);
        string sourceRuntimeScene = RequireCompiledRuntimeScene(source, sourceTileId);
        RequireCompiledRuntimeScene(target, targetTileId);
        target["runtime_scene"] = sourceRuntimeScene;
        return root.ToJsonString();
    }

    private static JsonObject ParseRoot(string json) =>
        JsonNode.Parse(json) as JsonObject
        ?? throw new InvalidOperationException("Native terrain manifest is not a JSON object.");

    private static JsonObject FindTile(JsonObject root, string tileId)
    {
        JsonArray tiles = root["tiles"] as JsonArray
            ?? throw new InvalidOperationException("Native terrain manifest has no tiles array.");
        foreach (JsonNode? node in tiles)
        {
            if (node is JsonObject tile
                && tile["tile_id"]?.GetValue<string>() == tileId)
            {
                return tile;
            }
        }

        throw new InvalidOperationException($"Native terrain manifest has no tile '{tileId}'.");
    }

    private static string RequireCompiledRuntimeScene(JsonObject tile, string tileId)
    {
        string runtimeScene = tile["runtime_scene"]?.GetValue<string>() ?? string.Empty;
        if (!runtimeScene.EndsWith(".scn", StringComparison.OrdinalIgnoreCase)
            || runtimeScene.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Native terrain tile '{tileId}' has no authoritative compiled runtime_scene.");
        }

        return runtimeScene;
    }
}
