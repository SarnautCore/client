using System.Linq;
using Godot;

namespace SarnautCore;

/// <summary>Phase-3 diagnostic: reports how the A_Stones instances are shaded.</summary>
public partial class P3RockDiagProbe : Node
{
    public override async void _Ready()
    {
        var loader = new ZoneLoader { Name = "ZoneLoader", SpawnNpcVisuals = false };
        AddChild(loader);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var tree = new AllodsResourceTree(loader.ConvertedRoot);
        if (ZoneEnvironmentSettings.TryLoad(tree, loader.MapName, "InstLeague1", out ZoneEnvironmentSettings settings, out string error))
        {
            loader.ApplyZoneLighting(settings);
        }
        else
        {
            GD.Print($"P3ROCKDIAG environment error: {error}");
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        foreach (Node child in loader.GetNode<Node3D>("StaticObjects").GetChildren())
        {
            if (child is not Node3D instance
                || !instance.GetMeta("native_scene", string.Empty).AsString()
                    .EndsWith("/A_Stones.tscn", System.StringComparison.Ordinal))
            {
                continue;
            }

            GD.Print($"P3ROCKDIAG instance at {instance.Position} scale={instance.Scale}");
            foreach (MeshInstance3D mesh in Descendants(instance))
            {
                string info = $"  mesh={mesh.Name} layers={mesh.Layers} visible={mesh.IsVisibleInTree()} type={mesh.Mesh?.GetType().Name}";
                if (mesh.Mesh is ArrayMesh arrayMesh)
                {
                    for (int surface = 0; surface < arrayMesh.GetSurfaceCount(); surface++)
                    {
                        Godot.Collections.Array arrays = arrayMesh.SurfaceGetArrays(surface);
                        bool hasColor = arrays.Count > (int)Mesh.ArrayType.Color
                            && arrays[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.PackedColorArray
                            && arrays[(int)Mesh.ArrayType.Color].AsColorArray().Length > 0;
                        Color first = hasColor ? arrays[(int)Mesh.ArrayType.Color].AsColorArray()[0] : Colors.Black;
                        int vertexCount = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
                        var material = arrayMesh.SurfaceGetMaterial(surface) as BaseMaterial3D;
                        info += $"\n    surface={arrayMesh.SurfaceGetName(surface)} verts={vertexCount} color={hasColor} first={first}"
                            + $" shading={material?.ShadingMode} vtxAlbedo={material?.VertexColorUseAsAlbedo}"
                            + $" albedoColor={material?.AlbedoColor} tex={(material?.AlbedoTexture != null)}";
                    }
                }

                GD.Print(info);
            }
        }

        GetTree().Quit(0);
    }

    private static System.Collections.Generic.IEnumerable<MeshInstance3D> Descendants(Node root)
    {
        if (root is MeshInstance3D mesh)
        {
            yield return mesh;
        }

        foreach (Node child in root.GetChildren())
        {
            foreach (MeshInstance3D descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
