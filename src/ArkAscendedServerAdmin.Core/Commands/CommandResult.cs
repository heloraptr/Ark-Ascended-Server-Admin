namespace ArkAscendedServerAdmin.Commands;

/// <summary>
/// Outcome of a UI command that validates input: either it succeeded, or it carries the problems the
/// page shows next to the form. Rejections from the process manager and maintenance services keep
/// their own <see cref="Processes.OperationOutcome"/> shape.
/// </summary>
public sealed record CommandResult(bool Succeeded, IReadOnlyList<string> Errors)
{
    public static readonly CommandResult Ok = new(true, []);

    public static CommandResult Fail(params IReadOnlyList<string> errors) => new(false, errors);

    /// <summary>The problems joined into one sentence, or null when there are none.</summary>
    public string? Error => Errors.Count == 0 ? null : string.Join(" ", Errors);
}

/// <summary>A <see cref="CommandResult"/> that also carries a value on success.</summary>
public sealed record CommandResult<T>(bool Succeeded, T? Value, IReadOnlyList<string> Errors)
{
    public static CommandResult<T> Ok(T value) => new(true, value, []);

    public static CommandResult<T> Fail(params IReadOnlyList<string> errors) => new(false, default, errors);

    public string? Error => Errors.Count == 0 ? null : string.Join(" ", Errors);
}
