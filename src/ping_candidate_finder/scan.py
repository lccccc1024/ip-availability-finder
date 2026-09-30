from __future__ import annotations

from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
from dataclasses import dataclass
from enum import Enum
from ipaddress import IPv4Address, IPv4Network
from threading import Event
from typing import Callable, Iterator, Protocol


class ProbeResult(Enum):
    REPLIED = "replied"
    TIMED_OUT = "timed_out"
    UNKNOWN = "unknown"


class Prober(Protocol):
    def probe(self, address: IPv4Address, timeout_ms: int) -> ProbeResult: ...


@dataclass(frozen=True)
class ScanOptions:
    network: IPv4Network
    count: int
    gateway: IPv4Address | None = None
    start: IPv4Address | None = None
    timeout_ms: int = 600
    attempts: int = 2


@dataclass(frozen=True)
class ScanProgress:
    checked: int
    replied: int
    unknown: int
    candidates: tuple[IPv4Address, ...]
    total: int


def parse_options(
    network_text: str,
    mask_text: str,
    gateway_text: str,
    start_text: str,
    count_text: str,
) -> ScanOptions:
    try:
        network = IPv4Network((network_text.strip(), mask_text.strip().removeprefix("/")), strict=False)
    except ValueError as exc:
        raise ValueError("网段或掩码无效。示例：192.168.10.0 和 255.255.254.0（或 23）。") from exc
    if not 16 <= network.prefixlen <= 30:
        raise ValueError("支持的 IPv4 网段大小为 /16 到 /30。")
    try:
        count = int(count_text.strip())
    except ValueError as exc:
        raise ValueError("候选数量必须是整数。") from exc
    if not 1 <= count <= 1000:
        raise ValueError("候选数量须在 1 到 1000 之间。")
    gateway = _optional_ipv4(gateway_text, "网关")
    start = _optional_ipv4(start_text, "起始 IP")
    first, last = int(network.network_address) + 1, int(network.broadcast_address) - 1
    if start is not None and not first <= int(start) <= last:
        raise ValueError("起始 IP 必须位于网段的可用主机地址范围内。")
    total = last - first + 1 - (1 if gateway is not None and gateway in network and first <= int(gateway) <= last else 0)
    if count > total:
        raise ValueError("所需候选数量超过这个网段可扫描的地址数。")
    return ScanOptions(network=network, count=count, gateway=gateway, start=start)


def _optional_ipv4(value: str, label: str) -> IPv4Address | None:
    if not value.strip():
        return None
    try:
        return IPv4Address(value.strip())
    except ValueError as exc:
        raise ValueError(f"{label}不是有效的 IPv4 地址。") from exc


def addresses(options: ScanOptions) -> Iterator[IPv4Address]:
    first = int(options.network.network_address) + 1
    last = int(options.network.broadcast_address) - 1
    start = int(options.start) if options.start is not None else first
    excluded = int(options.gateway) if options.gateway is not None else None
    for begin, end in ((start, last), (first, start - 1)):
        for address in range(begin, end + 1):
            if address != excluded:
                yield IPv4Address(address)


def _check(address: IPv4Address, options: ScanOptions, prober: Prober) -> ProbeResult:
    for _ in range(options.attempts):
        result = prober.probe(address, options.timeout_ms)
        if result is not ProbeResult.TIMED_OUT:
            return result
    return ProbeResult.TIMED_OUT


def scan(
    options: ScanOptions,
    prober: Prober,
    cancelled: Event,
    report: Callable[[ScanProgress], None],
) -> ScanProgress:
    """Scan in address order, with bounded parallel probes and early stopping.

    Results are committed in address order, so a faster later ping cannot displace
    an earlier candidate. Running probes are allowed to finish on cancellation.
    """
    iterator = enumerate(addresses(options))
    gateway_is_host = options.gateway is not None and (
        int(options.network.network_address) < int(options.gateway) < int(options.network.broadcast_address)
    )
    total = options.network.num_addresses - 2 - (1 if gateway_is_host else 0)
    checked = replied = unknown = 0
    candidates: list[IPv4Address] = []
    next_commit = 0
    completed: dict[int, tuple[IPv4Address, ProbeResult]] = {}
    pending = {}
    limit = min(256, max(32, options.count * 4))
    executor = ThreadPoolExecutor(max_workers=256, thread_name_prefix="icmp")

    def submit_until_full() -> None:
        while len(pending) + len(completed) < limit and not cancelled.is_set():
            try:
                index, address = next(iterator)
            except StopIteration:
                break
            future = executor.submit(_check, address, options, prober)
            pending[future] = (index, address)

    try:
        submit_until_full()
        while pending and not cancelled.is_set() and len(candidates) < options.count:
            done, _ = wait(pending, timeout=0.1, return_when=FIRST_COMPLETED)
            for future in done:
                index, address = pending.pop(future)
                try:
                    result = future.result()
                except Exception:
                    result = ProbeResult.UNKNOWN
                completed[index] = (address, result)
            while next_commit in completed and not cancelled.is_set() and len(candidates) < options.count:
                address, result = completed.pop(next_commit)
                next_commit += 1
                checked += 1
                if result is ProbeResult.REPLIED:
                    replied += 1
                elif result is ProbeResult.TIMED_OUT:
                    candidates.append(address)
                else:
                    unknown += 1
                progress = ScanProgress(checked, replied, unknown, tuple(candidates), total)
                report(progress)
            if len(candidates) >= options.count:
                break
            if checked >= 512 and not candidates:
                limit = 256
            submit_until_full()
    finally:
        executor.shutdown(wait=True, cancel_futures=True)
    return ScanProgress(checked, replied, unknown, tuple(candidates), total)
