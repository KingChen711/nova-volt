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
.PHONY: help up down down-v reset ps logs build test ci hooks format format-check clean backup secret-check cleanroom

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
	dotnet test --solution $(SOLUTION) -c Release --no-build

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
