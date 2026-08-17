PYTHON ?= python3
VENV ?= .venv-release
VENV_PYTHON := $(VENV)/bin/python

.PHONY: test bootstrap-release clean build check release-test release

# 免模型的测试骨架（鉴权矩阵 / 后处理正则 / 运行时参数决策 / WS 协议状态机）。
# 不需要 ONNX 模型，任何干净环境 `pip install -e '.[dev]'` 后即可跑。
test:
	$(PYTHON) -m pytest tests/ -q

bootstrap-release:
	$(PYTHON) -m venv $(VENV)
	$(VENV_PYTHON) -m pip install --upgrade pip setuptools wheel
	$(VENV_PYTHON) -m pip install -r requirements-release.txt

clean:
	rm -rf build dist *.egg-info

build: clean
	$(VENV_PYTHON) -m build

check: build
	$(VENV_PYTHON) -m twine check dist/*

release-test: check
	TWINE_NON_INTERACTIVE=1 $(VENV_PYTHON) -m twine upload --repository testpypi dist/*

release: check
	TWINE_NON_INTERACTIVE=1 $(VENV_PYTHON) -m twine upload dist/*
