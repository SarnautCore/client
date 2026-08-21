using System;
using System.Collections.Generic;
using Godot;
using SarnautCore.Content;

namespace SarnautCore.Tests;

public partial class AssetViewerSmoke : Node
{
    public override async void _Ready()
    {
        var scene = ResourceLoader.Load<PackedScene>("res://scenes/asset_viewer.tscn");
        var viewer = scene?.Instantiate<SarnautCore.AssetViewer>();
        scene?.Dispose();
        if (viewer == null)
        {
            Fail("Asset Viewer scene did not instantiate.");
            return;
        }

        AddChild(viewer);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        string nativeRoot = NativeContentSettings.NativeRoot;
        string? nativeScene = FindFirst(nativeRoot, NativeAssetKind.Scene);
        string? nativeResource = FindFirst(nativeRoot, NativeAssetKind.Resource);
        if (nativeScene == null || nativeResource == null)
        {
            Fail($"Native content needs one scene and one resource beneath {nativeRoot}.");
            return;
        }

        bool sceneLoaded = viewer.PreviewAsset(nativeScene);
        string sceneKind = viewer.CurrentPreviewKind;
        bool resourceLoaded = viewer.PreviewAsset(nativeResource);
        string resourceKind = viewer.CurrentPreviewKind;
        bool outsideRejected = !viewer.PreviewAsset($"{nativeRoot}-outside/probe.scn");
        bool unsupportedRejected = !viewer.PreviewAsset($"{nativeRoot}/manifest.json");

        if (!sceneLoaded || !resourceLoaded || !outsideRejected || !unsupportedRejected)
        {
            Fail(
                $"scene={sceneLoaded} resource={resourceLoaded} "
                + $"outside_rejected={outsideRejected} unsupported_rejected={unsupportedRejected}");
            return;
        }

        GD.Print(
            "ASSET_VIEWER_SMOKE_OK "
            + $"scene={sceneKind} resource={resourceKind} confined=true");
        GetTree().Quit(0);
    }

    private static string? FindFirst(string root, NativeAssetKind expectedKind)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directoryPath))
        {
            using var directory = DirAccess.Open(directoryPath);
            if (directory == null)
            {
                continue;
            }

            var directories = new List<string>();
            directory.ListDirBegin();
            string name = directory.GetNext();
            while (!string.IsNullOrEmpty(name))
            {
                if (!name.StartsWith('.'))
                {
                    string path = $"{directoryPath}/{name}";
                    if (directory.CurrentIsDir())
                    {
                        directories.Add(path);
                    }
                    else if (NativeAssetReference.ExtensionKind(System.IO.Path.GetExtension(name)) == expectedKind)
                    {
                        directory.ListDirEnd();
                        return path;
                    }
                }

                name = directory.GetNext();
            }

            directory.ListDirEnd();
            directories.Sort(StringComparer.OrdinalIgnoreCase);
            for (int index = directories.Count - 1; index >= 0; index--)
            {
                pending.Push(directories[index]);
            }
        }

        return null;
    }

    private void Fail(string message)
    {
        GD.PushError($"ASSET_VIEWER_SMOKE_FAILED {message}");
        GetTree().Quit(1);
    }
}
