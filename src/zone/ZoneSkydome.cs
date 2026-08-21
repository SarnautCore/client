using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Godot;

namespace SarnautCore;

/// <summary>
/// The zone's authored skydome, instanced from the converted SkyMesh parts
/// (background dome, star shell, animated cloud layers) referenced by the
/// zone's instant light set.
/// </summary>
/// <remarks>
/// Retail draws the sky centered on the viewer, so the node copies the active
/// camera's translation every frame while world geometry keeps occluding it
/// through the depth test — an interior therefore never sees the dome. The
/// dome geometry is authored at world scale (Ast_01 spans ~594 units) and lies
/// far outside the zone's authored fog, so its materials render unshaded with
/// fog disabled; the fog-colored background remains beneath as the backstop.
/// The authored per-part fovFactor projection trick and part ambientColor
/// modulation are not reproduced yet.
/// </remarks>
public partial class ZoneSkydome : Node3D
{
    private readonly List<Node3D> _parts = [];
    private readonly Dictionary<Node3D, Color> _partTints = [];

    public int PartCount => _parts.Count;

    private sealed record SkyPart(string GeometrySource, Color Tint);

    public static ZoneSkydome? TryCreate(AllodsResourceTree tree, string convertedRoot, string skyMeshSource)
    {
        AllodsResource? skyMesh = string.IsNullOrEmpty(skyMeshSource) ? null : tree.Load(skyMeshSource);
        if (skyMesh == null)
        {
            return null;
        }

        List<SkyPart> parts;
        try
        {
            XDocument document = XDocument.Parse(skyMesh.raw_xml);
            parts = document.Descendants()
                .Where(element => element.Name.LocalName is "Item" or "SkyMesh")
                .Select(item =>
                {
                    string href = item.Elements()
                        .FirstOrDefault(child => child.Name.LocalName == "geometry")
                        ?.Attribute("href")?.Value ?? string.Empty;
                    // The part's authored ambientColor is its modulation color:
                    // the sky textures are near-greyscale and the engine tints
                    // them per part (Ast_01: purple-blue dome, violet stars).
                    // Parts without one (the animated cloud layer) stay white.
                    string? tintText = item.Elements()
                        .FirstOrDefault(child => child.Name.LocalName == "ambientColor")
                        ?.Value;
                    Color tint = Colors.White;
                    if (long.TryParse(tintText, out long packed) && (packed & 0xFFFFFF) != 0)
                    {
                        uint argb = unchecked((uint)packed);
                        tint = new Color(
                            ((argb >> 16) & 0xFF) / 255.0f,
                            ((argb >> 8) & 0xFF) / 255.0f,
                            (argb & 0xFF) / 255.0f);
                    }

                    return new SkyPart(
                        string.IsNullOrEmpty(href)
                            ? string.Empty
                            : AllodsResourceTree.NormalizeHref(skyMeshSource, href),
                        tint);
                })
                .Where(part => !string.IsNullOrEmpty(part.GeometrySource))
                .GroupBy(part => part.GeometrySource)
                .Select(group => group.First())
                .ToList();
        }
        catch (System.Exception exception)
        {
            GD.PushWarning($"ZoneSkydome could not parse {skyMeshSource}: {exception.Message}");
            return null;
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var dome = new ZoneSkydome { Name = "Skydome" };
        foreach (SkyPart part in parts)
        {
            Node3D? node = InstantiatePart(convertedRoot, part.GeometrySource);
            if (node != null)
            {
                dome._parts.Add(node);
                dome._partTints[node] = part.Tint;
                dome.AddChild(node);
            }
        }

        if (dome._parts.Count == 0)
        {
            dome.QueueFree();
            return null;
        }

        return dome;
    }

    private static Node3D? InstantiatePart(string convertedRoot, string geometrySource)
    {
        const string geometryMarker = ".(Geometry).xdb";
        if (!geometrySource.EndsWith(geometryMarker, System.StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // The converted scene sits beside the geometry under the visual
        // template's name; its runtime script builds the mesh (with LODs and
        // the authored cloud animation) when the node enters the tree.
        string stem = geometrySource[..^geometryMarker.Length];
        string scenePath = $"{convertedRoot.TrimEnd('/')}/assets/{stem}.(VisObjectTemplate).scene.tscn";
        PackedScene? scene = ConvertedSceneLoader.Load(convertedRoot, scenePath, out string sceneError);
        if (scene?.Instantiate() is Node3D instance)
        {
            instance.Name = SafePartName(stem);
            return instance;
        }

        GD.PushWarning($"ZoneSkydome: sky part is not loadable: {scenePath}. {sceneError}".Trim());
        return null;
    }

    public override void _Ready()
    {
        // The parts' meshes are built by their scene scripts during AddChild,
        // so the sky-specific material state is applied one frame later.
        CallDeferred(MethodName.PrepareSkyMaterials);
    }

    private void PrepareSkyMaterials()
    {
        foreach (Node3D part in _parts)
        {
            Color tint = _partTints.TryGetValue(part, out Color authored) ? authored : Colors.White;
            foreach (MeshInstance3D meshInstance in EnumerateMeshes(part))
            {
                meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                if (meshInstance.Mesh is not ArrayMesh mesh)
                {
                    continue;
                }

                for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                {
                    if (mesh.SurfaceGetMaterial(surface) is not BaseMaterial3D material)
                    {
                        continue;
                    }

                    material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                    material.DisableFog = true;
                    material.AlbedoColor = tint;
                    // Sky shells carry their gradient in vertex colors when the
                    // source authored them; white columns are a no-op.
                    material.VertexColorUseAsAlbedo = true;
                    material.VertexColorIsSrgb = true;
                }
            }
        }
    }

    public override void _Process(double delta)
    {
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera != null)
        {
            GlobalPosition = camera.GlobalPosition;
        }
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

    private static string SafePartName(string stem)
    {
        int slash = stem.LastIndexOf('/');
        string name = slash >= 0 ? stem[(slash + 1)..] : stem;
        return name.Replace('.', '_').Replace(' ', '_');
    }
}
