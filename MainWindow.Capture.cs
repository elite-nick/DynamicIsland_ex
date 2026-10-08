using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland;

public partial class MainWindow
{
    string _captureHintText = "";

    void CaptureRow_Click(object sender, RoutedEventArgs e)
    {
        RefreshCapturePage();
        ShowPanel(Panel.Capture);
    }

    void CaptureBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Settings);
    void CaptureRefresh_Click(object sender, RoutedEventArgs e) => RefreshCapturePage();

    void CaptureMode_Click(object sender, RoutedEventArgs e)
    {
        int mode = CaptureModeSegments.PickUnderPointer();
        if (mode != (int)AudioCaptureMode.All && !ProcessLoopback.Supported) return;
        Settings.AudioCapture = (AudioCaptureMode)mode;
        _spectrum.Configure(Settings.AudioCapture, Settings.AudioCaptureProcesses);
        RefreshCapturePage();
    }

    void RefreshCapturePage()
    {
        var mode = Settings.AudioCapture;
        CaptureModeSegments.Set((int)mode, CaptureView.IsVisible);
        CaptureHint.Text = !ProcessLoopback.Supported
            ? "Выбор приложений требует Windows 10 21H2 или новее."
            : mode == AudioCaptureMode.All ? "Звук всех приложений на устройстве вывода по умолчанию."
            : "Для эквалайзера и пульсации. Выбор сохраняется для всех запусков приложения, включая дочерние процессы.";
        _captureHintText = CaptureHint.Text;
        CaptureProcessesPanel.Visibility = mode == AudioCaptureMode.All ? Visibility.Collapsed : Visibility.Visible;
        CaptureProcesses.Children.Clear();
        if (mode == AudioCaptureMode.All) return;

        var selected = new HashSet<string>(Settings.AudioCaptureProcesses, StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try { if (process.Id > 0) active.Add(process.ProcessName + ".exe"); }
                catch { /* A process can exit while this list is being built. */ }
            }
        }
        foreach (string name in active.Union(selected, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var check = new CheckBox
            {
                Content = name + (active.Contains(name) ? "" : " (не запущен)"),
                Tag = name, IsChecked = selected.Contains(name), Foreground = Brushes.White,
                Margin = new Thickness(4, 5, 4, 5), ToolTip = name,
            };
            check.Click += CaptureProcess_Click;
            CaptureProcesses.Children.Add(check);
        }
    }

    void UpdateCaptureStatus()
    {
        if (_view == View.Capture) CaptureHint.Text = _spectrum.Status.Length > 0 ? _spectrum.Status : _captureHintText;
    }

    void CaptureProcess_Click(object sender, RoutedEventArgs e)
    {
        Settings.AudioCaptureProcesses = CaptureProcesses.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray();
        _spectrum.Configure(Settings.AudioCapture, Settings.AudioCaptureProcesses);
    }
}
