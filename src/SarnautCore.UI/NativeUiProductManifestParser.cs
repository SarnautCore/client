using System.Text.Json;

namespace SarnautCore.UI;

public static class NativeUiProductManifestParser
{
    public const string SchemaId = "sarnaut.ui-product/v1";

    public static UiProductManifest Parse(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 48,
        });

        JsonElement root = document.RootElement;
        UiManifestJson.Object(root, "manifest");
        UiManifestJson.Only(root, "manifest", "schema_id", "catalogs", "screens");
        string schemaId = UiManifestJson.String(root, "schema_id", "manifest");
        if (schemaId != SchemaId)
        {
            throw new InvalidDataException($"Unsupported UI product schema '{schemaId}'");
        }

        JsonElement catalogs = UiManifestJson.Required(
            root,
            "catalogs",
            JsonValueKind.Object,
            "manifest");
        UiManifestJson.Only(catalogs, "catalogs", "cursors", "sounds");
        NativeContentPath cursorCatalog = UiManifestJson.Path(
            catalogs,
            "cursors",
            ".tres",
            "catalogs");
        NativeContentPath soundCatalog = UiManifestJson.Path(
            catalogs,
            "sounds",
            ".tres",
            "catalogs");

        UiScreenDefinition[] screens = UiManifestJson.Array(
            root,
            "screens",
            ReadScreen,
            "manifest");
        if (screens.Length == 0)
        {
            throw new InvalidDataException("screens must contain at least one screen");
        }

        UiManifestJson.Unique(screens.Select(screen => screen.Id), "screen id");
        return new UiProductManifest(cursorCatalog, soundCatalog, screens);
    }

    private static UiScreenDefinition ReadScreen(JsonElement element)
    {
        UiManifestJson.Object(element, "screen");
        UiManifestJson.Only(
            element,
            "screen",
            "id",
            "scene",
            "initially_visible",
            "cues",
            "roles",
            "actions",
            "values",
            "collections",
            "buttons",
            "focus_order");

        string id = UiManifestJson.Key(element, "id", "screen");
        string context = $"screen '{id}'";
        UiRoleDefinition[] roles = UiManifestJson.Array(
            element,
            "roles",
            item => ReadRole(item, context),
            context);
        UiManifestJson.Unique(roles.Select(role => role.Id), $"{context} role id");
        UiManifestJson.Unique(roles.Select(role => role.Node), $"{context} role node");
        var roleIds = roles.Select(role => role.Id).ToHashSet(StringComparer.Ordinal);

        UiActionDefinition[] actions = UiManifestJson.Array(
            element,
            "actions",
            item => ReadAction(item, context),
            context);
        UiManifestJson.Unique(actions.Select(action => action.Id), $"{context} action id");
        UiManifestJson.Unique(
            actions.SelectMany(action => action.Triggers)
                .Select(trigger => $"{trigger.Role}.{trigger.Event}"),
            $"{context} action trigger");
        foreach (UiActionTrigger trigger in actions.SelectMany(action => action.Triggers))
        {
            RequireRole(roleIds, trigger.Role, $"{context} action trigger");
        }

        UiValueBinding[] values = UiManifestJson.Array(
            element,
            "values",
            item => ReadValue(item, context),
            context);
        UiManifestJson.Unique(values.Select(value => value.Id), $"{context} value id");
        UiManifestJson.Unique(values.Select(value => value.Role), $"{context} value owner");
        foreach (UiValueBinding value in values)
        {
            RequireRole(roleIds, value.Role, $"{context} value");
            if (value.Secret && value.Kind != UiValueKind.Text)
            {
                throw new InvalidDataException($"{context} secret value '{value.Id}' must be text");
            }
        }

        UiCollectionBinding[] collections = UiManifestJson.Array(
            element,
            "collections",
            item => ReadCollection(item, context),
            context);
        UiManifestJson.Unique(
            collections.Select(collection => collection.Id),
            $"{context} collection id");
        UiManifestJson.Unique(
            collections.Select(collection => collection.Role),
            $"{context} collection owner");
        foreach (UiCollectionBinding collection in collections)
        {
            RequireRole(roleIds, collection.Role, $"{context} collection");
        }

        UiButtonDefinition[] buttons = UiManifestJson.Array(
            element,
            "buttons",
            item => ReadButton(item, context),
            context);
        UiManifestJson.Unique(buttons.Select(button => button.Role), $"{context} button role");
        foreach (UiButtonDefinition button in buttons)
        {
            RequireRole(roleIds, button.Role, $"{context} button");
        }

        var buttonsByRole = buttons.ToDictionary(button => button.Role, StringComparer.Ordinal);
        foreach (UiActionTrigger trigger in actions.SelectMany(action => action.Triggers))
        {
            if (trigger.Event == UiActionEvent.Pressed
                && (!buttonsByRole.TryGetValue(trigger.Role, out UiButtonDefinition? pressedButton)
                    || pressedButton.Toggle))
            {
                throw new InvalidDataException(
                    $"{context} pressed trigger role '{trigger.Role}' is not a momentary button");
            }

            if (trigger.Event == UiActionEvent.Toggled
                && (!buttonsByRole.TryGetValue(trigger.Role, out UiButtonDefinition? toggledButton)
                    || !toggledButton.Toggle))
            {
                throw new InvalidDataException(
                    $"{context} toggled trigger role '{trigger.Role}' is not a toggle button");
            }
        }

        string[] focusOrder = UiManifestJson.Array(
            element,
            "focus_order",
            item => UiManifestJson.Key(item, $"{context}.focus_order"),
            context);
        UiManifestJson.Unique(focusOrder, $"{context} focus role");
        foreach (string role in focusOrder)
        {
            RequireRole(roleIds, role, $"{context} focus order");
        }

        return new UiScreenDefinition(
            id,
            UiManifestJson.Path(element, "scene", ".tscn", context),
            UiManifestJson.Bool(element, "initially_visible", context),
            ReadCues(element, context),
            roles,
            actions,
            values,
            collections,
            buttons,
            focusOrder);
    }

    private static UiRoleDefinition ReadRole(JsonElement element, string screenContext)
    {
        string context = $"{screenContext} role";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "id", "node", "initially_visible", "cursor", "cues");
        return new UiRoleDefinition(
            UiManifestJson.Key(element, "id", context),
            UiManifestJson.Node(element, "node", context),
            UiManifestJson.Bool(element, "initially_visible", context),
            UiManifestJson.OptionalCatalogKey(element, "cursor", context),
            ReadCues(element, context));
    }

    private static UiActionDefinition ReadAction(JsonElement element, string screenContext)
    {
        string context = $"{screenContext} action";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "id", "triggers");
        string id = UiManifestJson.Key(element, "id", context);
        UiActionTrigger[] triggers = UiManifestJson.Array(
            element,
            "triggers",
            item => ReadTrigger(item, id),
            $"action '{id}'");
        if (triggers.Length == 0)
        {
            throw new InvalidDataException($"action '{id}' must contain at least one trigger");
        }

        UiManifestJson.Unique(
            triggers.Select(trigger => $"{trigger.Role}.{trigger.Event}"),
            $"action '{id}' trigger");
        return new UiActionDefinition(id, triggers);
    }

    private static UiActionTrigger ReadTrigger(JsonElement element, string actionId)
    {
        string context = $"action '{actionId}' trigger";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "role", "event");
        return new UiActionTrigger(
            UiManifestJson.Key(element, "role", context),
            UiManifestJson.Enum<UiActionEvent>(element, "event", context));
    }

    private static UiValueBinding ReadValue(JsonElement element, string screenContext)
    {
        string context = $"{screenContext} value";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "id", "role", "kind", "access", "secret");
        return new UiValueBinding(
            UiManifestJson.Key(element, "id", context),
            UiManifestJson.Key(element, "role", context),
            UiManifestJson.Enum<UiValueKind>(element, "kind", context),
            UiManifestJson.Enum<UiValueAccess>(element, "access", context),
            UiManifestJson.Bool(element, "secret", context));
    }

    private static UiCollectionBinding ReadCollection(JsonElement element, string screenContext)
    {
        string context = $"{screenContext} collection";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "id", "role", "item_scene", "selection");
        return new UiCollectionBinding(
            UiManifestJson.Key(element, "id", context),
            UiManifestJson.Key(element, "role", context),
            UiManifestJson.Path(element, "item_scene", ".tscn", context),
            UiManifestJson.Enum<UiCollectionSelection>(element, "selection", context));
    }

    private static UiButtonDefinition ReadButton(JsonElement element, string screenContext)
    {
        string context = $"{screenContext} button";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "role", "toggle", "initial_variant", "variants");
        string role = UiManifestJson.Key(element, "role", context);
        string initialVariant = UiManifestJson.Key(element, "initial_variant", context);
        UiButtonVariant[] variants = UiManifestJson.Array(
            element,
            "variants",
            item => ReadButtonVariant(item, role),
            $"button role '{role}'");
        if (variants.Length == 0)
        {
            throw new InvalidDataException($"button role '{role}' must contain at least one variant");
        }

        UiManifestJson.Unique(variants.Select(variant => variant.Id), $"button role '{role}' variant id");
        if (!variants.Any(variant => variant.Id == initialVariant))
        {
            throw new InvalidDataException(
                $"button role '{role}' initial variant '{initialVariant}' is not declared");
        }

        bool toggle = UiManifestJson.Bool(element, "toggle", context);
        if (!toggle && variants.Length != 1)
        {
            throw new InvalidDataException(
                $"button role '{role}' is momentary but declares multiple variants");
        }

        return new UiButtonDefinition(
            role,
            toggle,
            initialVariant,
            variants);
    }

    private static UiButtonVariant ReadButtonVariant(JsonElement element, string role)
    {
        string context = $"button role '{role}' variant";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "id", "visual_state", "cues");
        return new UiButtonVariant(
            UiManifestJson.Key(element, "id", context),
            UiManifestJson.Key(element, "visual_state", context),
            ReadCues(element, context));
    }

    private static UiCueSet ReadCues(JsonElement parent, string context)
    {
        if (!parent.TryGetProperty("cues", out JsonElement cues))
        {
            return UiCueSet.Empty;
        }

        string cuesContext = $"{context} cues";
        UiManifestJson.Object(cues, cuesContext);
        UiManifestJson.Only(cues, cuesContext, "show", "hide", "hover", "press");
        return new UiCueSet(
            UiManifestJson.OptionalCatalogKey(cues, "show", cuesContext),
            UiManifestJson.OptionalCatalogKey(cues, "hide", cuesContext),
            UiManifestJson.OptionalCatalogKey(cues, "hover", cuesContext),
            UiManifestJson.OptionalCatalogKey(cues, "press", cuesContext));
    }

    private static void RequireRole(HashSet<string> roleIds, string role, string context)
    {
        if (!roleIds.Contains(role))
        {
            throw new InvalidDataException($"{context} role '{role}' is not declared");
        }
    }
}
