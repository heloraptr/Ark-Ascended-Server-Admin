using System.Security.Claims;
using ArkAscendedServerAdmin.Auth;

namespace ArkAscendedServerAdmin.UnitTests.Auth;

public class PasswordClaimValidatorTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly string _currentHash = PasswordHash.Compute("hunter2");

    [Fact]
    public void ValidPrincipal_IsAccepted()
    {
        var principal = Principal(_currentHash, _now.AddHours(1));

        Assert.True(PasswordClaimValidator.IsValid(principal, _currentHash, _now));
    }

    [Fact]
    public void StaleHash_IsRejected()
    {
        var principal = Principal(PasswordHash.Compute("old-password"), _now.AddHours(1));

        Assert.False(PasswordClaimValidator.IsValid(principal, _currentHash, _now));
    }

    [Fact]
    public void ExpiredCookie_IsRejected()
    {
        var principal = Principal(_currentHash, _now.AddSeconds(-1));

        Assert.False(PasswordClaimValidator.IsValid(principal, _currentHash, _now));
    }

    [Fact]
    public void MissingClaims_AreRejected()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner")], "Cookies");

        Assert.False(PasswordClaimValidator.IsValid(new ClaimsPrincipal(identity), _currentHash, _now));
    }

    [Fact]
    public void AnonymousPrincipal_IsRejected()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(PasswordClaimValidator.IsValid(principal, _currentHash, _now));
        Assert.False(PasswordClaimValidator.IsValid(null, _currentHash, _now));
    }

    [Fact]
    public void NoConfiguredPassword_RejectsEveryone()
    {
        var principal = Principal(_currentHash, _now.AddHours(1));

        Assert.False(PasswordClaimValidator.IsValid(principal, null, _now));
        Assert.False(PasswordClaimValidator.IsValid(principal, string.Empty, _now));
    }

    [Fact]
    public void PasswordHash_IsDeterministicLowerHexSha256()
    {
        Assert.Equal("f52fbd32b2b3b86ff88ef6c490628285f482af15ddcb29541f94bcf526a3f6c7", PasswordHash.Compute("hunter2"));
        Assert.True(PasswordHash.Matches(_currentHash, PasswordHash.Compute("hunter2")));
        Assert.False(PasswordHash.Matches(_currentHash, PasswordHash.Compute("hunter3")));
        Assert.False(PasswordHash.Matches(null, _currentHash));
    }

    private static ClaimsPrincipal Principal(string hash, DateTimeOffset expiresAt)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, ArkClaimTypes.OwnerName),
                new Claim(ArkClaimTypes.PasswordHash, hash),
                new Claim(ArkClaimTypes.ExpiresAt, PasswordClaimValidator.EncodeExpiry(expiresAt)),
            ],
            "Cookies");
        return new ClaimsPrincipal(identity);
    }
}
