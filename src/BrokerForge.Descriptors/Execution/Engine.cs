using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;
using BrokerForge.Keys;

namespace BrokerForge.Descriptors.Execution;

/// <summary>Everything the renderer needs to resolve bindings for one call.</summary>
public sealed record RenderContext(
    CanonicalRequest Request,
    ServiceInstance Instance,
    ExternalKey? Key,
    IReadOnlyCollection<string> ReservedHeaders);

/// <summary>Builds the source request from a descriptor (descriptor spec 3, 4, 7).</summary>
public interface IRequestRenderer
{
    Task<RenderedRequest> RenderAsync(OperationDescriptor descriptor, RenderContext context, CancellationToken ct);
}

/// <summary>Turns the source response into rows, a record, a new id or outputs (descriptor spec 6, 8).</summary>
public interface IResponseMapper
{
    CanonicalResponse Map(OperationDescriptor descriptor, BrokerResult result, MapContext context);
}

public sealed record MapContext(CanonicalRequest Request, IKeyStrategy Keys);

/// <summary>Phase 1: bindings, path encoding, query, headers, body templates, paging parameters.</summary>
public sealed class RequestRenderer : IRequestRenderer
{
    /// <remarks>The router runs <see cref="QueryCapabilityCheck"/> before authentication, so queries reaching here are declared.</remarks>
    public Task<RenderedRequest> RenderAsync(OperationDescriptor descriptor, RenderContext context, CancellationToken ct)
    {
        throw new NotImplementedException(
            "Phase 1: render path (pathParams, URL-encoded), query (omitIfNull, format), headers (reject reserved), " +
            "body template or raw, paging parameters. Secrets resolve through ISecretResolver and are never logged.");
    }
}

/// <summary>Phase 1: JSONPath extraction, type coercion, option sets, key encoding, next-page token, error mapping.</summary>
public sealed class ResponseMapper : IResponseMapper
{
    public CanonicalResponse Map(OperationDescriptor descriptor, BrokerResult result, MapContext context)
    {
        ErrorMap.ThrowIfError(descriptor, result);

        throw new NotImplementedException(
            "Phase 1: map rows/record/outputs with JSONPath (JSONata for transforms), coerce types, " +
            "encode keys with context.Keys, read the next-page token. Missing paths are MappingException, never empty results.");
    }
}

/// <summary>Applies the descriptor's errors block (descriptor spec 8). Anything unmapped is a SourceException.</summary>
public static class ErrorMap
{
    public static void ThrowIfError(OperationDescriptor descriptor, BrokerResult result)
    {
        if (result.StatusCode is >= 200 and < 300)
        {
            // Phase 1: errors.successBodyCheck for APIs that report errors inside a 200.
            return;
        }

        var status = result.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (descriptor.Errors is not null && descriptor.Errors.TryGetValue(status, out var mapping) && mapping.Exception != "Fail")
        {
            // Phase 1: read mapping.Message (JSONPath) from the body, redact, cap at 500 characters.
            var code = (ErrorCode)Enum.Parse(typeof(ErrorCode), mapping.Exception);
            throw new SourceMappedException(code, $"Source returned {status} for {descriptor.Operation}.");
        }

        throw new SourceException(result.StatusCode, $"Source returned unmapped status {status} for {descriptor.Operation}.");
    }
}
