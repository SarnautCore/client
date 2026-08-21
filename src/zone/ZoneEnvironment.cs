using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Godot;

namespace SarnautCore;

/// <summary>
/// The authored lighting for one zone, read from the converted 1.1 data:
/// ZoneResource → ZoneLights → instantLights[0].light (+ PostEffectParams).
/// </summary>
/// <remarks>
/// Colors are stored as 32-bit ARGB integers (confirmed against the one field
/// the source writes in hex, ZoneResource.color). The alpha byte on authored
/// colors is not opacity — it reads as an intensity tag — so only RGB is used.
/// The instant light set is preferred over staticLight: League-start's static
/// set carries editor-day placeholder fog (10000..20000) while the instant set
/// carries the shipped cave values (20..150).
/// </remarks>
public sealed record ZoneEnvironmentSettings(
    Color AmbientColor,
    float AmbientFactor,
    Color SunColor,
    float SunYawDegrees,
    float SunPitchDegrees,
    Color FogColor,
    float FogStart,
    float FogEnd,
    Color PointLightColor,
    float ExposureMultiplier,
    string SkyMeshSource,
    string SourcePath)
{

    /// <summary>
    /// The color of the baked direct-visibility term. Outdoor zones author a
    /// warm sun in DiffuseColor and leave PointLightColor black; interior and
    /// astral zones author a near-black blue sun and put the torch light in
    /// PointLightColor (League start: amber 0x462A00 over blue ambient), so a
    /// non-black PointLightColor is the authored direct source.
    /// </summary>
    public Color DirectLightColor =>
        PointLightColor.R + PointLightColor.G + PointLightColor.B > 0.001f
            ? PointLightColor
            : SunColor;

    /// <summary>The colored ambient term of the baked two-term combine.</summary>
    public Color BakedAmbientLight => new(
        AmbientColor.R * AmbientFactor,
        AmbientColor.G * AmbientFactor,
        AmbientColor.B * AmbientFactor);

    public static bool TryLoad(
        AllodsResourceTree tree,
        string mapName,
        string zoneId,
        out ZoneEnvironmentSettings settings,
        out string error)
    {
        settings = null!;
        string zoneSource = $"Maps/{mapName}/Zones/{zoneId}/{zoneId}.(ZoneResource).xdb";
        AllodsResource? zone = tree.Load(zoneSource);
        if (zone == null)
        {
            error = $"Authored zone resource is missing: {zoneSource}";
            return false;
        }

        string lightsSource = AllodsResourceTree.NormalizeHref(
            zoneSource, AllodsResourceTree.ReadHref(zone.raw_xml, "zoneLights"));
        AllodsResource? lights = string.IsNullOrEmpty(lightsSource) ? null : tree.Load(lightsSource);
        if (lights == null)
        {
            error = $"Authored zone lights are missing: {lightsSource}";
            return false;
        }

        try
        {
            XDocument document = XDocument.Parse(lights.raw_xml);
            XElement? instant = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "instantLights")
                ?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "Item");
            XElement? light = instant?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "light")
                ?? document.Descendants().FirstOrDefault(element => element.Name.LocalName == "staticLight");
            if (light == null)
            {
                error = $"Authored zone lights carry no light block: {lightsSource}";
                return false;
            }

            string skyMesh = instant == null
                ? string.Empty
                : AllodsResourceTree.NormalizeHref(
                    lightsSource, ReadChildHref(instant, "skyMesh"));
            float exposure = ReadExposure(tree, lightsSource, instant);

            settings = new ZoneEnvironmentSettings(
                AmbientColor: ReadColor(light, "AmbientColor"),
                AmbientFactor: ReadValue(light, "AmbientFactor", 1.0f),
                SunColor: ReadColor(light, "DiffuseColor"),
                SunYawDegrees: ReadValue(light, "SunLightYaw", 0.0f),
                SunPitchDegrees: ReadValue(light, "SunLightPitch", 0.0f),
                FogColor: ReadColor(light, "FogColor"),
                FogStart: ReadValue(light, "FogStart", 0.0f),
                FogEnd: ReadValue(light, "FogEnd", 0.0f),
                PointLightColor: ReadColor(light, "PointLightColor"),
                ExposureMultiplier: exposure,
                SkyMeshSource: skyMesh,
                SourcePath: lightsSource);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = $"Authored zone lights could not be parsed ({lightsSource}): {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Drives the scene's WorldEnvironment and sun from the authored values.
    /// The scene's hand-authored defaults only survive when the authored data
    /// is unreachable.
    /// </summary>
    public void Apply(Godot.Environment environment, DirectionalLight3D sun)
    {
        environment.BackgroundMode = Godot.Environment.BGMode.Color;
        // The authored fog color backs the frame; the authored skydome
        // (ZoneSkydome, built from SkyMeshSource) draws over it when the
        // zone's converted sky parts are available.
        environment.BackgroundColor = FogColor;
        environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        environment.AmbientLightColor = AmbientColor;
        // The entity ambient base. Statics bake 2*(A*ambVis + D*dirVis) into
        // their vertices; ambient at 1x AmbientFactor equals that combine's
        // ambient half at the typical ambVis of one half, and the per-entity
        // SampledEntityLight carries the position-dependent surplus (torch
        // pools, open-sky ambient) on top. This replaces the phase-2 2x
        // MissingBakedLightCompensation placeholder, which stood in for the
        // then-missing local light term.
        environment.AmbientLightEnergy = AmbientFactor;
        environment.TonemapMode = Godot.Environment.ToneMapper.Linear;
        environment.TonemapExposure = ExposureMultiplier;
        environment.FogEnabled = FogEnd > FogStart;
        environment.FogMode = Godot.Environment.FogModeEnum.Depth;
        environment.FogDepthBegin = FogStart;
        environment.FogDepthEnd = FogEnd;
        environment.FogLightColor = FogColor;
        environment.FogSunScatter = 0;

        sun.LightColor = SunColor;
        sun.LightEnergy = 1.0f;
        // Allods aims the sun by yaw around up and pitch above the horizon;
        // Godot's light shines down its -Z, so pitch maps to a negative X
        // rotation. The League cave authors pitch 0 (a horizon-level key light).
        sun.RotationDegrees = new Vector3(-Mathf.Max(SunPitchDegrees, 8.0f), -SunYawDegrees, 0);
    }

    private static float ReadExposure(AllodsResourceTree tree, string lightsSource, XElement? instant)
    {
        if (instant == null)
        {
            return 1.0f;
        }

        string postSource = AllodsResourceTree.NormalizeHref(
            lightsSource, ReadChildHref(instant, "postEffectParams"));
        AllodsResource? post = string.IsNullOrEmpty(postSource) ? null : tree.Load(postSource);
        if (post == null)
        {
            return 1.0f;
        }

        try
        {
            XDocument document = XDocument.Parse(post.raw_xml);
            XElement? mul = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "diffuseFactor")
                ?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "mul");
            string? x = mul?.Attribute("x")?.Value;
            return x != null && float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && value > 0
                ? value
                : 1.0f;
        }
        catch
        {
            return 1.0f;
        }
    }

    private static string ReadChildHref(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == name)
            ?.Attribute("href")?.Value ?? string.Empty;

    private static float ReadValue(XElement light, string name, float fallback)
    {
        string? text = light.Elements()
            .FirstOrDefault(element => element.Name.LocalName == name)?.Value;
        return text != null && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;
    }

    private static Color ReadColor(XElement light, string name)
    {
        string? text = light.Elements()
            .FirstOrDefault(element => element.Name.LocalName == name)?.Value;
        if (text == null || !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long packed))
        {
            return Colors.Black;
        }

        uint argb = unchecked((uint)packed);
        return new Color(
            ((argb >> 16) & 0xFF) / 255.0f,
            ((argb >> 8) & 0xFF) / 255.0f,
            (argb & 0xFF) / 255.0f);
    }
}
