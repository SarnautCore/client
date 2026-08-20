using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SarnautCore.Shell;

/// <summary>
/// A password or token, carried in a type that cannot be printed by accident.
/// </summary>
/// <remarks>
/// The mirror of the server's <c>internal/account/secret.Value</c> (ADR 0030
/// section 5). Every conversion this type offers returns <c>[redacted]</c>, so an
/// accidental interpolation, <see cref="object.ToString"/> or JSON encode of a
/// containing record is already redacted. Reaching the real characters requires
/// <see cref="Reveal"/>, which is one grep away.
/// </remarks>
[JsonConverter(typeof(SecretJsonConverter))]
public readonly struct Secret : IEquatable<Secret>
{
    /// <summary>The text every conversion produces in place of the value.</summary>
    public const string Redacted = "[redacted]";

    private readonly string? _value;

    public Secret(string value) => _value = value;

    /// <summary>An absent secret. Distinct from a secret whose value is empty only in intent.</summary>
    public static Secret None => default;

    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>Returns the real characters. Every call site is a deliberate disclosure.</summary>
    public string Reveal() => _value ?? string.Empty;

    public override string ToString() => Redacted;

    public bool Equals(Secret other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Secret other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value ?? string.Empty);

    public static bool operator ==(Secret left, Secret right) => left.Equals(right);

    public static bool operator !=(Secret left, Secret right) => !left.Equals(right);
}

/// <summary>
/// Writes a <see cref="Secret"/> as its real value, because the only place one
/// travels is the request body that has to carry it.
/// </summary>
internal sealed class SecretJsonConverter : JsonConverter<Secret>
{
    public override Secret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new Secret(reader.GetString() ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Reveal());
    }
}
