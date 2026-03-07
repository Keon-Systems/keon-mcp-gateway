using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keon.McpGateway.Spine;

namespace Keon.McpGateway.Tests;

public sealed class GatewayIntegrationTests
{
    [Fact]
    public async Task Invoke_approve_and_execute_returns_canonical_success()
    {
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior());
        using var issuer = new TestTokenIssuer();
        await using var factory = new GatewayApplicationFactory(runtime.BaseUrl, issuer.PublicKeyPem);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("tnt_123", "usr_456", "keon:mcp:invoke", "keon:execute"));

        var response = await client.PostAsJsonAsync("/mcp/tools/invoke", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            correlation_id = "c01J9Z8Q6X4J5Y2P9H3K8M7N6",
            idempotency_key = "idem_123",
            tool = "keon.governed.execute.v1",
            arguments = new
            {
                purpose = "Summarize recent sent emails for weekly status update",
                action = "summarize",
                resource = new { type = "email", scope = "mailbox:sent" },
                @params = new { window_days = 7, max_items = 25 },
                mode = "decide_then_execute"
            }
        });

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.True(body!["ok"]!.GetValue<bool>());
        Assert.Equal("approved", body["decision"]!["status"]!.GetValue<string>());
        Assert.Equal("rcpt_exe_mock", body["receipts"]!["execution"]!.GetValue<string>());
        Assert.Equal("c01J9Z8Q6X4J5Y2P9H3K8M7N6", runtime.Behavior.DecideCorrelationIds.Single());
        Assert.Equal("c01J9Z8Q6X4J5Y2P9H3K8M7N6", runtime.Behavior.ExecuteCorrelationIds.Single());
    }

    [Fact]
    public async Task Invoke_deny_returns_ok_true_without_execute()
    {
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior());
        using var issuer = new TestTokenIssuer();
        await using var factory = new GatewayApplicationFactory(runtime.BaseUrl, issuer.PublicKeyPem);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("tnt_123", "usr_456", "keon:mcp:invoke", "keon:execute"));

        var response = await client.PostAsJsonAsync("/mcp/tools/invoke", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            correlation_id = "c01J9Z8Q6X4J5Y2P9H3K8M7N6",
            tool = "keon.governed.execute.v1",
            arguments = new
            {
                purpose = "deny",
                action = "deny_this_action",
                resource = new { type = "email", scope = "mailbox:sent" },
                @params = new { window_days = 7 },
                mode = "decide_then_execute"
            }
        });

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.True(body!["ok"]!.GetValue<bool>());
        Assert.Equal("denied", body["decision"]!["status"]!.GetValue<string>());
        Assert.Null(body["receipts"]!["execution"]);
        Assert.Empty(runtime.Behavior.ExecuteCorrelationIds);
    }

    [Fact]
    public async Task Invoke_missing_scope_fails_closed()
    {
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior());
        using var issuer = new TestTokenIssuer();
        await using var factory = new GatewayApplicationFactory(runtime.BaseUrl, issuer.PublicKeyPem);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("tnt_123", "usr_456", "keon:mcp:invoke"));

        var response = await client.PostAsJsonAsync("/mcp/tools/invoke", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            correlation_id = "c01J9Z8Q6X4J5Y2P9H3K8M7N6",
            tool = "keon.governed.execute.v1",
            arguments = new
            {
                action = "summarize",
                resource = new { type = "email", scope = "mailbox:sent" },
                @params = new { window_days = 7 }
            }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(body!["ok"]!.GetValue<bool>());
        Assert.Equal("MCP_SCOPE_DENIED", body["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invoke_tenant_mismatch_fails_closed()
    {
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior());
        using var issuer = new TestTokenIssuer();
        await using var factory = new GatewayApplicationFactory(runtime.BaseUrl, issuer.PublicKeyPem);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("different", "usr_456", "keon:mcp:invoke", "keon:execute"));

        var response = await client.PostAsJsonAsync("/mcp/tools/invoke", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            correlation_id = "c01J9Z8Q6X4J5Y2P9H3K8M7N6",
            tool = "keon.governed.execute.v1",
            arguments = new
            {
                action = "summarize",
                resource = new { type = "email", scope = "mailbox:sent" },
                @params = new { window_days = 7 }
            }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("MCP_TENANT_MISMATCH", body!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invoke_runtime_down_returns_runtime_decide_failed()
    {
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior { DecideStatusCode = HttpStatusCode.BadGateway });
        using var issuer = new TestTokenIssuer();
        await using var factory = new GatewayApplicationFactory(runtime.BaseUrl, issuer.PublicKeyPem);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("tnt_123", "usr_456", "keon:mcp:invoke", "keon:execute"));

        var response = await client.PostAsJsonAsync("/mcp/tools/invoke", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            correlation_id = "c01J9Z8Q6X4J5Y2P9H3K8M7N6",
            tool = "keon.governed.execute.v1",
            arguments = new
            {
                action = "summarize",
                resource = new { type = "email", scope = "mailbox:sent" },
                @params = new { window_days = 7 }
            }
        });

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadGateway, text);
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.Equal("RUNTIME_DECIDE_FAILED", body!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tools_list_returns_registered_tools()
    {
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior());
        using var issuer = new TestTokenIssuer();
        await using var factory = new GatewayApplicationFactory(runtime.BaseUrl, issuer.PublicKeyPem);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("tnt_123", "usr_456", "keon:mcp:list"));

        var response = await client.PostAsJsonAsync("/mcp/tools/list", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            include_schemas = true
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(2, body!["tools"]!.AsArray().Count);
    }

    [Fact]
    public async Task Tools_list_without_schemas_omits_schema_fields_and_returns_registered_tools()
    {
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior());
        using var issuer = new TestTokenIssuer();
        await using var factory = new GatewayApplicationFactory(runtime.BaseUrl, issuer.PublicKeyPem);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("tnt_123", "usr_456", "keon:mcp:list"));

        var response = await client.PostAsJsonAsync("/mcp/tools/list", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            include_schemas = false
        });

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);

        var body = JsonNode.Parse(text)!.AsObject();
        var tools = body["tools"]!.AsArray();
        Assert.Equal(2, tools.Count);
        Assert.All(tools, toolNode =>
        {
            var tool = Assert.IsType<JsonObject>(toolNode);
            Assert.False(tool.ContainsKey("input_schema"));
            Assert.False(tool.ContainsKey("output_schema"));
        });
    }

    [Fact]
    public async Task Required_mode_sink_failure_on_directive_fails_closed_before_runtime_call()
    {
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior());
        using var issuer = new TestTokenIssuer();
        var failingSink = new FailingIngressSpineSink();
        await using var factory = new GatewayApplicationFactory(runtime.BaseUrl, issuer.PublicKeyPem, IngressSpineMode.Required, sinkOverride: failingSink);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("tnt_123", "usr_456", "keon:mcp:invoke", "keon:execute"));

        var response = await client.PostAsJsonAsync("/mcp/tools/invoke", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            correlation_id = "c_required_directive_fail",
            tool = "keon.governed.execute.v1",
            arguments = new
            {
                action = "summarize",
                resource = new { type = "email", scope = "mailbox:sent" },
                @params = new { window_days = 7 }
            }
        });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("GOVERNANCE_FAIL_CLOSED", body!["error"]!["code"]!.GetValue<string>());
        Assert.Empty(runtime.Behavior.DecideCorrelationIds);
        Assert.Equal(1, failingSink.CallCount);
    }

    [Fact]
    public async Task Required_mode_runtime_down_still_appends_outcome()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"keon-ingress-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior { DecideStatusCode = HttpStatusCode.BadGateway });
        using var issuer = new TestTokenIssuer();
        await using var factory = new GatewayApplicationFactory(runtime.BaseUrl, issuer.PublicKeyPem, IngressSpineMode.Required, connectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("tnt_123", "usr_456", "keon:mcp:invoke", "keon:execute"));

        var response = await client.PostAsJsonAsync("/mcp/tools/invoke", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            correlation_id = "c_required_runtime_down",
            tool = "keon.governed.execute.v1",
            arguments = new
            {
                action = "summarize",
                resource = new { type = "email", scope = "mailbox:sent" },
                @params = new { window_days = 7 }
            }
        });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var events = IngressSpineAssertions.ReadEvents(connectionString);
        Assert.Equal(["directive", "intent", "outcome"], events.Select(e => e.EventType).ToArray());
        Assert.Equal("RUNTIME_DECIDE_FAILED", events.Last().Payload["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Required_mode_deny_path_appends_terminal_outcome()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"keon-ingress-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior());
        using var issuer = new TestTokenIssuer();
        await using var factory = new GatewayApplicationFactory(runtime.BaseUrl, issuer.PublicKeyPem, IngressSpineMode.Required, connectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("tnt_123", "usr_456", "keon:mcp:invoke", "keon:execute"));

        var response = await client.PostAsJsonAsync("/mcp/tools/invoke", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            correlation_id = "c_required_deny",
            tool = "keon.governed.execute.v1",
            arguments = new
            {
                action = "deny_this_action",
                resource = new { type = "email", scope = "mailbox:sent" },
                @params = new { window_days = 7 }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = IngressSpineAssertions.ReadEvents(connectionString);
        Assert.Equal(["directive", "intent", "outcome"], events.Select(e => e.EventType).ToArray());
        Assert.Equal("denied", events.Last().Payload["terminal_status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Required_mode_approve_execute_appends_directive_intent_outcome_in_order()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"keon-ingress-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior());
        using var issuer = new TestTokenIssuer();
        await using var factory = new GatewayApplicationFactory(runtime.BaseUrl, issuer.PublicKeyPem, IngressSpineMode.Required, connectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("tnt_123", "usr_456", "keon:mcp:invoke", "keon:execute"));

        var response = await client.PostAsJsonAsync("/mcp/tools/invoke", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            correlation_id = "c_required_approve",
            idempotency_key = "idem_approve",
            tool = "keon.governed.execute.v1",
            arguments = new
            {
                action = "summarize",
                resource = new { type = "email", scope = "mailbox:sent" },
                @params = new { window_days = 7 },
                mode = "decide_then_execute"
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = IngressSpineAssertions.ReadEvents(connectionString);
        Assert.Equal(["directive", "intent", "outcome"], events.Select(e => e.EventType).ToArray());
        Assert.Equal("approved", events.Last().Payload["terminal_status"]!.GetValue<string>());
        Assert.Equal("c_required_approve", events[0].CorrelationId);
    }

    [Fact]
    public async Task Best_effort_sink_failure_does_not_block()
    {
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior());
        using var issuer = new TestTokenIssuer();
        var failingSink = new FailingIngressSpineSink();
        await using var factory = new GatewayApplicationFactory(runtime.BaseUrl, issuer.PublicKeyPem, IngressSpineMode.BestEffort, sinkOverride: failingSink);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("tnt_123", "usr_456", "keon:mcp:invoke", "keon:execute"));

        var response = await client.PostAsJsonAsync("/mcp/tools/invoke", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            correlation_id = "c_best_effort",
            idempotency_key = "idem_best_effort",
            tool = "keon.governed.execute.v1",
            arguments = new
            {
                action = "summarize",
                resource = new { type = "email", scope = "mailbox:sent" },
                @params = new { window_days = 7 }
            }
        });

        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body!["ok"]!.GetValue<bool>());
        Assert.True(failingSink.CallCount >= 3);
    }

    [Fact]
    public async Task Tools_list_rate_limited_returns_429()
    {
        await using var runtime = new MockRuntimeServer(new RuntimeBehavior());
        using var issuer = new TestTokenIssuer();
        await using var factory = new GatewayApplicationFactory(
            runtime.BaseUrl,
            issuer.PublicKeyPem,
            rateLimitingEnabled: true,
            rateLimitingPermitLimit: 1,
            rateLimitingWindowSeconds: 60);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateToken("tnt_123", "usr_456", "keon:mcp:list"));

        var first = await client.PostAsJsonAsync("/mcp/tools/list", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            include_schemas = false
        });

        var second = await client.PostAsJsonAsync("/mcp/tools/list", new
        {
            tenant_id = "tnt_123",
            actor_id = "usr_456",
            include_schemas = false
        });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("MCP_RATE_LIMITED", body!["error"]!["code"]!.GetValue<string>());
    }
}
