using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Processes;
using Microsoft.AspNetCore.Components;
using Radzen;

namespace ArkAscendedServerAdmin.Components.Shared;

/// <summary>
/// Toast helpers over Radzen's <see cref="NotificationService"/> so every page reports the same way:
/// the verb that was clicked, then what happened. A <see cref="NotAuthorizedException"/> from a facade
/// sends the browser back to the login page (the layout tears the circuit down).
/// </summary>
public static class Feedback
{
    public static void Done(this NotificationService notifications, string summary, string? detail = null) =>
        Show(notifications, NotificationSeverity.Success, summary, detail);

    public static void Info(this NotificationService notifications, string summary, string? detail = null) =>
        Show(notifications, NotificationSeverity.Info, summary, detail);

    public static void Warn(this NotificationService notifications, string summary, string? detail = null) =>
        Show(notifications, NotificationSeverity.Warning, summary, detail, 9000);

    public static void Failed(this NotificationService notifications, string summary, string? detail = null) =>
        Show(notifications, NotificationSeverity.Error, summary, detail, 12000);

    /// <summary>Reports an <see cref="OperationOutcome"/>: the verb on success, the reason on rejection.</summary>
    public static void Report(this NotificationService notifications, OperationOutcome outcome, string verb, string subject)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Succeeded)
        {
            notifications.Done($"{verb} {subject}");
        }
        else
        {
            notifications.Failed($"Could not {verb.ToLowerInvariant()} {subject}", outcome.Error);
        }
    }

    public static void Report(this NotificationService notifications, CommandResult result, string verb, string subject)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Succeeded)
        {
            notifications.Done($"{verb} {subject}");
        }
        else
        {
            notifications.Failed($"Could not {verb.ToLowerInvariant()} {subject}", result.Error);
        }
    }

    /// <summary>Runs a facade call; on an expired session sends the browser to the login page instead of surfacing the exception.</summary>
    public static async Task<bool> GuardedAsync(this NavigationManager navigation, Func<Task> action, NotificationService notifications)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action();
            return true;
        }
        catch (NotAuthorizedException)
        {
            navigation.NavigateTo("login", forceLoad: true);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notifications.Failed("Something went wrong", ex.Message);
            return false;
        }
    }

    private static void Show(NotificationService notifications, NotificationSeverity severity, string summary, string? detail, double duration = 5000)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        notifications.Notify(new NotificationMessage
        {
            Severity = severity,
            Summary = summary,
            Detail = detail ?? string.Empty,
            Duration = duration,
            CloseOnClick = true,
        });
    }
}
