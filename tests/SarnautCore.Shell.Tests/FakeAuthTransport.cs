using System.Net;
using System.Text;

namespace SarnautCore.Shell.Tests;

/// <summary>One request this transport saw, with its body already read.</summary>
internal sealed record RecordedRequest(HttpMethod Method, string Path, string? Authorization, string Body);

/// <summary>
/// A fake account service. It answers from a script rather than a socket, so the
/// auth client's error mapping is tested without a listener, a certificate or a
/// database — none of which CI has.
/// </summary>
internal sealed class FakeAuthTransport : HttpMessageHandler
{
    private readonly Func<RecordedRequest, HttpResponseMessage> _respond;
    private readonly List<RecordedRequest> _requests = [];

    private FakeAuthTransport(Func<RecordedRequest, HttpResponseMessage> respond) => _respond = respond;

    public IReadOnlyList<RecordedRequest> Requests => _requests;

    /// <summary>Answers every request with the same status and body.</summary>
    public static FakeAuthTransport Always(HttpStatusCode status, string json) =>
        new(_ => Json(status, json));

    /// <summary>Answers per request, for a flow that makes more than one call.</summary>
    public static FakeAuthTransport Scripted(Func<RecordedRequest, HttpResponseMessage> respond) =>
        new(respond);

    /// <summary>Fails the way a service that is not listening fails.</summary>
    public static FakeAuthTransport Unreachable(string reason = "No connection could be made") =>
        new(_ => throw new HttpRequestException(reason));

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public HttpClient Client() => new(this) { BaseAddress = new Uri("http://127.0.0.1:8083") };

    public AuthClient Auth() => new(Client());

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri?.AbsolutePath ?? string.Empty,
            request.Headers.Authorization?.Parameter,
            body);
        _requests.Add(recorded);
        return _respond(recorded);
    }
}
