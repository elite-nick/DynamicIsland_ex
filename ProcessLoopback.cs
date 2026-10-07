using System.Runtime.InteropServices;

namespace DynamicIsland;

public sealed class ProcessLoopback : IDisposable
{
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    readonly AutoResetEvent _ready = new(false);
    readonly SpectrumAnalyzer _analyzer = new(44100);
    IAudioClient? _client;
    IAudioCaptureClient? _capture;
    short[] _pcm = [];
    float[] _mono = [];
    long _lastPacket;
    public readonly float[] Bands = new float[SpectrumAnalyzer.Bands];

    public ProcessLoopback(uint processId, bool exclude = false)
    {
        if (!Supported) throw new PlatformNotSupportedException("Process audio capture needs Windows build 20348.");
        try
        {
            _client = Activate(processId, exclude);
            var format = new WaveFormat { Tag = 1, Channels = 2, Rate = 44100, BytesPerSecond = 176400, BlockAlign = 4, Bits = 16 };
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormat>());
            try
            {
                Marshal.StructureToPtr(format, pointer, false);
                Marshal.ThrowExceptionForHR(_client.Initialize(0, 0x00020000 | 0x00040000 | unchecked((int)0x80000000), 0, 0, pointer, IntPtr.Zero));
            }
            finally { Marshal.FreeHGlobal(pointer); }
            Marshal.ThrowExceptionForHR(_client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()));
            Guid iid = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(_client.GetService(ref iid, out object? capture));
            _capture = (IAudioCaptureClient)capture!;
            Marshal.ThrowExceptionForHR(_client.Start());
        }
        catch { Dispose(); throw; }
    }

    public void Drain()
    {
        bool changed = false;
        for (int packets = 0; packets < 128; packets++)
        {
            Marshal.ThrowExceptionForHR(_capture!.GetNextPacketSize(out uint count));
            if (count == 0) break;
            Marshal.ThrowExceptionForHR(_capture.GetBuffer(out IntPtr data, out count, out int flags, out _, out _));
            try
            {
                int frames = checked((int)count);
                if (_mono.Length < frames) _mono = new float[frames];
                if ((flags & 2) != 0) Array.Clear(_mono, 0, frames);
                else
                {
                    if (_pcm.Length < frames * 2) _pcm = new short[frames * 2];
                    Marshal.Copy(data, _pcm, 0, frames * 2);
                    for (int i = 0; i < frames; i++) _mono[i] = (_pcm[i * 2] + _pcm[i * 2 + 1]) / 65536f;
                }
                _analyzer.Push(_mono, frames);
                _lastPacket = Environment.TickCount64;
                changed = true;
            }
            finally { Marshal.ThrowExceptionForHR(_capture.ReleaseBuffer(count)); }
        }
        if (Environment.TickCount64 - _lastPacket > 80) Array.Clear(Bands);
        else if (changed) _analyzer.Analyze(Bands);
    }

    public void Dispose()
    {
        try { _client?.Stop(); } catch { }
        if (_capture != null) Marshal.ReleaseComObject(_capture);
        if (_client != null) Marshal.ReleaseComObject(_client);
        _capture = null;
        _client = null;
        _ready.Dispose();
    }

    static IAudioClient Activate(uint pid, bool exclude)
    {
        var completion = new Completion();
        IntPtr blob = Marshal.AllocHGlobal(12);
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            Marshal.WriteInt32(blob, 0, 1); // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(blob, 4, unchecked((int)pid));
            Marshal.WriteInt32(blob, 8, exclude ? 1 : 0);
            var parameters = new ActivationVariant { Type = 65, Size = 12, Data = blob }; // VT_BLOB
            Guid iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, ref parameters, completion, out operation));
            return completion.Take(TimeSpan.FromSeconds(3));
        }
        finally
        {
            completion.Abandon();
            if (operation != null) Marshal.ReleaseComObject(operation);
            Marshal.FreeHGlobal(blob);
            GC.KeepAlive(completion);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint Rate, BytesPerSecond;
        public ushort BlockAlign, Bits, Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ActivationVariant
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public uint Size;
        public IntPtr Data;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid, ref ActivationVariant parameters,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        readonly object _sync = new();
        object? _result;
        Exception? _error;
        bool _done, _abandoned;
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            object? result = null;
            Exception? error = null;
            try
            {
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(out int hr, out result));
                Marshal.ThrowExceptionForHR(hr);
            }
            catch (Exception ex) { error = ex; }
            lock (_sync)
            {
                if (_abandoned)
                {
                    if (result != null && Marshal.IsComObject(result)) Marshal.ReleaseComObject(result);
                    return 0;
                }
                _result = result;
                _error = error;
                _done = true;
                Monitor.PulseAll(_sync);
            }
            return 0;
        }
        internal IAudioClient Take(TimeSpan timeout)
        {
            lock (_sync)
            {
                if (!_done) Monitor.Wait(_sync, timeout);
                if (!_done) throw new TimeoutException("Process audio activation timed out.");
                if (_error != null) throw _error;
                var result = (IAudioClient)_result!;
                _result = null;
                return result;
            }
        }
        internal void Abandon()
        {
            lock (_sync)
            {
                _abandoned = true;
                if (_result != null && Marshal.IsComObject(_result)) Marshal.ReleaseComObject(_result);
                _result = null;
            }
        }
    }
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object? activated);
}

[ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAgileObject { }
