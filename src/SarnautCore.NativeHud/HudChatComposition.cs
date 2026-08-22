namespace SarnautCore.NativeHud;

public enum HudChatChannel
{
    Whisper,
    Party,
    Say,
    Zone,
    ZoneSpecial,
    World,
    Guild,
    GuildOfficer,
    Raid,
}

public abstract record HudChatTarget
{
    private HudChatTarget()
    {
    }

    public sealed record NoTarget : HudChatTarget;

    public sealed record WhisperCharacterName : HudChatTarget
    {
        public WhisperCharacterName(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            Value = value;
        }

        public string Value { get; }
    }

    public sealed record NamedChannel : HudChatTarget
    {
        public NamedChannel(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            Value = value;
        }

        public string Value { get; }
    }

    public static NoTarget None { get; } = new();
}

public enum HudChatTargetKind
{
    None,
    WhisperCharacterName,
    NamedChannel,
}

public enum HudChatCommandAction
{
    Send,
    OpenTrade,
    Unsupported,
}

public sealed record HudChatCommandDefinition
{
    private readonly string[] _aliases;

    public HudChatCommandDefinition(
        string id,
        HudChatCommandAction action,
        HudChatChannel channel,
        HudChatTargetKind targetKind,
        int argumentCount,
        IEnumerable<string> aliases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(aliases);
        _aliases = aliases.ToArray();
        if (_aliases.Length == 0 || _aliases.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A chat command requires at least one non-empty alias.", nameof(aliases));
        }

        if (_aliases.Any(alias => alias.Contains('/') || alias.Contains('\\') || alias.Any(char.IsWhiteSpace)))
        {
            throw new ArgumentException("Chat command aliases cannot contain a prefix or whitespace.", nameof(aliases));
        }

        if (action == HudChatCommandAction.Send && argumentCount is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(argumentCount));
        }

        if (action == HudChatCommandAction.Send &&
            ((argumentCount == 0 && targetKind != HudChatTargetKind.None) ||
             (argumentCount == 1 && targetKind == HudChatTargetKind.None)))
        {
            throw new ArgumentException("Chat command target shape disagrees with its argument count.");
        }

        Id = id;
        Action = action;
        Channel = channel;
        TargetKind = targetKind;
        ArgumentCount = argumentCount;
    }

    public string Id { get; }

    public HudChatCommandAction Action { get; }

    public HudChatChannel Channel { get; }

    public HudChatTargetKind TargetKind { get; }

    public int ArgumentCount { get; }

    public ReadOnlySpan<string> Aliases => _aliases;
}

public readonly record struct HudChatSuggestion(string CommandId, string Alias);

public sealed record HudChatChannelPresentation
{
    public HudChatChannelPresentation(
        string channelId,
        byte clientChatType,
        string localizedPrefix,
        string defaultColorClass,
        bool bubbleEligible,
        HudChatChannel? runtimeChannel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localizedPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultColorClass);
        if (channelId.Any(char.IsWhiteSpace) || !HudChatText.IsWellFormedUtf16(localizedPrefix))
        {
            throw new ArgumentException("Chat channel presentation text is invalid.");
        }

        ChannelId = channelId;
        ClientChatType = clientChatType;
        LocalizedPrefix = localizedPrefix;
        DefaultColorClass = defaultColorClass;
        BubbleEligible = bubbleEligible;
        RuntimeChannel = runtimeChannel;
    }

    public string ChannelId { get; }

    public byte ClientChatType { get; }

    public string LocalizedPrefix { get; }

    public string DefaultColorClass { get; }

    public bool BubbleEligible { get; }

    public HudChatChannel? RuntimeChannel { get; }
}

public sealed class HudChatCommandCatalog
{
    private readonly char[] _prefixes;
    private readonly HudChatCommandDefinition[] _commands;
    private readonly HudChatChannelPresentation[] _channels;

    public HudChatCommandCatalog(
        IEnumerable<char> prefixes,
        IEnumerable<HudChatCommandDefinition> commands,
        int autocompleteCapacity,
        IEnumerable<HudChatChannelPresentation>? channels = null)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        ArgumentNullException.ThrowIfNull(commands);
        _prefixes = prefixes.ToArray();
        _commands = commands.ToArray();
        _channels = channels?.ToArray() ?? [];
        if (_prefixes.Length == 0 || _prefixes.Distinct().Count() != _prefixes.Length)
        {
            throw new ArgumentException("Chat prefixes must be non-empty and unique.", nameof(prefixes));
        }

        if (_commands.Length == 0)
        {
            throw new ArgumentException("Chat command catalog cannot be empty.", nameof(commands));
        }

        if (autocompleteCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(autocompleteCapacity));
        }

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commandIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HudChatCommandDefinition command in _commands)
        {
            if (!commandIds.Add(command.Id))
            {
                throw new ArgumentException($"Chat command ID '{command.Id}' is duplicated.", nameof(commands));
            }

            foreach (string alias in command.Aliases)
            {
                if (!aliases.Add(alias))
                {
                    throw new ArgumentException($"Chat command alias '{alias}' is duplicated.", nameof(commands));
                }
            }
        }

        var channelIds = new HashSet<string>(StringComparer.Ordinal);
        var clientTypes = new HashSet<byte>();
        var runtimeChannels = new HashSet<HudChatChannel>();
        foreach (HudChatChannelPresentation channel in _channels)
        {
            if (!channelIds.Add(channel.ChannelId) || !clientTypes.Add(channel.ClientChatType) ||
                (channel.RuntimeChannel is HudChatChannel runtime && !runtimeChannels.Add(runtime)))
            {
                throw new ArgumentException("Chat channel presentations must have unique IDs, client types, and runtime channels.", nameof(channels));
            }
        }

        AutocompleteCapacity = autocompleteCapacity;
    }

    public int AutocompleteCapacity { get; }

    public int CommandCount => _commands.Length;

    public int ChannelCount => _channels.Length;

    public ReadOnlySpan<HudChatChannelPresentation> Channels => _channels;

    public bool TryGetPresentation(HudChatChannel channel, out HudChatChannelPresentation? presentation)
    {
        foreach (HudChatChannelPresentation candidate in _channels)
        {
            if (candidate.RuntimeChannel == channel)
            {
                presentation = candidate;
                return true;
            }
        }

        presentation = null;
        return false;
    }

    internal bool IsPrefix(char value) => _prefixes.Contains(value);

    internal bool TryFind(string alias, out HudChatCommandDefinition? command)
    {
        foreach (HudChatCommandDefinition candidate in _commands)
        {
            foreach (string candidateAlias in candidate.Aliases)
            {
                if (string.Equals(alias, candidateAlias, StringComparison.OrdinalIgnoreCase))
                {
                    command = candidate;
                    return true;
                }
            }
        }

        command = null;
        return false;
    }

    internal int FindSuggestions(string fragment, Span<HudChatSuggestion> destination)
    {
        int count = 0;
        foreach (HudChatCommandDefinition command in _commands)
        {
            foreach (string alias in command.Aliases)
            {
                if (!alias.StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (count == destination.Length)
                {
                    return count;
                }

                destination[count++] = new HudChatSuggestion(command.Id, alias);
            }
        }

        return count;
    }
}

public sealed record HudChatSubmission(
    HudChatChannel Channel,
    string Text,
    HudChatTarget Target)
{
    public HudChatSubmission(HudChatChannel channel, string text)
        : this(channel, text, HudChatTarget.None)
    {
    }

    public HudChatSubmission Validate()
    {
        HudChatText.Validate(Text);
        bool validTarget = (Channel, Target) switch
        {
            (HudChatChannel.Whisper, HudChatTarget.WhisperCharacterName) => true,
            (HudChatChannel.Whisper, _) => false,
            (_, HudChatTarget.NoTarget) => true,
            _ => false,
        };
        if (!validTarget)
        {
            throw new ArgumentException("Chat submission target is invalid.", nameof(Target));
        }

        return this;
    }
}

public enum HudChatCommitKind
{
    None,
    Submit,
    OpenTrade,
    Unsupported,
    Throttled,
    WrongFormat,
}

public abstract record HudChatLocalAction
{
    private HudChatLocalAction()
    {
    }

    public sealed record InviteTradeByName : HudChatLocalAction
    {
        public InviteTradeByName(string playerName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
            if (!HudChatText.IsWellFormedUtf16(playerName))
            {
                throw new ArgumentException("A trade player name must be valid UTF-16.", nameof(playerName));
            }

            PlayerName = playerName;
        }

        public string PlayerName { get; }
    }

    public sealed record InviteTradeSelectedTarget : HudChatLocalAction;
}

public readonly record struct HudChatCommit(
    HudChatCommitKind Kind,
    HudChatSubmission? Submission,
    string? CommandId,
    HudChatLocalAction? LocalAction = null)
{
    public static HudChatCommit None => default;
}

/// <summary>Deterministic retail chat-line editing, autocomplete, parsing, and throttle policy.</summary>
public sealed class HudChatComposer
{
    public const int MaximumUtf16CodeUnits = 300;
    public const long ThrottleMilliseconds = 1000;

    private readonly HudChatCommandCatalog _catalog;
    private readonly HudChatSuggestion[] _suggestions;
    private int _suggestionCount;
    private int _selectedSuggestion = -1;
    private long _nextAcceptedSendAt;

    public HudChatComposer(HudChatCommandCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _suggestions = new HudChatSuggestion[catalog.AutocompleteCapacity];
    }

    public bool IsOpen { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public int SelectedSuggestionIndex => _selectedSuggestion;

    public ReadOnlySpan<HudChatSuggestion> Suggestions => _suggestions.AsSpan(0, _suggestionCount);

    public void Open()
    {
        IsOpen = true;
        Text = string.Empty;
        RefreshSuggestions();
    }

    public bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!HudChatText.IsWellFormedUtf16(text))
        {
            return false;
        }

        Text = HudChatText.TruncateAtScalarBoundary(text, MaximumUtf16CodeUnits);
        RefreshSuggestions();
        return true;
    }

    public bool MoveSuggestion(int direction)
    {
        if (_suggestionCount == 0 || direction == 0)
        {
            return false;
        }

        _selectedSuggestion = _selectedSuggestion < 0
            ? (direction > 0 ? 0 : _suggestionCount - 1)
            : (_selectedSuggestion + Math.Sign(direction) + _suggestionCount) % _suggestionCount;
        return true;
    }

    public bool ApplySelectedSuggestion()
    {
        if (_selectedSuggestion < 0 || _selectedSuggestion >= _suggestionCount || Text.Length == 0)
        {
            return false;
        }

        int tokenEnd = FindTokenEnd(Text, 1);
        string suffix = tokenEnd < Text.Length ? Text[tokenEnd..] : " ";
        Text = string.Concat(Text.AsSpan(0, 1), _suggestions[_selectedSuggestion].Alias, suffix);
        Text = HudChatText.TruncateAtScalarBoundary(Text, MaximumUtf16CodeUnits);
        RefreshSuggestions();
        return true;
    }

    public HudChatCommit Enter(long nowMilliseconds)
    {
        if (nowMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
        }

        HudChatCommit result = Parse();
        if (result.Kind == HudChatCommitKind.Submit)
        {
            if (nowMilliseconds < _nextAcceptedSendAt)
            {
                result = result with { Kind = HudChatCommitKind.Throttled, Submission = null };
            }
            else
            {
                _nextAcceptedSendAt = checked(nowMilliseconds + ThrottleMilliseconds);
            }
        }

        CloseAndClear();
        return result;
    }

    public void Escape() => CloseAndClear();

    private HudChatCommit Parse()
    {
        if (Text.Length == 0)
        {
            return HudChatCommit.None;
        }

        if (!_catalog.IsPrefix(Text[0]))
        {
            return Submit(new HudChatSubmission(HudChatChannel.Say, Text));
        }

        int commandEnd = FindTokenEnd(Text, 1);
        if (commandEnd == 1)
        {
            return HudChatCommit.None;
        }

        if (!_catalog.TryFind(Text[1..commandEnd], out HudChatCommandDefinition? command))
        {
            return new HudChatCommit(HudChatCommitKind.Unsupported, null, null);
        }

        if (command!.Action == HudChatCommandAction.OpenTrade)
        {
            string argument = commandEnd == Text.Length ? string.Empty : Text[(commandEnd + 1)..].Trim();
            if (argument is "\"\"" or "''")
            {
                return new HudChatCommit(HudChatCommitKind.WrongFormat, null, command.Id);
            }

            HudChatLocalAction action = argument.Length == 0
                ? new HudChatLocalAction.InviteTradeSelectedTarget()
                : new HudChatLocalAction.InviteTradeByName(argument);
            return new HudChatCommit(HudChatCommitKind.OpenTrade, null, command.Id, action);
        }

        if (command.Action == HudChatCommandAction.Unsupported)
        {
            return new HudChatCommit(HudChatCommitKind.Unsupported, null, command.Id);
        }

        if (commandEnd == Text.Length)
        {
            return HudChatCommit.None;
        }

        int cursor = commandEnd + 1;
        if (command.ArgumentCount == 0)
        {
            string body = Text[cursor..];
            return body.Length == 0
                ? HudChatCommit.None
                : Submit(new HudChatSubmission(command.Channel, body));
        }

        int targetEnd = FindTokenEnd(Text, cursor);
        if (targetEnd == cursor || targetEnd == Text.Length)
        {
            return HudChatCommit.None;
        }

        string target = Text[cursor..targetEnd];
        string message = Text[(targetEnd + 1)..];
        if (message.Length == 0)
        {
            return HudChatCommit.None;
        }

        HudChatTarget submissionTarget = command.TargetKind switch
        {
            HudChatTargetKind.WhisperCharacterName => new HudChatTarget.WhisperCharacterName(target),
            HudChatTargetKind.NamedChannel => new HudChatTarget.NamedChannel(target),
            _ => throw new InvalidOperationException("The validated command target kind is unsupported."),
        };
        return Submit(new HudChatSubmission(command.Channel, message, submissionTarget));
    }

    private static HudChatCommit Submit(HudChatSubmission submission) =>
        new(HudChatCommitKind.Submit, submission.Validate(), null);

    private void RefreshSuggestions()
    {
        _suggestionCount = 0;
        _selectedSuggestion = -1;
        if (Text.Length == 0 || !_catalog.IsPrefix(Text[0]))
        {
            return;
        }

        int end = FindTokenEnd(Text, 1);
        if (end != Text.Length)
        {
            return;
        }

        _suggestionCount = _catalog.FindSuggestions(Text[1..], _suggestions);
    }

    private void CloseAndClear()
    {
        IsOpen = false;
        Text = string.Empty;
        _suggestionCount = 0;
        _selectedSuggestion = -1;
    }

    private static int FindTokenEnd(string value, int start)
    {
        int index = start;
        while (index < value.Length && !char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        return index;
    }
}

public static class HudChatText
{
    public static void Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.Length > HudChatComposer.MaximumUtf16CodeUnits || !IsWellFormedUtf16(text))
        {
            throw new ArgumentException("Chat text must contain 1 to 300 valid UTF-16 code units.", nameof(text));
        }
    }

    public static bool IsWellFormedUtf16(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return !text.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF') || IsSurrogateSequenceValid(text);
    }

    public static string TruncateAtScalarBoundary(string text, int maximumCodeUnits)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumCodeUnits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCodeUnits));
        }

        if (text.Length <= maximumCodeUnits)
        {
            return text;
        }

        int length = maximumCodeUnits;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    private static bool IsSurrogateSequenceValid(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            char value = text[index];
            if (char.IsHighSurrogate(value))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(value))
            {
                return false;
            }
        }

        return true;
    }
}
