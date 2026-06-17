using System;
using System.Diagnostics;

namespace TruePing.Network;

/// <summary>Immutable snapshot of the ping statistics over a time window.</summary>
public readonly record struct PingStats(
    bool HasData,
    double CurrentMs,
    double AvgMs,
    double MinMs,
    double MaxMs,
    double JitterMs,
    double LossPercent,
    int SampleCount,
    double AgeSeconds)
{
    public static readonly PingStats Empty = new(false, 0, 0, 0, 0, 0, 0, 0, double.PositiveInfinity);
}

/// <summary>
/// Thread-safe store of round-trip samples. The network thread feeds samples in
/// (<see cref="AddSample"/> / <see cref="AddLoss"/>); the UI thread reads consolidated
/// stats out (<see cref="Snapshot"/>). All access is guarded by a single lock; the
/// critical sections are tiny so the game's network thread never stalls on us.
/// </summary>
public sealed class PingMonitor
{
    private readonly struct Sample
    {
        public readonly double Ms;
        public readonly long At; // Stopwatch ticks
        public Sample(double ms, long at) { Ms = ms; At = at; }
    }

    private readonly object gate = new();
    private readonly Sample[] ring;
    private int head;
    private int count;

    // Timestamps of lost keepalives, so loss can be reported over the same time window as the
    // latency (a recent figure, not a session-lifetime average).
    private readonly long[] lossAt;
    private int lossHead;
    private int lossCount;

    private long lastSampleAt;

    public PingMonitor(int capacity = 512)
    {
        ring = new Sample[capacity];
        lossAt = new long[capacity];
        lastSampleAt = 0;
    }

    /// <summary>A keepalive round-trip completed: record its latency in milliseconds.</summary>
    public void AddSample(double ms)
    {
        if (ms < 0 || ms > 60_000) return; // discard absurd readings
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            ring[head] = new Sample(ms, now);
            head = (head + 1) % ring.Length;
            if (count < ring.Length) count++;
            lastSampleAt = now;
        }
    }

    /// <summary>A keepalive went out but never came back within the timeout: count it as loss.</summary>
    public void AddLoss(int n = 1)
    {
        if (n <= 0) return;
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                lossAt[lossHead] = now;
                lossHead = (lossHead + 1) % lossAt.Length;
                if (lossCount < lossAt.Length) lossCount++;
            }
        }
    }

    /// <summary>Clears the history and the loss counters.</summary>
    public void Reset()
    {
        lock (gate)
        {
            Array.Clear(ring);
            Array.Clear(lossAt);
            head = count = 0;
            lossHead = lossCount = 0;
            lastSampleAt = 0;
        }
    }

    public PingStats Snapshot(double windowSeconds)
    {
        lock (gate)
        {
            if (count == 0)
                return PingStats.Empty;

            var now = Stopwatch.GetTimestamp();
            double freq = Stopwatch.Frequency;
            long windowTicks = (long)(windowSeconds * freq);

            double sum = 0, min = double.MaxValue, max = double.MinValue;
            double current = 0;
            int n = 0;
            double sumSq = 0; // for the standard deviation we report as jitter

            for (int i = 0; i < count; i++)
            {
                int idx = ((head - 1 - i) % ring.Length + ring.Length) % ring.Length;
                var s = ring[idx];
                if (windowTicks > 0 && now - s.At > windowTicks)
                    break; // ring is newest-first; once outside the window we are done
                if (n == 0) current = s.Ms;
                sum += s.Ms;
                sumSq += s.Ms * s.Ms;
                if (s.Ms < min) min = s.Ms;
                if (s.Ms > max) max = s.Ms;
                n++;
            }

            double loss = LossPercent(now, windowTicks, n);

            if (n == 0)
            {
                // Have history, but nothing inside the window: report the last value as stale.
                var last = ring[((head - 1) % ring.Length + ring.Length) % ring.Length];
                double ageStale = (now - lastSampleAt) / freq;
                return new PingStats(true, last.Ms, last.Ms, last.Ms, last.Ms, 0, loss, 0, ageStale);
            }

            double avg = sum / n;
            double variance = Math.Max(0, sumSq / n - avg * avg);
            double jitter = Math.Sqrt(variance);
            double age = (now - lastSampleAt) / freq;

            return new PingStats(true, current, avg, min, max, jitter, loss, n, age);
        }
    }

    /// <summary>Copies up to <paramref name="dest"/>.Length recent samples oldest-first for a sparkline.</summary>
    public int GetHistory(float[] dest)
    {
        lock (gate)
        {
            int n = Math.Min(dest.Length, count);
            for (int i = 0; i < n; i++)
            {
                int back = n - 1 - i; // oldest of the n we take, through to newest
                int idx = ((head - 1 - back) % ring.Length + ring.Length) % ring.Length;
                dest[i] = (float)ring[idx].Ms;
            }
            return n;
        }
    }

    /// <summary>Loss percentage over the window: lost / (answered + lost) for that window. Caller holds the lock.</summary>
    private double LossPercent(long now, long windowTicks, int samplesInWindow)
    {
        int lostInWindow = 0;
        for (int i = 0; i < lossCount; i++)
        {
            int idx = ((lossHead - 1 - i) % lossAt.Length + lossAt.Length) % lossAt.Length;
            if (windowTicks > 0 && now - lossAt[idx] > windowTicks)
                break;
            lostInWindow++;
        }

        int totalProbes = samplesInWindow + lostInWindow;
        return totalProbes == 0 ? 0 : 100.0 * lostInWindow / totalProbes;
    }
}
