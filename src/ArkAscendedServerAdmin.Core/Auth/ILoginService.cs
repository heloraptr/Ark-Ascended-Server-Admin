namespace ArkAscendedServerAdmin.Auth;

/// <summary>Outcome of a login attempt, for the login page to render.</summary>
public sealed record LoginResult(LoginOutcome Outcome, DateTimeOffset? LockedOutUntil = null)
{
    public static readonly LoginResult Success = new(LoginOutcome.Success);
    public static readonly LoginResult InvalidPassword = new(LoginOutcome.InvalidPassword);
    public static readonly LoginResult NotConfigured = new(LoginOutcome.NotConfigured);
    public static readonly LoginResult Busy = new(LoginOutcome.Busy);

    public static LoginResult LockedOut(DateTimeOffset until) => new(LoginOutcome.LockedOut, until);

    public bool Succeeded => Outcome == LoginOutcome.Success;

    public string Message => Outcome switch
    {
        LoginOutcome.Success => "Signed in.",
        LoginOutcome.InvalidPassword => "Incorrect password.",
        LoginOutcome.LockedOut => "Too many failed attempts. Try again in a few minutes.",
        LoginOutcome.NotConfigured => "No usable login password is configured. Set ArkAdmin:PasswordHash (or ArkAdmin:Password) in appsettings.json and restart the service.",
        LoginOutcome.Busy => "The server is busy. Try again in a moment.",
        _ => "Login failed.",
    };
}

public enum LoginOutcome
{
    Success,
    InvalidPassword,
    LockedOut,
    NotConfigured,

    /// <summary>Too many logins are already being verified; not counted as a failure.</summary>
    Busy,
}

/// <summary>
/// Verifies the single password and establishes the cookie session. Only usable during a static
/// server-side render (the login form) because signing in needs the HTTP response.
/// </summary>
public interface ILoginService
{
    Task<LoginResult> LoginAsync(string password, CancellationToken cancellationToken = default);
}
