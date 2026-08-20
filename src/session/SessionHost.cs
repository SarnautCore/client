using System;
using Godot;
using SarnautCore.Shell;

namespace SarnautCore;

/// <summary>
/// The autoload that carries the account, its token and the chosen character
/// across scene changes, and owns which scene each screen is.
/// </summary>
/// <remarks>
/// Before this existed the cross-scene seam was three static properties on
/// <c>ZoneWalkabout</c>. Statics are writable from anywhere, survive a scene
/// change by accident rather than by design, and cannot carry a credential
/// safely. Everything they did is now a field on one node with one lifetime,
/// reached through <see cref="Of"/> rather than through a static of its own.
///
/// It holds no game rules: the race and class list, the starting kit and the
/// spawn all come from the server (ADR 0032).
/// </remarks>
public partial class SessionHost : Node
{
    /// <summary>The autoload path, fixed by <c>project.godot</c>.</summary>
    public const string NodePath = "/root/Session";

    private const string AuthAddressVariable = "SARNAUT_AUTH_ADDRESS";
    private const string ServerAddressVariable = "SARNAUT_SERVER_ADDRESS";
    private const string DefaultServerAddress = "127.0.0.1:4242";

    private AuthClient? _auth;

    /// <summary>Who is signed in and which character they picked.</summary>
    public PlayerSession Player { get; } = new();

    /// <summary>Which screen the shell is on, and which moves are legal from it.</summary>
    public ScreenFlow Flow { get; } = new();

    /// <summary>What the zone scene loads when it next runs.</summary>
    public ZoneRequest Zone { get; set; } = ZoneRequest.Offline(ZoneLoader.DefaultMapName, "InstLeague1");

    /// <summary>The shard endpoint, from the environment or the default.</summary>
    public string ServerAddress { get; set; } =
        System.Environment.GetEnvironmentVariable(ServerAddressVariable) ?? DefaultServerAddress;

    /// <summary>
    /// The runtime pack this client claims in <c>ClientHello</c> (ADR 0029).
    /// Empty is legal and means the shard decides whether to admit a client that
    /// names no pack.
    /// </summary>
    public string ContentPackId { get; set; } = ContentPackIdentity.FromEnvironment();

    /// <summary>The account service, built once per run.</summary>
    public AuthClient Auth => _auth ??= AuthClient.Create(new Uri(
        System.Environment.GetEnvironmentVariable(AuthAddressVariable) ?? AuthClient.DefaultBaseAddress));

    /// <summary>Reaches the autoload from any node, without a static of its own.</summary>
    public static SessionHost Of(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.GetNode<SessionHost>(NodePath);
    }

    public override void _Ready()
    {
        // One mount for the whole run: every Control in every scene inherits the
        // root window's theme, so no screen loads one itself.
        UiTheme.Mount(GetTree().Root);
        GD.Print($"Session: theme={UiTheme.Source}");
    }

    /// <summary>Moves the shell to a screen and swaps to the scene that shows it.</summary>
    public void Show(Screen screen)
    {
        Error error = GetTree().ChangeSceneToFile(SceneFor(screen));
        if (error != Error.Ok)
        {
            GD.PushError($"Session: could not open {screen} ({error}).");
        }
    }

    /// <summary>
    /// Ends the account session locally, and best-effort on the service.
    /// </summary>
    /// <remarks>
    /// The local half happens whatever the service says: a logout that could not
    /// be delivered still means this client is no longer signed in, and the token
    /// expires on its own in 12 hours regardless (ADR 0030 section 2).
    /// </remarks>
    public async void SignOut()
    {
        Secret token = Player.Token;
        Player.SignOut();
        Flow.SignedOut();
        Show(Screen.Login);
        if (token.IsEmpty)
        {
            return;
        }

        try
        {
            await Auth.LogoutAsync(token).ConfigureAwait(false);
        }
        catch (AuthException exception)
        {
            GD.Print($"Session: the account service did not confirm the logout ({exception.Failure}).");
        }
    }

    private static string SceneFor(Screen screen) => screen switch
    {
        Screen.Start => "res://scenes/boot.tscn",
        Screen.Login => "res://scenes/ui/login.tscn",
        Screen.CharacterSelect => "res://scenes/ui/character_select.tscn",
        Screen.CharacterCreate => "res://scenes/ui/character_create.tscn",
        Screen.EnteringWorld or Screen.InWorld => "res://scenes/zone_walkabout.tscn",
        _ => "res://scenes/boot.tscn",
    };
}
