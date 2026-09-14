using ArkAscendedServerAdmin.Auth;

namespace ArkAscendedServerAdmin.UnitTests.Auth;

public class Pbkdf2PasswordHashTests
{
    // The minimum iteration count keeps the suite fast; the format and the checks are the same.
    private const int _fast = Pbkdf2PasswordHash.MinIterations;

    private static readonly string _validSalt = Convert.ToBase64String(new byte[Pbkdf2PasswordHash.SaltLength]);
    private static readonly string _validHash = Convert.ToBase64String(new byte[Pbkdf2PasswordHash.HashLength]);

    [Fact]
    public void Hash_RoundTripsThroughTheFormat()
    {
        var stored = Pbkdf2PasswordHash.Hash("hunter2", _fast);

        var fields = stored.Split('$');
        Assert.Equal(4, fields.Length);
        Assert.Equal("pbkdf2", fields[0]);
        Assert.Equal(_fast.ToString(System.Globalization.CultureInfo.InvariantCulture), fields[1]);
        Assert.Equal(Pbkdf2PasswordHash.SaltLength, Convert.FromBase64String(fields[2]).Length);
        Assert.Equal(Pbkdf2PasswordHash.HashLength, Convert.FromBase64String(fields[3]).Length);
        Assert.True(Pbkdf2PasswordHash.IsWellFormed(stored));
    }

    [Fact]
    public void Hash_UsesTheDefaultIterationsAndAFreshSalt()
    {
        var first = Pbkdf2PasswordHash.Hash("hunter2");
        var second = Pbkdf2PasswordHash.Hash("hunter2");

        Assert.StartsWith($"pbkdf2${Pbkdf2PasswordHash.Iterations}$", first, StringComparison.Ordinal);
        Assert.Equal(600_000, Pbkdf2PasswordHash.Iterations);
        Assert.NotEqual(first, second);
        Assert.True(Pbkdf2PasswordHash.Verify("hunter2", first));
        Assert.True(Pbkdf2PasswordHash.Verify("hunter2", second));
    }

    [Fact]
    public void Verify_AcceptsTheRightPasswordAndRefusesTheWrongOne()
    {
        var stored = Pbkdf2PasswordHash.Hash("hunter2", _fast);

        Assert.True(Pbkdf2PasswordHash.Verify("hunter2", stored));
        Assert.False(Pbkdf2PasswordHash.Verify("hunter3", stored));
        Assert.False(Pbkdf2PasswordHash.Verify("Hunter2", stored));
        Assert.False(Pbkdf2PasswordHash.Verify(" hunter2", stored));
        Assert.False(Pbkdf2PasswordHash.Verify(string.Empty, stored));
    }

    [Fact]
    public void Verify_IsIndependentOfTheSaltEncoding()
    {
        // A known vector: the same password against a hash string computed here, then re-parsed.
        var stored = Pbkdf2PasswordHash.Hash("pässwörd $ with spaces ", _fast);

        Assert.True(Pbkdf2PasswordHash.Verify("pässwörd $ with spaces ", stored));
        Assert.False(Pbkdf2PasswordHash.Verify("pässwörd $ with spaces", stored));
    }

    public static TheoryData<string, string?> MalformedShapes => new()
    {
        { "null", null },
        { "empty", string.Empty },
        { "bad prefix", $"pbkdf1$600000${_validSalt}${_validHash}" },
        { "upper-case prefix", $"PBKDF2$600000${_validSalt}${_validHash}" },
        { "three fields", $"pbkdf2$600000${_validSalt}" },
        { "five fields", $"pbkdf2$600000${_validSalt}${_validHash}$extra" },
        { "salt too short", $"pbkdf2$600000${Convert.ToBase64String(new byte[12])}${_validHash}" },
        { "salt too long", $"pbkdf2$600000${Convert.ToBase64String(new byte[24])}${_validHash}" },
        { "hash too short", $"pbkdf2$600000${_validSalt}${Convert.ToBase64String(new byte[20])}" },
        { "hash too long", $"pbkdf2$600000${_validSalt}${Convert.ToBase64String(new byte[48])}" },
        { "iterations below minimum", $"pbkdf2$9999${_validSalt}${_validHash}" },
        { "iterations above maximum", $"pbkdf2$5000001${_validSalt}${_validHash}" },
        { "iterations zero", $"pbkdf2$0${_validSalt}${_validHash}" },
        { "iterations not a number", $"pbkdf2$many${_validSalt}${_validHash}" },
        { "iterations signed", $"pbkdf2$+600000${_validSalt}${_validHash}" },
        { "iterations padded", $"pbkdf2$ 600000${_validSalt}${_validHash}" },
        { "iterations empty", $"pbkdf2$${_validSalt}${_validHash}" },
        { "salt not base64", $"pbkdf2$600000$!!!!!!!!!!!!!!!!!!!!!!!!${_validHash}" },
        { "hash not base64", $"pbkdf2$600000${_validSalt}$????????????????????????????????????????????" },
        { "salt with whitespace", $"pbkdf2$600000${_validSalt[..10]} {_validSalt[11..]}${_validHash}" },
        { "salt padding missing", $"pbkdf2$600000${_validSalt.TrimEnd('=')}${_validHash}" },
    };

    [Theory]
    [MemberData(nameof(MalformedShapes))]
    public void MalformedShapes_AreRefused(string shape, string? value)
    {
        Assert.False(Pbkdf2PasswordHash.IsWellFormed(value), shape);
        Assert.False(Pbkdf2PasswordHash.Verify("hunter2", value), shape);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(5_000_000)]
    public void IterationBounds_AreInclusive(int iterations)
    {
        var stored = $"pbkdf2${iterations}${_validSalt}${_validHash}";

        Assert.True(Pbkdf2PasswordHash.IsWellFormed(stored));
    }

    [Fact]
    public void Hash_RefusesIterationsOutsideTheBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Pbkdf2PasswordHash.Hash("x", Pbkdf2PasswordHash.MinIterations - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pbkdf2PasswordHash.Hash("x", Pbkdf2PasswordHash.MaxIterations + 1));
    }
}
