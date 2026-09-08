using ArkAscendedServerAdmin.Auth;
using ArkAscendedServerAdmin.Commands;
using ArkAscendedServerAdmin.Processes;
using Microsoft.AspNetCore.Components;
using Radzen;

namespace ArkAscendedServerAdmin.Components.Shared;

/// <summary>
/// Base for interactive pages: runs facade calls with the session guard handled, and fires long
/// lifecycle operations (start, stop, backup, delete) off the render path with a toast at the end.
/// </summary>
public abstract class ArkPage : ComponentBase
{
    [Inject]
    protected NotificationService Notifications { get; set; } = default!;

    [Inject]
    protected NavigationManager Navigation { get; set; } = default!;

    [Inject]
    protected DialogService Dialogs { get; set; } = default!;

    protected bool Busy { get; private set; }

    /// <summary>Awaits a facade call; an expired session goes back to the login page, any other failure becomes a toast.</summary>
    protected async Task<bool> GuardedAsync(Func<Task> action)
    {
        Busy = true;
        try
        {
            return await Navigation.GuardedAsync(action, Notifications);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Runs a long operation without holding up the page; the outcome is reported when it completes.</summary>
    protected void Fire(Func<Task<OperationOutcome>> operation, string verb, string subject, Action? whenDone = null) =>
        _ = InvokeAsync(async () =>
        {
            try
            {
                var outcome = await operation();
                Notifications.Report(outcome, verb, subject);
            }
            catch (NotAuthorizedException)
            {
                Navigation.NavigateTo("login", forceLoad: true);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Notifications.Failed($"Could not {verb.ToLowerInvariant()} {subject}", ex.Message);
            }

            whenDone?.Invoke();
            StateHasChanged();
        });

    /// <summary>Same as <see cref="Fire"/> for a batch: one toast summarizing how many succeeded and which were refused.</summary>
    protected void FireMany(Func<Task<IReadOnlyList<BulkOutcome>>> operation, string verb, Action? whenDone = null) =>
        _ = InvokeAsync(async () =>
        {
            try
            {
                var outcomes = await operation();
                var refused = outcomes.Where(o => !o.Outcome.Succeeded).ToList();
                var done = outcomes.Count - refused.Count;
                if (refused.Count == 0)
                {
                    Notifications.Done($"{verb} {Presentation.Plural(done, "instance")}");
                }
                else
                {
                    Notifications.Warn(
                        $"{verb} {done} of {outcomes.Count}",
                        string.Join(" ", refused.Select(r => $"{r.Name}: {r.Outcome.Error}")));
                }
            }
            catch (NotAuthorizedException)
            {
                Navigation.NavigateTo("login", forceLoad: true);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Notifications.Failed($"Could not {verb.ToLowerInvariant()}", ex.Message);
            }

            whenDone?.Invoke();
            StateHasChanged();
        });

    protected async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false, string? icon = null)
    {
        var result = await Dialogs.OpenAsync<ArkAscendedServerAdmin.Components.Dialogs.ConfirmDialog>(
            title,
            new Dictionary<string, object?> { ["Message"] = message, ["ConfirmText"] = confirmText, ["Danger"] = danger, ["Icon"] = icon ?? (danger ? "delete" : "check") },
            new DialogOptions { Width = "440px" });
        return result is true;
    }
}
