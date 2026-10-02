namespace ArkAscendedServerAdmin.Startup;

public static class ReadinessMonitorExtensions
{
    /// <summary>
    /// Completes when the readiness pipeline reports Ready, at once when it already does. The orchestrator
    /// migrates the database on its own schedule after the host starts, so a background service that touched
    /// the database at once could find the tables missing. A pipeline that never reaches Ready (a failed
    /// startup) keeps the caller waiting until <paramref name="cancellationToken"/> is canceled.
    /// </summary>
    public static async Task WaitUntilReadyAsync(this IReadinessMonitor readiness, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        if (readiness.Current.IsReady)
        {
            return;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(ReadinessState state)
        {
            if (state.IsReady)
            {
                ready.TrySetResult();
            }
        }

        readiness.Changed += OnChanged;
        try
        {
            // Ready may have been reported between the first check and the subscription.
            if (readiness.Current.IsReady)
            {
                return;
            }

            await ready.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            readiness.Changed -= OnChanged;
        }
    }
}
