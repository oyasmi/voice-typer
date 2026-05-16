# Changelog

## 1.2.0

### 流式双通道：partial 预览 + 离线复识别 final

流式模式（WebSocket）引入两通道架构，同时兼顾上屏速度与识别准确率：

- **partial（预览）**：录音期间继续使用流式模型（`paraformer-zh-streaming`）产出逐字预览，在客户端 HUD 实时显示，不直接写入目标程序。
- **final（最终结果）**：松开热键后，对录音期间累积的完整 PCM 执行一次离线整段识别（`paraformer-zh`），精度与 `--no-streaming` 模式一致；标点恢复、LLM 纠错均作用于该结果。

如果离线复识别出现异常，自动回退到流式碎片拼接。

#### 新增参数

- `--offline-model`：流式模式下用于复识别的离线模型，默认 `paraformer-zh`

#### 注意事项

- 流式模式启动时同时加载两个 ASR 模型，内存占用约增加 220MB
- 标点模型仅挂在离线识别器上，不再重复加载
- 对已有客户端透明，无需修改；macOS Swift 和 Windows 原生客户端均已支持

---

## 1.0.0

首个可发布版本。服务端从仓库内脚本入口重构为标准 Python package，并补齐发布链路。

### 代码变化

- 将原 `server/asr_server.py` 拆分为 `voice_typer_server/cli.py` 与 `voice_typer_server/app.py`
- 将 `auth.py`、`recognizer.py`、`llm_client.py` 迁移到 `voice_typer_server/` 包内
- 新增 `voice_typer_server/__main__.py`，支持 `python -m voice_typer_server`
- 新增 `console_scripts` 入口，支持 `voice-typer-server`
- 将 LLM prompt 改为 package resource，随 wheel/sdist 一起发布
- CLI 改为懒加载运行时依赖，使 `--help` 与 `--version` 不依赖完整运行环境

### 结构变化

- 新增 `server/pyproject.toml`，将服务端切换为标准打包结构
- 新增 `server/scripts/voice_typer_server.sh`
- 新增 `server/Makefile`
- 新增 `server/RELEASING.md`
- 新增 `server/requirements-release.txt`
- 删除旧入口与包装脚本：
  - `server/asr_server.py`
  - `server/run.sh`
  - `server/setup.sh`
- 删除旧的顶层模块文件，统一迁移到 `server/voice_typer_server/`

### 启动方式变化

- 旧方式：
  - `python asr_server.py`
  - `./run.sh`
  - `./setup.sh --start-server`
- 新方式：
  - `python -m voice_typer_server`
  - `voice-typer-server`
  - `./scripts/voice_typer_server.sh setup`
  - `./scripts/voice_typer_server.sh run`

### 文档变化

- 重写 `server/README.md`，改为 package/CLI 视角
- 更新根 `README.md` 中的服务端安装、接口与能力说明
- 新增发布流程文档 `server/RELEASING.md`

### 发布链路

- 新增 `make bootstrap-release`
- 新增 `make build`
- 新增 `make check`
- 新增 `make release-test`
- 新增 `make release`

### 其他调整

- 最低支持 Python 版本明确为 3.9，推荐版本为 3.12
- 版本号改为单点维护：`voice_typer_server/__init__.py`
- 更新 `.gitignore`，覆盖打包产物、发布虚拟环境与 PyPI 凭据文件
