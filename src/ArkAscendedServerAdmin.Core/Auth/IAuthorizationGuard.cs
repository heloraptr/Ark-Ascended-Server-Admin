namespace ArkAscendedServerAdmin.Auth;

/// <summary>
/// Server-side authorization check performed by every command facade before any work is enqueued.
/// Visibility (<c>AuthorizeView</c>) is not authorization: a stale event handler firing around cookie or
/// circuit invalidation must be refused here.
/// </summary>
public interface IAuthorizationGuard
{
    /// <summary>Throws <see cref="NotAuthorizedException"/> unless the current caller holds a valid session.</summary>
    ValueTask EnsureAuthorizedAsync(CancellationToken cancellationToken = default);
}

public sealed class NotAuthorizedException : Exception
{
    public NotAuthorizedException()
        : base("The session is no longer valid. Sign in again.")
    {
    }

    public NotAuthorizedException(string message)
        : base(message)
    {
    }

    public NotAuthorizedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
