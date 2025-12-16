# VoiceTyper Makefile

APP_NAME = VoiceTyper
VERSION = 1.0.0

.PHONY: all install run build build-lite clean dist test icon help

all: help

# 安装依赖
install:
	@echo "安装依赖..."
	pip install -r requirements.txt

# 开发运行
run:
	@echo "启动 VoiceTyper..."
	python main.py

# 完整打包 (PyInstaller，包含所有依赖，体积大)
build:
	@echo "完整打包 (PyInstaller)..."
	@chmod +x build.sh
	@./build.sh

# 轻量打包 (仅源码，需要用户安装依赖)
build-lite:
	@echo "轻量打包..."
	@chmod +x build_lite.sh
	@./build_lite.sh

# 清理
clean:
	@echo "清理构建文件..."
	rm -rf build dist __pycache__ *.spec.bak
	find . -type d -name "__pycache__" -exec rm -rf {} + 2>/dev/null || true
	find . -type f -name "*.pyc" -delete 2>/dev/null || true

# 创建发布包 (轻量版)
dist: build-lite
	@echo ""
	@echo "创建发布包..."
	@mkdir -p release
	@cd dist && zip -r -q ../release/$(APP_NAME)-$(VERSION)-macOS-lite.zip $(APP_NAME).app
	@echo "发布包: release/$(APP_NAME)-$(VERSION)-macOS-lite.zip"

# 测试运行
test:
	@if [ -d "dist/$(APP_NAME).app" ]; then \
		echo "运行应用..."; \
		open "dist/$(APP_NAME).app"; \
	else \
		echo "应用未构建，先执行 make build-lite"; \
	fi

# 创建图标
icon:
	@echo "创建图标..."
	@pip install Pillow -q 2>/dev/null || true
	@python create_icon.py

# 查看日志
log:
	@echo "应用日志:"
	@cat ~/.config/voice_input/app.log 2>/dev/null || echo "暂无日志"

# 清理日志
clean-log:
	@rm -f ~/.config/voice_input/app.log
	@echo "日志已清理"

# 帮助
help:
	@echo ""
	@echo "VoiceTyper 构建命令"
	@echo "===================="
	@echo ""
	@echo "开发:"
	@echo "  make install    - 安装 Python 依赖"
	@echo "  make run        - 开发模式运行"
	@echo ""
	@echo "打包:"
	@echo "  make build-lite - 轻量打包 (推荐，需用户安装依赖)"
	@echo "  make build      - 完整打包 (包含依赖，体积很大)"
	@echo "  make dist       - 创建发布 zip 包"
	@echo ""
	@echo "其他:"
	@echo "  make test       - 运行已打包的应用"
	@echo "  make icon       - 创建应用图标"
	@echo "  make log        - 查看应用日志"
	@echo "  make clean      - 清理构建文件"
	@echo ""