# VoiceTyper Makefile

APP_NAME = VoiceTyper
VERSION = 1.0.0
CONFIG_DIR = voice_typer

.PHONY: all install run build clean dist test icon log help

all: help

# 安装依赖
install:
	@echo "安装依赖..."
	pip install -r requirements.txt

# 开发运行
run:
	@echo "启动 $(APP_NAME)..."
	python main.py

# 轻量打包
build:
	@echo "打包应用..."
	@chmod +x build_lite.sh
	@./build_lite.sh

# 清理
clean:
	@echo "清理构建文件..."
	rm -rf build dist __pycache__
	find . -type d -name "__pycache__" -exec rm -rf {} + 2>/dev/null || true
	find . -type f -name "*.pyc" -delete 2>/dev/null || true

# 创建发布包
dist: build
	@echo ""
	@echo "创建发布包..."
	@mkdir -p release
	@cd dist && zip -r -q ../release/$(APP_NAME)-$(VERSION)-macOS.zip $(APP_NAME).app
	@echo ""
	@echo "发布包: release/$(APP_NAME)-$(VERSION)-macOS.zip"
	@ls -lh release/$(APP_NAME)-$(VERSION)-macOS.zip

# 测试运行
test:
	@if [ -d "dist/$(APP_NAME).app" ]; then \
		echo "运行应用..."; \
		open "dist/$(APP_NAME).app"; \
	else \
		echo "应用未构建，先执行 make build"; \
	fi

# 创建图标
icon:
	@echo "创建图标..."
	@pip install Pillow -q 2>/dev/null || true
	@python create_icon.py

# 查看日志
log:
	@if [ -f "$$HOME/.config/$(CONFIG_DIR)/app.log" ]; then \
		tail -50 "$$HOME/.config/$(CONFIG_DIR)/app.log"; \
	else \
		echo "暂无日志"; \
	fi

# 实时日志
log-follow:
	@if [ -f "$$HOME/.config/$(CONFIG_DIR)/app.log" ]; then \
		tail -f "$$HOME/.config/$(CONFIG_DIR)/app.log"; \
	else \
		echo "暂无日志"; \
	fi

# 清理配置（谨慎使用）
clean-config:
	@echo "清理配置目录..."
	@read -p "确定要删除 ~/.config/$(CONFIG_DIR)? [y/N] " confirm && \
	if [ "$$confirm" = "y" ] || [ "$$confirm" = "Y" ]; then \
		rm -rf "$$HOME/.config/$(CONFIG_DIR)"; \
		echo "已清理"; \
	else \
		echo "已取消"; \
	fi

# 帮助
help:
	@echo ""
	@echo "$(APP_NAME) 构建命令"
	@echo "====================="
	@echo ""
	@echo "开发:"
	@echo "  make install     - 安装 Python 依赖"
	@echo "  make run         - 开发模式运行"
	@echo ""
	@echo "打包:"
	@echo "  make build       - 打包为 macOS 应用"
	@echo "  make dist        - 创建发布 zip 包"
	@echo "  make icon        - 创建应用图标"
	@echo ""
	@echo "调试:"
	@echo "  make test        - 运行已打包的应用"
	@echo "  make log         - 查看应用日志"
	@echo "  make log-follow  - 实时查看日志"
	@echo ""
	@echo "清理:"
	@echo "  make clean       - 清理构建文件"
	@echo "  make clean-config- 清理配置目录"
	@echo ""
	@echo "配置目录: ~/.config/$(CONFIG_DIR)/"
	@echo ""