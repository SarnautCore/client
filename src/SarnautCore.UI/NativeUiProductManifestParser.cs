using System.Text.Json;

namespace SarnautCore.UI;

public static class NativeUiProductManifestParser
{
    public const string SchemaId = "sarnaut.ui-product/v2";

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
        UiManifestJson.Only(catalogs, "catalogs", "cursors", "sounds", "theme");
        string cursorCatalogValue = UiManifestJson.String(catalogs, "cursors", "catalogs");
        UiProductResourceEncoding resourceEncoding = CatalogEncoding(cursorCatalogValue);
        string catalogExtension = resourceEncoding == UiProductResourceEncoding.Compiled
            ? ".res"
            : ".tres";
        NativeContentPath cursorCatalog = UiManifestJson.Path(
            catalogs,
            "cursors",
            catalogExtension,
            "catalogs");
        NativeContentPath soundCatalog = UiManifestJson.Path(
            catalogs,
            "sounds",
            catalogExtension,
            "catalogs");
        NativeContentPath theme = UiManifestJson.Path(
            catalogs,
            "theme",
            catalogExtension,
            "catalogs");

        UiScreenDefinition[] screens = UiManifestJson.Array(
            root,
            "screens",
            item => ReadScreen(item, resourceEncoding),
            "manifest");
        if (screens.Length == 0)
        {
            throw new InvalidDataException("screens must contain at least one screen");
        }

        UiManifestJson.Unique(screens.Select(screen => screen.Id), "screen id");
        return new UiProductManifest(cursorCatalog, soundCatalog, theme, resourceEncoding, screens);
    }

    private static UiScreenDefinition ReadScreen(
        JsonElement element,
        UiProductResourceEncoding resourceEncoding)
    {
        UiManifestJson.Object(element, "screen");
        UiManifestJson.Only(
            element,
            "screen",
            "id",
            "scene",
            "priority",
            "initially_visible",
            "documents",
            "timeline",
            "cues",
            "roles",
            "actions",
            "values",
            "collections",
            "buttons",
            "selection_groups",
            "focus_order");

        string id = UiManifestJson.Key(element, "id", "screen");
        string context = $"screen '{id}'";
        UiDocumentReference[] documents = ReadDocuments(element, context);
        NativeContentPath? timeline = element.TryGetProperty("timeline", out _)
            ? UiManifestJson.Path(element, "timeline", ".json", context)
            : null;
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
            item => ReadCollection(item, context, resourceEncoding),
            context);
        UiManifestJson.Unique(
            collections.Select(collection => collection.Id),
            $"{context} collection id");
        UiManifestJson.Unique(
            collections.Select(collection => collection.Role),
            $"{context} collection owner");
        UiManifestJson.Unique(
            collections.Select(collection => collection.ItemRole),
            $"{context} collection item role");
        foreach (UiCollectionBinding collection in collections)
        {
            RequireRole(roleIds, collection.Role, $"{context} collection");
            RequireRole(roleIds, collection.ItemRole, $"{context} collection item");
        }

        var collectionsById = collections.ToDictionary(
            collection => collection.Id,
            StringComparer.Ordinal);
        var actionRoutes = new List<(string Signature, bool ItemRoute)>();
        foreach (UiActionDefinition action in actions)
        {
            string[] collectionIds = action.Arguments
                .Where(argument => argument.Kind == UiActionArgumentKind.CollectionItemId)
                .Select(argument => argument.Collection!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var itemRoles = new HashSet<string>(StringComparer.Ordinal);
            foreach (string collectionId in collectionIds)
            {
                if (!collectionsById.TryGetValue(collectionId, out UiCollectionBinding? collection))
                {
                    throw new InvalidDataException(
                        $"{context} action '{action.Id}' references unknown collection '{collectionId}'");
                }

                if (collection.Selection != UiCollectionSelection.Single)
                {
                    throw new InvalidDataException(
                        $"{context} action '{action.Id}' collection '{collectionId}' is not single-selection");
                }

                itemRoles.Add(collection.ItemRole);
            }

            bool hasItemTrigger = action.Triggers.Any(trigger => itemRoles.Contains(trigger.Role));
            bool hasUnrelatedTrigger = action.Triggers.Any(trigger => !itemRoles.Contains(trigger.Role));
            if (hasItemTrigger && hasUnrelatedTrigger)
            {
                throw new InvalidDataException(
                    $"{context} action '{action.Id}' mixes collection-item and unrelated triggers");
            }

            if (hasItemTrigger)
            {
                foreach (string collectionId in collectionIds)
                {
                    UiCollectionBinding collection = collectionsById[collectionId];
                    if (!action.Triggers.Any(trigger => trigger.Role == collection.ItemRole))
                    {
                        throw new InvalidDataException(
                            $"{context} action '{action.Id}' has no trigger on collection '{collectionId}' item role '{collection.ItemRole}'");
                    }
                }
            }
            actionRoutes.Add((ActionSignature(action), hasItemTrigger));
        }

        foreach (IGrouping<string, (string Signature, bool ItemRoute)> routes
                 in actionRoutes.GroupBy(route => route.Signature, StringComparer.Ordinal))
        {
            if (routes.Count() == 1)
            {
                continue;
            }

            bool validSplit = routes.Count() == 2
                && routes.Count(route => route.ItemRoute) == 1;
            if (!validSplit)
            {
                throw new InvalidDataException(
                    $"Duplicate {context} action signature '{routes.Key}'");
            }
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
        foreach (UiCollectionBinding collection in collections.Where(
            collection => collection.Selection == UiCollectionSelection.Single))
        {
            if (!buttonsByRole.TryGetValue(collection.ItemRole, out UiButtonDefinition? itemButton)
                || !itemButton.Toggle
                || itemButton.Variants.Count != 2)
            {
                throw new InvalidDataException(
                    $"{context} single-selection collection '{collection.Id}' item role '{collection.ItemRole}' must be a two-variant toggle button");
            }
        }

        var valueOwners = values.Select(value => value.Role).ToHashSet(StringComparer.Ordinal);
        foreach (UiActionTrigger trigger in actions.SelectMany(action => action.Triggers))
        {
            ValidateTriggerReachability(context, trigger, buttonsByRole, valueOwners);
        }

        var triggers = actions.SelectMany(action => action.Triggers).ToArray();
        foreach (UiButtonDefinition button in buttons)
        {
            foreach (UiButtonVariant variant in button.Variants)
            {
                foreach (UiInputRoute route in variant.Inputs)
                {
                    if (!triggers.Any(trigger =>
                        trigger.Role == button.Role && trigger.Event == route.Event))
                    {
                        throw new InvalidDataException(
                            $"{context} button role '{button.Role}' variant '{variant.Id}' maps {route.Input} to undeclared event {route.Event}");
                    }
                }
            }

            foreach (UiActionEvent actionEvent in triggers
                .Where(trigger => trigger.Role == button.Role)
                .Select(trigger => trigger.Event))
            {
                if (!button.Variants.Any(variant =>
                    variant.Inputs.Any(route => route.Event == actionEvent)))
                {
                    throw new InvalidDataException(
                        $"{context} button role '{button.Role}' action event {actionEvent} is unreachable from every variant");
                }
            }
        }

        UiSelectionGroupDefinition[] selectionGroups = UiManifestJson.Array(
            element,
            "selection_groups",
            item => ReadSelectionGroup(item, context),
            context);
        UiManifestJson.Unique(
            selectionGroups.Select(group => group.Id),
            $"{context} selection group id");
        var groupedRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (UiSelectionGroupDefinition group in selectionGroups)
        {
            if (group.Roles.Count < 2)
            {
                throw new InvalidDataException(
                    $"{context} selection group '{group.Id}' must contain at least two roles");
            }

            foreach (string role in group.Roles)
            {
                RequireRole(roleIds, role, $"{context} selection group '{group.Id}'");
                if (!groupedRoles.Add(role))
                {
                    throw new InvalidDataException(
                        $"{context} role '{role}' belongs to more than one selection group");
                }

                if (!buttonsByRole.TryGetValue(role, out UiButtonDefinition? button)
                    || !button.Toggle
                    || button.Variants.Count != 2)
                {
                    throw new InvalidDataException(
                        $"{context} selection group '{group.Id}' role '{role}' is not a two-variant toggle button");
                }
            }

            if (group.InitialRole is { } initialRole
                && !group.Roles.Contains(initialRole, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"{context} selection group '{group.Id}' initial role '{initialRole}' is not a member");
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
            UiManifestJson.Path(
                element,
                "scene",
                resourceEncoding == UiProductResourceEncoding.Compiled ? ".scn" : ".tscn",
                context),
            UiManifestJson.Int32(element, "priority", context),
            UiManifestJson.Bool(element, "initially_visible", context),
            documents,
            timeline,
            ReadCues(element, context),
            roles,
            actions,
            values,
            collections,
            buttons,
            selectionGroups,
            focusOrder);
    }

    private static UiDocumentReference[] ReadDocuments(JsonElement element, string screenContext)
    {
        if (!element.TryGetProperty("documents", out JsonElement documentsElement))
        {
            return [];
        }

        if (documentsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{screenContext}.documents must be Array");
        }

        UiDocumentReference[] documents = documentsElement.EnumerateArray()
            .Select(item => ReadDocument(item, screenContext))
            .ToArray();
        if (documents.Length == 0)
        {
            throw new InvalidDataException($"{screenContext}.documents must not be empty");
        }

        UiManifestJson.Unique(documents.Select(document => document.Id), $"{screenContext} document id");
        UiManifestJson.Unique(
            documents.Select(document => document.Path.Value),
            $"{screenContext} document path");
        return documents;
    }

    private static UiDocumentReference ReadDocument(JsonElement element, string screenContext)
    {
        string context = $"{screenContext} document";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "id", "path");
        return new UiDocumentReference(
            UiManifestJson.Key(element, "id", context),
            UiManifestJson.Path(element, "path", ".json", context));
    }

    private static UiRoleDefinition ReadRole(JsonElement element, string screenContext)
    {
        string context = $"{screenContext} role";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "id", "node", "initially_visible", "cursor", "cues");
        string id = UiManifestJson.Key(element, "id", context);
        string node = UiManifestJson.Node(element, "node", context, allowRoot: id == "screen-input");
        if ((id == "screen-input") != (node == "."))
        {
            throw new InvalidDataException(
                $"{screenContext} role 'screen-input' must exclusively address the scene root '.'");
        }

        return new UiRoleDefinition(
            id,
            node,
            UiManifestJson.Bool(element, "initially_visible", context),
            UiManifestJson.OptionalCatalogKey(element, "cursor", context),
            ReadCues(element, context));
    }

    private static UiActionDefinition ReadAction(JsonElement element, string screenContext)
    {
        string context = $"{screenContext} action";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "id", "arguments", "triggers");
        string id = UiManifestJson.Key(element, "id", context);
        UiActionArgument[] arguments = UiManifestJson.Array(
            element,
            "arguments",
            item => ReadActionArgument(item, id),
            $"action '{id}'");
        UiManifestJson.Unique(
            arguments.Select(argument => argument.Name),
            $"action '{id}' argument name");
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
        return new UiActionDefinition(id, arguments, triggers);
    }

    private static UiActionArgument ReadActionArgument(JsonElement element, string actionId)
    {
        string context = $"action '{actionId}' argument";
        UiManifestJson.Object(element, context);
        UiActionArgumentKind kind = UiManifestJson.Enum<UiActionArgumentKind>(
            element,
            "kind",
            context);
        if (kind == UiActionArgumentKind.ProductId)
        {
            UiManifestJson.Only(element, context, "name", "kind", "value");
            return new UiActionArgument(
                UiManifestJson.Key(element, "name", context),
                kind,
                UiManifestJson.Key(element, "value", context),
                null);
        }

        UiManifestJson.Only(element, context, "name", "kind", "collection");
        return new UiActionArgument(
            UiManifestJson.Key(element, "name", context),
            kind,
            null,
            UiManifestJson.Key(element, "collection", context));
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

    private static UiCollectionBinding ReadCollection(
        JsonElement element,
        string screenContext,
        UiProductResourceEncoding resourceEncoding)
    {
        string context = $"{screenContext} collection";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(
            element,
            context,
            "id",
            "role",
            "item_role",
            "item_scene",
            "selection");
        return new UiCollectionBinding(
            UiManifestJson.Key(element, "id", context),
            UiManifestJson.Key(element, "role", context),
            UiManifestJson.Key(element, "item_role", context),
            UiManifestJson.Path(
                element,
                "item_scene",
                resourceEncoding == UiProductResourceEncoding.Compiled ? ".scn" : ".tscn",
                context),
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
        UiManifestJson.Only(element, context, "id", "visual_state", "cues", "inputs");
        UiInputRoute[] inputs = UiManifestJson.Array(
            element,
            "inputs",
            item => ReadInputRoute(item, context),
            context);
        UiManifestJson.Unique(
            inputs.Select(route => route.Input.ToString()),
            $"{context} physical input");
        return new UiButtonVariant(
            UiManifestJson.Key(element, "id", context),
            UiManifestJson.Key(element, "visual_state", context),
            ReadCues(element, context),
            inputs);
    }

    private static UiInputRoute ReadInputRoute(JsonElement element, string variantContext)
    {
        string context = $"{variantContext} input";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "input", "event");
        var route = new UiInputRoute(
            UiManifestJson.Enum<UiPhysicalInput>(element, "input", context),
            UiManifestJson.Enum<UiActionEvent>(element, "event", context));
        bool compatible = route.Input switch
        {
            UiPhysicalInput.PrimaryPressed
                or UiPhysicalInput.PrimaryReleased
                or UiPhysicalInput.SecondaryPressed
                or UiPhysicalInput.SecondaryReleased =>
                route.Event is UiActionEvent.Pressed or UiActionEvent.Toggled,
            UiPhysicalInput.DoublePressed => route.Event == UiActionEvent.DoublePressed,
            UiPhysicalInput.HoverEntered => route.Event == UiActionEvent.HoverEntered,
            UiPhysicalInput.HoverExited => route.Event == UiActionEvent.HoverExited,
            _ => false,
        };
        if (!compatible)
        {
            throw new InvalidDataException(
                $"{context} cannot map physical input {route.Input} to logical event {route.Event}");
        }

        return route;
    }

    private static UiSelectionGroupDefinition ReadSelectionGroup(
        JsonElement element,
        string screenContext)
    {
        string context = $"{screenContext} selection group";
        UiManifestJson.Object(element, context);
        UiManifestJson.Only(element, context, "id", "roles", "allow_empty", "initial_role");
        string id = UiManifestJson.Key(element, "id", context);
        string[] roles = UiManifestJson.Array(
            element,
            "roles",
            item => UiManifestJson.Key(item, $"selection group '{id}' role"),
            context);
        UiManifestJson.Unique(roles, $"selection group '{id}' role");

        JsonElement initialRoleElement = UiManifestJson.Required(
            element,
            "initial_role",
            null,
            context);
        string? initialRole = initialRoleElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => UiManifestJson.Key(
                initialRoleElement,
                $"selection group '{id}'.initial_role"),
            _ => throw new InvalidDataException(
                $"selection group '{id}'.initial_role must be String or Null"),
        };

        return new UiSelectionGroupDefinition(
            id,
            roles,
            UiManifestJson.Bool(element, "allow_empty", context),
            initialRole);
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

    private static string ActionSignature(UiActionDefinition action)
    {
        string arguments = string.Join(
            ",",
            action.Arguments
                .OrderBy(argument => argument.Name, StringComparer.Ordinal)
                .Select(argument =>
                    $"{argument.Name}:{argument.Kind}:{argument.Value ?? argument.Collection}"));
        return $"{action.Id}({arguments})";
    }

    private static void ValidateTriggerReachability(
        string context,
        UiActionTrigger trigger,
        IReadOnlyDictionary<string, UiButtonDefinition> buttonsByRole,
        IReadOnlySet<string> valueOwners)
    {
        buttonsByRole.TryGetValue(trigger.Role, out UiButtonDefinition? button);
        bool reachable = trigger.Event switch
        {
            UiActionEvent.Pressed => button is { Toggle: false },
            UiActionEvent.Toggled => button is { Toggle: true },
            UiActionEvent.DoublePressed => button is not null,
            UiActionEvent.Submitted or UiActionEvent.Cancelled =>
                trigger.Role == "screen-input" || valueOwners.Contains(trigger.Role),
            UiActionEvent.Changed => valueOwners.Contains(trigger.Role),
            UiActionEvent.NavigatePrevious or UiActionEvent.NavigateNext =>
                trigger.Role == "screen-input",
            _ => true,
        };

        if (!reachable)
        {
            throw new InvalidDataException(
                $"{context} {trigger.Event} trigger role '{trigger.Role}' cannot emit that event");
        }
    }

    private static UiProductResourceEncoding CatalogEncoding(string path) =>
        path.EndsWith(".tres", StringComparison.Ordinal)
            ? UiProductResourceEncoding.Plain
            : path.EndsWith(".res", StringComparison.Ordinal)
                ? UiProductResourceEncoding.Compiled
                : throw new InvalidDataException(
                    "catalogs.cursors must be a confined .tres or .res resource path");
}
