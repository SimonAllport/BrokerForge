using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;

namespace BrokerForge.Descriptors.Storage;

/// <summary>Approved descriptor versions. Dataverse-backed (<c>bf_operation</c>) in production; file-backed for local work and tests.</summary>
public interface IDescriptorStore
{
    /// <summary>Throws <see cref="BrokerConfigurationException"/> when the operation is unknown.</summary>
    Task<OperationDescriptor> GetAsync(string serviceInstance, string operation, CancellationToken ct);
}

/// <summary>
/// Loads <c>descriptors/&lt;ServiceInstance&gt;/*.yaml</c> once, parses and validates every file, and fails at load
/// if any is invalid, so a bad descriptor stops the host starting rather than failing a user's call.
/// </summary>
public sealed class FileDescriptorStore : IDescriptorStore
{
    private readonly IReadOnlyDictionary<(string Instance, string Operation), OperationDescriptor> _descriptors;

    public FileDescriptorStore(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new BrokerConfigurationException($"Descriptor folder '{root}' does not exist.");
        }

        var loaded = new Dictionary<(string, string), OperationDescriptor>();
        foreach (var file in Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var instance = new DirectoryInfo(Path.GetDirectoryName(file)!).Name;
            var descriptor = DescriptorParser.ParseFile(file);
            DescriptorValidator.Validate(descriptor).ThrowIfInvalid($"{instance}/{descriptor.Operation}");

            if (!loaded.TryAdd((instance, descriptor.Operation), descriptor))
            {
                throw new BrokerConfigurationException($"Operation '{descriptor.Operation}' is defined twice for {instance}.");
            }
        }

        _descriptors = loaded;
    }

    public Task<OperationDescriptor> GetAsync(string serviceInstance, string operation, CancellationToken ct) =>
        _descriptors.TryGetValue((serviceInstance, operation), out var d)
            ? Task.FromResult(d)
            : throw new BrokerConfigurationException($"No approved descriptor '{operation}' for service instance '{serviceInstance}'.");
}

/// <summary>Phase 1: read approved versions from <c>bf_operation</c> through the Web API, cached and refreshed on publish.</summary>
public sealed class DataverseDescriptorStore : IDescriptorStore
{
    public Task<OperationDescriptor> GetAsync(string serviceInstance, string operation, CancellationToken ct) =>
        throw new NotImplementedException("Phase 1: Dataverse-backed descriptor store (bf_operation, status = approved).");
}
