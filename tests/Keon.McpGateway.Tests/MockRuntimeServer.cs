using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Keon.McpGateway.Tests;

internal sealed class MockRuntimeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Task _runTask;

    public MockRuntimeServer(RuntimeBehavior behavior)
    {
        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        Behavior = behavior;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(BaseUrl);
        _app = builder.Build();

        _app.MapGet("/runtime/v1/status", async context =>
        {
            await context.Response.WriteAsJsonAsync(new { status = "operational" });
        });

        _app.MapPost("/runtime/v1/decide", async (HttpContext context) =>
        {
            if (Behavior.DecideStatusCode is not null)
            {
                context.Response.StatusCode = (int)Behavior.DecideStatusCode;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = new { message = "runtime decide unavailable" }
                });
                return;
            }

            var body = await context.Request.ReadFromJsonAsync<JsonObject>() ?? [];
            Behavior.DecideCorrelationIds.Add(body["correlationId"]?.GetValue<string>() ?? string.Empty);
            var action = body["action"]?.GetValue<string>() ?? string.Empty;
            var decision = action.Contains("deny", StringComparison.OrdinalIgnoreCase) ? "deny" : "allow";

            await context.Response.WriteAsJsonAsync(new
            {
                success = true,
                data = new
                {
                    receiptId = "rcpt_dec_mock",
                    decision,
                    policyHash = "sha256:1234567890abcdef",
                    policyId = "dlp.mail.summarize",
                    policyVersion = "2026-03-01",
                    reasonCode = decision == "deny" ? "SENSITIVITY_LABEL_BLOCKED" : (string?)null
                }
            });
        });

        _app.MapPost("/runtime/v1/execute", async (HttpContext context) =>
        {
            if (Behavior.ExecuteStatusCode is not null)
            {
                context.Response.StatusCode = (int)Behavior.ExecuteStatusCode;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = new { message = "runtime execute unavailable" }
                });
                return;
            }

            var body = await context.Request.ReadFromJsonAsync<JsonObject>() ?? [];
            Behavior.ExecuteCorrelationIds.Add(body["correlationId"]?.GetValue<string>() ?? string.Empty);
            await context.Response.WriteAsJsonAsync(new
            {
                success = true,
                data = new
                {
                    executionReceiptId = "rcpt_exe_mock",
                    result = new
                    {
                        summary = "done",
                        items_considered = 18
                    }
                }
            });
        });

        _runTask = _app.RunAsync();
    }

    public string BaseUrl { get; }
    public RuntimeBehavior Behavior { get; }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _runTask;
        await _app.DisposeAsync();
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed class RuntimeBehavior
{
    public HttpStatusCode? DecideStatusCode { get; set; }
    public HttpStatusCode? ExecuteStatusCode { get; set; }
    public List<string> DecideCorrelationIds { get; } = [];
    public List<string> ExecuteCorrelationIds { get; } = [];
}
