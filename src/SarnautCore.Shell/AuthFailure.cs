namespace SarnautCore.Shell;

/// <summary>
/// The answers the account service gives that a screen reacts to differently.
/// </summary>
/// <remarks>
/// One case per error code the server's HTTP surface emits
/// (<c>SERVER:internal/account/http.go</c>), plus the two failures that never
/// reach it: the service is unreachable, and it answered something this build
/// cannot read.
/// </remarks>
public enum AuthFailure
{
    /// <summary>The service could not be reached at all: no answer, or no route to it.</summary>
    Unreachable,

    /// <summary>The service answered with something that is not the documented document.</summary>
    ProtocolError,

    /// <summary>Email or password is wrong, or the account is disabled. The server does not say which.</summary>
    InvalidCredentials,

    /// <summary>The session token is missing, expired or revoked.</summary>
    Unauthenticated,

    /// <summary>That email address is not usable.</summary>
    EmailInvalid,

    /// <summary>That email address is already registered.</summary>
    EmailTaken,

    /// <summary>A password is required.</summary>
    PasswordRequired,

    /// <summary>The password is shorter than the service accepts.</summary>
    PasswordTooShort,

    /// <summary>The name does not have the shape ADR 0032 section 3 fixes.</summary>
    NameInvalid,

    /// <summary>The name contains a blocked substring.</summary>
    NameBlocked,

    /// <summary>Another character already holds that name, normalized.</summary>
    NameTaken,

    /// <summary>No chargen option carries that id.</summary>
    UnknownOption,

    /// <summary>The chargen option exists and is not playable.</summary>
    OptionDisabled,

    /// <summary>No such character on this account.</summary>
    CharacterNotFound,

    /// <summary>That is not a character id.</summary>
    CharacterIdInvalid,

    /// <summary>The service refused the request body.</summary>
    MalformedRequest,

    /// <summary>The service failed and told us nothing about why, on purpose.</summary>
    ServiceError,
}
