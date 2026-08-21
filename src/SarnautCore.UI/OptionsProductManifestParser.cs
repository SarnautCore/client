using System.Collections.Immutable;
using System.Text.Json;

namespace SarnautCore.UI;

public static class OptionsProductManifestParser
{
    public static OptionsProduct Parse(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });

        JsonElement root = document.RootElement;
        Object(root, "options product");
        Only(
            root,
            "options product",
            "schema_id",
            "scene",
            "layout",
            "pages",
            "options",
            "graphics_presets",
            "binding_slots",
            "binding_sections",
            "content");

        string schemaId = String(root, "schema_id", "options product");
        if (schemaId != OptionsProduct.SchemaId)
        {
            throw new InvalidDataException($"Unsupported options product schema '{schemaId}'");
        }

        OptionsLayout layout = ReadLayout(Required(root, "layout", JsonValueKind.Object, "options product"));
        ImmutableArray<OptionsPageDefinition> pages = Array(root, "pages", ReadPage, "options product");
        ImmutableArray<OptionDefinition> options = Array(root, "options", ReadOption, "options product");
        ImmutableArray<GraphicsPresetDefinition> presets = Array(
            root,
            "graphics_presets",
            ReadPreset,
            "options product");
        ImmutableArray<BindingSectionDefinition> sections = Array(
            root,
            "binding_sections",
            ReadBindingSection,
            "options product");
        int bindingSlots = Integer(root, "binding_slots", "options product");
        OptionsContentInventory content = ReadContent(
            Required(root, "content", JsonValueKind.Object, "options product"));

        var product = new OptionsProduct(
            Path(root, "scene", [".tscn", ".scn"], "options product"),
            layout,
            pages,
            options,
            presets,
            bindingSlots,
            sections,
            content);
        Validate(product);
        return product;
    }

    private static OptionsLayout ReadLayout(JsonElement element)
    {
        const string Context = "options layout";
        Object(element, Context);
        Only(
            element,
            Context,
            "width",
            "height",
            "horizontal_align",
            "vertical_align",
            "priority",
            "child_order");
        return new OptionsLayout(
            Integer(element, "width", Context),
            Integer(element, "height", Context),
            Enum<OptionsAlignment>(element, "horizontal_align", Context),
            Enum<OptionsAlignment>(element, "vertical_align", Context),
            Integer(element, "priority", Context),
            Array(element, "child_order", item => String(item, $"{Context}.child_order"), Context));
    }

    private static OptionsPageDefinition ReadPage(JsonElement element)
    {
        const string Context = "options page";
        Object(element, Context);
        Only(element, Context, "id", "name", "description", "groups");
        string id = Id(element, "id", Context);
        return new OptionsPageDefinition(
            id,
            CleanTextAllowEmpty(element, "name", $"page '{id}'"),
            CleanTextAllowEmpty(element, "description", $"page '{id}'"),
            Array(element, "groups", item => ReadGroup(item, id), $"page '{id}'"));
    }

    private static OptionsGroupDefinition ReadGroup(JsonElement element, string page)
    {
        string context = $"page '{page}' group";
        Object(element, context);
        Only(element, context, "id", "name", "description", "blocks");
        string id = Id(element, "id", context);
        return new OptionsGroupDefinition(
            id,
            CleanTextAllowEmpty(element, "name", $"group '{id}'"),
            CleanTextAllowEmpty(element, "description", $"group '{id}'"),
            Array(element, "blocks", item => ReadBlock(item, id), $"group '{id}'"));
    }

    private static OptionsBlockDefinition ReadBlock(JsonElement element, string group)
    {
        string context = $"group '{group}' block";
        Object(element, context);
        Only(element, context, "id", "name", "description", "options");
        string id = Id(element, "id", context);
        return new OptionsBlockDefinition(
            id,
            CleanTextAllowEmpty(element, "name", $"block '{id}'"),
            CleanTextAllowEmpty(element, "description", $"block '{id}'"),
            Array(element, "options", item => Id(item, $"block '{id}'.options"), $"block '{id}'"));
    }

    private static OptionDefinition ReadOption(JsonElement element)
    {
        const string Context = "option";
        Object(element, Context);
        Only(
            element,
            Context,
            "id",
            "page",
            "group",
            "block",
            "name",
            "description",
            "storage",
            "data_kind",
            "view_kind",
            "authored_default_index",
            "effective_default_index",
            "declared_value_count",
            "values",
            "value_names",
            "value_descriptions",
            "live_preview",
            "handler");
        string id = Id(element, "id", Context);
        string context = $"option '{id}'";
        return new OptionDefinition(
            id,
            Id(element, "page", context),
            Id(element, "group", context),
            Id(element, "block", context),
            CleanTextAllowEmpty(element, "name", context),
            CleanTextAllowEmpty(element, "description", context),
            Enum<OptionStorage>(element, "storage", context),
            Enum<OptionDataKind>(element, "data_kind", context),
            Enum<OptionViewKind>(element, "view_kind", context),
            NonNegativeInteger(element, "authored_default_index", context),
            NonNegativeInteger(element, "effective_default_index", context),
            OptionalNonNegativeInteger(element, "declared_value_count", context),
            Array(element, "values", item => Scalar(item, $"{context}.values"), context),
            Array(element, "value_names", item => CleanTextAllowEmpty(item, $"{context}.value_names"), context),
            Array(
                element,
                "value_descriptions",
                item => CleanTextAllowEmpty(item, $"{context}.value_descriptions"),
                context),
            Boolean(element, "live_preview", context),
            Enum<OptionHandler>(element, "handler", context));
    }

    private static GraphicsPresetDefinition ReadPreset(JsonElement element)
    {
        const string Context = "graphics preset";
        Object(element, Context);
        Only(element, Context, "id", "values");
        string id = Id(element, "id", Context);
        JsonElement values = Required(element, "values", JsonValueKind.Object, $"preset '{id}'");
        var builder = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in values.EnumerateObject())
        {
            Id(property.Name, $"preset '{id}' value");
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException($"preset '{id}' repeats value '{property.Name}'");
            }

            builder.Add(property.Name, Number(property.Value, $"preset '{id}' value '{property.Name}'"));
        }

        return new GraphicsPresetDefinition(id, builder.ToImmutable());
    }

    private static BindingSectionDefinition ReadBindingSection(JsonElement element)
    {
        const string Context = "binding section";
        Object(element, Context);
        Only(element, Context, "id", "name", "assignment_slots", "bindings");
        string id = Id(element, "id", Context);
        return new BindingSectionDefinition(
            id,
            CleanTextAllowEmpty(element, "name", $"binding section '{id}'"),
            NonNegativeInteger(element, "assignment_slots", $"binding section '{id}'"),
            Array(
                element,
                "bindings",
                item => ReadBinding(item, id),
                $"binding section '{id}'"));
    }

    private static BindingDefinition ReadBinding(JsonElement element, string owner)
    {
        string context = $"binding in section '{owner}'";
        Object(element, context);
        Only(
            element,
            context,
            "id",
            "section",
            "name",
            "activation",
            "any_modifiers",
            "default_bindings");
        string id = Id(element, "id", context);
        return new BindingDefinition(
            id,
            Id(element, "section", $"binding '{id}'"),
            CleanTextAllowEmpty(element, "name", $"binding '{id}'"),
            Enum<BindingActivation>(element, "activation", $"binding '{id}'"),
            Boolean(element, "any_modifiers", $"binding '{id}'"),
            Array(
                element,
                "default_bindings",
                item => String(item, $"binding '{id}'.default_bindings"),
                $"binding '{id}'"));
    }

    private static OptionsContentInventory ReadContent(JsonElement element)
    {
        const string Context = "options content";
        Object(element, Context);
        Only(element, Context, "scenes", "resources", "textures");
        return new OptionsContentInventory(
            Array(element, "scenes", item => Path(item, [".tscn", ".scn"], $"{Context}.scenes"), Context),
            Array(element, "resources", item => Path(item, [".tres", ".res"], $"{Context}.resources"), Context),
            Array(element, "textures", item => Path(item, [".png"], $"{Context}.textures"), Context));
    }

    private static void Validate(OptionsProduct product)
    {
        if (product.Layout.Width != OptionsProduct.RequiredWidth
            || product.Layout.Height != OptionsProduct.RequiredHeight
            || product.Layout.HorizontalAlign != OptionsAlignment.Center
            || product.Layout.VerticalAlign != OptionsAlignment.Center
            || product.Layout.Priority != OptionsProduct.RequiredPriority
            || !product.Layout.ChildOrder.SequenceEqual(OptionsProduct.RequiredChildOrder))
        {
            throw new InvalidDataException("Options layout does not match the authored 750x673 contract");
        }

        RequireCount(product.Pages.Length, OptionsProduct.RequiredPageCount, "pages");
        RequireIdsInOrder(product.Pages.Select(page => page.Id), OptionsProduct.RequiredPageOrder, "page");
        OptionsGroupDefinition[] groups = product.Pages.SelectMany(page => page.Groups).ToArray();
        RequireCount(groups.Length, OptionsProduct.RequiredGroupCount, "groups");
        OptionsBlockDefinition[] blocks = groups.SelectMany(group => group.Blocks).ToArray();
        RequireCount(blocks.Length, OptionsProduct.RequiredBlockCount, "blocks");
        RequireCount(product.Options.Length, OptionsProduct.RequiredOptionCount, "options");
        RequireIdsInOrder(
            product.Options.Select(option => option.Id),
            OptionsProduct.RequiredOptionOrder,
            "option");
        RequireSequence(
            product.Pages.SelectMany(page => new[] { page.Groups.SelectMany(group => group.Blocks).Sum(block => block.Options.Length) }),
            [6, 16, 7, 19],
            "page option census");
        RequireCount(product.GraphicsPresets.Length, OptionsProduct.RequiredPresetCount, "graphics presets");
        RequireIdsInOrder(
            product.GraphicsPresets.Select(preset => preset.Id),
            OptionsProduct.RequiredPresetOrder,
            "graphics preset");
        RequireCount(product.BindingSections.Length, OptionsProduct.RequiredBindingSectionCount, "binding sections");
        RequireIdsInOrder(
            product.BindingSections.Select(section => section.Id),
            OptionsProduct.RequiredBindingSectionOrder,
            "binding section");
        RequireCount(
            product.BindingSections.Sum(section => section.Bindings.Length),
            OptionsProduct.RequiredBindingCount,
            "bindings");
        RequireSequence(
            product.BindingSections.Select(section => section.Bindings.Length),
            [13, 10, 44, 8, 8, 7],
            "binding section row census");
        RequireCount(
            product.BindingSections.SelectMany(section => section.Bindings)
                .Sum(binding => binding.DefaultBindings.Length),
            94,
            "nonempty binding defaults");
        RequireCount(product.BindingSlots, OptionsProduct.RequiredBindingSlots, "binding slots");

        Unique(product.Pages.Select(page => page.Id), "page id");
        Unique(groups.Select(group => group.Id), "group id");
        Unique(blocks.Select(block => block.Id), "block id");
        Unique(product.Options.Select(option => option.Id), "option id");
        Unique(product.GraphicsPresets.Select(preset => preset.Id), "graphics preset id");
        Unique(product.BindingSections.Select(section => section.Id), "binding section id");
        Unique(product.BindingSections.SelectMany(section => section.Bindings).Select(binding => binding.Id), "binding id");

        var pageOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var groupOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var blockOwners = new Dictionary<string, (string Page, string Group)>(StringComparer.Ordinal);
        foreach (OptionsPageDefinition page in product.Pages)
        {
            pageOwners.Add(page.Id, page.Id);
            foreach (OptionsGroupDefinition group in page.Groups)
            {
                groupOwners.Add(group.Id, page.Id);
                foreach (OptionsBlockDefinition block in group.Blocks)
                {
                    blockOwners.Add(block.Id, (page.Id, group.Id));
                    Unique(block.Options, $"block '{block.Id}' option id");
                }
            }
        }

        string[] placedOptions = blocks.SelectMany(block => block.Options).ToArray();
        RequireCount(placedOptions.Length, product.Options.Length, "placed options");
        Unique(placedOptions, "placed option id");
        RequireIdsInOrder(placedOptions, OptionsProduct.RequiredOptionOrder, "placed option");
        var byOption = product.Options.ToDictionary(option => option.Id, StringComparer.Ordinal);
        foreach (OptionsBlockDefinition block in blocks)
        {
            (string page, string group) = blockOwners[block.Id];
            foreach (string optionId in block.Options)
            {
                if (!byOption.TryGetValue(optionId, out OptionDefinition? option))
                {
                    throw new InvalidDataException($"Block '{block.Id}' references unknown option '{optionId}'");
                }

                if (option.Page != page || option.Group != group || option.Block != block.Id)
                {
                    throw new InvalidDataException($"Option '{option.Id}' has inconsistent page/group/block ownership");
                }
            }
        }

        foreach (OptionDefinition option in product.Options)
        {
            ValidateOption(option);
        }

        RequireEnumCensus(product.Options.Select(option => option.Storage),
            (OptionStorage.Global, 37),
            (OptionStorage.User, 11));
        RequireEnumCensus(product.Options.Select(option => option.DataKind),
            (OptionDataKind.Boolean, 16),
            (OptionDataKind.Discrete, 16),
            (OptionDataKind.DiscreteFloat, 15),
            (OptionDataKind.Action, 1));
        RequireEnumCensus(product.Options.Select(option => option.ViewKind),
            (OptionViewKind.Checkbox, 16),
            (OptionViewKind.List, 15),
            (OptionViewKind.Slider, 16),
            (OptionViewKind.Button, 1));
        RequireEnumCensus(product.Options.Select(option => option.Handler),
            (OptionHandler.None, 37),
            (OptionHandler.Audio, 7),
            (OptionHandler.Resolution, 1),
            (OptionHandler.Localization, 1),
            (OptionHandler.QualityPreset, 1),
            (OptionHandler.Autodetect, 1));
        RequireCount(product.Options.Count(option => option.LivePreview), 6, "live-preview options");
        RequireExactOptionSet(
            product.Options.Where(option => option.Storage == OptionStorage.User),
            [
                "show_all_overtips", "show_all_titles", "show_all_healthbars", "show_all_status",
                "show_hostile_overtips", "show_hostile_titles", "show_hostile_healthbars",
                "show_hostile_status", "chat_bubbles_show", "emote_icons_show",
                "chat_bubbles_opacity",
            ],
            "user storage");
        RequireExactOptionSet(
            product.Options.Where(option => option.Handler == OptionHandler.Audio),
            [
                "masterVolume", "muteAll", "ambientVolume", "interfaceVolume", "musicVolume",
                "sfxVolume", "voiceVolume",
            ],
            "audio handler");
        RequireExactOptionSet(
            product.Options.Where(option => option.LivePreview),
            ["masterVolume", "ambientVolume", "interfaceVolume", "musicVolume", "sfxVolume", "voiceVolume"],
            "live preview");
        RequireSingleHandler(product, OptionHandler.Resolution, "gfxResolution");
        RequireSingleHandler(product, OptionHandler.Localization, "localization");
        RequireSingleHandler(product, OptionHandler.QualityPreset, "gfxSystemSpec");
        RequireSingleHandler(product, OptionHandler.Autodetect, "gfxSystemSpecDefault");
        RequireOptionShape(
            product,
            "gfxResolution",
            OptionStorage.Global,
            OptionDataKind.Discrete,
            OptionViewKind.List,
            OptionHandler.Resolution,
            livePreview: false);
        RequireOptionShape(
            product,
            "localization",
            OptionStorage.Global,
            OptionDataKind.Discrete,
            OptionViewKind.List,
            OptionHandler.Localization,
            livePreview: false);
        RequireOptionShape(
            product,
            "gfxSystemSpec",
            OptionStorage.Global,
            OptionDataKind.Discrete,
            OptionViewKind.Slider,
            OptionHandler.QualityPreset,
            livePreview: false);
        RequireOptionShape(
            product,
            "gfxSystemSpecDefault",
            OptionStorage.Global,
            OptionDataKind.Action,
            OptionViewKind.Button,
            OptionHandler.Autodetect,
            livePreview: false);
        RequireOptionShape(
            product,
            "chat_bubbles_show",
            OptionStorage.User,
            OptionDataKind.Boolean,
            OptionViewKind.Checkbox,
            OptionHandler.None,
            livePreview: false);
        RequireOptionShape(
            product,
            "chat_bubbles_opacity",
            OptionStorage.User,
            OptionDataKind.Discrete,
            OptionViewKind.Slider,
            OptionHandler.None,
            livePreview: false);
        foreach (string volume in new[]
                 {
                     "masterVolume", "ambientVolume", "interfaceVolume", "musicVolume",
                     "sfxVolume", "voiceVolume",
                 })
        {
            RequireOptionShape(
                product,
                volume,
                OptionStorage.Global,
                OptionDataKind.DiscreteFloat,
                OptionViewKind.Slider,
                OptionHandler.Audio,
                livePreview: true);
        }

        RequireOptionShape(
            product,
            "muteAll",
            OptionStorage.Global,
            OptionDataKind.Boolean,
            OptionViewKind.Checkbox,
            OptionHandler.Audio,
            livePreview: false);
        OptionDefinition chatOpacity = byOption["chat_bubbles_opacity"];
        if (!chatOpacity.Values.SequenceEqual(
                Enumerable.Range(0, 11).Select(value => OptionScalar.FromNumber(value)))
            || chatOpacity.AuthoredDefaultIndex != 7
            || chatOpacity.EffectiveDefaultIndex != 7
            || chatOpacity.DeclaredValueCount != 0)
        {
            throw new InvalidDataException("chat_bubbles_opacity does not match the authored 0..10 contract");
        }

        foreach (GraphicsPresetDefinition preset in product.GraphicsPresets)
        {
            RequireCount(
                preset.Values.Count,
                OptionsProduct.RequiredPresetValueCount,
                $"graphics preset '{preset.Id}' values");
            foreach (string optionId in preset.Values.Keys)
            {
                if (!byOption.ContainsKey(optionId))
                {
                    throw new InvalidDataException($"Graphics preset '{preset.Id}' references unknown option '{optionId}'");
                }
            }

            RequireExactIds(
                preset.Values.Keys,
                OptionsProduct.RequiredPresetOptionIds,
                $"graphics preset '{preset.Id}' option");
        }

        foreach (BindingSectionDefinition section in product.BindingSections)
        {
            RequireCount(
                section.AssignmentSlots,
                OptionsProduct.RequiredBindingSlots,
                $"binding section '{section.Id}' assignment slots");
            foreach (BindingDefinition binding in section.Bindings)
            {
                if (binding.Section != section.Id)
                {
                    throw new InvalidDataException($"Binding '{binding.Id}' has inconsistent section ownership");
                }

                if (binding.DefaultBindings.Length > OptionsProduct.RequiredBindingSlots)
                {
                    throw new InvalidDataException($"Binding '{binding.Id}' declares more than two defaults");
                }
            }
        }

        Unique(product.Content.Scenes.Select(path => path.Value), "content scene");
        Unique(product.Content.Resources.Select(path => path.Value), "content resource");
        Unique(product.Content.Textures.Select(path => path.Value), "content texture");
        RequireCount(product.Content.Scenes.Length, OptionsProduct.RequiredSceneCount, "content scenes");
        RequireCount(product.Content.Resources.Length, OptionsProduct.RequiredResourceCount, "content resources");
        RequireCount(product.Content.Textures.Length, OptionsProduct.RequiredTextureCount, "content textures");
        RequireSorted(product.Content.Scenes.Select(path => path.Value), "content scenes");
        RequireSorted(product.Content.Resources.Select(path => path.Value), "content resources");
        RequireSorted(product.Content.Textures.Select(path => path.Value), "content textures");
        if (!product.Content.Scenes.Contains(product.Scene))
        {
            throw new InvalidDataException("Content inventory does not contain the options scene");
        }

        bool compiled = product.Scene.Value == OptionsProductLocation.CompiledRootScene;
        if (!compiled && product.Scene.Value != OptionsProductLocation.PlainRootScene)
        {
            throw new InvalidDataException("Options root scene does not match the typed product location");
        }

        string sceneSuffix = compiled ? ".scn" : ".tscn";
        string resourceSuffix = compiled ? ".res" : ".tres";
        if (product.Content.Scenes.Any(path => !path.Value.EndsWith(sceneSuffix, StringComparison.Ordinal))
            || product.Content.Resources.Any(path =>
                !path.Value.EndsWith(resourceSuffix, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Options closure mixes plain and compiled resource suffixes");
        }
    }

    private static void ValidateOption(OptionDefinition option)
    {
        bool fixedVector = option.DataKind is OptionDataKind.Discrete or OptionDataKind.DiscreteFloat
            && option.Values.Length > 0;
        if (fixedVector)
        {
            int expected = Math.Min(option.AuthoredDefaultIndex, option.Values.Length - 1);
            if (option.EffectiveDefaultIndex != expected)
            {
                throw new InvalidDataException(
                    $"Option '{option.Id}' effective default must clamp to its fixed value vector");
            }
        }

        if (option.LivePreview && option.Handler != OptionHandler.Audio)
        {
            throw new InvalidDataException($"Option '{option.Id}' enables live preview without the audio handler");
        }

        bool validView = option.DataKind switch
        {
            OptionDataKind.Boolean => option.ViewKind == OptionViewKind.Checkbox,
            OptionDataKind.Discrete => option.ViewKind is OptionViewKind.List or OptionViewKind.Slider,
            OptionDataKind.DiscreteFloat => option.ViewKind is OptionViewKind.List or OptionViewKind.Slider,
            OptionDataKind.Action => option.ViewKind == OptionViewKind.Button,
            _ => false,
        };
        if (!validView)
        {
            throw new InvalidDataException($"Option '{option.Id}' has an incompatible data/view kind pair");
        }

        if (option.DataKind == OptionDataKind.Boolean
            && option.Values.Any(value => value.Kind != OptionScalarKind.Boolean)
            || option.DataKind == OptionDataKind.DiscreteFloat
            && option.Values.Any(value => value.Kind != OptionScalarKind.Number))
        {
            throw new InvalidDataException($"Option '{option.Id}' has values incompatible with its data kind");
        }

        if (option.DataKind == OptionDataKind.Action && option.Values.Length != 0)
        {
            throw new InvalidDataException($"Action option '{option.Id}' cannot declare fixed values");
        }

        if (option.DataKind == OptionDataKind.Boolean
            && (option.AuthoredDefaultIndex is < 0 or > 1
                || option.EffectiveDefaultIndex is < 0 or > 1
                || option.AuthoredDefaultIndex != option.EffectiveDefaultIndex))
        {
            throw new InvalidDataException($"Boolean option '{option.Id}' default must be exactly 0 or 1");
        }

        if (option.Handler == OptionHandler.Autodetect && option.DataKind != OptionDataKind.Action)
        {
            throw new InvalidDataException($"Autodetect option '{option.Id}' must be an action");
        }

        // declared_value_count records authored metadata. Retail vectors contain known mismatches,
        // so the effective default is checked against the materialized vector instead.
    }

    private static void RequireCount(int actual, int expected, string context)
    {
        if (actual != expected)
        {
            throw new InvalidDataException($"Expected {expected} {context}, found {actual}");
        }
    }

    private static void RequireSequence(
        IEnumerable<int> actual,
        IReadOnlyList<int> expected,
        string context)
    {
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException($"{context} does not match the authored contract");
        }
    }

    private static void RequireIdsInOrder(
        IEnumerable<string> actual,
        IReadOnlyList<string> expected,
        string context)
    {
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{context} order does not match the authored contract");
        }
    }

    private static void RequireExactIds(
        IEnumerable<string> actual,
        IReadOnlyCollection<string> expected,
        string context)
    {
        string[] values = actual.ToArray();
        if (values.Length != expected.Count
            || !values.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
        {
            throw new InvalidDataException($"{context} set does not match the authored contract");
        }
    }

    private static void RequireExactOptionSet(
        IEnumerable<OptionDefinition> actual,
        IReadOnlyCollection<string> expected,
        string context) => RequireExactIds(actual.Select(option => option.Id), expected, context);

    private static void RequireSingleHandler(
        OptionsProduct product,
        OptionHandler handler,
        string expectedId)
    {
        OptionDefinition option = product.Options.Single(candidate => candidate.Handler == handler);
        if (option.Id != expectedId)
        {
            throw new InvalidDataException(
                $"{handler} handler must belong to authored option '{expectedId}'");
        }
    }

    private static void RequireOptionShape(
        OptionsProduct product,
        string id,
        OptionStorage storage,
        OptionDataKind dataKind,
        OptionViewKind viewKind,
        OptionHandler handler,
        bool livePreview)
    {
        OptionDefinition option = product.Options.Single(candidate => candidate.Id == id);
        if (option.Storage != storage
            || option.DataKind != dataKind
            || option.ViewKind != viewKind
            || option.Handler != handler
            || option.LivePreview != livePreview)
        {
            throw new InvalidDataException($"Option '{id}' does not match its authored semantic contract");
        }
    }

    private static void RequireEnumCensus<TEnum>(
        IEnumerable<TEnum> values,
        params (TEnum Value, int Count)[] expected)
        where TEnum : struct, System.Enum
    {
        var census = values.GroupBy(value => value).ToDictionary(group => group.Key, group => group.Count());
        if (census.Count != expected.Length
            || expected.Any(item => !census.TryGetValue(item.Value, out int count) || count != item.Count))
        {
            throw new InvalidDataException($"{typeof(TEnum).Name} census does not match the authored contract");
        }
    }

    private static void Unique(IEnumerable<string> values, string context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (!seen.Add(value))
            {
                throw new InvalidDataException($"Duplicate {context} '{value}'");
            }
        }
    }

    private static void RequireSorted(IEnumerable<string> values, string context)
    {
        string[] actual = values.ToArray();
        string[] sorted = actual.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(sorted, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{context} must use deterministic ordinal order");
        }
    }

    private static JsonElement Required(
        JsonElement parent,
        string property,
        JsonValueKind kind,
        string context)
    {
        if (!parent.TryGetProperty(property, out JsonElement value))
        {
            throw new InvalidDataException($"{context}.{property} is required");
        }

        if (value.ValueKind != kind)
        {
            throw new InvalidDataException($"{context}.{property} must be {kind}");
        }

        return value;
    }

    private static void Object(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{context} must be Object");
        }
    }

    private static void Only(JsonElement element, string context, params string[] fields)
    {
        var allowed = fields.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException($"{context} repeats field '{property.Name}'");
            }

            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException($"{context} contains unsupported field '{property.Name}'");
            }
        }
    }

    private static ImmutableArray<T> Array<T>(
        JsonElement parent,
        string property,
        Func<JsonElement, T> reader,
        string context) => Required(parent, property, JsonValueKind.Array, context)
        .EnumerateArray()
        .Select(reader)
        .ToImmutableArray();

    private static string String(JsonElement parent, string property, string context) =>
        String(Required(parent, property, JsonValueKind.String, context), $"{context}.{property}");

    private static string String(JsonElement element, string context)
    {
        string value = element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : throw new InvalidDataException($"{context} must be String");
        if (value.Length == 0)
        {
            throw new InvalidDataException($"{context} must not be empty");
        }

        return value;
    }

    private static string StringAllowEmpty(JsonElement parent, string property, string context) =>
        StringAllowEmpty(Required(parent, property, JsonValueKind.String, context), $"{context}.{property}");

    private static string StringAllowEmpty(JsonElement element, string context) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : throw new InvalidDataException($"{context} must be String");

    private static string CleanText(JsonElement parent, string property, string context) =>
        CleanText(String(parent, property, context), $"{context}.{property}");

    private static string CleanTextAllowEmpty(JsonElement parent, string property, string context) =>
        CleanText(StringAllowEmpty(parent, property, context), $"{context}.{property}");

    private static string CleanTextAllowEmpty(JsonElement element, string context) =>
        CleanText(StringAllowEmpty(element, context), context);

    private static string CleanText(string value, string context)
    {
        if (value.Contains(".xdb", StringComparison.OrdinalIgnoreCase)
            || value.Contains("#xpointer", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith('/')
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':'
                && value[2] is ('/' or '\\'))
        {
            throw new InvalidDataException($"{context} contains an original-source reference");
        }

        return value;
    }

    private static string Id(JsonElement parent, string property, string context) =>
        Id(String(parent, property, context), $"{context}.{property}");

    private static string Id(JsonElement element, string context) => Id(String(element, context), context);

    private static string Id(string value, string context)
    {
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('_' or '-' or '.')))
        {
            throw new InvalidDataException($"{context} '{value}' is not a safe identifier");
        }

        return value;
    }

    private static int Integer(JsonElement parent, string property, string context)
    {
        JsonElement element = Required(parent, property, JsonValueKind.Number, context);
        if (!element.TryGetInt32(out int value))
        {
            throw new InvalidDataException($"{context}.{property} must be an Int32");
        }

        return value;
    }

    private static int NonNegativeInteger(JsonElement parent, string property, string context)
    {
        int value = Integer(parent, property, context);
        if (value < 0)
        {
            throw new InvalidDataException($"{context}.{property} must not be negative");
        }

        return value;
    }

    private static int? OptionalNonNegativeInteger(JsonElement parent, string property, string context)
    {
        if (!parent.TryGetProperty(property, out JsonElement element))
        {
            throw new InvalidDataException($"{context}.{property} is required");
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out int value)
            || value < 0)
        {
            throw new InvalidDataException($"{context}.{property} must be null or a non-negative Int32");
        }

        return value;
    }

    private static bool Boolean(JsonElement parent, string property, string context)
    {
        if (!parent.TryGetProperty(property, out JsonElement element)
            || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"{context}.{property} must be Boolean");
        }

        return element.GetBoolean();
    }

    private static double Number(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out double value)
            || !double.IsFinite(value))
        {
            throw new InvalidDataException($"{context} must be a finite number");
        }

        return value;
    }

    private static OptionScalar Scalar(JsonElement element, string context) => element.ValueKind switch
    {
        JsonValueKind.True => OptionScalar.FromBoolean(true),
        JsonValueKind.False => OptionScalar.FromBoolean(false),
        JsonValueKind.Number => OptionScalar.FromNumber(Number(element, context)),
        JsonValueKind.String => OptionScalar.FromText(String(element, context)),
        _ => throw new InvalidDataException($"{context} values must be Boolean, Number, or String"),
    };

    private static TEnum Enum<TEnum>(JsonElement parent, string property, string context)
        where TEnum : struct, System.Enum
    {
        string value = String(parent, property, context);
        foreach (TEnum candidate in System.Enum.GetValues<TEnum>())
        {
            if (SnakeCase(candidate.ToString()) == value)
            {
                return candidate;
            }
        }

        throw new InvalidDataException($"{context}.{property} has unsupported value '{value}'");
    }

    private static string SnakeCase(string value)
    {
        var chars = new List<char>(value.Length + 4);
        for (int index = 0; index < value.Length; index++)
        {
            if (index > 0 && char.IsUpper(value[index]))
            {
                chars.Add('_');
            }

            chars.Add(char.ToLowerInvariant(value[index]));
        }

        return new string([.. chars]);
    }

    private static NativeContentPath Path(
        JsonElement parent,
        string property,
        IReadOnlyList<string> extensions,
        string context) => Path(
            Required(parent, property, JsonValueKind.String, context),
            extensions,
            $"{context}.{property}");

    private static NativeContentPath Path(
        JsonElement element,
        IReadOnlyList<string> extensions,
        string context)
    {
        NativeContentPath path = ConfinedPath(element, context);
        if (!extensions.Any(extension => path.Value.EndsWith(extension, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"{context} must end with one of: {string.Join(", ", extensions)}");
        }

        return path;
    }

    private static NativeContentPath ConfinedPath(JsonElement element, string context)
    {
        string value = String(element, context);
        if (value.StartsWith('/')
            || value.Contains('\\')
            || value.Contains(':')
            || value.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"{context} must be a confined product-relative path");
        }

        return new NativeContentPath(value);
    }
}
