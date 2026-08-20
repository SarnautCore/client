namespace SarnautCore.Shell;

/// <summary>
/// A refusal from the account service, carrying the case a screen switches on
/// and the sentence it shows.
/// </summary>
/// <remarks>
/// The message is the server's own <c>message</c> field wherever the server sent
/// one, because the server's answer is the one that counts (ADR 0032
/// consequences). No credential ever reaches this message: the fields that could
/// carry one are <see cref="Secret"/>, and nothing here formats a request body.
/// </remarks>
public sealed class AuthException : Exception
{
    public AuthException(AuthFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public AuthFailure Failure { get; }
}
