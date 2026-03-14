@echo off
setlocal

cd /d "%~dp0"
python -m voice_typer_server %*
