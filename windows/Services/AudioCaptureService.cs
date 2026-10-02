using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceTyper.Core;
using VoiceTyper.Support;

namespace VoiceTyper.Services;

internal enum AudioStartFailureKind
{
    /// <summary>系统隐私设置或 COM 层拒绝访问麦克风。</summary>
    AccessDenied,
    /// <summary>没有可用的输入设备。</summary>
    NoDevice,
    /// <summary>设备存在但打开 / 初始化失败（含不支持的声道数）。</summary>
    DeviceFailure,
}

internal sealed class AudioStartException : Exception
{
    public AudioStartFailureKind Kind { get; }
    public bool IsAccessDenied => Kind == AudioStartFailureKind.AccessDenied;

    public AudioStartException(string message, AudioStartFailureKind kind, Exception? inner = null) : base(message, inner)
    {
        Kind = kind;
    }
}

/// <summary>
/// 流式录音服务。录音期间每凑满 600ms（9600 个 16kHz float32 样本）通过 <see cref="OnChunk"/> 发出；
/// 停止时将剩余尾音通过 <see cref="OnTailChunk"/> 发出。
///
/// <see cref="_lock"/> 同时保护 <see cref="_running"/> 与 <see cref="_chunker"/>：
/// <see cref="AppendSamples"/>（WASAPI 回调线程）与 <see cref="Stop"/>（UI 线程）若不在同一把锁下
/// 原子地"判断 running + 处理缓冲区"，会出现两类竞态（R3-01）：
/// (a) <see cref="Stop"/> 取走尾音之后，一个仍在途的 <see cref="AppendSamples"/> 才把新样本写进
///     已被清空、此后再也不会被读取的缓冲区——那部分音频（通常是松键前最后几十毫秒）静默丢失；
/// (b) <see cref="OnChunk"/>/<see cref="OnTailChunk"/> 分别从音频线程与 UI 线程独立发起，
///     没有强制的先后关系，可能乱序到达。
/// 让投递动作本身（入队到 <see cref="_deliveryQueue"/>）也发生在同一把锁内，即可让"入队顺序"
/// 严格遵循锁定义的临界区顺序，从根上同时解决两个问题。
/// </summary>
internal sealed class AudioCaptureService : IAudioCapturing
{
    public Action<byte[]>? OnChunk { get; set; }
    public Action<byte[]>? OnTailChunk { get; set; }
    /// <summary>
    /// 录音期间输入设备变化（拔麦克风、切换音频设备等）导致本次录音被迫结束时触发一次。
    /// 已采到的音频仍会通过 <see cref="OnTailChunk"/>（本回调触发前已同步投递）正常交给当前会话
    /// 完成识别；这里只做"可见告知"，不做自动重建/自动恢复（F-15 / R2-03）。在 UI 线程触发。
    /// </summary>
    public Action? OnDeviceChanged { get; set; }

    /// <summary>录音期间的实时线性 RMS 电平，约每 30ms 一次，在音频线程触发（调用方须自行回到 UI 线程）。</summary>
    public Action<float>? OnLevel { get; set; }

    /// <summary>本次录音实际使用的输入设备；Start 成功后才有值，Stop 后保留到下次 Start（供收尾日志读取）。</summary>
    public ActiveInputDevice? ActiveDevice { get; private set; }

    public int ChunkSamples { get; } = AppConstants.ChunkSamples;

    private readonly object _lock = new();
    /// <summary>当前这次录音的采集上下文；<see cref="_lock"/> 保护。已停止、但还在等
    /// <c>RecordingStopped</c> 的旧上下文不再被它引用，由各自的回调释放。</summary>
    private CaptureContext? _current;
    private WaveFormat? _captureFormat;
    private AudioChunker _chunker;
    private bool _running;

    /// <summary>"stop() 之后的迟到样本"丢帧计数：R3-01 场景 (a) 的正常代价，不代表出错（R4-07）。</summary>
    private int _droppedNotRunningCount;

    /// <summary>
    /// 串行投递队列：<see cref="OnChunk"/>/<see cref="OnTailChunk"/> 统一从这里触发，而不是分别
    /// 直接从音频线程和 UI 线程调用，从而保证两者之间的相对顺序与 <see cref="_lock"/> 临界区顺序一致。
    /// </summary>
    private readonly BlockingCollection<Action> _deliveryQueue = new();
    private readonly Thread _deliveryThread;
    private bool _disposed;

    /// <summary>
    /// 单次采集的资源与事件订阅，所有权属于它自己：NAudio 会把 <c>RecordingStopped</c> 异步投递到
    /// 创建时的同步上下文，快速 Stop→Start 时旧回调可能晚于新 Start 到达。回调只操作自己捕获的
    /// 上下文，不读取 <see cref="_current"/> 的资源，旧采集的事件就不会污染新一次录音。
    /// </summary>
    private sealed class CaptureContext
    {
        public WasapiCapture Capture { get; }
        public MMDevice Device { get; }
        /// <summary>在订阅事件之前赋值，之后只读。</summary>
        public BufferedWaveProvider? Input { get; set; }
        public ISampleProvider? Resampled { get; set; }
        public EventHandler<WaveInEventArgs>? DataHandler { get; set; }
        public EventHandler<StoppedEventArgs>? StoppedHandler { get; set; }
        private int _released;

        public CaptureContext(WasapiCapture capture, MMDevice device)
        {
            Capture = capture;
            Device = device;
        }

        public bool IsReleased => Volatile.Read(ref _released) != 0;

        /// <summary>退订并释放采集器与设备；幂等，可从任意线程调用。</summary>
        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            if (DataHandler is not null) Capture.DataAvailable -= DataHandler;
            if (StoppedHandler is not null) Capture.RecordingStopped -= StoppedHandler;
            try { Capture.Dispose(); }
            catch (Exception ex) { AppLog.Warn("audio", $"释放采集器异常: {ex.Message}"); }
            try { Device.Dispose(); }
            catch (Exception ex) { AppLog.Warn("audio", $"释放设备异常: {ex.Message}"); }
        }
    }

    public bool IsRunning
    {
        get { lock (_lock) return _running; }
    }

    public AudioCaptureService()
    {
        _chunker = new AudioChunker(ChunkSamples);
        _deliveryThread = new Thread(RunDeliveryLoop) { IsBackground = true, Name = "VoiceTyper.AudioDelivery" };
        _deliveryThread.Start();
    }

    public void Start(AudioInputPolicy policy)
    {
        lock (_lock)
        {
            if (_running) return;

            MMDevice device;
            try
            {
                // MMDeviceEnumerator 必须释放：GetDefaultAudioEndpoint 返回的 MMDevice 持有独立
                // COM 引用，枚举器本身用完即弃（R1-3）。
                using var enumerator = new MMDeviceEnumerator();
                device = OpenInputDevice(enumerator, policy);
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070005u)
            {
                throw new AudioStartException(L10n.T("麦克风访问被拒绝，请在 Windows 设置中允许应用访问麦克风"), AudioStartFailureKind.AccessDenied, ex);
            }
            catch (Exception ex)
            {
                throw new AudioStartException(L10n.T("未找到可用麦克风设备"), AudioStartFailureKind.NoDevice, ex);
            }

            // 上下文一经创建就拥有 capture 与 device；失败路径统一经 FailStart 释放。
            CaptureContext? created = null;
            try
            {
                var capture = new WasapiCapture(device, useEventSync: true);
                var ctx = new CaptureContext(capture, device);
                created = ctx;
                _captureFormat = capture.WaveFormat;

                ctx.Input = new BufferedWaveProvider(_captureFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(2),
                    DiscardOnBufferOverflow = true,
                    // 关键（R1-1）：默认 ReadFully=true 时 Read 在数据不足会补零并返回请求长度，
                    // WdlResamplingSampleProvider 透传这一行为，使 OnCaptureDataAvailable 里的
                    // "读到短读/0 才退出"循环永不退出——在 WASAPI 回调线程上无限生成静音。
                    ReadFully = false,
                };

                ctx.Resampled = BuildResamplingChain(ctx.Input, _captureFormat.Channels);

                ctx.DataHandler = (_, e) => OnCaptureDataAvailable(ctx, e);
                ctx.StoppedHandler = (_, e) => OnCaptureStopped(ctx, e);
                capture.DataAvailable += ctx.DataHandler;
                capture.RecordingStopped += ctx.StoppedHandler;

                _chunker = new AudioChunker(ChunkSamples);
                _droppedNotRunningCount = 0;
                _lastLevelTicks = 0;
                _current = ctx;
                // running 必须在 StartRecording 之前、且在锁内置位：DataAvailable 理论上可能在
                // StartRecording() 返回后的极窄窗口内几乎立即触发，若仍在锁外才置位，这段窗口里
                // 到达的样本会被误判为"stop() 已经跑过"而丢弃，且这本身就是一处不受锁保护的
                // 跨线程写（对齐 macOS R4-07 的教训）。
                _running = true;
                capture.StartRecording();
            }
            catch (AudioStartException)
            {
                FailStart(created, device);
                throw;
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070005u)
            {
                FailStart(created, device);
                throw new AudioStartException(L10n.T("麦克风访问被拒绝，请在 Windows 设置中允许应用访问麦克风"), AudioStartFailureKind.AccessDenied, ex);
            }
            catch (Exception ex)
            {
                FailStart(created, device);
                throw new AudioStartException(L10n.F("启动录音失败: {0}", ex.Message), AudioStartFailureKind.DeviceFailure, ex);
            }
        }

        AppLog.Info("audio", $"录音启动: format={_captureFormat}, transport={ActiveDevice?.Transport.LogName() ?? "-"}{(ActiveDevice?.SwitchedByAuto == true ? "*" : "")}");
    }

    /// <summary>必须在持有 <see cref="_lock"/> 时调用：撤销本次 Start 的所有状态并释放资源。</summary>
    private void FailStart(CaptureContext? created, MMDevice device)
    {
        _running = false;
        _current = null;
        if (created is not null) created.Release();
        else device.Dispose(); // WasapiCapture 构造失败：上下文尚未接管设备
    }

    /// <summary>
    /// 按策略打开输入设备。用 <see cref="Role.Console"/> 而不是 <see cref="Role.Communications"/>：
    /// 后者在蓝牙耳机连接时几乎总是指向「免提通话」端点，一打开就把耳机切到电话级音质
    /// （VW-16）。设置页手选的设备 ID 不存在（设备被拔了）时回落系统默认。
    /// </summary>
    private MMDevice OpenInputDevice(MMDeviceEnumerator enumerator, AudioInputPolicy policy)
    {
        MMDevice systemDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        try
        {
            IReadOnlyList<AudioDeviceInfo> devices = Array.Empty<AudioDeviceInfo>();
            var outputTransport = AudioTransport.Unknown;
            if (policy is not AudioInputPolicy.SystemDefault)
            {
                devices = AudioDeviceCatalog.ListCaptureDevices();
                if (policy is AudioInputPolicy.Automatic) outputTransport = AudioDeviceCatalog.DefaultRenderTransport(enumerator);
            }

            var (deviceId, switchedByAuto) = AudioInputSelector.Resolve(policy, devices, systemDefault.ID, outputTransport);
            var chosen = systemDefault;
            if (deviceId is not null && deviceId != systemDefault.ID)
            {
                try
                {
                    chosen = enumerator.GetDevice(deviceId);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("audio", $"打开指定输入设备失败，回落系统默认: {ex.Message}");
                    switchedByAuto = false;
                }
            }

            var info = AudioDeviceCatalog.Describe(chosen);
            ActiveDevice = new ActiveInputDevice(info.Name, info.Transport, switchedByAuto);
            if (!ReferenceEquals(chosen, systemDefault)) systemDefault.Dispose();
            return chosen;
        }
        catch
        {
            systemDefault.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 停止录音，发出尾音帧。即使没有尾音也会以空 Data 触发 <see cref="OnTailChunk"/>，
    /// 让调用方知道录音流已经结束。
    /// </summary>
    public void Stop()
    {
        WasapiCapture? capture;
        bool wasRunning;
        int tailLength;
        lock (_lock)
        {
            wasRunning = _running;
            capture = _current?.Capture;
            if (_running)
            {
                _running = false;
                tailLength = DrainAndEnqueueTailLocked();
            }
            else
            {
                tailLength = 0;
            }
        }
        if (!wasRunning) return;

        try { capture?.StopRecording(); }
        catch (Exception ex) { AppLog.Warn("audio", $"StopRecording 异常: {ex.Message}"); }

        LogDroppedBuffersIfAny();
        AppLog.Info("audio", $"录音停止，尾音 {tailLength} bytes");
    }

    /// <summary>不发出尾音直接终止（如错误清理、用户主动取消）。</summary>
    public void StopWithoutResult()
    {
        WasapiCapture? capture;
        lock (_lock)
        {
            if (!_running) return;
            capture = _current?.Capture;
            _running = false;
            _chunker.Drain();
        }
        try { capture?.StopRecording(); }
        catch { /* swallow */ }
        LogDroppedBuffersIfAny();
    }

    /// <summary>必须在持有 <see cref="_lock"/> 时调用；返回尾音字节数供调用方日志使用。</summary>
    private int DrainAndEnqueueTailLocked()
    {
        var tail = _chunker.Drain();
        var handler = OnTailChunk;
        EnqueueDelivery(() => handler?.Invoke(tail));
        return tail.Length;
    }

    /// <summary>
    /// 重采样到 16kHz / mono / float32（IEEE float）。
    /// 选 WDL 而非 MediaFoundationResampler：纯托管、不依赖 MF DLL，且对语音 16kHz 重采样质量足够。
    /// 声道处理显式分三种（R1-3）：ToMono() 内部是 StereoToMonoSampleProvider，
    /// 源声道数 != 2 会抛 ArgumentException，被外层吞成笼统的"启动录音失败"。
    /// 单独成方法是为了让重采样质量测试与生产共用同一条链。
    /// </summary>
    internal static ISampleProvider BuildResamplingChain(BufferedWaveProvider input, int channels)
    {
        var sampleProvider = input.ToSampleProvider();
        switch (channels)
        {
            case 1:
                break;
            case 2:
                sampleProvider = sampleProvider.ToMono();
                break;
            default:
                throw new AudioStartException(
                    L10n.F("暂不支持 {0} 声道的输入设备，请在系统声音设置中改用单声道或立体声麦克风", channels),
                    AudioStartFailureKind.DeviceFailure);
        }
        return new WdlResamplingSampleProvider(sampleProvider, AppConstants.TargetSampleRate);
    }

    /// <summary>
    /// 把重采样器当前可读的样本全部拉出来交给 <paramref name="sink"/>（缓冲区与长度，只在回调内有效）。
    /// 转换本身与 running 无关，running 判定只在真正写入 chunker 时做（见 <see cref="AppendSamples"/>）。
    /// </summary>
    internal static void DrainResampled(ISampleProvider resampled, Action<float[], int> sink)
    {
        var pool = ArrayPool<float>.Shared;
        var tmp = pool.Rent(4096);
        try
        {
            // 迭代上限保险（R1-1）：单次回调最多消费约 3s @ 16kHz 的重采样输出。
            // 正常路径远达不到；一旦触顶说明补零行为又回来了，记一次 warn 并退出，
            // 但这不能替代 ReadFully=false。
            int maxIterations = (AppConstants.TargetSampleRate * 3) / tmp.Length + 1;
            int read;
            int iterations = 0;
            while ((read = resampled.Read(tmp, 0, tmp.Length)) > 0)
            {
                sink(tmp, read);
                if (read < tmp.Length) break;
                if (++iterations >= maxIterations)
                {
                    AppLog.Warn("audio", "重采样单次回调迭代触顶，提前退出（疑似补零回退）");
                    break;
                }
            }
        }
        finally
        {
            pool.Return(tmp);
        }
    }

    private void OnCaptureDataAvailable(CaptureContext ctx, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || ctx.IsReleased) return;
        var input = ctx.Input;
        var resampled = ctx.Resampled;
        if (input is null || resampled is null) return;

        try
        {
            input.AddSamples(e.Buffer, 0, e.BytesRecorded);

            DrainResampled(resampled, (buffer, count) => AppendSamples(ctx, buffer, count));
        }
        catch (Exception ex)
        {
            AppLog.Error("audio", "处理音频数据异常", ex);
        }
    }

    private void OnCaptureStopped(CaptureContext ctx, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            AppLog.Warn("audio", $"音频设备意外停止（可能是设备被拔出/切换）: {e.Exception.Message}");
            // 只有仍是当前录音的采集器才需要收尾；旧采集器的异常停止不得影响新的一次录音。
            if (IsCurrent(ctx)) UiDispatcher.Post(() => HandleDeviceChangedDuringRecording(ctx));
        }

        // NAudio 的 StopRecording 是异步的：真正的资源释放要等到 RecordingStopped 到达才做，
        // 否则正常 Stop() 路径从不释放采集器与设备，每次听写泄漏一组 COM 对象与采集线程（R1-3）。
        // 快速连按热键时新的 Start() 可能已经换了 _current——此时只释放旧上下文自己的资源。
        ctx.Release();
        lock (_lock)
        {
            if (ReferenceEquals(_current, ctx)) _current = null;
        }
    }

    private bool IsCurrent(CaptureContext ctx)
    {
        lock (_lock) return ReferenceEquals(_current, ctx);
    }

    /// <summary>
    /// 保留已采到的音频交给当前会话完成识别（走与正常停止相同的尾音刷出路径），
    /// 但明确告知用户设备已变化、本次录音已结束——不做自动重建/自动恢复（F-15 / R2-03）。
    /// </summary>
    private void HandleDeviceChangedDuringRecording(CaptureContext ctx)
    {
        bool wasRunning;
        lock (_lock)
        {
            // 迟到的设备变化回调属于旧采集：不得结束现在这一次录音。
            if (!ReferenceEquals(ctx, _current)) return;
            wasRunning = _running;
            if (_running)
            {
                _running = false;
                DrainAndEnqueueTailLocked();
            }
        }
        if (!wasRunning) return;

        LogDroppedBuffersIfAny();
        OnDeviceChanged?.Invoke();
    }

    private long _lastLevelTicks;

    /// <summary>线性 RMS 电平回调，节流到约 30ms 一次——波形动画用不到更高频率，
    /// 却会让 UI 线程的投递队列白白积压。</summary>
    private void ReportLevel(float[] buffer, int count)
    {
        var callback = OnLevel;
        if (callback is null || count <= 0) return;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastLevelTicks, now).TotalMilliseconds < 30) return;
        _lastLevelTicks = now;

        double sum = 0;
        for (int i = 0; i < count; i++) sum += buffer[i] * (double)buffer[i];
        callback((float)Math.Sqrt(sum / count));
    }

    private void AppendSamples(CaptureContext ctx, float[] buffer, int count)
    {
        lock (_lock)
        {
            if (!_running || !ReferenceEquals(ctx, _current))
            {
                // stop() 已经把 running 置 false 并取走尾音，或这批样本来自已被替换的旧采集：
                // 必然是"迟到"的，若仍写进 chunker 会成为永远不会被 flush 的孤儿数据，
                // 甚至混进新一次录音（R3-01 场景 a）。
                _droppedNotRunningCount++;
                return;
            }

            var chunks = _chunker.Append(buffer.AsSpan(0, count));
            var handler = OnChunk;
            // 入队动作必须在锁内完成，才能保证与 Stop()/设备变化那侧的入队顺序一致（见类注释）。
            foreach (var chunk in chunks)
            {
                EnqueueDelivery(() => handler?.Invoke(chunk));
            }
        }
        // 电平回调在锁外触发，避免调用方的代码在持锁时运行。
        ReportLevel(buffer, count);
    }

    /// <summary>只记计数，不含音频内容（AGENTS.md 日志约束）。"迟到"丢帧是 stop() 与音频线程
    /// 竞态下的正常代价，不代表出错，用 info 级别（R4-07）。</summary>
    private void LogDroppedBuffersIfAny()
    {
        int notRunning;
        lock (_lock)
        {
            notRunning = _droppedNotRunningCount;
            _droppedNotRunningCount = 0;
        }
        if (notRunning > 0)
        {
            AppLog.Info("audio", $"录音期间丢弃了 {notRunning} 个音频缓冲区（stop() 之后的迟到样本，属预期行为）");
        }
    }

    private void RunDeliveryLoop()
    {
        foreach (var action in _deliveryQueue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Error("audio", "音频投递任务异常", ex);
            }
        }
    }

    /// <summary>释放当前上下文（Dispose 与启动失败路径）。已停止、仍在等 RecordingStopped 的旧上下文
    /// 由各自的回调释放。</summary>
    private void Cleanup()
    {
        CaptureContext? ctx;
        lock (_lock)
        {
            ctx = _current;
            _current = null;
        }
        ctx?.Release();
    }

    /// <summary>投递队列关闭后（Dispose 之后的迟到调用）丢弃任务，而不是抛出到音频线程。</summary>
    private void EnqueueDelivery(Action action)
    {
        try
        {
            if (!_deliveryQueue.TryAdd(action)) AppLog.Debug("audio", "投递队列已关闭，丢弃任务");
        }
        catch (InvalidOperationException) { AppLog.Debug("audio", "投递队列已关闭，丢弃任务"); }
        catch (ObjectDisposedException) { AppLog.Debug("audio", "投递队列已释放，丢弃任务"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWithoutResult();
        Cleanup();
        _deliveryQueue.CompleteAdding();
        // 投递线程仍在跑（回调卡住）时不释放队列：它随后会访问已释放的集合。交给进程退出回收。
        if (_deliveryThread.Join(TimeSpan.FromSeconds(2))) _deliveryQueue.Dispose();
        else AppLog.Warn("audio", "音频投递线程 2 秒内未退出，保留队列由进程退出回收");
    }
}
