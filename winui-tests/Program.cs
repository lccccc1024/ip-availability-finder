using System.Runtime.InteropServices;
using PingCandidateFinder;
using PingCandidateFinder.WinUI;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void RejectInput(Action parse, string message)
{
    try { parse(); }
    catch (ArgumentException) { return; }
    throw new Exception(message);
}

RejectInput(() => Scanner.Parse("192.168.1.0", "255.0.255.0", "", "", "1"),
    "noncontiguous mask rejected");
RejectInput(() => Scanner.Parse("192.168.1.0", "/15", "", "", "1"),
    "network larger than /16 rejected");
RejectInput(() => Scanner.Parse("192.168.1.0", "/31", "", "", "1"),
    "network smaller than /30 rejected");
RejectInput(() => Scanner.Parse("192.168.1.0", "/24", "", "192.168.2.1", "1"),
    "start address outside subnet rejected");
RejectInput(() => Scanner.Parse("192.168.1.0", "/30", "192.168.1.1", "", "2"),
    "candidate request above eligible host count rejected");
Require(!Scanner.TryAddress("+1.2.3.4", out _) && !Scanner.TryAddress("1. 2.3.4", out _),
    "noncanonical IPv4 octets rejected");

ScanOptions options = Scanner.Parse("192.168.1.99", "255.255.255.248", "192.168.1.98",
    "192.168.1.101", "2");
ScanProgress scan = Scanner.Scan(options, CancellationToken.None, _ => { },
    (address, _) => Task.FromResult(address == Parse("192.168.1.101") ? ProbeResult.Replied : ProbeResult.TimedOut));
Require(scan.Checked == 3 && scan.Candidates.SequenceEqual(new[] { "192.168.1.102", "192.168.1.97" }),
    "scan order and wrap");

ScanOptions fullOptions = Scanner.Parse("10.7.3.42", "29", "10.7.3.41", "", "1", true);
List<string> reportedCandidates = new();
List<string> reportedReplied = new();
List<string> reportedUnknown = new();
ScanProgress full = Scanner.Scan(fullOptions, CancellationToken.None, progress =>
{
    Require(progress.Checked == progress.Replied + progress.CandidateCount + progress.Unknown,
        "full scan totals partition checked addresses");
    reportedCandidates.AddRange(progress.Candidates);
    reportedReplied.AddRange(progress.RepliedAddresses);
    reportedUnknown.AddRange(progress.UnknownAddresses);
}, (address, _) => Task.FromResult(address switch
{
    var value when value == Parse("10.7.3.42") || value == Parse("10.7.3.46") => ProbeResult.Replied,
    var value when value == Parse("10.7.3.44") => ProbeResult.Unknown,
    _ => ProbeResult.TimedOut
}));
Require(fullOptions.FullScan && fullOptions.Total == 5 && full.Checked == 5,
    "full scan checks every eligible address despite requested count of one");
Require(full.Replied == 2 && full.Unknown == 1 && full.CandidateCount == 2,
    "full scan summary");
Require(full.Candidates.SequenceEqual(new[] { "10.7.3.43", "10.7.3.45" })
    && reportedCandidates.SequenceEqual(full.Candidates), "full scan incremental candidates");
Require(full.RepliedAddresses.SequenceEqual(new[] { "10.7.3.42", "10.7.3.46" })
    && reportedReplied.SequenceEqual(full.RepliedAddresses)
    && full.UnknownAddresses.SequenceEqual(new[] { "10.7.3.44" })
    && reportedUnknown.SequenceEqual(full.UnknownAddresses), "full scan per-address classifications");

ScanOptions largeOptions = Scanner.Parse("10.8.0.0", "/16", "", "", "", true);
int streamedCandidates = 0;
int largestUpdate = 0;
ScanProgress large = Scanner.Scan(largeOptions, CancellationToken.None, progress =>
{
    streamedCandidates += progress.Candidates.Count;
    largestUpdate = Math.Max(largestUpdate, progress.Candidates.Count);
}, (_, _) => Task.FromResult(ProbeResult.TimedOut));
Require(large.Checked == 65534 && large.CandidateCount == 65534
    && streamedCandidates == 65534 && largestUpdate <= 128,
    "/16 full scan streams bounded updates and completes");

ScanOptions cancelOptions = Scanner.Parse("10.9.0.0", "/24", "", "", "", true);
using CancellationTokenSource cancellation = new();
using ManualResetEventSlim probeStarted = new();
Task<ScanProgress> cancelledScan = Task.Run(() => Scanner.Scan(cancelOptions, cancellation.Token,
    _ => { }, async (_, probeToken) =>
    {
        probeStarted.Set();
        await Task.Delay(Timeout.Infinite, probeToken).ConfigureAwait(false);
        return ProbeResult.Unknown;
    }));
Require(probeStarted.Wait(TimeSpan.FromSeconds(5)), "cancel test started a probe");
cancellation.Cancel();
Require(cancelledScan.Wait(TimeSpan.FromSeconds(5)) && cancelledScan.Result.Checked == 0,
    "cancellation ends scan without counting unfinished probes");
Require(Scanner.GatewayResponds(Parse("127.0.0.1"), CancellationToken.None), "ICMP loopback");

// 网关本身不参与扫描
ScanOptions gatewayOptions = Scanner.Parse("10.12.0.0", "/29", "10.12.0.3", "", "1", true);
HashSet<uint> probedAddresses = new();
ScanProgress gatewayScan = Scanner.Scan(gatewayOptions, CancellationToken.None, _ => { }, (address, _) =>
{
    probedAddresses.Add(address);
    return Task.FromResult(ProbeResult.Replied);
});
Require(!probedAddresses.Contains(Parse("10.12.0.3")), "gateway is never probed");
Require(probedAddresses.Count == gatewayOptions.Total && gatewayScan.Checked == gatewayOptions.Total,
    "every eligible host is probed exactly once");

// 凑够候选就停，不会把整批扫完
ScanOptions earlyStopOptions = Scanner.Parse("10.13.0.0", "/24", "", "", "3");
ScanProgress earlyStop = Scanner.Scan(earlyStopOptions, CancellationToken.None, _ => { },
    (_, _) => Task.FromResult(ProbeResult.TimedOut));
Require(earlyStop.Checked == 3 && earlyStop.CandidateCount == 3, "stops as soon as enough candidates are found");
Require(earlyStop.Candidates.SequenceEqual(new[] { "10.13.0.1", "10.13.0.2", "10.13.0.3" }),
    "candidates keep address order");

// 无法判断的地址不算待确认，也不阻塞后续地址
ScanOptions unknownOptions = Scanner.Parse("10.14.0.0", "/29", "", "", "1", true);
ScanProgress unknownScan = Scanner.Scan(unknownOptions, CancellationToken.None, _ => { },
    (_, _) => Task.FromResult(ProbeResult.Unknown));
Require(unknownScan.Checked == 6 && unknownScan.Unknown == 6 && unknownScan.CandidateCount == 0,
    "unknown responses are reported separately from candidates");

// 探测必须真正并发：串行执行会远超时间上限
ScanOptions concurrentOptions = Scanner.Parse("10.15.0.0", "/24", "", "", "1", true);
object flightGate = new();
int inFlight = 0;
int peakInFlight = 0;
System.Diagnostics.Stopwatch concurrentWatch = System.Diagnostics.Stopwatch.StartNew();
ScanProgress concurrentScan = Scanner.Scan(concurrentOptions, CancellationToken.None, _ => { }, async (_, probeToken) =>
{
    lock (flightGate)
    {
        inFlight++;
        peakInFlight = Math.Max(peakInFlight, inFlight);
    }

    await Task.Delay(50, probeToken).ConfigureAwait(false);

    lock (flightGate) inFlight--;
    return ProbeResult.Replied;
});
concurrentWatch.Stop();
Require(concurrentScan.Checked == 254, "concurrency test checks the whole /24");
Require(peakInFlight >= 32, $"probes run concurrently in flight (peak {peakInFlight})");
Require(concurrentWatch.Elapsed < TimeSpan.FromSeconds(5),
    $"probes are not serialized (took {concurrentWatch.ElapsedMilliseconds} ms)");

string? clipboard = null;
int writes = 0;
Action<string> writer = value =>
{
    writes++;
    if (writes == 2) throw new COMException("clipboard temporarily locked");
    clipboard = value;
};
Func<TimeSpan, Task> noDelay = _ => Task.CompletedTask;
Require(await ClipboardCopyService.TryCopyAsync("192.168.1.102\n192.168.1.97", writer, noDelay),
    "copy all");
Require(await ClipboardCopyService.TryCopyAsync("192.168.1.102", writer, noDelay),
    "copy selected after transient failure");
Require(clipboard == "192.168.1.102" && writes == 3, "clipboard sequence");
Require(!await ClipboardCopyService.TryCopyAsync("192.168.1.102",
    _ => throw new COMException("clipboard locked"), noDelay), "permanent failure is reported");
bool unexpectedClipboardErrorPropagated = false;
try
{
    await ClipboardCopyService.TryCopyAsync("192.168.1.102",
        _ => throw new InvalidOperationException("programming error"), noDelay);
}
catch (InvalidOperationException) { unexpectedClipboardErrorPropagated = true; }
Require(unexpectedClipboardErrorPropagated, "unexpected clipboard errors are not hidden");

Console.WriteLine("Scanner and clipboard sequence tests passed.");

static uint Parse(string text)
{
    if (!Scanner.TryAddress(text, out uint value)) throw new Exception("bad test IP");
    return value;
}
