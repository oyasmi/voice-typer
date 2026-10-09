using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
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

/// <summary>一次异步启动（<see cref="IAudioCapturing.BeginStart"/>）的结局。</summary>
internal abstract record AudioStartResult
{
    public sealed record Started(AudioStartTimings Timings) : AudioStartResult;
    /// <summary>启动完成前调用方已经撤销（<see cref="IAudioCapturing.StopWithoutResult"/>）。</summary>
    public sealed record Cancelled : AudioStartResult;
    public sealed record Failed(AudioStartException Error) : AudioStartResult;
}

/// <summary>
/// 打开麦克风的分段耗时（<see cref="Stopwatch"/> tick），并入听写耗时摘要。只允许数字与布尔
/// （<c>DictationMetricsTests</c> 用反射钉死）。
/// </summary>
internal sealed class AudioStartTimings
{
    /// <summary>发起启动到控制线程开始处理：控制线程正忙（例如设备变化后的预解析）时才不为 0。</summary>
    public long QueueTicks;
    /// <summary>选定并打开输入端点（枚举设备、读端点属性；命中缓存时只剩按 ID 取设备）。</summary>
    public long ResolveTicks;
    /// <summary>创建 <see cref="WasapiCapture"/>：激活 IAudioClient、读取混音格式。</summary>
    public long ActivateTicks;
    /// <summary><see cref="WasapiCapture.StartRecording"/>：IAudioClient.Initialize 与采集线程启动。</summary>
    public long InitTicks;
    /// <summary>输入端点是否命中缓存（见 <see cref="AudioCaptureService.PrepareInput"/>）。</summary>
    public bool DeviceCached;
}

/// <summary>
/// 流式录音服务。录音期间每凑满 600ms（9600 个 16kHz float32 样本）通过 <see cref="OnChunk"/> 发出；
/// 停止时将剩余尾音通过 <see cref="OnTailChunk"/> 发出。
///
/// <b>控制线程</b>：解析输入端点、创建 <see cref="WasapiCapture"/>、<c>StartRecording</c>（内部是
/// IAudioClient.Initialize，实测按下热键后 0.4~1.2s 都耗在这几步）、<c>StopRecording</c> 与释放，
/// 全部串行地在专用 MTA 线程 <see cref="_controlThread"/> 上执行。此前它们在 UI 线程上同步跑完，
/// 而全局键盘 / 鼠标钩子也装在 UI 线程：打开麦克风的这段时间里整机键鼠输入都要等钩子返回，
/// HUD 也只能等麦克风打开后才出现。调用方因此改为 <see cref="BeginStart"/> + 完成回调。
/// 所有对 WASAPI 对象的调用都在同一线程上排队，也就不存在"Start 还没做完、Stop 已经插进来"的交错。
///
/// <b>输入端点缓存</b>：「自动」策略每次都要枚举全部采集端点并读属性、再读默认播放端点。解析结果
/// 按策略缓存，系统端点拓扑变化（增删设备、默认设备切换、启停用）时由 <see cref="IMMNotificationClient"/>
/// 作废并在后台重新预解析；通知注册失败时不启用缓存（宁可慢，不能用错设备）。
///
/// <see cref="_lock"/> 同时保护 <see cref="_running"/>、<see cref="_chunker"/> 与启动撤销标记：
/// <see cref="AppendSamples"/>（WASAPI 回调线程）与 <see cref="Stop"/>（UI 线程）若不在同一把锁下
/// 原子地"判断 running + 处理缓冲区"，会出现两类竞态（R3-01）：
/// (a) <see cref="Stop"/> 取走尾音之后，一个仍在途的 <see cref="AppendSamples"/> 才把新样本写进
///     已被清空、此后再也不会被读取的缓冲区——那部分音频（通常是松键前最后几十毫秒）静默丢失；
/// (b) <see cref="OnChunk"/>/<see cref="OnTailChunk"/> 分别从音频线程与 UI 线程独立发起，
///     没有强制的先后关系，可能乱序到达。
/// 让投递动作本身（入队到 <see cref="_deliveryQueue"/>）也发生在同一把锁内，即可让"入队顺序"
/// 严格遵循锁定义的临界区顺序，从根上同时解决两个问题。这把锁不在任何 WASAPI 调用期间持有。
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

    /// <summary>本次录音实际使用的输入设备；启动成功后才有值，停止后保留到下次启动（供收尾日志读取）。
    /// 在控制线程写入、完成回调之后读取。</summary>
    public ActiveInputDevice? ActiveDevice { get; private set; }

    public int ChunkSamples { get; } = AppConstants.ChunkSamples;

    private readonly object _lock = new();
    /// <summary>当前这次录音的采集上下文；<see cref="_lock"/> 保护。已停止、但还在等
    /// <c>RecordingStopped</c> 的旧上下文不再被它引用，由各自的回调释放。</summary>
    private CaptureContext? _current;
    private WaveFormat? _captureFormat;
    private AudioChunker _chunker;
    private bool _running;
    /// <summary>每次 <see cref="BeginStart"/> 递增；<see cref="_lock"/> 保护。</summary>
    private long _startGeneration;
    /// <summary>尚未完成、也未被撤销的那次启动的代号，0 表示没有。撤销 = 把它清零，控制线程在
    /// 各个检查点发现代号对不上就放弃本次启动。<see cref="_lock"/> 保护。</summary>
    private long _pendingStart;

    /// <summary>"stop() 之后的迟到样本"丢帧计数：R3-01 场景 (a) 的正常代价，不代表出错（R4-07）。</summary>
    private int _droppedNotRunningCount;

    /// <summary>
    /// 串行投递队列：<see cref="OnChunk"/>/<see cref="OnTailChunk"/> 统一从这里触发，而不是分别
    /// 直接从音频线程和 UI 线程调用，从而保证两者之间的相对顺序与 <see cref="_lock"/> 临界区顺序一致。
    /// </summary>
    private readonly BlockingCollection<Action> _deliveryQueue = new();
    private readonly Thread _deliveryThread;

    /// <summary>控制线程的任务队列，见类注释。</summary>
    private readonly BlockingCollection<Action> _controlQueue = new();
    private readonly Thread _controlThread;
    private bool _disposed;

    // ─── 以下字段只在控制线程上访问 ─────────────────────────────
    private MMDeviceEnumerator? _enumerator;
    private DeviceNotificationClient? _notificationClient;
    /// <summary>端点通知注册成功才允许缓存：收不到拓扑变化就无从知道缓存何时过期。</summary>
    private bool _cacheEnabled;
    private ResolvedInput? _resolved;
    private int _resolvedTopologyVersion;
    /// <summary>最近一次预解析 / 启动所用的策略；拓扑变化后按它在后台重新预解析。</summary>
    private AudioInputPolicy? _preparedPolicy;

    // ─── 跨线程（端点通知在任意 COM 线程触发）─────────────────────
    private int _topologyVersion;
    private int _refreshQueued;

    /// <summary>某一策略解析出的端点 ID 与描述（设备名、传输类型）。</summary>
    private sealed record ResolvedInput(AudioInputPolicy Policy, string DeviceId, ActiveInputDevice Device);

    /// <summary>
    /// 单次采集的资源与事件订阅，所有权属于它自己：快速 Stop→Start 时旧采集的 <c>RecordingStopped</c>
    /// 可能晚于新 Start 到达。回调只操作自己捕获的上下文，不读取 <see cref="_current"/> 的资源，
    /// 旧采集的事件就不会污染新一次录音。
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

        /// <summary>退订并释放采集器与设备；幂等。正常路径在控制线程上调用。</summary>
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

    /// <summary>端点拓扑变化通知。在任意 COM 线程触发，只作废缓存，不得在回调里回调枚举器。</summary>
    private sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly Action _changed;
        public DeviceNotificationClient(Action changed) => _changed = changed;
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed();
        public void OnDeviceAdded(string pwstrDeviceId) => _changed();
        public void OnDeviceRemoved(string deviceId) => _changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _changed();
        // 端点属性变化（驱动频繁上报的各类属性）不影响选哪个端点；设备名变化只影响 HUD 文案，不值得重解析。
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
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
        // WASAPI 推荐在 MTA 中使用；UI 线程是 STA。
        _controlThread = new Thread(RunControlLoop) { IsBackground = true, Name = "VoiceTyper.AudioControl" };
        _controlThread.SetApartmentState(ApartmentState.MTA);
        _controlThread.Start();
    }

    // ─── 启动 ─────────────────────────────────────────────────────

    /// <summary>
    /// 在控制线程上提前解析输入端点并缓存，不打开麦克风。空闲时调用，让按下热键时省掉设备枚举。
    /// </summary>
    public void PrepareInput(AudioInputPolicy policy) => EnqueueControl(() => PrepareOnControlThread(policy));

    /// <summary>
    /// 异步打开麦克风。<paramref name="completed"/> 恰好触发一次，在控制线程上（队列已关闭时在调用线程上），
    /// 调用方须自行回到 UI 线程。完成前调用 <see cref="StopWithoutResult"/> 或 <see cref="Stop"/> 会撤销本次
    /// 启动：已经打开的采集会被随即停掉，回调得到 <see cref="AudioStartResult.Cancelled"/>（或在撤销前刚好完成时
    /// 得到 Started——此时撤销本身已经把它停掉）。
    /// </summary>
    public void BeginStart(AudioInputPolicy policy, Action<AudioStartResult> completed)
    {
        var requestedAt = Stopwatch.GetTimestamp();
        long generation;
        lock (_lock)
        {
            generation = ++_startGeneration;
            _pendingStart = generation;
        }
        if (EnqueueControl(() => RunStart(policy, generation, requestedAt, completed))) return;

        lock (_lock)
        {
            if (_pendingStart == generation) _pendingStart = 0;
        }
        completed(new AudioStartResult.Failed(new AudioStartException(L10n.T("开始录音失败"), AudioStartFailureKind.DeviceFailure)));
    }

    /// <summary>同步打开麦克风，阻塞调用线程直到完成。只供不在 UI 线程上的一次性探测使用（<see cref="Core.MicPermissionProbe"/>）。</summary>
    public void Start(AudioInputPolicy policy)
    {
        using var done = new ManualResetEventSlim();
        AudioStartResult? result = null;
        BeginStart(policy, r =>
        {
            result = r;
            done.Set();
        });
        done.Wait();
        if (result is AudioStartResult.Failed failed) ExceptionDispatchInfo.Capture(failed.Error).Throw();
    }

    private bool IsPending(long generation)
    {
        lock (_lock) return _pendingStart == generation;
    }

    private void RunStart(AudioInputPolicy policy, long generation, long requestedAt, Action<AudioStartResult> completed)
    {
        var timings = new AudioStartTimings { QueueTicks = Stopwatch.GetTimestamp() - requestedAt };
        AudioStartResult result;
        try
        {
            result = StartOnControlThread(policy, generation, timings);
        }
        catch (AudioStartException ex)
        {
            result = new AudioStartResult.Failed(ex);
        }
        catch (Exception ex)
        {
            result = new AudioStartResult.Failed(new AudioStartException(L10n.F("启动录音失败: {0}", ex.Message), AudioStartFailureKind.DeviceFailure, ex));
        }

        lock (_lock)
        {
            if (_pendingStart == generation) _pendingStart = 0;
        }

        if (result is AudioStartResult.Started)
        {
            AppLog.Info("audio", $"录音启动: format={_captureFormat}, transport={ActiveDevice?.Transport.LogName() ?? "-"}{(ActiveDevice?.SwitchedByAuto == true ? "*" : "")}, "
                + $"queue={Ms(timings.QueueTicks)}ms resolve={Ms(timings.ResolveTicks)}ms{(timings.DeviceCached ? "(cached)" : "")} "
                + $"activate={Ms(timings.ActivateTicks)}ms init={Ms(timings.InitTicks)}ms");
        }

        try { completed(result); }
        catch (Exception ex) { AppLog.Error("audio", "录音启动完成回调异常", ex); }
    }

    private static long Ms(long ticks) => (long)(ticks * 1000.0 / Stopwatch.Frequency);

    private AudioStartResult StartOnControlThread(AudioInputPolicy policy, long generation, AudioStartTimings timings)
    {
        if (!IsPending(generation)) return new AudioStartResult.Cancelled();
        lock (_lock)
        {
            if (_running) return new AudioStartResult.Started(timings);
        }

        var resolveStartedAt = Stopwatch.GetTimestamp();
        MMDevice device;
        ActiveInputDevice deviceInfo;
        try
        {
            (device, deviceInfo, timings.DeviceCached) = OpenInputDevice(policy);
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005u)
        {
            throw new AudioStartException(L10n.T("麦克风访问被拒绝，请在 Windows 设置中允许应用访问麦克风"), AudioStartFailureKind.AccessDenied, ex);
        }
        catch (Exception ex)
        {
            throw new AudioStartException(L10n.T("未找到可用麦克风设备"), AudioStartFailureKind.NoDevice, ex);
        }
        timings.ResolveTicks = Stopwatch.GetTimestamp() - resolveStartedAt;
        ActiveDevice = deviceInfo;

        // 上下文一经创建就拥有 capture 与 device；失败路径统一经 FailStart 释放。
        CaptureContext? created = null;
        try
        {
            var activateStartedAt = Stopwatch.GetTimestamp();
            // 在控制线程（没有 SynchronizationContext）上构造：NAudio 会在采集线程上直接触发
            // RecordingStopped，释放动作由 OnCaptureStopped 再排回控制线程。
            var capture = new WasapiCapture(device, useEventSync: true);
            var ctx = new CaptureContext(capture, device);
            created = ctx;
            var format = capture.WaveFormat;
            timings.ActivateTicks = Stopwatch.GetTimestamp() - activateStartedAt;

            ctx.Input = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                // 关键（R1-1）：默认 ReadFully=true 时 Read 在数据不足会补零并返回请求长度，
                // WdlResamplingSampleProvider 透传这一行为，使 OnCaptureDataAvailable 里的
                // "读到短读/0 才退出"循环永不退出——在 WASAPI 回调线程上无限生成静音。
                ReadFully = false,
            };

            ctx.Resampled = BuildResamplingChain(ctx.Input, format.Channels);

            ctx.DataHandler = (_, e) => OnCaptureDataAvailable(ctx, e);
            ctx.StoppedHandler = (_, e) => OnCaptureStopped(ctx, e);
            capture.DataAvailable += ctx.DataHandler;
            capture.RecordingStopped += ctx.StoppedHandler;

            lock (_lock)
            {
                // 打开设备期间调用方已经撤销：不再启动。
                if (_pendingStart != generation)
                {
                    created = null;
                    ctx.Release();
                    return new AudioStartResult.Cancelled();
                }
                _captureFormat = format;
                _chunker = new AudioChunker(ChunkSamples);
                _droppedNotRunningCount = 0;
                _lastLevelTicks = 0;
                _current = ctx;
                // running 必须在 StartRecording 之前、且在锁内置位：DataAvailable 可能在
                // StartRecording() 返回后的极窄窗口内几乎立即触发，若仍在锁外才置位，这段窗口里
                // 到达的样本会被误判为"stop() 已经跑过"而丢弃（对齐 macOS R4-07 的教训）。
                // 置位之后的撤销走正常的 StopWithoutResult：它排在本任务之后的 StopRecording 会停掉采集。
                _running = true;
            }

            var initStartedAt = Stopwatch.GetTimestamp();
            capture.StartRecording();
            timings.InitTicks = Stopwatch.GetTimestamp() - initStartedAt;
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
            // HRESULT 十六进制拼进消息：COMException 的 Message 不总包含它，而设置页横幅与探测日志
            // 只拿得到这条消息（AUDCLNT_E_* 等错误码是真机排障的唯一线索）。
            throw new AudioStartException(L10n.F("启动录音失败: {0}", $"{ex.Message} (0x{ex.HResult:X8})"),
                AudioStartFailureKind.DeviceFailure, ex);
        }

        return IsPending(generation) ? new AudioStartResult.Started(timings) : new AudioStartResult.Cancelled();
    }

    /// <summary>控制线程上调用：撤销本次 Start 的所有状态并释放资源。缓存的端点可能正是失败原因，一并作废。</summary>
    private void FailStart(CaptureContext? created, MMDevice device)
    {
        lock (_lock)
        {
            if (created is not null && ReferenceEquals(_current, created))
            {
                _running = false;
                _current = null;
            }
        }
        _resolved = null;
        if (created is not null) created.Release();
        else device.Dispose(); // WasapiCapture 构造失败：上下文尚未接管设备
    }

    // ─── 输入端点解析与缓存（控制线程）────────────────────────────

    private void PrepareOnControlThread(AudioInputPolicy policy)
    {
        try
        {
            var (device, _, _) = OpenInputDevice(policy);
            device.Dispose();
        }
        catch (Exception ex)
        {
            // 预解析只是优化：失败时按下热键会再走一遍完整解析，并在那里给出用户可见的错误。
            AppLog.Warn("audio", $"预解析输入设备失败，将在开始录音时重试: {ex.Message}");
        }
    }

    private MMDeviceEnumerator EnsureEnumerator()
    {
        if (_enumerator is not null) return _enumerator;
        var enumerator = new MMDeviceEnumerator();
        var client = new DeviceNotificationClient(OnDeviceTopologyChanged);
        try
        {
            var hr = enumerator.RegisterEndpointNotificationCallback(client);
            if (hr == 0)
            {
                _notificationClient = client;
                _cacheEnabled = true;
            }
            else
            {
                AppLog.Warn("audio", $"注册音频端点变化通知失败 (0x{hr:X8})，输入设备缓存停用");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("audio", $"注册音频端点变化通知失败，输入设备缓存停用: {ex.Message}");
        }
        _enumerator = enumerator;
        return enumerator;
    }

    /// <summary>端点通知回调（任意 COM 线程）：作废缓存，并合并成一次后台重新预解析。</summary>
    private void OnDeviceTopologyChanged()
    {
        Interlocked.Increment(ref _topologyVersion);
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            if (!EnqueueControl(RefreshPreparedInput)) Volatile.Write(ref _refreshQueued, 0);
        }
    }

    private void RefreshPreparedInput()
    {
        Volatile.Write(ref _refreshQueued, 0);
        if (_preparedPolicy is { } policy) PrepareOnControlThread(policy);
    }

    /// <summary>
    /// 按策略打开输入端点：缓存有效时直接按 ID 取设备，否则完整解析并写入缓存。
    /// 不写 <see cref="ActiveDevice"/>——预解析可能发生在录音进行中，那时 HUD 正在显示当前设备名。
    /// </summary>
    private (MMDevice Device, ActiveInputDevice Info, bool Cached) OpenInputDevice(AudioInputPolicy policy)
    {
        var enumerator = EnsureEnumerator();
        _preparedPolicy = policy;
        var version = Volatile.Read(ref _topologyVersion);

        if (_cacheEnabled && _resolved is { } hit && hit.Policy == policy && _resolvedTopologyVersion == version)
        {
            try
            {
                var device = enumerator.GetDevice(hit.DeviceId);
                if (device.State == DeviceState.Active) return (device, hit.Device, true);
                device.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warn("audio", $"缓存的输入设备不可用，重新解析: {ex.Message}");
            }
            _resolved = null;
        }

        var (chosen, info) = ResolveInputDevice(enumerator, policy);
        // 拓扑版本在解析之前读取：解析期间若又有变化，下次按键时版本对不上，会重新解析。
        _resolved = _cacheEnabled ? new ResolvedInput(policy, chosen.ID, info) : null;
        _resolvedTopologyVersion = version;
        return (chosen, info, false);
    }

    /// <summary>
    /// 按策略完整解析输入端点。用 <see cref="Role.Console"/> 而不是 <see cref="Role.Communications"/>：
    /// 后者在蓝牙耳机连接时几乎总是指向「免提通话」端点，一打开就把耳机切到电话级音质
    /// （VW-16）。设置页手选的设备 ID 不存在（设备被拔了）时回落系统默认。
    /// </summary>
    private static (MMDevice Device, ActiveInputDevice Info) ResolveInputDevice(MMDeviceEnumerator enumerator, AudioInputPolicy policy)
    {
        MMDevice systemDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        try
        {
            IReadOnlyList<AudioDeviceInfo> devices = Array.Empty<AudioDeviceInfo>();
            var outputTransport = AudioTransport.Unknown;
            if (policy is not AudioInputPolicy.SystemDefault)
            {
                devices = AudioDeviceCatalog.ListCaptureDevices(enumerator);
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
            if (!ReferenceEquals(chosen, systemDefault)) systemDefault.Dispose();
            return (chosen, new ActiveInputDevice(info.Name, info.Transport, switchedByAuto));
        }
        catch
        {
            systemDefault.Dispose();
            throw;
        }
    }

    // ─── 停止 ─────────────────────────────────────────────────────

    /// <summary>
    /// 停止录音，发出尾音帧。即使没有尾音也会以空 Data 触发 <see cref="OnTailChunk"/>，
    /// 让调用方知道录音流已经结束。启动尚未完成时只撤销启动，不发尾音。
    /// </summary>
    public void Stop()
    {
        CaptureContext? ctx;
        int tailLength;
        lock (_lock)
        {
            _pendingStart = 0;
            if (!_running) return;
            ctx = _current;
            _running = false;
            tailLength = DrainAndEnqueueTailLocked();
        }

        RequestStopRecording(ctx);
        LogDroppedBuffersIfAny();
        AppLog.Info("audio", $"录音停止，尾音 {tailLength} bytes");
    }

    /// <summary>不发出尾音直接终止（如错误清理、用户主动取消）；启动尚未完成时撤销启动。</summary>
    public void StopWithoutResult()
    {
        CaptureContext? ctx;
        lock (_lock)
        {
            _pendingStart = 0;
            if (!_running) return;
            ctx = _current;
            _running = false;
            _chunker.Drain();
        }
        RequestStopRecording(ctx);
        LogDroppedBuffersIfAny();
    }

    /// <summary>StopRecording 排到控制线程：保证它一定在同一采集器的 StartRecording 之后执行。
    /// NAudio 的 StopRecording 只是置停止标记，真正停下后由 RecordingStopped 回调释放资源。</summary>
    private void RequestStopRecording(CaptureContext? ctx)
    {
        if (ctx is null) return;
        if (!EnqueueControl(() => StopRecordingOnControlThread(ctx))) StopRecordingOnControlThread(ctx);
    }

    private static void StopRecordingOnControlThread(CaptureContext ctx)
    {
        if (ctx.IsReleased) return;
        try { ctx.Capture.StopRecording(); }
        catch (Exception ex) { AppLog.Warn("audio", $"StopRecording 异常: {ex.Message}"); }
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
    /// 声道处理（R1-3）：单声道直通；立体声用 NAudio 内置下混；3 声道及以上用
    /// <see cref="MultichannelToMonoSampleProvider"/> 平均下混——Windows 11 麦克风阵列在共享模式下的
    /// 混音格式可能是 4 声道，此前直接抛异常，在设置页表现为笼统的「设备打开失败」。
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
                sampleProvider = new MultichannelToMonoSampleProvider(sampleProvider);
                break;
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

    /// <summary>
    /// 在 NAudio 的采集线程上触发（采集器在没有 SynchronizationContext 的控制线程上构造）。
    /// </summary>
    private void OnCaptureStopped(CaptureContext ctx, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            AppLog.Warn("audio", $"音频设备意外停止（可能是设备被拔出/切换）: {e.Exception.Message}");
            // 只有仍是当前录音的采集器才需要收尾；旧采集器的异常停止不得影响新的一次录音。
            // 收尾要在 UI 线程上做，而它依赖 _current 仍指向本上下文——所以释放必须排在收尾之后，
            // 不能在这里先把 _current 清掉（此前回调本就在 UI 线程上，同步执行掩盖了这个先后关系）。
            if (IsCurrent(ctx))
            {
                UiDispatcher.Post(() =>
                {
                    HandleDeviceChangedDuringRecording(ctx);
                    ReleaseStopped(ctx);
                });
                return;
            }
        }
        ReleaseStopped(ctx);
    }

    /// <summary>
    /// NAudio 的 StopRecording 是异步的：真正的资源释放要等到 RecordingStopped 到达才做，
    /// 否则正常 Stop() 路径从不释放采集器与设备，每次听写泄漏一组 COM 对象与采集线程（R1-3）。
    /// 释放排回控制线程：与其他 WASAPI 调用串行，也避免在采集线程上释放它自己。
    /// 快速连按热键时新的 Start() 可能已经换了 _current——此时只释放旧上下文自己的资源。
    /// </summary>
    private void ReleaseStopped(CaptureContext ctx)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_current, ctx)) _current = null;
        }
        if (!EnqueueControl(ctx.Release)) ctx.Release();
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

    private void RunControlLoop()
    {
        foreach (var action in _controlQueue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Error("audio", "音频控制任务异常", ex);
            }
        }
    }

    /// <returns>队列已关闭（Dispose 之后）时返回 false，由调用方决定就地执行还是放弃。</returns>
    private bool EnqueueControl(Action action)
    {
        try
        {
            return _controlQueue.TryAdd(action);
        }
        // ObjectDisposedException 派生自 InvalidOperationException，一并覆盖（已关闭或已释放）。
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>控制线程上的最终清理：释放当前上下文、注销端点通知、释放枚举器——它们都在控制线程上创建。
    /// 已停止、仍在等 RecordingStopped 的旧上下文由各自的回调释放。</summary>
    private void ShutdownOnControlThread()
    {
        CaptureContext? ctx;
        lock (_lock)
        {
            ctx = _current;
            _current = null;
        }
        ctx?.Release();

        if (_enumerator is not null && _notificationClient is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClient); }
            catch (Exception ex) { AppLog.Warn("audio", $"注销音频端点变化通知异常: {ex.Message}"); }
        }
        _notificationClient = null;
        _enumerator?.Dispose();
        _enumerator = null;
        _resolved = null;
    }

    /// <summary>投递队列关闭后（Dispose 之后的迟到调用）丢弃任务，而不是抛出到音频线程。</summary>
    private void EnqueueDelivery(Action action)
    {
        try
        {
            if (!_deliveryQueue.TryAdd(action)) AppLog.Debug("audio", "投递队列已关闭，丢弃任务");
        }
        // ObjectDisposedException 派生自 InvalidOperationException，一并覆盖（已关闭或已释放）。
        catch (InvalidOperationException) { AppLog.Debug("audio", "投递队列已关闭或已释放，丢弃任务"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWithoutResult();
        if (!EnqueueControl(ShutdownOnControlThread)) ShutdownOnControlThread();
        _controlQueue.CompleteAdding();
        // 控制线程卡在某次 WASAPI 调用里时不释放队列，交给进程退出回收（与投递线程同理）。
        if (_controlThread.Join(TimeSpan.FromSeconds(3))) _controlQueue.Dispose();
        else AppLog.Warn("audio", "音频控制线程 3 秒内未退出，保留队列由进程退出回收");

        _deliveryQueue.CompleteAdding();
        // 投递线程仍在跑（回调卡住）时不释放队列：它随后会访问已释放的集合。交给进程退出回收。
        if (_deliveryThread.Join(TimeSpan.FromSeconds(2))) _deliveryQueue.Dispose();
        else AppLog.Warn("audio", "音频投递线程 2 秒内未退出，保留队列由进程退出回收");
    }
}
