internal static class SchemaPathResolver
{
    public static string ResolveGatewaySchema(string contentRootPath)
        => Resolve(
            contentRootPath,
            Path.Combine("schemas", "mcp_gateway.v1.schema.json"),
            Path.Combine("..", "..", "contracts", "mcp_gateway.v1.schema.json"));

    public static string ResolveHardeningSchema(string contentRootPath)
        => Resolve(
            contentRootPath,
            Path.Combine("schemas", "hardening_attestation.v1.schema.json"),
            Path.Combine("..", "..", "vendor", "keon-contracts", "Hardening", "schema", "hardening_attestation.v1.schema.json"));

    private static string Resolve(string contentRootPath, string packagedRelativePath, string sourceRelativePath)
    {
        var packagedPath = Path.Combine(contentRootPath, packagedRelativePath);
        if (File.Exists(packagedPath))
        {
            return packagedPath;
        }

        return Path.GetFullPath(Path.Combine(contentRootPath, sourceRelativePath));
    }
}
