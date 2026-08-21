using System.Collections.Immutable;

namespace SarnautCore.UI;

public enum OptionsCloseDirective
{
    StayOpen,
    Accepted,
    Cancelled,
}

public enum OptionsOutcome
{
    Opened,
    Changed,
    NoChange,
    AwaitingCapture,
    AwaitingConflictConfirmation,
    AwaitingDefaultsConfirmation,
    Applied,
    CloseRequested,
    Rejected,
    Failed,
}

public enum OptionsIssueCode
{
    NotOpen,
    AlreadyClosed,
    StoreReadFailed,
    StoreCommitFailed,
    ConcurrentStoreChange,
    SettingsReadFailed,
    SettingsPrepareFailed,
    SettingsCommitFailed,
    InputPrepareFailed,
    InputCommitFailed,
    AdapterContractViolation,
    RollbackContractViolation,
    UnknownStoredOption,
    InvalidStoredOption,
    UnknownStoredBinding,
    DuplicateStoredBinding,
    UnknownOption,
    UnknownPage,
    InvalidValue,
    UnsupportedAction,
    AudioPreviewFailed,
    AudioRollbackFailed,
    InvalidBinding,
    InvalidBindingSlot,
    CaptureNotActive,
    ConflictNotPending,
    DefaultsConfirmationNotPending,
    ModalOperationActive,
    Disposed,
}

public enum OptionsIssueSeverity
{
    Warning,
    Error,
    Fatal,
}

public readonly record struct OptionsIssue(
    OptionsIssueCode Code,
    OptionsIssueSeverity Severity,
    string? RelatedId = null);

public sealed record OptionsTransition(
    OptionsOutcome Outcome,
    OptionsCloseDirective Close,
    OptionsView? View,
    ImmutableArray<OptionsIssue> Issues)
{
    public bool Succeeded => Outcome is not (OptionsOutcome.Rejected or OptionsOutcome.Failed);
}

public enum OptionsScopeKind
{
    All,
    Page,
    Bindings,
}

public readonly record struct OptionsScope
{
    private OptionsScope(OptionsScopeKind kind, string? page)
    {
        Kind = kind;
        Page = page;
    }

    public OptionsScopeKind Kind { get; }
    public string? Page { get; }
    public static OptionsScope All { get; } = new(OptionsScopeKind.All, null);
    public static OptionsScope Bindings { get; } = new(OptionsScopeKind.Bindings, null);

    public static OptionsScope ForPage(string page)
    {
        ArgumentException.ThrowIfNullOrEmpty(page);
        return new OptionsScope(OptionsScopeKind.Page, page);
    }
}

public enum BindingSlot
{
    Primary,
    Secondary,
}

public readonly record struct InputChord
{
    public InputChord(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Token = token.Trim().ToUpperInvariant();
    }

    public string Token { get; }
    public override string ToString() => Token;
}

public readonly record struct BindingLocation(string Binding, BindingSlot Slot);

public sealed record BindingConflictView(
    BindingLocation Target,
    InputChord Candidate,
    ImmutableArray<BindingLocation> Occupants);

public sealed record BindingCaptureView(BindingLocation Target);

public sealed record DefaultsConfirmationView(OptionsScope Scope);

public sealed record OptionRowView(
    OptionDefinition Definition,
    OptionScalar Applied,
    OptionScalar Draft,
    OptionScalar Default,
    ImmutableArray<OptionScalar> Choices,
    bool Dirty);

public sealed record BindingRowView(
    BindingDefinition Definition,
    InputChord? AppliedPrimary,
    InputChord? AppliedSecondary,
    InputChord? DraftPrimary,
    InputChord? DraftSecondary,
    InputChord? DefaultPrimary,
    InputChord? DefaultSecondary,
    bool Dirty);

public sealed record OptionsView(
    long Revision,
    string ActivePage,
    ImmutableArray<OptionsPageDefinition> Pages,
    ImmutableArray<OptionRowView> Options,
    ImmutableArray<BindingSectionDefinition> BindingSections,
    ImmutableArray<BindingRowView> Bindings,
    BindingCaptureView? Capture,
    BindingConflictView? Conflict,
    DefaultsConfirmationView? DefaultsConfirmation,
    bool IsDirty,
    bool CanApply,
    ImmutableArray<OptionsIssue> Warnings);

public readonly record struct ChatBubbleSettings(bool Show, int Opacity);

public sealed class ChatBubbleSettingsChangedEventArgs(ChatBubbleSettings current) : EventArgs
{
    public ChatBubbleSettings Current { get; } = current;
}

public interface IChatBubbleSettingsSource
{
    ChatBubbleSettings CurrentChatBubbleSettings { get; }
    event EventHandler<ChatBubbleSettingsChangedEventArgs>? ChatBubbleSettingsChanged;
}

public static class OptionsPersistenceLayout
{
    public const string Global = "global.cfg";
    public const string User = "user.cfg";
    public const string KeyBindings = "key_bindings";
}

public abstract record OptionsCommand
{
    private OptionsCommand()
    {
    }

    public sealed record SelectPage(string Page) : OptionsCommand;
    public sealed record SetOption(string Option, OptionScalar Value) : OptionsCommand;
    public sealed record ActivateOption(string Option) : OptionsCommand;
    public sealed record BeginBindingCapture(string Binding, BindingSlot Slot) : OptionsCommand;
    public sealed record OfferBinding(InputChord Chord) : OptionsCommand;
    public sealed record ClearBinding(string Binding, BindingSlot Slot) : OptionsCommand;
    public sealed record ResolveBindingConflict(bool ConfirmReplacement) : OptionsCommand;
    public sealed record RequestDefaults(OptionsScope Scope) : OptionsCommand;
    public sealed record ResolveDefaults(bool Confirm) : OptionsCommand;
    public sealed record ResetDraft(OptionsScope Scope) : OptionsCommand;
    public sealed record Apply : OptionsCommand;
    public sealed record Accept : OptionsCommand;
    public sealed record Cancel : OptionsCommand;
    public sealed record Escape : OptionsCommand;
}
