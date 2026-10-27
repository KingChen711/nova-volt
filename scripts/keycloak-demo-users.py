"""Đặt mật khẩu cho user thử của realm `novavolt` từ NVM_DEMO_USER_PASSWORD trong .env.

Mật khẩu không nằm trong realm-novavolt.json (K13: không secret trong file commit). Chạy sau mỗi lần realm
được import lại (container Keycloak tạo mới):

    python -I -X utf8 scripts/keycloak-demo-users.py

Không in mật khẩu, chỉ in tên user.
"""

import json
import sys
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def env() -> dict[str, str]:
    values = {}
    for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip()
    return values


def call(method: str, url: str, token: str | None = None, data: bytes | None = None, form: bool = False):
    request = urllib.request.Request(url, data=data, method=method)
    if token:
        request.add_header("Authorization", "Bearer " + token)
    request.add_header("Content-Type", "application/x-www-form-urlencoded" if form else "application/json")
    with urllib.request.urlopen(request, timeout=15) as response:
        body = response.read()
        return json.loads(body) if body else None


def main() -> int:
    values = env()
    password = values.get("NVM_DEMO_USER_PASSWORD", "")
    if not password or password.startswith("CHANGE_ME"):
        print("keycloak-demo-users: set NVM_DEMO_USER_PASSWORD in .env first.", file=sys.stderr)
        return 1

    base = f"http://localhost:{values['NVM_PORT_KEYCLOAK']}"
    token = call("POST", f"{base}/realms/master/protocol/openid-connect/token", form=True, data=urllib.parse.urlencode({
        "grant_type": "password",
        "client_id": "admin-cli",
        "username": values["NVM_KEYCLOAK_ADMIN"],
        "password": values["NVM_KEYCLOAK_PASSWORD"],
    }).encode())["access_token"]

    users = call("GET", f"{base}/admin/realms/novavolt/users?max=200", token)
    for user in users:
        call("PUT", f"{base}/admin/realms/novavolt/users/{user['id']}/reset-password", token,
             json.dumps({"type": "password", "value": password, "temporary": False}).encode())
        print(f"keycloak-demo-users: password set for {user['username']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
