using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Keon.McpGateway.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Keon.McpGateway.Spine;

public enum IngressSpineMode
{
    Off,
    BestEffort,
    Required
}

public sealed class IngressSpineOptions
{
    public string Mode { get; set; } = "Off";
    public string ConnectionString { get; set; } = "Data Source=ingress-spine.db";

    public IngressSpineMode ParsedMode
        => Enum.TryParse<IngressSpineMode>(Mode, ignoreCase: true, out var mode) ? mode : IngressSpineMode.Off;
}

public sealed record IngressSpineEvent(
    string EventId,
    string EventType,
    string CorrelationId,
    string ReceiptId,
    string PayloadJson,
    DateTimeOffset CreatedUtc);

public interface IIngressSpineSink
{
    Task AppendAsync(IngressSpineEvent ingressEvent, CancellationToken ct);
}

public sealed class NoopIngressSpineSink : IIngressSpineSink
{
    public Task AppendAsync(IngressSpineEvent ingressEvent, CancellationToken ct)
        => Task.CompletedTask;
}

public sealed class SqliteIngressSpineSink : IIngressSpineSink
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile bool _initialized;

    public SqliteIngressSpineSink(IOptions<IngressSpineOptions> options)
    {
        _connectionString = SqliteConnectionStringFactory.Normalize(options.Value.ConnectionString);
    }

    public async Task AppendAsync(IngressSpineEvent ingressEvent, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        await _writeGate.WaitAsync(ct);
        try
        {
            await ExecuteWithRetryAsync(
                async token =>
                {
                    await using var connection = new SqliteConnection(_connectionString);
                    await connection.OpenAsync(token);
                    await ConfigureConnectionAsync(connection, token);

                    var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        INSERT INTO events(event_id, event_type, correlation_id, receipt_id, payload_json, created_utc)
                        VALUES ($event_id, $event_type, $correlation_id, $receipt_id, $payload_json, $created_utc);
                        """;
                    command.Parameters.AddWithValue("$event_id", ingressEvent.EventId);
                    command.Parameters.AddWithValue("$event_type", ingressEvent.EventType);
                    command.Parameters.AddWithValue("$correlation_id", ingressEvent.CorrelationId);
                    command.Parameters.AddWithValue("$receipt_id", ingressEvent.ReceiptId);
                    command.Parameters.AddWithValue("$payload_json", ingressEvent.PayloadJson);
                    command.Parameters.AddWithValue("$created_utc", ingressEvent.CreatedUtc.ToString("O"));

                    await command.ExecuteNonQueryAsync(token);
                },
                ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await _initGate.WaitAsync(ct);
        try
        {
            if (_initialized)
            {
                return;
            }

            await ExecuteWithRetryAsync(
                async token =>
                {
                    await using var connection = new SqliteConnection(_connectionString);
                    await connection.OpenAsync(token);

                    var pragmaCommand = connection.CreateCommand();
                    pragmaCommand.CommandText =
                        """
                        PRAGMA journal_mode = DELETE;
                        PRAGMA synchronous = NORMAL;
                        """;
                    await pragmaCommand.ExecuteNonQueryAsync(token);

                    var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        CREATE TABLE IF NOT EXISTS events(
                          event_id TEXT PRIMARY KEY,
                          event_type TEXT NOT NULL,
                          correlation_id TEXT NOT NULL,
                          receipt_id TEXT NOT NULL,
                          payload_json TEXT NOT NULL,
                          created_utc TEXT NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS idx_events_correlation_id ON events(correlation_id);
                        """;

                    await command.ExecuteNonQueryAsync(token);
                },
                ct);
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteWithRetryAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        var delayMs = 100;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await operation(ct);
                return;
            }
            catch (SqliteException ex) when (attempt < 5 && IsTransient(ex))
            {
                await Task.Delay(delayMs, ct);
                delayMs *= 2;
            }
        }
    }

    private static bool IsTransient(SqliteException ex)
        => ex.SqliteErrorCode is 5 or 6;
}

public static class SqliteConnectionStringFactory
{
    public static string Normalize(string rawConnectionString)
    {
        var builder = new SqliteConnectionStringBuilder(rawConnectionString)
        {
            Pooling = false,
            DefaultTimeout = 5
        };
        return builder.ConnectionString;
    }
}

public sealed class IngressSpineException : Exception
{
    public IngressSpineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record IngressAppendResult(bool Persisted, string? ErrorMessage);

public sealed class IngressSpineEmitter
{
    private readonly IIngressSpineSink _sink;
    private readonly IngressSpineOptions _options;
    private readonly ILogger<IngressSpineEmitter> _logger;

    public IngressSpineEmitter(IIngressSpineSink sink, IOptions<IngressSpineOptions> options, ILogger<IngressSpineEmitter> logger)
    {
        _sink = sink;
        _options = options.Value;
        _logger = logger;
    }

    public IngressSpineMode Mode => _options.ParsedMode;

    public async Task<IngressAppendResult> AppendDirectiveAsync(DirectiveReceipt directive, CancellationToken ct)
        => await AppendAsync(
            "directive",
            directive.CorrelationId,
            directive.ReceiptId,
            new JsonObject
            {
                ["tool"] = directive.Tool,
                ["args_hash"] = directive.ArgsHash,
                ["tenant_id"] = directive.TenantId,
                ["actor_id"] = directive.ActorId,
                ["received_at"] = directive.ReceivedAtUtc.ToString("O")
            },
            ct);

    public async Task<IngressAppendResult> AppendIntentAsync(IntentReceipt intent, CancellationToken ct)
        => await AppendAsync(
            "intent",
            intent.CorrelationId,
            intent.ReceiptId,
            new JsonObject
            {
                ["intent_type"] = intent.IntentType,
                ["resource_scope"] = intent.ResourceScope,
                ["purpose"] = intent.Purpose,
                ["constraints"] = intent.Constraints.DeepClone()
            },
            ct);

    public async Task<IngressAppendResult> AppendOutcomeAsync(string correlationId, string receiptId, JsonObject payload, CancellationToken ct)
    {
        payload["persisted"] = true;
        return await AppendAsync("outcome", correlationId, receiptId, payload, ct);
    }

    private async Task<IngressAppendResult> AppendAsync(string eventType, string correlationId, string receiptId, JsonObject payload, CancellationToken ct)
    {
        if (Mode == IngressSpineMode.Off)
        {
            return new IngressAppendResult(false, null);
        }

        try
        {
            var ingressEvent = new IngressSpineEvent(
                IdFactory.NewReceiptId("evt"),
                eventType,
                correlationId,
                receiptId,
                payload.ToJsonString(JsonSerializerHelper.JsonOptions),
                DateTimeOffset.UtcNow);

            await _sink.AppendAsync(ingressEvent, ct);
            return new IngressAppendResult(true, null);
        }
        catch (Exception ex)
        {
            if (Mode == IngressSpineMode.Required)
            {
                throw new IngressSpineException($"Ingress spine append failed for {eventType}: {ex.Message}", ex);
            }

            _logger.LogError(ex, "Ingress spine append failed for {EventType} on correlation {CorrelationId}", eventType, correlationId);
            return new IngressAppendResult(false, ex.Message);
        }
    }
}

public static class OutcomeFactory
{
    public static JsonObject FromSuccess(McpSuccessResponse response)
        => new()
        {
            ["terminal_status"] = response.Decision.Status == "denied" ? "denied" : "approved",
            ["tool"] = response.Tool,
            ["ok"] = response.Ok,
            ["policy_hash"] = response.Decision.PolicyHash,
            ["decision_receipt"] = response.Receipts.Decision,
            ["execution_receipt"] = response.Receipts.Execution,
            ["outcome_receipt"] = response.Receipts.Outcome
        };

    public static JsonObject FromError(McpErrorResponse response)
        => new()
        {
            ["terminal_status"] = "error",
            ["tool"] = response.Tool,
            ["ok"] = response.Ok,
            ["error_code"] = response.Error.Code,
            ["http_status"] = response.Error.HttpStatus,
            ["retryable"] = response.Error.Retryable,
            ["directive_receipt"] = response.Receipts.Directive
        };
}
