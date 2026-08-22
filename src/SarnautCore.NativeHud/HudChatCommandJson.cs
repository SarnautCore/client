using System.Text.Json;

namespace SarnautCore.NativeHud;

/// <summary>Loads the source-free chat command product baked from the classic client data.</summary>
public static class HudChatCommandJson
{
    public const string Schema = "sarnaut.chat-commands/v1";
    public const string ProductRelativePath = "catalogs/chat-commands-eng.json";
    public const string Locale = "eng";
    public const int AutocompleteCapacity = 22;

    public static HudChatCommandCatalog Parse(ReadOnlySpan<byte> utf8Json)
    {
        using JsonDocument document = JsonDocument.Parse(utf8Json.ToArray());
        JsonElement root = RequireObject(document.RootElement, "root");
        RequireProperties(root, "schema", "locale", "command_prefixes", "autocomplete_capacity", "channels",
            "runtime_options", "commands");
        RequireString(root, "schema", Schema);
        RequireString(root, "locale", Locale);

        char[] prefixes = ParsePrefixes(RequireArray(Require(root, "command_prefixes"), "command_prefixes"));
        int autocompleteCapacity = RequireIntegerInRange(root, "autocomplete_capacity", 1, int.MaxValue);
        if (!prefixes.SequenceEqual(['/', '\\']) || autocompleteCapacity != AutocompleteCapacity)
        {
            throw Error("Chat composer limits differ from the classic product.");
        }

        HudChatChannelPresentation[] channels = ParseChannels(
            RequireArray(Require(root, "channels"), "channels"));
        ParseRuntimeOptions(RequireObject(Require(root, "runtime_options"), "runtime_options"));
        HudChatCommandDefinition[] commands = ParseCommands(
            RequireArray(Require(root, "commands"), "commands"));

        return new HudChatCommandCatalog(prefixes, commands, autocompleteCapacity, channels);
    }

    private static char[] ParsePrefixes(JsonElement value)
    {
        var prefixes = new List<char>();
        foreach (JsonElement element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: 1 } prefix)
            {
                throw Error("Chat command prefixes must be single characters.");
            }

            prefixes.Add(prefix[0]);
        }

        return prefixes.ToArray();
    }

    private static HudChatChannelPresentation[] ParseChannels(JsonElement value)
    {
        var channels = new List<HudChatChannelPresentation>();
        foreach (JsonElement element in value.EnumerateArray())
        {
            JsonElement channel = RequireObject(element, "channel");
            RequireProperties(channel, "channel", "client_chat_type", "localized_prefix", "default_color_class",
                "bubble_eligible");
            string id = RequireNonemptyString(channel, "channel");
            ChannelContract contract = Channel(id);
            byte clientType = checked((byte)RequireIntegerInRange(channel, "client_chat_type", byte.MinValue, byte.MaxValue));
            string prefix = RequireNonemptyString(channel, "localized_prefix");
            string color = RequireNonemptyString(channel, "default_color_class");
            bool bubbleEligible = RequireBoolean(channel, "bubble_eligible");
            if (clientType != contract.ClientChatType || prefix != contract.LocalizedPrefix ||
                color != contract.DefaultColorClass ||
                bubbleEligible != contract.BubbleEligible)
            {
                throw Error($"Chat channel '{id}' differs from the classic presentation contract.");
            }

            channels.Add(new HudChatChannelPresentation(
                id, clientType, prefix, color, bubbleEligible, contract.RuntimeChannel));
        }

        if (channels.Count != 10 || ChannelContracts.Any(expected =>
                channels.All(actual => !string.Equals(actual.ChannelId, expected.Id, StringComparison.Ordinal))))
        {
            throw Error("Chat channel presentation census differs from the classic product.");
        }

        return channels.ToArray();
    }

    private static void ParseRuntimeOptions(JsonElement value)
    {
        RequireProperties(value, "bubbles_enabled", "bubble_opacity");
        RequireString(value, "bubbles_enabled", "chat-bubbles-show");
        RequireString(value, "bubble_opacity", "chat-bubbles-opacity");
    }

    private static HudChatCommandDefinition[] ParseCommands(JsonElement value)
    {
        var commands = new List<HudChatCommandDefinition>();
        foreach (JsonElement element in value.EnumerateArray())
        {
            JsonElement command = RequireObject(element, "command");
            RequireProperties(command, "id", "aliases", "argument_policy", "action");
            string id = RequireNonemptyString(command, "id");
            string[] aliases = ParseAliases(RequireArray(Require(command, "aliases"), $"command '{id}' aliases"));
            string argumentPolicy = RequireNonemptyString(command, "argument_policy");
            JsonElement action = RequireObject(Require(command, "action"), $"command '{id}' action");
            string kind = RequireNonemptyString(action, "kind");
            commands.Add(kind switch
            {
                "send-chat" => ParseSend(id, aliases, argumentPolicy, action),
                "client-command" => ParseClientCommand(id, aliases, argumentPolicy, action),
                "trade" => ParseTrade(id, aliases, argumentPolicy, action),
                "emote" => ParseEmote(id, aliases, argumentPolicy, action),
                _ => throw Error($"Chat command '{id}' has unsupported action kind '{kind}'."),
            });
        }

        return commands.ToArray();
    }

    private static string[] ParseAliases(JsonElement value)
    {
        var aliases = new List<string>();
        foreach (JsonElement element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            {
                throw Error("Chat command aliases must be non-empty strings.");
            }

            aliases.Add(element.GetString()!);
        }

        return aliases.ToArray();
    }

    private static HudChatCommandDefinition ParseSend(
        string id,
        string[] aliases,
        string argumentPolicy,
        JsonElement action)
    {
        RequireProperties(action, "kind", "channel");
        string channelId = RequireNonemptyString(action, "channel");
        ChannelContract channel = Channel(channelId);
        if (channel.RuntimeChannel is not HudChatChannel runtime)
        {
            if (argumentPolicy != "none")
            {
                throw Error($"Chat command '{id}' has an invalid client-only channel policy.");
            }

            return Unsupported(id, aliases);
        }

        bool targeted = runtime == HudChatChannel.Whisper;
        string expectedPolicy = targeted ? "first-token" : "none";
        if (argumentPolicy != expectedPolicy)
        {
            throw Error($"Chat command '{id}' has an invalid send argument policy.");
        }

        return new HudChatCommandDefinition(
            id,
            HudChatCommandAction.Send,
            runtime,
            targeted ? HudChatTargetKind.WhisperCharacterName : HudChatTargetKind.None,
            targeted ? 1 : 0,
            aliases);
    }

    private static HudChatCommandDefinition ParseClientCommand(
        string id,
        string[] aliases,
        string argumentPolicy,
        JsonElement action)
    {
        RequireProperties(action, "kind", "command");
        RequireNonemptyString(action, "command");
        RequireArgumentPolicy(id, argumentPolicy);
        return Unsupported(id, aliases);
    }

    private static HudChatCommandDefinition ParseTrade(
        string id,
        string[] aliases,
        string argumentPolicy,
        JsonElement action)
    {
        RequireProperties(action, "kind", "argument", "empty_fallback", "reject_npc");
        if (id != "trade" || argumentPolicy != "rest" ||
            RequireString(action, "argument") != "optional-player-name-rest" ||
            RequireString(action, "empty_fallback") != "selected-visible-player" ||
            !RequireBoolean(action, "reject_npc"))
        {
            throw Error("Trade command policy differs from the classic product.");
        }

        return new HudChatCommandDefinition(
            id, HudChatCommandAction.OpenTrade, default, default, -1, aliases);
    }

    private static HudChatCommandDefinition ParseEmote(
        string id,
        string[] aliases,
        string argumentPolicy,
        JsonElement action)
    {
        RequireProperties(action, "kind", "emote_id", "animation", "localized_name", "localized_description");
        if (RequireNonemptyString(action, "emote_id") != id)
        {
            throw Error($"Chat emote '{id}' has a mismatched product ID.");
        }

        RequireNonemptyString(action, "animation");
        RequireNonemptyString(action, "localized_name");
        RequireString(action, "localized_description");
        RequireArgumentPolicy(id, argumentPolicy);
        return Unsupported(id, aliases);
    }

    private static HudChatCommandDefinition Unsupported(string id, string[] aliases) =>
        new(id, HudChatCommandAction.Unsupported, default, default, -1, aliases);

    private static void RequireArgumentPolicy(string id, string argumentPolicy)
    {
        if (argumentPolicy is not ("none" or "rest" or "first-token"))
        {
            throw Error($"Chat command '{id}' has unsupported argument policy '{argumentPolicy}'.");
        }
    }

    private static ChannelContract Channel(string id) => ChannelContracts.SingleOrDefault(candidate => candidate.Id == id)
        ?? throw Error($"Unsupported classic chat channel '{id}'.");

    private static JsonElement Require(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement result)
            ? result
            : throw Error($"Missing chat command property '{property}'.");

    private static JsonElement RequireObject(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object ? value : throw Error($"Chat command {name} must be an object.");

    private static JsonElement RequireArray(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Array ? value : throw Error($"Chat command {name} must be an array.");

    private static string RequireString(JsonElement value, string property)
    {
        JsonElement element = Require(value, property);
        return element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw Error($"Chat command property '{property}' must be a string.");
    }

    private static string RequireNonemptyString(JsonElement value, string property)
    {
        string result = RequireString(value, property);
        return result.Length > 0 ? result : throw Error($"Chat command property '{property}' cannot be empty.");
    }

    private static void RequireString(JsonElement value, string property, string expected)
    {
        if (!string.Equals(RequireString(value, property), expected, StringComparison.Ordinal))
        {
            throw Error($"Chat command property '{property}' has an unsupported value.");
        }
    }

    private static bool RequireBoolean(JsonElement value, string property)
    {
        JsonElement element = Require(value, property);
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : throw Error($"Chat command property '{property}' must be a boolean.");
    }

    private static int RequireIntegerInRange(JsonElement value, string property, int minimum, int maximum)
    {
        JsonElement element = Require(value, property);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int result) ||
            result < minimum || result > maximum)
        {
            throw Error($"Chat command property '{property}' must be an integer from {minimum} through {maximum}.");
        }

        return result;
    }

    private static void RequireProperties(JsonElement value, params string[] allowed)
    {
        var expected = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw Error($"Chat command property '{property.Name}' is unknown or duplicated.");
            }
        }
    }

    private static JsonException Error(string message) => new(message);

    private sealed record ChannelContract(
        string Id,
        byte ClientChatType,
        string LocalizedPrefix,
        string DefaultColorClass,
        bool BubbleEligible,
        HudChatChannel? RuntimeChannel);

    private static readonly ChannelContract[] ChannelContracts =
    [
        new("say", 2, "Say", "LogColorWhite", true, HudChatChannel.Say),
        new("tell", 0, "Whisper", "LogColorMagenta", false, HudChatChannel.Whisper),
        new("psionic", 12, "Telepathy", "LogColorGold", false, null),
        new("party", 1, "Party", "LogColorBlue", false, HudChatChannel.Party),
        new("raid", 11, "Raid", "LogColorOrange", false, HudChatChannel.Raid),
        new("guild", 9, "Guild", "LogColorLightGreen", false, HudChatChannel.Guild),
        new("officer", 10, "Officer", "LogColorGreen", false, HudChatChannel.GuildOfficer),
        new("yellzone", 5, "Shout", "LogColorCian", true, HudChatChannel.ZoneSpecial),
        new("zone", 4, "Zone", "LogColorBrown", true, HudChatChannel.Zone),
        new("world", 6, "World", "LogColorGold", true, HudChatChannel.World),
    ];
}
