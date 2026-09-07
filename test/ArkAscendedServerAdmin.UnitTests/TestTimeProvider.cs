namespace ArkAscendedServerAdmin.UnitTests;

/// <summary>A manually advanced clock for throttle and expiry tests.</summary>
public sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
