.PHONY: all build clean package help

# 版本信息
VERSION = 1.0.0
APP_NAME = VoiceTyper
BINARY_NAME = voicetyper.exe

# 目录
DIST_DIR = dist
RELEASE_DIR = release

# Go相关
GOCMD = go
GOBUILD = $(GOCMD) build
GOCLEAN = $(GOCMD) clean
GOMOD = $(GOCMD) mod

# 构建标志
LDFLAGS = -s -w -X main.version=$(VERSION)

# 默认目标
all: deps build

# 安装依赖
deps:
	@echo "Installing dependencies..."
	$(GOMOD) tidy
	$(GOCMD) mod download
	@echo "Dependencies installed successfully"

# 构建 Windows 版本
build:
	@echo "Building for Windows..."
	$(GOBUILD) -ldflags="$(LDFLAGS)" -o $(BINARY_NAME)
	@echo "Build completed: $(BINARY_NAME)"

# 清理构建文件
clean:
	@echo "Cleaning build artifacts..."
	$(GOCLEAN)
	rm -rf $(DIST_DIR)
	rm -rf $(RELEASE_DIR)
	rm -f $(BINARY_NAME)
	@echo "Clean completed"

# 打包 Windows 版本
package: build
	@echo "Creating release package..."
	@mkdir -p $(RELEASE_DIR)
	@cp $(BINARY_NAME) $(RELEASE_DIR)/
	@echo "Package created: $(RELEASE_DIR)/$(BINARY_NAME)"

# 开发运行
run:
	@echo "Running $(BINARY_NAME)..."
	$(GOBUILD) -ldflags="$(LDFLAGS)" -o $(BINARY_NAME) && ./$(BINARY_NAME)

# 格式化代码
fmt:
	@echo "Formatting code..."
	gofmt -s -w .

# 代码检查
vet:
	@echo "Vetting code..."
	$(GOCMD) vet ./...

# 帮助
help:
	@echo "VoiceTyper Go Client - Makefile (Windows)"
	@echo ""
	@echo "Usage: make [target]"
	@echo ""
	@echo "Targets:"
	@echo "  all     - 安装依赖并构建（默认）"
	@echo "  deps    - 安装Go依赖"
	@echo "  build   - 构建Windows版本"
	@echo "  package - 创建发布包"
	@echo "  clean   - 清理构建文件"
	@echo "  run     - 开发模式运行"
	@echo "  fmt     - 格式化代码"
	@echo "  vet     - 代码检查"
	@echo "  help    - 显示此帮助"
