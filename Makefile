# NovaVolt MES — task runner
#
# Mọi lệnh hay dùng gom vào đây để không ai phải nhớ cờ dòng lệnh.
# `make ci` chạy đúng chuỗi mà GitHub Actions sẽ chạy — một bộ kiểm tra, hai nơi.

# GNU Make trên Windows chọn shell theo kinh nghiệm: lệnh trông đơn giản thì gọi thẳng
# .exe, lệnh có ký tự đặc biệt thì mới gọi sh. Hệ quả là cùng một Makefile chạy khác nhau
# tuỳ dòng. Ghi rõ SHELL để hành vi giống nhau mọi lúc, mọi nền tảng.
# `sh` luôn có: Windows lấy từ Git, Linux/macOS có sẵn.
SHELL := sh
.SHELLFLAGS := -c

SOLUTION := NovaVolt.Mes.slnx
COMPOSE  := docker compose

# `down` phải nêu đủ profile, nếu không container của profile không active sẽ bị bỏ lại.
ALL_PROFILES := --profile probe --profile init --profile obs --profile tools --profile sim --profile ingestion --profile execution --profile load

# Đọc RIÊNG một biến từ .env thay vì `include .env`.
# `include` nạp mọi biến vào make — kể cả mật khẩu — và một khoá trùng tên với biến
# đặc biệt của make (SHELL, MAKEFLAGS…) sẽ phá cả file mà không báo lý do.
BACKUP_DIR := $(shell grep -E '^NVM_BACKUP_DIR=' .env 2>/dev/null | cut -d= -f2-)

.DEFAULT_GOAL := help
.PHONY: help up up-obs down down-v reset ps logs net-check grafana-net-check dmz-shell build test ci hooks format format-check clean backup secret-check rotation-preflight rabbitmq-durability-check bus-fanout bus-dlq bus-chaos sim-up sim-down sim-logs sim-report sim-net-check edge-up edge-down edge-logs edge-net-check buffer-crash ingestion-up ingestion-down ingestion-logs ingestion-migrate file-drop-race-probe telemetry-policy-lab rollup-refresh-wide telemetry-backfill compression-report rollup-bench rollup-bench-line rollup-reconcile calendar-lab outage-lab backpressure-lab load load-session-check load-net-check reconcile

help:
	@echo "NovaVolt MES"
	@echo ""
	@echo "  Ha tang"
	@echo "    make up            Khoi dong ha tang + chay init, cho den khi tat ca healthy"
	@echo "    make up-obs        Nhu tren, kem Grafana va role PostgreSQL read-only"
	@echo "    make down          Dung, GIU nguyen du lieu"
	@echo "    make down-v        Dung va XOA volume - mat sach du lieu"
	@echo "    make reset         down-v roi up lai tu dau"
	@echo "    make ps            Trang thai container"
	@echo "    make logs          Theo doi log"
	@echo "    make net-check     Kiem ranh gioi OT/IT (K11) - 9 phep do"
	@echo "    make rabbitmq-durability-check  Message durable co song qua recreate khong"
	@echo "    make grafana-net-check Kiem Grafana toi TimescaleDB, khong toi EMQX"
	@echo "    make dmz-shell     Mo shell trong dmz-net de dung MQTT"
	@echo ""
	@echo "  Simulator (can 'make up' truoc)"
	@echo "    make sim-up        Build va chay simulator trong ot-net, fault BAT"
	@echo "    make sim-down      Dung simulator"
	@echo "    make sim-logs      Theo doi log simulator"
	@echo "    make sim-report    In bao cao run - ve trai cua phep doi chieu D1"
	@echo "    make sim-net-check Kiem simulator chi o ot-net, toi EMQX nhung khong toi RabbitMQ"
	@echo ""
	@echo "  Edge gateway"
	@echo "    make edge-up       Build va chay gateway trong dmz-net"
	@echo "    make edge-down     Dung gateway"
	@echo "    make edge-logs     Theo doi decoded/buffered/forwarded"
	@echo "    make edge-net-check Kiem gateway chi o dmz-net, toi EMQX nhung khong toi RabbitMQ"
	@echo "    make buffer-crash  200 vong kill -9 + reopen buffer (ROUNDS de chay nhanh luc dev)"
	@echo ""
	@echo "  Ingestion"
	@echo "    make ingestion-up  Migrate, build va chay bridge tren dmz-net + it-net"
	@echo "    make ingestion-down Dung ingestion"
	@echo "    make ingestion-logs Theo doi batch inserted/duplicate"
	@echo "    make ingestion-migrate Chay rieng migration job PostgreSQL"
	@echo "    make execution-prepare-poc Tao schema va fixture Equipment M4"
	@echo "    make execution-prepare-operator-fixture Seed 1000 unit/site va WIP M4"
	@echo "    make execution-up     Chay POM Execution (sau seed)"
	@echo "    make telemetry-policy-lab Do retention 500 ngay va gia ghi vao chunk da nen"
	@echo "    make rollup-refresh-wide Refresh rollup trong mot cua so dong, co gioi han"
	@echo "    make telemetry-backfill Sinh lich su tu dung duong cong simulator va binary COPY"
	@echo "    make compression-report D1/M3: do nen o hai cardinality tren chunk that"
	@echo "    make rollup-bench   D2/M3: do chi doc, muc kenh va may tren hai cua so"
	@echo "    make rollup-bench-line Do chi doc, muc line F1 1.000 kenh (evidence)"
	@echo "    make rollup-bench-prepare Refresh parent/child va nen fixture (co ghi)"
	@echo "    make rollup-reconcile D5/M3: dem mau den muon ma rollup con thieu"
	@echo "    make calendar-lab  C14/M3: do CAST(date) sai voi lich san xuat bao nhieu"
	@echo "    make outage-lab    D3 fail-closed: tat backend 2 phut, assert row delta = 0"
	@echo "    make backpressure-lab  Lab #3: nap 30 phut roi xa 2 lan, A/B rate limit"
	@echo ""
	@echo "  Do tai (D2)"
	@echo "    make load          D2/M2 tren 1.000 kenh: source = gateway = DB exact, receiver theo kip"
	@echo "    NVM_LOAD_ENFORCE_N1=1 make load    Nghiem thu nang luc: ep N1 5.000 msg/s va p95 <5s"
	@echo "                                       qualification o M9, requalification o M13 (ADR-031)"
	@echo "    NVM_SEED_DIR=seed make load        Chay D2 tren topology demo 8 kenh"
	@echo "    make load-session-check Kiem 60 giay: mot node, mot session, khong sequence gap/rebirth"
	@echo "    make load-net-check Kiem harness chi o ot-net, toi EMQX nhung khong toi RabbitMQ"
	@echo "    make reconcile     D1: doi chieu phep do logic voi row trong DB (DURATION=3600)"
	@echo ""
	@echo "  Code"
	@echo "    make build         Build solution"
	@echo "    make test          Chay toan bo test cua solution"
	@echo "    make ci            Chay dung chuoi kiem tra cua CI"
	@echo "    make rotation-preflight Kiem 45 negative control cua rotation verifier"
	@echo "    make hooks         Bat pre-commit hook cho repo nay"
	@echo "    make format        Tu dong sua format theo .editorconfig"
	@echo "    make format-check  Kiem format, khong sua"
	@echo "    make clean         Xoa thu muc artifacts"
	@echo ""
	@echo "  Bus (can 'make up' truoc)"
	@echo "    make bus-fanout    D1: 1 publish -> 2 queue doc lap cung nhan"
	@echo "    make bus-dlq       D2: consumer loi 5 lan -> message vao _error"
	@echo "    make bus-chaos     D4: tat broker giua luc publish, DEM so event mat"
	@echo ""
	@echo "  Khac"
	@echo "    make backup        git bundle toan bo repo sang NVM_BACKUP_DIR"

# Ranh gioi mang la rang buoc CUNG (AGENTS.md K11), nhung tu M0 toi M1 no chi duoc
# kiem mot lan bang tay - va phep kiem do bo lot duong vong qua port host suot hai
# milestone. Tu day no la mot lenh chay lai duoc.
#
# CO Y khong nam trong `make ci`: ci phai chay duoc tren may khong bat Docker.
# Doi lai, phai chay `make net-check` moi khi dung toi docker-compose.yml.
net-check: .env
	@sh scripts/net-check.sh

# K13 o tang runtime. .env.example duoc commit, nen moi mat khau trong no la cong khai; copy
# nguyen xi sang .env de credential that cua stack dang chay bang credential ai cung doc duoc.
#
# CO Y khong nam trong `make up`: xoay mat khau sau khi volume da tao doi hoi `down -v`, va do
# la quyet dinh cua nguoi van hanh chu khong phai cua mot task runner. `up` chi CANH BAO.
secret-check:
	@sh scripts/secret-check.sh

# N-M3-4. Hoi "message co con khong" chu khong hoi "co ghim hostname chua": cau thu hai luon tra loi
# duoc bang cach doc file, cau thu nhat chi mot lan recreate that moi tra loi duoc.
rabbitmq-durability-check: .env
	@sh scripts/rabbitmq-durability-check.sh

grafana-net-check: .env
	@sh scripts/grafana-net-check.sh

# Duong HOP LE de mot con nguoi cham toi MQTT sau khi EMQX bo `ports:`.
dmz-shell: .env
	@echo "Dang o trong dmz-net. Broker la 'emqx'. Vi du:"
	@echo "    mosquitto_sub -h emqx -t '#' -v"
	@echo "    wget -qO- http://emqx:18083/status"
	@$(COMPOSE) --profile tools run --rm dmz-shell

# Chan som voi thong bao ro rang. Thieu .env thi docker compose bao loi kho hieu
# ve bien khong resolve duoc, chu khong noi la thieu file.
.env:
	@echo "Chua co .env. Chay lenh nay truoc:"
	@echo "    cp .env.example .env"
	@exit 1

# HAI BUOC, khong phai mot.
#
# `docker compose up --wait` coi moi container DA THOAT la that bai, ke ca khi thoat 0,
# nen mssql-init va minio-init nam trong profile `init` va khong duoc goi o buoc mot.
# Neu chi chay buoc mot thi may sach se co SQL Server nhung KHONG co database NovaVolt,
# va D1 truot theo kieu rat kho doan. Xem C07.1 trong docs/plans/M0-bootstrap.md.
up: .env
	@start=$$(date +%s); \
	$(COMPOSE) up -d --wait || exit 1; \
	$(COMPOSE) run --rm mssql-init || exit 1; \
	$(COMPOSE) run --rm minio-init || exit 1; \
	echo ""; \
	echo "All healthy in $$(($$(date +%s)-start))s"; \
	sh scripts/secret-check.sh || true

# M3/C13 bat Grafana som de nhin duong cong formation. Migration phai chay truoc
# grant: role read-only khong duoc tu suy ra quyen tren table da ton tai.
up-obs: .env
	@start=$$(date +%s); \
	$(COMPOSE) --profile obs up -d --wait || exit 1; \
	$(COMPOSE) --profile ingestion run --rm ingestion-migrate || exit 1; \
	$(COMPOSE) run --rm grafana-db-init || exit 1; \
	$(COMPOSE) run --rm mssql-init || exit 1; \
	$(COMPOSE) run --rm minio-init || exit 1; \
	echo ""; \
	echo "All healthy in $$(($$(date +%s)-start))s"; \
	sh scripts/secret-check.sh || true

down:
	$(COMPOSE) $(ALL_PROFILES) down

down-v:
	$(COMPOSE) $(ALL_PROFILES) down -v

reset: down-v up

ps:
	$(COMPOSE) ps -a

logs:
	$(COMPOSE) logs -f --tail 100

build:
	dotnet build $(SOLUTION) --nologo

# .NET 10 bo VSTest cho Microsoft.Testing.Platform, va xunit v3 chay tren MTP.
# MTP mode duoc bat trong global.json va doi cu phap: phai la --solution.
test:
	dotnet test --solution $(SOLUTION)

# Dung THU TU va DUNG CO ma GitHub Actions se dung, khong phai mot bien the khac.
# Neu hai ben lech nhau thi CI khong con la thu du doan duoc, va nguoi ta se hoc
# cach bo qua no.
#
# restore -> format -> build -> test la co y: format chay TRUOC build de mot loi
# thut le khong phai cho het mot lan build Release moi lo ra.
ci:
	$(MAKE) rotation-preflight
	dotnet restore $(SOLUTION)
	dotnet format $(SOLUTION) --verify-no-changes --no-restore
	dotnet build $(SOLUTION) -c Release --no-restore --nologo
	dotnet test --solution $(SOLUTION) -c Release --no-build
	$(MAKE) buffer-crash

rotation-preflight:
	sh scripts/rotate-env-preflight-test.sh

# Hook KHONG tu cai khi clone — Git bo qua .git/hooks tu repo vi ly do bao mat.
# core.hooksPath la cach chinh thuc de tro sang thu muc duoc version hoa.
hooks:
	git config core.hooksPath .githooks
	@echo "pre-commit hook da bat (core.hooksPath = .githooks)"

format:
	dotnet format $(SOLUTION)

format-check:
	dotnet format $(SOLUTION) --verify-no-changes

clean:
	dotnet clean $(SOLUTION) --nologo
	rm -rf artifacts

# Chay cuoi moi buoi lam viec. Chua co remote nen day la ban sao duy nhat ngoai o D.
# `bundle verify` la phan quan trong: mot ban backup chua bao gio kiem lai thi khong
# phai backup, chi la mot file.
backup:
	@test -n "$(BACKUP_DIR)" || { echo "NVM_BACKUP_DIR chua duoc dat trong .env"; exit 1; }
	@mkdir -p "$(BACKUP_DIR)"
	@git bundle create "$(BACKUP_DIR)/novavolt-mes.bundle" --all
	@git bundle verify "$(BACKUP_DIR)/novavolt-mes.bundle" >/dev/null
	@echo "Backup OK: $(BACKUP_DIR)/novavolt-mes.bundle"

# ─────────────────────────────────────────────────────────
# Simulator — nguon du lieu thiet bi cua M2
#
# Profile rieng: `make up` thuong KHONG keo no len. Ha tang va nguon tai la hai
# quyet dinh khac nhau, va gop lai thi moi lan khoi dong ha tang de doc mot cai gi
# do lai kem theo mot dong message chay nen.
#
# Fault BAT trong docker-compose.yml, TAT trong code. Xem C06.
# ─────────────────────────────────────────────────────────
sim-up: .env
	@$(COMPOSE) --profile sim up -d --build simulator
	@sh scripts/sim-net-check.sh
	@echo ""
	@echo "Simulator dang chay trong ot-net. Theo doi: make sim-logs"

sim-down:
	@$(COMPOSE) --profile sim rm -sf simulator

sim-logs:
	@$(COMPOSE) --profile sim logs -f --tail 100 simulator

# Ve TRAI cua phep doi chieu D1. Doc tu trong container vi bao cao nam tren volume,
# khong nam tren o dia cua host.
sim-report:
	@docker exec nvm-simulator cat /var/lib/nvm-simulator/simulator-run.json

sim-net-check:
	@sh scripts/sim-net-check.sh

# ─────────────────────────────────────────────────────────
# Edge gateway — conduit duy nhat tu MQTT sang ingestion
# ─────────────────────────────────────────────────────────
edge-up: .env
	@$(COMPOSE) up -d --build edge-gateway
	@sh scripts/edge-net-check.sh
	@echo ""
	@echo "Edge gateway dang chay trong dmz-net. Theo doi: make edge-logs"

edge-down:
	@$(COMPOSE) rm -sf edge-gateway

edge-logs:
	@$(COMPOSE) logs -f --tail 100 edge-gateway

edge-net-check:
	@sh scripts/edge-net-check.sh

buffer-crash:
	@$(COMPOSE) build edge-gateway
	@ROUNDS=$${ROUNDS:-200} sh scripts/buffer-crash.sh

# ─────────────────────────────────────────────────────────
# Ingestion — migration job tách khỏi app startup (scope.md §8.4)
# ─────────────────────────────────────────────────────────
# C02 chỉ chuẩn bị fixture Equipment; startup runtime không giữ credential migration.
.PHONY: execution-up execution-prepare-poc execution-prepare-operator-fixture
execution-prepare-poc: .env
	@$(COMPOSE) --profile execution build execution
	@$(COMPOSE) --profile execution run --rm --no-deps execution-prepare-poc

execution-up: .env
	@$(COMPOSE) --profile execution build execution
	@$(COMPOSE) --profile execution up -d --wait --no-deps execution

execution-prepare-operator-fixture: .env
	@$(COMPOSE) --profile execution build execution
	@$(COMPOSE) --profile execution run --rm --no-deps execution-prepare-poc --prepare-operator-fixture

ingestion-up: .env
	@$(COMPOSE) --profile ingestion build ingestion
	@$(COMPOSE) --profile ingestion run --rm ingestion-migrate
	@$(COMPOSE) --profile ingestion up -d --wait ingestion
	@echo "Ingestion healthy tren dmz-net + it-net. Theo doi: make ingestion-logs"

# Hai probe cua vong audit 7, chay lai tren image dang chay (ADR-035 §Evidence).
# CANH BAO: xoa sach inbox/processed/rejected va tat MinIO mot lat. Lab, khong phai lenh van hanh.
file-drop-race-probe:
	@sh scripts/file-drop-race-probe.sh $${PROBE:-all}

ingestion-down:
	@$(COMPOSE) --profile ingestion rm -sf ingestion

ingestion-logs:
	@$(COMPOSE) --profile ingestion logs -f --tail 100 ingestion

ingestion-migrate: .env
	@$(COMPOSE) --profile ingestion build ingestion
	@$(COMPOSE) --profile ingestion run --rm ingestion-migrate

# C06 destructive lab: retention deliberately runs against the real local stack. The SQL creates
# its own labelled samples and never resets a volume; reruns append a new measurement set.
telemetry-policy-lab: ingestion-migrate
	@sh scripts/telemetry-policy-lab.sh

# C07 recovery path for readings older than the regular five-hour refresh window. Refreshes the
# channel parent before the machine child on the same closed range. Defaults to a paired, closed
# seven-day range; explicit UTC-minute boundaries must stay inside the 399-day raw horizon and can
# cover at most 399 days.
rollup-refresh-wide: ingestion-migrate
	@sh scripts/rollup-refresh-wide.sh

# C08 deterministic historical dataset. The defaults are deliberately small; every benchmark calls
# the same target with explicit cardinality, interval and sample period so its evidence is rerunnable.
telemetry-backfill: ingestion-migrate
	@CHANNELS="$(CHANNELS)" DAYS="$(DAYS)" END_AT="$(END_AT)" \
	  SAMPLE_PERIOD_SECONDS="$(SAMPLE_PERIOD_SECONDS)" DRIFTED_RATE="$(DRIFTED_RATE)" \
	  CLOCK_DRIFT_HOURS="$(CLOCK_DRIFT_HOURS)" BATCH_SIZE="$(BATCH_SIZE)" \
	  sh scripts/telemetry-backfill.sh

# C09 compares two exclusive five-day windows. The SQL first normalizes selected chunks through
# decompression, so every run measures the same compact rowstore before/after boundary.
compression-report: ingestion-migrate
	@sh scripts/compression-report.sh

# ADR-037: phép đo không chạy migration, refresh hoặc compression ngầm.
rollup-bench: .env
	@sh scripts/rollup-bench.sh

rollup-bench-line: .env
	@sh scripts/rollup-bench-line.sh

.PHONY: rollup-bench-prepare
rollup-bench-prepare: ingestion-migrate
	@sh scripts/rollup-bench.sh prepare

# C11 creates a uniquely labelled late-arrival probe, proves the regular five-hour refresh misses
# it, proves a parent-only repair still leaves the machine child stale, then repairs both levels.
rollup-reconcile: ingestion-migrate
	@sh scripts/rollup-reconcile.sh

# C14 reads the pinned C10 fixture and compares the SQL date cast with the domain calendar.
# DE1 rows are synthetic, one per elapsed minute, because the database fixture belongs to NV1.
calendar-lab: ingestion-migrate
	@sh scripts/calendar-lab.sh

# D2. Harness chay TRONG ot-net; lag doc tu ingestion o dmz-net qua dmz-shell.
# `make load` mac dinh OFFER 5.100 msg/s. Nguong DoD van la 5.000 va nam trong script, khong
# phai o day: mot nguon phat dung bang nguong chi dat duoc no neu khong bao gio vap, vi mot
# mili-giay mat vi stall la mot mili-giay khong lay lai duoc. Offer cao hon mot chut la cach phep
# do noi ve duong ong thay vi noi ve dung cu do.
#
# Hai ve cua phep do o hai mang khac nhau CO Y: harness khong duoc nhin thay ingestion,
# vi thiet bi that cung khong nhin thay (K11).
#
# Topology mac dinh cua target nay la seed-load (1.000 kenh), KHONG phai seed demo 8 kenh.
# scope.md §9/M2 cam ket bai test/load chay 1.000 kenh, va mot cam ket chi duoc giu bang thu
# chay mac dinh: mot co opt-in thi lan nao quen la lan do D2 do mot nha may khac. Demo va
# docker-compose van mac dinh `seed` — xem NVM_EDGE__SeedDirectory. Doi co chu dich thi van duoc:
#     NVM_SEED_DIR=seed make load
load: .env
	@NVM_SEED_DIR=$${NVM_SEED_DIR:-seed-load} RATE=$${RATE:-5100} DURATION=$${DURATION:-600} sh scripts/load-gate.sh

# R2 protocol gate. 500 msg/s nam duoi capacity da do cua receiver de co lap tinh hop le cua
# Sparkplug session; no KHONG thay the D2 o target `load` (5.000 msg/s trong 600 giay).
load-session-check: .env
	@RATE=$${RATE:-500} DURATION=$${DURATION:-60} sh scripts/load-session-check.sh

load-net-check: .env
	@sh scripts/load-net-check.sh

# D1 — menh de quan trong nhat cua M2. Mac dinh 1 gio dong ho THAT: nen thoi gian doi hanh vi
# duoi tai lien tuc, va do la thu dang do o day.
# `make reconcile DURATION=120` de thu duong ong truoc khi bo ra mot gio.
reconcile: .env
	@sh scripts/reconcile.sh

# Lab D3. Can `make up && make sim-up && make edge-up && make ingestion-up` truoc.
# WARMUP/OUTAGE/DRAIN_BUDGET de chay nhanh luc dev; mac dinh la con so cua DoD.
outage-lab: .env
	@sh scripts/backend-outage-lab.sh

# Lab pha hoai #3 (plan C10.3). Nap 30 phut o N1 voi ingestion TAT, chup buffer, roi xa CUNG mot
# backlog hai lan: mot lan khong rate limit, mot lan co. Bon con so di vao ADR-029.
# FILL/DRAIN_BUDGET de chay nhanh luc dev; mac dinh la con so cua plan.
backpressure-lab: .env
	@sh scripts/backpressure-lab.sh

# ─────────────────────────────────────────────────────────
# Bus — bang chung cua M1/C13
#
# Ba muc tieu nay KHONG nam trong `make ci`: chung can RabbitMQ dang chay va moi
# lan chay mat tu 30 giay den hon mot phut. Chung la lab, khong phai test.
#
# C18 chuyen ca ba sang duong THAT: ben publish la Nvm.Ingestion, kich bang mot file CSV tha
# vao inbox cua C15 — khong con dev endpoint nao. Ben nhan la tools/buslab/Nvm.BusLab, process
# rieng (dieu kien cua D1). Nvm.BusProbe da bi xoa.
# ─────────────────────────────────────────────────────────
BUS_LAB := scripts/bus-lab.sh

bus-fanout: .env
	@sh $(BUS_LAB) fanout

bus-dlq: .env
	@sh $(BUS_LAB) dlq

# Lab pha hoai. AGENTS.md §5.8.4: DOAN con so truoc khi chay, roi moi doi chieu.
# Con so cuoi cung phai duoc chep vao docs/benchmarks.md va ADR-022.
bus-chaos: .env
	@sh $(BUS_LAB) chaos
