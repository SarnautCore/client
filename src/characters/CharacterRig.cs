using System;
using Godot;

namespace SarnautCore;

/// <summary>The runtime controls every playable or replicated character through this contract.</summary>
public interface ICharacterRig
{
    string ScenePath { get; set; }
    bool HasModel { get; }
    bool IsAnimationPlaying { get; }
    string ActiveClip { get; }
    string LastError { get; }
    Node3D? Model { get; }
    bool Load();
    void SetMoving(bool moving);
    bool PlayAttack();
    bool PlayHit();
    bool PlayDeath();
}

/// <summary>Loads one native character scene and owns its locomotion and one-shot clips.</summary>
public partial class CharacterRig : Node3D, ICharacterRig
{
    [Export(PropertyHint.File, "*.tscn")] public string ScenePath { get; set; } = string.Empty;
    [Export] public bool AutoLoad { get; set; } = true;
    [Export] public bool ShowPlaceholderOnFailure { get; set; } = true;
    [Export] public float CrossFadeSeconds { get; set; } = 0.18f;
    [Export] public float ModelYawDegrees { get; set; }

    public bool HasModel { get; private set; }
    public int SkeletonBoneCount { get; private set; }
    public int ClipCount { get; private set; }
    public string ActiveClip { get; private set; } = string.Empty;
    public bool IsAnimationPlaying => _animationPlayer?.IsPlaying() == true;
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
            Load();
        }
    }

    public bool Load()
    {
        ClearModel();
        PackedScene? scene = ResourceLoader.Load<PackedScene>(ScenePath);
        if (scene?.Instantiate() is not Node3D model)
        {
            return Fail($"Native character scene is unavailable or is not Node3D: {ScenePath}");
        }

        model.Name = "NativeModel";
        model.RotationDegrees = new Vector3(0, ModelYawDegrees, 0);
        AddChild(model);
        Model = model;

        Skeleton3D? skeleton = FindDescendant<Skeleton3D>(model);
        _animationPlayer = FindDescendant<AnimationPlayer>(model);
        SkeletonBoneCount = skeleton?.GetBoneCount() ?? 0;
        ClipCount = _animationPlayer?.GetAnimationList().Length ?? 0;
        if (SkeletonBoneCount <= 0 || ClipCount <= 0)
        {
            string details = $"bones={SkeletonBoneCount}, clips={ClipCount}";
            RemoveChild(model);
            model.QueueFree();
            Model = null;
            return Fail($"Native character is incomplete ({details}): {ScenePath}");
        }

        _idleClip = FindAnimation(_animationPlayer!, "idle", "default");
        _moveClip = FindAnimation(_animationPlayer!, "run", "walk");
        _attackClip = FindAnimationContaining(_animationPlayer!, "attack", "battle");
        _hitClip = FindAnimationContaining(_animationPlayer!, "hit", "damage");
        _deathClip = FindAnimationContaining(_animationPlayer!, "death", "dead");
        _animationPlayer!.AnimationFinished += OnAnimationFinished;
        HasModel = true;
        LastError = string.Empty;
        DynamicEntityLighting.MarkReceivers(model);
        EnsureSampledLight();
        Play(_idleClip, 0);
        return true;
    }

    public void SetMoving(bool moving)
    {
        if (!HasModel || _animationPlayer == null || moving == _moving)
        {
            return;
        }

        _moving = moving;
        if (_oneShotClip.Length > 0 || _dead)
        {
            return;
        }

        Play(moving ? _moveClip : _idleClip, CrossFadeSeconds);
    }

    public bool PlayAttack() => PlayOneShot(_attackClip, staysLocked: false);
    public bool PlayHit() => PlayOneShot(_hitClip, staysLocked: false);
    public bool PlayDeath() => PlayOneShot(_deathClip, staysLocked: true);

    private void EnsureSampledLight()
    {
        if (GetNodeOrNull<SampledEntityLight>("SampledBakedLight") == null)
        {
            AddChild(new SampledEntityLight());
        }
    }

    private void Play(string clip, float blendSeconds)
    {
        if (_animationPlayer == null || clip.Length == 0 || ActiveClip == clip)
        {
            return;
        }

        _animationPlayer.Play(clip, blendSeconds);
        ActiveClip = clip;
    }

    private bool PlayOneShot(string clip, bool staysLocked)
    {
        if (!HasModel || _animationPlayer == null || clip.Length == 0)
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
        _animationPlayer = null;
        HasModel = false;
        SkeletonBoneCount = 0;
        ClipCount = 0;
        ActiveClip = string.Empty;
        LastError = message;
        GD.PushWarning($"CharacterRig: {message}");
        if (ShowPlaceholderOnFailure)
        {
            Model = CreatePlaceholder();
            AddChild(Model);
            DynamicEntityLighting.MarkReceivers(Model);
            EnsureSampledLight();
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
        HasModel = false;
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
