PYTHON ?= python3
VENV ?= .venv-release
VENV_PYTHON := $(VENV)/bin/python

.PHONY: bootstrap-release clean build check release-test release

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
