using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Keon.McpGateway;
using Keon.McpGateway.Auth;
using Keon.McpGateway.ControlPlane;
using Keon.McpGateway.Contracts;
using Keon.McpGateway.Runtime;
using Keon.McpGateway.Spine;
using Keon.McpGateway.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RuntimeOptions>(builder.Configuration.GetSection("Runtime"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<ControlPlaneOptions>(builder.Configuration.GetSection("ControlPlane"));
builder.Services.Configure<IngressSpineOptions>(builder.Configuration.GetSection("IngressSpine"));
builder.Services.Configure<RateLimitingOptions>(builder.Configuration.GetSection("RateLimiting"));
builder.Services.Configure<SchemaOptions>(options =>
{
    options.SchemaPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "contracts", "mcp_gateway.v1.schema.json"));
});
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = async (context, ct) =>
    {
        var correlationId = CorrelationIdHelper.ResolveOrCreate(context.HttpContext.Request.Headers["x-correlation-id"].FirstOrDefault());
        var response = McpResults.Error(correlationId, "keon.governed.execute.v1", "MCP_RATE_LIMITED", "Request rate limit exceeded.", StatusCodes.Status429TooManyRequests, true);
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(response, cancellationToken: ct);
    };

    options.AddPolicy("mcp_public", httpContext =>
    {
        var rateLimitingOptions = httpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<RateLimitingOptions>>().Value;
        if (!rateLimitingOptions.Enabled)
        {
            return RateLimitPartition.GetNoLimiter("mcp_public_disabled");
        }

        var path = httpContext.Request.Path.ToString();
        var bucket =
            path.StartsWith("/mcp/tools/list", StringComparison.OrdinalIgnoreCase) ? "/mcp/tools/list" :
            path.StartsWith("/mcp/tools/invoke", StringComparison.OrdinalIgnoreCase) ? "/mcp/tools/invoke" :
            "/mcp/other";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"mcp_public:{bucket}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimitingOptions.PermitLimit,
                Window = TimeSpan.FromSeconds(rateLimitingOptions.WindowSeconds),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            });
    });
});

builder.Services.AddHttpClient();
builder.Services.AddHttpClient<RuntimeClient>();
builder.Services.AddHttpClient<ControlPlaneClient>();
builder.Services.AddSingleton<SchemaRegistry>();
builder.Services.AddSingleton<JwtValidator>();
builder.Services.AddSingleton<ApiKeyValidator>();
builder.Services.AddSingleton<UsageEventOutbox>();
builder.Services.AddSingleton<DirectiveFactory>();
builder.Services.AddSingleton<IntentFactory>();
builder.Services.AddSingleton<InvokeTerminalWriter>();
builder.Services.AddSingleton<IIngressSpineSink>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IngressSpineOptions>>().Value;
    return options.ParsedMode == IngressSpineMode.Off
        ? new NoopIngressSpineSink()
        : new SqliteIngressSpineSink(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IngressSpineOptions>>());
});
builder.Services.AddSingleton<IngressSpineEmitter>();
builder.Services.AddSingleton<ToolRegistryAccessor>();
builder.Services.AddSingleton<GovernedExecuteHandler>();
builder.Services.AddSingleton<LaunchHardeningHandler>();
builder.Services.AddSingleton<ToolRegistry>(sp =>
{
    var registry = new ToolRegistry(
        sp.GetRequiredService<GovernedExecuteHandler>(),
        sp.GetRequiredService<LaunchHardeningHandler>(),
        sp.GetRequiredService<IWebHostEnvironment>());
    sp.GetRequiredService<ToolRegistryAccessor>().Registry = registry;
    return registry;
});

var app = builder.Build();
app.UseRateLimiter();

app.MapGet("/health", async (RuntimeClient runtimeClient, CancellationToken ct) =>
{
    var status = await runtimeClient.GetStatusAsync(ct);
    return Results.Json(new
    {
        status = "ok",
        runtime = status
    });
});

app.MapPost("/mcp/tools/list", async (
    HttpContext httpContext,
    ToolsListRequest request,
    SchemaRegistry schemas,
    JwtValidator jwtValidator,
    ToolRegistry toolRegistry,
    CancellationToken ct) =>
{
    var correlationId = CorrelationIdHelper.ResolveOrCreate(request.CorrelationId);
    var requestValidation = schemas.ValidateDefinition("ToolsListRequest", JsonSerializerHelper.ToNode(request));
    if (!requestValidation.IsValid)
    {
        return Results.Json(
            McpResults.Error(correlationId, "keon.governed.execute.v1", "MCP_TOOL_SCHEMA_INVALID", requestValidation.Message, StatusCodes.Status422UnprocessableEntity, false),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    var auth = await jwtValidator.ValidateAsync(httpContext, request.TenantId, request.ActorId, new[] { "keon:mcp:list" }, ct);
    if (auth.Error is not null)
    {
        return Results.Json(auth.Error, statusCode: auth.Error.Error.HttpStatus);
    }

    var response = new ToolsListResponse(correlationId, toolRegistry.ListTools(request.IncludeSchemas ?? true));
    var responseValidation = schemas.ValidateDefinition("ToolsListResponse", JsonSerializerHelper.ToNode(response));
    if (!responseValidation.IsValid)
    {
        return Results.Json(
            McpResults.Error(correlationId, "keon.governed.execute.v1", "GOVERNANCE_FAIL_CLOSED", responseValidation.Message, StatusCodes.Status500InternalServerError, false),
            statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Json(response);
})
.RequireRateLimiting("mcp_public");

app.MapPost("/mcp/tools/invoke", async (
    HttpContext httpContext,
    JsonObject requestBody,
    SchemaRegistry schemas,
    JwtValidator jwtValidator,
    ToolRegistry toolRegistry,
    DirectiveFactory directiveFactory,
    IntentFactory intentFactory,
    IngressSpineEmitter ingressSpineEmitter,
    InvokeTerminalWriter terminalWriter,
    CancellationToken ct) =>
{
    var invokeRequestResult = JsonSerializerHelper.Deserialize<ToolsInvokeRequest>(requestBody);
    var correlationId = CorrelationIdHelper.ResolveOrCreate(invokeRequestResult.Value?.CorrelationId);
    var requestValidation = schemas.ValidateDefinition("ToolsInvokeRequest", requestBody);
    if (!requestValidation.IsValid || invokeRequestResult.Value is null)
    {
        return Results.Json(
            McpResults.Error(correlationId, "keon.governed.execute.v1", "MCP_TOOL_SCHEMA_INVALID", requestValidation.IsValid ? invokeRequestResult.ErrorMessage! : requestValidation.Message, StatusCodes.Status422UnprocessableEntity, false),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    var request = invokeRequestResult.Value;
    httpContext.Items["tool"] = request.Tool;

    var hasApiKey = !string.IsNullOrWhiteSpace(httpContext.Request.Headers["X-Api-Key"].FirstOrDefault());
    AuthResult auth;
    GatewayApiKeySnapshot? apiKeySnapshot = null;
    if (hasApiKey)
    {
        var apiKeyAuth = await httpContext.RequestServices.GetRequiredService<ApiKeyValidator>().ValidateAsync(httpContext, request, ct);
        if (apiKeyAuth.Error is not null)
        {
            return Results.Json(apiKeyAuth.Error, statusCode: apiKeyAuth.Error.Error.HttpStatus);
        }

        apiKeySnapshot = apiKeyAuth.Context!.ApiKey;
        auth = new AuthResult(new AuthSuccess(apiKeyAuth.Context.Principal), null);
        httpContext.Items["api_key_snapshot"] = apiKeySnapshot;
    }
    else
    {
        auth = await jwtValidator.ValidateAsync(httpContext, request.TenantId, request.ActorId, toolRegistry.GetRequiredScopes(request.Tool), ct);
    }

    if (auth.Error is not null)
    {
        return Results.Json(auth.Error, statusCode: auth.Error.Error.HttpStatus);
    }

    DirectiveReceipt directive;
    try
    {
        directive = directiveFactory.Create(request, auth.Principal!.Principal);
    }
    catch (Exception ex)
    {
        return Results.Json(
            McpResults.Error(correlationId, request.Tool, "GOVERNANCE_FAIL_CLOSED", ex.Message, StatusCodes.Status500InternalServerError, false),
            statusCode: StatusCodes.Status500InternalServerError);
    }

    try
    {
        await ingressSpineEmitter.AppendDirectiveAsync(directive, ct);
    }
    catch (IngressSpineException ex)
    {
        var failClosed = McpResults.Error(correlationId, request.Tool, "GOVERNANCE_FAIL_CLOSED", ex.Message, StatusCodes.Status500InternalServerError, false, directive.ReceiptId);
        return Results.Json(failClosed, statusCode: StatusCodes.Status500InternalServerError);
    }

    var handler = toolRegistry.Resolve(request.Tool);
    if (handler is null)
    {
        var missingTool = McpResults.Error(correlationId, request.Tool, "MCP_TOOL_NOT_FOUND", $"Unknown tool: {request.Tool}", StatusCodes.Status404NotFound, false, directive.ReceiptId);
        var finalizedMissingTool = await terminalWriter.FinalizeAsync(StatusCodes.Status404NotFound, missingTool, correlationId, request.Tool, directive, ingressSpineEmitter, ct);
        return Results.Json(finalizedMissingTool.Body, statusCode: finalizedMissingTool.StatusCode);
    }

    var intent = intentFactory.Create(request, directive);
    try
    {
        await ingressSpineEmitter.AppendIntentAsync(intent, ct);
    }
    catch (IngressSpineException ex)
    {
        var failClosed = McpResults.Error(correlationId, request.Tool, "GOVERNANCE_FAIL_CLOSED", ex.Message, StatusCodes.Status500InternalServerError, false, directive.ReceiptId);
        var finalizedFailClosed = await terminalWriter.FinalizeAsync(StatusCodes.Status500InternalServerError, failClosed, correlationId, request.Tool, directive, ingressSpineEmitter, ct);
        return Results.Json(finalizedFailClosed.Body, statusCode: finalizedFailClosed.StatusCode);
    }

    var result = await handler.InvokeAsync(new ToolInvocationContext(request, auth.Principal.Principal, directive, intent), ct);
    var responseValidation = schemas.ValidateDefinition("ToolsInvokeResponse", JsonSerializerHelper.ToNode(result.Body));
    if (!responseValidation.IsValid)
    {
        var failClosed = McpResults.Error(correlationId, request.Tool, "GOVERNANCE_FAIL_CLOSED", responseValidation.Message, StatusCodes.Status500InternalServerError, false, directive.ReceiptId);
        var finalizedFailClosed = await terminalWriter.FinalizeAsync(StatusCodes.Status500InternalServerError, failClosed, correlationId, request.Tool, directive, ingressSpineEmitter, ct);
        return Results.Json(finalizedFailClosed.Body, statusCode: finalizedFailClosed.StatusCode);
    }

    if (apiKeySnapshot is not null && result.Body is McpSuccessResponse successBody)
    {
        var outbox = httpContext.RequestServices.GetRequiredService<UsageEventOutbox>();
        var stored = await outbox.EnqueueAsync(request, successBody, apiKeySnapshot, ct);
        if (!stored)
        {
            var failClosed = McpResults.Error(correlationId, request.Tool, "USAGE_EVENT_OUTBOX_FAILED", "Terminal decision could not be durably queued for metering.", StatusCodes.Status502BadGateway, true, directive.ReceiptId);
            var finalizedFailure = await terminalWriter.FinalizeAsync(StatusCodes.Status502BadGateway, failClosed, correlationId, request.Tool, directive, ingressSpineEmitter, ct);
            return Results.Json(finalizedFailure.Body, statusCode: finalizedFailure.StatusCode);
        }
    }

    var finalized = await terminalWriter.FinalizeAsync(result.StatusCode, result.Body, correlationId, request.Tool, directive, ingressSpineEmitter, ct);
    return Results.Json(finalized.Body, statusCode: finalized.StatusCode);
})
.RequireRateLimiting("mcp_public");

app.Run();

public partial class Program;
