using System.Net;

namespace SarnautCore.Networking;

public readonly record struct GameEndpoint(string Host, int Port)
{
    public static GameEndpoint Parse(string value, int defaultPort = 4242)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();

        if (Uri.TryCreate($"quic://{trimmed}", UriKind.Absolute, out Uri? uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            int port = uri.IsDefaultPort ? defaultPort : uri.Port;
            if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            {
                throw new FormatException($"Invalid game server port in '{value}'.");
            }

            return new GameEndpoint(uri.Host, port);
        }

        throw new FormatException($"Invalid game server endpoint '{value}'. Use host:port.");
    }
}
