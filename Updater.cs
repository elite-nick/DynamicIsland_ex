using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DynamicIsland;

/// <summary>
/// Looks the latest release up on GitHub and, asked to, puts its exe in the place of the one that runs and starts it.
/// </summary>
sealed class Updater
{
    public enum Stage { Idle, Checking, Latest, Available, Loading, Failed }

    const string Latest = "https://api.github.com/repos/mihailkotovski/DynamicIsland/releases/latest";
    const string Asset = "DynamicIsland.exe";
    /// <summary>Tells the island it was started by the one it replaces, which may not have gone yet.</summary>
    public const string Restarted = "--updated";

    // the page is opened many times over: the release is not asked for again sooner than this
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(30);
    static readonly HttpClient Http = CreateClient();

    string _url = "", _digest = "";
    DateTime _checked;

    /// <summary>Raised on the calling (UI) thread whenever the stage or the progress has changed.</summary>
    public event Action? Changed;

    public Stage State { get; private set; }

    /// <summary>The release on GitHub, once it is known.</summary>
    public Version? Found { get; private set; }

    /// <summary>How much of the new exe is here, 0..100.</summary>
    public int Percent { get; private set; }

    public static Version Current { get; } = Trim(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    static string Exe => Environment.ProcessPath ?? "";
    static string Old => Exe + ".old";
    static string Fetched => Exe + ".new";

    /// <summary>Call from the UI thread. Asks GitHub which release is the latest, unless it was asked a moment ago.</summary>
    public async Task CheckAsync(bool force = false)
    {
        if (State is Stage.Checking or Stage.Loading) return;
        if (!force && State != Stage.Failed && DateTime.UtcNow - _checked < Fresh) return;

        Set(Stage.Checking);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(Latest, timeout.Token));
            JsonElement release = doc.RootElement;
            Found = Trim(Version.Parse(release.GetProperty("tag_name").GetString()!.TrimStart('v')));
            _url = _digest = "";
            foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != Asset) continue;
                _url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (asset.TryGetProperty("digest", out JsonElement digest)) _digest = digest.GetString() ?? "";
            }
            _checked = DateTime.UtcNow;
            Set(Found > Current && _url.Length > 0 ? Stage.Available : Stage.Latest);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Set(Stage.Failed);
        }
    }

    /// <summary>
    /// Call from the UI thread. Downloads the release found, swaps it with the exe that runs and starts it;
    /// true once the new island has been started and this one is to close.
    /// </summary>
    public async Task<bool> InstallAsync()
    {
        if (State != Stage.Available) return false;

        Percent = 0;
        Set(Stage.Loading);
        try
        {
            await DownloadAsync();
            Swap();
            Process.Start(new ProcessStartInfo(Exe, Restarted) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            try { File.Delete(Fetched); }
            catch { }
            Set(Stage.Failed);
            return false;
        }
    }

    async Task DownloadAsync()
    {
        using HttpResponseMessage response = await Http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0, done = 0;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (Stream from = await response.Content.ReadAsStreamAsync())
        await using (var to = new FileStream(Fetched, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read));
                sha.AppendData(buffer, 0, read);
                done += read;
                int percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent == Percent) continue;
                Percent = percent;
                Changed?.Invoke();
            }
        }

        if (total > 0 && done != total) throw new IOException($"The update came short: {done} of {total} bytes.");
        // GitHub states the hash of each file of a release; older releases have none
        string hash = "sha256:" + Convert.ToHexString(sha.GetHashAndReset());
        if (_digest.Length > 0 && !hash.Equals(_digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update does not match the hash GitHub gives for it.");
    }

    // an exe that runs cannot be written over, but it can be renamed: it steps aside and the new one takes its name
    static void Swap()
    {
        File.Move(Exe, Old, true);
        try
        {
            File.Move(Fetched, Exe);
        }
        catch
        {
            File.Move(Old, Exe);
            throw;
        }
    }

    /// <summary>Removes the exe the last update left behind. It may still be closing, so this takes a few tries.</summary>
    public static void CleanUp() => Task.Run(async () =>
    {
        for (int i = 0; i < 10 && File.Exists(Old); i++)
        {
            try { File.Delete(Old); }
            catch { await Task.Delay(1000); }
        }
    });

    void Set(Stage stage)
    {
        State = stage;
        Changed?.Invoke();
    }

    // 1.9 and 1.9.0.0 are the same release
    static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    static HttpClient CreateClient()
    {
        // no timeout: the exe is tens of megabytes, and how long it takes is up to the connection
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/" + Current);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
