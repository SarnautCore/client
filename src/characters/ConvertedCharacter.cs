using System;
using Godot;

namespace SarnautCore;

public partial class ConvertedCharacter : Node3D
{
    public const string DefaultConvertedRoot = "res://converted/assets/classic-1.1";
    public const string DefaultPlayerScene = "Characters/Elf_male/ElfMale.(VisObjectTemplate).scene.tscn";

    [Export(PropertyHint.Dir)] public string ConvertedRoot { get; set; } = DefaultConvertedRoot;
    [Export] public string CharacterScene { get; set; } = DefaultPlayerScene;
    [Export] public bool AutoLoad { get; set; } = true;
    [Export] public bool ShowPlaceholderOnFailure { get; set; } = true;
    [Export] public bool LocomotionOnly { get; set; }
    [Export] public float CrossFadeSeconds { get; set; } = 0.18f;
    [Export] public float ModelYawDegrees { get; set; }

    public bool HasConvertedModel { get; private set; }
    public int SkeletonBoneCount { get; private set; }
    public int ClipCount { get; private set; }
    public string ActiveClip { get; private set; } = string.Empty;
    public string LastError { get; private set; } = string.Empty;
    public Node3D? Model { get; private set; }

    private AnimationPlayer? _animationPlayer;
    private string _idleClip = string.Empty;
    private string _moveClip = string.Empty;
    private string _attackClip = string.Empty;
    private string _hitClip = string.Empty;
    private string _deathClip = string.Empty;
    private string _oneShotClip = string.Empty;
    private bool _moving;
    private bool _dead;

    public override void _Ready()
    {
        if (AutoLoad)
        {
            LoadCharacter();
        }
    }

    public bool LoadCharacter()
    {
        ClearModel();
        PackedScene? scene = ConvertedSceneLoader.Load(
            ConvertedRoot,
            CharacterScene,
            out string error,
            enableRuntimeSkinnedMesh: true,
            locomotionOnly: LocomotionOnly);
        if (scene?.Instantiate() is not Node3D model)
        {
            return Fail(string.IsNullOrEmpty(error) ? $"Converted character scene is not Node3D: {CharacterScene}" : error);
        }

        model.Name = "ConvertedModel";
        model.RotationDegrees = new Vector3(0, ModelYawDegrees, 0);
        AddChild(model);
        Model = model;

        Skeleton3D? skeleton = FindDescendant<Skeleton3D>(model);
        _animationPlayer = FindDescendant<AnimationPlayer>(model);
        SkeletonBoneCount = skeleton?.GetBoneCount() ?? 0;
        ClipCount = CountAnimations(_animationPlayer);
        if (SkeletonBoneCount <= 0 || ClipCount <= 0)
        {
            string details = $"bones={SkeletonBoneCount}, clips={ClipCount}";
            model.QueueFree();
            Model = null;
            return Fail($"Converted character is incomplete ({details}): {CharacterScene}");
        }

        _idleClip = FindAnimation(_animationPlayer!, "idle", "default");
        _moveClip = FindAnimation(_animationPlayer!, "run", "walk");
        _attackClip = FindAnimationContaining(_animationPlayer!, "attack", "battle");
        _hitClip = FindAnimationContaining(_animationPlayer!, "hit", "damage");
        _deathClip = FindAnimationContaining(_animationPlayer!, "death", "dead");
        _animationPlayer!.AnimationFinished += OnAnimationFinished;
        HasConvertedModel = true;
        LastError = string.Empty;
        Play(_idleClip, 0);
        return true;
    }

    public void SetMoving(bool moving)
    {
        if (!HasConvertedModel || _animationPlayer == null || moving == _moving)
        {
            return;
        }

        _moving = moving;
        if (_oneShotClip.Length > 0 || _dead)
        {
            return;
        }

        string requested = moving ? _moveClip : _idleClip;
        Play(requested, CrossFadeSeconds);
    }

    /// <summary>Plays the converted attack clip, then returns to locomotion.</summary>
    public bool PlayAttack() => PlayOneShot(_attackClip, staysLocked: false);

    /// <summary>Plays the converted hit reaction, then returns to locomotion.</summary>
    public bool PlayHit() => PlayOneShot(_hitClip, staysLocked: false);

    /// <summary>Plays and holds the converted death clip.</summary>
    public bool PlayDeath() => PlayOneShot(_deathClip, staysLocked: true);

    private void Play(string clip, float blendSeconds)
    {
        if (_animationPlayer == null || string.IsNullOrEmpty(clip) || ActiveClip == clip)
        {
            return;
        }

        _animationPlayer.Play(clip, blendSeconds);
        ActiveClip = clip;
    }

    private bool PlayOneShot(string clip, bool staysLocked)
    {
        if (!HasConvertedModel || _animationPlayer == null || string.IsNullOrEmpty(clip))
        {
            return false;
        }

        _oneShotClip = clip;
        _dead = staysLocked;
        Play(clip, CrossFadeSeconds);
        return true;
    }

    private void OnAnimationFinished(StringName animationName)
    {
        if (_dead || _oneShotClip.Length == 0 || animationName.ToString() != _oneShotClip)
        {
            return;
        }

        _oneShotClip = string.Empty;
        Play(_moving ? _moveClip : _idleClip, CrossFadeSeconds);
    }

    private bool Fail(string message)
    {
        HasConvertedModel = false;
        SkeletonBoneCount = 0;
        ClipCount = 0;
        ActiveClip = string.Empty;
        LastError = message;
        GD.PushWarning($"ConvertedCharacter: {message}");
        if (ShowPlaceholderOnFailure)
        {
            Model = CreatePlaceholder();
            AddChild(Model);
        }

        return false;
    }

    private void ClearModel()
    {
        if (_animationPlayer != null)
        {
            _animationPlayer.AnimationFinished -= OnAnimationFinished;
        }

        if (Model != null && IsInstanceValid(Model))
        {
            RemoveChild(Model);
            Model.QueueFree();
        }

        Model = null;
        _animationPlayer = null;
        _idleClip = string.Empty;
        _moveClip = string.Empty;
        _attackClip = string.Empty;
        _hitClip = string.Empty;
        _deathClip = string.Empty;
        _oneShotClip = string.Empty;
        _moving = false;
        _dead = false;
        HasConvertedModel = false;
        SkeletonBoneCount = 0;
        ClipCount = 0;
        ActiveClip = string.Empty;
        LastError = string.Empty;
    }

    private static Node3D CreatePlaceholder()
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color("d06a55"),
            Roughness = 0.85f,
        };
        return new MeshInstance3D
        {
            Name = "CharacterPlaceholder",
            Position = new Vector3(0, 0.9f, 0),
            Mesh = new CapsuleMesh { Radius = 0.42f, Height = 1.8f },
            MaterialOverride = material,
        };
    }

    private static int CountAnimations(AnimationPlayer? player)
    {
        if (player == null)
        {
            return 0;
        }

        int count = 0;
        foreach (StringName _ in player.GetAnimationList())
        {
            count++;
        }

        return count;
    }

    private static string FindAnimation(AnimationPlayer player, params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            foreach (StringName animationName in player.GetAnimationList())
            {
                string name = animationName.ToString();
                if (name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return string.Empty;
    }

    private static string FindAnimationContaining(AnimationPlayer player, params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            foreach (StringName animationName in player.GetAnimationList())
            {
                string name = animationName.ToString();
                if (name.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return string.Empty;
    }

    private static T? FindDescendant<T>(Node parent) where T : Node
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }
}
