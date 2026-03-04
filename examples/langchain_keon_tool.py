import requests
from langchain_core.tools import StructuredTool
from pydantic import BaseModel, Field


class GovernedExecuteInput(BaseModel):
    purpose: str = Field(..., description="Business purpose bound to policy.")
    action: str = Field(..., description="Governed action, for example summarize or retrieve.")
    resource_type: str = Field(..., description="Target resource type, for example email or file.")
    resource_scope: str = Field(..., description="Target scope, for example mailbox:sent.")
    params: dict = Field(default_factory=dict, description="Tool-specific parameters.")
    correlation_id: str | None = Field(default=None, description="Optional correlation ID.")
    mode: str = Field(default="decide_then_execute", description="decide_only or decide_then_execute.")


def build_keon_governed_execute_tool(
    *,
    gateway_url: str,
    bearer_token: str,
    tenant_id: str,
    actor_id: str,
):
    def _invoke(
        purpose: str,
        action: str,
        resource_type: str,
        resource_scope: str,
        params: dict | None = None,
        correlation_id: str | None = None,
        mode: str = "decide_then_execute",
    ) -> dict:
        response = requests.post(
            f"{gateway_url.rstrip('/')}/mcp/tools/invoke",
            headers={"Authorization": f"Bearer {bearer_token}"},
            json={
                "tenant_id": tenant_id,
                "actor_id": actor_id,
                "correlation_id": correlation_id or "langchain-keon-demo",
                "tool": "keon.governed.execute.v1",
                "arguments": {
                    "purpose": purpose,
                    "action": action,
                    "resource": {"type": resource_type, "scope": resource_scope},
                    "params": params or {},
                    "mode": mode,
                },
            },
            timeout=30,
        )
        response.raise_for_status()
        return response.json()

    return StructuredTool.from_function(
        name="keon_governed_execute",
        description="Invoke Keon governed MCP execution through keon.governed.execute.v1.",
        func=_invoke,
        args_schema=GovernedExecuteInput,
    )

