using System;
using System.Threading;

namespace Core.Metrics;

public static class CacheMetrics
{
    private static long _hits;
    private static long _misses;

    public static long Hits => Interlocked.Read(ref _hits);
    public static long Misses => Interlocked.Read(ref _misses);

    public static void RecordHit() => Interlocked.Increment(ref _hits);
    public static void RecordMiss() => Interlocked.Increment(ref _misses);

    public static void Reset()
    {
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
    }

    public static (long Hits, long Misses, double HitRatePercentage) GetSnapshot()
    {
        long h = Interlocked.Read(ref _hits);
        long m = Interlocked.Read(ref _misses);
        long total = h + m;
        double rate = total > 0 ? ((double)h / total) * 100.0 : 0.0;
        return (h, m, rate);
    }
}
