using System.Runtime.Serialization;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Storage;

namespace BrokerForge.Host;

public static class Endpoints
{
    public static WebApplication MapBrokerForgeEndpoints(this WebApplication app)
    {
        // Load and validate every descriptor at start-up: a bad descriptor stops the host, not a user's call.
        app.Services.GetRequiredService<IDescriptorStore>();

        app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        app.MapPost("/v1/execute", ExecuteAsync);

        app.MapGet("/connect/{serviceInstance}", (string serviceInstance) =>
            Results.Problem($"The connect page for '{serviceInstance}' arrives in phase 3.", statusCode: StatusCodes.Status501NotImplemented));

        return app;
    }

    /// <summary>
    /// 401: trust failed. 400: body is not a canonical request. 200: a canonical response, which may carry a typed error.
    /// 500: an unexpected host failure; the plug-in raises BrokerUnavailableException.
    /// </summary>
    private static async Task<IResult> ExecuteAsync(HttpContext http, IRequestTrust trust, BrokerRouter router, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger("BrokerForge.Execute");

        var decision = await trust.ValidateAsync(http.Request, ct);
        if (!decision.Allowed)
        {
            log.LogWarning("Rejected call: {Reason}", decision.Reason);
            return Results.Problem(decision.Reason, statusCode: StatusCodes.Status401Unauthorized);
        }

        CanonicalRequest request;
        try
        {
            using var reader = new StreamReader(http.Request.Body);
            request = CanonicalJson.Deserialize<CanonicalRequest>(await reader.ReadToEndAsync(ct));
            _ = request.Message;
        }
        catch (Exception ex) when (ex is SerializationException or ArgumentException)
        {
            return Results.Problem("Body is not a canonical request.", statusCode: StatusCodes.Status400BadRequest);
        }

        using var scope = log.BeginScope(new Dictionary<string, object> { ["correlationId"] = request.CorrelationId });

        CanonicalResponse response;
        try
        {
            response = await router.ExecuteAsync(request, decision, ct);
            log.LogInformation("{Message} on {ServiceInstance}/{Table} succeeded", request.MessageName, request.ServiceInstance, request.Table ?? request.CustomApi);
        }
        catch (BrokerForgeException ex)
        {
            ex.CorrelationId ??= request.CorrelationId;
            log.LogWarning("{Message} on {ServiceInstance} failed: {Code} {Error}", request.MessageName, request.ServiceInstance, ex.Code, ex.Message);
            response = CanonicalResponse.Failure(request.CorrelationId, ex.ToError());
        }
        catch (NotImplementedException ex)
        {
            // Stubbed paths fail loudly with their phase, not as a generic 500.
            log.LogWarning("{Message} on {ServiceInstance} reached a stub: {Stub}", request.MessageName, request.ServiceInstance, ex.Message);
            response = CanonicalResponse.Failure(request.CorrelationId, new BrokerError
            {
                Code = ErrorCode.Configuration,
                Message = $"Not implemented yet: {ex.Message}",
                CorrelationId = request.CorrelationId,
            });
        }

        return Results.Text(CanonicalJson.Serialize(response), "application/json");
    }
}
