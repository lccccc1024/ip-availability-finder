using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PingCandidateFinder
{
    internal enum ProbeResult { Replied, TimedOut, Unknown }

    internal sealed class ScanOptions
    {
        public uint Network;
        public uint Broadcast;
        public uint? Gateway;
        public uint? Start;
        public int Prefix;
        public int Count;
        public int Total;
        public bool FullScan;
    }

    internal sealed class ScanProgress
    {
        public int Checked;
        public int Replied;
        public int Unknown;
        public int Total;
        public int CandidateCount;
        public List<string> Candidates = new List<string>();
        public List<string> RepliedAddresses = new List<string>();
        public List<string> UnknownAddresses = new List<string>();

        public ScanProgress CloneSince(int previousCandidateCount, int previousRepliedCount, int previousUnknownCount)
        {
            return new ScanProgress { Checked = Checked, Replied = Replied, Unknown = Unknown,
                Total = Total, CandidateCount = Candidates.Count,
                Candidates = Candidates.GetRange(previousCandidateCount, Candidates.Count - previousCandidateCount),
                RepliedAddresses = RepliedAddresses.GetRange(previousRepliedCount,
                    RepliedAddresses.Count - previousRepliedCount),
                UnknownAddresses = UnknownAddresses.GetRange(previousUnknownCount,
                    UnknownAddresses.Count - previousUnknownCount) };
        }
    }

    internal static class Scanner
    {
        public static ScanOptions Parse(string networkText, string maskText, string gatewayText, string startText,
            string countText, bool fullScan = false)
        {
            uint networkAddress;
            if (!TryAddress(networkText.Trim(), out networkAddress))
                throw new ArgumentException("网段 IP 无效。示例：192.168.10.0。");

            int prefix;
            string mask = maskText.Trim().TrimStart('/');
            if (!int.TryParse(mask, out prefix))
            {
                uint maskAddress;
                if (!TryAddress(mask, out maskAddress))
                    throw new ArgumentException("子网掩码无效。示例：23 或 255.255.254.0。");
                prefix = 0;
                bool sawZero = false;
                for (int bit = 31; bit >= 0; bit--)
                {
                    bool one = (maskAddress & (1u << bit)) != 0;
                    if (one && sawZero) throw new ArgumentException("子网掩码必须是连续的 1。");
                    if (one) prefix++; else sawZero = true;
                }
            }
            if (prefix < 16 || prefix > 30) throw new ArgumentException("支持的 IPv4 网段大小为 /16 到 /30。");

            int count = 0;
            if (!fullScan && (!int.TryParse(countText.Trim(), out count) || count < 1 || count > 1000))
                throw new ArgumentException("候选数量须为 1 到 1000 之间的整数。");

            uint? gateway = OptionalAddress(gatewayText, "网关");
            uint? start = OptionalAddress(startText, "起始 IP");
            uint maskBits = 0xffffffffu << (32 - prefix);
            uint first = networkAddress & maskBits;
            uint last = first | ~maskBits;
            if (start.HasValue && (start.Value <= first || start.Value >= last))
                throw new ArgumentException("起始 IP 必须位于网段的可用主机地址范围内。");
            int total = checked((int)(last - first - 1));
            if (gateway.HasValue && gateway.Value > first && gateway.Value < last) total--;
            if (!fullScan && count > total) throw new ArgumentException("所需候选数量超过这个网段可扫描的地址数。");
            if (fullScan) count = total;
            return new ScanOptions { Network = first, Broadcast = last, Prefix = prefix,
                Gateway = gateway, Start = start, Count = count, Total = total, FullScan = fullScan };
        }

        private static uint? OptionalAddress(string text, string label)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            uint result;
            if (!TryAddress(text.Trim(), out result)) throw new ArgumentException(label + "不是有效的 IPv4 地址。");
            return result;
        }

        public static bool TryAddress(string text, out uint value)
        {
            value = 0;
            string[] parts = text.Split('.');
            if (parts.Length != 4) return false;
            uint result = 0;
            foreach (string part in parts)
            {
                byte octet;
                if (part.Length == 0 || !byte.TryParse(part, NumberStyles.None,
                    CultureInfo.InvariantCulture, out octet)) return false;
                result = (result << 8) | octet;
            }
            value = result;
            return true;
        }

        public static string Format(uint address)
        {
            return String.Format("{0}.{1}.{2}.{3}", address >> 24, (address >> 16) & 255,
                (address >> 8) & 255, address & 255);
        }

        private static ProbeResult Probe(uint address, CancellationToken token)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (token.IsCancellationRequested) return ProbeResult.Unknown;
                try
                {
                    using (Ping ping = new Ping())
                    {
                        PingReply reply = ping.SendPingAsync(Format(address),
                            TimeSpan.FromMilliseconds(600), cancellationToken: token)
                            .GetAwaiter().GetResult();
                        if (reply.Status == IPStatus.Success) return ProbeResult.Replied;
                        if (reply.Status != IPStatus.TimedOut) return ProbeResult.Unknown;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return ProbeResult.Unknown; }
                catch (PingException) { return ProbeResult.Unknown; }
                catch (SocketException) { return ProbeResult.Unknown; }
            }
            return ProbeResult.TimedOut;
        }

        public static bool GatewayResponds(uint address, CancellationToken token)
        {
            return Probe(address, token) == ProbeResult.Replied;
        }

        private static IEnumerable<uint> Addresses(ScanOptions options)
        {
            uint first = options.Network + 1;
            uint last = options.Broadcast - 1;
            uint start = options.Start.HasValue ? options.Start.Value : first;
            for (uint address = start; address <= last; address++)
                if (!options.Gateway.HasValue || address != options.Gateway.Value) yield return address;
            for (uint address = first; address < start; address++)
                if (!options.Gateway.HasValue || address != options.Gateway.Value) yield return address;
        }

        public static ScanProgress Scan(ScanOptions options, CancellationToken token, Action<ScanProgress> report)
        {
            return Scan(options, token, report, Probe);
        }

        internal static ScanProgress Scan(ScanOptions options, CancellationToken token,
            Action<ScanProgress> report, Func<uint, CancellationToken, ProbeResult> prober)
        {
            ScanProgress progress = new ScanProgress { Total = options.Total };
            int batchSize = Math.Min(256, Math.Max(32, options.Count * 4));
            List<uint> batch = new List<uint>();
            int reportedCandidates = 0;
            int reportedReplied = 0;
            int reportedUnknown = 0;
            foreach (uint address in Addresses(options))
            {
                if (token.IsCancellationRequested || ShouldStop(options, progress)) break;
                batch.Add(address);
                if (batch.Count < batchSize) continue;
                CommitBatch(batch, token, options, progress, report, prober,
                    ref reportedCandidates, ref reportedReplied, ref reportedUnknown);
                batch.Clear();
                if (!options.FullScan && progress.Checked >= 512 && progress.Candidates.Count == 0) batchSize = 256;
            }
            if (!token.IsCancellationRequested && !ShouldStop(options, progress) && batch.Count > 0)
                CommitBatch(batch, token, options, progress, report, prober,
                    ref reportedCandidates, ref reportedReplied, ref reportedUnknown);
            EmitProgress(progress, report, ref reportedCandidates, ref reportedReplied, ref reportedUnknown);
            progress.CandidateCount = progress.Candidates.Count;
            return progress;
        }

        private static bool ShouldStop(ScanOptions options, ScanProgress progress)
        {
            return !options.FullScan && progress.Candidates.Count >= options.Count;
        }

        private static void EmitProgress(ScanProgress progress, Action<ScanProgress> report,
            ref int reportedCandidates, ref int reportedReplied, ref int reportedUnknown)
        {
            report(progress.CloneSince(reportedCandidates, reportedReplied, reportedUnknown));
            reportedCandidates = progress.Candidates.Count;
            reportedReplied = progress.RepliedAddresses.Count;
            reportedUnknown = progress.UnknownAddresses.Count;
        }

        private static void CommitBatch(List<uint> batch, CancellationToken token, ScanOptions options,
            ScanProgress progress, Action<ScanProgress> report, Func<uint, CancellationToken, ProbeResult> prober,
            ref int reportedCandidates, ref int reportedReplied, ref int reportedUnknown)
        {
            using CancellationTokenSource batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<ProbeResult>[] tasks = new Task<ProbeResult>[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                uint address = batch[i];
                tasks[i] = Task.Run(() => prober(address, batchCancellation.Token), batchCancellation.Token);
            }
            try
            {
                for (int i = 0; i < tasks.Length; i++)
                {
                    if (token.IsCancellationRequested || ShouldStop(options, progress)) return;
                    ProbeResult result;
                    try { result = tasks[i].WaitAsync(token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    if (token.IsCancellationRequested) return;
                    progress.Checked++;
                    if (result == ProbeResult.Replied)
                    {
                        progress.Replied++;
                        progress.RepliedAddresses.Add(Format(batch[i]));
                    }
                    else if (result == ProbeResult.TimedOut) progress.Candidates.Add(Format(batch[i]));
                    else
                    {
                        progress.Unknown++;
                        progress.UnknownAddresses.Add(Format(batch[i]));
                    }
                    if (i == tasks.Length - 1 || ShouldStop(options, progress)
                        || progress.Checked % (options.FullScan ? 128 : 16) == 0
                        || (!options.FullScan && result == ProbeResult.TimedOut && progress.Candidates.Count == 1))
                        EmitProgress(progress, report, ref reportedCandidates, ref reportedReplied, ref reportedUnknown);
                }
            }
            finally { batchCancellation.Cancel(); }
        }
    }
}
