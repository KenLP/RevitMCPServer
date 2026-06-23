using System.Threading;
using System.Text.Json.Nodes;

namespace RevitMCPAddin.Server;

/// <summary>
/// Thread-safe in-process request counters, surfaced by <c>GET /stats</c>.
/// All mutation is lock-free (Interlocked) so it adds negligible overhead to the
/// request path.
/// </summary>
internal sealed class ServerMetrics
{
    private long _total;
    private long _success;
    private long _failed;
    private long _rejected;
    private long _totalDurationMs;
    private long _peakDurationMs;
    private int _inFlight;

    public int IncInFlight() => Interlocked.Increment(ref _inFlight);
    public void DecInFlight() => Interlocked.Decrement(ref _inFlight);
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Count a request rejected by backpressure (never entered the handler).</summary>
    public void RecordRejected() => Interlocked.Increment(ref _rejected);

    /// <summary>Record a completed request (ok = HTTP 200) and its duration.</summary>
    public void Record(bool ok, long durationMs)
    {
        Interlocked.Increment(ref _total);
        if (ok) Interlocked.Increment(ref _success);
        else Interlocked.Increment(ref _failed);
        Interlocked.Add(ref _totalDurationMs, durationMs);

        // Lock-free peak update.
        long prev;
        do
        {
            prev = Volatile.Read(ref _peakDurationMs);
            if (durationMs <= prev) break;
        }
        while (Interlocked.CompareExchange(ref _peakDurationMs, durationMs, prev) != prev);
    }

    public JsonObject Snapshot()
    {
        long total = Interlocked.Read(ref _total);
        long totalDur = Interlocked.Read(ref _totalDurationMs);
        return new JsonObject
        {
            ["totalRequests"] = total,
            ["success"] = Interlocked.Read(ref _success),
            ["failed"] = Interlocked.Read(ref _failed),
            ["rejected"] = Interlocked.Read(ref _rejected),
            ["inFlight"] = InFlight,
            ["avgDurationMs"] = total > 0 ? System.Math.Round((double)totalDur / total, 1) : 0,
            ["peakDurationMs"] = Interlocked.Read(ref _peakDurationMs),
        };
    }
}
