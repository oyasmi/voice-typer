# 发布流程

## 前置要求

- Python 3.9+，建议 3.12
- 已准备好一个干净的发布虚拟环境
- 已准备好 PyPI 或 TestPyPI 凭据

推荐使用环境变量传入 token，避免非交互环境里 `twine` 退回密码提示：

```bash
export TWINE_USERNAME=__token__
export TWINE_PASSWORD=pypi-xxxx
```

## 初始化发布环境

在 `server/` 目录执行：

```bash
make bootstrap-release
```

默认会创建：

```text
server/.venv-release
```

并安装发布工具：

- `build`
- `twine`

## 本地检查

在 `server/` 目录执行：

```bash
make check
```

这会执行：

1. 清理旧产物
2. 构建 `sdist` 和 `wheel`
3. 用 `twine check` 检查包元数据

如果你不想使用默认发布环境，也可以覆盖：

```bash
make VENV=.venv-publish check
```

## 发布到 TestPyPI

```bash
make release-test
```

或：

```bash
TWINE_NON_INTERACTIVE=1 python3 -m twine upload --repository testpypi dist/*
```

## 发布到 PyPI

```bash
make release
```

或：

```bash
TWINE_NON_INTERACTIVE=1 python3 -m twine upload dist/*
```

## 推荐发布前检查项

- 确认 [__init__.py](/home/oyasmi/projects/voice-typer/server/voice_typer_server/__init__.py) 中 `__version__` 已更新
- 确认 [README.md](/home/oyasmi/projects/voice-typer/server/README.md) 与实际 CLI 一致
- 运行 `python3 -m voice_typer_server --help`
- 在干净虚拟环境中执行一次 `pip install .`
- 正式发 PyPI 前先过一遍 TestPyPI
