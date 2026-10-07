using System.Runtime.InteropServices;

namespace DynamicIsland;

sealed class ProcessAudioSet : IDisposable
{
    readonly Dictionary<uint, ProcessLoopback> _readers = [];
    readonly float[] _sum = new float[SpectrumAnalyzer.Bands];
    long _lastRefresh;
    public string Status { get; private set; } = "";

    public float[] Read(AudioCaptureMode mode, string[] names)
    {
        if (!ProcessLoopback.Supported) { Status = "Выбор приложений требует Windows 10 21H2"; return _sum; }
        if (Environment.TickCount64 - _lastRefresh >= 1000 || _lastRefresh == 0)
        {
            _lastRefresh = Environment.TickCount64;
            Refresh(mode, names);
        }
        Array.Clear(_sum);
        foreach (var (pid, reader) in _readers.ToArray())
        {
            try
            {
                reader.Drain();
                for (int b = 0; b < _sum.Length; b++) _sum[b] += reader.Bands[b];
            }
            catch (Exception ex)
            {
                App.Log(ex);
                reader.Dispose();
                _readers.Remove(pid);
                Status = "Не удалось захватить звук одного из приложений. Повторяем подключение…";
            }
        }
        return _sum;
    }

    void Refresh(AudioCaptureMode mode, string[] names)
    {
        var processes = Snapshot();
        var selected = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var audio = AudioProcesses();
        var targets = SelectRoots(processes, audio, selected, mode, out bool restricted);
        Status = restricted ? "Исключён дочерний процесс: звук его родительского дерева тоже пропущен." : "";
        foreach (uint pid in _readers.Keys.Except(targets).ToArray())
        {
            _readers[pid].Dispose();
            _readers.Remove(pid);
        }
        foreach (uint pid in targets.Except(_readers.Keys))
        {
            try { _readers.Add(pid, new ProcessLoopback(pid)); }
            catch (Exception ex)
            {
                App.Log(ex);
                Status = "Захват приложения недоступен. Повторяем подключение…";
            }
        }
    }

    internal readonly record struct Entry(uint Parent, string Name);

    internal static HashSet<uint> SelectRoots(Dictionary<uint, Entry> processes, HashSet<uint> audio,
        HashSet<string> selected, AudioCaptureMode mode, out bool restricted)
    {
        bool Matches(uint pid) => Lineage(pid, processes).Any(p => processes.TryGetValue(p, out var e) && selected.Contains(e.Name));
        var allowed = audio.Where(pid => pid != 0 && processes.ContainsKey(pid) &&
            (mode == AudioCaptureMode.Include ? Matches(pid) : !Matches(pid))).ToHashSet();
        var blocked = (mode == AudioCaptureMode.Exclude
            ? processes.Keys.Where(Matches) : audio.Except(allowed)).ToHashSet();
        var unsafeParents = allowed.Where(parent => blocked.Any(child => Lineage(child, processes).Contains(parent))).ToArray();
        restricted = unsafeParents.Length > 0;
        allowed.ExceptWith(unsafeParents);
        return allowed.Where(pid => !Lineage(pid, processes).Skip(1).Any(allowed.Contains)).ToHashSet();
    }

    static IEnumerable<uint> Lineage(uint pid, Dictionary<uint, Entry> processes)
    {
        var seen = new HashSet<uint>();
        while (pid != 0 && seen.Add(pid))
        {
            yield return pid;
            if (!processes.TryGetValue(pid, out var entry)) yield break;
            pid = entry.Parent;
        }
    }

    internal static Dictionary<uint, Entry> Snapshot()
    {
        var result = new Dictionary<uint, Entry>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32First(snapshot, ref entry))
                do { result[entry.Id] = new Entry(entry.Parent, entry.Name); } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    static HashSet<uint> AudioProcesses()
    {
        var result = new HashSet<uint>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDeviceCollection? devices = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 1, out IntPtr pointer));
            try { devices = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }
            Marshal.ThrowExceptionForHR(devices.GetCount(out uint count));
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                IAudioSessionManager2? manager = null;
                IAudioSessionEnumerator? sessions = null;
                try
                {
                    if (devices.Item(i, out device) != 0 || device == null) continue;
                    manager = device.Activate<IAudioSessionManager2>();
                    if (manager == null || manager.GetSessionEnumerator(out sessions) != 0 || sessions == null) continue;
                    if (sessions.GetCount(out int n) != 0) continue;
                    for (int j = 0; j < n; j++)
                    {
                        object? session = null;
                        try
                        {
                            if (sessions.GetSession(j, out session) == 0 && session is IAudioSessionControl2 control &&
                                control.GetState(out int state) == 0 && state != 2 && control.GetProcessId(out uint pid) == 0)
                                result.Add(pid);
                        }
                        finally { if (session != null) Marshal.ReleaseComObject(session); }
                    }
                }
                finally
                {
                    if (sessions != null) Marshal.ReleaseComObject(sessions);
                    if (manager != null) Marshal.ReleaseComObject(manager);
                    if (device != null) Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            if (devices != null) Marshal.ReleaseComObject(devices);
            Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var reader in _readers.Values) reader.Dispose();
        _readers.Clear();
        Array.Clear(_sum);
        _lastRefresh = 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-C0A3B2A54D7A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}
