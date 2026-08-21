using System;
using System.Collections.Generic;
using Godot;

namespace SarnautCore;

/// <summary>
/// Samples the zone's baked lighting at a world position, from the same
/// per-tile terrain lightmaps the terrain shader combines (R = ambient/sky
/// visibility, G = direct-light visibility toward the zone's authored direct
/// source — the sun outdoors, torch/point sources in interiors).
/// </summary>
/// <remarks>
/// <para>
/// This is the retail model for lighting dynamic entities in a baked zone: the
/// engine reads the bake near the entity and colors it with the same authored
/// ZoneLights terms the statics were colored with, so a character standing in
/// a torch pool goes warm and one in the astral shade stays cool blue. The
/// League tutorial ships no placed <c>LightComponent</c> sources at all (its
/// Maya point lights survive only as the bake), so this sampled term is the
/// only authored record of where its local light falls.
/// </para>
/// <para>
/// One probe exists per loaded zone. <see cref="Active"/> is set when the
/// zone's authored environment is applied and cleared when the zone resets, so
/// character rigs outside a zone (chargen, asset viewer, appearance probes)
/// sample nothing and keep their neutral lighting.
/// </para>
/// </remarks>
public sealed class BakedLightProbe
{
    /// <summary>The probe of the currently loaded zone, if any.</summary>
    public static BakedLightProbe? Active { get; private set; }

    private readonly List<TileEntry> _tiles = [];

    /// <summary>The zone's authored direct-light color (PointLightColor when authored, else sun).</summary>
    public Color DirectColor { get; private set; } = Colors.Black;

    /// <summary>The zone's authored colored ambient term.</summary>
    public Color AmbientColor { get; private set; } = Colors.Black;

    public int TileCount => _tiles.Count;

    public static void Activate(BakedLightProbe? probe)
    {
        Active = probe;
    }

    public void SetZoneColors(Color ambient, Color direct)
    {
        AmbientColor = ambient;
        DirectColor = direct;
    }

    /// <summary>
    /// Registers one terrain tile's lightmap. The world→UV mapping is fitted
    /// from the mesh's own vertex data rather than assumed from filenames, so
    /// any UV convention the converter emits keeps sampling correctly.
    /// </summary>
    public bool TryAddTile(MeshInstance3D side, Vector3 worldOffset)
    {
        if (side.MaterialOverride is not ShaderMaterial material
            || material.GetShaderParameter("lightmap_tex").Obj is not Texture2D texture)
        {
            return false;
        }

        Image? image = texture.GetImage();
        if (image == null || image.IsEmpty())
        {
            return false;
        }

        if (image.IsCompressed() && image.Decompress() != Error.Ok)
        {
            return false;
        }

        if (side.Mesh is not { } mesh || mesh.GetSurfaceCount() == 0)
        {
            return false;
        }

        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
        if (arrays.Count <= (int)Mesh.ArrayType.TexUV
            || arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.PackedVector3Array
            || arrays[(int)Mesh.ArrayType.TexUV].VariantType != Variant.Type.PackedVector2Array)
        {
            return false;
        }

        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector2[] uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        if (vertices.Length < 2 || uvs.Length != vertices.Length)
        {
            return false;
        }

        // Fit the axis-aligned affine map world(x,z) -> uv from the extreme
        // vertices. Terrain tiles are regular axis-aligned grids, so the two
        // spans fully determine the mapping.
        int minXIndex = 0, maxXIndex = 0, minZIndex = 0, maxZIndex = 0;
        for (int index = 1; index < vertices.Length; index++)
        {
            if (vertices[index].X < vertices[minXIndex].X) { minXIndex = index; }
            if (vertices[index].X > vertices[maxXIndex].X) { maxXIndex = index; }
            if (vertices[index].Z < vertices[minZIndex].Z) { minZIndex = index; }
            if (vertices[index].Z > vertices[maxZIndex].Z) { maxZIndex = index; }
        }

        float xSpan = vertices[maxXIndex].X - vertices[minXIndex].X;
        float zSpan = vertices[maxZIndex].Z - vertices[minZIndex].Z;
        if (xSpan <= 0 || zSpan <= 0)
        {
            return false;
        }

        _tiles.Add(new TileEntry(
            OriginX: worldOffset.X + vertices[minXIndex].X,
            OriginZ: worldOffset.Z + vertices[minZIndex].Z,
            SizeX: xSpan,
            SizeZ: zSpan,
            UAtMinX: uvs[minXIndex].X,
            UAtMaxX: uvs[maxXIndex].X,
            VAtMinZ: uvs[minZIndex].Y,
            VAtMaxZ: uvs[maxZIndex].Y,
            Lightmap: image));
        return true;
    }

    /// <summary>
    /// Samples the baked visibilities at a world position. Outside every tile
    /// the position is open astral sky: full ambient, no local direct light.
    /// </summary>
    public (float Ambient, float Direct) Sample(Vector3 world)
    {
        foreach (TileEntry tile in _tiles)
        {
            float localX = world.X - tile.OriginX;
            float localZ = world.Z - tile.OriginZ;
            if (localX < 0 || localX > tile.SizeX || localZ < 0 || localZ > tile.SizeZ)
            {
                continue;
            }

            float u = Mathf.Lerp(tile.UAtMinX, tile.UAtMaxX, localX / tile.SizeX);
            float v = Mathf.Lerp(tile.VAtMinZ, tile.VAtMaxZ, localZ / tile.SizeZ);
            // The terrain shader samples the lightmap at (u, 1 - v); mirror it.
            int px = (int)Math.Clamp(u * (tile.Lightmap.GetWidth() - 1), 0, tile.Lightmap.GetWidth() - 1);
            int py = (int)Math.Clamp((1.0f - v) * (tile.Lightmap.GetHeight() - 1), 0, tile.Lightmap.GetHeight() - 1);
            Color sample = tile.Lightmap.GetPixel(px, py);
            return (sample.R, sample.G);
        }

        return (1.0f, 0.0f);
    }

    private sealed record TileEntry(
        float OriginX,
        float OriginZ,
        float SizeX,
        float SizeZ,
        float UAtMinX,
        float UAtMaxX,
        float VAtMinZ,
        float VAtMaxZ,
        Image Lightmap);
}
