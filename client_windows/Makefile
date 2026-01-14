.PHONY: all install run build clean help

# 默认目标
all: build

# 安装依赖
install:
	@echo "安装依赖..."
	pip install -r requirements.txt

# 运行开发版本
run:
	@echo "启动 VoiceTyper..."
	python main.py

# 构建可执行文件
build:
	@echo "构建 Windows 可执行文件..."
	pyinstaller voicetyper.spec --clean

# 清理构建文件
clean:
	@echo "清理构建文件..."
	rm -rf build dist *.spec

# 帮助信息
help:
	@echo "VoiceTyper Windows 客户端 - Makefile 命令"
	@echo ""
	@echo "  make install   - 安装依赖"
	@echo "  make run       - 运行开发版本"
	@echo "  make build     - 构建 Windows 可执行文件"
	@echo "  make clean     - 清理构建文件"
	@echo "  make help      - 显示此帮助信息"
	@echo ""
