namespace SarnautCore.UI;

/// <summary>A game version whose spelling is part of the acceptance contract.</summary>
public sealed record GameVersion
{
    public GameVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The game version cannot be empty", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

/// <summary>Supplies the exact version shown by this client build.</summary>
public interface IGameVersionSource
{
    GameVersion Current { get; }
}

/// <summary>Persists which exact game version accepted the EULA.</summary>
public interface IEulaAcceptanceStore
{
    GameVersion? AcceptedVersion { get; }
    void Accept(GameVersion version);
    void Clear();
}

/// <summary>Requests a normal client shutdown after a refusal.</summary>
public interface IApplicationExitRequest
{
    void RequestExit();
}

/// <summary>Hands control back to the out-of-game flow after EULA resolution.</summary>
public interface IEulaContinuation
{
    void ContinueAfterEula();
}

/// <summary>One authored EULA document in its product-defined order.</summary>
public sealed record EulaDocument(string Id, string Body);

public enum EulaStatus
{
    NotStarted,
    Presenting,
    Continued,
    ExitRequested,
}

public enum EulaCommand
{
    Accept,
    Decline,
    Close,
}

/// <summary>The complete render state consumed by the client UI adapter.</summary>
public sealed record EulaViewState(
    EulaStatus Status,
    EulaDocument? Document,
    int DocumentNumber,
    int DocumentCount,
    bool CanAccept)
{
    public bool IsVisible => Status == EulaStatus.Presenting;
}

/// <summary>
/// Owns EULA lifecycle rules without depending on Godot or a product manifest.
/// </summary>
public sealed class EulaController
{
    public const int RequiredDocumentCount = 3;

    private readonly IReadOnlyList<EulaDocument> _documents;
    private readonly GameVersion _currentVersion;
    private readonly IEulaAcceptanceStore _acceptance;
    private readonly IApplicationExitRequest _exit;
    private readonly IEulaContinuation _continuation;
    private int _documentIndex;
    private bool _scrollAtEnd;

    public EulaController(
        IReadOnlyList<EulaDocument> documents,
        IGameVersionSource gameVersion,
        IEulaAcceptanceStore acceptance,
        IApplicationExitRequest exit,
        IEulaContinuation continuation)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(gameVersion);
        ArgumentNullException.ThrowIfNull(acceptance);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(continuation);
        ValidateDocuments(documents);

        _documents = documents.ToArray();
        _currentVersion = gameVersion.Current
            ?? throw new ArgumentException("The game version source returned null", nameof(gameVersion));
        _acceptance = acceptance;
        _exit = exit;
        _continuation = continuation;
    }

    public EulaStatus Status { get; private set; } = EulaStatus.NotStarted;

    public EulaViewState State => new(
        Status,
        Status == EulaStatus.Presenting ? _documents[_documentIndex] : null,
        Status == EulaStatus.Presenting ? _documentIndex + 1 : 0,
        _documents.Count,
        Status == EulaStatus.Presenting && _scrollAtEnd);

    /// <summary>
    /// Starts once. An exact stored version bypasses the modal and continues.
    /// </summary>
    public EulaViewState Start()
    {
        if (Status != EulaStatus.NotStarted)
        {
            return State;
        }

        if (_acceptance.AcceptedVersion == _currentVersion)
        {
            Status = EulaStatus.Continued;
            _continuation.ContinueAfterEula();
            return State;
        }

        Status = EulaStatus.Presenting;
        _documentIndex = 0;
        _scrollAtEnd = false;
        return State;
    }

    /// <summary>Updates whether the current document has reached its scroll end.</summary>
    public EulaViewState SetScrollAtEnd(bool atEnd)
    {
        if (Status == EulaStatus.Presenting)
        {
            _scrollAtEnd = atEnd;
        }

        return State;
    }

    /// <summary>Applies a product action after the manifest adapter maps it.</summary>
    public EulaViewState Dispatch(EulaCommand command)
    {
        if (Status != EulaStatus.Presenting)
        {
            return State;
        }

        switch (command)
        {
            case EulaCommand.Accept:
                AcceptCurrentDocument();
                break;
            case EulaCommand.Decline:
            case EulaCommand.Close:
                Refuse();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }

        return State;
    }

    private void AcceptCurrentDocument()
    {
        if (!_scrollAtEnd)
        {
            return;
        }

        if (_documentIndex < _documents.Count - 1)
        {
            _documentIndex++;
            _scrollAtEnd = false;
            return;
        }

        _acceptance.Accept(_currentVersion);
        Status = EulaStatus.Continued;
        _scrollAtEnd = false;
        _continuation.ContinueAfterEula();
    }

    private void Refuse()
    {
        _acceptance.Clear();
        Status = EulaStatus.ExitRequested;
        _scrollAtEnd = false;
        _exit.RequestExit();
    }

    private static void ValidateDocuments(IReadOnlyList<EulaDocument> documents)
    {
        if (documents.Count != RequiredDocumentCount)
        {
            throw new ArgumentException(
                $"The EULA requires exactly {RequiredDocumentCount} ordered documents",
                nameof(documents));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (EulaDocument document in documents)
        {
            if (document is null)
            {
                throw new ArgumentException("An EULA document cannot be null", nameof(documents));
            }

            if (string.IsNullOrWhiteSpace(document.Id))
            {
                throw new ArgumentException("An EULA document id cannot be empty", nameof(documents));
            }

            if (!ids.Add(document.Id))
            {
                throw new ArgumentException(
                    $"EULA document id '{document.Id}' is duplicated",
                    nameof(documents));
            }

            if (string.IsNullOrWhiteSpace(document.Body))
            {
                throw new ArgumentException(
                    $"EULA document '{document.Id}' has no body",
                    nameof(documents));
            }
        }
    }
}
