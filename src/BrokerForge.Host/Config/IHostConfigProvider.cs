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
    /// <summary>
    /// The designs that could be read, and one failure text per design (or unrelated resource) that could not. Throws
    /// only when the environment cannot be read at all: <see cref="DesignSourceUnavailableException"/> when that may
    /// pass by itself, <see cref="BrokerForge.Abstractions.ConfigurationException"/> otherwise.
    /// </summary>
    Task<DesignLoad> LoadAsync(string environmentId, CancellationToken ct);
}

/// <summary>What <see cref="IDesignSource.LoadAsync"/> read: the usable designs, and why each other one was left out.</summary>
public sealed record DesignLoad(IReadOnlyList<(ConnectionDesign Design, EnvironmentSettings Settings)> Designs, IReadOnlyList<string> Failures)
{
    public DesignLoad(IEnumerable<(ConnectionDesign Design, EnvironmentSettings Settings)> designs) : this(designs.ToList(), Array.Empty<string>()) { }
}

/// <summary>
/// An <see cref="IDesignSource"/> could not read the environment's designs at all (as opposed to reading a design that
/// is wrong). The message is the full text a request gets; the provider may serve the last good store meanwhile.
/// </summary>
public sealed class DesignSourceUnavailableException(string message) : Exception(message);
