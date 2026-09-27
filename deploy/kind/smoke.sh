#!/usr/bin/env bash
# Smoke M13: cài Helm chart deploy/helm/novavolt lên cluster kind, dùng hạ tầng đang chạy trong docker compose.
#
# Chart không chứa hạ tầng, nên cluster mượn SQL Server, TimescaleDB, RabbitMQ, Keycloak, EMQX của compose: node kind
# được nối vào it-net và dmz-net; RabbitMQ/EMQX lộ vào cluster bằng Service + EndpointSlice trỏ IP container. Secret
# dựng từ .env qua file tạm (xoá ngay), không in giá trị nào.
#
# Cần: compose đang chạy (make up + execution/projection/ingestion), kind, kubectl, helm, image novavolt/*:dev.
# Chạy:  bash deploy/kind/smoke.sh          Dọn:  kind delete cluster --name novavolt
set -euo pipefail
cd "$(dirname "$0")/../.."
CLUSTER=novavolt
CTX="kind-$CLUSTER"
RELEASE=novavolt
IMAGE=kindest/node:v1.35.0
set -a; . ./.env; set +a

kind get clusters | grep -qx "$CLUSTER" || kind create cluster --name "$CLUSTER" --image "$IMAGE" --wait 180s
NODE="$CLUSTER-control-plane"
for net in novavolt-mes_it-net novavolt-mes_dmz-net; do
  docker network connect "$net" "$NODE" 2>/dev/null || true
done
ip() { docker inspect -f "{{(index .NetworkSettings.Networks \"$2\").IPAddress}}" "$1"; }
MSSQL=$(ip nvm-mssql novavolt-mes_it-net)
PG=$(ip nvm-timescale novavolt-mes_it-net)
RABBIT=$(ip nvm-rabbitmq novavolt-mes_it-net)
KEYCLOAK=$(ip nvm-keycloak novavolt-mes_it-net)
EMQX=$(ip nvm-emqx novavolt-mes_dmz-net)

# Values sinh từ solution.yaml; image cùng tag version của solution.
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT
dotnet run --project tools/solution-cli/Nvm.SolutionCli -- generate --mode distributed --out "$OUT" >/dev/null
TAG=$(sed -n 's/^  version: "\(.*\)"/\1/p' "$OUT/values.yaml" | head -1)
for image in nvm-execution nvm-projection nvm-ingestion nvm-edge-gateway; do
  docker tag "novavolt/$image:dev" "novavolt/$image:$TAG"
  kind load docker-image --name "$CLUSTER" "novavolt/$image:$TAG" >/dev/null
done

external() {  # $1 tên service, $2 IP, $3 tên cổng, $4 cổng
  kubectl --context "$CTX" apply -f - >/dev/null <<EOF
apiVersion: v1
kind: Service
metadata: { name: $1 }
spec:
  ports: [{ name: $3, port: $4 }]
---
apiVersion: discovery.k8s.io/v1
kind: EndpointSlice
metadata:
  name: $1-compose
  labels: { kubernetes.io/service-name: $1 }
addressType: IPv4
ports: [{ name: $3, port: $4 }]
endpoints: [{ addresses: ["$2"] }]
EOF
}
external rabbitmq "$RABBIT" amqp 5672
external emqx "$EMQX" mqtt 1883

secret() {  # $1 tên secret; stdin: dòng KEY=VALUE
  local file="$OUT/$1.env"
  cat > "$file"
  kubectl --context "$CTX" create secret generic "$1" --from-env-file="$file" --dry-run=client -o yaml \
    | kubectl --context "$CTX" apply -f - >/dev/null
  rm -f "$file"
}
SQL_OPTS="Encrypt=True;TrustServerCertificate=True;Connect Timeout=3"
secret "$RELEASE-execution" <<EOF
NVM_POM__ConnectionString=Host=$PG;Port=5432;Database=$NVM_POSTGRES_DB;Username=nvm_pom;Password=$NVM_POM_PASSWORD;GSS Encryption Mode=Disable
NVM_POM__Authority=http://localhost:$NVM_PORT_KEYCLOAK/realms/novavolt
NVM_POM__MetadataAddress=http://$KEYCLOAK:8080/realms/novavolt/.well-known/openid-configuration
NVM_COMMANDS__ConnectionString=Server=$MSSQL;Database=NovaVolt;User Id=nvm_app;Password=$NVM_MSSQL_APP_PASSWORD;$SQL_OPTS
NVM_RABBITMQ_USER=$NVM_RABBITMQ_USER
NVM_RABBITMQ_PASSWORD=$NVM_RABBITMQ_PASSWORD
EOF
secret "$RELEASE-execution-commands-migrate" <<EOF
NVM_COMMANDS__MigrationConnectionString=Server=$MSSQL;Database=NovaVolt;User Id=sa;Password=$NVM_MSSQL_SA_PASSWORD;$SQL_OPTS
EOF
secret "$RELEASE-execution-pom-migrate" <<EOF
NVM_POM__MigrationConnectionString=Host=$PG;Port=5432;Database=$NVM_POSTGRES_DB;Username=$NVM_POSTGRES_USER;Password=$NVM_POSTGRES_PASSWORD;GSS Encryption Mode=Disable
EOF
secret "$RELEASE-projection" <<EOF
NVM_PROJECTIONS__ConnectionString=Host=$PG;Database=$NVM_POSTGRES_DB;Username=nvm_projection;Password=$NVM_PROJECTION_PG_PASSWORD;GSS Encryption Mode=Disable
NVM_PROJECTIONS__SqlConnectionString=Server=$MSSQL;Database=NovaVolt;User Id=nvm_projection;Password=$NVM_PROJECTION_SQL_PASSWORD;$SQL_OPTS
NVM_RABBITMQ_USER=$NVM_RABBITMQ_USER
NVM_RABBITMQ_PASSWORD=$NVM_RABBITMQ_PASSWORD
EOF
secret "$RELEASE-projection-migrate" <<EOF
NVM_PROJECTIONS__MigrationConnectionString=Host=$PG;Database=$NVM_POSTGRES_DB;Username=$NVM_POSTGRES_USER;Password=$NVM_POSTGRES_PASSWORD;GSS Encryption Mode=Disable
NVM_PROJECTIONS__SqlMigrationConnectionString=Server=$MSSQL;Database=NovaVolt;User Id=sa;Password=$NVM_MSSQL_SA_PASSWORD;$SQL_OPTS
NVM_PROJECTION_PG_PASSWORD=$NVM_PROJECTION_PG_PASSWORD
NVM_PROJECTION_SQL_PASSWORD=$NVM_PROJECTION_SQL_PASSWORD
EOF
secret "$RELEASE-ingestion" <<EOF
NVM_INGEST__ConnectionString=Host=$PG;Port=5432;Database=$NVM_POSTGRES_DB;Username=$NVM_POSTGRES_USER;Password=$NVM_POSTGRES_PASSWORD;Application Name=Nvm.Ingestion
NVM_INGEST__BusUsername=$NVM_RABBITMQ_USER
NVM_INGEST__BusPassword=$NVM_RABBITMQ_PASSWORD
EOF
secret "$RELEASE-ingestion-migrate" <<EOF
NVM_INGEST__ConnectionString=Host=$PG;Port=5432;Database=$NVM_POSTGRES_DB;Username=$NVM_POSTGRES_USER;Password=$NVM_POSTGRES_PASSWORD;Application Name=Nvm.Ingestion.Migrate
EOF
# Client id MQTT riêng: trùng với edge-gateway của compose thì EMQX cho hai phiên giành nhau mỗi 2 s (đã gặp).
secret "$RELEASE-edge-gateway" <<EOF
NVM_EDGE__SeedDirectory=seed
NVM_EDGE__ClientId=nvm-edge-gateway-$CLUSTER
EOF

# Development: realm Keycloak của máy dev phát token qua http; Production đòi https.
helm upgrade --install "$RELEASE" deploy/helm/novavolt --kube-context "$CTX" -f "$OUT/values.yaml" \
  --set config.execution.ASPNETCORE_ENVIRONMENT=Development --wait --timeout 5m
kubectl --context "$CTX" get deploy,pods -o wide

# Smoke: readiness của App Execution qua port-forward (kiểm cả POM lẫn command store).
kubectl --context "$CTX" port-forward svc/execution 18080:8080 >/dev/null 2>&1 &
PF=$!
trap 'kill $PF 2>/dev/null; rm -rf "$OUT"' EXIT
for _ in $(seq 1 20); do
  code=$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18080/health/ready || true)
  [ "$code" = "200" ] && break
  sleep 1
done
echo "execution /health/ready via cluster: $code"
[ "$code" = "200" ]
