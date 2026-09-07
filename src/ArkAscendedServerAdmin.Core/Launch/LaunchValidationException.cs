namespace ArkAscendedServerAdmin.Launch;

/// <summary>
/// Thrown by <see cref="LaunchArgumentBuilder.Build"/> when the request cannot produce a safe command
/// line. <see cref="Problems"/> lists every problem found (each names the offending value) so the caller
/// can show them all at once.
/// </summary>
public sealed class LaunchValidationException(IReadOnlyList<string> problems)
    : Exception(BuildMessage(problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;

    private static string BuildMessage(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return problems.Count == 0
            ? "The launch request is invalid."
            : "The launch request is invalid: " + string.Join(' ', problems);
    }
}
