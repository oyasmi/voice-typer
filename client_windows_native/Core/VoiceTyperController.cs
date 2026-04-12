using VoiceTyper.Services;
using VoiceTyper.Support;

namespace VoiceTyper.Core;

/// <summary>
/// 核心控制器：串联热键 → 录音 → 识别 → 文本插入的完整流程。
/// 状态机设计与 macOS Swift 版完全一致。
/// 所有回调均在 UI 线程执行（通过键盘钩子的消息循环保证），无需加锁。
/// </summary>
internal sealed class VoiceTyperController : IDisposable
{
    private readonly HotkeyService _hotkeyService = new();
    private readonly AudioCaptureService _audioCaptureService = new();
    private readonly ASRClient _asrClient = new();
    private readonly TextInsertionService _textInsertionService = new();

    private readonly AppConfig _config;
    private readonly List<string> _hotwords;
    private bool _isRecording;
    private bool _isRunning;

    public event Action<AppState>? OnStateChange;
    public int InputCount { get; private set; }
    public int CharCount { get; private set; }

    public VoiceTyperController(AppConfig config, List<string> hotwords)
    {
        _config = config;
        _hotwords = hotwords;
    }

    public void Start()
    {
        if (_isRunning) return;

        _hotkeyService.OnPress += BeginRecording;
        _hotkeyService.OnRelease += FinishRecording;
        _hotkeyService.Start(_config.Hotkey);

        _isRunning = true;
        OnStateChange?.Invoke(AppState.Idle);
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _hotkeyService.OnPress -= BeginRecording;
        _hotkeyService.OnRelease -= FinishRecording;
        _hotkeyService.Stop();

        if (_isRecording)
        {
            _audioCaptureService.StopWithoutResult();
            _isRecording = false;
        }

        _isRunning = false;
    }

    public async Task<bool> HealthCheckAsync()
    {
        return await _asrClient.HealthCheckAsync(_config.Server);
    }

    private void BeginRecording()
    {
        if (!_isRunning || _isRecording) return;
        _isRecording = true;

        try
        {
            _audioCaptureService.Start();
            OnStateChange?.Invoke(AppState.Recording);
        }
        catch (Exception ex)
        {
            _isRecording = false;
            AppLog.Error("开始录音失败", ex);
            OnStateChange?.Invoke(AppState.Error);
        }
    }

    /// <summary>
    /// 热键释放后的完整流程：停止录音 → 识别 → 输入文本。
    /// 使用 async void 因为这是事件处理器，异常在内部捕获。
    /// </summary>
    private async void FinishRecording()
    {
        if (!_isRecording) return;
        _isRecording = false;

        try
        {
            // 1. 停止录音并获取音频数据（含格式转换）
            var (audioData, duration) = _audioCaptureService.Stop();

            // 2. 检查录音时长
            if (duration.TotalSeconds < Constants.MinimumRecordingDuration)
            {
                AppLog.Info($"录音过短: {duration.TotalSeconds:F1}s，已忽略");
                OnStateChange?.Invoke(AppState.Idle);
                return;
            }

            AppLog.Info($"录音完成: {duration.TotalSeconds:F1}s, {audioData.Length} bytes");

            // 3. 发送识别请求
            OnStateChange?.Invoke(AppState.Recognizing);
            var text = await _asrClient.RecognizeAsync(audioData, _hotwords, _config.Server);

            if (string.IsNullOrWhiteSpace(text))
            {
                AppLog.Info("未识别到文字");
                OnStateChange?.Invoke(AppState.Idle);
                return;
            }

            // 4. 插入文本
            OnStateChange?.Invoke(AppState.Inserting);
            _textInsertionService.Insert(text);

            InputCount++;
            CharCount += text.Length;
            AppLog.Info($"已输入: \"{text}\"");

            OnStateChange?.Invoke(AppState.Idle);
        }
        catch (Exception ex)
        {
            AppLog.Error("识别流程出错", ex);
            OnStateChange?.Invoke(AppState.Error);

            // 2 秒后自动恢复就绪状态
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                OnStateChange?.Invoke(AppState.Idle);
            });
        }
    }

    public void Dispose()
    {
        Stop();
        _hotkeyService.Dispose();
        _audioCaptureService.Dispose();
        _asrClient.Dispose();
    }
}
