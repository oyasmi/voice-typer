APP_NAME = VoiceTyper
VERSION = 1.0.0

.PHONY: install run build clean test log help

install:
	pip install -r requirements.txt

run:
	python main.py

build:
	@chmod +x build.sh && ./build.sh

clean:
	rm -rf dist __pycache__

test:
	@[ -d "dist/$(APP_NAME).app" ] && open "dist/$(APP_NAME).app" || echo "先运行 make build"

log:
	@tail -50 ~/.config/voice_typer/app.log 2>/dev/null || echo "暂无日志"

help:
	@echo "$(APP_NAME) 客户端"
	@echo ""
	@echo "  make install - 安装依赖"
	@echo "  make run     - 运行"
	@echo "  make build   - 打包"
	@echo "  make test    - 测试打包的应用"
	@echo "  make log     - 查看日志"