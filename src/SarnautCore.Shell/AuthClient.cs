using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SarnautCore.Shell;

/// <summary>
/// The client half of the account service's HTTP API (ADR 0030, ADR 0032).
/// </summary>
/// <remarks>
/// Everything a player does before a game connection exists happens here:
/// register, log in, list and create characters, read the chargen options the
/// server offers, and mint the single-use shard ticket that
/// <c>EnterZoneRequest</c> carries (session spec rule 5.3).
///
/// It takes an <see cref="HttpClient"/> rather than a base address so a test can
/// hand it a fake transport; <see cref="Create"/> is the production
/// construction. Every refusal leaves as an <see cref="AuthException"/> whose
/// <see cref="AuthException.Failure"/> is the case a screen switches on. No
/// password, email or token is ever formatted into one of those messages.
/// </remarks>
public sealed class AuthClient
{
    /// <summary>The listener ADR 0030 gave the account API, matching config.example.yaml.</summary>
    public const string DefaultBaseAddress = "http://127.0.0.1:8083";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly HttpClient _http;

    public AuthClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>Builds a client against a live service.</summary>
    public static AuthClient Create(Uri baseAddress, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        return new AuthClient(new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        });
    }

    /// <summary>Where this client talks, for a message that has to name it. Never a credential.</summary>
    public string ServiceAddress => _http.BaseAddress?.ToString() ?? "the account service";

    /// <summary>Registers an account. The service answers 409 when the address is taken.</summary>
    public async Task<Guid> RegisterAsync(string email, Secret password, CancellationToken cancellationToken = default)
    {
        RegisterDocument document = await SendAsync<RegisterDocument>(
            HttpMethod.Post,
            "/v1/accounts",
            new CredentialBody(new Secret(email), password),
            token: Secret.None,
            cancellationToken).ConfigureAwait(false);
        return ParseGuid(document.AccountId, "account_id");
    }

    /// <summary>Logs in and returns the 12-hour account session token (ADR 0030 section 2).</summary>
    public async Task<AccountSession> LoginAsync(
        string email,
        Secret password,
        CancellationToken cancellationToken = default)
    {
        LoginDocument document = await SendAsync<LoginDocument>(
            HttpMethod.Post,
            "/v1/sessions",
            new CredentialBody(new Secret(email), password),
            token: Secret.None,
            cancellationToken).ConfigureAwait(false);
        return new AccountSession(
            ParseGuid(document.AccountId, "account_id"),
            new Secret(document.SessionToken),
            ParseTimestamp(document.ExpiresAt, "expires_at"));
    }

    /// <summary>Revokes the session token. A logout that fails still ends the local session.</summary>
    public async Task LogoutAsync(Secret token, CancellationToken cancellationToken = default)
    {
        await SendAsync(HttpMethod.Delete, "/v1/sessions", body: null, token, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the account's roster.</summary>
    public async Task<IReadOnlyList<CharacterSummary>> ListCharactersAsync(
        Secret token,
        CancellationToken cancellationToken = default)
    {
        CharacterListDocument document = await SendAsync<CharacterListDocument>(
            HttpMethod.Get,
            "/v1/characters",
            body: null,
            token,
            cancellationToken).ConfigureAwait(false);
        return [.. document.Characters.Select(ToSummary)];
    }

    /// <summary>Creates a character. The name rule is the server's (ADR 0032 section 3).</summary>
    public async Task<CharacterSummary> CreateCharacterAsync(
        Secret token,
        CreateCharacterSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        CharacterDocument document = await SendAsync<CharacterDocument>(
            HttpMethod.Post,
            "/v1/characters",
            submission,
            token,
            cancellationToken).ConfigureAwait(false);
        return ToSummary(document);
    }

    /// <summary>Soft-deletes a character.</summary>
    public async Task DeleteCharacterAsync(
        Secret token,
        Guid characterId,
        CancellationToken cancellationToken = default)
    {
        await SendAsync(
            HttpMethod.Delete,
            $"/v1/characters/{characterId}",
            body: null,
            token,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reserves a name for the creation form. A courtesy, not an authority.</summary>
    public async Task<NameCheck> CheckNameAsync(
        Secret token,
        string name,
        CancellationToken cancellationToken = default)
    {
        NameCheckDocument document = await SendAsync<NameCheckDocument>(
            HttpMethod.Post,
            "/v1/characters/name-checks",
            new NameCheckBody(name),
            token,
            cancellationToken).ConfigureAwait(false);
        return new NameCheck(
            document.Name,
            document.Available,
            document.Reason ?? string.Empty,
            string.IsNullOrEmpty(document.ReservedUntil)
                ? null
                : ParseTimestamp(document.ReservedUntil, "reserved_until"));
    }

    /// <summary>
    /// Mints the 60-second single-use ticket the shard redeems over NATS.
    /// </summary>
    public async Task<ShardTicket> MintTicketAsync(
        Secret token,
        Guid characterId,
        CancellationToken cancellationToken = default)
    {
        TicketDocument document = await SendAsync<TicketDocument>(
            HttpMethod.Post,
            "/v1/tickets",
            new TicketBody(characterId.ToString()),
            token,
            cancellationToken).ConfigureAwait(false);
        return new ShardTicket(
            new Secret(document.Ticket),
            ParseGuid(document.CharacterId, "character_id"),
            ParseTimestamp(document.ExpiresAt, "expires_at"));
    }

    /// <summary>
    /// Reads the character-creation options the server offers.
    /// </summary>
    /// <remarks>
    /// Unauthenticated on the server's side on purpose: the option list is
    /// content, so the creation screen can render before anyone has logged in.
    /// The client renders this list and never derives it (ADR 0032 section 2).
    /// </remarks>
    public async Task<IReadOnlyList<ChargenOption>> ListChargenOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        ChargenOptionsDocument document = await SendAsync<ChargenOptionsDocument>(
            HttpMethod.Get,
            "/v1/chargen/options",
            body: null,
            token: Secret.None,
            cancellationToken).ConfigureAwait(false);
        return
        [
            .. document.Options.Select(option => new ChargenOption(
                option.Id,
                option.Race,
                option.Class,
                option.Sex,
                option.Faction,
                option.NameKey,
                option.DescriptionKey,
                option.VisualRef,
                option.SpawnZoneId,
                option.StartingLevel,
                option.SpawnX,
                option.SpawnY,
                option.SpawnZ)),
        ];
    }

    private async Task<TDocument> SendAsync<TDocument>(
        HttpMethod method,
        string path,
        object? body,
        Secret token,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await ExchangeAsync(method, path, body, token, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            TDocument? document = await response.Content
                .ReadFromJsonAsync<TDocument>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (document is null)
            {
                throw new AuthException(
                    AuthFailure.ProtocolError,
                    "The account service answered with an empty document.");
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw new AuthException(
                AuthFailure.ProtocolError,
                "The account service answered with something this build cannot read.",
                exception);
        }
    }

    private async Task SendAsync(
        HttpMethod method,
        string path,
        object? body,
        Secret token,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await ExchangeAsync(method, path, body, token, cancellationToken)
            .ConfigureAwait(false);
        response.Dispose();
    }

    private async Task<HttpResponseMessage> ExchangeAsync(
        HttpMethod method,
        string path,
        object? body,
        Secret token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        }

        if (!token.IsEmpty)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Reveal());
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // The message names the address and the transport reason and nothing
            // else: the request body is where the password is.
            throw new AuthException(
                AuthFailure.Unreachable,
                $"The account service at {ServiceAddress} could not be reached ({exception.Message}).",
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthException(
                AuthFailure.Unreachable,
                $"The account service at {ServiceAddress} did not answer in time.",
                exception);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await ReadRefusalAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<AuthException> ReadRefusalAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ErrorDocument? document = null;
        try
        {
            document = await response.Content
                .ReadFromJsonAsync<ErrorDocument>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // A refusal that is not the documented document still has a status.
        }

        string code = document?.Error ?? string.Empty;
        string message = string.IsNullOrWhiteSpace(document?.Message)
            ? DefaultMessage(response.StatusCode)
            : document!.Message;
        return new AuthException(FailureFor(code, response.StatusCode), message);
    }

    private static AuthFailure FailureFor(string code, HttpStatusCode status) => code switch
    {
        "INVALID_CREDENTIALS" => AuthFailure.InvalidCredentials,
        "UNAUTHENTICATED" => AuthFailure.Unauthenticated,
        "EMAIL_INVALID" => AuthFailure.EmailInvalid,
        "EMAIL_TAKEN" => AuthFailure.EmailTaken,
        "PASSWORD_REQUIRED" => AuthFailure.PasswordRequired,
        "PASSWORD_TOO_SHORT" => AuthFailure.PasswordTooShort,
        "NAME_INVALID" => AuthFailure.NameInvalid,
        "NAME_BLOCKED" => AuthFailure.NameBlocked,
        "NAME_TAKEN" => AuthFailure.NameTaken,
        "UNKNOWN_OPTION" => AuthFailure.UnknownOption,
        "OPTION_DISABLED" => AuthFailure.OptionDisabled,
        "CHARACTER_NOT_FOUND" => AuthFailure.CharacterNotFound,
        "CHARACTER_ID_INVALID" => AuthFailure.CharacterIdInvalid,
        "MALFORMED_REQUEST" => AuthFailure.MalformedRequest,
        "INTERNAL" => AuthFailure.ServiceError,
        _ => status == HttpStatusCode.Unauthorized ? AuthFailure.Unauthenticated : AuthFailure.ServiceError,
    };

    private static string DefaultMessage(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "The account service refused the credential.",
        HttpStatusCode.NotFound => "The account service has no such record.",
        HttpStatusCode.Conflict => "That is already taken.",
        _ => $"The account service refused the request ({(int)status}).",
    };

    private static CharacterSummary ToSummary(CharacterDocument document) => new(
        ParseGuid(document.CharacterId, "character_id"),
        document.Name,
        document.ChargenOptionId,
        ParseTimestamp(document.CreatedAt, "created_at"));

    private static Guid ParseGuid(string? value, string field)
    {
        if (Guid.TryParse(value, out Guid parsed))
        {
            return parsed;
        }

        throw new AuthException(
            AuthFailure.ProtocolError,
            $"The account service sent a {field} this build cannot read.");
    }

    private static DateTimeOffset ParseTimestamp(string? value, string field)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            return parsed;
        }

        throw new AuthException(
            AuthFailure.ProtocolError,
            $"The account service sent a {field} this build cannot read.");
    }

    private sealed record CredentialBody(
        [property: JsonPropertyName("email")] Secret Email,
        [property: JsonPropertyName("password")] Secret Password);

    private sealed record NameCheckBody([property: JsonPropertyName("name")] string Name);

    private sealed record TicketBody([property: JsonPropertyName("character_id")] string CharacterId);

    private sealed record RegisterDocument([property: JsonPropertyName("account_id")] string AccountId);

    private sealed record LoginDocument(
        [property: JsonPropertyName("session_token")] string SessionToken,
        [property: JsonPropertyName("account_id")] string AccountId,
        [property: JsonPropertyName("expires_at")] string ExpiresAt);

    private sealed record CharacterDocument(
        [property: JsonPropertyName("character_id")] string CharacterId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("chargen_option_id")] string ChargenOptionId,
        [property: JsonPropertyName("created_at")] string CreatedAt);

    private sealed record CharacterListDocument(
        [property: JsonPropertyName("characters")] IReadOnlyList<CharacterDocument> Characters);

    private sealed record NameCheckDocument(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("available")] bool Available,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("reserved_until")] string? ReservedUntil);

    private sealed record TicketDocument(
        [property: JsonPropertyName("ticket")] string Ticket,
        [property: JsonPropertyName("character_id")] string CharacterId,
        [property: JsonPropertyName("expires_at")] string ExpiresAt);

    private sealed record ChargenOptionDocument(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("race")] string Race,
        [property: JsonPropertyName("class")] string Class,
        [property: JsonPropertyName("sex")] string Sex,
        [property: JsonPropertyName("faction")] string Faction,
        [property: JsonPropertyName("name_key")] string NameKey,
        [property: JsonPropertyName("description_key")] string DescriptionKey,
        [property: JsonPropertyName("visual_ref")] string VisualRef,
        [property: JsonPropertyName("spawn_zone_id")] string SpawnZoneId,
        [property: JsonPropertyName("starting_level")] uint StartingLevel,
        [property: JsonPropertyName("spawn_x")] float SpawnX,
        [property: JsonPropertyName("spawn_y")] float SpawnY,
        [property: JsonPropertyName("spawn_z")] float SpawnZ);

    private sealed record ChargenOptionsDocument(
        [property: JsonPropertyName("options")] IReadOnlyList<ChargenOptionDocument> Options);

    private sealed record ErrorDocument(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("message")] string Message);
}
