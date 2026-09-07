namespace ArkAscendedServerAdmin.Ports;

/// <summary>
/// One reason a candidate instance may not use <see cref="Port"/>. <see cref="Reason"/> is a
/// human-readable sentence naming the other owner, the web UI, or the OS listener (plan step 15).
/// </summary>
public sealed record PortConflict(int Port, string Reason);
