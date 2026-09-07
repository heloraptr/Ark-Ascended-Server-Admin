namespace ArkAscendedServerAdmin.Launch;

/// <summary>
/// The ordered argument list for one launch, intended for <c>ProcessStartInfo.ArgumentList</c> (no
/// shell, no quoting). <see cref="ToDisplayString"/> is for the console header and the instance summary
/// page only.
/// </summary>
public sealed record LaunchArguments(IReadOnlyList<string> Arguments)
{
    /// <summary>Joins the arguments with spaces, wrapping any argument that contains whitespace in double quotes.</summary>
    public string ToDisplayString() =>
        string.Join(' ', Arguments.Select(argument => argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument));
}
