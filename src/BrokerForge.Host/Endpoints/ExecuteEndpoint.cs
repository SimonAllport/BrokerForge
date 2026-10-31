using System.Diagnostics;
using System.Text.RegularExpressions;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Mapping;
using BrokerForge.Descriptors.Planning;
using BrokerForge.Descriptors.Rendering;
using BrokerForge.Host.Errors;
using BrokerForge.Host.Execution;
using BrokerForge.Host.Ingress;
using BrokerForge.Host.Routing;
using BrokerForge.Keys;

namespace BrokerForge.Host.Endpoints;

/// <summary>
/// POST /v1/execute: one canonical request in, one canonical response (or error payload) out (HLD 4).
/// Every log line runs in a correlationId scope; bodies and secret values are never logged.
/// </summary>
public static partial class ExecuteEndpoint
{
    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex CorrelationIdFormat();

    public static void Map(WebApplication app) =>
        app.MapPost("/v1/execute", Handle).RequireAuthorization();

    private static async Task<IResult> Handle(
        CanonicalRequest request,
        HttpContext http,
        CallerGuard guard,
        RequestRouter router,
        ISecretResolver secrets,
        PageTokenKey tokenKey,
        IConfiguration configuration,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var log = loggers.CreateLogger("BrokerForge.Execute");
        var correlationId = request.CorrelationId is { } id && CorrelationIdFormat().IsMatch(id) ? id : null;
        if (correlationId == null)
        {
            var generated = Guid.NewGuid().ToString();
            return Results.Json(new ErrorPayload { CorrelationId = generated, Error = "BadRequest", Message = "The request needs a correlationId of letters, digits and hyphens." }, statusCode: 400);
        }

        using var scope = log.BeginScope(new Dictionary<string, object> { ["correlationId"] = correlationId });
        var watch = Stopwatch.StartNew();
        try
        {
            guard.Check(http.User, request);
            var (op, broker) = await router.ResolveAsync(request, ct);
            log.LogInformation("Executing {Message} on {Table} with {Operation} v{Version}", request.Message, request.Table, op.Descriptor.Operation, op.Descriptor.Version);

            var timeout = TimeSpan.FromSeconds(configuration.GetValue("Host:TimeoutSeconds", 90));
            var response = await Run(request, op, new OperationExecutor(broker), secrets, tokenKey, Deadline.After(timeout), ct);
            response.CorrelationId = correlationId;
            log.LogInformation("Completed {Message} on {Table} in {ElapsedMs} ms", request.Message, request.Table, watch.ElapsedMilliseconds);
            return Results.Json(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (status, payload) = ErrorTranslator.Translate(ex, correlationId);
            if (status >= 500 && payload.Error is "InternalError" or "ConfigurationError")
                log.LogError(ex, "Failed {Message} on {Table} after {ElapsedMs} ms with {Status}", request.Message, request.Table, watch.ElapsedMilliseconds, status);
            else
                log.LogWarning("Failed {Message} on {Table} after {ElapsedMs} ms with {Status} {Error}: {Reason}", request.Message, request.Table, watch.ElapsedMilliseconds, status, payload.Error, payload.Message);
            return Results.Json(payload, statusCode: status);
        }
    }

    private static async Task<CanonicalResponse> Run(CanonicalRequest request, OperationContext op, OperationExecutor executor, ISecretResolver secrets, PageTokenKey tokenKey, Deadline deadline, CancellationToken ct)
    {
        var d = op.Descriptor;
        var correlationId = op.Binding.CorrelationId;
        switch (request.Message)
        {
            case Messages.RetrieveMultiple:
                var plan = QueryPlanner.Plan(d, request.Query ?? new CanonicalQuery(), correlationId);
                var (rows, next) = await new PagingCoordinator(executor, secrets, tokenKey.Value).ReadPageAsync(op, plan, deadline, ct);
                return new CanonicalResponse { Rows = rows.Select(r => ToRow(op, r)).ToList(), Next = next };

            case Messages.Retrieve:
                var result = await Call(op, executor, secrets, deadline, ct);
                var row = ResponseMapper.MapRows(d, result, op.Keys.Kind, correlationId).Single();
                if (row.Key != op.Binding.Id)
                    throw new MappingException($"The source returned record {row.Key.Value} when asked for {op.Binding.Id!.Value} (operation {d.Operation}).", correlationId);
                return new CanonicalResponse { Record = new Row { Id = request.Id, Cells = row.Cells.ToList() } };

            case Messages.Create:
                var created = await Call(op, executor, secrets, deadline, ct);
                return new CanonicalResponse { Id = Encode(op, ResponseMapper.MapCreatedKey(d, created, op.Keys.Kind, correlationId)) };

            default:
                await Call(op, executor, secrets, deadline, ct);
                return new CanonicalResponse();
        }
    }

    private static async Task<BrokerResult> Call(OperationContext op, OperationExecutor executor, ISecretResolver secrets, Deadline deadline, CancellationToken ct)
    {
        var d = op.Descriptor;
        var request = await RequestRenderer.RenderAsync(d.Request, d, op.Binding, secrets, ct);
        var result = await executor.ExecuteAsync(d, request, op.Channel, deadline, op.Binding.CorrelationId, ct);
        ErrorMapper.ThrowIfError(d, result, op.Binding.CorrelationId, PagingCoordinator.SecretValues(request));
        return result;
    }

    private static Row ToRow(OperationContext op, MappedRow row) => new() { Id = Encode(op, row.Key), Cells = row.Cells.ToList() };

    private static string Encode(OperationContext op, ExternalKey key)
    {
        try
        {
            return op.Keys.Encode(key).ToString();
        }
        catch (KeyTooLongException ex)
        {
            throw new MappingException($"Source key {key.Value} does not fit the {op.Map.KeyStrategy} key strategy: {ex.Message} (table {op.Map.Table}).", op.Binding.CorrelationId, ex);
        }
    }
}
