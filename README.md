# Keon MCP Gateway

Standalone ASP.NET Minimal API service that fronts the Keon Runtime Gateway and exposes a governed MCP surface:

- `GET /health`
- `POST /mcp/tools/list`
- `POST /mcp/tools/invoke`

The gateway enforces:

- JWT validation with issuer and audience checks
- fail-closed `tenant_id` and `actor_id` binding from claims
- per-tool scope enforcement
- `Decide` before any `Execute`
- canonical MCP request and response validation against [`contracts/mcp_gateway.v1.schema.json`](./contracts/mcp_gateway.v1.schema.json)
- optional durable ingress spine emission for `directive`, `intent`, and terminal `outcome`

## Tools

- `keon.governed.execute.v1`
- `keon.launch.hardening.v1`

## Quickstart

```powershell
dotnet restore tests\Keon.McpGateway.Tests\Keon.McpGateway.Tests.csproj
dotnet test tests\Keon.McpGateway.Tests\Keon.McpGateway.Tests.csproj
dotnet run --project src\Keon.McpGateway\Keon.McpGateway.csproj
```

Default local URL:

```text
http://localhost:5000
```

## Config

`src/Keon.McpGateway/appsettings.json`

```json
{
  "Runtime": {
    "BaseUrl": "http://localhost:8080",
    "TimeoutSeconds": 5,
    "MaxRetries": 2
  },
  "IngressSpine": {
    "Mode": "Off",
    "ConnectionString": "Data Source=ingress-spine.db"
  },
  "Auth": {
    "Issuer": "keon-auth",
    "Audience": "keon-mcp-gateway",
    "JwksUrl": "",
    "JwtPublicKeyPem": "",
    "RequiredScopes": []
  }
}
```

`IngressSpine:Mode`:

- `Off`: no ingress persistence
- `BestEffort`: append failures are logged and never block request completion
- `Required`: append failures are fail-closed; if `directive` append fails the runtime is never called

### Point at Keon SaaS Runtime

Set `Runtime:BaseUrl` to the SaaS runtime base, for example:

```powershell
$env:Runtime__BaseUrl="https://api.keon.systems"
dotnet run --project src\Keon.McpGateway\Keon.McpGateway.csproj
```

### Point at Enterprise Runtime

Set `Runtime:BaseUrl` to the internal runtime base:

```powershell
$env:Runtime__BaseUrl="https://keon-runtime.internal"
dotnet run --project src\Keon.McpGateway\Keon.McpGateway.csproj
```

## Example MCP Invoke

```json
{
  "tenant_id": "tnt_123",
  "actor_id": "usr_456",
  "correlation_id": "c01J9Z8Q6X4J5Y2P9H3K8M7N6",
  "idempotency_key": "idem_01J9Z8Q6X4J5Y2P9H3K8M7N6_tool",
  "tool": "keon.governed.execute.v1",
  "arguments": {
    "purpose": "Summarize recent sent emails for weekly status update",
    "action": "summarize",
    "resource": {
      "type": "email",
      "scope": "mailbox:sent"
    },
    "params": {
      "window_days": 7,
      "max_items": 25
    },
    "mode": "decide_then_execute"
  }
}
```

See [`examples/invoke_client.py`](./examples/invoke_client.py) for a working client snippet.

## AI Assistant Trust Failure Demo

The demo harness uses the real MCP invoke path and the real schema envelope.

Prerequisites:

```powershell
pip install requests pyjwt cryptography fastapi uvicorn
```

### Demo A: Runtime Down

```powershell
.\examples\demo_trust_failure_runtime_down.ps1
```

Expected result:

- gateway returns fail-closed because runtime decide cannot be reached
- ingress SQLite still contains `directive -> intent -> outcome`
- terminal outcome contains `error_code=RUNTIME_DECIDE_FAILED`

### Demo B: Policy Deny Stops Summarization

```powershell
.\examples\demo_policy_deny_blocks_summarize.ps1
```

Expected result:

- mock runtime denies `mailbox:sent` summarization
- no execute call is made
- ingress SQLite contains `directive -> intent -> outcome`
- terminal outcome contains `terminal_status=denied`

The demo mock runtime is [`examples/mock_runtime_server.py`](./examples/mock_runtime_server.py).

## LangChain Adapter

[`examples/langchain_keon_tool.py`](./examples/langchain_keon_tool.py) is the minimal LangChain wrapper:

```python
tool = build_keon_governed_execute_tool(
    gateway_url="http://localhost:5000",
    bearer_token=os.environ["KEON_MCP_BEARER_TOKEN"],
    tenant_id="tnt_123",
    actor_id="usr_456",
)

result = tool.invoke({
    "purpose": "Summarize recent sent emails for weekly status update",
    "action": "summarize",
    "resource_type": "email",
    "resource_scope": "mailbox:sent",
    "params": {"window_days": 7, "max_items": 25}
})
```

## Tool Schemas

Minimal tool metadata lives in:

- [`contracts/mcp_gateway.v1.schema.json`](./contracts/mcp_gateway.v1.schema.json)
- [`vendor/keon-contracts/Hardening/schema/hardening_attestation.v1.schema.json`](./vendor/keon-contracts/Hardening/schema/hardening_attestation.v1.schema.json)

## Test Coverage

The test suite covers:

- schema fixture validation for 6 golden payloads
- approve and execute
- deny without execute
- missing scope
- tenant mismatch
- runtime unavailable
- correlation preservation through decide and execute

## Deferred

- live spine persistence for Directive, Intent, Request, and Outcome receipts
- real `keon.launch.hardening.v1` execution wiring beyond the current governed decision stub
- remote git hosting and PR publication
