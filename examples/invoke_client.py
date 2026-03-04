import json
import os

import requests


def main() -> None:
    base_url = os.getenv("KEON_MCP_GATEWAY_URL", "http://localhost:5000")
    bearer = os.environ["KEON_MCP_BEARER_TOKEN"]
    correlation_id = os.getenv("KEON_DEMO_CORRELATION_ID", "c01J9Z8Q6X4J5Y2P9H3K8M7N6")
    tenant_id = os.getenv("KEON_DEMO_TENANT_ID", "tnt_123")
    actor_id = os.getenv("KEON_DEMO_ACTOR_ID", "usr_456")
    tool = os.getenv("KEON_DEMO_TOOL", "keon.governed.execute.v1")
    action = os.getenv("KEON_DEMO_ACTION", "summarize")
    resource_type = os.getenv("KEON_DEMO_RESOURCE_TYPE", "email")
    resource_scope = os.getenv("KEON_DEMO_RESOURCE_SCOPE", "mailbox:sent")
    purpose = os.getenv("KEON_DEMO_PURPOSE", "Summarize recent sent emails for weekly status update")
    mode = os.getenv("KEON_DEMO_MODE", "decide_then_execute")
    params_json = os.getenv("KEON_DEMO_PARAMS_JSON", "{\"window_days\": 7, \"max_items\": 25}")

    payload = {
        "tenant_id": tenant_id,
        "actor_id": actor_id,
        "correlation_id": correlation_id,
        "idempotency_key": f"idem_{correlation_id}_tool",
        "tool": tool,
        "arguments": {
            "purpose": purpose,
            "action": action,
            "resource": {
                "type": resource_type,
                "scope": resource_scope
            },
            "params": json.loads(params_json),
            "mode": mode
        }
    }

    response = requests.post(
        f"{base_url}/mcp/tools/invoke",
        headers={"Authorization": f"Bearer {bearer}"},
        json=payload,
        timeout=15,
    )
    response.raise_for_status()
    print(json.dumps(response.json(), indent=2))


if __name__ == "__main__":
    main()
