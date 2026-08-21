using Godot;

namespace SarnautCore;

/// <summary>
/// The per-character local light that carries the baked direct term to a
/// dynamic entity. It periodically samples <see cref="BakedLightProbe.Active"/>
/// at its own world position and drives an omni light colored with the zone's
/// authored direct source, so a character in a torch pool is lit warm and one
/// outside stays with the scene's cool ambient.
/// </summary>
/// <remarks>
/// <para>
/// The target is the statics' own baked combine at the entity's position,
/// <c>2 * (A*ambVis + D*dirVis)</c> with A the colored ambient term and D the
/// direct color. The scene environment already provides the base <c>A</c>
/// (ambient at 1x AmbientFactor equals the combine at the typical ambVis of
/// one half), so this light carries only the surplus:
/// <c>A*max(2*ambVis - 1, 0) + D*2*dirVis</c>. A character in a torch pool
/// therefore goes warm amber over the blue base, and one in astral shade adds
/// nothing and stays cool — the same reading a baked wall next to it has.
/// </para>
/// <para>
/// The light culls to <see cref="DynamicEntityLighting.ReceiverLayerMask"/>
/// only: baked statics and terrain keep their own baked combine and are never
/// double-lit. Nearby entities inside the small range share the pool's light,
/// which is the intended reading of a shared torch.
/// </para>
/// </remarks>
public partial class SampledEntityLight : OmniLight3D
{
    private const float SampleIntervalSeconds = 0.2f;

    /// <summary>
    /// Compensates the omni's geometric losses so the surplus arrives at
    /// roughly full strength on the skin: average attenuation over the torso
    /// (~0.65 at this range) times average N·L on lit faces (~0.7).
    /// </summary>
    private const float GeometricCompensation = 2.2f;

    private double _sinceSample = SampleIntervalSeconds;
    private bool _reported;

    public override void _Ready()
    {
        Name = "SampledBakedLight";
        Position = new Vector3(0, 1.15f, 0);
        LightCullMask = DynamicEntityLighting.ReceiverLayerMask;
        OmniRange = 2.8f;
        OmniAttenuation = 1.0f;
        ShadowEnabled = false;
        LightEnergy = 0.0f;
        Visible = false;
    }

    public override void _Process(double delta)
    {
        _sinceSample += delta;
        if (_sinceSample < SampleIntervalSeconds)
        {
            return;
        }

        _sinceSample = 0;
        BakedLightProbe? probe = BakedLightProbe.Active;
        if (probe == null || !IsInsideTree())
        {
            Visible = false;
            return;
        }

        (float ambient, float direct) = probe.Sample(GlobalPosition);
        Color a = probe.AmbientColor;
        Color d = probe.DirectColor;
        float ambientSurplus = Mathf.Max(2.0f * ambient - 1.0f, 0.0f);
        float directShare = 2.0f * direct;
        var surplus = new Vector3(
            a.R * ambientSurplus + d.R * directShare,
            a.G * ambientSurplus + d.G * directShare,
            a.B * ambientSurplus + d.B * directShare);
        float peak = Mathf.Max(surplus.X, Mathf.Max(surplus.Y, surplus.Z));
        if (!_reported && System.Environment.GetEnvironmentVariable("SARNAUT_LIGHT_DEBUG") == "1")
        {
            _reported = true;
            GD.Print(
                $"SampledEntityLight {GetParent()?.Name}: pos={GlobalPosition} ambient={ambient:F3} "
                + $"direct={direct:F3} surplus=({surplus.X:F3}, {surplus.Y:F3}, {surplus.Z:F3})");
        }

        if (peak <= 0.01f)
        {
            Visible = false;
            return;
        }

        LightColor = new Color(surplus.X / peak, surplus.Y / peak, surplus.Z / peak);
        LightEnergy = peak * GeometricCompensation;
        Visible = true;
    }
}
