import json
import os

import requests


def main() -> None:
    base_url = os.getenv("KEON_MCP_GATEWAY_URL", "http://localhost:5000")
    bearer = os.environ["KEON_MCP_BEARER_TOKEN"]

    payload = {
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
