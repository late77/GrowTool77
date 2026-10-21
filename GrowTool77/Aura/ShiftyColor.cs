namespace GrowTool77.Aura;

// The colour of Growtopia's Shifty Block (item 2590). In items.dat it uses
// visual effect 25, the game's rainbow effect shared only by rainbow items
// (Rainbow Hair, Rainbow Cape, Kaleidoscopic Wallpaper…): a tint whose hue
// keeps turning. Runs on its own by default; whenever a Shifty Block is
// visible on screen, its real colour is measured and the hue — and the speed
// it turns at — lock onto the game's.
public sealed class ShiftyColor
{
    public const int ItemId = 2590;

    private double _rate = 120;                 // degrees per second until measured (one turn in 3 s)
    private double _phase;                      // hue at t = 0
    private readonly List<(double Hue, double T)> _samples = new();   // recent hues, unwrapped
    private readonly object _gate = new();

    public bool Synced { get; private set; }
    public double DegreesPerSecond { get { lock (_gate) return _rate; } }

    public (byte R, byte G, byte B) At(double seconds)
    {
        double hue;
        lock (_gate) hue = Mod(_phase + _rate * seconds, 360);
        return FromHue(hue);
    }

    // A colour measured on screen at time t (seconds). Greyish or dark samples are ignored.
    public bool Observe(byte r, byte g, byte b, double t)
    {
        var (h, s, v) = ToHsv(r, g, b);
        if (s < 0.35 || v < 0.25) return false;
        lock (_gate)
        {
            if (_samples.Count > 0 && (t <= _samples[^1].T || t - _samples[^1].T > 1.5)) _samples.Clear();
            double unwrapped = h;
            if (_samples.Count > 0)
            {
                double last = _samples[^1].Hue;
                unwrapped = last + (Mod(h - Mod(last, 360) + 180, 360) - 180);   // shortest signed step
            }
            _samples.Add((unwrapped, t));
            _samples.RemoveAll(x => t - x.T > 1.5);

            // Speed = slope of hue over the last 1.5 s (least squares), robust to 8-bit rounding.
            if (_samples.Count >= 3)
            {
                double mt = _samples.Average(x => x.T), mh = _samples.Average(x => x.Hue);
                double num = _samples.Sum(x => (x.T - mt) * (x.Hue - mh)), den = _samples.Sum(x => (x.T - mt) * (x.T - mt));
                if (den > 1e-6) _rate = Math.Clamp(num / den, -1500, 1500);
            }
            _phase = Mod(h - _rate * t, 360);
            Synced = true;
        }
        return true;
    }

    public static (byte R, byte G, byte B) FromHue(double hue)
    {
        hue = Mod(hue, 360) / 60.0;
        int sector = (int)hue;
        double f = hue - sector;
        byte up = (byte)Math.Round(255 * f), down = (byte)Math.Round(255 * (1 - f));
        return sector switch
        {
            0 => (255, up, 0),
            1 => (down, 255, 0),
            2 => (0, 255, up),
            3 => (0, down, 255),
            4 => (up, 0, 255),
            _ => (255, 0, down),
        };
    }

    public static (double H, double S, double V) ToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf)), d = max - min;
        double h = d == 0 ? 0 : max == rf ? 60 * (((gf - bf) / d) % 6) : max == gf ? 60 * ((bf - rf) / d + 2) : 60 * ((rf - gf) / d + 4);
        return (Mod(h, 360), max == 0 ? 0 : d / max, max);
    }

    private static double Mod(double x, double m) => ((x % m) + m) % m;
}
