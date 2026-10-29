# NovaVolt MOM v2 — task runner
#
# `make ci` chạy đúng chuỗi mà GitHub Actions chạy — một bộ kiểm tra, hai nơi.

# GNU Make trên Windows chọn shell theo kinh nghiệm; ghi rõ SHELL để mọi dòng chạy giống nhau.
SHELL := sh
.SHELLFLAGS := -c

SOLUTION := NovaVolt.Mes.slnx
COMPOSE  := docker compose
# Chạy thử `--version`, không chỉ `command -v`: trên Windows `python3` có thể là stub mở Microsoft Store.
PYTHON   := $(shell python3 --version >/dev/null 2>&1 && echo python3 || echo python)

# Đọc RIÊNG một biến từ .env thay vì `include .env` (include nạp cả mật khẩu vào make).
BACKUP_DIR := $(shell grep -E '^NVM_BACKUP_DIR=' .env 2>/dev/null | cut -d= -f2-)

.DEFAULT_GOAL := help
.PHONY: help up down down-v reset ps logs build test ci hooks format format-check clean backup secret-check cleanroom update-db master-data line-polling linedb line-sim line-relay line-load keycloak-users mom web web-ci lab-web views quality-config sql-key

help:
	@echo "NovaVolt MOM v2"
	@echo ""
	@echo "  Ha tang"
	@echo "    make up            Khoi dong ha tang + tao database, cho den khi tat ca healthy"
	@echo "    make down          Dung, GIU nguyen du lieu"
	@echo "    make down-v        Dung va XOA volume - mat sach du lieu"
	@echo "    make reset         down-v roi up lai tu dau"
	@echo "    make ps / logs     Trang thai / log container"
	@echo ""
	@echo "  Code"
	@echo "    make build / test  Build / chay test"
	@echo "    make ci            Dung chuoi CI: cleanroom -> restore -> format -> build -> test"
	@echo "    make format        Sua format theo .editorconfig"
	@echo "    make hooks         Bat pre-commit hook (format + clean-room)"
	@echo ""
	@echo "  Chay MOM"
	@echo "    make update-db     Update Database: can schema DB MOM theo model (quyen sa)"
	@echo "    make master-data   Nap master data bang suite (suites/), bo qua suite da co trong lich su"
	@echo "    make quality-config Nap cau hinh huy theo ca (suites/quality), sau master data"
	@echo "    make views         Cai SQL tay cua team: view cua reading model, procedure thanh pham"
	@echo "    make sql-key       Dat key Nv.SqlConnection (chuoi ket noi cho procedure) tu .env"
	@echo "    make linedb        Cai schema LineDB (bang may, bang trung gian, role MOM); RESET=1 xoa sach"
	@echo "    make line-polling  Bat duong poll cho tin tu xuong (signal rule moi phut, key Nv.LineTransfer)"
	@echo "    make line-sim      May ghi mot ca cua mot line vao LineDB: LINE=1 CELLS=24, PACK=1 dong tote va pallet, EOLV=1 do cuoi"
	@echo "    make line-relay    Lich gui cua duong relay (send_pending moi phut)"
	@echo "    make line-load     Do tai: TAKT=5 (gap doi danh dinh) MINUTES=60, RELAY=1 de chay kem lich relay"
	@echo "    make keycloak-users Dat mat khau user thu cua realm tu NVM_DEMO_USER_PASSWORD"
	@echo "    make mom           Chay host MOM (OData, worker event, signal rule) o NVM_PORT_MOM"
	@echo "    make web           Chay web Next.js o NVM_PORT_WEB (lan dau: npm ci trong web/)"
	@echo "    make lab-web       Lab giao dien Playwright (Edge) tren stack dang chay"
	@echo "    make web-ci        Kiem kieu va build web, nhu job CI"
	@echo ""
	@echo "  Kiem tra"
	@echo "    make secret-check  .env co dung credential cong khai khong (K13)"
	@echo "    make cleanroom     Repo co ten, ma cua du an khach hang khong (K14)"
	@echo "    make backup        git bundle toan repo sang NVM_BACKUP_DIR"

.env:
	@echo "Chua co .env. Chay: cp .env.example .env roi dien mat khau"; exit 1

up: .env
	@start=$$(date +%s); \
	$(COMPOSE) up -d --wait || exit 1; \
	$(COMPOSE) run --rm mssql-init || exit 1; \
	echo ""; \
	echo "All healthy in $$(($$(date +%s)-start))s"; \
	sh scripts/secret-check.sh || true

down:
	$(COMPOSE) --profile init down

down-v:
	$(COMPOSE) --profile init down -v

reset: down-v up

ps:
	$(COMPOSE) ps -a

logs:
	$(COMPOSE) logs -f --tail 100

secret-check:
	@sh scripts/secret-check.sh

cleanroom:
	@$(PYTHON) -I -X utf8 tools/cleanroom/check.py

build:
	dotnet build $(SOLUTION) --nologo

# .NET 10 chạy test trên Microsoft.Testing.Platform (bật trong global.json): phải là --solution.
test:
	dotnet test --solution $(SOLUTION)

# Đúng thứ tự và đúng cờ của GitHub Actions. format chạy TRƯỚC build để lỗi thụt lề
# không phải chờ hết một lần build Release mới lộ ra.
ci: cleanroom
	dotnet restore $(SOLUTION)
	dotnet format $(SOLUTION) --verify-no-changes --no-restore
	dotnet build $(SOLUTION) -c Release --no-restore --nologo
	dotnet test --solution $(SOLUTION) -c Release --no-build --filter-not-trait "Category=Benchmark"
	# Benchmark chay rieng, khong tranh CPU voi cac bo test tich hop khac.
	dotnet test --project tests/Integration/Nvm.FoundationTests -c Release --no-build --filter-trait "Category=Benchmark"
	dotnet test --project tests/Integration/Nvm.SolutionTests -c Release --no-build --filter-trait "Category=Benchmark"

# Git không tự bật hook khi clone; core.hooksPath trỏ sang thư mục được version hoá.
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

# Một bản backup chưa từng kiểm lại thì chưa phải backup: `bundle verify` là phần quan trọng.
backup:
	@test -n "$(BACKUP_DIR)" || { echo "NVM_BACKUP_DIR chua duoc dat trong .env"; exit 1; }
	@mkdir -p "$(BACKUP_DIR)"
	@git bundle create "$(BACKUP_DIR)/novavolt-mes.bundle" --all
	@git bundle verify "$(BACKUP_DIR)/novavolt-mes.bundle" >/dev/null
	@echo "Backup OK: $(BACKUP_DIR)/novavolt-mes.bundle"

update-db:
	dotnet run --project src/Solution/Nvm.Mom.Host --no-launch-profile -- --update-database

master-data:
	dotnet run --project src/Solution/Nvm.Mom.Host --no-launch-profile -- --load-master-data

# Cau hinh huy theo ca (suites/quality): sau master-data vi can production schedule.
quality-config:
	dotnet run --project src/Solution/Nvm.Mom.Host --no-launch-profile -- --load-quality-config

# View cua reading model (danh sach NC): buoc rieng, Update Database khong tao.
views:
	dotnet run --project src/Solution/Nvm.Mom.Host --no-launch-profile -- --install-views

sql-key:
	dotnet run --project src/Solution/Nvm.Mom.Host --no-launch-profile -- --set-sql-connection-key

linedb:
	dotnet run --project src/Solution/Nvm.Mom.Host --no-launch-profile -- --install-linedb $(if $(RESET),--reset,)

line-polling:
	dotnet run --project src/Solution/Nvm.Mom.Host --no-launch-profile -- --enable-line-polling

LINESIM := dotnet run --project src/Shopfloor/Nvm.LineSimulator --
line-sim:
	$(LINESIM) run --line $(or $(LINE),1) --cells $(or $(CELLS),24) $(if $(PACK),--pack) $(if $(EOLV),--eolv)

line-relay:
	$(LINESIM) relay

line-load:
	$(LINESIM) load --takt $(or $(TAKT),5) --minutes $(or $(MINUTES),60) $(if $(RELAY),--relay,)

keycloak-users:
	@$(PYTHON) -I -X utf8 scripts/keycloak-demo-users.py

mom:
	dotnet run --project src/Solution/Nvm.Mom.Host --no-launch-profile

web:
	npm run --prefix web dev

# Lab giao dien tren stack dang chay that (up, update-db, master-data, keycloak-users, mom, web).
lab-web:
	npm run --prefix web lab

web-ci:
	npm ci --prefix web
	npm run --prefix web typecheck
	npm run --prefix web build
