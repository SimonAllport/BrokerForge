using BrokerForge.Connections;

namespace BrokerForge.Host.Generation;

/// <summary>
/// One Generate for one connection in one environment. <see cref="BaseUrl"/> and <see cref="AuthJson"/> are this
/// environment's own values for the design's variables; null leaves a value as it is.
/// </summary>
public sealed record GenerationRequest(string EnvironmentId, ConnectionDesign Design, string Solution, string Publisher, string? BaseUrl, string? AuthJson,
    string? ConfirmedFingerprint, string CorrelationId);

public enum JobState { Queued, Running, Succeeded, Failed }

/// <summary>
/// A generation job as reported. <see cref="Writes"/> are the writes made; <see cref="Failure"/> says why a failed job
/// stopped. <see cref="NeedsConfirmation"/> marks a job that stopped before writing because its changes need confirming
/// (none confirmed, or not the ones confirmed): start it again with the fingerprint the failure names.
/// </summary>
public sealed record GenerationJob(Guid Id, string EnvironmentId, string Instance, JobState State, IReadOnlyList<string> Writes, int? DesignVersion,
    string? Failure, DateTimeOffset Started, DateTimeOffset? Finished, string CorrelationId, bool NeedsConfirmation = false);

/// <summary>A job for the same environment and instance is still queued or running.</summary>
public sealed class GenerationBusyException(string message) : Exception(message);
