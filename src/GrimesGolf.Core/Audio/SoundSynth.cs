namespace GrimesGolf.Core.Audio;

/// <summary>The game's sound effects.</summary>
public enum SoundEffect { Wood, Iron, Wedge, Putt, Landing, Tree, Cup, Applause, Cheer, Ready }

/// <summary>
/// Makes the sound effects from code, so there are no audio files to license or ship. Each is mono, 16-bit, built the
/// same way every time. Struck sounds are a few decaying tones with a burst of noise; applause is many separate
/// short claps; a cheer adds a swelling band of voices under them.
/// </summary>
public static class SoundSynth
{
    public const int SampleRate = 44100;
    private const double Peak = 0.85;

    /// <summary>The sound for a club code (DR, W3, H4, I7, PW, SW, PT...).</summary>
    public static SoundEffect ImpactFor(string club)
    {
        string c = club.Trim().ToUpperInvariant();
        if (c == "PT") return SoundEffect.Putt;
        if (c.Length > 0 && c[0] is 'D' or 'W' or 'H') return SoundEffect.Wood;
        return c is "PW" or "GW" or "AW" or "SW" or "LW" ? SoundEffect.Wedge : SoundEffect.Iron;
    }

    public static short[] Make(SoundEffect effect) => Finish(effect switch
    {
        SoundEffect.Wood => Struck(0.42, 1, new[] { (1900.0, 0.8, 90.0), (3400.0, 0.55, 120.0), (5300.0, 0.3, 170.0), (420.0, 0.5, 45.0) }, noise: 0.7, noiseDecay: 180),
        SoundEffect.Iron => Struck(0.28, 2, new[] { (2700.0, 0.55, 150.0), (4300.0, 0.35, 210.0), (650.0, 0.65, 65.0) }, noise: 0.9, noiseDecay: 260),
        SoundEffect.Wedge => Struck(0.22, 3, new[] { (2300.0, 0.4, 190.0), (800.0, 0.7, 85.0) }, noise: 0.8, noiseDecay: 320),
        SoundEffect.Putt => Struck(0.16, 4, new[] { (1250.0, 0.7, 120.0), (420.0, 0.5, 70.0) }, noise: 0.15, noiseDecay: 500),
        SoundEffect.Landing => Struck(0.2, 5, new[] { (130.0, 0.9, 32.0), (260.0, 0.3, 45.0) }, noise: 0.25, noiseDecay: 70),
        SoundEffect.Tree => Tree(),
        SoundEffect.Cup => Cup(),
        SoundEffect.Applause => Crowd(3.2, claps: 650, voices: 0, seed: 7),
        SoundEffect.Cheer => Crowd(4.8, claps: 1500, voices: 0.8, seed: 8),
        SoundEffect.Ready => Chime(),
        _ => throw new ArgumentOutOfRangeException(nameof(effect)),
    });

    /// <summary>The launch monitor is ready: two soft, rising bell notes (a fifth apart).</summary>
    private static double[] Chime()
    {
        var d = new double[(int)(0.75 * SampleRate)];
        foreach (var (start, hz) in new[] { (0.0, 880.0), (0.13, 1318.5) })
        {
            int at = (int)(start * SampleRate);
            for (int i = 0; at + i < d.Length; i++)
            {
                double t = (double)i / SampleRate;
                double attack = Math.Min(1, t / 0.006);   // a soft start, so it doesn't click
                double tone = Math.Sin(2 * Math.PI * hz * t) + 0.3 * Math.Sin(2 * Math.PI * hz * 2 * t) * Math.Exp(-6 * t);
                d[at + i] += attack * Math.Exp(-5.5 * t) * tone;
            }
        }
        return d;
    }

    /// <summary>Little-endian 16-bit samples, as an audio engine wants them.</summary>
    public static byte[] ToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>A strike: tones (frequency in Hz, level, how fast each dies away per second) and a burst of noise, from a standing start.</summary>
    private static double[] Struck(double seconds, int seed, (double Hz, double Level, double Decay)[] tones, double noise, double noiseDecay)
    {
        var random = new Random(seed);
        var d = new double[(int)(seconds * SampleRate)];
        double last = 0;
        for (int i = 0; i < d.Length; i++)
        {
            double t = (double)i / SampleRate, v = 0;
            foreach (var (hz, level, decay) in tones) v += level * Math.Exp(-decay * t) * Math.Sin(2 * Math.PI * hz * t);
            // Differenced noise leans bright, which reads as the click of contact.
            double n = random.NextDouble() * 2 - 1;
            v += noise * Math.Exp(-noiseDecay * t) * (n - last) * 0.5;
            last = n;
            d[i] = v;
        }
        return d;
    }

    /// <summary>A knock on wood, then leaves.</summary>
    private static double[] Tree()
    {
        var d = Struck(0.9, 6, new[] { (780.0, 0.8, 75.0), (1750.0, 0.35, 120.0), (270.0, 0.5, 40.0) }, noise: 0.5, noiseDecay: 200);
        var random = new Random(16);
        double low = 0;
        for (int i = 0; i < d.Length; i++)
        {
            double t = (double)i / SampleRate;
            double n = random.NextDouble() * 2 - 1;
            low += (n - low) * 0.35;
            // The rustle comes up just after the knock and fades.
            d[i] += (n - low) * 0.28 * Math.Min(1, t / 0.05) * Math.Exp(-5 * t);
        }
        return d;
    }

    /// <summary>The ball dropping in: a hollow knock on the liner, then a couple of quicker settles.</summary>
    private static double[] Cup()
    {
        var d = new double[(int)(0.62 * SampleRate)];
        foreach (var (start, level) in new[] { (0.0, 1.0), (0.16, 0.65), (0.27, 0.42), (0.34, 0.25), (0.38, 0.15) })
        {
            var knock = Struck(0.2, 9, new[] { (920.0, 0.7, 55.0), (1680.0, 0.35, 85.0), (410.0, 0.5, 40.0) }, noise: 0.2, noiseDecay: 400);
            int at = (int)(start * SampleRate);
            for (int i = 0; i < knock.Length && at + i < d.Length; i++) d[at + i] += knock[i] * level;
        }
        return d;
    }

    /// <summary>Claps scattered over a swell that rises fast and tails off slowly, with an optional band of voices underneath.</summary>
    private static double[] Crowd(double seconds, int claps, double voices, int seed)
    {
        var random = new Random(seed);
        var d = new double[(int)(seconds * SampleRate)];
        static double Swell(double u) => Math.Min(1, u / 0.1) * Math.Pow(1 - u, 1.4);

        for (int c = 0; c < claps; c++)
        {
            // Pick moments in proportion to the swell, so claps thin out as the crowd stops.
            double u;
            do u = random.NextDouble(); while (random.NextDouble() > Swell(u));
            int at = (int)(u * d.Length);
            double level = 0.25 + random.NextDouble() * 0.75;
            double decay = 180 + random.NextDouble() * 220;
            int length = (int)(0.035 * SampleRate);
            double low = 0;
            for (int i = 0; i < length && at + i < d.Length; i++)
            {
                double n = random.NextDouble() * 2 - 1;
                low += (n - low) * 0.5;
                d[at + i] += (n - low * 0.7) * level * Math.Exp(-decay * i / SampleRate);
            }
        }
        if (voices <= 0) return d;

        // Voices: noise narrowed to the range of a shout by two one-pole filters, wandering slowly in level.
        double a = 0, b = 0, wander = 0.5;
        for (int i = 0; i < d.Length; i++)
        {
            double n = random.NextDouble() * 2 - 1;
            a += (n - a) * 0.16;
            b += (a - b) * 0.025;
            wander = Math.Clamp(wander + (random.NextDouble() - 0.5) * 0.0015, 0.25, 1);
            d[i] += (a - b) * voices * wander * Swell((double)i / d.Length) * 7;
        }
        return d;
    }

    /// <summary>Levels the sound to one peak and shortens the last few milliseconds so it can't end on a click.</summary>
    private static short[] Finish(double[] d)
    {
        double peak = d.Max(Math.Abs);
        double gain = peak > 0 ? Peak / peak : 0;
        int fade = Math.Min(d.Length, SampleRate / 200);
        var s = new short[d.Length];
        for (int i = 0; i < d.Length; i++)
        {
            double tail = Math.Min(1, (double)(d.Length - 1 - i) / fade);
            s[i] = (short)Math.Round(d[i] * gain * tail * short.MaxValue);
        }
        return s;
    }
}
