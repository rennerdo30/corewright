namespace Corewright.Service;

/// <summary>
/// PM-51: how responsive the PC is, from what the service's tick already has (owner, 2026-11-13: "Process Lasso … shows
/// the responsiveness of the system … detect system stalls"). No thread, timer or wake-up of its own:
/// <list type="bullet">
/// <item>scheduling delay: how late the tick woke up after its timed wait (a normal-priority thread could not run);</item>
/// <item>processor queue: ready threads per logical processor in the sampler's snapshot;</item>
/// <item>DPC and interrupt time on the busiest logical processor since the previous tick (driver load, usually CPU 0);</item>
/// <item>hard page faults per second, counted only while less than 10 % of the memory is free (paging).</item>
/// </list>
/// Each signal maps to a 0..1 stall share between a "fine" and a "stalled" level; the score is 100 minus the worst share.
/// A stall is a score below <see cref="StallBelow"/> for <see cref="StallAfterMs"/> (or one tick that woke a second
/// late); it ends after <see cref="RecoverAfterMs"/> at or above <see cref="RecoverAt"/>.
/// </summary>
internal sealed class ResponsivenessMeter
{
    public const int StallBelow = 50, RecoverAt = 70, StallAfterMs = 1500, RecoverAfterMs = 5000, HardStallMs = 1000;
    /// <summary>A named signal at this stall share or more is the cause (and, for CPU or interrupts, arms core 0).</summary>
    private const double NamedCause = 0.3;
    /// <summary>How long Autopilot keeps core 0 free after the last CPU or interrupt stall ("stall" trigger).</summary>
    public const int CoreZeroHoldMs = 5 * 60_000;

    private static readonly string[] Numbers = [.. Enumerable.Range(0, 101).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    public int Score { get; private set; } = 100;
    /// <summary>The signal behind the current score: none, scheduling, cpu, interrupts or memory.</summary>
    public string Cause { get; private set; } = "none";
    public double LatenessMs { get; private set; }
    public double ReadyPerCpu { get; private set; }
    public float DpcIsrPercent { get; private set; }
    public int DpcIsrCpu { get; private set; }
    public double HardFaultsPerSec { get; private set; }

    public bool Stalled { get; private set; }
    public long StallStartMs { get; private set; }
    public int StallLowest { get; private set; } = 100;
    public string StallCause { get; private set; } = "none";
    /// <summary>Stalls since the service started.</summary>
    public int Stalls { get; private set; }
    /// <summary>Unix ms of the last stall's start, 0 when none.</summary>
    public long LastStallUnixMs { get; private set; }
    /// <summary>Tick time until which a CPU or interrupt stall asks for core 0 to be kept free.</summary>
    public long CoreZeroUntilMs { get; private set; } = long.MinValue;

    private long _lowSinceMs = -1, _okSinceMs = -1;

    public string ScoreText => Numbers[Score];

    /// <param name="latenessMs">How late the timed wait ended; NaN when the tick was woken early or ran outside the loop.</param>
    /// <returns>True when a stall started or ended in this tick.</returns>
    public bool Update(long nowMs, double latenessMs, int readyThreads, int logical, float[] dpcIsr, double hardFaultsPerSec, double memoryUsedPercent)
    {
        if (!double.IsNaN(latenessMs)) LatenessMs = latenessMs;
        ReadyPerCpu = (double)readyThreads / Math.Max(1, logical);
        float worst = 0; int worstCpu = 0;
        for (int i = 0; i < dpcIsr.Length; i++)
            if (dpcIsr[i] > worst) { worst = dpcIsr[i]; worstCpu = i; }
        DpcIsrPercent = worst;
        DpcIsrCpu = worstCpu;
        HardFaultsPerSec = hardFaultsPerSec;

        double sLate = double.IsNaN(latenessMs) ? 0 : Ramp(latenessMs, 50, 500);
        double sQueue = Ramp(ReadyPerCpu, 1, 3);
        // 5 to 25 % on one logical processor is normal on an idle desktop (owner's PC, 2026-11-13: up to 23 % on CPU 1).
        double sDpc = Ramp(worst, 30, 80);
        double sMem = memoryUsedPercent >= 90 ? Ramp(hardFaultsPerSec, 200, 2000) : 0;
        double share = Math.Max(Math.Max(sLate, sQueue), Math.Max(sDpc, sMem));
        Score = (int)Math.Round(100 * (1 - share));
        // The delay is the symptom: a named signal with a clear share is the cause even when the delay saturates first.
        double named = Math.Max(sDpc, Math.Max(sQueue, sMem));
        Cause = share <= 0 ? "none"
            : named < NamedCause && sLate > named ? "scheduling"
            : sDpc >= named ? "interrupts"
            : sQueue >= named ? "cpu"
            : "memory";

        bool changed = false;
        bool hard = !double.IsNaN(latenessMs) && latenessMs >= HardStallMs;
        if (Score < StallBelow)
        {
            _okSinceMs = -1;
            if (_lowSinceMs < 0) _lowSinceMs = nowMs;
            if (!Stalled && (nowMs - _lowSinceMs >= StallAfterMs || hard))
            {
                Stalled = changed = true;
                StallStartMs = _lowSinceMs;
                StallLowest = 100;
                Stalls++;
                LastStallUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (nowMs - _lowSinceMs);
            }
        }
        else
        {
            _lowSinceMs = -1;
            if (Score >= RecoverAt)
            {
                if (_okSinceMs < 0) _okSinceMs = nowMs;
                if (Stalled && nowMs - _okSinceMs >= RecoverAfterMs) { Stalled = false; changed = true; }
            }
            else _okSinceMs = -1;
        }
        if (Stalled && Score <= StallLowest)
        {
            StallLowest = Score;
            StallCause = Cause;
        }
        // Core 0 helps when programs crowd the processors or drivers load CPU 0, not against paging.
        if (Stalled && (sDpc >= NamedCause || sQueue >= NamedCause)) CoreZeroUntilMs = nowMs + CoreZeroHoldMs;
        return changed;
    }

    /// <summary>After sleep: a low reading from before it must not combine with one after it into a stall.</summary>
    public void Resumed()
    {
        _lowSinceMs = -1;
        LatenessMs = 0;
    }

    public bool CoreZeroWanted(long nowMs) => nowMs < CoreZeroUntilMs;

    public string Describe(string cause) => cause switch
    {
        "cpu" => $"{ReadyPerCpu:0.0} threads per processor waiting to run",
        "interrupts" => $"CPU {DpcIsrCpu} spent {DpcIsrPercent:0} % in driver interrupts and DPCs",
        "memory" => $"{HardFaultsPerSec:0} hard page faults per second with little free memory",
        "scheduling" => $"threads woke up {LatenessMs:0} ms late",
        _ => "no cause",
    };

    public static string CauseName(string cause) => cause switch
    {
        "cpu" => "processor queue",
        "interrupts" => "driver interrupts and DPCs",
        "memory" => "paging",
        "scheduling" => "late wake-ups",
        _ => "no cause",
    };

    private static double Ramp(double v, double fine, double stalled) => Math.Clamp((v - fine) / (stalled - fine), 0, 1);
}
