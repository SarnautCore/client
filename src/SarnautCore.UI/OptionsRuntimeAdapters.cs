using System.Collections.Immutable;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SarnautCore.UI.Tests")]
[assembly: InternalsVisibleTo("SarnautCore")]

namespace SarnautCore.UI;

internal readonly record struct OptionsAdapterResult<T>(T? Value, OptionsIssueCode? Error)
{
    public bool Succeeded => Error is null;
    public static OptionsAdapterResult<T> Success(T value) => new(value, null);
    public static OptionsAdapterResult<T> Failure(OptionsIssueCode error) => new(default, error);
}

internal sealed record OptionsSettingsSnapshot(
    ImmutableDictionary<string, OptionScalar> Current,
    ImmutableDictionary<string, OptionScalar> Defaults,
    ImmutableDictionary<string, ImmutableArray<OptionScalar>> DynamicChoices);

internal interface IPreparedOptionsApply : IDisposable
{
    void Commit();
}

internal interface IOptionsSettingsAdapter
{
    OptionsAdapterResult<OptionsSettingsSnapshot> Read(OptionsProduct product);

    OptionsAdapterResult<ImmutableDictionary<string, OptionScalar>> Autodetect(
        OptionsProduct product);

    OptionsAdapterResult<IPreparedOptionsApply> PrepareApply(
        ImmutableDictionary<string, OptionScalar> values);
}

internal interface IOptionsAudioAdapter
{
    OptionsAdapterResult<bool> Preview(ImmutableDictionary<string, OptionScalar> values);
    OptionsAdapterResult<bool> Restore(ImmutableDictionary<string, OptionScalar> values);
}

internal interface IOptionsInputAdapter
{
    OptionsAdapterResult<InputChord> Validate(InputChord chord);

    OptionsAdapterResult<IPreparedOptionsApply> PrepareApply(
        ImmutableDictionary<string, BindingPair> bindings);
}

internal interface IOptionsPersistenceAdapter
{
    OptionsAdapterResult<OptionsStoredState> Load();
    OptionsAdapterResult<bool> Commit(OptionsStoredState state);
}

internal readonly record struct BindingPair(InputChord? Primary, InputChord? Secondary)
{
    public InputChord? this[BindingSlot slot] => slot == BindingSlot.Primary ? Primary : Secondary;

    public BindingPair With(BindingSlot slot, InputChord? chord) => slot == BindingSlot.Primary
        ? this with { Primary = chord }
        : this with { Secondary = chord };
}

internal sealed record OptionsStoredState(
    long Revision,
    ImmutableDictionary<string, OptionScalar> Global,
    ImmutableDictionary<string, OptionScalar> User,
    ImmutableDictionary<string, BindingPair> Bindings);

internal sealed class RecordingOptionsAdapters
{
    public RecordingOptionsAdapters()
    {
        Settings = new RecordingSettingsAdapter();
        Audio = new RecordingAudioAdapter();
        Input = new RecordingInputAdapter();
        Persistence = new RecordingPersistenceAdapter();
    }

    public RecordingSettingsAdapter Settings { get; }
    public RecordingAudioAdapter Audio { get; }
    public RecordingInputAdapter Input { get; }
    public RecordingPersistenceAdapter Persistence { get; }

    public OptionsRuntime Create(OptionsProduct product) =>
        new(product, Settings, Audio, Input, Persistence);
}

internal sealed class RecordingPreparedApply(Action commit) : IPreparedOptionsApply
{
    private bool _disposed;

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        commit();
    }

    public bool IsDisposed => _disposed;
    public void Dispose() => _disposed = true;
}

internal sealed class RecordingSettingsAdapter : IOptionsSettingsAdapter
{
    public bool FailRead { get; set; }
    public bool FailAutodetect { get; set; }
    public bool FailPrepare { get; set; }
    public bool ThrowOnPreparedCommit { get; set; }
    public int PreparedCommitCount { get; private set; }
    public int AutodetectCount { get; private set; }
    public RecordingPreparedApply? LastPlan { get; private set; }
    public Dictionary<string, OptionScalar> Current { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, OptionScalar> Defaults { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ImmutableArray<OptionScalar>> DynamicChoices { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, OptionScalar> Detected { get; } = new(StringComparer.Ordinal);
    public ImmutableDictionary<string, OptionScalar>? LastPrepared { get; private set; }

    public OptionsAdapterResult<OptionsSettingsSnapshot> Read(OptionsProduct product) => FailRead
        ? OptionsAdapterResult<OptionsSettingsSnapshot>.Failure(OptionsIssueCode.SettingsReadFailed)
        : OptionsAdapterResult<OptionsSettingsSnapshot>.Success(new OptionsSettingsSnapshot(
            Current.ToImmutableDictionary(StringComparer.Ordinal),
            Defaults.ToImmutableDictionary(StringComparer.Ordinal),
            DynamicChoices.ToImmutableDictionary(StringComparer.Ordinal)));

    public OptionsAdapterResult<ImmutableDictionary<string, OptionScalar>> Autodetect(
        OptionsProduct product)
    {
        AutodetectCount++;
        return FailAutodetect
            ? OptionsAdapterResult<ImmutableDictionary<string, OptionScalar>>.Failure(
                OptionsIssueCode.SettingsReadFailed)
            : OptionsAdapterResult<ImmutableDictionary<string, OptionScalar>>.Success(
                Detected.ToImmutableDictionary(StringComparer.Ordinal));
    }

    public OptionsAdapterResult<IPreparedOptionsApply> PrepareApply(
        ImmutableDictionary<string, OptionScalar> values)
    {
        LastPrepared = values;
        if (FailPrepare)
        {
            return OptionsAdapterResult<IPreparedOptionsApply>.Failure(OptionsIssueCode.SettingsPrepareFailed);
        }

        LastPlan = new RecordingPreparedApply(() =>
        {
            if (ThrowOnPreparedCommit)
            {
                throw new InvalidOperationException("Injected prepared settings commit failure");
            }

            PreparedCommitCount++;
        });
        return OptionsAdapterResult<IPreparedOptionsApply>.Success(LastPlan);
    }
}

internal sealed class RecordingAudioAdapter : IOptionsAudioAdapter
{
    public bool FailPreview { get; set; }
    public bool FailRestore { get; set; }
    public List<ImmutableDictionary<string, OptionScalar>> Previews { get; } = [];
    public List<ImmutableDictionary<string, OptionScalar>> Restores { get; } = [];

    public OptionsAdapterResult<bool> Preview(ImmutableDictionary<string, OptionScalar> values)
    {
        Previews.Add(values);
        return FailPreview
            ? OptionsAdapterResult<bool>.Failure(OptionsIssueCode.AudioPreviewFailed)
            : OptionsAdapterResult<bool>.Success(true);
    }

    public OptionsAdapterResult<bool> Restore(ImmutableDictionary<string, OptionScalar> values)
    {
        Restores.Add(values);
        return FailRestore
            ? OptionsAdapterResult<bool>.Failure(OptionsIssueCode.AudioRollbackFailed)
            : OptionsAdapterResult<bool>.Success(true);
    }
}

internal sealed class RecordingInputAdapter : IOptionsInputAdapter
{
    public bool FailValidation { get; set; }
    public bool FailPrepare { get; set; }
    public bool ThrowOnPrepare { get; set; }
    public bool ThrowOnPreparedCommit { get; set; }
    public int PreparedCommitCount { get; private set; }
    public ImmutableDictionary<string, BindingPair>? LastPrepared { get; private set; }

    public OptionsAdapterResult<InputChord> Validate(InputChord chord) => FailValidation
        ? OptionsAdapterResult<InputChord>.Failure(OptionsIssueCode.InvalidBinding)
        : OptionsAdapterResult<InputChord>.Success(new InputChord(chord.Token));

    public OptionsAdapterResult<IPreparedOptionsApply> PrepareApply(
        ImmutableDictionary<string, BindingPair> bindings)
    {
        if (ThrowOnPrepare)
        {
            throw new InvalidOperationException("Injected input prepare failure");
        }

        LastPrepared = bindings;
        return FailPrepare
            ? OptionsAdapterResult<IPreparedOptionsApply>.Failure(OptionsIssueCode.InputPrepareFailed)
            : OptionsAdapterResult<IPreparedOptionsApply>.Success(
                new RecordingPreparedApply(() =>
                {
                    if (ThrowOnPreparedCommit)
                    {
                        throw new InvalidOperationException("Injected prepared input commit failure");
                    }

                    PreparedCommitCount++;
                }));
    }
}

internal sealed class RecordingPersistenceAdapter : IOptionsPersistenceAdapter
{
    public bool FailLoad { get; set; }
    public bool FailCommit { get; set; }
    public int CommitCount { get; private set; }
    public OptionsStoredState State { get; set; } = new(
        0,
        ImmutableDictionary<string, OptionScalar>.Empty,
        ImmutableDictionary<string, OptionScalar>.Empty,
        ImmutableDictionary<string, BindingPair>.Empty);

    public OptionsAdapterResult<OptionsStoredState> Load() => FailLoad
        ? OptionsAdapterResult<OptionsStoredState>.Failure(OptionsIssueCode.StoreReadFailed)
        : OptionsAdapterResult<OptionsStoredState>.Success(State);

    public OptionsAdapterResult<bool> Commit(OptionsStoredState state)
    {
        if (FailCommit)
        {
            return OptionsAdapterResult<bool>.Failure(OptionsIssueCode.StoreCommitFailed);
        }

        if (state.Revision != State.Revision)
        {
            return OptionsAdapterResult<bool>.Failure(OptionsIssueCode.ConcurrentStoreChange);
        }

        CommitCount++;
        State = state with { Revision = State.Revision + 1 };
        return OptionsAdapterResult<bool>.Success(true);
    }
}
