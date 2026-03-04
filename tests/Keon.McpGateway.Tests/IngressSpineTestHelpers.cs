using System.Text.Json.Nodes;
using Keon.McpGateway.Spine;
using Microsoft.Data.Sqlite;

namespace Keon.McpGateway.Tests;

internal sealed class FailingIngressSpineSink : IIngressSpineSink
{
    public int CallCount { get; private set; }

    public Task AppendAsync(IngressSpineEvent ingressEvent, CancellationToken ct)
    {
        CallCount++;
        throw new InvalidOperationException("sink failure");
    }
}

internal static class IngressSpineAssertions
{
    public static IReadOnlyList<StoredIngressEvent> ReadEvents(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT event_type, correlation_id, receipt_id, payload_json FROM events ORDER BY rowid;";
        using var reader = command.ExecuteReader();

        var events = new List<StoredIngressEvent>();
        while (reader.Read())
        {
            events.Add(new StoredIngressEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                JsonNode.Parse(reader.GetString(3))!.AsObject()));
        }

        return events;
    }
}

internal sealed record StoredIngressEvent(string EventType, string CorrelationId, string ReceiptId, JsonObject Payload);
