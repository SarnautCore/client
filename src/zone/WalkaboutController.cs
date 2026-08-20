using Godot;

namespace SarnautCore;

public partial class WalkaboutController : CharacterBody3D
{
    [Export] public float WalkSpeed { get; set; } = 9.0f;
    [Export] public float FlySpeed { get; set; } = 24.0f;
    [Export] public float FastFlyMultiplier { get; set; } = 3.0f;
    [Export] public float JumpVelocity { get; set; } = 7.0f;
    [Export] public float MouseSensitivity { get; set; } = 0.0022f;

    private Node3D _head = null!;
    private CollisionShape3D _collision = null!;
    private ConvertedCharacter? _character;
    private float _gravity;

    public bool IsFlying { get; private set; }

    public bool NetworkControlled { get; set; }

    // TODO: add client-side prediction and reconciliation without changing the offline controller path.
    public Vector3 NetworkMoveDirection { get; private set; }

    public override void _Ready()
    {
        _head = GetNode<Node3D>("Head");
        _collision = GetNode<CollisionShape3D>("CollisionShape3D");
        _character = GetNodeOrNull<ConvertedCharacter>("Character");
        _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8).AsDouble();
        if (DisplayServer.GetName() != "headless")
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(-motion.Relative.X * MouseSensitivity);
            Vector3 headRotation = _head.Rotation;
            headRotation.X = Mathf.Clamp(headRotation.X - motion.Relative.Y * MouseSensitivity, -1.52f, 1.52f);
            _head.Rotation = headRotation;
            GetViewport().SetInputAsHandled();
        }
        else if (inputEvent is InputEventMouseButton button && button.Pressed && button.ButtonIndex == MouseButton.Left)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
        else if (!NetworkControlled && inputEvent is InputEventKey key && key.Pressed && !key.Echo && MatchesKey(key, Key.F))
        {
            SetFlying(!IsFlying);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        Vector2 input = ReadMovementInput();
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
            float vertical = IsKeyDown(Key.Space) ? 1.0f : 0.0f;
            if (IsKeyDown(Key.Q) || IsKeyDown(Key.Ctrl))
            {
                vertical -= 1.0f;
            }

            Vector3 flyDirection = (worldDirection + Vector3.Up * vertical).Normalized();
            float multiplier = IsKeyDown(Key.Shift) ? FastFlyMultiplier : 1.0f;
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
            else if (IsKeyDown(Key.Space))
            {
                velocity.Y = JumpVelocity;
            }

            Velocity = velocity;
        }

        MoveAndSlide();
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
        float x = (IsKeyDown(Key.D) ? 1.0f : 0.0f) - (IsKeyDown(Key.A) ? 1.0f : 0.0f);
        float y = (IsKeyDown(Key.S) ? 1.0f : 0.0f) - (IsKeyDown(Key.W) ? 1.0f : 0.0f);
        return new Vector2(x, y).LimitLength();
    }

    private static bool IsKeyDown(Key key)
    {
        return Input.IsPhysicalKeyPressed(key) || Input.IsKeyPressed(key);
    }

    private static bool MatchesKey(InputEventKey input, Key key)
    {
        return input.PhysicalKeycode == key || input.Keycode == key;
    }
}
