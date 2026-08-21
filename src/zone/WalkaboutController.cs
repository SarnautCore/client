using Godot;

namespace SarnautCore;

public partial class WalkaboutController : CharacterBody3D
{
    /// <summary>
    /// The named input actions this controller reads, declared in
    /// <c>project.godot</c>'s <c>[input]</c> section.
    /// </summary>
    /// <remarks>
    /// Movement used to be <c>Input.IsPhysicalKeyPressed(Key.W)</c> and friends.
    /// Physical keycodes are not rebindable, are wrong on every non-QWERTY
    /// layout, and put the key map in three source files instead of the one
    /// place Godot already has for it.
    /// </remarks>
    public const string MoveForward = "move_forward";
    public const string MoveBack = "move_back";
    public const string MoveLeft = "move_left";
    public const string MoveRight = "move_right";
    public const string MoveJump = "move_jump";
    public const string MoveDescend = "move_descend";
    public const string MoveSprint = "move_sprint";
    public const string MoveToggleFly = "move_toggle_fly";
    public const string CameraZoomIn = "camera_zoom_in";
    public const string CameraZoomOut = "camera_zoom_out";

    [Export] public float WalkSpeed { get; set; } = 9.0f;
    [Export] public float FlySpeed { get; set; } = 24.0f;
    [Export] public float FastFlyMultiplier { get; set; } = 3.0f;
    [Export] public float JumpVelocity { get; set; } = 7.0f;
    [Export] public float MouseSensitivity { get; set; } = 0.0022f;
    [Export] public float CameraZoomStep { get; set; } = 0.6f;
    [Export] public float CameraZoomMin { get; set; } = 1.2f;
    [Export] public float CameraZoomMax { get; set; } = 9.0f;
    [Export(PropertyHint.Range, "0.1,1.0,0.05")]
    public float NetworkGroundingDistance { get; set; } = 0.5f;

    private Node3D _head = null!;
    private SpringArm3D? _cameraBoom;
    private CollisionShape3D _collision = null!;
    private CharacterRig? _character;
    private float _gravity;

    public bool IsFlying { get; private set; }

    public bool NetworkControlled { get; set; }

    /// <summary>Whether movement and walkabout commands may read input.</summary>
    public bool InputEnabled { get; set; } = true;

    /// <summary>Whether mouse motion may rotate the camera.</summary>
    public bool LookInputEnabled { get; set; } = true;

    // TODO: add client-side prediction and reconciliation without changing the offline controller path.
    public Vector3 NetworkMoveDirection { get; private set; }

    /// <summary>The latest unmodified position received from the shard.</summary>
    public Vector3 AuthoritativeNetworkPosition { get; private set; }

    /// <summary>Client-only vertical adjustment onto nearby authored support.</summary>
    public float NetworkGroundingAdjustment { get; private set; }

    public override void _Ready()
    {
        _head = GetNode<Node3D>("Head");
        _cameraBoom = _head.GetNodeOrNull<SpringArm3D>("SpringArm3D");
        _collision = GetNode<CollisionShape3D>("CollisionShape3D");
        _character = GetNodeOrNull<CharacterRig>("Character");
        _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8).AsDouble();
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseMotion motion && LookInputEnabled)
        {
            RotateY(-motion.Relative.X * MouseSensitivity);
            Vector3 headRotation = _head.Rotation;
            headRotation.X = Mathf.Clamp(headRotation.X - motion.Relative.Y * MouseSensitivity, -1.52f, 1.52f);
            _head.Rotation = headRotation;
            GetViewport().SetInputAsHandled();
        }
        else if (InputEnabled && !NetworkControlled && inputEvent.IsActionPressed(MoveToggleFly))
        {
            SetFlying(!IsFlying);
            GetViewport().SetInputAsHandled();
        }
        else if (_cameraBoom != null && LookInputEnabled && inputEvent.IsActionPressed(CameraZoomIn))
        {
            ApplyCameraZoom(-CameraZoomStep);
            GetViewport().SetInputAsHandled();
        }
        else if (_cameraBoom != null && LookInputEnabled && inputEvent.IsActionPressed(CameraZoomOut))
        {
            ApplyCameraZoom(CameraZoomStep);
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>The camera boom length after clamping; retail wheels the third-person camera along it.</summary>
    public float CameraZoomDistance => _cameraBoom?.SpringLength ?? 0;

    private void ApplyCameraZoom(float delta)
    {
        _cameraBoom!.SpringLength = Mathf.Clamp(
            _cameraBoom.SpringLength + delta, CameraZoomMin, CameraZoomMax);
    }

    public override void _PhysicsProcess(double delta)
    {
        Vector2 input = InputEnabled ? ReadMovementInput() : Vector2.Zero;
        Vector3 localDirection = new(input.X, 0, input.Y);
        Vector3 worldDirection = (Basis * localDirection).Normalized();
        NetworkMoveDirection = worldDirection;
        _character?.SetMoving(input.LengthSquared() > 0.001f);

        if (NetworkControlled)
        {
            Velocity = Vector3.Zero;
            return;
        }

        if (IsFlying)
        {
            float vertical = Input.GetActionStrength(MoveJump) - Input.GetActionStrength(MoveDescend);
            Vector3 flyDirection = (worldDirection + Vector3.Up * vertical).Normalized();
            float multiplier = Input.IsActionPressed(MoveSprint) ? FastFlyMultiplier : 1.0f;
            Velocity = flyDirection * FlySpeed * multiplier;
        }
        else
        {
            Vector3 velocity = Velocity;
            velocity.X = worldDirection.X * WalkSpeed;
            velocity.Z = worldDirection.Z * WalkSpeed;
            if (!IsOnFloor())
            {
                velocity.Y -= _gravity * (float)delta;
            }
            else if (Input.IsActionPressed(MoveJump))
            {
                velocity.Y = JumpVelocity;
            }

            Velocity = velocity;
        }

        MoveAndSlide();
    }

    public bool PlayAttack() => _character?.PlayAttack() ?? false;

    public bool PlayHit() => _character?.PlayHit() ?? false;

    public bool PlayDeath() => _character?.PlayDeath() ?? false;

    /// <summary>
    /// Applies a shard position while keeping the rendered capsule in contact
    /// with nearby authored support geometry.
    /// </summary>
    /// <remarks>
    /// The server coordinate remains authoritative. This only closes a small
    /// vertical authoring clearance (for example the League start is 0.151 m
    /// above its floor) and never changes the authoritative X/Z position.
    /// </remarks>
    public void ApplyAuthoritativePosition(Vector3 authoritativePosition)
    {
        AuthoritativeNetworkPosition = authoritativePosition;
        NetworkGroundingAdjustment = 0;
        Vector3 displayedPosition = authoritativePosition;

        if (IsInsideTree()
            && _collision is not null
            && _collision.Shape is CapsuleShape3D capsule
            && TryFindNetworkSupport(authoritativePosition, capsule, out float supportY))
        {
            float localFeetOffset = _collision.Position.Y - (capsule.Height * 0.5f * _collision.Scale.Y);
            displayedPosition.Y = supportY - localFeetOffset;
            NetworkGroundingAdjustment = displayedPosition.Y - authoritativePosition.Y;
        }

        Position = displayedPosition;
    }

    // Safe to call outside _PhysicsProcess only because this project keeps
    // physics on the main thread: project.godot does not set
    // physics/3d/run_on_separate_thread (default off), so DirectSpaceState is
    // accessible from _Process-driven callers such as the network loop. If that
    // setting is ever enabled, this query must move to _PhysicsProcess.
    private bool TryFindNetworkSupport(Vector3 authoritativePosition, CapsuleShape3D capsule, out float supportY)
    {
        float localFeetOffset = _collision.Position.Y - (capsule.Height * 0.5f * _collision.Scale.Y);
        float feetY = authoritativePosition.Y + localFeetOffset;
        Vector3 start = new(authoritativePosition.X, feetY + 0.15f, authoritativePosition.Z);
        Vector3 end = new(authoritativePosition.X, feetY - NetworkGroundingDistance, authoritativePosition.Z);
        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(start, end, collisionMask: 1);
        query.Exclude = [GetRid()];
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.TryGetValue("position", out Variant positionValue)
            && hit.TryGetValue("normal", out Variant normalValue)
            && normalValue.AsVector3().Dot(Vector3.Up) >= 0.5f)
        {
            supportY = positionValue.AsVector3().Y;
            return true;
        }

        supportY = 0;
        return false;
    }

    private void SetFlying(bool flying)
    {
        IsFlying = flying;
        _collision.SetDeferred(CollisionShape3D.PropertyName.Disabled, flying);
        Velocity = Vector3.Zero;
        GD.Print($"Walkabout: {(flying ? "fly" : "walk")} mode");
    }

    private static Vector2 ReadMovementInput()
    {
        return Input.GetVector(MoveLeft, MoveRight, MoveForward, MoveBack).LimitLength();
    }
}
