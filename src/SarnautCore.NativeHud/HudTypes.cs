namespace SarnautCore.NativeHud;

/// <summary>A stable identifier authored by the offline HUD bake.</summary>
public readonly struct HudId : IEquatable<HudId>, IComparable<HudId>
{
    private readonly string? _value;

    public HudId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public static HudId Empty => default;

    public string Value => _value ?? string.Empty;

    public bool IsEmpty => _value is null;

    public int CompareTo(HudId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public bool Equals(HudId other) => StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is HudId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    public static bool operator ==(HudId left, HudId right) => left.Equals(right);

    public static bool operator !=(HudId left, HudId right) => !left.Equals(right);
}

/// <summary>Authority order. Epoch, server revision, then order within that revision.</summary>
public readonly record struct HudStamp(uint SourceEpoch, ulong Revision, uint Ordinal) : IComparable<HudStamp>
{
    public int CompareTo(HudStamp other)
    {
        int epoch = SourceEpoch.CompareTo(other.SourceEpoch);
        if (epoch != 0)
        {
            return epoch;
        }

        int revision = Revision.CompareTo(other.Revision);
        return revision != 0 ? revision : Ordinal.CompareTo(other.Ordinal);
    }
}

public readonly record struct HudPoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

public readonly record struct HudViewport(double X, double Y, double Width, double Height)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) &&
        double.IsFinite(Height) && Width > 0 && Height > 0;

    public bool Contains(HudPoint point) => point.IsFinite && point.X >= X && point.Y >= Y &&
        point.X <= X + Width && point.Y <= Y + Height;
}

public readonly record struct HudFrame(long NowMilliseconds, HudViewport Viewport);

public enum HudFeedbackKind
{
    Avatar,
    Enemy,
    Experience,
}

public enum HudFocus
{
    World,
    Hud,
    Chat,
    Modal,
    Drag,
}

public enum HudCursor
{
    Default,
    Hover,
    Text,
    Drag,
}

[Flags]
public enum HudEventFamilies
{
    None = 0,
    ActionSlots = 1 << 0,
    Units = 1 << 1,
    CombatFeedback = 1 << 2,
    QuestTracker = 1 << 3,
    Chat = 1 << 4,
    All = ActionSlots | Units | CombatFeedback | QuestTracker | Chat,
}

[Flags]
public enum HudCommandFamilies
{
    None = 0,
    ActivateAction = 1 << 0,
    SelectWorldEntity = 1 << 1,
    SubmitChat = 1 << 2,
    InteractWorldEntity = 1 << 3,
    All = ActivateAction | SelectWorldEntity | SubmitChat | InteractWorldEntity,
}

public readonly record struct HudSessionCapabilities(HudEventFamilies Events, HudCommandFamilies Commands);

public enum HudSessionState
{
    Open,
    Closed,
    Faulted,
}

public readonly record struct HudSessionRead(int Count, int DroppedCount, HudSessionState State);

public interface IHudSession
{
    HudSessionCapabilities Capabilities { get; }

    HudSessionRead Read(Span<HudEvent> destination);

    bool TryWrite(in HudCommand command);
}

public readonly record struct HudWorldQuery(ulong EntityId, HudViewport Viewport);

public readonly record struct HudProjection(HudPoint Screen, double Depth, bool InFrustum, bool Occluded);

public interface IHudWorld
{
    bool TryProject(in HudWorldQuery query, out HudProjection projection);
}
