import os
from http import HTTPStatus

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
import uvicorn

app = FastAPI(title="Keon MCP Demo Runtime Mock")


@app.get("/runtime/v1/status")
async def status():
    return {"status": "operational"}


@app.post("/runtime/v1/decide")
async def decide(request: Request):
    mode = os.getenv("DEMO_RUNTIME_MODE", "allow")
    body = await request.json()
    action = body.get("action", "")
    scope = body.get("resourceScope", "")

    if mode == "deny_mailbox_sent" and scope == "mailbox:sent":
        return {
            "success": True,
            "data": {
                "receiptId": "rcpt_dec_demo_deny",
                "decision": "deny",
                "policyHash": "sha256:demo-deny-policy",
                "policyId": "dlp.mail.summarize",
                "policyVersion": "2026-03-01",
                "reasonCode": "SENSITIVITY_LABEL_BLOCKED"
            }
        }

    if "deny" in action.lower():
        return {
            "success": True,
            "data": {
                "receiptId": "rcpt_dec_demo_deny",
                "decision": "deny",
                "policyHash": "sha256:demo-deny-policy",
                "policyId": "dlp.mail.summarize",
                "policyVersion": "2026-03-01",
                "reasonCode": "PURPOSE_MISMATCH"
            }
        }

    return {
        "success": True,
        "data": {
            "receiptId": "rcpt_dec_demo_allow",
            "decision": "allow",
            "policyHash": "sha256:demo-allow-policy",
            "policyId": "dlp.mail.summarize",
            "policyVersion": "2026-03-01"
        }
    }


@app.post("/runtime/v1/execute")
async def execute(request: Request):
    body = await request.json()
    return {
        "success": True,
        "data": {
            "executionReceiptId": "rcpt_exe_demo",
            "result": {
                "summary": "Governed summary generated.",
                "items_considered": body.get("parameters", {}).get("max_items", 0)
            }
        }
    }


@app.exception_handler(Exception)
async def unhandled(_: Request, ex: Exception):
    return JSONResponse(
        status_code=HTTPStatus.INTERNAL_SERVER_ERROR,
        content={"success": False, "error": {"message": str(ex)}},
    )


if __name__ == "__main__":
    port = int(os.getenv("PORT", "8080"))
    uvicorn.run(app, host="127.0.0.1", port=port, log_level="info")
