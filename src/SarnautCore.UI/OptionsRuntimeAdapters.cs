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

internal interface IPreparedOptionsTransaction : IDisposable
{
    OptionsAdapterResult<bool> Commit();
    OptionsAdapterResult<bool> Rollback();
}

internal interface IOptionsSettingsAdapter
{
    OptionsAdapterResult<OptionsSettingsSnapshot> Read(OptionsProduct product);

    OptionsAdapterResult<ImmutableDictionary<string, OptionScalar>> Autodetect(
        OptionsProduct product);

    OptionsAdapterResult<IPreparedOptionsTransaction> PrepareApply(
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

    OptionsAdapterResult<IPreparedOptionsTransaction> PrepareApply(
        ImmutableDictionary<string, BindingPair> bindings);
}

internal interface IOptionsPersistenceAdapter
{
    OptionsAdapterResult<OptionsStoredState> Load();
    OptionsAdapterResult<IPreparedOptionsTransaction> PrepareCommit(OptionsStoredState state);
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

internal sealed class RecordingPreparedTransaction(
    Func<OptionsAdapterResult<bool>> commit,
    Func<OptionsAdapterResult<bool>> rollback) : IPreparedOptionsTransaction
{
    private bool _disposed;

    public OptionsAdapterResult<bool> Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return commit();
    }

    public OptionsAdapterResult<bool> Rollback()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return rollback();
    }

    public bool IsDisposed => _disposed;
    public void Dispose() => _disposed = true;
}

internal sealed class RecordingSettingsAdapter : IOptionsSettingsAdapter
{
    public bool FailRead { get; set; }
    public bool FailAutodetect { get; set; }
    public bool FailPrepare { get; set; }
    public bool FailCommit { get; set; }
    public bool ThrowOnCommit { get; set; }
    public bool FailRollback { get; set; }
    public bool ThrowOnRollback { get; set; }
    public int PreparedCommitCount { get; private set; }
    public int RollbackCount { get; private set; }
    public int AutodetectCount { get; private set; }
    public RecordingPreparedTransaction? LastPlan { get; private set; }
    public Dictionary<string, OptionScalar> Current { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, OptionScalar> Defaults { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ImmutableArray<OptionScalar>> DynamicChoices { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, OptionScalar> Detected { get; } = new(StringComparer.Ordinal);
    public ImmutableDictionary<string, OptionScalar>? LastPrepared { get; private set; }
    public ImmutableDictionary<string, OptionScalar> Committed { get; private set; } =
        ImmutableDictionary<string, OptionScalar>.Empty;

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

    public OptionsAdapterResult<IPreparedOptionsTransaction> PrepareApply(
        ImmutableDictionary<string, OptionScalar> values)
    {
        LastPrepared = values;
        if (FailPrepare)
        {
            return OptionsAdapterResult<IPreparedOptionsTransaction>.Failure(
                OptionsIssueCode.SettingsPrepareFailed);
        }

        ImmutableDictionary<string, OptionScalar> previous = Committed;
        LastPlan = new RecordingPreparedTransaction(
            commit: () =>
            {
                PreparedCommitCount++;
                Committed = values;
                if (ThrowOnCommit)
                {
                    throw new InvalidOperationException("Injected prepared settings commit failure");
                }

                return FailCommit
                    ? OptionsAdapterResult<bool>.Failure(OptionsIssueCode.SettingsCommitFailed)
                    : OptionsAdapterResult<bool>.Success(true);
            },
            rollback: () =>
            {
                RollbackCount++;
                if (ThrowOnRollback)
                {
                    throw new InvalidOperationException("Injected settings rollback failure");
                }

                if (FailRollback)
                {
                    return OptionsAdapterResult<bool>.Failure(
                        OptionsIssueCode.RollbackContractViolation);
                }

                Committed = previous;
                return OptionsAdapterResult<bool>.Success(true);
            });
        return OptionsAdapterResult<IPreparedOptionsTransaction>.Success(LastPlan);
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
    public bool FailCommit { get; set; }
    public bool ThrowOnCommit { get; set; }
    public bool FailRollback { get; set; }
    public bool ThrowOnRollback { get; set; }
    public int PreparedCommitCount { get; private set; }
    public int RollbackCount { get; private set; }
    public ImmutableDictionary<string, BindingPair>? LastPrepared { get; private set; }
    public ImmutableDictionary<string, BindingPair> Installed { get; private set; } =
        ImmutableDictionary<string, BindingPair>.Empty;
    public RecordingPreparedTransaction? LastPlan { get; private set; }

    public OptionsAdapterResult<InputChord> Validate(InputChord chord) => FailValidation
        ? OptionsAdapterResult<InputChord>.Failure(OptionsIssueCode.InvalidBinding)
        : OptionsAdapterResult<InputChord>.Success(new InputChord(chord.Token));

    public OptionsAdapterResult<IPreparedOptionsTransaction> PrepareApply(
        ImmutableDictionary<string, BindingPair> bindings)
    {
        if (ThrowOnPrepare)
        {
            throw new InvalidOperationException("Injected input prepare failure");
        }

        LastPrepared = bindings;
        if (FailPrepare)
        {
            return OptionsAdapterResult<IPreparedOptionsTransaction>.Failure(
                OptionsIssueCode.InputPrepareFailed);
        }

        ImmutableDictionary<string, BindingPair> previous = Installed;
        LastPlan = new RecordingPreparedTransaction(
            commit: () =>
            {
                PreparedCommitCount++;
                Installed = bindings;
                if (ThrowOnCommit)
                {
                    throw new InvalidOperationException("Injected prepared input commit failure");
                }

                return FailCommit
                    ? OptionsAdapterResult<bool>.Failure(OptionsIssueCode.InputCommitFailed)
                    : OptionsAdapterResult<bool>.Success(true);
            },
            rollback: () =>
            {
                RollbackCount++;
                if (ThrowOnRollback)
                {
                    throw new InvalidOperationException("Injected input rollback failure");
                }

                if (FailRollback)
                {
                    return OptionsAdapterResult<bool>.Failure(
                        OptionsIssueCode.RollbackContractViolation);
                }

                Installed = previous;
                return OptionsAdapterResult<bool>.Success(true);
            });
        return OptionsAdapterResult<IPreparedOptionsTransaction>.Success(LastPlan);
    }
}

internal sealed class RecordingPersistenceAdapter : IOptionsPersistenceAdapter
{
    public bool FailLoad { get; set; }
    public bool FailCommit { get; set; }
    public bool ThrowOnCommit { get; set; }
    public bool FailRollback { get; set; }
    public bool ThrowOnRollback { get; set; }
    public int CommitCount { get; private set; }
    public int RollbackCount { get; private set; }
    public RecordingPreparedTransaction? LastPlan { get; private set; }
    public OptionsStoredState State { get; set; } = new(
        0,
        ImmutableDictionary<string, OptionScalar>.Empty,
        ImmutableDictionary<string, OptionScalar>.Empty,
        ImmutableDictionary<string, BindingPair>.Empty);

    public OptionsAdapterResult<OptionsStoredState> Load() => FailLoad
        ? OptionsAdapterResult<OptionsStoredState>.Failure(OptionsIssueCode.StoreReadFailed)
        : OptionsAdapterResult<OptionsStoredState>.Success(State);

    public OptionsAdapterResult<IPreparedOptionsTransaction> PrepareCommit(OptionsStoredState state)
    {
        if (state.Revision != State.Revision)
        {
            return OptionsAdapterResult<IPreparedOptionsTransaction>.Failure(
                OptionsIssueCode.ConcurrentStoreChange);
        }

        OptionsStoredState previous = State;
        LastPlan = new RecordingPreparedTransaction(
            commit: () =>
            {
                CommitCount++;
                State = state with { Revision = previous.Revision + 1 };
                if (ThrowOnCommit)
                {
                    throw new InvalidOperationException("Injected persistence commit failure");
                }

                return FailCommit
                    ? OptionsAdapterResult<bool>.Failure(OptionsIssueCode.StoreCommitFailed)
                    : OptionsAdapterResult<bool>.Success(true);
            },
            rollback: () =>
            {
                RollbackCount++;
                if (ThrowOnRollback)
                {
                    throw new InvalidOperationException("Injected persistence rollback failure");
                }

                if (FailRollback)
                {
                    return OptionsAdapterResult<bool>.Failure(
                        OptionsIssueCode.RollbackContractViolation);
                }

                State = previous;
                return OptionsAdapterResult<bool>.Success(true);
            });
        return OptionsAdapterResult<IPreparedOptionsTransaction>.Success(LastPlan);
    }
}
