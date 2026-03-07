param(
    [string]$PrivateKeyPath = "$env:LOCALAPPDATA\keon-mcp-auth\keon-mcp-gateway-private.pem",
    [string]$TenantId = "tnt_123",
    [string]$ActorId = "usr_456",
    [string]$Scope = "keon:mcp:list keon:mcp:invoke keon:execute",
    [string]$Issuer = "keon-auth",
    [string]$Audience = "keon-mcp-gateway",
    [int]$LifetimeMinutes = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $PrivateKeyPath)) {
    throw "Private key not found at $PrivateKeyPath"
}

function ConvertTo-Base64Url([byte[]]$bytes) {
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$now = [DateTimeOffset]::UtcNow
$headerJson = '{"alg":"RS256","typ":"JWT"}'
$payloadJson = @{
    iss = $Issuer
    aud = $Audience
    sub = $ActorId
    tenant_id = $TenantId
    actor_id = $ActorId
    scope = $Scope
    iat = [int][double]::Parse(($now.ToUnixTimeSeconds()).ToString())
    exp = [int][double]::Parse(($now.AddMinutes($LifetimeMinutes).ToUnixTimeSeconds()).ToString())
} | ConvertTo-Json -Compress

$header = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($headerJson))
$payload = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($payloadJson))
$unsignedToken = "$header.$payload"

$rsa = [System.Security.Cryptography.RSA]::Create()
$privatePem = Get-Content -Raw $PrivateKeyPath
$rsa.ImportFromPem($privatePem)
$signatureBytes = $rsa.SignData(
    [Text.Encoding]::UTF8.GetBytes($unsignedToken),
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$signature = ConvertTo-Base64Url $signatureBytes

Write-Output "$unsignedToken.$signature"
