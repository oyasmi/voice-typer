# VoiceTyper 根 Makefile：统一的构建入口，实际工作委托给各平台已有的脚本。
#
#   make                按本机系统构建：macOS → macos，Windows → windows；
#                       识别不出（含 Linux，尚无一体化版本）时默认按 macos 处理
#   make macos          macos/build_xcode.sh，产物在 macos/dist/
#   make windows        Windows 上运行 windows/build.bat，产物在 windows/dist/；
#                       其他系统上改为交叉发布到 windows/dist-cross/（只验证结构，不能运行）
#   make test           运行本机平台的测试
#   make clean          删除两个平台的构建产物
#
# 签名、公证等可选环境变量（VOICETYPER_*）原样透传给平台脚本，用法见各平台 DEVELOPMENT.md。
# 在 Windows 上需要 GNU make 加 POSIX shell（Git Bash 或 MSYS2）。
# 只用 GNU make 3.81 的语法：macOS 自带的 make 就是这个版本。

ifeq ($(OS),Windows_NT)
  HOST_OS := windows
else
  UNAME_S := $(shell uname -s 2>/dev/null)
  ifeq ($(UNAME_S),Darwin)
    HOST_OS := macos
  else ifneq ($(filter MINGW% MSYS% CYGWIN%,$(UNAME_S)),)
    HOST_OS := windows
  else
    HOST_OS := macos
  endif
endif

.DEFAULT_GOAL := build

.PHONY: help info build test clean \
	macos macos-project macos-test macos-model clean-macos \
	windows windows-check windows-test clean-windows

help:
	@echo "VoiceTyper 构建入口（本机识别为: $(HOST_OS)）"
	@echo ""
	@echo "  make                 按本机系统构建（等同 make $(HOST_OS)）"
	@echo "  make test            运行本机平台的测试"
	@echo "  make clean           删除 macOS 与 Windows 的全部构建产物"
	@echo ""
	@echo "  make macos           构建 macOS 版 → macos/dist/（.dmg + .zip，需要 Xcode）"
	@echo "  make macos-project   重新生成 Xcode 工程（增删或移动 Swift 文件后必须执行）"
	@echo "  make macos-test      运行 macOS 测试（缺模型的用例自动跳过）"
	@echo "  make macos-model     预置语音模型，供金标准测试或跳过首启下载"
	@echo "  make clean-macos     删除 macos/build/ 与 macos/dist/"
	@echo ""
	@echo "  make windows         Windows 上：build.bat → windows/dist/（便携版 zip，有 Inno Setup 时含安装包）"
	@echo "                       其他系统：交叉发布 → windows/dist-cross/（只验证结构，不能运行；"
	@echo "                       WINDOWS_RID=win-x64 或 win-arm64 只发布一个架构）"
	@echo "  make windows-check   只编译不发布（非 Windows 上为交叉编译检查）"
	@echo "  make windows-test    运行 Windows 测试（仅限 Windows）"
	@echo "  make clean-windows   删除 windows/ 下的 dist、dist-cross、bin、obj 与测试结果"

info:
	@echo "HOST_OS=$(HOST_OS)"

build: $(HOST_OS)

test: $(HOST_OS)-test

clean: clean-macos clean-windows

# ---------- macOS ----------

macos:
	@command -v xcodebuild >/dev/null 2>&1 || { \
		echo "错误: 未找到 xcodebuild，macOS 版只能在装有 Xcode 的 Mac 上构建。" >&2; \
		echo "      在 Linux 上可用 make windows 交叉发布 Windows 版。" >&2; \
		exit 1; }
	cd macos && ./build_xcode.sh

macos-project:
	cd macos && ruby scripts/generate_xcodeproj.rb

macos-test:
	@command -v xcodebuild >/dev/null 2>&1 || { echo "错误: 未找到 xcodebuild，macOS 测试只能在 Mac 上运行。" >&2; exit 1; }
	cd macos && xcodebuild -project VoiceTyper.xcodeproj -scheme VoiceTyper -destination 'platform=macOS' test

macos-model:
	cd macos && ./scripts/fetch_model.sh

clean-macos:
	rm -rf macos/build macos/dist

# ---------- Windows ----------

# build.bat 不带参数时会在结束时 pause、出错时也会 pause：这里传一个参数关掉结尾的 pause，
# 再把 stdin 接到 /dev/null，让出错路径上的 pause 立即返回，而不是把 make 挂住。
# 非 Windows 主机上改为交叉发布：只能验证"能否编译与发布"，无法运行或测试 WinForms 程序；
# 安装包与签名仍须在 Windows 上走 build.bat。可用 WINDOWS_RID=win-x64 或 win-arm64 只发布一个架构。
windows:
ifeq ($(HOST_OS),windows)
	cd windows && cmd.exe //c "build.bat nopause" < /dev/null
else
	cd windows && scripts/cross_publish_win_from_linux.sh $(WINDOWS_RID)
endif

windows-check:
ifeq ($(HOST_OS),windows)
	cd windows && dotnet build VoiceTyper.sln -c Release --nologo
else
	cd windows && dotnet build VoiceTyper.csproj -c Release -r win-x64 --self-contained false \
		-p:EnableWindowsTargeting=true --nologo -v q
endif

windows-test:
ifeq ($(HOST_OS),windows)
	cd windows && dotnet test VoiceTyper.sln --nologo
else
	@echo "错误: Windows 测试需要在 Windows 上运行；非 Windows 主机的变通做法见 windows/DEVELOPMENT.md「在 macOS / Linux 上开发」。" >&2
	@exit 1
endif

clean-windows:
	rm -rf windows/dist windows/dist-cross windows/bin windows/obj windows/TestResults \
		windows/Tests/VoiceTyper.Tests/bin windows/Tests/VoiceTyper.Tests/obj
