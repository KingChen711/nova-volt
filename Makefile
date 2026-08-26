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
ALL_PROFILES := --profile probe --profile init --profile obs

# Đọc RIÊNG một biến từ .env thay vì `include .env`.
# `include` nạp mọi biến vào make — kể cả mật khẩu — và một khoá trùng tên với biến
# đặc biệt của make (SHELL, MAKEFLAGS…) sẽ phá cả file mà không báo lý do.
BACKUP_DIR := $(shell grep -E '^NVM_BACKUP_DIR=' .env 2>/dev/null | cut -d= -f2-)

.DEFAULT_GOAL := help
.PHONY: help up up-obs down down-v reset ps logs build test ci hooks format format-check clean backup

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
	@echo ""
	@echo "  Code"
	@echo "    make build         Build solution"
	@echo "    make test          Chay unit test"
	@echo "    make ci            Chay dung chuoi kiem tra cua CI"
	@echo "    make hooks         Bat pre-commit hook cho repo nay"
	@echo "    make format        Tu dong sua format theo .editorconfig"
	@echo "    make format-check  Kiem format, khong sua"
	@echo "    make clean         Xoa thu muc artifacts"
	@echo ""
	@echo "  Khac"
	@echo "    make backup        git bundle toan bo repo sang NVM_BACKUP_DIR"

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
