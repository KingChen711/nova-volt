---
title: "Runbook — sự cố thường gặp và cách xử lý"
status: living
created: 2026-08-31
---

# Runbook

Một sự cố một mục. Mỗi mục có **triệu chứng**, **cơ chế thật** (đã kiểm, không phải giả định),
**thao tác**, và **phép kiểm chứng minh đã xong**.

`scope.md` §9/M13 đặt mục tiêu **10 mục**. Hiện có **2** — viết khi gặp thật, không viết trước cho đủ số.

| # | Mục | Ra ở |
|---|---|---|
| [1](#1-xoay-9-credential-tại-chỗ-không-mất-dữ-liệu) | Xoay 9 credential tại chỗ, không mất dữ liệu | M3 · K13 |
| [2](#2-file-nằm-trong-inbox-mà-không-được-đọc) | File nằm trong inbox mà không được đọc | M3 · C15 |

---

## 1. Xoay 9 credential tại chỗ, không mất dữ liệu

**Triệu chứng**: `make secret-check` exit 1 và liệt kê tên khoá, kèm dòng
*"were published in this repo's git history"*.

**Nguyên nhân**: `.env` trên máy này đang dùng đúng những giá trị từng nằm trong `.env.example` đã
commit. Chúng công khai trong mọi bản clone, mọi fork, mọi cache CI — và chúng **đang xác thực được**
với các container đang chạy. `scripts/burned-credentials.sha256` giữ hash của chúng để phép kiểm
không thể xanh giả sau khi `.env.example` đổi sang placeholder.

> [!danger] Ba điều cấm — owner chốt 2026-08-31 (`ADR-034` §O8/K13)
> 1. **Cấm `make down-v`** và mọi cách xoá volume. Nó xoá dataset 2,19 triệu row mà bằng chứng
>    D1/D2/D5 của M3 đang dựa vào, **và** xoá object trong bucket `raw-curve` đang nằm dưới object
>    lock COMPLIANCE 15 năm. Đổi mật khẩu bằng cách xoá dữ liệu không phải một thao tác vận hành.
> 2. **Cấm in secret ra output**: không `echo` giá trị, không `cat .env`, không `docker compose
>    config`, không `docker inspect` phần environment. Hai lệnh cuối in **toàn bộ** biến môi trường
>    đã resolve. Audit M3 đã rò rỉ credential **hai lần** đúng theo đường đó.
> 3. **Cấm dùng giả định "chỉ cần recreate container" cho mọi service.** Giả định cũ nói MinIO,
>    EMQX, Keycloak và Grafana đều đọc lại credential từ env. Nó **đúng với 2 trong 4**; mục
>    [1.1](#11-cơ-chế-thật-của-từng-service) là nguồn hiện hành và nói rõ hai cái nào.

---

### 1.1 Cơ chế thật của từng service

Bảng này là phần đắt nhất của mục này, vì **secret nằm ở đâu quyết định cách xoay nó**. Cột cuối nói
rõ mỗi dòng được biết bằng cách nào — `AGENTS.md` §1.3 và §3.6.

| # | Credential | Secret nằm ở đâu | Recreate container có xoay không | Kiểm bằng cách nào |
|---|---|---|---|---|
| 1 | `NVM_POSTGRES_PASSWORD` — role `nvm` | Volume `pgdata`, trong `pg_authid` | **KHÔNG** — `POSTGRES_PASSWORD` chỉ dùng lúc `initdb` | Tài liệu image + `deploy/postgres/init/01-schemas.sql` không đụng password |
| 2 | `NVM_GRAFANA_DB_PASSWORD` — role `nvm_grafana` | Volume `pgdata` | **CÓ** — `grafana-db-init` chạy `ALTER ROLE nvm_grafana WITH LOGIN PASSWORD` **vô điều kiện** ở mọi `make up-obs` | Đã đọc code: `docker-compose.yml:153` |
| 3 | `NVM_MSSQL_SA_PASSWORD` — login `sa` | Volume `mssqldata` | **KHÔNG** | Tài liệu Microsoft: đổi `sa` bằng `ALTER LOGIN`. Bước 3 dưới đúng dù giả định này sai |
| 4 | `NVM_MSSQL_APP_PASSWORD` — login `nvm_app` | Volume `mssqldata` | **KHÔNG** | Đã đọc code: `deploy/mssql/init/01-database.sql` chỉ `CREATE LOGIN` trong nhánh `IF NOT EXISTS` |
| 5 | `NVM_RABBITMQ_PASSWORD` — user `nvm` | Volume `rabbitmqdata`, thư mục theo **tên node**, và tên node nay **đã được ghim** | **KHÔNG** — dùng `rabbitmqctl change_password` | **Sửa 2026-09-01, xem cảnh báo ngay dưới** |
| 6 | `NVM_EMQX_PASSWORD` — Dashboard | Volume `emqxdata`, bảng mnesia `emqx_admin` | **KHÔNG** — `EMQX_NODE__NAME` được ghim nên node cũ giữ nguyên DB cũ | **Đo hôm nay**: `emqx_admin.DCD` / `.DCL` có mặt trong `/opt/emqx/data/mnesia/emqx@nvm-emqx/` |
| 7 | `NVM_MINIO_PASSWORD` — root | **Không nằm ở đâu cả** — chỉ trong env | **CÓ** | **Đo hôm nay**: `/data/.minio.sys/config/iam/` chỉ có `format.json`, không có user nào |
| 8 | `NVM_KEYCLOAK_PASSWORD` — admin | `/opt/keycloak/data/h2/keycloakdb.mv.db`, nằm trong **writable layer của container**, **không có volume** | **CÓ** — container mới ⇒ H2 rỗng ⇒ `KC_BOOTSTRAP_ADMIN_PASSWORD` bootstrap lại | **Đo hôm nay**: file H2 tồn tại trong container; `docker-compose.yml` cố ý không mount volume cho nó |
| 9 | `NVM_GRAFANA_ADMIN_PASSWORD` — admin | Volume `grafanadata`, trong `grafana.db` | **KHÔNG** | **Đo hôm nay**: `grafana.db` 1,7 MB trong volume; `grafana cli admin reset-admin-password` mở được DB **trong lúc server đang chạy** |

**Đọc bảng này thành ba nhóm:**

| Nhóm | Credential | Cách xoay |
|---|---|---|
| **A — sửa state trong service trước recreate** | 1, 3, 4, **5**, 6, 9 | Bước 2–7; service nào hỗ trợ thì xác thực bằng giá trị cũ |
| **B — chỉ cần `.env` mới rồi recreate** | 7, 8 | Bước 8 làm hết |
| **C — job init tự `ALTER` mỗi lần `up-obs`** | 2 | Bước 8 làm hết |

> [!danger] RabbitMQ đã chuyển từ nhóm B sang nhóm A ngày 2026-09-01, và lý do đáng đọc
> Bản đầu của bảng này ghi RabbitMQ ở nhóm B với cơ chế *"container mới là một node rỗng nên
> `RABBITMQ_DEFAULT_PASS` được áp lại"*. Câu đó **đúng về mặt mô tả và sai về mặt kết luận**: nó
> đúng là một cơ chế xoay được, và nó đúng là **mất sạch state của broker** mỗi lần.
>
> Đo được: volume chứa **5** thư mục `rabbit@<container-id>`, cái bị bỏ rơi lớn nhất giữ **4,1 MB**,
> node đang chạy báo **0 queue**. Suốt M2 và M3 không ai thấy vì chưa có gì durable sống đủ lâu để
> mất — một bus *at-least-once* mà state biến mất mỗi lần recreate thì không phải at-least-once.
>
> `docker-compose.yml` nay ghim `hostname: nvm-rabbitmq`. Hệ quả **bắt buộc phải nhớ**:
> `RABBITMQ_DEFAULT_PASS` chỉ chạy trên một node **chưa khởi tạo**, nên từ giờ nó **không còn** áp
> lại khi recreate. Xoay RabbitMQ mà vẫn theo cách cũ sẽ ghi giá trị mới vào `.env`, recreate, và
> broker vẫn nhận giá trị **cũ** — một lần xoay thất bại **im lặng**, trong khi `make secret-check`
> vẫn in `OK` vì nó chỉ đọc `.env`.
>
> Kiểm bằng `make rabbitmq-durability-check`: nó publish 5 message durable, recreate container, và
> đòi cả **số message** lẫn **tên node** không đổi. Đã chứng minh nó đỏ được — gỡ `hostname` ra thì
> node đi từ `rabbit@13565cdda431` sang `rabbit@f907182d29fb` và queue biến mất.

> [!warning] Vì sao nhóm A tồn tại, và vì sao bỏ qua nó tạo ra **xanh giả**
> `make secret-check` chỉ đọc `.env`. Nếu chỉ sửa `.env` rồi recreate, credential nhóm A **vẫn là giá
> trị cũ đã lộ** bên trong service, còn `secret-check` in `OK`. Lúc đó phép kiểm nói ngược với sự thật
> — đúng thứ nó được viết ra để chống. §1.4 vì vậy kiểm **từng credential bằng một lần đăng nhập
> thật**, không kiểm bằng `secret-check`.

---

### 1.2 Chuẩn bị

**Ràng buộc cho 9 giá trị mới** — sinh ngoài chat, không dán vào đây:

| Ràng buộc | Vì sao |
|---|---|
| Chỉ dùng `A–Z a–z 0–9 - _ . ~` | `"` và `\` phá JSON body của EMQX; `#` và khoảng trắng đầu/cuối phá cách đọc `.env` |
| Tối thiểu 20 ký tự, mỗi khoá **một giá trị khác nhau** | Dùng lại một giá trị cho hai service là một lần lộ thành hai |
| Có đủ chữ hoa, chữ thường, số | `nvm_app` được tạo với `CHECK_POLICY = ON`, SQL Server sẽ từ chối mật khẩu yếu |
| **Giữ 9 giá trị cũ trong tầm tay tới hết §1.4** | Nhóm A cần giá trị cũ để xác thực trước khi đổi và mọi giá trị cũ phải được thử từ chối |

**Thứ tự cố định, không đảo**: sửa `.env` **trước** (bước 1), rồi mới sửa trong service (bước 2–7),
rồi recreate (bước 8). Lý do: `.env` không được đọc cho tới khi có một lệnh `docker compose` chạy, nên
bước 1 hoàn toàn vô hại; còn để nó sau bước 8 thì phải recreate hai lần.

Từ bước 2 tới bước 8 các container đang chạy sẽ **log lỗi xác thực**. Đó là bình thường và biến mất sau
bước 8. Đừng chữa nó giữa chừng.

---

### 1.3 Hai đường: tay hay script

| | Đường tay ([§1.3.1](#131-thao-tác-bằng-tay)) | Đường script (`scripts/rotate-credentials.sh`) |
|---|---|---|
| Ai nghĩ ra 9 giá trị | **Người vận hành** | Script sinh ngẫu nhiên 32 ký tự, ghi thẳng vào `.env`, **không in ra đâu cả** |
| Người vận hành có biết mật khẩu của mình không | Có | **Không** cho tới khi tự mở `.env` ra xem |
| Thời gian | ~20 phút | ~40 giây |
| Học được gì | Cơ chế của từng service, bằng cơ bắp | Không nhiều |

**Mặc định là đường tay**, và không phải vì nó an toàn hơn — vì
[`AGENTS.md`](../AGENTS.md) §3.6 nói *"người giữ secret phải là người đặt secret"*, và
vì mỗi bước tay dạy một thứ về chỗ service đó cất mật khẩu.

**Đường script là lựa chọn của owner cho riêng máy local này**, chốt 2026-08-31: stack dev, mọi port
bind `127.0.0.1`, và owner đổi 20 phút thao tác lấy một lệnh. Script giữ đúng ba tính chất của
runbook — không in giá trị, không đụng volume, cập nhật `.env` cho từng credential ngay sau khi
service đó xoay xong nên không bao giờ rơi vào cảnh *"file nói một đằng, service nói một nẻo"*.

Giới hạn của script, ghi ra thay vì giấu: `sqlcmd -P` và `curl -d` của EMQX nhận giá trị qua **đối
số bên trong container**, nên nó thấy được trong `ps` của container đó vài mili-giây. Trên stack dev
local là đánh đổi chấp nhận được; trên production thì không, và lúc đó dùng đường tay.

```bash
sh scripts/rotate-credentials.sh
```

Rồi bỏ qua §1.3.1, đi thẳng tới [bước 8](#bước-8--recreate-một-lần-duy-nhất).

---

### 1.3.1 Thao tác bằng tay

#### Bước 1 — đặt 9 giá trị mới vào `.env`

Mở `.env` bằng editor, thay giá trị của đúng 9 khoá dưới đây. Không đọc file ra terminal.

```
NVM_POSTGRES_PASSWORD
NVM_GRAFANA_DB_PASSWORD
NVM_MSSQL_SA_PASSWORD
NVM_MSSQL_APP_PASSWORD
NVM_RABBITMQ_PASSWORD
NVM_EMQX_PASSWORD
NVM_MINIO_PASSWORD
NVM_KEYCLOAK_PASSWORD
NVM_GRAFANA_ADMIN_PASSWORD
```

#### Bước 2 — Postgres role `nvm`

`\password` là lệnh của psql: nó hỏi mật khẩu **hai lần, không hiện ký tự**, băm SCRAM ở phía client,
rồi mới gửi câu `ALTER`. Mật khẩu thô không đi qua dòng lệnh, không vào `~/.psql_history`, và không
vào log server.

```bash
docker exec -it nvm-timescale psql -U nvm -d novavolt
```

Trong prompt `novavolt=#`, gõ đúng hai dòng:

```
\password nvm
\q
```

Trên máy này hai role login là `nvm` và `nvm_grafana`, database là `novavolt` — lấy từ
`SELECT rolname FROM pg_roles WHERE rolcanlogin`, không đoán.

#### Bước 3 — MSSQL `sa`

Bỏ `-P` để sqlcmd **hỏi mật khẩu cũ** thay vì nhận nó từ dòng lệnh:

```bash
docker exec -it nvm-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C
```

Trong prompt `1>`, gõ:

```
ALTER LOGIN sa WITH PASSWORD = '<gia-tri-moi>' OLD_PASSWORD = '<gia-tri-cu>';
GO
```

`OLD_PASSWORD` là cố ý: nó bắt SQL Server xác minh lại người gõ, và là cách Microsoft khuyến nghị cho
việc tự đổi mật khẩu của chính mình.

#### Bước 4 — MSSQL login `nvm_app`

Trong **cùng phiên sqlcmd** của bước 3, sau khi bước 3 đã `GO` thành công:

```
ALTER LOGIN nvm_app WITH PASSWORD = '<gia-tri-moi>';
GO
:EXIT
```

`nvm_app` không có `OLD_PASSWORD` vì `sa` đang đổi mật khẩu của **người khác**, không phải của mình.

> [!note] Vì sao bước này phải làm tay, trong khi `nvm_grafana` thì không
> `deploy/mssql/init/01-database.sql` bọc `CREATE LOGIN nvm_app` trong `IF NOT EXISTS`, nên chạy lại
> `mssql-init` **không** xoay gì. `grafana-db-init` thì `ALTER ROLE` vô điều kiện. Hai job cùng vai
> trò, hai hành vi khác nhau — đây là **nợ**, không phải thiết kế: `01-database.sql` nên `ALTER LOGIN`
> ở nhánh `ELSE` để hai đường giống nhau. Ghi lại ở đây để lần sau không phải phát hiện lại.

#### Bước 5 — RabbitMQ user `nvm`

Node RabbitMQ đã ghim tên và giữ user trong volume, nên `RABBITMQ_DEFAULT_PASS` **không** chạy lại
khi recreate. Đọc mật khẩu mới không echo trên host, rồi đổi bằng CLI trong container:

```bash
docker exec -it nvm-rabbitmq sh -c 'stty -echo; printf "mat khau moi: "; read -r P; printf "\n"; stty echo; rabbitmqctl change_password nvm "$P"; unset P'
```

`rabbitmqctl` chỉ nhận mật khẩu bằng argument. Giá trị vì vậy xuất hiện vài mili-giây trong process
arguments **bên trong container**, nhưng không nằm trong shell history của host và không được in ra.
Đây là cùng giới hạn đã ghi cho đường script; production cần cơ chế secret rotation của môi trường
triển khai thay vì sao chép nguyên lệnh dev này.

#### Bước 6 — EMQX Dashboard

`emqx ctl` **không dùng được trong image này** — nó đi qua Erlang distribution và báo
`Node 'emqx@nvm-emqx' not responding to pings` dù broker chạy hoàn toàn bình thường. Điều này đã được
ghi trong `docker-compose.yml` (phần healthcheck của emqx) và kiểm lại 2026-08-31. Đường còn lại là
**REST API của chính dashboard**, gọi từ bên trong container.

Nạp script vào container:

```bash
docker exec -i nvm-emqx sh -c 'cat > /tmp/emqx-rotate.sh' <<'SCRIPT'
set -eu
API=http://127.0.0.1:18083/api/v5
printf 'EMQX dashboard user: '; read -r U
stty -echo
printf 'mat khau cu: '; read -r OLD; printf '\n'
printf 'mat khau moi: '; read -r NEW; printf '\n'
stty echo
TOKEN=$(curl -sS -X POST "$API/login" -H 'Content-Type: application/json' \
  -d "$(printf '{"username":"%s","password":"%s"}' "$U" "$OLD")" \
  | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
if [ -z "$TOKEN" ]; then printf 'login THAT BAI voi mat khau cu\n'; exit 1; fi
CODE=$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$API/users/$U/change_pwd" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "$(printf '{"old_pwd":"%s","new_pwd":"%s"}' "$OLD" "$NEW")")
printf 'change_pwd HTTP %s\n' "$CODE"
case "$CODE" in 200|204) printf 'OK\n' ;; *) exit 1 ;; esac
SCRIPT
```

Chạy nó (đây là chỗ gõ mật khẩu, `stty -echo` nên không hiện ký tự):

```bash
docker exec -it nvm-emqx sh /tmp/emqx-rotate.sh
```

Xoá script ngay sau khi xong:

```bash
docker exec nvm-emqx rm -f /tmp/emqx-rotate.sh
```

#### Bước 7 — Grafana admin

`--password-from-stdin` là cờ có sẵn của Grafana 13.2.0 và là lý do bước này không cần dán mật khẩu
vào dòng lệnh. Gõ mật khẩu mới rồi **Enter**, sau đó **Ctrl+D**.

```bash
docker exec -i nvm-grafana grafana cli --homepath /usr/share/grafana admin reset-admin-password --password-from-stdin
```

Lệnh này mở `grafana.db` trong lúc server đang chạy — đã kiểm 2026-08-31, không cần dừng Grafana.

#### Bước 8 — recreate, một lần duy nhất

```bash
make down && make up-obs && make ingestion-up
```

`make down` **không** có `-v`: nó xoá container, **giữ nguyên mọi volume**. Bước này áp
`MINIO_ROOT_PASSWORD` và `KC_BOOTSTRAP_ADMIN_PASSWORD` vào hai service vừa được tạo mới;
`grafana-db-init` `ALTER ROLE nvm_grafana`; `mssql-init` và `minio-init` chạy lại bằng giá trị mới;
`ingestion`, Grafana datasource và các RabbitMQ client nhận credential mới. RabbitMQ server **đã**
được đổi ở bước 5: `RABBITMQ_DEFAULT_PASS` không áp lại trên pinned node đã khởi tạo.

Nếu edge gateway đang chạy trước đó thì bật lại: `make edge-up`. Nó **không** dùng credential nào
trong 9 khoá — nó chỉ POST HTTP sang ingestion.

---

### 1.4 Kiểm chứng — 9 credential mới, K3 và 9 credential cũ

`make secret-check` **không** nằm trong 9 phép này. Nó chỉ đọc `.env`; chín phép dưới đây đăng nhập
thật vào chín thứ.

Đường script chạy cả **9 giá trị mới + K3 + 9 giá trị cũ** bằng một lệnh, với đúng file backup mà
`rotate-credentials.sh` vừa tạo:

```bash
sh scripts/rotate-verify.sh .env.rotate-backup.<timestamp>
```

Trước khi chạm Docker hay HTTP, verifier bắt buộc backup có đúng một giá trị không rỗng, không phải
`CHANGE_ME_*`, và khác giá trị hiện tại cho cả chín key. Nó còn băm từng giá trị cũ và buộc cặp
**digest + tên key** phải có trong `scripts/burned-credentials.sha256`; một file có chín giá trị bịa
khác `.env` không còn được tính là backup lịch sử. Không có backup, truyền `.env.example`, thiếu key,
key rỗng, key lặp, manifest thiếu hoặc digest nằm dưới sai key đều dừng với exit `2`. Ma trận thuần
**45 case** chạy bằng `make rotation-preflight`; nó không đọc `.env` thật và không cần stack.

Nếu đi đường thủ công, chạy **mỗi phép 1–9 hai lần bằng đúng cùng block lệnh**: lần đầu nhập giá trị
mới trong `.env`, lần sau nhập giá trị cũ từ backup đã được preflight ở trên. Không bỏ lượt cũ của
bất kỳ service nào; `9 new green` chỉ chứng minh service dùng được, còn `9 old red` mới chứng minh
rotation đã thu hồi toàn bộ credential bị lộ.

> [!warning] Phép 1 và 2 **phải** chạy từ một container khác, không phải `docker exec` vào chính
> `nvm-timescale`
> `pg_hba.conf` do `initdb` sinh ra có dòng `host all all 127.0.0.1/32 trust`. Nghĩa là mọi kết nối
> tới loopback **bên trong** container Postgres được nhận **không cần mật khẩu** — `docker exec ...
> psql -h 127.0.0.1` sẽ xanh kể cả khi mật khẩu sai hoàn toàn. Đã kiểm 2026-08-31. Chỉ đường đi qua
> mạng `it-net` mới rơi vào dòng `host all all all scram-sha-256`, và đó mới là đường mà phép kiểm
> này muốn đo. *(Port publish ra host đi qua gateway của Docker chứ không qua loopback của container,
> nên nó cũng đòi mật khẩu — dòng `trust` này không phải một lỗ hổng, nhưng nó là một cái bẫy đo
> lường.)*
>
> Tên network là `novavolt-mes_it-net` — lấy từ `docker inspect nvm-timescale` chứ không đoán theo
> tên thư mục.

```bash
# 1 · Postgres role nvm — psql hoi mat khau, khong hien ky tu
docker run --rm -it --network novavolt-mes_it-net timescale/timescaledb:2.29.2-pg17 \
  psql -h timescale -U nvm -d novavolt -W -c 'SELECT 1'
```

```bash
# 2 · Postgres role nvm_grafana — phai doc duoc ts_scoped, va KHONG doc duoc ts
docker run --rm -it --network novavolt-mes_it-net timescale/timescaledb:2.29.2-pg17 \
  psql -h timescale -U nvm_grafana -d novavolt -W \
  -c 'SELECT count(*) FROM ts_scoped.readable_site' \
  -c 'SELECT 1 FROM ts.telemetry_measurement LIMIT 1'
```

Phép 2 **đạt khi câu thứ hai đỏ** với `permission denied for schema ts`. Câu thứ nhất xanh chứng minh
credential đúng; câu thứ hai đỏ chứng minh K3 vẫn còn nguyên sau khi xoay.

```bash
# 3 · MSSQL sa — bo -P de sqlcmd hoi
docker exec -it nvm-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -Q 'SELECT 1'
```

```bash
# 4 · MSSQL nvm_app
docker exec -it nvm-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U nvm_app -d NovaVolt -C -Q 'SELECT 1'
```

```bash
# 5 · RabbitMQ — hoi thang broker, khong doan qua log
docker exec -it nvm-rabbitmq rabbitmqctl authenticate_user nvm
```

```bash
# 6 · EMQX — mat khau MOI phai 200
docker exec -it nvm-emqx sh -c 'stty -echo; printf "user: "; read -r U; printf "\nmat khau: "; read -r P; printf "\n"; stty echo; curl -s -o /dev/null -w "HTTP %{http_code}\n" -X POST http://127.0.0.1:18083/api/v5/login -H "Content-Type: application/json" -d "$(printf "{\"username\":\"%s\",\"password\":\"%s\"}" "$U" "$P")"'
```

Phép 6 trả `HTTP 200` ở lượt mới và `HTTP 401` ở lượt cũ.

```bash
# 7 · MinIO root
docker run --rm -it --network novavolt-mes_it-net --entrypoint sh \
  minio/mc:RELEASE.2025-08-13T08-35-41Z -c \
  'stty -echo; printf "root user: "; read -r U; printf "\nmat khau: "; read -r P; printf "\n"; stty echo; mc alias set probe http://minio:9000 "$U" "$P" && mc ls probe/raw-curve'
```

```bash
# 8 · Keycloak admin — lay token, in HTTP code chu khong in token
curl -s -o /dev/null -w 'HTTP %{http_code}\n' \
  -d 'grant_type=password' -d 'client_id=admin-cli' \
  --data-urlencode 'username=<admin-user>' --data-urlencode 'password=<gia-tri-moi>' \
  http://127.0.0.1:<NVM_PORT_KEYCLOAK>/realms/master/protocol/openid-connect/token
```

```bash
# 9 · Grafana admin
curl -s -o /dev/null -w 'HTTP %{http_code}\n' -u '<admin-user>' \
  http://127.0.0.1:<NVM_PORT_GRAFANA>/api/user
```

`-u '<admin-user>'` không kèm dấu `:` — curl sẽ **hỏi mật khẩu** thay vì nhận từ dòng lệnh.

Oracle của hai lượt — kiểm đúng lỗi, không chỉ nhìn exit code đỏ:

| # | Giá trị mới phải | Giá trị cũ phải |
|---|---|---|
| 1 · Postgres `nvm` | `SELECT 1` thành công | `password authentication failed` |
| 2 · Postgres `nvm_grafana` | đọc `ts_scoped` được; đọc thẳng `ts` bị từ chối bởi K3 | authentication thất bại trước cả hai query |
| 3 · MSSQL `sa` | `SELECT 1` thành công | `Login failed for user 'sa'` |
| 4 · MSSQL `nvm_app` | `SELECT 1` thành công trong `NovaVolt` | `Login failed for user 'nvm_app'` |
| 5 · RabbitMQ | `Success` | `Error: user authentication failed` |
| 6 · EMQX | `HTTP 200` | `HTTP 401` |
| 7 · MinIO | đặt alias và `mc ls` thành công | access denied / exit khác `0` |
| 8 · Keycloak | `HTTP 200` | `HTTP 401` |
| 9 · Grafana | `HTTP 200` | `HTTP 401` |

Sau 18 phép credential và phép K3 mới chạy ba kiểm tra hệ thống:

```bash
make secret-check       # phai in "OK"
make net-check          # 9/9
make grafana-net-check  # 3/3
```

**Xong khi**: **9/9 giá trị mới** đăng nhập được, **9/9 giá trị cũ** bị từ chối đúng oracle,
`nvm_grafana` vẫn không đọc thẳng `ts`, `secret-check` in `OK`, `net-check` 9/9,
`grafana-net-check` 3/3, và `docker ps` cho thấy mọi container `healthy`.

---

### 1.5 Nếu hỏng giữa chừng

| Triệu chứng | Nguyên nhân gần như chắc chắn | Xử lý |
|---|---|---|
| `nvm-mssql` không lên `healthy` sau bước 8 | Healthcheck dùng `MSSQL_SA_PASSWORD` từ env, nhưng bước 3 chưa chạy hoặc chạy sai | Chạy lại bước 3 với giá trị cũ. Container vẫn chạy dù healthcheck đỏ, nên vẫn `docker exec` vào được |
| `ingestion` restart liên tục | `.env` đã đổi nhưng bước 2 chưa chạy — Postgres vẫn giữ mật khẩu cũ | Chạy lại bước 2, rồi `make ingestion-up` |
| Grafana đăng nhập được bằng mật khẩu **cũ** sau bước 8 | Bước 7 chưa chạy. Recreate **không** xoay admin của Grafana | Chạy bước 7 |
| RabbitMQ chỉ nhận mật khẩu **cũ** sau bước 8 | Bước 5 bị bỏ qua; pinned node không áp lại `RABBITMQ_DEFAULT_PASS` | Chạy bước 5 bằng mật khẩu mới trong `.env`, rồi kiểm cả giá trị mới và cũ ở §1.4 |
| EMQX login 401 với **cả hai** giá trị | Bước 6 đổi thành một giá trị thứ ba do gõ nhầm | Không có đường phục hồi qua CLI trong image này. Dừng lại, hỏi trước khi làm gì tiếp — **đừng** xoá volume `emqxdata` |
| `secret-check` vẫn đỏ và nêu một khoá | Khoá đó trong `.env` còn là giá trị cũ, hoặc còn `CHANGE_ME_*` | Sửa đúng khoá đó trong `.env`, chạy lại từ bước tương ứng |

> [!important] Đây là bài học vận hành, không phải một thủ tục hành chính
> Điều đáng giữ lại sau khi chạy xong không phải chín mật khẩu mới. Nó là: **secret nằm ở đâu quyết
> định cách xoay nó**, và ba service trông giống nhau từ bên ngoài (`đọc env lúc start`) hoá ra chia
> làm hai nhóm ngược nhau khi nhìn vào chỗ chúng thật sự lưu dữ liệu. Một runbook chép lại lời hứa
> trong tài liệu sẽ hỏng đúng ở chỗ đó — im lặng, và kèm một phép kiểm màu xanh.


---

## 2. File nằm trong inbox mà không được đọc

**Triệu chứng**: export đã nằm trong inbox nhưng không có row nào vào database, không có gì trong
`processed/` hay `rejected/`, và không có lỗi nào trong log của `ingestion`. Sau vài phút xuất hiện:

```
File drop 'run-123.csv' has been in the inbox 00:05:12 and has not been read because it is not
published; the exporter either has not finished it or never renames its exports to
'run-123.csv.ready'
```

(EventId **2514**, mức Warning, **một dòng cho mỗi file** — không lặp lại mỗi lượt poll.)

### 2.1 Cơ chế thật

Adapter **chỉ** đọc file tên `*.csv.ready`. Đó là hợp đồng publish của `ADR-035`: producer ghi export
ra `<tên>.csv.partial`, đóng file, rồi **rename nguyên tử** thành `<tên>.csv.ready`. Chính phép rename
đó là lời tuyên bố "file đã xong".

Lý do không đọc thẳng `*.csv`: **đổi tên một file không đóng handle mà exporter đang giữ**. Trên NFS,
exporter vẫn ghi tiếp vào cùng inode sau khi ingestion đã rename file sang `.processing` — ingestion
lưu nửa run, rồi xoá phần đuôi cùng claim, và **không có lỗi ở đâu cả**. Readiness nằm trong chính
tên file chứ không ở một marker bên cạnh, vì hai file thì phải lấy bằng hai lần rename và producer
publish lại giữa hai lần đó làm người đọc lấy byte của export này dưới readiness của export khác.

Vì vậy triệu chứng ở trên là hợp đồng **đang làm đúng việc**: fail-closed. Một file không được đọc thì
tệ, nhưng một nửa run được lưu như thể là cả run thì tệ hơn — và không ai phát hiện ra.

### 2.2 Thao tác

Xác định exporter đang publish kiểu gì:

```bash
docker exec nvm-ingestion ls -la /var/lib/nvm-ingestion/inbox
```

| Thấy gì | Nghĩa là | Làm gì |
|---|---|---|
| `run-123.csv` | Exporter chưa được dạy hợp đồng | Sửa exporter, hoặc bọc bằng script: ghi ra `.csv.partial` rồi `mv` thành `.csv.ready`. **Không** `mv` file cũ bằng tay khi exporter vẫn đang mở nó |
| `run-123.csv.partial` đứng yên nhiều phút | Exporter chết giữa lúc ghi | Xoá file `.partial`, cho máy xuất lại. Nó chưa từng được đọc nên không có gì trùng lặp |
| `run-123.recovered-<guid>.csv.ready` | Ingestion đã crash lúc đang giữ claim, và **tự trả file về** lúc khởi động | Không phải sự cố. File sẽ được đọc ở lượt poll kế tiếp |
| `run-123.retry-<guid>.csv.ready`, rồi `retry2-`, `retry3-`… | Một lượt xử lý đã lỗi (thường là archive MinIO không tới được) và file được trả về để thử lại. **Con số là số vòng đã quay** — nó không dừng lại, vì `ADR-033` chọn chặn còn hơn bỏ bằng chứng | Đọc lỗi thật kèm EventId **2507** hoặc lỗi của archive rồi sửa nguyên nhân; file tự được thử lại. Số vòng tăng đều mà không ai sửa là dấu hiệu lỗi **vĩnh viễn**, không phải lỗi tạm |
| Không thấy gì trong inbox nhưng vẫn thiếu dữ liệu | File đã vào `processed/` hoặc `rejected/` | `docker exec nvm-ingestion ls /var/lib/nvm-ingestion/rejected` và đọc file `.error` đi kèm |

Thả một file bằng tay, đúng hợp đồng:

```bash
docker exec -i nvm-ingestion sh -c 'cat > /var/lib/nvm-ingestion/inbox/x.csv.partial && mv /var/lib/nvm-ingestion/inbox/x.csv.partial /var/lib/nvm-ingestion/inbox/x.csv.ready'
```

### 2.3 Phép kiểm chứng minh đã xong

Log khởi động phải nói **hợp đồng nào đang chạy** — đây là thứ kiểm được, khác với đọc file cấu hình:

```bash
docker logs nvm-ingestion 2>&1 | grep 'reads an export only once'
```

- EventId **2512** (Information): hợp đồng đang bật, kèm hậu tố thật.
- `NVM_INGEST__FileDrop__PublishedSuffix` rỗng hoặc không hợp lệ làm adapter bật bị từ chối khi
  khởi động. Sửa producer theo §2.2 và đặt hậu tố hợp lệ; runtime chỉ nhận file đã publish
  ([ADR-036](adr/ADR-036-file-drop-bat-buoc-publish.md)).

Sau khi sửa exporter: thả một export thật, rồi kiểm cả ba mặt trong cùng một lượt — row vào database,
object trong MinIO, và file trong `processed/`. Chỉ một trong ba là chưa chứng minh được gì.

<a id="port-windows-m4"></a>

## Mendix/Keycloak không bind được port trên Windows

Nếu không có listener nhưng lỗi chứa `AccessDenied` hoặc
`An attempt was made to access a socket in a way forbidden by its access permissions`, kiểm dải
Windows đang giữ. Ngày 2026-09-07 đã gặp dải `8071–8170` chứa cả Mendix 8080 và Keycloak 8081.

```powershell
Get-NetTCPConnection -State Listen | Where-Object LocalPort -in 7782,8080,8081,5081
netsh interface ipv4 show excludedportrange protocol=tcp
netsh interface ipv6 show excludedportrange protocol=tcp
docker port nvm-keycloak
```

Chạy lại các lệnh sau mỗi lần restart. Không suy từ `healthy` bên trong container rằng port host đã
được publish. Với port đang trống, thử bind TCP rồi đóng socket là phép kiểm trực tiếp; trước khi dùng
port thay thế cũng kiểm như vậy. Đổi port Keycloak/Mendix phải đồng bộ `.env`, issuer, redirect URI
trong client Keycloak và cấu hình OIDC của app; giữ H2 trước khi recreate Keycloak.

MCP 7782 có thể hiện process `System` vì dùng HTTP.sys. Khi đó đối chiếu request queue:

```powershell
netsh http show servicestate view=requestq verbose=yes
```

Queue `HTTP://LOCALHOST:7782/MCP/` thuộc `studiopro.exe`, POST MCP thành công nghĩa là port đang phục vụ
đúng app. GET MCP trả 405 không chứng minh server hỏng. Sau lần restart tiếp theo ngày 2026-09-09,
Mendix 8080 bind được, Keycloak 8081 trả discovery/token và MCP 7782 đọc module được; project giữ port cũ.

Nếu Equipment trả 401 sau khi OIDC login đã hoạt động, kiểm audience của access token mới: cần
`nvm-api`. Client `nvm-mendix` phải có mapper `nvm-api-audience` như realm JSON; cập nhật JSON không tự
thay đổi realm đã import. Áp dụng mapper vào runtime và đăng nhập lại. Không đưa token hoặc secret vào log.
