# VoiceTyper Windows 版开发辅助目标。
#
# 注意：`cross-*` 目标是在 Linux/macOS 上交叉编译 Windows 程序，只能验证"能否编译与发布"，
# 无法运行或测试 WinForms 程序。正式发布（安装包、签名）仍使用 Windows 上的 build.bat。

.PHONY: help cross-check cross-publish cross-publish-x64 cross-publish-arm64 clean-cross

help:
	@echo "VoiceTyper Windows 版 Makefile（在 Linux/macOS 上交叉编译，不能运行）"
	@echo ""
	@echo "  make cross-check          - 交叉编译检查（dotnet build，不产出发布目录）"
	@echo "  make cross-publish        - 交叉发布 win-x64 与 win-arm64 到 dist-cross/（含便携版 zip）"
	@echo "  make cross-publish-x64    - 仅交叉发布 win-x64"
	@echo "  make cross-publish-arm64  - 仅交叉发布 win-arm64"
	@echo "  make clean-cross          - 删除 dist-cross/"
	@echo ""
	@echo "需要 .NET 10 SDK。安装包与签名请在 Windows 上运行 build.bat。"

cross-check:
	dotnet build VoiceTyper.csproj -c Release -r win-x64 --self-contained false -p:EnableWindowsTargeting=true --nologo -v q

cross-publish:
	scripts/cross_publish_win_from_linux.sh

cross-publish-x64:
	scripts/cross_publish_win_from_linux.sh win-x64

cross-publish-arm64:
	scripts/cross_publish_win_from_linux.sh win-arm64

clean-cross:
	rm -rf dist-cross
