using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace SarnautCore;

/// <summary>
/// A map cell's baked per-vertex static lighting, read from the converter's
/// <c>*_MapRegion.xdb.lightvrt.json</c> companion (itself translated from the
/// source <c>X_Y_lightvrt.bin</c>). Each placed object's surfaces carry one
/// ambient-visibility and one direct-visibility byte per vertex, in the
/// emitted surface vertex order.
/// </summary>
/// <remarks>
/// <para>
/// The visibilities are colored at apply time with the zone's authored lights
/// (ambient = AmbientColor * AmbientFactor, direct = PointLightColor when
/// authored, else DiffuseColor) and written as vertex COLOR data. Lit surfaces
/// then render unshaded with the vertex color modulating the albedo, which is
/// the source engine's static-lighting model: statics are lit entirely by the
/// bake, not by the runtime sun. Additive and alpha-blended effect surfaces
/// keep their materials untouched, mirroring retail, where effect meshes
/// bypass lightvrt modulation.
/// </para>
/// <para>
/// The source geometry authors <c>vertexBakedLight</c> per material; parts
/// that author it false are lit dynamically in retail and their lightvrt
/// records are full-bright filler. The v3 companion marks those surfaces
/// <c>dynamic</c>: they keep their shaded converter materials and the mesh
/// stays on the runtime-lit receiver layer so the zone's point lights and
/// ambient reach them (the astral A_Stones rocks are the League example).
/// A mesh is demoted to the baked-only layer only when nothing on it needs
/// runtime light.
/// </para>
/// </remarks>
public sealed class BakedStaticLighting
{
    private readonly Dictionary<int, BakedObject> _objects;

    private BakedStaticLighting(Dictionary<int, BakedObject> objects)
    {
        _objects = objects;
    }

    public int ObjectCount => _objects.Count;

    public static BakedStaticLighting? TryLoad(string companionPath)
    {
        if (!FileAccess.FileExists(companionPath))
        {
            return null;
        }

        try
        {
            string json = FileAccess.GetFileAsString(companionPath);
            BakedDocumentJson? document = JsonSerializer.Deserialize<BakedDocumentJson>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (document?.Objects == null
                || document.Format is not ("sarnaut-baked-static-light-v2" or "sarnaut-baked-static-light-v3"))
            {
                return null;
            }

            var objects = new Dictionary<int, BakedObject>();
            foreach ((string key, BakedObjectJson value) in document.Objects)
            {
                if (!int.TryParse(key, out int index) || value.Surfaces == null)
                {
                    continue;
                }

                var surfaces = new List<BakedSurface>();
                foreach (BakedSurfaceJson surface in value.Surfaces)
                {
                    if (surface.Name == null)
                    {
                        continue;
                    }

                    if (surface.Dynamic)
                    {
                        surfaces.Add(new BakedSurface(surface.Name, [], [], Dynamic: true));
                        continue;
                    }

                    if (surface.AmbientB64 == null || surface.DirectB64 == null)
                    {
                        continue;
                    }

                    byte[] ambient = Convert.FromBase64String(surface.AmbientB64);
                    byte[] direct = Convert.FromBase64String(surface.DirectB64);
                    if (ambient.Length == direct.Length)
                    {
                        surfaces.Add(new BakedSurface(surface.Name, ambient, direct, Dynamic: false));
                    }
                }

                objects[index] = new BakedObject(surfaces);
            }

            return new BakedStaticLighting(objects);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"BakedStaticLighting could not parse {companionPath}: {exception.Message}");
            return null;
        }
    }

    public bool HasObject(int objectIndex) => _objects.ContainsKey(objectIndex);

    /// <summary>
    /// Colors one placed instance's meshes with its baked vertex light.
    /// Returns the number of surfaces that received baked colors.
    /// </summary>
    public int Apply(Node3D instance, int objectIndex, Color ambientLight, Color directLight)
    {
        if (!_objects.TryGetValue(objectIndex, out BakedObject? baked))
        {
            return 0;
        }

        int applied = 0;
        foreach (MeshInstance3D meshInstance in EnumerateMeshes(instance))
        {
            applied += ApplyToMesh(meshInstance, baked, ambientLight, directLight);
        }

        return applied;
    }

    private static int ApplyToMesh(
        MeshInstance3D meshInstance,
        BakedObject baked,
        Color ambientLight,
        Color directLight)
    {
        if (meshInstance.Mesh is not ArrayMesh mesh)
        {
            return 0;
        }

        int surfaceCount = mesh.GetSurfaceCount();
        bool any = false;
        bool anyDynamic = false;
        var rebuilt = new ArrayMesh();
        // Converted meshes may repeat one part name across surfaces (a model
        // element split by material), so surfaces match the bake sequentially:
        // both sides preserve the source part order, and LOD meshes are an
        // order-preserving subset of it.
        int cursor = 0;
        for (int surface = 0; surface < surfaceCount; surface++)
        {
            Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
            string name = mesh.SurfaceGetName(surface);
            Material? material = mesh.SurfaceGetMaterial(surface);
            SurfaceBakeResult result = TryBuildLitSurface(
                arrays, name, material, baked, ref cursor, ambientLight, directLight, out Material? litMaterial);
            rebuilt.AddSurfaceFromArrays(mesh.SurfaceGetPrimitiveType(surface), arrays);
            int destination = rebuilt.GetSurfaceCount() - 1;
            rebuilt.SurfaceSetName(destination, name);
            Material? finalMaterial = result == SurfaceBakeResult.Lit ? litMaterial : material;
            if (finalMaterial != null)
            {
                rebuilt.SurfaceSetMaterial(destination, finalMaterial);
            }

            any |= result == SurfaceBakeResult.Lit;
            anyDynamic |= result == SurfaceBakeResult.Dynamic;
        }

        if (!any)
        {
            return 0;
        }

        meshInstance.Mesh = rebuilt;
        // A fully baked mesh leaves the runtime-lit receiver layer: its
        // unshaded vertex-baked materials already ignore lights, and the layer
        // keeps the zone's point lights (authored and sampled) from ever
        // double-lighting it. A mesh with authored-dynamic surfaces stays a
        // receiver — its baked surfaces are unshaded and immune anyway, while
        // the dynamic ones need the runtime lights to not render black.
        if (!anyDynamic)
        {
            meshInstance.Layers = DynamicEntityLighting.BakedOnlyLayers;
        }

        return surfaceCount;
    }

    private enum SurfaceBakeResult
    {
        /// <summary>No bake entry applies; the surface keeps its material.</summary>
        Unmatched,

        /// <summary>The bake colored the surface; it renders unshaded.</summary>
        Lit,

        /// <summary>The source authored vertexBakedLight=false: runtime-lit.</summary>
        Dynamic,
    }

    private static SurfaceBakeResult TryBuildLitSurface(
        Godot.Collections.Array arrays,
        string surfaceName,
        Material? material,
        BakedObject baked,
        ref int cursor,
        Color ambientLight,
        Color directLight,
        out Material? litMaterial)
    {
        litMaterial = null;

        // Effect surfaces (additive glows, alpha-blended membranes) emit or
        // filter light on their own and bypass baked modulation in retail.
        if (material is BaseMaterial3D effect
            && (effect.BlendMode == BaseMaterial3D.BlendModeEnum.Add
                || effect.Transparency == BaseMaterial3D.TransparencyEnum.Alpha))
        {
            return SurfaceBakeResult.Unmatched;
        }

        if (arrays.Count <= (int)Mesh.ArrayType.Vertex
            || arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.PackedVector3Array)
        {
            return SurfaceBakeResult.Unmatched;
        }

        int vertexCount = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
        int match = -1;
        for (int candidate = cursor; candidate < baked.Surfaces.Count; candidate++)
        {
            BakedSurface entry = baked.Surfaces[candidate];
            if (entry.Name == surfaceName && (entry.Dynamic || entry.Ambient.Length == vertexCount))
            {
                match = candidate;
                break;
            }
        }

        if (match < 0)
        {
            GD.PushWarning(
                $"BakedStaticLighting: surface '{surfaceName}' ({vertexCount} vertices) "
                + "has no matching bake entry; left unlit.");
            return SurfaceBakeResult.Unmatched;
        }

        BakedSurface samples = baked.Surfaces[match];
        cursor = match + 1;
        if (samples.Dynamic)
        {
            // Authored vertexBakedLight=false: retail lights this part at
            // runtime, so its shaded converter material stands and the mesh
            // stays on the runtime-lit receiver layer.
            return SurfaceBakeResult.Dynamic;
        }

        var colors = new Color[vertexCount];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            // The ambient byte is bias-128 (0x80 encodes zero); the direct
            // byte is a plain visibility. See the converter's lightvrt module
            // for the channel-order evidence. The 2x matches the source
            // engine's modulate-2x lightmapping (authored light colors sit at
            // half intensity so the bake can over-brighten the albedo).
            float ambient = samples.Ambient[vertex] / 255.0f;
            float direct = samples.Direct[vertex] / 255.0f;
            colors[vertex] = new Color(
                Mathf.Min((ambientLight.R * ambient + directLight.R * direct) * 2.0f, 1.0f),
                Mathf.Min((ambientLight.G * ambient + directLight.G * direct) * 2.0f, 1.0f),
                Mathf.Min((ambientLight.B * ambient + directLight.B * direct) * 2.0f, 1.0f));
        }

        arrays[(int)Mesh.ArrayType.Color] = colors;
        if (material is BaseMaterial3D standard)
        {
            var unshaded = (BaseMaterial3D)standard.Duplicate();
            unshaded.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            unshaded.VertexColorUseAsAlbedo = true;
            // The authored colors and visibilities live in the source's gamma
            // space; declaring the vertex color sRGB keeps the multiply in the
            // same space as the albedo texture.
            unshaded.VertexColorIsSrgb = true;
            litMaterial = unshaded;
        }

        return SurfaceBakeResult.Lit;
    }

    private static IEnumerable<MeshInstance3D> EnumerateMeshes(Node root)
    {
        if (root is MeshInstance3D meshInstance)
        {
            yield return meshInstance;
        }

        foreach (Node child in root.GetChildren())
        {
            foreach (MeshInstance3D descendant in EnumerateMeshes(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed record BakedSurface(string Name, byte[] Ambient, byte[] Direct, bool Dynamic);

    private sealed class BakedObject
    {
        public BakedObject(List<BakedSurface> surfaces)
        {
            Surfaces = surfaces;
        }

        public List<BakedSurface> Surfaces { get; }
    }

    private sealed class BakedDocumentJson
    {
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("objects")] public Dictionary<string, BakedObjectJson>? Objects { get; set; }
    }

    private sealed class BakedObjectJson
    {
        [JsonPropertyName("template_href")] public string? TemplateHref { get; set; }
        [JsonPropertyName("geometry_source")] public string? GeometrySource { get; set; }
        [JsonPropertyName("surfaces")] public BakedSurfaceJson[]? Surfaces { get; set; }
    }

    private sealed class BakedSurfaceJson
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("ambient_b64")] public string? AmbientB64 { get; set; }
        [JsonPropertyName("direct_b64")] public string? DirectB64 { get; set; }
        [JsonPropertyName("dynamic")] public bool Dynamic { get; set; }
    }
}
