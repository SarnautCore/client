using System.Collections.Immutable;

namespace SarnautCore.UI;

public sealed class OptionsRuntime : IDisposable, IChatBubbleSettingsSource
{
    public const string BindingsPage = "bindings";

    private readonly OptionsProduct _product;
    private readonly IOptionsSettingsAdapter _settings;
    private readonly IOptionsAudioAdapter _audio;
    private readonly IOptionsInputAdapter _input;
    private readonly IOptionsPersistenceAdapter _persistence;
    private readonly ImmutableDictionary<string, OptionDefinition> _optionDefinitions;
    private readonly ImmutableDictionary<string, BindingDefinition> _bindingDefinitions;

    private ImmutableDictionary<string, ImmutableArray<OptionScalar>> _choices =
        ImmutableDictionary<string, ImmutableArray<OptionScalar>>.Empty;
    private ImmutableDictionary<string, OptionScalar> _defaults =
        ImmutableDictionary<string, OptionScalar>.Empty;
    private ImmutableDictionary<string, OptionScalar> _applied =
        ImmutableDictionary<string, OptionScalar>.Empty;
    private ImmutableDictionary<string, OptionScalar> _draft =
        ImmutableDictionary<string, OptionScalar>.Empty;
    private ImmutableDictionary<string, BindingPair> _bindingDefaults =
        ImmutableDictionary<string, BindingPair>.Empty;
    private ImmutableDictionary<string, BindingPair> _bindingApplied =
        ImmutableDictionary<string, BindingPair>.Empty;
    private ImmutableDictionary<string, BindingPair> _bindingDraft =
        ImmutableDictionary<string, BindingPair>.Empty;
    private ImmutableArray<OptionsIssue> _warnings = [];
    private long _storeRevision;
    private long _revision;
    private string _activePage;
    private BindingCaptureView? _capture;
    private BindingConflictView? _conflict;
    private DefaultsConfirmationView? _defaultsConfirmation;
    private bool _opened;
    private bool _closed;
    private bool _disposed;

    internal OptionsRuntime(
        OptionsProduct product,
        IOptionsSettingsAdapter settings,
        IOptionsAudioAdapter audio,
        IOptionsInputAdapter input,
        IOptionsPersistenceAdapter persistence)
    {
        _product = product ?? throw new ArgumentNullException(nameof(product));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _optionDefinitions = product.Options.ToImmutableDictionary(option => option.Id, StringComparer.Ordinal);
        _bindingDefinitions = product.BindingSections.SelectMany(section => section.Bindings)
            .ToImmutableDictionary(binding => binding.Id, StringComparer.Ordinal);
        _activePage = product.Pages[0].Id;
    }

    public event EventHandler<ChatBubbleSettingsChangedEventArgs>? ChatBubbleSettingsChanged;

    public OptionsView View => _opened
        ? BuildView()
        : throw new InvalidOperationException("Options runtime is not open");

    public ChatBubbleSettings CurrentChatBubbleSettings => _opened
        ? ReadChatBubbleSettings(_applied)
        : throw new InvalidOperationException("Options runtime is not open");

    public OptionsTransition Open()
    {
        if (_disposed)
        {
            return Failure(OptionsIssueCode.Disposed, fatal: true);
        }

        if (_opened && !_closed)
        {
            return Transition(OptionsOutcome.NoChange);
        }

        OptionsAdapterResult<OptionsSettingsSnapshot> settingsRead;
        OptionsAdapterResult<OptionsStoredState> storeRead;
        try
        {
            settingsRead = _settings.Read(_product);
            storeRead = _persistence.Load();
        }
        catch (Exception)
        {
            return Failure(OptionsIssueCode.AdapterContractViolation, fatal: true);
        }

        if (!settingsRead.Succeeded || settingsRead.Value is null)
        {
            return Failure(settingsRead.Error ?? OptionsIssueCode.SettingsReadFailed, fatal: true);
        }

        if (!storeRead.Succeeded || storeRead.Value is null)
        {
            return Failure(storeRead.Error ?? OptionsIssueCode.StoreReadFailed, fatal: true);
        }

        var warnings = ImmutableArray.CreateBuilder<OptionsIssue>();
        if (!TryBuildOptions(settingsRead.Value, storeRead.Value, warnings, out OptionOpenState optionState)
            || !TryBuildBindings(storeRead.Value, warnings, out BindingOpenState bindingState))
        {
            return Failure(OptionsIssueCode.SettingsReadFailed, fatal: true);
        }

        _choices = optionState.Choices;
        _defaults = optionState.Defaults;
        _applied = optionState.Applied;
        _draft = optionState.Applied;
        _bindingDefaults = bindingState.Defaults;
        _bindingApplied = bindingState.Applied;
        _bindingDraft = bindingState.Applied;
        _warnings = warnings.ToImmutable();
        _storeRevision = storeRead.Value.Revision;
        _revision++;
        _activePage = _product.Pages[0].Id;
        _capture = null;
        _conflict = null;
        _defaultsConfirmation = null;
        _opened = true;
        _closed = false;
        return Transition(OptionsOutcome.Opened);
    }

    public OptionsTransition Dispatch(OptionsCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_disposed)
        {
            return Failure(OptionsIssueCode.Disposed);
        }

        if (!_opened)
        {
            return Failure(OptionsIssueCode.NotOpen);
        }

        if (_closed)
        {
            return Failure(OptionsIssueCode.AlreadyClosed);
        }

        return command switch
        {
            OptionsCommand.SelectPage select => SelectPage(select.Page),
            OptionsCommand.SetOption set => SetOption(set.Option, set.Value),
            OptionsCommand.ActivateOption activate => ActivateOption(activate.Option),
            OptionsCommand.BeginBindingCapture begin => BeginCapture(begin.Binding, begin.Slot),
            OptionsCommand.OfferBinding offer => OfferBinding(offer.Chord),
            OptionsCommand.ClearBinding clear => ClearBinding(clear.Binding, clear.Slot),
            OptionsCommand.ResolveBindingConflict resolve => ResolveConflict(resolve.ConfirmReplacement),
            OptionsCommand.RequestDefaults request => RequestDefaults(request.Scope),
            OptionsCommand.ResolveDefaults resolve => ResolveDefaults(resolve.Confirm),
            OptionsCommand.ResetDraft reset => ResetDraft(reset.Scope),
            OptionsCommand.Apply => Apply(closeAfter: false),
            OptionsCommand.Accept => Apply(closeAfter: true),
            OptionsCommand.Cancel => Cancel(),
            OptionsCommand.Escape => Escape(),
            _ => Failure(OptionsIssueCode.UnsupportedAction),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_opened && !_closed && HasAudioDraftChanges())
        {
            try
            {
                _ = _audio.Restore(AudioValues(_applied));
            }
            catch (Exception)
            {
                // Dispose cannot report a transition. Production adapters log their own failure.
            }
        }

        _disposed = true;
    }

    private OptionsTransition SelectPage(string page)
    {
        if (page != BindingsPage && !_product.Pages.Any(candidate => candidate.Id == page))
        {
            return Failure(OptionsIssueCode.UnknownPage, page);
        }

        if (_activePage == page)
        {
            return Transition(OptionsOutcome.NoChange);
        }

        _activePage = page;
        _revision++;
        return Transition(OptionsOutcome.Changed);
    }

    private OptionsTransition SetOption(string id, OptionScalar value)
    {
        if (ModalActive())
        {
            return Failure(OptionsIssueCode.ModalOperationActive, id);
        }

        if (!_optionDefinitions.TryGetValue(id, out OptionDefinition? definition))
        {
            return Failure(OptionsIssueCode.UnknownOption, id);
        }

        if (definition.DataKind == OptionDataKind.Action)
        {
            return Failure(OptionsIssueCode.UnsupportedAction, id);
        }

        if (!ValidValue(definition, value))
        {
            return Failure(OptionsIssueCode.InvalidValue, id);
        }

        if (_draft[id] == value && definition.Handler != OptionHandler.QualityPreset)
        {
            return Transition(OptionsOutcome.NoChange);
        }

        ImmutableDictionary<string, OptionScalar> candidate = definition.Handler == OptionHandler.QualityPreset
            ? StageGraphicsPreset(_draft, value)
            : StageIndividualOption(_draft.SetItem(id, value), id);
        if (!PreviewAudioCandidate(candidate))
        {
            return Failure(OptionsIssueCode.AudioPreviewFailed, id);
        }


        if (MapEqual(candidate, _draft))
        {
            return Transition(OptionsOutcome.NoChange);
        }

        _draft = candidate;
        _revision++;
        return Transition(OptionsOutcome.Changed);
    }

    private OptionsTransition ActivateOption(string id)
    {
        if (ModalActive())
        {
            return Failure(OptionsIssueCode.ModalOperationActive, id);
        }

        if (!_optionDefinitions.TryGetValue(id, out OptionDefinition? definition))
        {
            return Failure(OptionsIssueCode.UnknownOption, id);
        }

        if (definition.DataKind != OptionDataKind.Action || definition.Handler != OptionHandler.Autodetect)
        {
            return Failure(OptionsIssueCode.UnsupportedAction, id);
        }

        OptionsAdapterResult<ImmutableDictionary<string, OptionScalar>> detected;
        try
        {
            detected = _settings.Autodetect(_product);
        }
        catch (Exception)
        {
            return Failure(OptionsIssueCode.AdapterContractViolation, id);
        }

        if (!detected.Succeeded || detected.Value is null)
        {
            return Failure(detected.Error ?? OptionsIssueCode.SettingsReadFailed, id);
        }

        ImmutableDictionary<string, OptionScalar> candidate = _draft;
        foreach ((string optionId, OptionScalar value) in detected.Value)
        {
            if (!_optionDefinitions.TryGetValue(optionId, out OptionDefinition? option)
                || option.DataKind == OptionDataKind.Action
                || !ValidEffectiveValue(option, value))
            {
                return Failure(OptionsIssueCode.InvalidValue, optionId);
            }

            candidate = candidate.SetItem(optionId, value);
        }

        if (!PreviewAudioCandidate(candidate))
        {
            return Failure(OptionsIssueCode.AudioPreviewFailed, id);
        }

        if (MapEqual(candidate, _draft))
        {
            return Transition(OptionsOutcome.NoChange);
        }

        _draft = candidate;
        _revision++;
        return Transition(OptionsOutcome.Changed);
    }

    private OptionsTransition BeginCapture(string binding, BindingSlot slot)
    {
        if (_conflict is not null || _defaultsConfirmation is not null)
        {
            return Failure(OptionsIssueCode.ModalOperationActive, binding);
        }

        if (!_bindingDefinitions.ContainsKey(binding))
        {
            return Failure(OptionsIssueCode.InvalidBinding, binding);
        }

        if (!System.Enum.IsDefined(slot))
        {
            return Failure(OptionsIssueCode.InvalidBindingSlot, binding);
        }

        _capture = new BindingCaptureView(new BindingLocation(binding, slot));
        _revision++;
        return Transition(OptionsOutcome.AwaitingCapture);
    }

    private OptionsTransition OfferBinding(InputChord chord)
    {
        if (_capture is null)
        {
            return Failure(OptionsIssueCode.CaptureNotActive);
        }

        OptionsAdapterResult<InputChord> validation;
        try
        {
            validation = _input.Validate(chord);
        }
        catch (Exception)
        {
            return Failure(OptionsIssueCode.AdapterContractViolation, _capture.Target.Binding);
        }

        if (!validation.Succeeded)
        {
            return Failure(validation.Error ?? OptionsIssueCode.InvalidBinding, _capture.Target.Binding);
        }

        InputChord normalized = validation.Value;
        ImmutableArray<BindingLocation> occupants = FindOccupants(normalized, _capture.Target);
        if (occupants.Length > 0)
        {
            _conflict = new BindingConflictView(_capture.Target, normalized, occupants);
            _capture = null;
            _revision++;
            return Transition(OptionsOutcome.AwaitingConflictConfirmation);
        }

        BindingLocation target = _capture.Target;
        _bindingDraft = _bindingDraft.SetItem(
            target.Binding,
            _bindingDraft[target.Binding].With(target.Slot, normalized));
        _capture = null;
        _revision++;
        return Transition(OptionsOutcome.Changed);
    }

    private OptionsTransition ClearBinding(string binding, BindingSlot slot)
    {
        if (ModalActive())
        {
            return Failure(OptionsIssueCode.ModalOperationActive, binding);
        }

        if (!_bindingDraft.TryGetValue(binding, out BindingPair pair))
        {
            return Failure(OptionsIssueCode.InvalidBinding, binding);
        }

        if (!System.Enum.IsDefined(slot))
        {
            return Failure(OptionsIssueCode.InvalidBindingSlot, binding);
        }

        if (pair[slot] is null)
        {
            return Transition(OptionsOutcome.NoChange);
        }

        _bindingDraft = _bindingDraft.SetItem(binding, pair.With(slot, null));
        _revision++;
        return Transition(OptionsOutcome.Changed);
    }

    private OptionsTransition ResolveConflict(bool confirm)
    {
        if (_conflict is null)
        {
            return Failure(OptionsIssueCode.ConflictNotPending);
        }

        if (confirm)
        {
            foreach (BindingLocation occupant in _conflict.Occupants)
            {
                _bindingDraft = _bindingDraft.SetItem(
                    occupant.Binding,
                    _bindingDraft[occupant.Binding].With(occupant.Slot, null));
            }

            BindingLocation target = _conflict.Target;
            _bindingDraft = _bindingDraft.SetItem(
                target.Binding,
                _bindingDraft[target.Binding].With(target.Slot, _conflict.Candidate));
        }

        _conflict = null;
        _revision++;
        return Transition(confirm ? OptionsOutcome.Changed : OptionsOutcome.NoChange);
    }

    private OptionsTransition RequestDefaults(OptionsScope scope)
    {
        if (ModalActive())
        {
            return Failure(OptionsIssueCode.ModalOperationActive);
        }

        if (!ValidScope(scope))
        {
            return Failure(OptionsIssueCode.UnknownPage, scope.Page);
        }

        _defaultsConfirmation = new DefaultsConfirmationView(scope);
        _revision++;
        return Transition(OptionsOutcome.AwaitingDefaultsConfirmation);
    }

    private OptionsTransition ResolveDefaults(bool confirm)
    {
        if (_defaultsConfirmation is null)
        {
            return Failure(OptionsIssueCode.DefaultsConfirmationNotPending);
        }

        OptionsScope scope = _defaultsConfirmation.Scope;
        _defaultsConfirmation = null;
        if (!confirm)
        {
            _revision++;
            return Transition(OptionsOutcome.NoChange);
        }

        return StageScope(scope, _defaults, _bindingDefaults);
    }

    private OptionsTransition ResetDraft(OptionsScope scope)
    {
        if (ModalActive())
        {
            return Failure(OptionsIssueCode.ModalOperationActive);
        }

        if (!ValidScope(scope))
        {
            return Failure(OptionsIssueCode.UnknownPage, scope.Page);
        }

        return StageScope(scope, _applied, _bindingApplied);
    }

    private OptionsTransition StageScope(
        OptionsScope scope,
        ImmutableDictionary<string, OptionScalar> optionSource,
        ImmutableDictionary<string, BindingPair> bindingSource)
    {
        ImmutableDictionary<string, OptionScalar> candidate = _draft;
        if (scope.Kind is OptionsScopeKind.All or OptionsScopeKind.Page)
        {
            foreach (OptionDefinition option in _product.Options.Where(
                         option => option.DataKind != OptionDataKind.Action
                             && (scope.Kind == OptionsScopeKind.All || option.Page == scope.Page)))
            {
                candidate = candidate.SetItem(option.Id, optionSource[option.Id]);
            }
        }

        if (!PreviewAudioCandidate(candidate))
        {
            return Failure(OptionsIssueCode.AudioPreviewFailed);
        }

        ImmutableDictionary<string, BindingPair> bindingCandidate = _bindingDraft;
        if (scope.Kind is OptionsScopeKind.All or OptionsScopeKind.Bindings)
        {
            bindingCandidate = bindingSource;
        }

        bool changed = !MapEqual(candidate, _draft) || !MapEqual(bindingCandidate, _bindingDraft);
        _draft = candidate;
        _bindingDraft = bindingCandidate;
        _revision++;
        return Transition(changed ? OptionsOutcome.Changed : OptionsOutcome.NoChange);
    }

    private OptionsTransition Apply(bool closeAfter)
    {
        if (ModalActive())
        {
            return Failure(OptionsIssueCode.ModalOperationActive);
        }

        if (!IsDirty())
        {
            if (!closeAfter)
            {
                return Transition(OptionsOutcome.NoChange);
            }

            _closed = true;
            _revision++;
            return Transition(OptionsOutcome.CloseRequested, OptionsCloseDirective.Accepted);
        }

        var plans = new List<IPreparedOptionsTransaction>(3);
        try
        {
            OptionsAdapterResult<IPreparedOptionsTransaction> settingsPlan =
                _settings.PrepareApply(PersistableValues(_draft));
            if (!settingsPlan.Succeeded || settingsPlan.Value is null)
            {
                return Failure(settingsPlan.Error ?? OptionsIssueCode.SettingsPrepareFailed);
            }

            plans.Add(settingsPlan.Value);
            OptionsAdapterResult<IPreparedOptionsTransaction> inputPlan =
                _input.PrepareApply(_bindingDraft);
            if (!inputPlan.Succeeded || inputPlan.Value is null)
            {
                DisposePlans(plans);
                return Failure(inputPlan.Error ?? OptionsIssueCode.InputPrepareFailed);
            }

            plans.Add(inputPlan.Value);
            OptionsAdapterResult<IPreparedOptionsTransaction> persistencePlan =
                _persistence.PrepareCommit(BuildStoredState());
            if (!persistencePlan.Succeeded || persistencePlan.Value is null)
            {
                DisposePlans(plans);
                return Failure(persistencePlan.Error ?? OptionsIssueCode.StoreCommitFailed);
            }

            plans.Add(persistencePlan.Value);
        }
        catch (Exception)
        {
            DisposePlans(plans);
            return Failure(OptionsIssueCode.AdapterContractViolation);
        }

        OptionsIssueCode? commitFailure;
        bool rollbackViolated;
        try
        {
            commitFailure = CommitPlans(plans, out rollbackViolated);
        }
        finally
        {
            DisposePlans(plans);
        }

        if (commitFailure is OptionsIssueCode failure)
        {
            return Failure(
                rollbackViolated ? OptionsIssueCode.RollbackContractViolation : failure,
                fatal: rollbackViolated);
        }

        _storeRevision++;
        ChatBubbleSettings previousChat = ReadChatBubbleSettings(_applied);
        _applied = _draft;
        _bindingApplied = _bindingDraft;
        _revision++;
        ChatBubbleSettings currentChat = ReadChatBubbleSettings(_applied);
        if (currentChat != previousChat)
        {
            NotifyChatBubbleSettingsChanged(currentChat);
        }
        if (closeAfter)
        {
            _closed = true;
            return Transition(OptionsOutcome.CloseRequested, OptionsCloseDirective.Accepted);
        }

        return Transition(OptionsOutcome.Applied);
    }

    private static OptionsIssueCode? CommitPlans(
        IReadOnlyList<IPreparedOptionsTransaction> plans,
        out bool rollbackViolated)
    {
        rollbackViolated = false;
        for (int index = 0; index < plans.Count; index++)
        {
            OptionsIssueCode? failure = null;
            try
            {
                OptionsAdapterResult<bool> result = plans[index].Commit();
                if (!result.Succeeded)
                {
                    failure = result.Error ?? OptionsIssueCode.AdapterContractViolation;
                }
            }
            catch (Exception)
            {
                failure = OptionsIssueCode.AdapterContractViolation;
            }

            if (failure is null)
            {
                continue;
            }

            for (int rollbackIndex = index; rollbackIndex >= 0; rollbackIndex--)
            {
                try
                {
                    OptionsAdapterResult<bool> rollback = plans[rollbackIndex].Rollback();
                    rollbackViolated |= !rollback.Succeeded;
                }
                catch (Exception)
                {
                    rollbackViolated = true;
                }
            }

            return failure;
        }

        return null;
    }

    private static void DisposePlans(IEnumerable<IPreparedOptionsTransaction> plans)
    {
        foreach (IPreparedOptionsTransaction plan in plans)
        {
            plan.Dispose();
        }
    }

    private OptionsTransition Cancel()
    {
        if (HasAudioDraftChanges())
        {
            OptionsAdapterResult<bool> restored;
            try
            {
                restored = _audio.Restore(AudioValues(_applied));
            }
            catch (Exception)
            {
                return Failure(OptionsIssueCode.AdapterContractViolation);
            }

            if (!restored.Succeeded)
            {
                return Failure(restored.Error ?? OptionsIssueCode.AudioRollbackFailed);
            }
        }

        _draft = _applied;
        _bindingDraft = _bindingApplied;
        _capture = null;
        _conflict = null;
        _defaultsConfirmation = null;
        _closed = true;
        _revision++;
        return Transition(OptionsOutcome.CloseRequested, OptionsCloseDirective.Cancelled);
    }

    private OptionsTransition Escape()
    {
        if (_conflict is not null)
        {
            _conflict = null;
            _revision++;
            return Transition(OptionsOutcome.NoChange);
        }

        if (_capture is not null)
        {
            _capture = null;
            _revision++;
            return Transition(OptionsOutcome.NoChange);
        }

        if (_defaultsConfirmation is not null)
        {
            _defaultsConfirmation = null;
            _revision++;
            return Transition(OptionsOutcome.NoChange);
        }

        return Cancel();
    }

    private bool TryBuildOptions(
        OptionsSettingsSnapshot settings,
        OptionsStoredState stored,
        ImmutableArray<OptionsIssue>.Builder warnings,
        out OptionOpenState state)
    {
        var choices = ImmutableDictionary.CreateBuilder<string, ImmutableArray<OptionScalar>>(StringComparer.Ordinal);
        var defaults = ImmutableDictionary.CreateBuilder<string, OptionScalar>(StringComparer.Ordinal);
        var applied = ImmutableDictionary.CreateBuilder<string, OptionScalar>(StringComparer.Ordinal);
        foreach (OptionDefinition option in _product.Options)
        {
            ImmutableArray<OptionScalar> optionChoices = option.Values;
            if (optionChoices.Length == 0
                && settings.DynamicChoices.TryGetValue(option.Id, out ImmutableArray<OptionScalar> dynamicChoices))
            {
                optionChoices = dynamicChoices;
            }

            if (!TryDefault(option, optionChoices, settings, out OptionScalar defaultValue))
            {
                state = default!;
                return false;
            }

            OptionScalar current = settings.Current.TryGetValue(option.Id, out OptionScalar runtimeValue)
                && ValidEffectiveValue(option, runtimeValue, optionChoices)
                    ? runtimeValue
                    : defaultValue;
            ImmutableDictionary<string, OptionScalar> owner = option.Storage == OptionStorage.Global
                ? stored.Global
                : stored.User;
            ImmutableDictionary<string, OptionScalar> wrongOwner = option.Storage == OptionStorage.Global
                ? stored.User
                : stored.Global;
            bool misrouted = wrongOwner.ContainsKey(option.Id);
            if (misrouted)
            {
                warnings.Add(new OptionsIssue(
                    OptionsIssueCode.InvalidStoredOption,
                    OptionsIssueSeverity.Warning,
                    option.Id));
            }

            if (owner.TryGetValue(option.Id, out OptionScalar persisted))
            {
                if (ValidEffectiveValue(option, persisted, optionChoices))
                {
                    current = persisted;
                }
                else
                {
                    current = defaultValue;
                    warnings.Add(new OptionsIssue(
                        OptionsIssueCode.InvalidStoredOption,
                        OptionsIssueSeverity.Warning,
                        option.Id));
                }
            }
            else if (misrouted)
            {
                current = defaultValue;
            }

            choices.Add(option.Id, optionChoices);
            defaults.Add(option.Id, defaultValue);
            applied.Add(option.Id, current);
        }

        foreach (string unknown in stored.Global.Keys.Concat(stored.User.Keys)
                     .Where(id => !_optionDefinitions.ContainsKey(id))
                     .Distinct(StringComparer.Ordinal))
        {
            warnings.Add(new OptionsIssue(
                OptionsIssueCode.UnknownStoredOption,
                OptionsIssueSeverity.Warning,
                unknown));
        }

        state = new OptionOpenState(choices.ToImmutable(), defaults.ToImmutable(), applied.ToImmutable());
        return true;
    }

    private bool TryBuildBindings(
        OptionsStoredState stored,
        ImmutableArray<OptionsIssue>.Builder warnings,
        out BindingOpenState state)
    {
        var defaults = ImmutableDictionary.CreateBuilder<string, BindingPair>(StringComparer.Ordinal);
        var applied = ImmutableDictionary.CreateBuilder<string, BindingPair>(StringComparer.Ordinal);
        var defaultOccupied = new HashSet<InputChord>();
        var currentOccupied = new HashSet<InputChord>();
        foreach (BindingDefinition binding in _product.BindingSections.SelectMany(section => section.Bindings))
        {
            BindingPair defaultPair = Defaults(binding);
            defaultPair = RemoveDuplicateChords(defaultPair, binding.Id, defaultOccupied, warnings);
            defaults.Add(binding.Id, defaultPair);

            BindingPair current = stored.Bindings.TryGetValue(binding.Id, out BindingPair persisted)
                ? persisted
                : defaultPair;
            current = NormalizeStoredBinding(current, binding.Id, defaultPair, warnings);
            current = RemoveDuplicateChords(current, binding.Id, currentOccupied, warnings);
            applied.Add(binding.Id, current);
        }

        foreach (string unknown in stored.Bindings.Keys.Where(id => !_bindingDefinitions.ContainsKey(id)))
        {
            warnings.Add(new OptionsIssue(
                OptionsIssueCode.UnknownStoredBinding,
                OptionsIssueSeverity.Warning,
                unknown));
        }

        state = new BindingOpenState(defaults.ToImmutable(), applied.ToImmutable());
        return true;
    }

    private BindingPair NormalizeStoredBinding(
        BindingPair pair,
        string binding,
        BindingPair fallback,
        ImmutableArray<OptionsIssue>.Builder warnings)
    {
        InputChord? primary = NormalizeStoredChord(pair.Primary, binding, fallback.Primary, warnings);
        InputChord? secondary = NormalizeStoredChord(pair.Secondary, binding, fallback.Secondary, warnings);
        return new BindingPair(primary, secondary);
    }

    private InputChord? NormalizeStoredChord(
        InputChord? chord,
        string binding,
        InputChord? fallback,
        ImmutableArray<OptionsIssue>.Builder warnings)
    {
        if (chord is null)
        {
            return null;
        }

        try
        {
            OptionsAdapterResult<InputChord> result = _input.Validate(chord.Value);
            if (result.Succeeded)
            {
                return result.Value;
            }
        }
        catch (Exception)
        {
            // Treat malformed persisted input as data, not an adapter crash during Open.
        }

        warnings.Add(new OptionsIssue(
            OptionsIssueCode.InvalidBinding,
            OptionsIssueSeverity.Warning,
            binding));
        return fallback;
    }

    private static BindingPair RemoveDuplicateChords(
        BindingPair pair,
        string binding,
        HashSet<InputChord> occupied,
        ImmutableArray<OptionsIssue>.Builder warnings)
    {
        InputChord? primary = AddOrClear(pair.Primary);
        InputChord? secondary = AddOrClear(pair.Secondary);
        return new BindingPair(primary, secondary);

        InputChord? AddOrClear(InputChord? chord)
        {
            if (chord is null || occupied.Add(chord.Value))
            {
                return chord;
            }

            warnings.Add(new OptionsIssue(
                OptionsIssueCode.DuplicateStoredBinding,
                OptionsIssueSeverity.Warning,
                binding));
            return null;
        }
    }

    private static BindingPair Defaults(BindingDefinition binding) => new(
        binding.DefaultBindings.Length > 0 ? new InputChord(binding.DefaultBindings[0]) : null,
        binding.DefaultBindings.Length > 1 ? new InputChord(binding.DefaultBindings[1]) : null);

    private bool TryDefault(
        OptionDefinition option,
        ImmutableArray<OptionScalar> choices,
        OptionsSettingsSnapshot settings,
        out OptionScalar value)
    {
        if (option.Values.Length > 0)
        {
            value = choices[Math.Min(option.EffectiveDefaultIndex, choices.Length - 1)];
            return ValidValue(option, value, choices);
        }

        if (option.DataKind == OptionDataKind.Action)
        {
            value = OptionScalar.FromBoolean(false);
            return true;
        }

        if (option.DataKind == OptionDataKind.Boolean)
        {
            value = OptionScalar.FromBoolean(option.EffectiveDefaultIndex != 0);
            return true;
        }

        if (settings.Defaults.TryGetValue(option.Id, out value)
            && ValidValue(option, value, choices))
        {
            return true;
        }

        value = default;
        return false;
    }

    private bool ValidValue(OptionDefinition option, OptionScalar value) =>
        ValidValue(option, value, _choices.GetValueOrDefault(option.Id, option.Values));

    private bool ValidEffectiveValue(OptionDefinition option, OptionScalar value) =>
        ValidEffectiveValue(option, value, _choices.GetValueOrDefault(option.Id, option.Values));

    private bool ValidEffectiveValue(
        OptionDefinition option,
        OptionScalar value,
        ImmutableArray<OptionScalar> choices) => ValidValue(option, value, choices)
            || IsPresetControlled(option.Id)
                && value.Kind == OptionScalarKind.Number
                && IsAuthoredPresetValue(option.Id, value.Number);

    private static bool ValidValue(
        OptionDefinition option,
        OptionScalar value,
        ImmutableArray<OptionScalar> choices)
    {
        if (option.DataKind == OptionDataKind.Boolean)
        {
            return value.Kind == OptionScalarKind.Boolean;
        }

        if (option.DataKind == OptionDataKind.Action)
        {
            return false;
        }

        return choices.Length > 0 && choices.Contains(value);
    }

    private ImmutableDictionary<string, OptionScalar> StageGraphicsPreset(
        ImmutableDictionary<string, OptionScalar> candidate,
        OptionScalar selected)
    {
        OptionDefinition quality = _product.Options.Single(
            option => option.Handler == OptionHandler.QualityPreset);
        ImmutableArray<OptionScalar> qualityChoices = _choices[quality.Id];
        int qualityIndex = qualityChoices.IndexOf(selected);
        candidate = candidate.SetItem(quality.Id, selected);
        if (qualityIndex is < 0 or >= 5)
        {
            return candidate;
        }

        string presetId = OptionsProduct.RequiredPresetOrder[qualityIndex];
        GraphicsPresetDefinition preset = _product.GraphicsPresets.Single(item => item.Id == presetId);
        foreach ((string optionId, double number) in preset.Values)
        {
            OptionScalar value = OptionScalar.FromNumber(number);
            candidate = candidate.SetItem(optionId, value);
        }

        return candidate;
    }

    private ImmutableDictionary<string, OptionScalar> StageIndividualOption(
        ImmutableDictionary<string, OptionScalar> candidate,
        string changedOption)
    {
        if (!_product.GraphicsPresets.Any(preset => preset.Values.ContainsKey(changedOption)))
        {
            return candidate;
        }

        OptionDefinition quality = _product.Options.Single(option => option.Handler == OptionHandler.QualityPreset);
        ImmutableArray<OptionScalar> qualityChoices = _choices[quality.Id];
        return qualityChoices.Length >= 6
            ? candidate.SetItem(quality.Id, qualityChoices[5])
            : candidate;
    }

    private bool IsPresetControlled(string optionId) =>
        _product.GraphicsPresets.Any(preset => preset.Values.ContainsKey(optionId));

    private bool IsAuthoredPresetValue(string optionId, double value) =>
        _product.GraphicsPresets.Any(preset =>
            preset.Values.TryGetValue(optionId, out double presetValue) && presetValue == value);

    private OptionScalar ProjectForView(OptionDefinition option, OptionScalar value)
    {
        ImmutableArray<OptionScalar> choices = _choices[option.Id];
        if (IsPresetControlled(option.Id)
            && option.DataKind == OptionDataKind.Boolean
            && value.Kind == OptionScalarKind.Number)
        {
            return OptionScalar.FromBoolean(value.Number != 0);
        }

        if (!IsPresetControlled(option.Id)
            || value.Kind != OptionScalarKind.Number
            || choices.Length == 0
            || choices.Contains(value))
        {
            return value;
        }

        return choices
            .Select((choice, index) => new
            {
                Choice = choice,
                Index = index,
                Distance = choice.Kind == OptionScalarKind.Number
                    ? Math.Abs(choice.Number - value.Number)
                    : double.PositiveInfinity,
            })
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Index)
            .First()
            .Choice;
    }

    private bool PreviewAudioCandidate(ImmutableDictionary<string, OptionScalar> candidate)
    {
        ImmutableDictionary<string, OptionScalar> previous = AudioValues(_draft);
        ImmutableDictionary<string, OptionScalar> next = AudioValues(candidate);
        if (MapEqual(previous, next))
        {
            return true;
        }

        try
        {
            OptionsAdapterResult<bool> result = _audio.Preview(next);
            return result.Succeeded;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private ImmutableDictionary<string, OptionScalar> AudioValues(
        ImmutableDictionary<string, OptionScalar> values) => _product.Options
        .Where(option => option.LivePreview && values.ContainsKey(option.Id))
        .ToImmutableDictionary(option => option.Id, option => values[option.Id], StringComparer.Ordinal);

    private ImmutableDictionary<string, OptionScalar> PersistableValues(
        ImmutableDictionary<string, OptionScalar> values) => _product.Options
        .Where(option => option.DataKind != OptionDataKind.Action)
        .ToImmutableDictionary(option => option.Id, option => values[option.Id], StringComparer.Ordinal);

    private static ChatBubbleSettings ReadChatBubbleSettings(
        ImmutableDictionary<string, OptionScalar> values)
    {
        OptionScalar show = values["chat_bubbles_show"];
        OptionScalar opacity = values["chat_bubbles_opacity"];
        return new ChatBubbleSettings(show.Boolean, checked((int)opacity.Number));
    }

    private void NotifyChatBubbleSettingsChanged(ChatBubbleSettings current)
    {
        var arguments = new ChatBubbleSettingsChangedEventArgs(current);
        foreach (EventHandler<ChatBubbleSettingsChangedEventArgs> handler in
                 ChatBubbleSettingsChanged?.GetInvocationList()
                     .Cast<EventHandler<ChatBubbleSettingsChangedEventArgs>>() ?? [])
        {
            try
            {
                handler(this, arguments);
            }
            catch (Exception)
            {
                // A read-only observer cannot invalidate a completed settings transaction.
            }
        }
    }

    private bool HasAudioDraftChanges() => !MapEqual(AudioValues(_draft), AudioValues(_applied));

    private ImmutableArray<BindingLocation> FindOccupants(InputChord chord, BindingLocation target)
    {
        var occupants = ImmutableArray.CreateBuilder<BindingLocation>();
        foreach ((string binding, BindingPair pair) in _bindingDraft)
        {
            if (pair.Primary == chord && target != new BindingLocation(binding, BindingSlot.Primary))
            {
                occupants.Add(new BindingLocation(binding, BindingSlot.Primary));
            }

            if (pair.Secondary == chord && target != new BindingLocation(binding, BindingSlot.Secondary))
            {
                occupants.Add(new BindingLocation(binding, BindingSlot.Secondary));
            }
        }

        return occupants.ToImmutable();
    }

    private OptionsStoredState BuildStoredState()
    {
        var global = ImmutableDictionary.CreateBuilder<string, OptionScalar>(StringComparer.Ordinal);
        var user = ImmutableDictionary.CreateBuilder<string, OptionScalar>(StringComparer.Ordinal);
        foreach (OptionDefinition option in _product.Options.Where(option => option.DataKind != OptionDataKind.Action))
        {
            (option.Storage == OptionStorage.Global ? global : user).Add(option.Id, _draft[option.Id]);
        }

        return new OptionsStoredState(
            _storeRevision,
            global.ToImmutable(),
            user.ToImmutable(),
            _bindingDraft);
    }

    private bool ValidScope(OptionsScope scope) => scope.Kind switch
    {
        OptionsScopeKind.All => true,
        OptionsScopeKind.Bindings => true,
        OptionsScopeKind.Page => scope.Page is not null
            && _product.Pages.Any(page => page.Id == scope.Page),
        _ => false,
    };

    private bool ModalActive() => _capture is not null || _conflict is not null || _defaultsConfirmation is not null;

    private bool IsDirty() => !MapEqual(_draft, _applied) || !MapEqual(_bindingDraft, _bindingApplied);

    private static bool MapEqual<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> left,
        IReadOnlyDictionary<TKey, TValue> right)
        where TKey : notnull => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out TValue? value)
                && EqualityComparer<TValue>.Default.Equals(pair.Value, value));

    private OptionsView BuildView()
    {
        ImmutableArray<OptionRowView> options = _product.Options
            .Select(option => new OptionRowView(
                option,
                ProjectForView(option, _applied[option.Id]),
                ProjectForView(option, _draft[option.Id]),
                _defaults[option.Id],
                _choices[option.Id],
                _applied[option.Id] != _draft[option.Id]))
            .ToImmutableArray();
        ImmutableArray<BindingRowView> bindings = _product.BindingSections
            .SelectMany(section => section.Bindings)
            .Select(binding =>
            {
                BindingPair applied = _bindingApplied[binding.Id];
                BindingPair draft = _bindingDraft[binding.Id];
                BindingPair defaults = _bindingDefaults[binding.Id];
                return new BindingRowView(
                    binding,
                    applied.Primary,
                    applied.Secondary,
                    draft.Primary,
                    draft.Secondary,
                    defaults.Primary,
                    defaults.Secondary,
                    applied != draft);
            })
            .ToImmutableArray();
        bool dirty = IsDirty();
        return new OptionsView(
            _revision,
            _activePage,
            _product.Pages,
            options,
            _product.BindingSections,
            bindings,
            _capture,
            _conflict,
            _defaultsConfirmation,
            dirty,
            dirty && !ModalActive(),
            _warnings);
    }

    private OptionsTransition Transition(
        OptionsOutcome outcome,
        OptionsCloseDirective close = OptionsCloseDirective.StayOpen) =>
        new(outcome, close, BuildView(), []);

    private OptionsTransition Failure(
        OptionsIssueCode code,
        string? relatedId = null,
        bool fatal = false) => new(
            OptionsOutcome.Failed,
            OptionsCloseDirective.StayOpen,
            _opened ? BuildView() : null,
            [new OptionsIssue(code, fatal ? OptionsIssueSeverity.Fatal : OptionsIssueSeverity.Error, relatedId)]);

    private sealed record OptionOpenState(
        ImmutableDictionary<string, ImmutableArray<OptionScalar>> Choices,
        ImmutableDictionary<string, OptionScalar> Defaults,
        ImmutableDictionary<string, OptionScalar> Applied);

    private sealed record BindingOpenState(
        ImmutableDictionary<string, BindingPair> Defaults,
        ImmutableDictionary<string, BindingPair> Applied);
}
