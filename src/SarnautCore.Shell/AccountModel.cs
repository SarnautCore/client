using System.Text.Json.Serialization;

namespace SarnautCore.Shell;

/// <summary>One account's HTTP credential, from <c>POST /v1/sessions</c>.</summary>
public sealed record AccountSession(Guid AccountId, Secret Token, DateTimeOffset ExpiresAt);

/// <summary>One row of the account's roster, from <c>GET /v1/characters</c>.</summary>
public sealed record CharacterSummary(
    Guid CharacterId,
    string Name,
    string ChargenOptionId,
    DateTimeOffset CreatedAt);

/// <summary>
/// One character-creation option, from <c>GET /v1/chargen/options</c>.
/// </summary>
/// <remarks>
/// Every field is a pack row the server read (ADR 0032 section 2). The client
/// holds no starting item, no starting stat and no spawn coordinate of its own,
/// so a second playable option is a data change rather than a client rebuild.
/// </remarks>
public sealed record ChargenOption(
    string Id,
    string Race,
    string Class,
    string Sex,
    string Faction,
    string NameKey,
    string DescriptionKey,
    string VisualRef,
    string SpawnZoneId,
    uint StartingLevel,
    float SpawnX,
    float SpawnY,
    float SpawnZ);

/// <summary>The opaque single-use shard ticket, from <c>POST /v1/tickets</c>.</summary>
public sealed record ShardTicket(Secret Token, Guid CharacterId, DateTimeOffset ExpiresAt);

/// <summary>
/// The courtesy answer of <c>POST /v1/characters/name-checks</c>. A reservation,
/// not an authority: the unique index decides (ADR 0032 section 3).
/// </summary>
public sealed record NameCheck(string Name, bool Available, string Reason, DateTimeOffset? ReservedUntil);

/// <summary>
/// The body <c>POST /v1/characters</c> receives.
/// </summary>
/// <remarks>
/// Built by <see cref="CharacterCreateViewModel.BuildSubmission"/> and by nothing
/// else, so the payload has one construction site and one test.
/// </remarks>
public sealed record CreateCharacterSubmission(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("chargen_option_id")] string ChargenOptionId);
