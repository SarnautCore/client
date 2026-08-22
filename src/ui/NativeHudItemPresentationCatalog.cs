using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using SarnautCore.NativeHud;

namespace SarnautCore;

public readonly record struct HudItemPresentation(
    HudId ItemId,
    HudId NameTextId,
    HudId DescriptionTextId,
    HudId IconTextureId,
    Texture2D Icon,
    string Quality,
    int QualityOrdinal,
    string Category,
    string EquipmentSlot,
    string RetailDressSlot,
    int RetailDressOrdinal,
    int StackLimit,
    HudId ActionId);

public readonly record struct HudItemSlotPresentation(
    HudId UnknownNameTextId,
    HudId UnknownDescriptionTextId,
    HudId UnknownIconTextureId,
    Texture2D UnknownIcon,
    HudId PreparedTextureId,
    Texture2D Prepared,
    HudId CooldownTextureId,
    Texture2D Cooldown,
    HudId CooldownTextRole,
    Color CooldownTextColor);

/// <summary>Exact-ordinal lookup into the compiled, product-owned item presentation catalog.</summary>
public interface IHudItemPresentationCatalog
{
    bool TryGet(HudId itemId, out HudItemPresentation presentation);
    bool TryResolveText(HudId textId, out string text);
    HudItemSlotPresentation SlotPresentation { get; }
}

internal sealed class NativeHudItemPresentationCatalog : IHudItemPresentationCatalog, IDisposable
{
    public const string ProductKey = "hud.items.inst-league1";
    public const string RelativePath = "items/item_presentation_catalog.res";
    private const string SchemaId = "sarnaut.item-presentation-catalog";
    private const int SchemaVersion = 1;

    private static readonly string[] ItemFields =
    [
        "name_text_id", "description_text_id", "icon_texture_id", "icon", "quality",
        "quality_ordinal", "category", "equipment_slot", "retail_dress_slot",
        "retail_dress_ordinal", "stack_limit", "action_id",
    ];

    private static readonly string[] SlotFields =
    [
        "unknown_icon_texture_id", "unknown_icon", "unknown_name_text_id",
        "unknown_description_text_id", "prepared_texture_id", "prepared",
        "prepared_state_available", "cooldown_texture_id", "cooldown",
        "cooldown_state_available", "cooldown_text_role", "cooldown_text_argb",
    ];

    private readonly Resource _resource;
    private readonly Dictionary<HudId, HudItemPresentation> _items;
    private readonly Dictionary<HudId, string> _strings;
    private bool _disposed;

    private NativeHudItemPresentationCatalog(
        Resource resource,
        Dictionary<HudId, HudItemPresentation> items,
        Dictionary<HudId, string> strings,
        HudItemSlotPresentation slotPresentation)
    {
        _resource = resource;
        _items = items;
        _strings = strings;
        SlotPresentation = slotPresentation;
    }

    public HudItemSlotPresentation SlotPresentation { get; }

    public static NativeHudItemPresentationCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Resource resource = ResourceLoader.Load<Resource>(path)
            ?? throw new FileNotFoundException($"Native HUD item presentation catalog is missing: {path}");
        try
        {
            RequireString(resource, "schema_id", path, SchemaId);
            RequireInteger(resource, "schema_version", path, SchemaVersion);
            RequireString(resource, "catalog_id", path, ProductKey);
            RequirePackId(resource, path);
            Dictionary<HudId, string> strings = LoadFallbackStrings(resource, path);

            Godot.Collections.Dictionary entries = RequireDictionary(resource, "items", path);
            var items = new Dictionary<HudId, HudItemPresentation>(entries.Count);
            foreach (Variant rawKey in entries.Keys)
            {
                string itemId = RequireString(rawKey, "item catalog key", path);
                Variant rawEntry = entries[rawKey];
                if (rawEntry.VariantType != Variant.Type.Dictionary)
                {
                    throw new InvalidDataException($"Native HUD item '{itemId}' is not a dictionary: {path}");
                }

                Godot.Collections.Dictionary entry = rawEntry.AsGodotDictionary();
                RequireExactFields(entry, ItemFields, $"item '{itemId}'", path);
                var key = new HudId(itemId);
                var presentation = new HudItemPresentation(
                    key,
                    RequireId(entry, "name_text_id", itemId, path),
                    RequireId(entry, "description_text_id", itemId, path),
                    RequireId(entry, "icon_texture_id", itemId, path),
                    RequireObject<Texture2D>(entry, "icon", itemId, path),
                    RequireString(entry["quality"], "quality", path),
                    RequireInteger(entry, "quality_ordinal", itemId, path),
                    RequireString(entry["category"], "category", path),
                    RequireString(entry["equipment_slot"], "equipment_slot", path),
                    RequireString(entry["retail_dress_slot"], "retail_dress_slot", path),
                    RequireInteger(entry, "retail_dress_ordinal", itemId, path),
                    RequirePositiveInteger(entry, "stack_limit", itemId, path),
                    OptionalId(entry["action_id"], "action_id", itemId, path));
                if (!items.TryAdd(key, presentation))
                {
                    throw new InvalidDataException($"Native HUD item id '{itemId}' is duplicated: {path}");
                }
            }

            if (items.Count == 0)
            {
                throw new InvalidDataException($"Native HUD item presentation catalog is empty: {path}");
            }

            Godot.Collections.Dictionary slot = RequireDictionary(resource, "slot_presentation", path);
            RequireExactFields(slot, SlotFields, "slot_presentation", path);
            if (!RequireBoolean(slot, "prepared_state_available", path)
                || !RequireBoolean(slot, "cooldown_state_available", path))
            {
                throw new InvalidDataException($"Native HUD item overlays are not available: {path}");
            }

            uint argb = RequireArgb(slot, "cooldown_text_argb", path);
            var slotPresentation = new HudItemSlotPresentation(
                RequireId(slot, "unknown_name_text_id", "slot_presentation", path),
                RequireId(slot, "unknown_description_text_id", "slot_presentation", path),
                RequireId(slot, "unknown_icon_texture_id", "slot_presentation", path),
                RequireObject<Texture2D>(slot, "unknown_icon", "slot_presentation", path),
                RequireId(slot, "prepared_texture_id", "slot_presentation", path),
                RequireObject<Texture2D>(slot, "prepared", "slot_presentation", path),
                RequireId(slot, "cooldown_texture_id", "slot_presentation", path),
                RequireObject<Texture2D>(slot, "cooldown", "slot_presentation", path),
                RequireId(slot, "cooldown_text_role", "slot_presentation", path),
                Color.Color8(
                    (byte)(argb >> 16),
                    (byte)(argb >> 8),
                    (byte)argb,
                    (byte)(argb >> 24)));
            return new NativeHudItemPresentationCatalog(resource, items, strings, slotPresentation);
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }

    public bool TryGet(HudId itemId, out HudItemPresentation presentation)
    {
        if (itemId.IsEmpty)
        {
            presentation = default;
            return false;
        }

        return _items.TryGetValue(itemId, out presentation);
    }

    public bool TryResolveText(HudId textId, out string text)
    {
        if (!textId.IsEmpty && _strings.TryGetValue(textId, out string? resolved))
        {
            text = resolved;
            return true;
        }

        text = string.Empty;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _resource.Dispose();
    }

    private static Godot.Collections.Dictionary RequireDictionary(Resource resource, string key, string path)
    {
        if (!resource.HasMeta(key))
        {
            throw new InvalidDataException($"Native HUD item catalog has no '{key}' metadata: {path}");
        }

        Variant value = resource.GetMeta(key);
        if (value.VariantType != Variant.Type.Dictionary)
        {
            throw new InvalidDataException($"Native HUD item catalog '{key}' is not a dictionary: {path}");
        }

        return value.AsGodotDictionary();
    }

    private static Dictionary<HudId, string> LoadFallbackStrings(Resource catalog, string path)
    {
        if (!catalog.HasMeta("strings"))
        {
            throw new InvalidDataException($"Native HUD item catalog has no compiled strings resource: {path}");
        }

        Variant rawStrings = catalog.GetMeta("strings");
        Resource? strings = rawStrings.VariantType == Variant.Type.Object
            ? rawStrings.AsGodotObject() as Resource
            : null;
        if (strings is null || !strings.HasMeta("fallback_locale") || !strings.HasMeta("locales"))
        {
            throw new InvalidDataException($"Native HUD item strings resource is incompatible: {path}");
        }

        string locale = RequireString(strings.GetMeta("fallback_locale"), "fallback locale", path);
        Godot.Collections.Dictionary locales = RequireDictionary(strings, "locales", path);
        Variant localeKey = Variant.From(locale);
        if (!locales.ContainsKey(localeKey) || locales[localeKey].VariantType != Variant.Type.Dictionary)
        {
            throw new InvalidDataException($"Native HUD item fallback locale '{locale}' is missing: {path}");
        }

        Godot.Collections.Dictionary entries = locales[localeKey].AsGodotDictionary();
        var result = new Dictionary<HudId, string>(entries.Count);
        foreach (Variant rawKey in entries.Keys)
        {
            string id = RequireString(rawKey, "item text id", path);
            string text = RequireText(entries[rawKey], $"item text '{id}'", path);
            if (!result.TryAdd(new HudId(id), text))
            {
                throw new InvalidDataException($"Native HUD item text id '{id}' is duplicated: {path}");
            }
        }

        return result;
    }

    private static string RequireText(Variant value, string label, string path)
    {
        if (value.VariantType != Variant.Type.String)
        {
            throw new InvalidDataException($"Native HUD {label} is not a string: {path}");
        }

        return value.AsString();
    }

    private static void RequireExactFields(
        Godot.Collections.Dictionary dictionary,
        IReadOnlyCollection<string> expected,
        string label,
        string path)
    {
        if (dictionary.Keys.Any(key => key.VariantType != Variant.Type.String))
        {
            throw new InvalidDataException($"Native HUD {label} has a non-string field: {path}");
        }

        var actual = dictionary.Keys.Select(key => key.AsString()).ToHashSet(StringComparer.Ordinal);
        if (actual.Count != expected.Count || !actual.SetEquals(expected))
        {
            throw new InvalidDataException($"Native HUD {label} has incompatible fields: {path}");
        }
    }

    private static void RequireString(Resource resource, string key, string path, string expected)
    {
        if (!resource.HasMeta(key) || resource.GetMeta(key).VariantType != Variant.Type.String
            || !StringComparer.Ordinal.Equals(resource.GetMeta(key).AsString(), expected))
        {
            throw new InvalidDataException($"Native HUD item catalog '{key}' is incompatible: {path}");
        }
    }

    private static void RequireInteger(Resource resource, string key, string path, int expected)
    {
        if (!resource.HasMeta(key) || resource.GetMeta(key).VariantType != Variant.Type.Int
            || resource.GetMeta(key).AsInt64() != expected)
        {
            throw new InvalidDataException($"Native HUD item catalog '{key}' is incompatible: {path}");
        }
    }

    private static void RequirePackId(Resource resource, string path)
    {
        if (!resource.HasMeta("content_pack_id")
            || resource.GetMeta("content_pack_id").VariantType != Variant.Type.String)
        {
            throw new InvalidDataException(
                $"Native HUD item catalog 'content_pack_id' is incompatible: {path}");
        }

        string packId = resource.GetMeta("content_pack_id").AsString();
        if (packId.Length != 64 || packId.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"Native HUD item catalog 'content_pack_id' is incompatible: {path}");
        }
    }

    private static string RequireString(Variant value, string label, string path)
    {
        if (value.VariantType != Variant.Type.String || string.IsNullOrWhiteSpace(value.AsString()))
        {
            throw new InvalidDataException($"Native HUD {label} is not a non-empty string: {path}");
        }

        return value.AsString();
    }

    private static HudId RequireId(
        Godot.Collections.Dictionary dictionary,
        string field,
        string item,
        string path) => new(RequireString(dictionary[field], $"item '{item}' {field}", path));

    private static HudId OptionalId(Variant value, string field, string item, string path)
    {
        if (value.VariantType == Variant.Type.Nil)
        {
            return HudId.Empty;
        }

        return new HudId(RequireString(value, $"item '{item}' {field}", path));
    }

    private static T RequireObject<T>(
        Godot.Collections.Dictionary dictionary,
        string field,
        string item,
        string path) where T : GodotObject
    {
        Variant value = dictionary[field];
        T? result = value.VariantType == Variant.Type.Object ? value.AsGodotObject() as T : null;
        return result ?? throw new InvalidDataException(
            $"Native HUD item '{item}' {field} is not a {typeof(T).Name}: {path}");
    }

    private static int RequireInteger(
        Godot.Collections.Dictionary dictionary,
        string field,
        string item,
        string path)
    {
        Variant value = dictionary[field];
        if (value.VariantType != Variant.Type.Int)
        {
            throw new InvalidDataException($"Native HUD item '{item}' {field} is not an integer: {path}");
        }

        return checked((int)value.AsInt64());
    }

    private static int RequirePositiveInteger(
        Godot.Collections.Dictionary dictionary,
        string field,
        string item,
        string path)
    {
        int value = RequireInteger(dictionary, field, item, path);
        if (value <= 0)
        {
            throw new InvalidDataException($"Native HUD item '{item}' {field} must be positive: {path}");
        }

        return value;
    }

    private static uint RequireArgb(
        Godot.Collections.Dictionary dictionary,
        string field,
        string path)
    {
        string value = RequireString(dictionary[field], field, path);
        if (value.Length != 8
            || !uint.TryParse(
                value,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out uint argb))
        {
            throw new InvalidDataException(
                $"Native HUD item slot {field} is not an eight-digit ARGB value: {path}");
        }

        return argb;
    }

    private static bool RequireBoolean(Godot.Collections.Dictionary dictionary, string field, string path)
    {
        Variant value = dictionary[field];
        if (value.VariantType != Variant.Type.Bool)
        {
            throw new InvalidDataException($"Native HUD item slot {field} is not boolean: {path}");
        }

        return value.AsBool();
    }
}
