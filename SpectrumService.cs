using System.Runtime.InteropServices;

namespace DynamicIsland;

sealed class SpectrumService : IDisposable
{
    public const int Bands = SpectrumAnalyzer.Bands;

    const int StreamLoopback = 0x00020000, BufferSilent = 2;
    const int FormatPcm = 1, FormatFloat = 3, FormatExtensible = 0xFFFE;
    const int TagOffset = 0, ChannelsOffset = 2, RateOffset = 4, BitsOffset = 14, SubFormatOffset = 24;
    const float PcmFullScale = 32768f;
    const long BufferDuration = 2_000_000;
    const int PollMs = 8, SilenceMs = 80, LingerMs = 3000, RetryMs = 2000, DeviceCheckMs = 2000, DrainRetryMs = 200;

    readonly object _lock = new();
    readonly float[] _bands = new float[Bands];
    readonly float[] _scratch = new float[Bands];
    readonly AutoResetEvent _wake = new(false);
    Thread? _thread;
    volatile bool _active, _failed;

    sealed record CaptureConfig(AudioCaptureMode Mode, string[] Names);
    CaptureConfig _config = new(Settings.AudioCapture, Settings.AudioCaptureProcesses);
    CaptureConfig? _applied;
    ProcessAudioSet? _processAudio;
    volatile bool _disposed;
    volatile string _status = "";
    public string Status => _status;

    public void Configure(AudioCaptureMode mode, string[] names)
    {
        if (_disposed) return;
        Volatile.Write(ref _config, new CaptureConfig(mode, (string[])names.Clone()));
        lock (_lock) Array.Clear(_bands);
        _wake.Set();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
    }

    IAudioClient? _client;
    IAudioCaptureClient? _capture;
    SpectrumAnalyzer? _analyzer;
    string? _deviceId;
    int _channels;
    bool _float;
    float[] _samples = [];
    short[] _pcm = [];
    float[] _mono = [];
    long _lastDataAt;
    bool _hasNewData;

    public bool Active
    {
        get => _active;
        set
        {
            if (_disposed || _active == value) return;
            _active = value;
            if (!value) return;
            if (_thread == null)
            {
                _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Spectrum", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
            _wake.Set();
        }
    }

    public bool Read(float[] bands)
    {
        if (_failed && Volatile.Read(ref _config).Mode == AudioCaptureMode.All) return false;
        lock (_lock) Array.Copy(_bands, bands, Bands);
        return true;
    }

    void CaptureLoop()
    {
        long lastActive = Environment.TickCount64, lastCheck = 0;
        while (!_disposed)
        {
            long now = Environment.TickCount64;
            if (_active) lastActive = now;
            else if (now - lastActive > LingerMs)
            {
                Close();
                _wake.WaitOne();
                continue;
            }

            try
            {
                CaptureConfig config = Volatile.Read(ref _config);
                if (!ReferenceEquals(config, _applied))
                {
                    Close();
                    _applied = config;
                    _status = "";
                    _failed = false;
                }
                if (config.Mode != AudioCaptureMode.All && !(config.Mode == AudioCaptureMode.Exclude && config.Names.Length == 0))
                {
                    _processAudio ??= new ProcessAudioSet();
                    float[] bands = _processAudio.Read(config.Mode, config.Names);
                    _status = _processAudio.Status;
                    lock (_lock)
                    {
                        // Never publish data from a configuration replaced while opening COM clients.
                        if (ReferenceEquals(config, Volatile.Read(ref _config))) Array.Copy(bands, _bands, Bands);
                    }
                    _wake.WaitOne(PollMs);
                    continue;
                }
                if (_capture == null)
                {
                    _failed = !Open();
                    if (_failed)
                    {
                        Close();
                        _wake.WaitOne(RetryMs);
                        continue;
                    }
                    lastCheck = now;
                }
                else if (now - lastCheck > DeviceCheckMs)
                {
                    lastCheck = now;
                    if (AudioEndpoint.DefaultId() != _deviceId)
                    {
                        Close();
                        continue;
                    }
                }

                if (!Drain())
                {
                    Close();
                    _wake.WaitOne(DrainRetryMs);
                    continue;
                }
                Analyze();
            }
            catch (Exception ex)
            {
                App.Log(ex);
                _failed = true;
                _status = "Захват звука недоступен. Повторяем подключение…";
                Close();
                _wake.WaitOne(RetryMs);
                continue;
            }
            _wake.WaitOne(PollMs);
        }
        Close();
        _wake.Dispose();
    }

    bool Open()
    {
        IMMDevice? device = AudioEndpoint.Default();
        if (device == null) return false;
        try
        {
            if (device.GetId(out _deviceId) != 0) return false;

            _client = device.Activate<IAudioClient>();
            if (_client == null || _client.GetMixFormat(out IntPtr format) != 0) return false;

            int rate;
            try
            {
                int tag = (ushort)Marshal.ReadInt16(format, TagOffset);
                _channels = (ushort)Marshal.ReadInt16(format, ChannelsOffset);
                rate = Marshal.ReadInt32(format, RateOffset);
                int bits = (ushort)Marshal.ReadInt16(format, BitsOffset);
                if (tag == FormatExtensible) tag = (ushort)Marshal.ReadInt16(format, SubFormatOffset);
                _float = tag == FormatFloat && bits == 32;
                if (_channels == 0 || rate <= 0 || !(_float || (tag == FormatPcm && bits == 16))) return false;
                if (_client.Initialize(0, StreamLoopback, BufferDuration, 0, format, IntPtr.Zero) != 0) return false;
            }
            finally
            {
                Marshal.FreeCoTaskMem(format);
            }

            Guid iid = typeof(IAudioCaptureClient).GUID;
            if (_client.GetService(ref iid, out object? capture) != 0) return false;
            _capture = capture as IAudioCaptureClient;
            if (_capture == null || _client.Start() != 0) return false;

            _analyzer = new SpectrumAnalyzer(rate);
            _lastDataAt = 0;
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    void Close()
    {
        _processAudio?.Dispose();
        _processAudio = null;
        try { _client?.Stop(); }
        catch { }
        if (_capture != null) Marshal.ReleaseComObject(_capture);
        if (_client != null) Marshal.ReleaseComObject(_client);
        _capture = null;
        _client = null;
        _analyzer = null;
        lock (_lock) Array.Clear(_bands);
    }

    bool Drain()
    {
        while (true)
        {
            if (_capture!.GetNextPacketSize(out uint frames) < 0) return false;
            if (frames == 0) return true;
            if (_capture.GetBuffer(out IntPtr data, out frames, out int flags, out _, out _) < 0) return false;
            if (frames == 0) return true;
            Push(data, (int)frames, (flags & BufferSilent) != 0);
            _capture.ReleaseBuffer(frames);
        }
    }

    void Push(IntPtr data, int frames, bool silent)
    {
        if (_mono.Length < frames) _mono = new float[frames];
        if (silent) Array.Clear(_mono, 0, frames);
        else Downmix(data, frames);

        _analyzer!.Push(_mono, frames);
        _lastDataAt = Environment.TickCount64;
        _hasNewData = true;
    }

    void Downmix(IntPtr data, int frames)
    {
        int count = frames * _channels;
        if (_samples.Length < count) _samples = new float[count];

        float fullScale = 1;
        if (_float)
        {
            Marshal.Copy(data, _samples, 0, count);
        }
        else
        {
            if (_pcm.Length < count) _pcm = new short[count];
            Marshal.Copy(data, _pcm, 0, count);
            for (int i = 0; i < count; i++) _samples[i] = _pcm[i];
            fullScale = PcmFullScale;
        }

        float divisor = _channels * fullScale;
        for (int frame = 0, sample = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (int channel = 0; channel < _channels; channel++) sum += _samples[sample++];
            _mono[frame] = sum / divisor;
        }
    }

    void Analyze()
    {
        if (Environment.TickCount64 - _lastDataAt > SilenceMs)
        {
            lock (_lock) Array.Clear(_bands);
            return;
        }
        if (!_hasNewData) return;
        _hasNewData = false;

        _analyzer!.Analyze(_scratch);
        lock (_lock)
        {
            if (ReferenceEquals(_applied, Volatile.Read(ref _config))) Array.Copy(_scratch, _bands, Bands);
        }
    }
}
