"""
Windows 服务包装模块

使用 pywin32 的 win32serviceutil 将 VoiceTyper Server 注册为 Windows 服务。
仅在 Windows 平台下使用，其他平台不需导入此模块。
"""
import asyncio
import logging
import os
import sys
from logging.handlers import RotatingFileHandler
from typing import Optional

import win32event
import win32service
import win32serviceutil
import servicemanager

from .cli import build_parser

logger = logging.getLogger("VoiceTyper")

# 注册表参数键名（存储在 HKLM\SYSTEM\...\Services\VoiceTyperServer\Parameters 下）
_REG_KEY_ARGS = "ServerArgs"

# 服务默认参数（与 CLI 默认值保持一致）
_DEFAULT_ARGS = [
    "--host", "127.0.0.1",
    "--port", "6008",
    "--device", "cpu",
]


def _get_log_dir() -> str:
    """获取日志目录路径"""
    home = os.environ.get("USERPROFILE", os.path.expanduser("~"))
    log_dir = os.path.join(home, ".voice-typer")
    os.makedirs(log_dir, exist_ok=True)
    return log_dir


def _configure_service_logging():
    """配置服务模式的日志（输出到文件）"""
    log_dir = _get_log_dir()
    log_file = os.path.join(log_dir, "server.log")

    handler = RotatingFileHandler(
        log_file,
        maxBytes=10 * 1024 * 1024,  # 10MB
        backupCount=3,
        encoding="utf-8",
    )
    handler.setFormatter(logging.Formatter(
        "%(asctime)s - %(levelname)s - %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    ))

    root_logger = logging.getLogger()
    root_logger.setLevel(logging.INFO)
    root_logger.addHandler(handler)

    # 同时添加到 VoiceTyper logger
    logger.addHandler(handler)
    logger.setLevel(logging.INFO)


class VoiceTyperService(win32serviceutil.ServiceFramework):
    """VoiceTyper 语音识别 Windows 服务"""

    _svc_name_ = "VoiceTyperServer"
    _svc_display_name_ = "VoiceTyper 语音识别服务"
    _svc_description_ = "基于 FunASR ONNX 的本地语音识别服务，为 VoiceTyper 客户端提供语音转文字功能。"

    def __init__(self, args):
        win32serviceutil.ServiceFramework.__init__(self, args)
        self._stop_event = win32event.CreateEvent(None, 0, 0, None)
        self._server_ctx = None
        self._ioloop = None

    def SvcStop(self):
        """服务停止回调"""
        self.ReportServiceStatus(win32service.SERVICE_STOP_PENDING)
        logger.info("收到停止信号，正在关闭服务...")
        win32event.SetEvent(self._stop_event)

        # 从 IOLoop 线程安全地调度关闭
        if self._ioloop and self._server_ctx:
            self._ioloop.add_callback(self._do_shutdown)

    def _do_shutdown(self):
        """在 IOLoop 线程中执行关闭"""
        if self._server_ctx:
            self._server_ctx.shutdown()

    def SvcDoRun(self):
        """服务启动入口"""
        servicemanager.LogMsg(
            servicemanager.EVENTLOG_INFORMATION_TYPE,
            servicemanager.PYS_SERVICE_STARTED,
            (self._svc_name_, ""),
        )

        try:
            self._run_server()
        except Exception as exc:
            logger.exception(f"服务异常退出: {exc}")
            servicemanager.LogErrorMsg(f"VoiceTyper 服务异常退出: {exc}")
        finally:
            servicemanager.LogMsg(
                servicemanager.EVENTLOG_INFORMATION_TYPE,
                servicemanager.PYS_SERVICE_STOPPED,
                (self._svc_name_, ""),
            )

    def _run_server(self):
        """启动 HTTP 服务"""
        _configure_service_logging()

        # 从注册表读取参数
        server_args = self._load_args_from_registry()
        parser = build_parser()
        args = parser.parse_args(server_args)

        logger.info("以 Windows 服务模式启动")

        # 延迟导入，避免在非服务场景中加载模型
        from .app import create_server, configure_logging
        import tornado.ioloop

        # 为服务线程创建新的事件循环
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)

        self._server_ctx = create_server(args)
        self._ioloop = tornado.ioloop.IOLoop.current()

        self.ReportServiceStatus(win32service.SERVICE_RUNNING)
        logger.info("服务已进入运行状态")

        # 启动 IOLoop（会阻塞直到 stop() 被调用）
        self._ioloop.start()

        # IOLoop 已停止，清理 asyncio 事件循环
        loop.close()
        logger.info("服务已停止")

    def _load_args_from_registry(self) -> list:
        """从注册表读取服务运行参数"""
        import winreg

        try:
            key_path = rf"SYSTEM\CurrentControlSet\Services\{self._svc_name_}\Parameters"
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, key_path) as key:
                args_str, _ = winreg.QueryValueEx(key, _REG_KEY_ARGS)
                logger.info(f"从注册表加载参数: {args_str}")
                return args_str.split()
        except FileNotFoundError:
            logger.warning("注册表中未找到参数，使用默认值")
            return list(_DEFAULT_ARGS)
        except Exception as exc:
            logger.warning(f"读取注册表参数失败: {exc}，使用默认值")
            return list(_DEFAULT_ARGS)


def save_args_to_registry(args: list):
    """将运行参数保存到注册表"""
    import winreg

    svc_name = VoiceTyperService._svc_name_
    key_path = rf"SYSTEM\CurrentControlSet\Services\{svc_name}\Parameters"

    try:
        key = winreg.CreateKeyEx(
            winreg.HKEY_LOCAL_MACHINE,
            key_path,
            0,
            winreg.KEY_SET_VALUE,
        )
        args_str = " ".join(args)
        winreg.SetValueEx(key, _REG_KEY_ARGS, 0, winreg.REG_SZ, args_str)
        winreg.CloseKey(key)
        print(f"已保存运行参数到注册表: {args_str}")
    except PermissionError:
        print("错误: 保存参数到注册表需要管理员权限", file=sys.stderr)
        sys.exit(1)
    except Exception as exc:
        print(f"错误: 保存参数到注册表失败: {exc}", file=sys.stderr)
        sys.exit(1)


def install_service(startup: str = "auto", server_args: Optional[list] = None):
    """安装 Windows 服务

    Args:
        startup: 启动类型，auto 或 manual
        server_args: 传递给 voice-typer-server 的运行参数
    """
    try:
        # 使用 HandleCommandLine 安装，这是 pywin32 推荐的方式
        install_argv = ["", "install"]
        if startup == "auto":
            install_argv.append("--startup=auto")
        else:
            install_argv.append("--startup=manual")
        win32serviceutil.HandleCommandLine(VoiceTyperService, argv=install_argv)

        print(f"服务 '{VoiceTyperService._svc_display_name_}' 已安装")
        print(f"  启动类型: {'自动' if startup == 'auto' else '手动'}")

        # 保存运行参数到注册表
        if server_args:
            save_args_to_registry(server_args)
        else:
            save_args_to_registry(_DEFAULT_ARGS)

    except Exception as exc:
        if "Access is denied" in str(exc) or "拒绝访问" in str(exc):
            print("错误: 安装服务需要管理员权限，请以管理员身份运行命令提示符", file=sys.stderr)
        else:
            print(f"错误: 安装服务失败: {exc}", file=sys.stderr)
        sys.exit(1)


def uninstall_service():
    """卸载 Windows 服务"""
    try:
        # 先尝试停止服务
        try:
            win32serviceutil.StopService(VoiceTyperService._svc_name_)
            print("已停止服务")
        except Exception:
            pass  # 服务可能未运行

        win32serviceutil.RemoveService(VoiceTyperService._svc_name_)
        print(f"服务 '{VoiceTyperService._svc_display_name_}' 已卸载")
    except Exception as exc:
        if "Access is denied" in str(exc) or "拒绝访问" in str(exc):
            print("错误: 卸载服务需要管理员权限", file=sys.stderr)
        else:
            print(f"错误: 卸载服务失败: {exc}", file=sys.stderr)
        sys.exit(1)


def start_service():
    """启动 Windows 服务"""
    try:
        win32serviceutil.StartService(VoiceTyperService._svc_name_)
        print(f"服务 '{VoiceTyperService._svc_display_name_}' 已启动")
    except Exception as exc:
        if "already running" in str(exc).lower() or "已经启动" in str(exc):
            print("服务已在运行中")
        else:
            print(f"错误: 启动服务失败: {exc}", file=sys.stderr)
            sys.exit(1)


def stop_service():
    """停止 Windows 服务"""
    try:
        win32serviceutil.StopService(VoiceTyperService._svc_name_)
        print(f"服务 '{VoiceTyperService._svc_display_name_}' 已停止")
    except Exception as exc:
        if "not been started" in str(exc).lower() or "尚未启动" in str(exc):
            print("服务当前未运行")
        else:
            print(f"错误: 停止服务失败: {exc}", file=sys.stderr)
            sys.exit(1)


if __name__ == "__main__":
    # 当作为服务进程的入口点时（由 SCM 调用）
    if len(sys.argv) == 1:
        servicemanager.Initialize()
        servicemanager.PrepareToHostSingle(VoiceTyperService)
        servicemanager.StartServiceCtrlDispatcher()
    else:
        win32serviceutil.HandleCommandLine(VoiceTyperService)
