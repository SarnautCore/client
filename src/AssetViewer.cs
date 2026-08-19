using System;
using System.Collections.Generic;
using Godot;

namespace SarnautCore;

public partial class AssetViewer : Control
{
    private const string ConvertedRoot = "res://converted";
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".tres",
        ".tscn",
        ".skmesh",
    };

    private Tree _tree = null!;
    private Label _info = null!;
    private Label _emptyState = null!;
    private TextureRect _texturePreview = null!;
    private SubViewportContainer _viewportContainer = null!;
    private Node3D _previewRoot = null!;
    private Node3D _orbitPivot = null!;
    private Camera3D _camera = null!;
    private bool _orbiting;
    private Vector2 _lastPointerPosition;
    private float _cameraDistance = 5.0f;

    public string CurrentPreviewKind { get; private set; } = "None";

    public override void _Ready()
    {
        BuildInterface();
        RefreshTree();
    }

    public bool PreviewAsset(string path)
    {
        ClearPreview();
        if (!FileAccess.FileExists(path))
        {
            SetInfo(path, "Missing", 0, "The file no longer exists.");
            return false;
        }

        long size = ReadFileSize(path);
        string extension = System.IO.Path.GetExtension(path);
        try
        {
            switch (extension.ToLowerInvariant())
            {
                case ".png":
                    return PreviewTexture(path, size);
                case ".skmesh":
                    return PreviewSkinMesh(path, size);
                case ".tscn":
                    return PreviewScene(path, size);
                case ".tres":
                    return PreviewResource(path, size);
                default:
                    SetInfo(path, "Unsupported", size, "No preview is available for this file type.");
                    return false;
            }
        }
        catch (Exception exception)
        {
            GD.PushError($"Asset Viewer could not load {path}: {exception.Message}");
            SetInfo(path, "Load error", size, exception.Message);
            return false;
        }
    }

    private void BuildInterface()
    {
        var backdrop = new ColorRect
        {
            Color = new Color("0d1118"),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        var page = new VBoxContainer();
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        page.OffsetLeft = 12;
        page.OffsetTop = 12;
        page.OffsetRight = -12;
        page.OffsetBottom = -12;
        page.AddThemeConstantOverride("separation", 10);
        AddChild(page);

        var toolbar = new HBoxContainer();
        toolbar.AddThemeConstantOverride("separation", 8);
        page.AddChild(toolbar);

        var back = new Button { Text = "Back" };
        back.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/boot.tscn");
        toolbar.AddChild(back);

        var heading = new Label
        {
            Text = "Asset Viewer",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        heading.AddThemeFontSizeOverride("font_size", 22);
        toolbar.AddChild(heading);

        var refresh = new Button { Text = "Refresh converted/" };
        refresh.Pressed += RefreshTree;
        toolbar.AddChild(refresh);

        var split = new HSplitContainer
        {
            SplitOffsets = [330],
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        page.AddChild(split);

        var browserPanel = new PanelContainer { CustomMinimumSize = new Vector2(300, 0) };
        split.AddChild(browserPanel);

        var browser = new VBoxContainer();
        browser.AddThemeConstantOverride("separation", 8);
        browserPanel.AddChild(browser);

        var browserTitle = new Label { Text = "converted/" };
        browserTitle.AddThemeFontSizeOverride("font_size", 16);
        browser.AddChild(browserTitle);

        _tree = new Tree
        {
            Columns = 1,
            HideRoot = false,
            AllowReselect = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _tree.ItemActivated += OnTreeItemActivated;
        browser.AddChild(_tree);

        var previewPanel = new PanelContainer();
        split.AddChild(previewPanel);

        var previewColumn = new VBoxContainer();
        previewColumn.AddThemeConstantOverride("separation", 8);
        previewPanel.AddChild(previewColumn);

        _info = new Label
        {
            Text = "Select a converted asset.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 52),
        };
        previewColumn.AddChild(_info);

        var previewHost = new Control
        {
            ClipContents = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        previewColumn.AddChild(previewHost);

        _emptyState = new Label
        {
            Text = "Choose a .png, .tres, .tscn, or .skmesh file.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Modulate = new Color("8d99a8"),
        };
        _emptyState.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        previewHost.AddChild(_emptyState);

        _texturePreview = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Visible = false,
        };
        _texturePreview.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        previewHost.AddChild(_texturePreview);

        BuildThreeDimensionalPreview(previewHost);
    }

    private void BuildThreeDimensionalPreview(Control previewHost)
    {
        _viewportContainer = new SubViewportContainer
        {
            Stretch = true,
            Visible = false,
        };
        _viewportContainer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _viewportContainer.GuiInput += OnViewportInput;
        previewHost.AddChild(_viewportContainer);

        var viewport = new SubViewport
        {
            OwnWorld3D = true,
            Size = new Vector2I(900, 650),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        _viewportContainer.AddChild(viewport);

        var world = new Node3D { Name = "PreviewWorld" };
        viewport.AddChild(world);

        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color("171d26"),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("c8d5e3"),
            AmbientLightEnergy = 0.65f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
        world.AddChild(new WorldEnvironment { Environment = environment });

        var keyLight = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-45, -35, 0),
            LightEnergy = 1.2f,
            ShadowEnabled = true,
        };
        world.AddChild(keyLight);

        _previewRoot = new Node3D { Name = "Asset" };
        world.AddChild(_previewRoot);

        _orbitPivot = new Node3D
        {
            Name = "OrbitPivot",
            Rotation = new Vector3(Mathf.DegToRad(-18), Mathf.DegToRad(28), 0),
        };
        world.AddChild(_orbitPivot);

        _camera = new Camera3D
        {
            Current = true,
            Fov = 45,
            Near = 0.01f,
            Far = 5000,
        };
        _orbitPivot.AddChild(_camera);
        UpdateCameraDistance();
    }

    private void RefreshTree()
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        root.SetText(0, "converted");
        root.SetTooltipText(0, ConvertedRoot);
        root.Collapsed = false;

        int fileCount = PopulateDirectory(ConvertedRoot, root);
        if (fileCount == 0)
        {
            var empty = _tree.CreateItem(root);
            empty.SetText(0, "No supported assets found");
            empty.SetCustomColor(0, new Color("8d99a8"));
        }
    }

    private static int PopulateDirectory(string directoryPath, TreeItem parent)
    {
        using var directory = DirAccess.Open(directoryPath);
        if (directory == null)
        {
            return 0;
        }

        var directoryNames = new List<string>();
        var fileNames = new List<string>();
        directory.ListDirBegin();
        string name = directory.GetNext();
        while (!string.IsNullOrEmpty(name))
        {
            if (!name.StartsWith('.'))
            {
                if (directory.CurrentIsDir())
                {
                    directoryNames.Add(name);
                }
                else if (SupportedExtensions.Contains(System.IO.Path.GetExtension(name)))
                {
                    fileNames.Add(name);
                }
            }

            name = directory.GetNext();
        }

        directory.ListDirEnd();
        directoryNames.Sort(StringComparer.OrdinalIgnoreCase);
        fileNames.Sort(StringComparer.OrdinalIgnoreCase);

        int count = 0;
        foreach (string directoryName in directoryNames)
        {
            string childPath = $"{directoryPath}/{directoryName}";
            var child = parent.CreateChild();
            child.SetText(0, directoryName);
            child.SetTooltipText(0, childPath);
            child.Collapsed = true;
            count += PopulateDirectory(childPath, child);
            if (child.GetChildCount() == 0)
            {
                child.Free();
            }
        }

        foreach (string fileName in fileNames)
        {
            string filePath = $"{directoryPath}/{fileName}";
            var item = parent.CreateChild();
            item.SetText(0, fileName);
            item.SetTooltipText(0, filePath);
            item.SetMetadata(0, filePath);
            count++;
        }

        return count;
    }

    private void OnTreeItemActivated()
    {
        TreeItem? selected = _tree.GetSelected();
        if (selected == null)
        {
            return;
        }

        string path = selected.GetMetadata(0).AsString();
        if (!string.IsNullOrEmpty(path))
        {
            PreviewAsset(path);
        }
    }

    private bool PreviewTexture(string path, long size)
    {
        Texture2D? texture = ResourceLoader.Load<Texture2D>(path);
        if (texture == null)
        {
            SetInfo(path, "Texture load error", size, "Godot could not load the texture.");
            return false;
        }

        _texturePreview.Texture = texture;
        _texturePreview.Visible = true;
        _emptyState.Visible = false;
        CurrentPreviewKind = "Texture2D";
        SetInfo(path, CurrentPreviewKind, size, $"{texture.GetWidth()} x {texture.GetHeight()} px");
        return true;
    }

    private bool PreviewSkinMesh(string path, long size)
    {
        ArrayMesh? mesh = ConvertedSkinnedMesh.LoadPreviewMesh(path);
        if (mesh == null || mesh.GetSurfaceCount() == 0)
        {
            SetInfo(path, "Mesh load error", size, "The .skmesh file has no readable surfaces.");
            return false;
        }

        return ShowMesh(path, size, mesh, "Converted skinned mesh");
    }

    private bool PreviewScene(string path, long size)
    {
        PackedScene? scene = ResourceLoader.Load<PackedScene>(path);
        Node? instance = scene?.Instantiate();
        if (instance is not Node3D node3D)
        {
            instance?.QueueFree();
            SetInfo(path, "Scene", size, "Only scenes with a Node3D root have a visual preview.");
            return false;
        }

        DisableImportedCameras(node3D);
        ShowNode3D(node3D);
        CurrentPreviewKind = "3D scene";
        SetInfo(path, CurrentPreviewKind, size, $"Root: {node3D.GetClass()}");
        return true;
    }

    private bool PreviewResource(string path, long size)
    {
        Resource? resource = ResourceLoader.Load(path);
        switch (resource)
        {
            case Texture2D texture:
                _texturePreview.Texture = texture;
                _texturePreview.Visible = true;
                _emptyState.Visible = false;
                CurrentPreviewKind = "Texture2D resource";
                SetInfo(path, CurrentPreviewKind, size, $"{texture.GetWidth()} x {texture.GetHeight()} px");
                return true;
            case Mesh mesh:
                return ShowMesh(path, size, mesh, "Mesh resource");
            case null:
                SetInfo(path, "Resource load error", size, "Godot could not load the resource.");
                return false;
            default:
                CurrentPreviewKind = resource.GetClass();
                SetInfo(path, CurrentPreviewKind, size, "Loaded resource. This type has no visual preview.");
                return true;
        }
    }

    private bool ShowMesh(string path, long size, Mesh mesh, string kind)
    {
        var instance = new MeshInstance3D { Mesh = mesh };
        ShowNode3D(instance);
        CurrentPreviewKind = kind;
        SetInfo(path, kind, size, $"{mesh.GetSurfaceCount()} surface(s)");
        return true;
    }

    private void ShowNode3D(Node3D node)
    {
        _previewRoot.AddChild(node);
        _previewRoot.Position = Vector3.Zero;
        FramePreview();
        _viewportContainer.Visible = true;
        _emptyState.Visible = false;
    }

    private void ClearPreview()
    {
        CurrentPreviewKind = "None";
        _texturePreview.Texture = null;
        _texturePreview.Visible = false;
        _viewportContainer.Visible = false;
        _emptyState.Visible = true;
        _previewRoot.Position = Vector3.Zero;
        foreach (Node child in _previewRoot.GetChildren())
        {
            _previewRoot.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void FramePreview()
    {
        bool hasBounds = false;
        Aabb bounds = default;
        foreach (MeshInstance3D meshInstance in FindMeshInstances(_previewRoot))
        {
            Aabb local = meshInstance.GetAabb();
            for (int corner = 0; corner < 8; corner++)
            {
                var point = local.Position + new Vector3(
                    (corner & 1) == 0 ? 0 : local.Size.X,
                    (corner & 2) == 0 ? 0 : local.Size.Y,
                    (corner & 4) == 0 ? 0 : local.Size.Z);
                Vector3 worldPoint = meshInstance.GlobalTransform * point;
                if (!hasBounds)
                {
                    bounds = new Aabb(worldPoint, Vector3.Zero);
                    hasBounds = true;
                }
                else
                {
                    bounds = bounds.Expand(worldPoint);
                }
            }
        }

        if (!hasBounds)
        {
            _cameraDistance = 5.0f;
            UpdateCameraDistance();
            return;
        }

        _previewRoot.Position = -bounds.GetCenter();
        float largestSide = Mathf.Max(bounds.Size.X, Mathf.Max(bounds.Size.Y, bounds.Size.Z));
        _cameraDistance = Mathf.Max(1.5f, largestSide * 1.8f);
        _camera.Near = Mathf.Max(0.005f, _cameraDistance / 2000.0f);
        _camera.Far = Mathf.Max(5000.0f, _cameraDistance * 20.0f);
        UpdateCameraDistance();
    }

    private static IEnumerable<MeshInstance3D> FindMeshInstances(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is MeshInstance3D meshInstance && meshInstance.Mesh != null)
            {
                yield return meshInstance;
            }

            foreach (MeshInstance3D descendant in FindMeshInstances(child))
            {
                yield return descendant;
            }
        }
    }

    private static void DisableImportedCameras(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is Camera3D camera)
            {
                camera.Current = false;
            }

            DisableImportedCameras(child);
        }
    }

    private void OnViewportInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton button)
        {
            if (button.ButtonIndex == MouseButton.Left)
            {
                _orbiting = button.Pressed;
                _lastPointerPosition = button.Position;
            }
            else if (button.Pressed && button.ButtonIndex == MouseButton.WheelUp)
            {
                _cameraDistance = Mathf.Max(0.1f, _cameraDistance * 0.88f);
                UpdateCameraDistance();
            }
            else if (button.Pressed && button.ButtonIndex == MouseButton.WheelDown)
            {
                _cameraDistance = Mathf.Min(10000.0f, _cameraDistance * 1.14f);
                UpdateCameraDistance();
            }
        }
        else if (inputEvent is InputEventMouseMotion motion && _orbiting)
        {
            Vector2 delta = motion.Position - _lastPointerPosition;
            _lastPointerPosition = motion.Position;
            _orbitPivot.RotateY(-delta.X * 0.008f);
            Vector3 rotation = _orbitPivot.Rotation;
            rotation.X = Mathf.Clamp(rotation.X - delta.Y * 0.008f, -1.45f, 1.45f);
            _orbitPivot.Rotation = rotation;
        }
    }

    private void UpdateCameraDistance()
    {
        _camera.Position = new Vector3(0, 0, _cameraDistance);
    }

    private void SetInfo(string path, string type, long size, string detail)
    {
        _info.Text = $"{path}\n{FormatBytes(size)} | {type} | {detail}";
    }

    private static long ReadFileSize(string path)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        return (long)(file?.GetLength() ?? 0UL);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.0} KiB";
        }

        return $"{bytes / (1024.0 * 1024.0):0.0} MiB";
    }
}
