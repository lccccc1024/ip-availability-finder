import sys
from pathlib import Path
from ipaddress import IPv4Address, IPv4Network
from threading import Event
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ping_candidate_finder.scan import ProbeResult, ScanOptions, addresses, parse_options, scan


class FakeProber:
    def __init__(self, results: dict[str, ProbeResult], delays: dict[str, float] | None = None):
        self.results = results
        self.delays = delays or {}
        self.calls: dict[str, int] = {}

    def probe(self, address: IPv4Address, timeout_ms: int) -> ProbeResult:
        key = str(address)
        self.calls[key] = self.calls.get(key, 0) + 1
        time.sleep(self.delays.get(key, 0))
        return self.results.get(key, ProbeResult.TIMED_OUT)


class ScanTests(unittest.TestCase):
    def test_input_normalizes_network_and_checks_range(self):
        options = parse_options("192.168.11.5", "255.255.254.0", "192.168.10.1", "192.168.11.20", "5")
        self.assertEqual(str(options.network), "192.168.10.0/23")
        self.assertEqual(options.count, 5)
        with self.assertRaises(ValueError):
            parse_options("192.168.10.0", "15", "", "", "5")
        with self.assertRaises(ValueError):
            parse_options("192.168.10.0", "23", "", "192.168.12.1", "5")

    def test_scan_order_wraps_and_skips_gateway(self):
        options = ScanOptions(IPv4Network("10.0.0.0/29"), 2, IPv4Address("10.0.0.1"), IPv4Address("10.0.0.5"))
        self.assertEqual([str(ip) for ip in addresses(options)], [
            "10.0.0.5", "10.0.0.6", "10.0.0.2", "10.0.0.3", "10.0.0.4"
        ])

    def test_earliest_candidates_win_despite_out_of_order_replies(self):
        options = ScanOptions(IPv4Network("10.0.0.0/29"), 2)
        prober = FakeProber(
            {"10.0.0.2": ProbeResult.REPLIED},
            {"10.0.0.1": 0.015},
        )
        updates = []
        result = scan(options, prober, Event(), updates.append)
        self.assertEqual([str(ip) for ip in result.candidates], ["10.0.0.1", "10.0.0.3"])
        self.assertEqual(result.checked, 3)
        self.assertEqual(result.replied, 1)
        self.assertEqual(prober.calls["10.0.0.1"], 2)

    def test_network_error_never_becomes_candidate(self):
        options = ScanOptions(IPv4Network("10.0.0.0/30"), 1)
        prober = FakeProber({"10.0.0.1": ProbeResult.UNKNOWN})
        result = scan(options, prober, Event(), lambda _: None)
        self.assertEqual([str(ip) for ip in result.candidates], ["10.0.0.2"])
        self.assertEqual(result.unknown, 1)
        self.assertEqual(prober.calls["10.0.0.1"], 1)

    def test_slow_first_address_does_not_trigger_unbounded_probe_ahead(self):
        options = ScanOptions(IPv4Network("10.0.0.0/24"), 1)
        prober = FakeProber({}, {"10.0.0.1": 0.03})
        result = scan(options, prober, Event(), lambda _: None)
        self.assertEqual([str(ip) for ip in result.candidates], ["10.0.0.1"])
        self.assertLessEqual(len(prober.calls), 32)

    def test_cancelled_scan_does_not_schedule_new_hosts(self):
        options = ScanOptions(IPv4Network("10.0.0.0/24"), 50)
        stop = Event()
        prober = FakeProber({}, {"10.0.0.1": 0.01})

        def cancel_on_first(progress):
            stop.set()

        result = scan(options, prober, stop, cancel_on_first)
        self.assertTrue(stop.is_set())
        self.assertLess(result.checked, 50)


if __name__ == "__main__":
    unittest.main()
