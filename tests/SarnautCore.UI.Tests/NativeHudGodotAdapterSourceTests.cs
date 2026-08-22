namespace SarnautCore.UI.Tests;

public sealed class NativeHudGodotAdapterSourceTests
{
    [Fact]
    public void HudProductIsCompiledAtomicAndUsesRetailFixedPools()
    {
        string content = ReadSource("NativeHudContent.cs");

        Assert.Contains("ProductRelativePath = \"ui/hud/hud-product.json\"", content, StringComparison.Ordinal);
        Assert.Contains("paths.Resolve(manifest.RuntimeScene, \".scn\")", content, StringComparison.Ordinal);
        Assert.Contains("Native HUD Main root must be Control", content, StringComparison.Ordinal);
        Assert.Contains("Native HUD ContextCharacter root must be Control", content, StringComparison.Ordinal);
        Assert.Contains("Native HUD TargetSelection root must be Node3D", content, StringComparison.Ordinal);
        Assert.Contains("must stay beneath Main", content, StringComparison.Ordinal);
        Assert.Contains("\"avatar\"", content, StringComparison.Ordinal);
        Assert.Contains("\"enemy\"", content, StringComparison.Ordinal);
        Assert.Contains("\"experience\"", content, StringComparison.Ordinal);
        Assert.Contains("character.Visible = false", content, StringComparison.Ordinal);
        Assert.Contains("targetSelection.Visible = false", content, StringComparison.Ordinal);

        Assert.Equal(1, Count(content, "scene.Instantiate()"));
        int load = content.IndexOf("public static NativeHudContent Load", StringComparison.Ordinal);
        int attach = content.IndexOf("public void AttachTo", StringComparison.Ordinal);
        int mountTarget = content.IndexOf("world.MountTargetSelection(TargetSelection)", StringComparison.Ordinal);
        int addChild = content.IndexOf("owner.AddChild(Root)", StringComparison.Ordinal);
        int reveal = content.IndexOf("Root.Visible = true", StringComparison.Ordinal);
        Assert.True(load >= 0 && attach > load && mountTarget > attach && addChild > mountTarget && reveal > addChild);
        Assert.Contains("Detach(Root)", content[attach..], StringComparison.Ordinal);
        Assert.Contains("Detach(TargetSelection)", content[attach..], StringComparison.Ordinal);
    }

    [Fact]
    public void HostTranslatesEngineFactsAndNeverBuildsAuthoredPools()
    {
        string host = ReadSource("NativeGameplayHudHost.cs");

        Assert.Contains("HudPointerSource.Mouse", host, StringComparison.Ordinal);
        Assert.Contains("HudPointerSource.Controller", host, StringComparison.Ordinal);
        Assert.Contains("result.Consumed", host, StringComparison.Ordinal);
        Assert.Contains("_world.TryPickEntity", host, StringComparison.Ordinal);
        Assert.Contains("SampleAlpha(normalized)", host, StringComparison.Ordinal);
        Assert.Contains("Present(HudDiff diff)", host, StringComparison.Ordinal);
        Assert.Contains("NativeHudItemPresentationCatalog.Load(", host, StringComparison.Ordinal);
        Assert.Contains("paths.Resolve(NativeHudItemPresentationCatalog.RelativePath, \".res\")", host, StringComparison.Ordinal);
        Assert.Contains("_itemCatalog?.Dispose()", host, StringComparison.Ordinal);
        Assert.Contains("HudInput.ResolveMessageBox(requestId, decision)", host, StringComparison.Ordinal);
        Assert.Contains("case HudChangeKind.MessageBox", host, StringComparison.Ordinal);
        Assert.Contains("PresentSelectedTarget", host, StringComparison.Ordinal);
        Assert.Contains("radius = pickRadius * Fraction(sizing.ObjectCutAreaScale)", host, StringComparison.Ordinal);
        Assert.Contains("decal.Size = new Vector3(diameter, depth, diameter)", host, StringComparison.Ordinal);
        Assert.Contains("decal.CullMask = sizing.CullMask", host, StringComparison.Ordinal);
        Assert.Contains("case HudSemanticEvent.OpenOptions", host, StringComparison.Ordinal);
        Assert.Contains("_openNativeProduct(\"options\")", host, StringComparison.Ordinal);
        Assert.DoesNotContain("OptionsRuntime", host, StringComparison.Ordinal);
        Assert.DoesNotContain("options-product.json", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Instantiate()", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicate()", host, StringComparison.Ordinal);
        Assert.DoesNotContain("new Label", host, StringComparison.Ordinal);
        Assert.DoesNotContain("new Button", host, StringComparison.Ordinal);
    }

    [Fact]
    public void WorldProjectionReturnsFactsThroughNarrowSeam()
    {
        string world = ReadSource("GodotHudWorld.cs");

        Assert.Contains("class GodotHudWorld", world, StringComparison.Ordinal);
        Assert.Contains(": IHudWorld", world, StringComparison.Ordinal);
        Assert.Contains("camera.IsPositionBehind", world, StringComparison.Ordinal);
        Assert.Contains("camera.UnprojectPosition", world, StringComparison.Ordinal);
        Assert.Contains("scene.IsOccluded", world, StringComparison.Ordinal);
        Assert.Contains("TryPickEntity", world, StringComparison.Ordinal);
        Assert.Contains("void MountTargetSelection(Node3D targetSelection)", world, StringComparison.Ordinal);
        Assert.Contains(
            "bool TryGetGroundFootprint(ulong entityId, out Vector3 position, out float pickRadius)",
            world,
            StringComparison.Ordinal);
        Assert.Contains("position = visual.GlobalPosition", world, StringComparison.Ordinal);
        Assert.Contains("pickRadius = visual.PickRadius", world, StringComparison.Ordinal);
        Assert.Contains("worldRoot.AddChild(targetSelection)", world, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemPresentationCatalogIsCompiledExactOrdinalAndSourceFree()
    {
        string catalog = ReadSource("NativeHudItemPresentationCatalog.cs");

        Assert.Contains("ProductKey = \"hud.items.inst-league1\"", catalog, StringComparison.Ordinal);
        Assert.Contains(
            "RelativePath = \"items/item_presentation_catalog.res\"",
            catalog,
            StringComparison.Ordinal);
        Assert.Contains("Dictionary<HudId, HudItemPresentation>", catalog, StringComparison.Ordinal);
        Assert.Contains("StringComparer.Ordinal", catalog, StringComparison.Ordinal);
        Assert.Contains("OptionalId(entry[\"action_id\"]", catalog, StringComparison.Ordinal);
        Assert.Contains("TryResolveText(HudId textId", catalog, StringComparison.Ordinal);
        Assert.Contains("fallback_locale", catalog, StringComparison.Ordinal);
        Assert.Contains("prepared_state_available", catalog, StringComparison.Ordinal);
        Assert.Contains("cooldown_state_available", catalog, StringComparison.Ordinal);
        Assert.Contains(
            @"RequireString(resource, ""catalog_id"", path, ProductKey)",
            catalog,
            StringComparison.Ordinal);
        Assert.Contains("RequirePackId(resource, path)", catalog, StringComparison.Ordinal);
        Assert.Contains("NumberStyles.AllowHexSpecifier", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("allods", catalog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("has_use_spell", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("removeTime", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextPresenterUsesStableViewsAndCompiledItemPresentationOnly()
    {
        string presenter = ReadSource("NativeHudContextPresenter.cs");

        Assert.Contains("PresentInventory(HudInventoryReadModel model)", presenter, StringComparison.Ordinal);
        Assert.Contains("PresentLoot(HudLootReadModel model)", presenter, StringComparison.Ordinal);
        Assert.Contains("PresentQuestLog(HudQuestLogReadModel model)", presenter, StringComparison.Ordinal);
        Assert.Contains("PresentQuestInfo(in HudQuestInfoView model)", presenter, StringComparison.Ordinal);
        Assert.Contains("PresentCharacter(HudCharacterReadModel model)", presenter, StringComparison.Ordinal);
        Assert.Contains("PresentMessageBoxes(HudMessageBoxReadModel model)", presenter, StringComparison.Ordinal);
        Assert.Contains("request.EffectiveLifetimeMilliseconds", presenter, StringComparison.Ordinal);
        Assert.Contains("HudMessageBoxButtons.AcceptDecline", presenter, StringComparison.Ordinal);
        Assert.Contains("items.TryGet(itemId", presenter, StringComparison.Ordinal);
        Assert.Contains("items.TryResolveText", presenter, StringComparison.Ordinal);
        Assert.Contains("PreparedVisible", presenter, StringComparison.Ordinal);
        Assert.Contains("CooldownRemainingMilliseconds", presenter, StringComparison.Ordinal);
        Assert.Contains("count > 1", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveTime", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("res://", presenter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ItemId.Value", presenter, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplicatedEntityHasNoSecondNameOrHealthRenderer()
    {
        string visual = ReadSource("NetworkEntityVisual.cs");

        Assert.DoesNotContain("AddOverhead", visual, StringComparison.Ordinal);
        Assert.DoesNotContain("new Label3D", visual, StringComparison.Ordinal);
        Assert.DoesNotContain("HealthBar", visual, StringComparison.Ordinal);
        Assert.Contains("HudAnchorPosition", visual, StringComparison.Ordinal);
        Assert.Contains("ContextOvertip HUD is the sole owner", visual, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoneRelaysNativeProductIntentWithoutOwningOptions()
    {
        string zone = ReadSource("ZoneWalkabout.cs");

        Assert.Contains("NativeProductRequestedEventHandler", zone, StringComparison.Ordinal);
        Assert.Contains("RequestNativeProduct", zone, StringComparison.Ordinal);
        Assert.Contains("EmitSignal(SignalName.NativeProductRequested, productKey)", zone, StringComparison.Ordinal);
        Assert.DoesNotContain("OptionsRuntime", zone, StringComparison.Ordinal);
        Assert.DoesNotContain("options-product.json", zone, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int start = 0;
        while ((start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract-source", name));
}
