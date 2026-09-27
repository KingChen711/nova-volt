"""Thêm vào realm `novavolt` đang chạy những gì có trong realm-novavolt.json mà runtime chưa có.

Chỉ THÊM, không ghi đè: client scope, realm role, client, user còn thiếu. Không recreate container, nên dữ liệu runtime
(user id mà Mendix đã ánh xạ, phiên đăng nhập) được giữ. Sửa một thứ đã tồn tại thì vẫn phải làm tay hoặc recreate
theo deploy/keycloak/README.md.

Chạy: python scripts/keycloak-add-missing.py   (đọc NVM_PORT_KEYCLOAK, NVM_KEYCLOAK_ADMIN, NVM_KEYCLOAK_PASSWORD từ .env)
Không in credential nào.
"""
import json
import pathlib
import sys
import urllib.parse
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]


def env():
    values = {}
    for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
        if "=" in line and not line.lstrip().startswith("#"):
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip().strip('"')
    return values


def main():
    config = env()
    base = f"http://localhost:{config['NVM_PORT_KEYCLOAK']}"
    form = urllib.parse.urlencode({"client_id": "admin-cli", "grant_type": "password",
                                   "username": config["NVM_KEYCLOAK_ADMIN"],
                                   "password": config["NVM_KEYCLOAK_PASSWORD"]}).encode()
    with urllib.request.urlopen(f"{base}/realms/master/protocol/openid-connect/token", form) as response:
        token = json.load(response)["access_token"]

    def call(method, path, body=None):
        request = urllib.request.Request(f"{base}/admin/realms/novavolt{path}", method=method,
                                         data=None if body is None else json.dumps(body).encode(),
                                         headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"})
        with urllib.request.urlopen(request) as response:
            data = response.read().decode()
            return json.loads(data) if data else None

    wanted = json.loads((ROOT / "deploy/keycloak/realm-novavolt.json").read_text(encoding="utf-8"))
    scopes = {s["name"] for s in call("GET", "/client-scopes")}
    for scope in wanted.get("clientScopes", []):
        if scope["name"] not in scopes:
            call("POST", "/client-scopes", scope)
            print("added client scope", scope["name"])

    roles = {r["name"] for r in call("GET", "/roles?max=1000")}
    clients = {c["clientId"] for c in call("GET", "/clients?max=1000")}
    users = {u["username"] for u in call("GET", "/users?max=1000")}
    new_clients = [c for c in wanted.get("clients", []) if c["clientId"] not in clients]
    result = call("POST", "/partialImport", {
        "ifResourceExists": "SKIP",
        "roles": {"realm": [r for r in wanted["roles"]["realm"] if r["name"] not in roles]},
        "clients": new_clients,
        "users": [u for u in wanted.get("users", []) if u["username"] not in users],
    })
    for item in result.get("results", []):
        print(item["action"].lower(), item["resourceType"].lower(), item["resourceName"])

    # partialImport tạo client nhưng không tạo user service account; bật lại cờ để Keycloak tạo.
    for client in new_clients:
        if client.get("serviceAccountsEnabled"):
            stored = call("GET", f"/clients?clientId={urllib.parse.quote(client['clientId'])}")[0]
            for flag in (False, True):
                stored["serviceAccountsEnabled"] = flag
                call("PUT", f"/clients/{stored['id']}", stored)
            print("service account", call("GET", f"/clients/{stored['id']}/service-account-user")["username"])
    print(f"done: {result.get('added', 0)} added")


if __name__ == "__main__":
    sys.exit(main())
