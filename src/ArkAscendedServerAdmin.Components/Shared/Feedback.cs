using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Processes;
using Microsoft.AspNetCore.Components;
using Radzen;

namespace ArkAscendedServerAdmin.Components.Shared;

/// <summary>
/// The two forms a toast needs for one action: past tense for the success line ("Started Alpha") and
/// the plain form for the failure ("Could not start Alpha"). The failure line used to lower-case the
/// past tense, which read "Could not started Alpha"; English is too irregular to derive one from the
/// other, so both are named here once and the call sites pick a member.
/// </summary>
/// <param name="Past">Past tense, used as <c>{Past} {subject}</c> when the operation succeeded.</param>
/// <param name="Plain">Plain form, used as <c>Could not {Plain} {subject}</c> when it did not.</param>
public sealed record ActionVerb(string Past, string Plain)
{
    public static ActionVerb Started { get; } = new("Started", "start");
    public static ActionVerb Stopped { get; } = new("Stopped", "stop");
    public static ActionVerb Restarted { get; } = new("Restarted", "restart");
    public static ActionVerb Deleted { get; } = new("Deleted", "delete");
    public static ActionVerb Saved { get; } = new("Saved", "save");
    public static ActionVerb Removed { get; } = new("Removed", "remove");
    public static ActionVerb Restored { get; } = new("Restored", "restore");
    public static ActionVerb Recovered { get; } = new("Recovered", "recover");
    public static ActionVerb Discarded { get; } = new("Discarded", "discard");
    public static ActionVerb Resumed { get; } = new("Resumed", "resume");
    public static ActionVerb Skipped { get; } = new("Skipped", "skip");
    public static ActionVerb Requeued { get; } = new("Re-queued", "re-queue");
    public static ActionVerb AddedTo { get; } = new("Added to", "add to");
    public static ActionVerb SavedIdentityOf { get; } = new("Saved identity of", "save identity of");
}

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
    public static void Report(this NotificationService notifications, OperationOutcome outcome, ActionVerb verb, string subject)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(verb);
        if (outcome.Succeeded)
        {
            notifications.Done($"{verb.Past} {subject}");
        }
        else
        {
            notifications.Failed($"Could not {verb.Plain} {subject}", outcome.Error);
        }
    }

    public static void Report(this NotificationService notifications, CommandResult result, ActionVerb verb, string subject)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(verb);
        if (result.Succeeded)
        {
            notifications.Done($"{verb.Past} {subject}");
        }
        else
        {
            notifications.Failed($"Could not {verb.Plain} {subject}", result.Error);
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
