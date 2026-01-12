.PHONY: all build clean package help test install deps

# 版本信息
VERSION = 1.0.0
APP_NAME = VoiceTyper
BINARY_NAME = voicetyper

# 目录
BUILD_DIR = build
DIST_DIR = dist
RELEASE_DIR = release
CONFIG_DIR = configs
ASSETS_DIR = assets

# Go相关
GOCMD = go
GOBUILD = $(GOCMD) build
GOCLEAN = $(GOCMD) clean
GOTEST = $(GOCMD) test
GOGET = $(GOCMD) get
GOMOD = $(GOCMD) mod
GOINSTALL = $(GOCMD) install

# 构建标志
LDFLAGS = -s -w -X main.version=$(VERSION)

# 默认目标
all: deps build

# 安装依赖
deps:
	@echo "Installing dependencies..."
	$(GOGET) fyne.io/fyne/v2
	$(GOGET) github.com/gen2brain/beeep
	$(GOGET) github.com/gen2brain/malgo
	$(GOGET) github.com/go-resty/resty/v2
	$(GOGET) github.com/go-vgo/robotgo
	$(GOGET) golang.design/x/clipboard
	$(GOGET) gopkg.in/yaml.v3
	$(GOMOD) tidy
	@echo "Dependencies installed successfully"

# 构建当前平台
build:
	@echo "Building for current platform..."
	$(GOBUILD) -ldflags="$(LDFLAGS)" -o $(BINARY_NAME)
	@echo "Build completed: $(BINARY_NAME)"

# 跨平台构建
build-all:
	@echo "Building for all platforms..."
	@chmod +x $(BUILD_DIR)/build.sh
	@$(BUILD_DIR)/build.sh

# 打包
package: build-all
	@echo "Creating release packages..."
	@chmod +x $(BUILD_DIR)/package.sh
	@$(BUILD_DIR)/package.sh

# 清理构建文件
clean:
	@echo "Cleaning build artifacts..."
	$(GOCLEAN)
	rm -rf $(DIST_DIR)
	rm -rf $(RELEASE_DIR)
	rm -f $(BINARY_NAME)
	@echo "Clean completed"

# 测试
test:
	@echo "Running tests..."
	$(GOTEST) -v ./...

# 安装到GOPATH/bin
install: build
	@echo "Installing $(BINARY_NAME) to GOPATH/bin..."
	$(GOINSTALL)

# 开发运行
run:
	@echo "Running $(BINARY_NAME)..."
	$(GOBUILD) -ldflags="$(LDFLAGS)" -o $(BINARY_NAME) && ./$(BINARY_NAME)

# 查看日志（如果有）
log:
	@if [ -f ~/.config/voice-typer/app.log ]; then \
		tail -50 ~/.config/voice-typer/app.log; \
	else \
		echo "No log file found"; \
	fi

# 格式化代码
fmt:
	@echo "Formatting code..."
	gofmt -s -w .

# 代码检查
vet:
	@echo "Vetting code..."
	$(GO) vet ./...

# 帮助
help:
	@echo "VoiceTyper Go Client - Makefile"
	@echo ""
	@echo "Usage: make [target]"
	@echo ""
	@echo "Targets:"
	@echo "  all        - 安装依赖并构建（默认）"
	@echo "  deps       - 安装Go依赖"
	@echo "  build      - 构建当前平台"
	@echo "  build-all  - 跨平台构建"
	@echo "  package    - 创建发布包"
	@echo "  clean      - 清理构建文件"
	@echo "  test       - 运行测试"
	@echo "  install    - 安装到GOPATH/bin"
	@echo "  run        - 开发模式运行"
	@echo "  fmt        - 格式化代码"
	@echo "  vet        - 代码检查"
	@echo "  help       - 显示此帮助"
