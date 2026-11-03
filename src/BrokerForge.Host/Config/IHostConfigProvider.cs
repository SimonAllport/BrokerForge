using BrokerForge.Connections;

namespace BrokerForge.Host.Config;

/// <summary>
/// The configuration for each Dataverse environment the host serves: the file configuration plus that environment's
/// connection designs. <see cref="Files"/> is the file configuration alone, for what is not per environment.
/// </summary>
public interface IHostConfigProvider
{
    Task<HostConfigStore> ForEnvironmentAsync(string environmentId, CancellationToken ct);
    HostConfigStore Files { get; }
    void Invalidate(string environmentId);
}

/// <summary>Reads an environment's connection designs and their per-environment settings.</summary>
public interface IDesignSource
{
    Task<IReadOnlyList<(ConnectionDesign Design, EnvironmentSettings Settings)>> LoadAsync(string environmentId, CancellationToken ct);
}

/// <summary>
/// An <see cref="IDesignSource"/> could not read the environment's designs at all (as opposed to reading a design that
/// is wrong). The message is the full text a request gets; the provider may serve the last good store meanwhile.
/// </summary>
public sealed class DesignSourceUnavailableException(string message) : Exception(message);
