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
ALL_PROFILES := --profile probe --profile init --profile obs --profile tools --profile sim --profile ingestion --profile load

# Đọc RIÊNG một biến từ .env thay vì `include .env`.
# `include` nạp mọi biến vào make — kể cả mật khẩu — và một khoá trùng tên với biến
# đặc biệt của make (SHELL, MAKEFLAGS…) sẽ phá cả file mà không báo lý do.
BACKUP_DIR := $(shell grep -E '^NVM_BACKUP_DIR=' .env 2>/dev/null | cut -d= -f2-)

.DEFAULT_GOAL := help
.PHONY: help up up-obs down down-v reset ps logs net-check dmz-shell build test ci hooks format format-check clean backup bus-fanout bus-dlq bus-chaos sim-up sim-down sim-logs sim-report sim-net-check edge-up edge-down edge-logs edge-net-check buffer-crash ingestion-up ingestion-down ingestion-logs ingestion-migrate outage-lab load load-net-check

help:
	@echo "NovaVolt MES"
	@echo ""
	@echo "  Ha tang"
	@echo "    make up            Khoi dong ha tang + chay init, cho den khi tat ca healthy"
	@echo "    make up-obs        Nhu tren, kem profile obs (trong cho den M13)"
	@echo "    make down          Dung, GIU nguyen du lieu"
	@echo "    make down-v        Dung va XOA volume - mat sach du lieu"
	@echo "    make reset         down-v roi up lai tu dau"
	@echo "    make ps            Trang thai container"
	@echo "    make logs          Theo doi log"
	@echo "    make net-check     Kiem ranh gioi OT/IT (K11) - 9 phep do"
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
	@echo "    make outage-lab    D3: tat backend 2 phut, do backlog va so row lech"
	@echo ""
	@echo "  Do tai (D2)"
	@echo "    make load          Ban RATE msg/s trong DURATION giay tu ot-net, roi in lag"
	@echo "    make load-net-check Kiem harness chi o ot-net, toi EMQX nhung khong toi RabbitMQ"
	@echo ""
	@echo "  Code"
	@echo "    make build         Build solution"
	@echo "    make test          Chay toan bo test cua solution"
	@echo "    make ci            Chay dung chuoi kiem tra cua CI"
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
	echo "All healthy in $$(($$(date +%s)-start))s"

# Profile `obs` con trong cho den M13. Giu target o day de duong dan da co san,
# nhung dung tuong no dang bat them gi.
up-obs: .env
	@start=$$(date +%s); \
	$(COMPOSE) --profile obs up -d --wait || exit 1; \
	$(COMPOSE) run --rm mssql-init || exit 1; \
	$(COMPOSE) run --rm minio-init || exit 1; \
	echo ""; \
	echo "All healthy in $$(($$(date +%s)-start))s"

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
	dotnet restore $(SOLUTION)
	dotnet format $(SOLUTION) --verify-no-changes --no-restore
	dotnet build $(SOLUTION) -c Release --no-restore --nologo
	dotnet test --solution $(SOLUTION) -c Release --no-build
	$(MAKE) buffer-crash

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
ingestion-up: .env
	@$(COMPOSE) --profile ingestion build ingestion
	@$(COMPOSE) --profile ingestion run --rm ingestion-migrate
	@$(COMPOSE) --profile ingestion up -d --wait ingestion
	@echo "Ingestion healthy tren dmz-net + it-net. Theo doi: make ingestion-logs"

ingestion-down:
	@$(COMPOSE) --profile ingestion rm -sf ingestion

ingestion-logs:
	@$(COMPOSE) --profile ingestion logs -f --tail 100 ingestion

ingestion-migrate: .env
	@$(COMPOSE) --profile ingestion build ingestion
	@$(COMPOSE) --profile ingestion run --rm ingestion-migrate

# D2. Harness chay TRONG ot-net; lag doc tu ingestion o dmz-net qua dmz-shell.
# `make load RATE=5000 DURATION=600` la con so cua DoD; de nho hon khi dang dev.
#
# Hai ve cua phep do o hai mang khac nhau CO Y: harness khong duoc nhin thay ingestion,
# vi thiet bi that cung khong nhin thay (K11).
load: .env
	@$(COMPOSE) --profile load build load-harness
	@NVM_LOAD_RATE=$${RATE:-5000} NVM_LOAD_DURATION=$${DURATION:-600} 		$(COMPOSE) --profile load run --rm load-harness
	@echo ""
	@echo "  Ve NHAN — lag do chinh ingestion tinh (recorded_at - device_timestamp):"
	@$(COMPOSE) --profile tools run --rm dmz-shell 		wget -qO- http://ingestion:8080/api/ingestion/v1/stats || 		echo "  Khong doc duoc stats. Ingestion dang chay chua? make ingestion-up"

load-net-check: .env
	@sh scripts/load-net-check.sh

# Lab D3. Can `make up && make sim-up && make edge-up && make ingestion-up` truoc.
# WARMUP/OUTAGE/DRAIN_BUDGET de chay nhanh luc dev; mac dinh la con so cua DoD.
outage-lab: .env
	@sh scripts/backend-outage-lab.sh

# ─────────────────────────────────────────────────────────
# Bus — bang chung cua M1/C13
#
# Ba muc tieu nay KHONG nam trong `make ci`: chung can RabbitMQ dang chay va moi
# lan chay mat tu 30 giay den hon mot phut. Chung la lab, khong phai test.
#
# Kich ban nam trong src/Workers/Nvm.BusProbe/bus-lab.sh, canh worker ma no dieu
# khien — de khi M2 xoa worker thi khong con mot script mo coi trong tools/.
# ─────────────────────────────────────────────────────────
BUS_LAB := src/Workers/Nvm.BusProbe/bus-lab.sh

bus-fanout: .env
	@sh $(BUS_LAB) fanout

bus-dlq: .env
	@sh $(BUS_LAB) dlq

# Lab pha hoai. AGENTS.md §5.8.4: DOAN con so truoc khi chay, roi moi doi chieu.
# Con so cuoi cung phai duoc chep vao docs/benchmarks.md va ADR-022.
bus-chaos: .env
	@sh $(BUS_LAB) chaos
