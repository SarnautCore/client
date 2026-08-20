using Godot;

namespace SarnautCore;

/// <summary>
/// A 3D character preview inside a Control: a <see cref="SubViewport"/> with its
/// own world, lit, framed, and orbited by dragging.
/// </summary>
/// <remarks>
/// The same arrangement <c>AssetViewer</c> uses, extracted so the creation screen
/// does not grow a second copy of it. The model is the converted rig named by the
/// chargen option when the converted tree is present; when it is not — a fresh
/// clone, or CI — the preview shows a placeholder and says so, because the
/// screen still has to work.
/// </remarks>
public partial class CharacterPreview : SubViewportContainer
{
    private SubViewport _viewport = null!;
    private Node3D _stage = null!;
    private Node3D _orbitPivot = null!;
    private Camera3D _camera = null!;
    private Label _caption = null!;
    private bool _orbiting;
    private Vector2 _lastPointerPosition;
    private float _cameraDistance = 3.2f;

    /// <summary>Whether the last <see cref="ShowOption"/> found a converted model.</summary>
    public bool HasConvertedModel { get; private set; }

    /// <summary>What the preview is currently showing, for a caption.</summary>
    public string Description { get; private set; } = "No character selected.";

    public override void _Ready()
    {
        Stretch = true;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        GuiInput += OnGuiInput;

        _viewport = new SubViewport
        {
            Name = "PreviewViewport",
            OwnWorld3D = true,
            Size = new Vector2I(420, 620),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            TransparentBg = true,
        };
        AddChild(_viewport);

        var world = new Node3D { Name = "PreviewWorld" };
        _viewport.AddChild(world);
        world.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color("141a24"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color("c8d5e3"),
                AmbientLightEnergy = 0.7f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });
        world.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-35, -140, 0),
            LightEnergy = 1.25f,
            ShadowEnabled = false,
        });

        _stage = new Node3D { Name = "Stage" };
        world.AddChild(_stage);

        _orbitPivot = new Node3D
        {
            Name = "OrbitPivot",
            Position = new Vector3(0, 1.0f, 0),
            Rotation = new Vector3(Mathf.DegToRad(-8), Mathf.DegToRad(20), 0),
        };
        world.AddChild(_orbitPivot);

        _camera = new Camera3D { Current = true, Fov = 42, Near = 0.01f, Far = 200 };
        _orbitPivot.AddChild(_camera);
        UpdateCameraDistance();

        _caption = new Label
        {
            Name = "Caption",
            Text = Description,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _caption.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _caption.AddThemeColorOverride("font_color", UiTheme.MutedInk);
        AddChild(_caption);
    }

    /// <summary>
    /// Shows the model for one chargen option.
    /// </summary>
    /// <param name="visualRef">
    /// The option's <c>visual_ref</c>, which names a pack visual rather than a
    /// converted path. Until that mapping exists the preview shows the default
    /// converted rig and captions the reference it was asked for, so the screen
    /// never claims to be showing something it is not.
    /// </param>
    public void ShowOption(string visualRef, string title)
    {
        Clear();
        var character = new ConvertedCharacter
        {
            Name = "PreviewCharacter",
            AutoLoad = false,
            LocomotionOnly = true,
            ShowPlaceholderOnFailure = true,
            ModelYawDegrees = 180,
        };
        _stage.AddChild(character);
        HasConvertedModel = character.LoadCharacter();
        Description = HasConvertedModel
            ? $"{title} · {visualRef}"
            : $"{title} · {visualRef} · placeholder (no converted model)";
        _caption.Text = HasConvertedModel ? string.Empty : Description;
        FrameStage();
    }

    /// <summary>Empties the stage.</summary>
    public void Clear()
    {
        foreach (Node child in _stage.GetChildren())
        {
            _stage.RemoveChild(child);
            child.QueueFree();
        }

        HasConvertedModel = false;
    }

    private void FrameStage()
    {
        bool hasBounds = false;
        Aabb bounds = default;
        foreach (MeshInstance3D mesh in FindMeshes(_stage))
        {
            Aabb local = mesh.GetAabb();
            Aabb worldBounds = mesh.GlobalTransform * local;
            bounds = hasBounds ? bounds.Merge(worldBounds) : worldBounds;
            hasBounds = true;
        }

        if (!hasBounds)
        {
            _cameraDistance = 3.2f;
            UpdateCameraDistance();
            return;
        }

        Vector3 centre = bounds.GetCenter();
        _orbitPivot.Position = new Vector3(centre.X, centre.Y, centre.Z);
        float tallest = Mathf.Max(bounds.Size.X, Mathf.Max(bounds.Size.Y, bounds.Size.Z));
        _cameraDistance = Mathf.Max(1.6f, tallest * 1.7f);
        UpdateCameraDistance();
    }

    private static System.Collections.Generic.IEnumerable<MeshInstance3D> FindMeshes(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is MeshInstance3D mesh && mesh.Mesh != null)
            {
                yield return mesh;
            }

            foreach (MeshInstance3D descendant in FindMeshes(child))
            {
                yield return descendant;
            }
        }
    }

    private void OnGuiInput(InputEvent inputEvent)
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
                _cameraDistance = Mathf.Max(0.8f, _cameraDistance * 0.9f);
                UpdateCameraDistance();
            }
            else if (button.Pressed && button.ButtonIndex == MouseButton.WheelDown)
            {
                _cameraDistance = Mathf.Min(40.0f, _cameraDistance * 1.12f);
                UpdateCameraDistance();
            }
        }
        else if (inputEvent is InputEventMouseMotion motion && _orbiting)
        {
            Vector2 delta = motion.Position - _lastPointerPosition;
            _lastPointerPosition = motion.Position;
            _orbitPivot.RotateY(-delta.X * 0.008f);
            Vector3 rotation = _orbitPivot.Rotation;
            rotation.X = Mathf.Clamp(rotation.X - delta.Y * 0.006f, -0.9f, 0.9f);
            _orbitPivot.Rotation = rotation;
        }
    }

    private void UpdateCameraDistance() => _camera.Position = new Vector3(0, 0, _cameraDistance);
}
