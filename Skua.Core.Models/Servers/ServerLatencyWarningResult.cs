namespace Skua.Core.Models.Servers;

/// <summary>
/// Captures the user's decision in a high-latency server warning.
/// </summary>
public sealed record ServerLatencyWarningResult(bool Accepted, bool DontShowAgain);
