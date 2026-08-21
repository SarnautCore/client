namespace SarnautCore.UI.Tests;

public sealed class UiAssetCatalogTests
{
    [Fact]
    public void CursorCatalogResolvesExactProjectKeysAndHotspots()
    {
        var texture = new object();
        var catalog = new UiCursorCatalog<object>(
        [
            new UiCursorAsset<object>("default", new UiCursorHotspot(3, 5), texture),
        ]);

        UiCursorAsset<object> entry = catalog.GetRequired("default");

        Assert.Same(texture, entry.Texture);
        Assert.Equal(new UiCursorHotspot(3, 5), entry.Hotspot);
        Assert.False(catalog.TryGet("DEFAULT", out _));
    }

    [Fact]
    public void SoundCatalogResolvesExactProjectCueKeys()
    {
        var sound = new object();
        var catalog = new UiSoundCatalog<object>(
        [
            new UiSoundAsset<object>("button_press", sound),
        ]);

        Assert.Same(sound, catalog.GetRequired("button_press").Sound);
        Assert.Throws<KeyNotFoundException>(() => catalog.GetRequired("Button_Press"));
    }

    [Fact]
    public void CatalogsRejectDuplicateOrPathLikeKeys()
    {
        var value = new object();

        Assert.Throws<ArgumentException>(() => new UiSoundCatalog<object>(
        [
            new UiSoundAsset<object>("same", value),
            new UiSoundAsset<object>("same", value),
        ]));
        Assert.Throws<InvalidDataException>(() => new UiCursorCatalog<object>(
        [
            new UiCursorAsset<object>("folder/cursor", new UiCursorHotspot(0, 0), value),
        ]));
        Assert.Throws<InvalidDataException>(() => new UiSoundCatalog<object>(
        [
            new UiSoundAsset<object>("button-press", value),
        ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiCursorHotspot(-1, 0));
    }

    [Fact]
    public void CatalogsRejectMissingNativeContent()
    {
        Assert.Throws<ArgumentException>(() => new UiCursorCatalog<object>([]));
        Assert.Throws<ArgumentException>(() => new UiSoundCatalog<object>([]));
    }

    [Fact]
    public void ProductCatalogBindingClosesEveryCursorAndCueReference()
    {
        UiProductManifest manifest = UiProductFixture.Parse();
        var cursors = new UiCursorCatalog<object>(
        [
            new("default", new UiCursorHotspot(0, 0), new object()),
            new("use", new UiCursorHotspot(0, 0), new object()),
        ]);
        var sounds = new UiSoundCatalog<object>(
            ProductCueKeys(manifest).Select(key => new UiSoundAsset<object>(key, new object())));

        UiProductCatalogBinding.Validate(manifest, cursors, sounds);
    }

    [Fact]
    public void ProductCatalogBindingFailsOnAnUnresolvedReference()
    {
        UiProductManifest manifest = UiProductFixture.Parse();
        var cursors = new UiCursorCatalog<object>(
        [
            new("default", new UiCursorHotspot(0, 0), new object()),
        ]);
        var sounds = new UiSoundCatalog<object>(
        [
            new("placeholder", new object()),
        ]);

        Assert.Throws<KeyNotFoundException>(() =>
            UiProductCatalogBinding.Validate(manifest, cursors, sounds));
    }

    private static IEnumerable<string> ProductCueKeys(UiProductManifest manifest) =>
        manifest.Screens
            .SelectMany(screen =>
                new[] { screen.Cues }
                    .Concat(screen.Roles.Select(role => role.Cues))
                    .Concat(screen.Buttons.SelectMany(button => button.Variants).Select(variant => variant.Cues)))
            .SelectMany(cues => new[] { cues.Show, cues.Hide, cues.Hover, cues.Press })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);
}
