# VoiceTyper Windows 版本 - 快速开始指南

## 📦 完整文件清单

### ✅ 需要从 macOS 版本复制的文件
从 `VoiceTyper-macOS/` 目录复制以下文件（无需修改）：

```bash
recorder.py          # 录音模块
asr_client.py        # ASR 客户端
```

### 🆕 Windows 特定文件（已提供完整实现）

```
VoiceTyper-Windows/
├── main.py              # 主程序入口（已提供）
├── config.py            # 配置管理（已提供）
├── controller.py        # 核心控制器（已提供）
├── text_inserter.py     # 文本输入（已提供）
├── indicator.py         # 录音提示窗口（已提供）
├── requirements.txt     # 依赖清单（已提供）
├── build.bat           # 批处理打包脚本（已提供）
├── build.ps1           # PowerShell打包脚本（已提供）
└── README.md           # 使用说明（已提供）
```

## 🚀 三步完成设置

### 第 1 步：准备文件

1. 创建项目目录：
```bash
mkdir VoiceTyper-Windows
cd VoiceTyper-Windows
```

2. 保存所有已提供的 Windows 特定文件到该目录

3. 从 macOS 版本复制两个文件：
```bash
copy ..\VoiceTyper-macOS\recorder.py .
copy ..\VoiceTyper-macOS\asr_client.py .
```

### 第 2 步：测试运行

```bash
# 创建虚拟环境
python -m venv venv

# 激活虚拟环境
venv\Scripts\activate

# 安装依赖
pip install -r requirements.txt

# 运行程序
python main.py
```

**预期结果**：
- 控制台显示初始化信息
- 系统托盘出现绿色麦克风图标
- 配置文件自动创建在 `%APPDATA%\VoiceTyper\`

### 第 3 步：打包发布

```bash
# 运行打包脚本
build.bat

# 或使用 PowerShell
.\build.ps1
```

**生成文件**：`dist\VoiceTyper.exe` (约 50-80MB)

## 🧪 测试功能

### 1. 测试热键
- 按住 `Ctrl+Space`
- 看到录音提示窗口
- 说话：「你好世界」
- 释放热键

### 2. 验证输出
- 打开记事本
- 按住 `Ctrl+Space` 录音
- 释放后等待 1-2 秒
- 识别的文字应自动输入

### 3. 修改配置
1. 右键托盘图标 → "打开配置文件"
2. 修改热键（例如改为 `Alt+V`）：
```yaml
hotkey:
  modifiers:
    - "alt"
  key: "v"
```
3. 重启程序使配置生效

## ⚠️ 常见问题速查

### Q1: pip install 失败
```bash
# 升级 pip
python -m pip install --upgrade pip

# 单独安装问题库
pip install pywin32 --only-binary :all:
```

### Q2: keyboard 热键不响应
```bash
# 以管理员权限运行 CMD
# 右键 CMD → 以管理员身份运行
python main.py
```

### Q3: 找不到 tkinter
重新安装 Python 时勾选 "tcl/tk and IDLE" 选项

### Q4: 打包后运行出错
检查是否缺少隐藏导入：
```bash
pyinstaller --hidden-import=模块名 main.py
```

## 📋 快速检查清单

打包前确认：

- [ ] 所有 9 个文件都已创建
- [ ] 从 macOS 复制了 2 个文件
- [ ] requirements.txt 内容正确
- [ ] Python 版本 >= 3.8
- [ ] 已测试基本功能正常
- [ ] 配置文件可正常读取

打包后确认：

- [ ] dist/VoiceTyper.exe 存在
- [ ] 双击 exe 可运行
- [ ] 托盘图标正常显示
- [ ] 热键功能正常
- [ ] 文字可正常输入

## 🎯 下一步

1. **自定义词库**
   - 打开 `%APPDATA%\VoiceTyper\hotwords.txt`
   - 添加你的专业术语

2. **调整热键**
   - 修改 `config.yaml` 中的 hotkey 配置
   - 重启程序

3. **连接远程服务**
   - 修改 `server.host` 为远程地址
   - 设置 `server.api_key`（如需要）

4. **创建安装程序**（可选）
   - 使用 Inno Setup 创建安装包
   - 更专业的分发方式

## 💡 开发技巧

### 调试模式
修改 `main.py` 中的打包参数：
```bash
# 开发时使用 --console 看日志
pyinstaller --console main.py
```

### 快速测试
创建 `test.py` 快速测试各模块：
```python
# 测试录音
from recorder import AudioRecorder
recorder = AudioRecorder()
recorder.start()
input("按回车停止...")
audio = recorder.stop()
print(f"录音长度: {len(audio)}")

# 测试 ASR
from asr_client import ASRClient
client = ASRClient()
print(f"服务状态: {client.health_check()}")
```

### 性能分析
```python
# 添加性能计时
import time
t0 = time.time()
# 你的代码
print(f"耗时: {time.time() - t0:.2f}s")
```

## 📞 获取帮助

- 查看详细文档：`README.md`
- 开发者指南：`DEVELOPMENT.md`（如已创建）
- 检查日志输出
- 搜索错误信息

## ✅ 完成！

现在你已经有了一个完整可用的 Windows 语音输入工具！

祝使用愉快！🎉