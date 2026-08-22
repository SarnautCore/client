using System.Collections.Immutable;

namespace SarnautCore.UI.Tests;

public sealed class OptionsRuntimeTests
{
    [Fact]
    public void OpensAnExactImmutableViewWithProductAndAdapterDefaults()
    {
        OptionsRuntime runtime = Open(out OptionsProduct product, out _, out OptionsTransition opened);

        Assert.Equal(OptionsOutcome.Opened, opened.Outcome);
        Assert.Equal(48, runtime.View.Options.Length);
        Assert.Equal(90, runtime.View.Bindings.Length);
        Assert.False(runtime.View.IsDirty);
        Assert.False(runtime.View.CanApply);
        Assert.Equal(product.Pages[0].Id, runtime.View.ActivePage);
        Assert.True(Row(runtime, "use_area_effect").Default.Boolean);
        Assert.Equal(2, Row(runtime, "gfxResolution").Choices.Length);
        Assert.Equal("1280x720", Row(runtime, "gfxResolution").Draft.Text);
    }

    [Fact]
    public void DynamicResolutionDefaultComesFromTheSettingsAdapter()
    {
        OptionsProduct product = OptionsProductFixture.Parse();
        RecordingOptionsAdapters adapters = OptionsProductFixture.Adapters(product);
        adapters.Settings.Defaults["gfxResolution"] = OptionScalar.FromText("1920x1080");
        OptionsRuntime runtime = adapters.Create(product);

        OptionsTransition opened = runtime.Open();

        Assert.True(opened.Succeeded);
        Assert.Equal("1920x1080", Row(runtime, "gfxResolution").Default.Text);
        Assert.Equal("1280x720", Row(runtime, "gfxResolution").Draft.Text);
    }

    [Fact]
    public void AudioPreviewFailureRejectsTheEditWithoutChangingTheDraft()
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);
        OptionRowView before = Row(runtime, "masterVolume");
        adapters.Audio.FailPreview = true;

        OptionsTransition result = runtime.Dispatch(new OptionsCommand.SetOption(
            "masterVolume",
            before.Choices[0]));

        Assert.False(result.Succeeded);
        Assert.Equal(OptionsIssueCode.AudioPreviewFailed, Assert.Single(result.Issues).Code);
        Assert.Equal(before.Draft, Row(runtime, "masterVolume").Draft);
        Assert.Single(adapters.Audio.Previews);
        Assert.False(runtime.View.IsDirty);
    }

    [Fact]
    public void ApplySplitsGlobalUserAndBindingsThenAdvancesTheBaseline()
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);
        runtime.Dispatch(new OptionsCommand.SetOption("use_area_effect", OptionScalar.FromBoolean(false)));
        runtime.Dispatch(new OptionsCommand.BeginBindingCapture("binding_00", BindingSlot.Primary));
        runtime.Dispatch(new OptionsCommand.OfferBinding(new InputChord("NEW_KEY")));

        OptionsTransition applied = runtime.Dispatch(new OptionsCommand.Apply());

        Assert.Equal(OptionsOutcome.Applied, applied.Outcome);
        Assert.Equal(1, adapters.Persistence.CommitCount);
        Assert.Equal(36, adapters.Persistence.State.Global.Count);
        Assert.Equal(11, adapters.Persistence.State.User.Count);
        Assert.Equal(90, adapters.Persistence.State.Bindings.Count);
        Assert.Equal("global.cfg", OptionsPersistenceLayout.Global);
        Assert.Equal("user.cfg", OptionsPersistenceLayout.User);
        Assert.Equal("key_bindings", OptionsPersistenceLayout.KeyBindings);
        Assert.Equal(1, adapters.Settings.PreparedCommitCount);
        Assert.Equal(1, adapters.Input.PreparedCommitCount);
        Assert.False(runtime.View.IsDirty);
        Assert.Equal(Row(runtime, "use_area_effect").Applied, Row(runtime, "use_area_effect").Draft);
    }

    [Fact]
    public void FailedAcceptKeepsTheScreenOpenAndDraftIntact()
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);
        runtime.Dispatch(new OptionsCommand.SetOption("use_area_effect", OptionScalar.FromBoolean(false)));
        adapters.Persistence.FailCommit = true;

        OptionsTransition failed = runtime.Dispatch(new OptionsCommand.Accept());

        Assert.False(failed.Succeeded);
        Assert.Equal(OptionsCloseDirective.StayOpen, failed.Close);
        Assert.True(runtime.View.IsDirty);
        Assert.Equal(1, adapters.Settings.PreparedCommitCount);
        Assert.Equal(1, adapters.Input.PreparedCommitCount);
        Assert.Equal(1, adapters.Settings.RollbackCount);
        Assert.Equal(1, adapters.Input.RollbackCount);
        Assert.Equal(1, adapters.Persistence.RollbackCount);
    }

    [Fact]
    public void CancelRollsBackLiveAudioWithoutWritingAndRequestsClose()
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);
        runtime.Dispatch(new OptionsCommand.SetOption("masterVolume", Row(runtime, "masterVolume").Choices[0]));

        OptionsTransition cancelled = runtime.Dispatch(new OptionsCommand.Cancel());

        Assert.Equal(OptionsOutcome.CloseRequested, cancelled.Outcome);
        Assert.Equal(OptionsCloseDirective.Cancelled, cancelled.Close);
        Assert.Single(adapters.Audio.Restores);
        Assert.Equal(0, adapters.Persistence.CommitCount);
        Assert.False(cancelled.View!.IsDirty);
    }

    [Fact]
    public void MuteStagesWithoutLivePreviewOrCancelRollback()
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);

        runtime.Dispatch(new OptionsCommand.SetOption("muteAll", OptionScalar.FromBoolean(true)));

        Assert.Empty(adapters.Audio.Previews);
        OptionsTransition cancelled = runtime.Dispatch(new OptionsCommand.Cancel());
        Assert.True(cancelled.Succeeded);
        Assert.Empty(adapters.Audio.Restores);
        Assert.Equal(0, adapters.Persistence.CommitCount);
    }

    [Fact]
    public void CancelAfterApplyKeepsTheNewAppliedBaseline()
    {
        OptionsRuntime runtime = Open(out _, out _, out _);
        runtime.Dispatch(new OptionsCommand.SetOption("use_area_effect", OptionScalar.FromBoolean(false)));
        runtime.Dispatch(new OptionsCommand.Apply());
        runtime.Dispatch(new OptionsCommand.SetOption("use_area_effect", OptionScalar.FromBoolean(true)));

        OptionsTransition cancelled = runtime.Dispatch(new OptionsCommand.Cancel());

        OptionRowView row = cancelled.View!.Options.Single(option => option.Definition.Id == "use_area_effect");
        Assert.False(row.Applied.Boolean);
        Assert.False(row.Draft.Boolean);
    }

    [Fact]
    public void BindingConflictConfirmationReplacesEveryGlobalOccupant()
    {
        OptionsRuntime runtime = Open(out _, out _, out _);
        InputChord occupied = BindingRow(runtime, "binding_00").DraftPrimary!.Value;

        runtime.Dispatch(new OptionsCommand.BeginBindingCapture("binding_01", BindingSlot.Secondary));
        OptionsTransition offered = runtime.Dispatch(new OptionsCommand.OfferBinding(occupied));

        Assert.Equal(OptionsOutcome.AwaitingConflictConfirmation, offered.Outcome);
        Assert.Equal(new BindingLocation("binding_01", BindingSlot.Secondary), runtime.View.Conflict!.Target);
        Assert.Contains(new BindingLocation("binding_00", BindingSlot.Primary), runtime.View.Conflict.Occupants);

        runtime.Dispatch(new OptionsCommand.ResolveBindingConflict(ConfirmReplacement: true));
        Assert.Null(BindingRow(runtime, "binding_00").DraftPrimary);
        Assert.Equal(occupied, BindingRow(runtime, "binding_01").DraftSecondary);
    }

    [Fact]
    public void EscapeDismissesConflictThenCaptureBeforeItCancelsTheScreen()
    {
        OptionsRuntime runtime = Open(out _, out _, out _);
        InputChord occupied = BindingRow(runtime, "binding_00").DraftPrimary!.Value;
        runtime.Dispatch(new OptionsCommand.BeginBindingCapture("binding_01", BindingSlot.Primary));
        runtime.Dispatch(new OptionsCommand.OfferBinding(occupied));

        OptionsTransition conflictEscape = runtime.Dispatch(new OptionsCommand.Escape());
        Assert.Null(conflictEscape.View!.Conflict);
        Assert.Equal(OptionsCloseDirective.StayOpen, conflictEscape.Close);

        runtime.Dispatch(new OptionsCommand.BeginBindingCapture("binding_01", BindingSlot.Primary));
        OptionsTransition captureEscape = runtime.Dispatch(new OptionsCommand.Escape());
        Assert.Null(captureEscape.View!.Capture);
        Assert.Equal(OptionsCloseDirective.StayOpen, captureEscape.Close);

        OptionsTransition screenEscape = runtime.Dispatch(new OptionsCommand.Escape());
        Assert.Equal(OptionsCloseDirective.Cancelled, screenEscape.Close);
    }

    [Fact]
    public void DefaultsRequireConfirmationAndResetUsesTheAppliedScope()
    {
        OptionsRuntime runtime = Open(out _, out _, out _);
        Assert.True(Row(runtime, "use_area_effect").Draft.Boolean);
        runtime.Dispatch(new OptionsCommand.SetOption("use_area_effect", OptionScalar.FromBoolean(false)));
        runtime.Dispatch(new OptionsCommand.Apply());
        runtime.Dispatch(new OptionsCommand.RequestDefaults(OptionsScope.ForPage("advanced_video_page")));

        Assert.NotNull(runtime.View.DefaultsConfirmation);
        Assert.False(runtime.View.CanApply);
        runtime.Dispatch(new OptionsCommand.ResolveDefaults(Confirm: true));
        Assert.True(Row(runtime, "use_area_effect").Draft.Boolean);
        Assert.False(Row(runtime, "use_area_effect").Applied.Boolean);

        runtime.Dispatch(new OptionsCommand.ResetDraft(OptionsScope.ForPage("advanced_video_page")));
        Assert.False(Row(runtime, "use_area_effect").Draft.Boolean);
    }

    [Fact]
    public void IndividualGraphicsEditMarksQualityCustom()
    {
        OptionsRuntime runtime = Open(out OptionsProduct product, out _, out _);
        string controlled = "gfx_fog_factor";
        Assert.Contains(controlled, product.GraphicsPresets[0].Values.Keys);
        OptionRowView controlledRow = Row(runtime, controlled);
        OptionScalar direct = controlledRow.Choices.First(choice => choice != controlledRow.Draft);

        runtime.Dispatch(new OptionsCommand.SetOption(controlled, direct));

        Assert.Equal(5, Row(runtime, "gfxSystemSpec").Draft.Number);
    }

    [Fact]
    public void PresetKeepsRawGlobalsWhileTheViewProjectsNearestChoice()
    {
        OptionsRuntime runtime = Open(out OptionsProduct product, out RecordingOptionsAdapters adapters, out _);
        KeyValuePair<string, double>[] rawTargets = product.GraphicsPresets[0].Values
            .Where(pair => pair.Value is 2 or 3)
            .OrderBy(pair => pair.Value)
            .ToArray();

        runtime.Dispatch(new OptionsCommand.SetOption("gfxSystemSpec", OptionScalar.FromNumber(0)));
        runtime.Dispatch(new OptionsCommand.Apply());

        Assert.Equal(2, rawTargets.Length);
        Assert.Equal(2, adapters.Persistence.State.Global[rawTargets[0].Key].Number);
        Assert.Equal(3, adapters.Persistence.State.Global[rawTargets[1].Key].Number);
        Assert.All(rawTargets, pair => Assert.Equal(1, Row(runtime, pair.Key).Draft.Number));
    }

    [Fact]
    public void EveryQualityChoiceStagesItsExactAuthoredPresetRow()
    {
        OptionsProduct product = OptionsProductFixture.Parse();
        for (int qualityIndex = 0; qualityIndex < 5; qualityIndex++)
        {
            RecordingOptionsAdapters adapters = OptionsProductFixture.Adapters(product);
            OptionsRuntime runtime = adapters.Create(product);
            Assert.True(runtime.Open().Succeeded);

            OptionScalar quality = Row(runtime, "gfxSystemSpec").Choices[qualityIndex];
            Assert.True(runtime.Dispatch(
                new OptionsCommand.SetOption("gfxSystemSpec", quality)).Succeeded);
            Assert.True(runtime.Dispatch(new OptionsCommand.Apply()).Succeeded);

            GraphicsPresetDefinition preset = product.GraphicsPresets[qualityIndex];
            Assert.Equal(OptionsProduct.RequiredPresetOrder[qualityIndex], preset.Id);
            foreach ((string optionId, double rawValue) in preset.Values)
            {
                Assert.Equal(rawValue, adapters.Persistence.State.Global[optionId].Number);
            }
        }
    }

    [Fact]
    public void CustomQualityChoiceDoesNotReplayAnyPresetRow()
    {
        OptionsRuntime runtime = Open(out OptionsProduct product, out _, out _);
        Dictionary<string, OptionScalar> before = product.GraphicsPresets[0].Values.Keys
            .ToDictionary(id => id, id => Row(runtime, id).Draft, StringComparer.Ordinal);

        OptionsTransition changed = runtime.Dispatch(new OptionsCommand.SetOption(
            "gfxSystemSpec",
            Row(runtime, "gfxSystemSpec").Choices[5]));

        Assert.True(changed.Succeeded);
        Assert.Equal(5, Row(runtime, "gfxSystemSpec").Draft.Number);
        foreach ((string optionId, OptionScalar value) in before)
        {
            Assert.Equal(value, Row(runtime, optionId).Draft);
        }
    }

    [Fact]
    public void RunsAllPresetRowsFromTheOptInRealManifest()
    {
        string? manifest = Environment.GetEnvironmentVariable("SARNAUT_OPTIONS_PRODUCT_MANIFEST");
        if (string.IsNullOrEmpty(manifest))
        {
            return;
        }

        using FileStream stream = File.OpenRead(manifest);
        OptionsProduct product = OptionsProductManifestParser.Parse(stream);
        for (int qualityIndex = 0; qualityIndex < 5; qualityIndex++)
        {
            RecordingOptionsAdapters adapters = OptionsProductFixture.Adapters(product);
            OptionsRuntime runtime = adapters.Create(product);
            Assert.True(runtime.Open().Succeeded);
            Assert.True(runtime.Dispatch(new OptionsCommand.SetOption(
                "gfxSystemSpec",
                Row(runtime, "gfxSystemSpec").Choices[qualityIndex])).Succeeded);
            Assert.True(runtime.Dispatch(new OptionsCommand.Apply()).Succeeded);

            foreach ((string optionId, double rawValue) in product.GraphicsPresets[qualityIndex].Values)
            {
                Assert.Equal(rawValue, adapters.Persistence.State.Global[optionId].Number);
            }
        }
    }

    [Fact]
    public void AutodetectStagesAdapterValuesWithoutPersisting()
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);

        OptionsTransition detected = runtime.Dispatch(
            new OptionsCommand.ActivateOption("gfxSystemSpecDefault"));

        Assert.True(detected.Succeeded);
        Assert.Equal(1, adapters.Settings.AutodetectCount);
        Assert.Equal(0, adapters.Persistence.CommitCount);
        Assert.True(runtime.View.IsDirty);
        Assert.Equal("1920x1080", Row(runtime, "gfxResolution").Draft.Text);
    }

    [Fact]
    public void MalformedPersistedValuesFallBackWithWarningsWhileRawPresetValuesRemainValid()
    {
        OptionsProduct product = OptionsProductFixture.Parse();
        RecordingOptionsAdapters adapters = OptionsProductFixture.Adapters(product);
        KeyValuePair<string, double> rawPresetValue = product.GraphicsPresets[0].Values
            .Single(pair => pair.Value == 2);
        adapters.Settings.Current["gfx_gamma"] = OptionScalar.FromNumber(0);
        adapters.Settings.Defaults["gfxResolution"] = OptionScalar.FromText("1920x1080");
        adapters.Persistence.State = adapters.Persistence.State with
        {
            Global = adapters.Persistence.State.Global
                .SetItem("gfx_gamma", OptionScalar.FromBoolean(true))
                .SetItem(rawPresetValue.Key, OptionScalar.FromNumber(rawPresetValue.Value))
                .SetItem("gfxResolution", OptionScalar.FromBoolean(true)),
            User = adapters.Persistence.State.User
                .SetItem("chat_bubbles_opacity", OptionScalar.FromNumber(99)),
        };
        OptionsRuntime runtime = adapters.Create(product);

        OptionsTransition opened = runtime.Open();

        Assert.True(opened.Succeeded);
        Assert.Equal(Row(runtime, "gfx_gamma").Default, Row(runtime, "gfx_gamma").Draft);
        Assert.Equal("1920x1080", Row(runtime, "gfxResolution").Draft.Text);
        Assert.Equal(7, Row(runtime, "chat_bubbles_opacity").Draft.Number);
        Assert.Equal(1, Row(runtime, rawPresetValue.Key).Draft.Number);
        Assert.Contains(runtime.View.Warnings, warning =>
            warning is { Code: OptionsIssueCode.InvalidStoredOption, RelatedId: "gfx_gamma" });
        Assert.Contains(runtime.View.Warnings, warning =>
            warning is { Code: OptionsIssueCode.InvalidStoredOption, RelatedId: "chat_bubbles_opacity" });
        Assert.Contains(runtime.View.Warnings, warning =>
            warning is { Code: OptionsIssueCode.InvalidStoredOption, RelatedId: "gfxResolution" });
        Assert.DoesNotContain(runtime.View.Warnings, warning => warning.RelatedId == rawPresetValue.Key);
    }

    [Fact]
    public void GlobalOptionInUserDocumentIsIgnoredAndWarned()
    {
        OptionsProduct product = OptionsProductFixture.Parse();
        RecordingOptionsAdapters adapters = OptionsProductFixture.Adapters(product);
        adapters.Settings.Current["gfx_gamma"] = OptionScalar.FromNumber(0);
        adapters.Persistence.State = adapters.Persistence.State with
        {
            User = adapters.Persistence.State.User.SetItem(
                "gfx_gamma",
                OptionScalar.FromNumber(0)),
        };
        OptionsRuntime runtime = adapters.Create(product);

        OptionsTransition opened = runtime.Open();

        Assert.True(opened.Succeeded);
        Assert.Equal(Row(runtime, "gfx_gamma").Default, Row(runtime, "gfx_gamma").Draft);
        Assert.Contains(runtime.View.Warnings, warning => warning is
        {
            Code: OptionsIssueCode.InvalidStoredOption,
            RelatedId: "gfx_gamma",
        });
    }

    [Fact]
    public void UserOptionInGlobalDocumentIsIgnoredAndWarned()
    {
        OptionsProduct product = OptionsProductFixture.Parse();
        RecordingOptionsAdapters adapters = OptionsProductFixture.Adapters(product);
        adapters.Settings.Current["chat_bubbles_opacity"] = OptionScalar.FromNumber(3);
        adapters.Persistence.State = adapters.Persistence.State with
        {
            Global = adapters.Persistence.State.Global.SetItem(
                "chat_bubbles_opacity",
                OptionScalar.FromNumber(3)),
        };
        OptionsRuntime runtime = adapters.Create(product);

        OptionsTransition opened = runtime.Open();

        Assert.True(opened.Succeeded);
        Assert.Equal(7, Row(runtime, "chat_bubbles_opacity").Draft.Number);
        Assert.Contains(runtime.View.Warnings, warning => warning is
        {
            Code: OptionsIssueCode.InvalidStoredOption,
            RelatedId: "chat_bubbles_opacity",
        });
    }

    [Fact]
    public void ValidCorrectOwnerWinsWhileWrongDuplicateStillWarns()
    {
        OptionsProduct product = OptionsProductFixture.Parse();
        RecordingOptionsAdapters adapters = OptionsProductFixture.Adapters(product);
        adapters.Persistence.State = adapters.Persistence.State with
        {
            Global = adapters.Persistence.State.Global.SetItem(
                "gfx_gamma",
                OptionScalar.FromNumber(0)),
            User = adapters.Persistence.State.User.SetItem(
                "gfx_gamma",
                OptionScalar.FromNumber(1)),
        };
        OptionsRuntime runtime = adapters.Create(product);

        Assert.True(runtime.Open().Succeeded);

        Assert.Equal(0, Row(runtime, "gfx_gamma").Draft.Number);
        Assert.Contains(runtime.View.Warnings, warning => warning is
        {
            Code: OptionsIssueCode.InvalidStoredOption,
            RelatedId: "gfx_gamma",
        });
    }

    [Fact]
    public void UnauthoredRawPresetValueFallsBackButAuthoredRawValuesSurvive()
    {
        OptionsProduct product = OptionsProductFixture.Parse();
        KeyValuePair<string, double>[] rawPresetValues = product.GraphicsPresets[0].Values
            .Where(pair => pair.Value is 2 or 3)
            .OrderBy(pair => pair.Value)
            .ToArray();
        Assert.Equal(2, rawPresetValues.Length);
        RecordingOptionsAdapters rejectedAdapters = OptionsProductFixture.Adapters(product);
        rejectedAdapters.Persistence.State = rejectedAdapters.Persistence.State with
        {
            Global = rejectedAdapters.Persistence.State.Global.SetItem(
                rawPresetValues[0].Key,
                OptionScalar.FromNumber(999)),
        };
        OptionsRuntime rejected = rejectedAdapters.Create(product);

        Assert.True(rejected.Open().Succeeded);
        Assert.Equal(
            Row(rejected, rawPresetValues[0].Key).Default,
            Row(rejected, rawPresetValues[0].Key).Draft);
        Assert.Contains(rejected.View.Warnings, warning => warning.RelatedId == rawPresetValues[0].Key);
        Assert.Throws<ArgumentOutOfRangeException>(() => OptionScalar.FromNumber(double.NaN));

        RecordingOptionsAdapters acceptedAdapters = OptionsProductFixture.Adapters(product);
        acceptedAdapters.Persistence.State = acceptedAdapters.Persistence.State with
        {
            Global = acceptedAdapters.Persistence.State.Global
                .SetItem(rawPresetValues[0].Key, OptionScalar.FromNumber(rawPresetValues[0].Value))
                .SetItem(rawPresetValues[1].Key, OptionScalar.FromNumber(rawPresetValues[1].Value)),
        };
        OptionsRuntime accepted = acceptedAdapters.Create(product);

        Assert.True(accepted.Open().Succeeded);
        Assert.DoesNotContain(accepted.View.Warnings, warning =>
            rawPresetValues.Any(pair => pair.Key == warning.RelatedId));
        Assert.All(rawPresetValues, pair => Assert.Equal(1, Row(accepted, pair.Key).Draft.Number));
    }

    [Fact]
    public void ChatSettingsPublishOnlyAfterACompletedApply()
    {
        OptionsRuntime runtime = Open(out _, out _, out _);
        var observed = new List<ChatBubbleSettings>();
        runtime.ChatBubbleSettingsChanged += (_, args) => observed.Add(args.Current);

        Assert.Equal(new ChatBubbleSettings(Show: true, Opacity: 7),
            runtime.CurrentChatBubbleSettings);
        runtime.Dispatch(new OptionsCommand.SetOption(
            "chat_bubbles_show",
            OptionScalar.FromBoolean(false)));
        runtime.Dispatch(new OptionsCommand.SetOption(
            "chat_bubbles_opacity",
            OptionScalar.FromNumber(3)));

        Assert.Empty(observed);
        Assert.Equal(new ChatBubbleSettings(Show: true, Opacity: 7),
            runtime.CurrentChatBubbleSettings);

        OptionsTransition applied = runtime.Dispatch(new OptionsCommand.Apply());

        Assert.True(applied.Succeeded);
        Assert.Equal(new ChatBubbleSettings(Show: false, Opacity: 3),
            runtime.CurrentChatBubbleSettings);
        Assert.Equal([new ChatBubbleSettings(Show: false, Opacity: 3)], observed);
    }

    [Fact]
    public void FaultingChatObserverCannotInvalidateACommittedTransaction()
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);
        runtime.ChatBubbleSettingsChanged += (_, _) => throw new InvalidOperationException("observer");
        runtime.Dispatch(new OptionsCommand.SetOption(
            "chat_bubbles_show",
            OptionScalar.FromBoolean(false)));

        OptionsTransition applied = runtime.Dispatch(new OptionsCommand.Apply());

        Assert.True(applied.Succeeded);
        Assert.Equal(1, adapters.Persistence.CommitCount);
        Assert.False(runtime.CurrentChatBubbleSettings.Show);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("input")]
    [InlineData("store")]
    public void ApplyPreparationAndPersistenceFailuresPreserveDraft(string failure)
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);
        runtime.Dispatch(new OptionsCommand.SetOption("use_area_effect", OptionScalar.FromBoolean(false)));
        adapters.Settings.FailPrepare = failure == "settings";
        adapters.Input.FailPrepare = failure == "input";
        adapters.Persistence.FailCommit = failure == "store";

        OptionsTransition result = runtime.Dispatch(new OptionsCommand.Apply());

        Assert.False(result.Succeeded);
        Assert.True(runtime.View.IsDirty);
        Assert.False(Row(runtime, "use_area_effect").Draft.Boolean);
    }

    [Theory]
    [InlineData("settings", false)]
    [InlineData("settings", true)]
    [InlineData("input", false)]
    [InlineData("input", true)]
    [InlineData("persistence", false)]
    [InlineData("persistence", true)]
    public void CommitFailureRestoresEveryPreviouslyCommittedAdapter(
        string lane,
        bool throws)
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);
        runtime.Dispatch(new OptionsCommand.SetOption("use_area_effect", OptionScalar.FromBoolean(false)));
        runtime.Dispatch(new OptionsCommand.BeginBindingCapture("binding_00", BindingSlot.Primary));
        runtime.Dispatch(new OptionsCommand.OfferBinding(new InputChord("BASELINE_KEY")));
        Assert.True(runtime.Dispatch(new OptionsCommand.Apply()).Succeeded);
        ImmutableDictionary<string, OptionScalar> settingsBaseline = adapters.Settings.Committed;
        ImmutableDictionary<string, BindingPair> inputBaseline = adapters.Input.Installed;
        OptionsStoredState storeBaseline = adapters.Persistence.State;

        runtime.Dispatch(new OptionsCommand.SetOption("use_area_effect", OptionScalar.FromBoolean(true)));
        runtime.Dispatch(new OptionsCommand.BeginBindingCapture("binding_00", BindingSlot.Primary));
        runtime.Dispatch(new OptionsCommand.OfferBinding(new InputChord("DRAFT_KEY")));
        SetCommitFailure(adapters, lane, throws, enabled: true);

        OptionsTransition failed = runtime.Dispatch(new OptionsCommand.Apply());

        Assert.False(failed.Succeeded);
        Assert.True(runtime.View.IsDirty);
        Assert.False(Row(runtime, "use_area_effect").Applied.Boolean);
        Assert.True(Row(runtime, "use_area_effect").Draft.Boolean);
        Assert.Same(settingsBaseline, adapters.Settings.Committed);
        Assert.Same(inputBaseline, adapters.Input.Installed);
        Assert.Same(storeBaseline, adapters.Persistence.State);
        Assert.Equal(lane is "input" or "persistence" ? 1 : 0, adapters.Input.RollbackCount);
        Assert.Equal(lane is "persistence" ? 1 : 0, adapters.Persistence.RollbackCount);
        Assert.Equal(1, adapters.Settings.RollbackCount);

        SetCommitFailure(adapters, lane, throws, enabled: false);
        Assert.True(runtime.Dispatch(new OptionsCommand.Apply()).Succeeded);
        Assert.False(runtime.View.IsDirty);
        Assert.Equal(storeBaseline.Revision + 1, adapters.Persistence.State.Revision);
    }

    [Fact]
    public void RollbackViolationIsFatalButStillRollsBackEveryOtherPlan()
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);
        runtime.Dispatch(new OptionsCommand.SetOption("use_area_effect", OptionScalar.FromBoolean(false)));
        Assert.True(runtime.Dispatch(new OptionsCommand.Apply()).Succeeded);
        ImmutableDictionary<string, OptionScalar> settingsBaseline = adapters.Settings.Committed;
        OptionsStoredState storeBaseline = adapters.Persistence.State;

        runtime.Dispatch(new OptionsCommand.SetOption("use_area_effect", OptionScalar.FromBoolean(true)));
        adapters.Persistence.FailCommit = true;
        adapters.Input.FailRollback = true;

        OptionsTransition failed = runtime.Dispatch(new OptionsCommand.Apply());

        OptionsIssue issue = Assert.Single(failed.Issues);
        Assert.Equal(OptionsIssueCode.RollbackContractViolation, issue.Code);
        Assert.Equal(OptionsIssueSeverity.Fatal, issue.Severity);
        Assert.True(runtime.View.IsDirty);
        Assert.False(Row(runtime, "use_area_effect").Applied.Boolean);
        Assert.True(Row(runtime, "use_area_effect").Draft.Boolean);
        Assert.Same(settingsBaseline, adapters.Settings.Committed);
        Assert.Same(storeBaseline, adapters.Persistence.State);
        Assert.Equal(1, adapters.Settings.RollbackCount);
        Assert.Equal(1, adapters.Input.RollbackCount);
        Assert.Equal(1, adapters.Persistence.RollbackCount);
    }

    [Fact]
    public void RollbackViolationQuarantinesTheRuntimeWithoutFurtherAdapterCalls()
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);
        runtime.Dispatch(new OptionsCommand.SetOption(
            "masterVolume",
            Row(runtime, "masterVolume").Choices[0]));
        adapters.Persistence.FailCommit = true;
        adapters.Input.FailRollback = true;

        OptionsTransition failed = runtime.Dispatch(new OptionsCommand.Apply());
        Assert.Equal(OptionsIssueCode.RollbackContractViolation, Assert.Single(failed.Issues).Code);
        Assert.False(failed.View!.CanApply);

        int settingsCommits = adapters.Settings.PreparedCommitCount;
        int inputCommits = adapters.Input.PreparedCommitCount;
        int storeCommits = adapters.Persistence.CommitCount;
        int audioRestores = adapters.Audio.Restores.Count;
        adapters.Persistence.FailCommit = false;
        adapters.Input.FailRollback = false;

        OptionsTransition edit = runtime.Dispatch(new OptionsCommand.SetOption(
            "muteAll",
            OptionScalar.FromBoolean(true)));
        OptionsTransition retry = runtime.Dispatch(new OptionsCommand.Apply());
        OptionsTransition reopen = runtime.Open();
        runtime.Dispose();

        Assert.All([edit, retry, reopen], transition =>
        {
            OptionsIssue issue = Assert.Single(transition.Issues);
            Assert.Equal(OptionsIssueCode.RollbackContractViolation, issue.Code);
            Assert.Equal(OptionsIssueSeverity.Fatal, issue.Severity);
            Assert.False(transition.View!.CanApply);
        });
        Assert.Equal(settingsCommits, adapters.Settings.PreparedCommitCount);
        Assert.Equal(inputCommits, adapters.Input.PreparedCommitCount);
        Assert.Equal(storeCommits, adapters.Persistence.CommitCount);
        Assert.Equal(audioRestores, adapters.Audio.Restores.Count);
    }

    [Fact]
    public void ThrowingInputPrepareDisposesThePreparedSettingsPlan()
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);
        runtime.Dispatch(new OptionsCommand.SetOption("use_area_effect", OptionScalar.FromBoolean(false)));
        adapters.Input.ThrowOnPrepare = true;

        OptionsTransition result = runtime.Dispatch(new OptionsCommand.Apply());

        Assert.False(result.Succeeded);
        Assert.NotNull(adapters.Settings.LastPlan);
        Assert.True(adapters.Settings.LastPlan.IsDisposed);
        Assert.Equal(0, adapters.Persistence.CommitCount);
    }

    [Fact]
    public void ConcurrentStoreChangePreservesTheOpenDraftAndBaseline()
    {
        OptionsRuntime runtime = Open(out _, out RecordingOptionsAdapters adapters, out _);
        runtime.Dispatch(new OptionsCommand.SetOption("use_area_effect", OptionScalar.FromBoolean(false)));
        adapters.Persistence.State = adapters.Persistence.State with { Revision = 1 };

        OptionsTransition result = runtime.Dispatch(new OptionsCommand.Apply());

        Assert.False(result.Succeeded);
        Assert.Equal(OptionsIssueCode.ConcurrentStoreChange, Assert.Single(result.Issues).Code);
        Assert.True(runtime.View.IsDirty);
        Assert.True(Row(runtime, "use_area_effect").Applied.Boolean);
        Assert.False(Row(runtime, "use_area_effect").Draft.Boolean);
        Assert.Equal(0, adapters.Persistence.CommitCount);
        Assert.True(runtime.Dispatch(new OptionsCommand.Cancel()).Succeeded);
    }

    private static OptionsRuntime Open(
        out OptionsProduct product,
        out RecordingOptionsAdapters adapters,
        out OptionsTransition opened)
    {
        product = OptionsProductFixture.Parse();
        adapters = OptionsProductFixture.Adapters(product);
        OptionsRuntime runtime = adapters.Create(product);
        opened = runtime.Open();
        Assert.True(opened.Succeeded);
        return runtime;
    }

    private static void SetCommitFailure(
        RecordingOptionsAdapters adapters,
        string lane,
        bool throws,
        bool enabled)
    {
        if (lane == "settings")
        {
            adapters.Settings.FailCommit = enabled && !throws;
            adapters.Settings.ThrowOnCommit = enabled && throws;
        }
        else if (lane == "input")
        {
            adapters.Input.FailCommit = enabled && !throws;
            adapters.Input.ThrowOnCommit = enabled && throws;
        }
        else
        {
            adapters.Persistence.FailCommit = enabled && !throws;
            adapters.Persistence.ThrowOnCommit = enabled && throws;
        }
    }

    private static OptionRowView Row(OptionsRuntime runtime, string option) =>
        runtime.View.Options.Single(row => row.Definition.Id == option);

    private static BindingRowView BindingRow(OptionsRuntime runtime, string binding) =>
        runtime.View.Bindings.Single(row => row.Definition.Id == binding);
}
