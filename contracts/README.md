# Contracts

`contracts/mcp_gateway.v1.schema.json` is the canonical bundled schema for the gateway surface.

Use the named definitions under `$defs` when validating concrete payloads:

- `ToolsListRequest`
- `ToolsListResponse`
- `ToolsInvokeRequest`
- `ToolsInvokeResponse`

The service validates incoming requests and fail-closes if response validation fails.

`vendor/keon-contracts/Hardening/schema/hardening_attestation.v1.schema.json` is a temporary source-import from `keon-systems`. Replace it with a package reference when canonical contract packages exist.
