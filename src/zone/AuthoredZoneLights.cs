using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace SarnautCore;

/// <summary>
/// A map cell's authored point lights, read from the converter's
/// <c>X_Y_MapRegion.xdb.lights.json</c> companion. Each entry is one
/// <c>client.Scene.LightComponent</c> found on a placed object's visual
/// template, with the light pivot already transformed into the placement's
/// tile-local Godot position.
/// </summary>
/// <remarks>
/// The source component authors geometry only — pivot, radius, intensity,
/// attenuation — and no color: retail tints every placed point light with the
/// zone's ZoneLights <c>PointLightColor</c> at render time, which is the same
/// authored color the baked direct term is colored with. The client therefore
/// colors these lights with <see cref="ZoneEnvironmentSettings.DirectLightColor"/>.
/// A negative intensity is an authored anti-light (a darkener used by the
/// Anti_Light_Cube editor markers); those are counted and skipped, not placed.
/// </remarks>
public sealed record AuthoredZoneLight(
    int ObjectIndex,
    string TemplateHref,
    Vector3 Position,
    float Radius,
    float Intensity,
    float AttenuationPower);

public static class AuthoredZoneLights
{
    public const string Format = "sarnaut-authored-lights-v1";

    /// <summary>
    /// Parses a lights companion document. Returns null when the JSON is not a
    /// valid companion of the expected format; entries with a non-positive
    /// radius are dropped, anti-lights (intensity &lt;= 0) are returned but
    /// expected to be skipped by the caller.
    /// </summary>
    public static IReadOnlyList<AuthoredZoneLight>? Parse(string json)
    {
        try
        {
            DocumentJson? document = JsonSerializer.Deserialize<DocumentJson>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (document?.Lights == null || document.Format != Format)
            {
                return null;
            }

            var lights = new List<AuthoredZoneLight>(document.Lights.Length);
            foreach (LightJson light in document.Lights)
            {
                if (light.Position is not { Length: 3 } || light.Radius <= 0)
                {
                    continue;
                }

                lights.Add(new AuthoredZoneLight(
                    light.ObjectIndex,
                    light.TemplateHref ?? string.Empty,
                    new Vector3(light.Position[0], light.Position[1], light.Position[2]),
                    light.Radius,
                    light.Intensity,
                    light.AttenuationPower));
            }

            return lights;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"AuthoredZoneLights could not parse a lights companion: {exception.Message}");
            return null;
        }
    }

    private sealed class DocumentJson
    {
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("lights")] public LightJson[]? Lights { get; set; }
    }

    private sealed class LightJson
    {
        [JsonPropertyName("object_index")] public int ObjectIndex { get; set; }
        [JsonPropertyName("template_href")] public string? TemplateHref { get; set; }
        [JsonPropertyName("position")] public float[]? Position { get; set; }
        [JsonPropertyName("radius")] public float Radius { get; set; }
        [JsonPropertyName("intensity")] public float Intensity { get; set; } = 1.0f;
        [JsonPropertyName("intensity_random")] public float IntensityRandom { get; set; }
        [JsonPropertyName("attenuation_power")] public float AttenuationPower { get; set; } = 1.0f;
        [JsonPropertyName("facing_value")] public float FacingValue { get; set; } = -1.0f;
        [JsonPropertyName("direction")] public float[]? Direction { get; set; }
    }
}
