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
    (address, _) => address == Parse("192.168.1.101") ? ProbeResult.Replied : ProbeResult.TimedOut);
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
}, (address, _) => address switch
{
    var value when value == Parse("10.7.3.42") || value == Parse("10.7.3.46") => ProbeResult.Replied,
    var value when value == Parse("10.7.3.44") => ProbeResult.Unknown,
    _ => ProbeResult.TimedOut
});
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
}, (_, _) => ProbeResult.TimedOut);
Require(large.Checked == 65534 && large.CandidateCount == 65534
    && streamedCandidates == 65534 && largestUpdate <= 128,
    "/16 full scan streams bounded updates and completes");

ScanOptions cancelOptions = Scanner.Parse("10.9.0.0", "/24", "", "", "", true);
using CancellationTokenSource cancellation = new();
using ManualResetEventSlim probeStarted = new();
Task<ScanProgress> cancelledScan = Task.Run(() => Scanner.Scan(cancelOptions, cancellation.Token,
    _ => { }, (_, probeToken) =>
    {
        probeStarted.Set();
        probeToken.WaitHandle.WaitOne();
        return ProbeResult.Unknown;
    }));
Require(probeStarted.Wait(TimeSpan.FromSeconds(5)), "cancel test started a probe");
cancellation.Cancel();
Require(cancelledScan.Wait(TimeSpan.FromSeconds(5)) && cancelledScan.Result.Checked == 0,
    "cancellation ends scan without counting unfinished probes");
Require(Scanner.GatewayResponds(Parse("127.0.0.1"), CancellationToken.None), "ICMP loopback");

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
