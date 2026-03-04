namespace Keon.McpGateway;

public sealed class RateLimitingOptions
{
    public bool Enabled { get; set; }
    public int PermitLimit { get; set; } = 20;
    public int AdminPermitLimit { get; set; } = 30;
    public int WindowSeconds { get; set; } = 60;
}
