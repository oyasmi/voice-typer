APP_NAME = VoiceTyper
VERSION = 1.1.0

.PHONY: install run build clean dist test log help

# 安装依赖
install:
	pip install -r requirements.txt

# 开发运行
run:
	python main.py

# 打包应用
build:
	@chmod +x build.sh && ./build.sh

# 清理
clean:
	rm -rf build dist __pycache__ *.spec.bak
	find . -type d -name "__pycache__" -exec rm -rf {} + 2>/dev/null || true

# 创建发布包
dist: build
	@echo "创建发布包..."
	@mkdir -p release
	@cd dist && zip -r -q ../release/$(APP_NAME)-$(VERSION)-macOS.zip $(APP_NAME).app
	@echo "发布包: release/$(APP_NAME)-$(VERSION)-macOS.zip"
	@ls -lh release/$(APP_NAME)-$(VERSION)-macOS.zip

# 测试
test:
	@if [ -d "dist/$(APP_NAME).app" ]; then \
		open "dist/$(APP_NAME).app"; \
	else \
		echo "先运行 make build"; \
	fi

# 查看日志
log:
	@tail -100 ~/.config/voice_typer/app.log 2>/dev/null || echo "暂无日志"

# 实时日志
log-f:
	@tail -f ~/.config/voice_typer/app.log 2>/dev/null || echo "暂无日志"

# 帮助
help:
	@echo ""
	@echo "$(APP_NAME) 客户端构建命令"
	@echo "=========================="
	@echo ""
	@echo "  make install  - 安装依赖"
	@echo "  make run      - 开发模式运行"
	@echo "  make build    - 打包为 macOS 应用"
	@echo "  make dist     - 创建发布 zip 包"
	@echo "  make test     - 运行打包的应用"
	@echo "  make log      - 查看日志"
	@echo "  make clean    - 清理构建文件"
	@echo ""