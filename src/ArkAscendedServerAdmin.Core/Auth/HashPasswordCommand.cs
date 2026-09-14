using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ArkAscendedServerAdmin.Auth;

/// <summary>
/// <c>--hash-password</c> (release plan step A1): reads standard input as UTF-8 to end of stream, strips
/// exactly one trailing line terminator (<c>\r\n</c> or <c>\n</c>) and nothing else, and prints the
/// <see cref="Pbkdf2PasswordHash"/> string. Leading and trailing spaces are part of the password. An
/// empty password, a second line break, or undecodable input exits non-zero with nothing on stdout.
/// </summary>
public static class HashPasswordCommand
{
    public const string Argument = "--hash-password";

    /// <summary>Runs the command over the given streams and returns the process exit code.</summary>
    public static int Run(Stream standardInput, TextWriter standardOutput, TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        string input;
        try
        {
            // No BOM detection and no lenient decoding: the bytes are the password, or the input is refused.
            using var reader = new StreamReader(
                standardInput,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false);
            input = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is DecoderFallbackException or IOException)
        {
            standardError.WriteLine($"Could not read the password from standard input: {ex.Message}");
            return 1;
        }

        if (!TryExtractPassword(input, out var password, out var problem))
        {
            standardError.WriteLine(problem);
            return 1;
        }

        standardOutput.WriteLine(Pbkdf2PasswordHash.Hash(password));
        return 0;
    }

    /// <summary>Applies the stripping and refusal rules to the raw input text.</summary>
    public static bool TryExtractPassword(string input, [NotNullWhen(true)] out string? password, [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(input);
        password = null;
        problem = null;

        var text = input.EndsWith("\r\n", StringComparison.Ordinal) ? input[..^2]
            : input.EndsWith('\n') ? input[..^1]
            : input;

        if (text.Length == 0)
        {
            problem = "The password is empty.";
            return false;
        }

        if (text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal))
        {
            problem = "The password must be a single line.";
            return false;
        }

        password = text;
        return true;
    }
}
