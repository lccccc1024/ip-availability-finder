using System;
using System.Threading;

namespace PingCandidateFinder
{
    internal static class ScannerTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Main()
        {
            ScanOptions options = Scanner.Parse("192.168.1.99", "255.255.255.248", "192.168.1.98",
                "192.168.1.101", "2");
            Assert(options.Network == Address("192.168.1.96"), "network normalization");
            Assert(options.Prefix == 29 && options.Total == 5, "mask and gateway exclusion");

            ScanProgress last = null;
            ScanProgress result = Scanner.Scan(options, CancellationToken.None, p => last = p,
                (address, token) => address == Address("192.168.1.101")
                    ? ProbeResult.Replied : ProbeResult.TimedOut);
            Assert(result.Checked == 3 && result.Replied == 1, "checked order");
            Assert(result.Candidates.Count == 2 && result.Candidates[0] == "192.168.1.102"
                && result.Candidates[1] == "192.168.1.97", "wrap and deterministic candidates");
            Assert(last != null && last.Candidates.Count == 2, "progress report");

            bool invalid = false;
            try { Scanner.Parse("192.168.0.0", "255.0.255.0", "", "", "1"); }
            catch (ArgumentException) { invalid = true; }
            Assert(invalid, "reject non-contiguous mask");

            CancellationTokenSource cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            result = Scanner.Scan(options, cancelled.Token, p => { throw new Exception("unexpected report"); },
                (address, token) => { throw new Exception("unexpected probe"); });
            Assert(result.Checked == 0, "immediate cancellation");
            Assert(Scanner.GatewayResponds(Address("127.0.0.1"), CancellationToken.None),
                "Windows ICMP loopback probe");
            Console.WriteLine("Scanner tests passed.");
        }

        private static uint Address(string text)
        {
            uint value;
            if (!Scanner.TryAddress(text, out value)) throw new Exception("bad test address");
            return value;
        }
    }
}
