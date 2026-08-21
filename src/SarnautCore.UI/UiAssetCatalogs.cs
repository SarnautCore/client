namespace SarnautCore.UI;

public readonly record struct UiCursorHotspot
{
    public UiCursorHotspot(int x, int y)
    {
        if (x < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if (y < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        X = x;
        Y = y;
    }

    public int X { get; }
    public int Y { get; }
}

public sealed record UiCursorAsset<TTexture>(
    string Key,
    UiCursorHotspot Hotspot,
    TTexture Texture)
    where TTexture : class;

public sealed class UiCursorCatalog<TTexture> where TTexture : class
{
    private readonly IReadOnlyDictionary<string, UiCursorAsset<TTexture>> _assets;

    public UiCursorCatalog(IEnumerable<UiCursorAsset<TTexture>> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var indexed = new Dictionary<string, UiCursorAsset<TTexture>>(StringComparer.Ordinal);
        foreach (UiCursorAsset<TTexture> asset in assets)
        {
            ArgumentNullException.ThrowIfNull(asset);
            UiRuntimeKey.ValidateCatalogKey(asset.Key, "cursor key");
            ArgumentNullException.ThrowIfNull(asset.Texture);
            if (!indexed.TryAdd(asset.Key, asset))
            {
                throw new ArgumentException($"Duplicate cursor key '{asset.Key}'", nameof(assets));
            }
        }

        if (indexed.Count == 0)
        {
            throw new ArgumentException("Native cursor catalog must not be empty", nameof(assets));
        }

        _assets = indexed;
    }

    public int Count => _assets.Count;
    public IEnumerable<string> Keys => _assets.Keys;

    public bool TryGet(string key, out UiCursorAsset<TTexture>? asset) =>
        _assets.TryGetValue(key, out asset);

    public UiCursorAsset<TTexture> GetRequired(string key) =>
        _assets.TryGetValue(key, out UiCursorAsset<TTexture>? asset)
            ? asset
            : throw new KeyNotFoundException($"Cursor '{key}' is absent from native content");
}

public sealed record UiSoundAsset<TSound>(string Key, TSound Sound)
    where TSound : class;

public sealed class UiSoundCatalog<TSound> where TSound : class
{
    private readonly IReadOnlyDictionary<string, UiSoundAsset<TSound>> _assets;

    public UiSoundCatalog(IEnumerable<UiSoundAsset<TSound>> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var indexed = new Dictionary<string, UiSoundAsset<TSound>>(StringComparer.Ordinal);
        foreach (UiSoundAsset<TSound> asset in assets)
        {
            ArgumentNullException.ThrowIfNull(asset);
            UiRuntimeKey.ValidateCatalogKey(asset.Key, "sound key");
            ArgumentNullException.ThrowIfNull(asset.Sound);
            if (!indexed.TryAdd(asset.Key, asset))
            {
                throw new ArgumentException($"Duplicate sound key '{asset.Key}'", nameof(assets));
            }
        }

        if (indexed.Count == 0)
        {
            throw new ArgumentException("Native sound catalog must not be empty", nameof(assets));
        }

        _assets = indexed;
    }

    public int Count => _assets.Count;
    public IEnumerable<string> Keys => _assets.Keys;

    public bool TryGet(string key, out UiSoundAsset<TSound>? asset) =>
        _assets.TryGetValue(key, out asset);

    public UiSoundAsset<TSound> GetRequired(string key) =>
        _assets.TryGetValue(key, out UiSoundAsset<TSound>? asset)
            ? asset
            : throw new KeyNotFoundException($"Sound '{key}' is absent from native content");
}
