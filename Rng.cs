namespace TikTokLiveAutoLiker;

static class Rng
{
    [ThreadStatic] static Random? _r;

    static Random R => _r ??= new Random(Guid.NewGuid().GetHashCode());

    public static double Unit() => R.NextDouble();

    public static int Int(int min, int max) => max <= min ? min : R.Next(min, max + 1);

    public static bool Chance(double p) => R.NextDouble() < p;

    public static double Gaussian(double mean, double sigma)
    {
        double u1 = 1.0 - R.NextDouble();
        double u2 = R.NextDouble();
        return mean + sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    // Two uniforms averaged: stays inside the bounds the user set but clusters mid-range
    // the way repeated human timings do, instead of sitting flat across the whole span.
    public static int Triangular(int min, int max)
    {
        if (max <= min) return min;
        double t = (R.NextDouble() + R.NextDouble()) / 2.0;
        return min + (int)Math.Round(t * (max - min));
    }
}
