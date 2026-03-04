import argparse
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import uuid4

import jwt
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa


def ensure_keypair(private_key_path: Path, public_key_path: Path) -> bytes:
    if private_key_path.exists() and public_key_path.exists():
        return private_key_path.read_bytes()

    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    private_key_path.write_bytes(
        key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption(),
        )
    )
    public_key_path.write_bytes(
        key.public_key().public_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PublicFormat.SubjectPublicKeyInfo,
        )
    )
    return private_key_path.read_bytes()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tenant-id", default="tnt_123")
    parser.add_argument("--actor-id", default="usr_456")
    parser.add_argument("--issuer", default="keon-auth")
    parser.add_argument("--audience", default="keon-mcp-gateway")
    parser.add_argument("--scopes", nargs="+", required=True)
    parser.add_argument("--private-key", required=True)
    parser.add_argument("--public-key", required=True)
    args = parser.parse_args()

    private_key_path = Path(args.private_key)
    public_key_path = Path(args.public_key)
    private_key = ensure_keypair(private_key_path, public_key_path)

    now = datetime.now(timezone.utc)
    token = jwt.encode(
        {
            "iss": args.issuer,
            "aud": args.audience,
            "sub": args.actor_id,
            "tenant_id": args.tenant_id,
            "actor_id": args.actor_id,
            "scope": " ".join(args.scopes),
            "iat": int(now.timestamp()),
            "nbf": int(now.timestamp()),
            "exp": int((now + timedelta(minutes=15)).timestamp()),
            "jti": uuid4().hex,
        },
        private_key,
        algorithm="RS256",
    )

    print(json.dumps({"token": token, "public_key": str(public_key_path)}, indent=2))


if __name__ == "__main__":
    main()
