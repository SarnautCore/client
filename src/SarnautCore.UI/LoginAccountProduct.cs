namespace SarnautCore.UI;

/// <summary>The product-owned contract the account login screen must provide.</summary>
public sealed class LoginAccountProduct
{
    public const string ScreenId = "login-account";
    public const string AccountRole = "account-input";
    public const string PasswordRole = "password-input";
    public const string EnterRole = "enter-button";
    public const string OptionsRole = "options-button";
    public const string LocalRole = "local-button";
    public const string CreditsRole = "credits-button";
    public const string ExitRole = "exit-button";
    public const string AccountValue = "account-name";
    public const string PasswordValue = "account-password";
    public const string SubmitAction = "submit-login";
    public const string CancelAction = "cancel-login";
    public const string ToggleOptionsAction = "toggle-options";
    public const string LocalSessionAction = "start-local-session";
    public const string CreditsAction = "show-credits";
    public const string QuitAction = "quit";

    private static readonly string[] RequiredRoles =
    [
        AccountRole,
        PasswordRole,
        EnterRole,
        OptionsRole,
        LocalRole,
        CreditsRole,
        ExitRole,
    ];

    private static readonly string[] RequiredActions =
    [
        SubmitAction,
        CancelAction,
        ToggleOptionsAction,
        LocalSessionAction,
        CreditsAction,
        QuitAction,
    ];

    private static readonly string[] RequiredValues =
    [
        AccountValue,
        PasswordValue,
    ];

    private static readonly string[] RequiredButtons =
    [
        EnterRole,
        OptionsRole,
        LocalRole,
        CreditsRole,
        ExitRole,
    ];

    private LoginAccountProduct(
        UiScreenDefinition screen,
        UiValueBinding account,
        UiValueBinding password)
    {
        Screen = screen;
        Account = account;
        Password = password;
    }

    public UiScreenDefinition Screen { get; }
    public UiValueBinding Account { get; }
    public UiValueBinding Password { get; }

    public static LoginAccountProduct Bind(UiProductManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        UiScreenDefinition screen = manifest.Screens.SingleOrDefault(
            candidate => candidate.Id == ScreenId)
            ?? throw new InvalidDataException($"UI product has no '{ScreenId}' screen");

        RequireExactSet(screen.Roles.Select(role => role.Id), RequiredRoles, "role");
        RequireExactSet(screen.Actions.Select(action => action.Id), RequiredActions, "action");
        RequireExactSet(screen.Values.Select(value => value.Id), RequiredValues, "value");
        RequireExactSet(screen.Buttons.Select(button => button.Role), RequiredButtons, "button");
        if (screen.Collections.Count != 0)
        {
            throw new InvalidDataException(
                $"Screen '{ScreenId}' has an incompatible collection set");
        }

        if (!screen.FocusOrder.SequenceEqual(RequiredRoles, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Screen '{ScreenId}' has an incompatible focus order");
        }

        UiValueBinding account = RequireTextValue(
            screen,
            AccountValue,
            AccountRole,
            secret: false);
        UiValueBinding password = RequireTextValue(
            screen,
            PasswordValue,
            PasswordRole,
            secret: true);

        RequireExactTriggers(
            screen,
            SubmitAction,
            (AccountRole, UiActionEvent.Submitted),
            (PasswordRole, UiActionEvent.Submitted),
            (EnterRole, UiActionEvent.Pressed));
        RequireExactTriggers(
            screen,
            CancelAction,
            (AccountRole, UiActionEvent.Cancelled),
            (PasswordRole, UiActionEvent.Cancelled));
        RequireExactTriggers(
            screen,
            ToggleOptionsAction,
            (OptionsRole, UiActionEvent.Toggled));
        RequireExactTriggers(
            screen,
            LocalSessionAction,
            (LocalRole, UiActionEvent.Pressed));
        RequireExactTriggers(
            screen,
            CreditsAction,
            (CreditsRole, UiActionEvent.Pressed));
        RequireExactTriggers(screen, QuitAction, (ExitRole, UiActionEvent.Pressed));

        return new LoginAccountProduct(screen, account, password);
    }

    private static UiValueBinding RequireTextValue(
        UiScreenDefinition screen,
        string valueId,
        string roleId,
        bool secret)
    {
        UiValueBinding value = screen.Values.SingleOrDefault(candidate => candidate.Id == valueId)
            ?? throw new InvalidDataException(
                $"Screen '{ScreenId}' has no value '{valueId}'");
        if (value.Role != roleId
            || value.Kind != UiValueKind.Text
            || value.Access != UiValueAccess.ReadWrite
            || value.Secret != secret)
        {
            throw new InvalidDataException(
                $"Screen '{ScreenId}' value '{valueId}' has an incompatible contract");
        }

        return value;
    }

    private static void RequireExactTriggers(
        UiScreenDefinition screen,
        string actionId,
        params (string Role, UiActionEvent Event)[] expected)
    {
        UiActionDefinition action = screen.Actions.Single(candidate => candidate.Id == actionId);
        var actual = action.Triggers
            .Select(trigger => (trigger.Role, trigger.Event))
            .ToHashSet();
        if (actual.Count != expected.Length || !actual.SetEquals(expected))
        {
            throw new InvalidDataException(
                $"Screen '{ScreenId}' action '{actionId}' has incompatible triggers");
        }
    }

    private static void RequireExactSet(
        IEnumerable<string> actual,
        IReadOnlyCollection<string> expected,
        string label)
    {
        string[] values = actual.ToArray();
        if (values.Length != expected.Count
            || !values.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
        {
            throw new InvalidDataException(
                $"Screen '{ScreenId}' has an incompatible {label} set");
        }
    }
}
