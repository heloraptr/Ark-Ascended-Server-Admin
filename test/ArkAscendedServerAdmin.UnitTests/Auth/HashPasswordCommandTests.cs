using System.Text;
using ArkAscendedServerAdmin.Auth;

namespace ArkAscendedServerAdmin.UnitTests.Auth;

public class HashPasswordCommandTests
{
    [Fact]
    public void RoundTrip_NonAsciiSpacesAndDollarSign()
    {
        // Leading and trailing spaces, a `$` (the format's own separator), and characters outside ASCII
        // all survive stdin, hashing, and verification. Only the one trailing "\n" is stripped.
        const string password = "  pässwörd $ ünïcödé 密码  ";
        var (exitCode, stdout, stderr) = Run(password + "\n");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        var hash = stdout.TrimEnd('\r', '\n');
        Assert.DoesNotContain('\n', hash);
        Assert.StartsWith("pbkdf2$", hash, StringComparison.Ordinal);
        Assert.True(Pbkdf2PasswordHash.IsWellFormed(hash));
        Assert.True(Pbkdf2PasswordHash.Verify(password, hash));
        Assert.False(Pbkdf2PasswordHash.Verify(password.Trim(), hash));
    }

    [Theory]
    [InlineData("secret\r\n", "secret")]
    [InlineData("secret\n", "secret")]
    [InlineData("secret", "secret")]
    [InlineData(" secret ", " secret ")]
    [InlineData(" secret \n", " secret ")]
    [InlineData("a b\tc\n", "a b\tc")]
    public void TryExtractPassword_StripsExactlyOneTrailingLineTerminator(string input, string expected)
    {
        Assert.True(HashPasswordCommand.TryExtractPassword(input, out var password, out var problem));
        Assert.Equal(expected, password);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\n\n")]
    [InlineData("secret\n\n")]
    [InlineData("secret\r\n\r\n")]
    [InlineData("two\nlines\n")]
    [InlineData("two\rlines\n")]
    [InlineData("secret\r")]
    public void TryExtractPassword_RefusesEmptyAndMultiLineInput(string input)
    {
        Assert.False(HashPasswordCommand.TryExtractPassword(input, out var password, out var problem));
        Assert.Null(password);
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("first\nsecond\n")]
    public void Run_RefusalsExitNonZeroWithNothingOnStdout(string input)
    {
        var (exitCode, stdout, stderr) = Run(input);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.NotEqual(string.Empty, stderr);
    }

    [Fact]
    public void Run_RefusesInvalidUtf8()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var input = new MemoryStream([0x73, 0xFF, 0xFE, 0x0A]);

        var exitCode = HashPasswordCommand.Run(input, stdout, stderr);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotEqual(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Run_TreatsAByteOrderMarkAsPartOfThePassword()
    {
        var (exitCode, stdout, _) = Run("﻿secret\n");

        Assert.Equal(0, exitCode);
        Assert.True(Pbkdf2PasswordHash.Verify("﻿secret", stdout.TrimEnd('\r', '\n')));
        Assert.False(Pbkdf2PasswordHash.Verify("secret", stdout.TrimEnd('\r', '\n')));
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(string input)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var stream = new MemoryStream(new UTF8Encoding(false).GetBytes(input));

        var exitCode = HashPasswordCommand.Run(stream, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }
}
