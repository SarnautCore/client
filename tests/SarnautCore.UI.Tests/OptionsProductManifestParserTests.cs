using System.Text;
using System.Text.Json.Nodes;

namespace SarnautCore.UI.Tests;

public sealed class OptionsProductManifestParserTests
{
    [Fact]
    public void ParsesTheExactAuthoredOptionsContract()
    {
        OptionsProduct product = OptionsProductFixture.Parse();

        Assert.Equal("screens/options.tscn", product.Scene.Value);
        Assert.Equal((750, 673, 5400),
            (product.Layout.Width, product.Layout.Height, product.Layout.Priority));
        Assert.Equal([6, 16, 7, 19], product.Pages.Select(PageOptionCount));
        Assert.Equal(8, product.Pages.Sum(page => page.Groups.Length));
        Assert.Equal(15, product.Pages.SelectMany(page => page.Groups).Sum(group => group.Blocks.Length));
        Assert.Equal(48, product.Options.Length);
        Assert.Equal(5, product.GraphicsPresets.Length);
        Assert.All(product.GraphicsPresets, preset => Assert.Equal(14, preset.Values.Count));
        Assert.Equal([13, 10, 44, 8, 8, 7],
            product.BindingSections.Select(section => section.Bindings.Length));
        Assert.Equal(90, product.BindingSections.Sum(section => section.Bindings.Length));
        Assert.Equal(94, product.BindingSections.SelectMany(section => section.Bindings)
            .Sum(binding => binding.DefaultBindings.Length));
        Assert.Equal(6, product.Options.Count(option => option.LivePreview));
        Assert.Equal(69, product.Content.Scenes.Length);
        Assert.Empty(product.Content.Resources);
        Assert.Equal(33, product.Content.Textures.Length);
    }

    [Fact]
    public void PreservesAuthoredIdsAndAcceptsKnownDeclaredVectorMismatch()
    {
        OptionsProduct product = OptionsProductFixture.Parse();

        Assert.Contains(product.Options, option => option.Id == "gfxResolution");
        Assert.Contains(product.Options, option => option.Id == "gfxSystemSpec");
        Assert.Contains(product.Options, option => option.Id == "gfxSystemSpecDefault");
        OptionDefinition mismatch = product.Options.Single(option => option.Id == "chat_bubbles_opacity");
        Assert.Equal(0, mismatch.DeclaredValueCount);
        Assert.Equal(11, mismatch.Values.Length);
        Assert.Equal(7, mismatch.EffectiveDefaultIndex);
    }

    [Theory]
    [InlineData("unexpected", 1)]
    [InlineData("schema_id", "sarnaut.options-product/v2")]
    public void RejectsUnknownRootFieldsAndSchemaDrift(string field, object value)
    {
        JsonObject root = Root();
        root[field] = JsonValue.Create(value);

        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Fact]
    public void RejectsGeometryOrderAndCardinalityDrift()
    {
        JsonObject badWidth = Root();
        badWidth["layout"]!["width"] = 751;
        Assert.Throws<InvalidDataException>(() => Parse(badWidth));

        JsonObject badOrder = Root();
        JsonArray order = badOrder["layout"]!["child_order"]!.AsArray();
        string first = order[0]!.GetValue<string>();
        string second = order[1]!.GetValue<string>();
        order[0] = second;
        order[1] = first;
        Assert.Throws<InvalidDataException>(() => Parse(badOrder));

        JsonObject badCount = Root();
        badCount["options"]!.AsArray().RemoveAt(0);
        Assert.Throws<InvalidDataException>(() => Parse(badCount));
    }

    [Fact]
    public void RejectsWrongEffectiveDefaultsButUsesMaterializedVectorForTheClamp()
    {
        JsonObject root = Root();
        JsonObject option = root["options"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(candidate => candidate["id"]!.GetValue<string>() == "gfx_gamma");
        option["authored_default_index"] = 20;
        option["effective_default_index"] = 0;

        Assert.Throws<InvalidDataException>(() => Parse(root));

        option["effective_default_index"] = 1;
        OptionsProduct parsed = Parse(root);
        Assert.Equal(
            1,
            parsed.Options.Single(candidate => candidate.Id == "gfx_gamma")
                .EffectiveDefaultIndex);
    }

    [Fact]
    public void RejectsOriginalSourceReferencesInProductText()
    {
        JsonObject root = Root();
        root["options"]!.AsArray()[0]!["description"] = "E:\\allods\\data\\Options.xdb";

        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Fact]
    public void RejectsUnsortedDuplicateAndTraversingClosurePaths()
    {
        JsonObject unsorted = Root();
        unsorted["content"]!["textures"] = new JsonArray("textures/z.png", "textures/a.png");
        Assert.Throws<InvalidDataException>(() => Parse(unsorted));

        JsonObject duplicate = Root();
        duplicate["content"]!["resources"] = new JsonArray(
            "resources/options.tres",
            "resources/options.tres");
        Assert.Throws<InvalidDataException>(() => Parse(duplicate));

        JsonObject traversal = Root();
        traversal["content"]!["scenes"] = new JsonArray("../screens/options.tscn");
        Assert.Throws<InvalidDataException>(() => Parse(traversal));
    }

    [Fact]
    public void ExposesConfinedPlainAndCompiledProductLocations()
    {
        Assert.Equal("options", OptionsProductLocation.ProductKey);
        Assert.Equal("ui/options", OptionsProductLocation.ProductDirectory);
        Assert.Equal("options-product.json", OptionsProductLocation.ManifestFile);
        Assert.Equal("screens/options.tscn", OptionsProductLocation.PlainRootScene);
        Assert.Equal("screens/options.scn", OptionsProductLocation.CompiledRootScene);
        Assert.Equal(
            "res://content/league-slice/ui/options/options-product.json",
            OptionsProductLocation.ManifestPath("res://content/league-slice"));
        Assert.Equal(
            "res://content/league-slice/ui/options/screens/options.tscn",
            OptionsProductLocation.Resolve(
                "res://content/league-slice/ui/options/options-product.json",
                OptionsProductFixture.Parse().Scene));
        Assert.Throws<ArgumentException>(() => OptionsProductLocation.ManifestPath("res://content/../private"));
    }

    [Fact]
    public void ParsesACompilerRewrittenSceneAndResourceClosure()
    {
        JsonObject root = Root();
        root["scene"] = OptionsProductLocation.CompiledRootScene;
        JsonArray scenes = root["content"]!["scenes"]!.AsArray();
        for (int index = 0; index < scenes.Count; index++)
        {
            scenes[index] = scenes[index]!.GetValue<string>()[..^5] + ".scn";
        }

        JsonArray resources = root["content"]!["resources"]!.AsArray();
        for (int index = 0; index < resources.Count; index++)
        {
            resources[index] = resources[index]!.GetValue<string>()[..^5] + ".res";
        }

        OptionsProduct compiled = Parse(root);

        Assert.Equal(OptionsProductLocation.CompiledRootScene, compiled.Scene.Value);
        Assert.All(compiled.Content.Scenes, path => Assert.EndsWith(".scn", path.Value));
        Assert.All(compiled.Content.Resources, path => Assert.EndsWith(".res", path.Value));
    }

    [Fact]
    public void ParsesTheOptInRealConverterProduct()
    {
        string? manifest = Environment.GetEnvironmentVariable("SARNAUT_OPTIONS_PRODUCT_MANIFEST");
        if (string.IsNullOrEmpty(manifest))
        {
            return;
        }

        using FileStream stream = File.OpenRead(manifest);
        OptionsProduct product = OptionsProductManifestParser.Parse(stream);

        Assert.Equal(48, product.Options.Length);
        Assert.Equal(90, product.BindingSections.Sum(section => section.Bindings.Length));
        Assert.Equal(69, product.Content.Scenes.Length);
        Assert.Equal(33, product.Content.Textures.Length);
    }

    private static int PageOptionCount(OptionsPageDefinition page) =>
        page.Groups.SelectMany(group => group.Blocks).Sum(block => block.Options.Length);

    private static JsonObject Root() => JsonNode.Parse(OptionsProductFixture.Json())!.AsObject();

    private static OptionsProduct Parse(JsonObject root)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(root.ToJsonString()));
        return OptionsProductManifestParser.Parse(stream);
    }
}
