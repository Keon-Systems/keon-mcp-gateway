using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Keon.McpGateway.ControlPlane;

public sealed class ControlPlaneOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public int TimeoutSeconds { get; set; } = 3;
}

public sealed record GatewayApiKeySnapshot(
    int Version,
    string ApiKeyId,
    string TenantId,
    string ProjectId,
    string EnvironmentId,
    string Prefix,
    string SecretSalt,
    string SecretHash,
    string Mode,
    string Status);

public sealed record TenantEntitlementSnapshot(
    int Version,
    string TenantId,
    string PlanCode,
    string BillingState,
    int MonthlyExecutionLimit,
    int BurstRpsLimit,
    int ProjectsMax,
    int ApiKeysMax,
    int ReceiptRetentionDays,
    int CurrentPeriodConsumed,
    int ConservativeRemaining);

public sealed record GatewayEnvironmentState(
    int Version,
    string EnvironmentId,
    string TenantId,
    string ProjectId,
    string Status);

public sealed record UsageEventRequest(
    string TenantId,
    string ProjectId,
    string EnvironmentId,
    string? ApiKeyId,
    string ExecutionId,
    string? ReceiptId,
    string? IdempotencyKey,
    string Endpoint,
    string Decision,
    int BillableUnits,
    bool Billable,
    DateTimeOffset OccurredAtUtc);

public sealed class ControlPlaneClient
{
    private readonly HttpClient _httpClient;

    public ControlPlaneClient(HttpClient httpClient, IOptions<ControlPlaneOptions> options)
    {
        var config = options.Value;
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
    }

    public async Task<GatewayApiKeySnapshot?> GetApiKeyAsync(string prefix, CancellationToken ct)
        => await _httpClient.GetFromJsonAsync<GatewayApiKeySnapshot>($"v1/internal/gateway/api-keys/{prefix}", cancellationToken: ct);

    public async Task<TenantEntitlementSnapshot?> GetEntitlementsAsync(string tenantId, CancellationToken ct)
        => await _httpClient.GetFromJsonAsync<TenantEntitlementSnapshot>($"v1/internal/gateway/tenants/{tenantId}/entitlements", cancellationToken: ct);

    public async Task<GatewayEnvironmentState?> GetEnvironmentAsync(string environmentId, CancellationToken ct)
        => await _httpClient.GetFromJsonAsync<GatewayEnvironmentState>($"v1/internal/gateway/environments/{environmentId}/state", cancellationToken: ct);

    public async Task<bool> PostUsageEventAsync(UsageEventRequest request, CancellationToken ct)
    {
        var response = await _httpClient.PostAsJsonAsync("v1/internal/usage-events", request, ct);
        return response.IsSuccessStatusCode;
    }
}
