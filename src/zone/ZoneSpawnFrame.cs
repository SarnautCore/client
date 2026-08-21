using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SarnautCore;

/// <summary>
/// Chooses an offline camera start over one populated terrain part rather than
/// the empty centre of a disconnected map-wide bounding box.
/// </summary>
internal static class ZoneSpawnFrame
{
    private const float GroundClearance = 0.5f;

    public static Vector3 Suggest(IReadOnlyList<Aabb> terrainParts, IReadOnlyList<Vector3> authoredHints)
    {
        if (terrainParts.Count == 0)
        {
            return new Vector3(0, 5, 0);
        }

        Vector3 hint = authoredHints.Count == 0
            ? Largest(terrainParts).GetCenter()
            : new Vector3(
                Median(authoredHints.Select(point => point.X)),
                Median(authoredHints.Select(point => point.Y)),
                Median(authoredHints.Select(point => point.Z)));
        Aabb part = terrainParts
            .Where(bounds => ContainsHorizontal(bounds, hint))
            .OrderByDescending(HorizontalArea)
            .FirstOrDefault();
        if (HorizontalArea(part) <= 0)
        {
            part = terrainParts
                .OrderBy(bounds => HorizontalDistanceSquared(bounds, hint))
                .First();
        }

        float insetX = Math.Min(GroundClearance, part.Size.X * 0.25f);
        float insetZ = Math.Min(GroundClearance, part.Size.Z * 0.25f);
        float x = Math.Clamp(hint.X, part.Position.X + insetX, part.End.X - insetX);
        float z = Math.Clamp(hint.Z, part.Position.Z + insetZ, part.End.Z - insetZ);
        float y = Math.Clamp(
            hint.Y + GroundClearance,
            part.Position.Y + GroundClearance,
            part.End.Y + 5.0f);
        return new Vector3(x, y, z);
    }

    private static Aabb Largest(IReadOnlyList<Aabb> terrainParts) =>
        terrainParts.OrderByDescending(HorizontalArea).First();

    private static float HorizontalArea(Aabb bounds) => bounds.Size.X * bounds.Size.Z;

    private static bool ContainsHorizontal(Aabb bounds, Vector3 point) =>
        point.X >= bounds.Position.X
        && point.X <= bounds.End.X
        && point.Z >= bounds.Position.Z
        && point.Z <= bounds.End.Z;

    private static float HorizontalDistanceSquared(Aabb bounds, Vector3 point)
    {
        float x = Math.Clamp(point.X, bounds.Position.X, bounds.End.X);
        float z = Math.Clamp(point.Z, bounds.Position.Z, bounds.End.Z);
        float deltaX = point.X - x;
        float deltaZ = point.Z - z;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

    private static float Median(IEnumerable<float> values)
    {
        float[] sorted = values.Order().ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) * 0.5f;
    }
}

