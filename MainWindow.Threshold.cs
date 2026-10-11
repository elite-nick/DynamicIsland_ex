using System.Windows;

namespace DynamicIsland;

public partial class MainWindow
{
    readonly float[] _thresholdBands = new float[SpectrumService.Bands];
    double _thresholdPreviewDb = Settings.MinEqualizerThreshold;
    bool _refreshingThreshold;

    void RefreshThreshold()
    {
        _refreshingThreshold = true;
        ThresholdSlider.Value = Settings.EqualizerThreshold;
        ThresholdValue.Text = $"{Settings.EqualizerThreshold} дБ";
        _refreshingThreshold = false;
    }

    void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _refreshingThreshold || ThresholdValue == null) return;
        Settings.EqualizerThreshold = (int)Math.Round(e.NewValue);
        ThresholdValue.Text = $"{Settings.EqualizerThreshold} дБ";
    }

    void ThresholdReset_Click(object sender, RoutedEventArgs e)
    {
        Settings.EqualizerThreshold = Settings.DefaultEqualizerThreshold;
        RefreshThreshold();
    }

    void AdvanceThresholdPreview(double dt)
    {
        bool available = _spectrum.Read(_thresholdBands);
        double power = 0;
        if (available)
            foreach (float band in _thresholdBands) power = Math.Max(power, band);
        double db = power > 0 ? 10 * Math.Log10(power) : Settings.MinEqualizerThreshold;
        db = Math.Clamp(db, Settings.MinEqualizerThreshold, Settings.MaxEqualizerThreshold);
        _thresholdPreviewDb += (db - _thresholdPreviewDb) * (1 - Math.Exp(-dt / 0.10));
        ThresholdLevel.Value = _thresholdPreviewDb;
        ThresholdAudioText.Text = !available ? "Аудио недоступно"
            : power <= 0 ? "Уровень спектра: тишина"
            : $"Уровень спектра: {10 * Math.Log10(power):F0} дБ";
    }
}
