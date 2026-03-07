param(
    [string]$TenantId = "tnt_staging_redteam",
    [string]$ActorId = "usr_staging_operator",
    [string]$Scope = "keon:mcp:list keon:mcp:invoke keon:execute keon:attest",
    [int]$LifetimeMinutes = 60
)

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$privateKeyPath = Join-Path $env:LOCALAPPDATA 'keon-mcp-auth\staging\keon-mcp-gateway-staging-private.pem'

& (Join-Path $scriptRoot 'mint_token.ps1') `
    -PrivateKeyPath $privateKeyPath `
    -TenantId $TenantId `
    -ActorId $ActorId `
    -Scope $Scope `
    -Issuer 'keon-auth-staging' `
    -Audience 'keon-mcp-gateway-staging' `
    -LifetimeMinutes $LifetimeMinutes
