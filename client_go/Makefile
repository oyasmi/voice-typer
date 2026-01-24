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
LDFLAGS = -s -w -H=windowsgui -X main.version=$(VERSION)

# 默认目标
all: deps build

# 安装依赖
deps:
	@echo "Installing dependencies..."
	$(GOGET) github.com/gen2brain/beeep
	$(GOGET) github.com/gen2brain/malgo
	$(GOGET) github.com/getlantern/systray
	$(GOGET) github.com/go-resty/resty/v2
	$(GOGET) github.com/go-vgo/robotgo
	$(GOGET) golang.design/x/clipboard
	$(GOGET) golang.org/x/sys/windows
	$(GOGET) gopkg.in/yaml.v3
	$(GOMOD) tidy
	@echo "Dependencies installed successfully"

# 构建当前平台 (强制Windows)
build:
	@echo "Building for Windows..."
	GOOS=windows GOARCH=amd64 CGO_ENABLED=1 $(GOBUILD) -ldflags="$(LDFLAGS)" -o $(BINARY_NAME).exe
	@echo "Build completed: $(BINARY_NAME).exe"

# 打包 (仅Windows)
package: build
	@echo "Packaging for Release..."
	@if not exist $(RELEASE_DIR) mkdir $(RELEASE_DIR)
	@copy $(BINARY_NAME).exe $(RELEASE_DIR)\
	@copy README.md $(RELEASE_DIR)\
	@echo "Package created in $(RELEASE_DIR)"

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
