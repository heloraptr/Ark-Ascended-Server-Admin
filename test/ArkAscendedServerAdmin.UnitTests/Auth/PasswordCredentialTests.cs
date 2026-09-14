using ArkAscendedServerAdmin.Auth;

namespace ArkAscendedServerAdmin.UnitTests.Auth;

public class PasswordCredentialTests
{
    private static readonly string _storedHash = Pbkdf2PasswordHash.Hash("hashed-secret", Pbkdf2PasswordHash.MinIterations);

    [Fact]
    public void NothingConfigured_IsNoneAndRefusesEverything()
    {
        var credential = PasswordCredential.Resolve(null, null);

        Assert.Equal(PasswordCredentialKind.None, credential.Kind);
        Assert.False(credential.IsUsable);
        Assert.Null(credential.Snapshot);
        Assert.Null(credential.Verify("anything"));
        Assert.Same(PasswordCredential.None, PasswordCredential.Resolve(string.Empty, string.Empty));
    }

    [Fact]
    public void PlaintextPassword_KeepsTheSha256Claim()
    {
        var credential = PasswordCredential.Resolve("hunter2", null);

        Assert.Equal(PasswordCredentialKind.Plaintext, credential.Kind);
        Assert.True(credential.IsUsable);
        Assert.Equal(PasswordHash.Compute("hunter2"), credential.Snapshot);
        Assert.Equal(credential.Snapshot, credential.Verify("hunter2"));
        Assert.Null(credential.Verify("hunter3"));
    }

    [Fact]
    public void PasswordHash_VerifiesAndReturnsTheStoredStringAsTheSnapshot()
    {
        var credential = PasswordCredential.Resolve(null, _storedHash);

        Assert.Equal(PasswordCredentialKind.Pbkdf2, credential.Kind);
        Assert.True(credential.IsUsable);
        Assert.Equal(_storedHash, credential.Snapshot);
        Assert.Equal(_storedHash, credential.Verify("hashed-secret"));
        Assert.Null(credential.Verify("hunter2"));
        Assert.False(credential.BothConfigured);
    }

    [Fact]
    public void BothKeysSet_PrefersTheHash()
    {
        var credential = PasswordCredential.Resolve("hunter2", _storedHash);

        Assert.Equal(PasswordCredentialKind.Pbkdf2, credential.Kind);
        Assert.True(credential.BothConfigured);
        Assert.Equal(_storedHash, credential.Verify("hashed-secret"));
        Assert.Null(credential.Verify("hunter2"));
    }

    [Fact]
    public void MalformedHash_RefusesEveryLoginWithNoFallbackToPassword()
    {
        var credential = PasswordCredential.Resolve("hunter2", "pbkdf2$600000$short$short");

        Assert.Equal(PasswordCredentialKind.Malformed, credential.Kind);
        Assert.False(credential.IsUsable);
        Assert.Null(credential.Snapshot);
        Assert.True(credential.BothConfigured);
        Assert.Null(credential.Verify("hunter2"));
        Assert.Null(credential.Verify("hashed-secret"));
    }

    [Fact]
    public void Snapshot_IsWhatTheClaimValidatorCompares()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var credential = PasswordCredential.Resolve(null, _storedHash);
        var claim = credential.Verify("hashed-secret")!;
        var identity = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(ArkClaimTypes.PasswordHash, claim),
                new System.Security.Claims.Claim(ArkClaimTypes.ExpiresAt, PasswordClaimValidator.EncodeExpiry(now.AddHours(1))),
            ],
            "Cookies");

        Assert.True(PasswordClaimValidator.IsValid(new System.Security.Claims.ClaimsPrincipal(identity), credential.Snapshot, now));
        Assert.False(PasswordClaimValidator.IsValid(new System.Security.Claims.ClaimsPrincipal(identity), PasswordCredential.Resolve(null, null).Snapshot, now));
    }
}
