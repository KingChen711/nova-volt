# NovaVolt MES — task runner
#
# Mọi lệnh hay dùng gom vào đây để không ai phải nhớ cờ dòng lệnh.
# `make ci` ở C12 sẽ chạy đúng chuỗi mà GitHub Actions chạy — một bộ kiểm tra, hai nơi.

# GNU Make trên Windows chọn shell theo kinh nghiệm: lệnh trông đơn giản thì gọi thẳng
# .exe, lệnh có ký tự đặc biệt thì mới gọi sh. Hệ quả là cùng một Makefile chạy khác nhau
# tuỳ dòng. Ghi rõ SHELL để hành vi giống nhau mọi lúc, mọi nền tảng.
# `sh` luôn có: Windows lấy từ Git, Linux/macOS có sẵn.
SHELL := sh
.SHELLFLAGS := -c

SOLUTION := NovaVolt.Mes.slnx

.DEFAULT_GOAL := help
.PHONY: help build test format format-check clean

help:
	@echo "NovaVolt MES"
	@echo ""
	@echo "  make build         Build toan bo solution"
	@echo "  make test          Chay unit test"
	@echo "  make format        Tu dong sua format theo .editorconfig"
	@echo "  make format-check  Kiem format, khong sua. CI dung lenh nay"
	@echo "  make clean         Xoa thu muc artifacts"

build:
	dotnet build $(SOLUTION) --nologo

# .NET 10 bo VSTest cho Microsoft.Testing.Platform, va xunit v3 chay tren MTP.
# MTP mode duoc bat trong global.json va doi cu phap: phai la --solution, khong phai
# truyen thang duong dan solution nhu truoc.
test:
	dotnet test --solution $(SOLUTION)

format:
	dotnet format $(SOLUTION)

format-check:
	dotnet format $(SOLUTION) --verify-no-changes

clean:
	dotnet clean $(SOLUTION) --nologo
	rm -rf artifacts
