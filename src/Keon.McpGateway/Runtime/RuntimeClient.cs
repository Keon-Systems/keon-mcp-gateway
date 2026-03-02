using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Keon.McpGateway.Contracts;
using Microsoft.Extensions.Options;

namespace Keon.McpGateway.Runtime;

public sealed class RuntimeOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public int TimeoutSeconds { get; set; } = 5;
    public int MaxRetries { get; set; } = 2;
}

public sealed class RuntimeClientException : Exception
{
    public RuntimeClientException(string message, int statusCode, bool retryable)
        : base(message)
    {
        StatusCode = statusCode;
        Retryable = retryable;
    }

    public int StatusCode { get; }
    public bool Retryable { get; }
}

public sealed record RuntimeDecideRequest(string TenantId, string ActorId, string CorrelationId, string Action, string ResourceType, string? ResourceId, string? ResourceScope, string? Purpose, JsonObject Parameters);

public sealed record RuntimeExecuteRequest(string TenantId, string ActorId, string CorrelationId, string? IdempotencyKey, string DecisionReceiptId, string Action, string ResourceType, string? ResourceId, JsonObject Parameters);

public sealed class RuntimeClient
{
    private readonly HttpClient _httpClient;
    private readonly RuntimeOptions _options;

    public RuntimeClient(HttpClient httpClient, IOptions<RuntimeOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public async Task<JsonObject> GetStatusAsync(CancellationToken ct)
    {
        var response = await _httpClient.GetAsync("runtime/v1/status", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)) ?? [];
    }

    public async Task<RuntimeDecisionRecord> DecideAsync(RuntimeDecideRequest request, CancellationToken ct)
    {
        var response = await SendWithRetryAsync(
            () => _httpClient.PostAsJsonAsync("runtime/v1/decide", new
            {
                tenantId = request.TenantId,
                actorId = request.ActorId,
                correlationId = request.CorrelationId,
                action = request.Action,
                resourceType = request.ResourceType,
                resourceId = request.ResourceId,
                resourceScope = request.ResourceScope,
                purpose = request.Purpose,
                parameters = request.Parameters
            }, cancellationToken: ct),
            canRetry: true,
            ct);

        var payload = await response.Content.ReadFromJsonAsync<RuntimeEnvelope>(cancellationToken: ct)
            ?? throw new RuntimeClientException("Runtime decide response was empty.", StatusCodes.Status502BadGateway, true);

        if (!response.IsSuccessStatusCode || payload.Data is null)
        {
            throw CreateRuntimeException("decide", response.StatusCode, payload.Error?.Message ?? "Runtime decide failed.");
        }

        var policyHash = payload.Data["policyHash"]?.GetValue<string>() ?? payload.Data["policy_hash"]?.GetValue<string>();
        return new RuntimeDecisionRecord(
            payload.Data["receiptId"]?.GetValue<string>() ?? throw new RuntimeClientException("Runtime decision receiptId missing.", StatusCodes.Status502BadGateway, false),
            NormalizeDecision(payload.Data["decision"]?.GetValue<string>()),
            policyHash ?? string.Empty,
            payload.Data["policyId"]?.GetValue<string>() ?? payload.Data["policy_id"]?.GetValue<string>(),
            payload.Data["policyVersion"]?.GetValue<string>() ?? payload.Data["policy_version"]?.GetValue<string>(),
            payload.Data["reasonCode"]?.GetValue<string>() ?? payload.Data["reason_code"]?.GetValue<string>());
    }

    public async Task<RuntimeExecutionRecord> ExecuteAsync(RuntimeExecuteRequest request, CancellationToken ct)
    {
        var canRetry = !string.IsNullOrWhiteSpace(request.IdempotencyKey);
        var response = await SendWithRetryAsync(
            () => _httpClient.PostAsJsonAsync("runtime/v1/execute", new
            {
                tenantId = request.TenantId,
                actorId = request.ActorId,
                correlationId = request.CorrelationId,
                idempotencyKey = request.IdempotencyKey,
                action = request.Action,
                resourceType = request.ResourceType,
                resourceId = request.ResourceId,
                parameters = request.Parameters,
                receipt = new
                {
                    receiptId = request.DecisionReceiptId,
                    decision = "allow",
                    correlationId = request.CorrelationId
                }
            }, cancellationToken: ct),
            canRetry,
            ct);

        var payload = await response.Content.ReadFromJsonAsync<RuntimeEnvelope>(cancellationToken: ct)
            ?? throw new RuntimeClientException("Runtime execute response was empty.", StatusCodes.Status502BadGateway, canRetry);

        if (!response.IsSuccessStatusCode || payload.Data is null)
        {
            throw CreateRuntimeException("execute", response.StatusCode, payload.Error?.Message ?? "Runtime execute failed.");
        }

        return new RuntimeExecutionRecord(
            payload.Data["executionReceiptId"]?.GetValue<string>()
                ?? payload.Data["receiptId"]?.GetValue<string>()
                ?? IdFactory.NewReceiptId("rcpt_exe"),
            payload.Data["result"]?.AsObject() ?? payload.Data.DeepClone().AsObject());
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<Task<HttpResponseMessage>> action, bool canRetry, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await action();
                if ((int)response.StatusCode < 500 || !canRetry || attempt > _options.MaxRetries)
                {
                    return response;
                }
            }
            catch (Exception ex) when (canRetry && attempt <= _options.MaxRetries && ex is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct);
        }
    }

    private static RuntimeClientException CreateRuntimeException(string operation, HttpStatusCode statusCode, string message)
        => new($"Runtime {operation} failed: {message}", statusCode == HttpStatusCode.GatewayTimeout ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway, (int)statusCode >= 500);

    private static string NormalizeDecision(string? decision)
        => string.Equals(decision, "allow", StringComparison.OrdinalIgnoreCase) ? "approved" : "denied";

    private sealed record RuntimeEnvelope(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] JsonObject? Data,
        [property: JsonPropertyName("error")] RuntimeError? Error);

    private sealed record RuntimeError(
        [property: JsonPropertyName("message")] string Message);
}
