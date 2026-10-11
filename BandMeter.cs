namespace DynamicIsland;

sealed class BandMeter
{
    const double RangeDb = 10, RestingHeight = 0.5;
    const double BaselineRiseSeconds = 0.8, BaselineSinkSeconds = 2.5;
    const double SpreadDb = 12;
    double _floorDb = Settings.EqualizerThreshold;

    readonly double[] _db, _baseline;

    public BandMeter(int bars)
    {
        Heights = new double[bars];
        _db = new double[bars];
        _baseline = new double[bars];
        Array.Fill(_baseline, _floorDb);
    }

    public double[] Heights { get; }

    public void Measure(float[] spectrum, double dt)
    {
        if (_floorDb != Settings.EqualizerThreshold)
        {
            _floorDb = Settings.EqualizerThreshold;
            Array.Fill(_baseline, _floorDb);
        }
        int bars = Heights.Length;
        double headroom = RangeDb * (1 - RestingHeight), top = _floorDb;
        for (int i = 0; i < bars; i++)
        {
            int from = i * spectrum.Length / bars, to = Math.Max((i + 1) * spectrum.Length / bars, from + 1);
            double power = 0;
            for (int bin = from; bin < to; bin++) power += spectrum[bin];
            double db = _db[i] = 10 * Math.Log10(power / (to - from) + 1e-14);

            if (db > _floorDb)
            {
                double baseline = _baseline[i];
                baseline += (db - baseline) * (1 - Math.Exp(-dt / (db > baseline ? BaselineRiseSeconds : BaselineSinkSeconds)));
                _baseline[i] = Math.Max(baseline, db - headroom);
            }
            top = Math.Max(top, _baseline[i]);
        }

        for (int i = 0; i < bars; i++)
        {
            double reference = Math.Max(_baseline[i], top - SpreadDb);
            Heights[i] = Math.Clamp(RestingHeight + (_db[i] - reference) / RangeDb, 0, 1);
        }
    }
}
