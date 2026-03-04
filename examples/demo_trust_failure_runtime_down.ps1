$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$publicKey = Join-Path $PSScriptRoot "demo-public.pem"
$privateKey = Join-Path $PSScriptRoot "demo-private.pem"
$dbPath = Join-Path $env:TEMP "keon-mcp-demo-runtime-down.db"
$correlationId = "demo-runtime-down-001"

$jwtJson = python (Join-Path $PSScriptRoot "demo_jwt.py") `
  --tenant-id tnt_123 `
  --actor-id usr_456 `
  --private-key $privateKey `
  --public-key $publicKey `
  --scopes keon:mcp:invoke keon:execute

$jwt = ($jwtJson | ConvertFrom-Json).token

$env:KEON_MCP_BEARER_TOKEN = $jwt
$env:KEON_MCP_GATEWAY_URL = "http://127.0.0.1:5010"
$env:KEON_DEMO_CORRELATION_ID = $correlationId
$env:KEON_DEMO_ACTION = "summarize"
$env:KEON_DEMO_RESOURCE_SCOPE = "mailbox:sent"
$env:KEON_DEMO_PURPOSE = "Summarize recent sent emails for weekly status update"
$env:KEON_DEMO_PARAMS_JSON = '{"window_days":7,"max_items":25}'
$env:Runtime__BaseUrl = "http://127.0.0.1:5999"
$env:Auth__Issuer = "keon-auth"
$env:Auth__Audience = "keon-mcp-gateway"
$env:Auth__JwtPublicKeyPem = Get-Content $publicKey -Raw
$env:IngressSpine__Mode = "Required"
$env:IngressSpine__ConnectionString = "Data Source=$dbPath"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5010"

$gateway = Start-Process dotnet -ArgumentList "run --project src\Keon.McpGateway\Keon.McpGateway.csproj --no-launch-profile" -WorkingDirectory $root -PassThru
try {
  Start-Sleep -Seconds 4
  python (Join-Path $PSScriptRoot "invoke_client.py")
} catch {
  Write-Host "Gateway response returned fail-closed as expected."
} finally {
  if (!$gateway.HasExited) { Stop-Process -Id $gateway.Id -Force }
}

@"
import sqlite3
db_path = r"$dbPath"
correlation_id = "$correlationId"
con = sqlite3.connect(db_path)
for row in con.execute("SELECT event_type, receipt_id, created_utc FROM events WHERE correlation_id = ? ORDER BY created_utc", (correlation_id,)):
    print(row)
con.close()
"@ | python -
