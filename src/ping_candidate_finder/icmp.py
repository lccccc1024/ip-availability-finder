"""Windows ICMP echo through the built-in IP Helper API (no admin rights)."""
from __future__ import annotations

import ctypes
from ctypes import wintypes
from ipaddress import IPv4Address
import socket

from .scan import ProbeResult


IP_SUCCESS = 0
IP_REQ_TIMED_OUT = 11010
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value


class _IpOptions(ctypes.Structure):
    _fields_ = [
        ("ttl", ctypes.c_ubyte),
        ("tos", ctypes.c_ubyte),
        ("flags", ctypes.c_ubyte),
        ("options_size", ctypes.c_ubyte),
        ("options_data", ctypes.c_void_p),
    ]


class _EchoReply(ctypes.Structure):
    _fields_ = [
        ("address", wintypes.ULONG),
        ("status", wintypes.ULONG),
        ("round_trip_time", wintypes.ULONG),
        ("data_size", wintypes.USHORT),
        ("reserved", wintypes.USHORT),
        ("data", ctypes.c_void_p),
        ("options", _IpOptions),
    ]


class WindowsIcmpProber:
    def __init__(self) -> None:
        if not hasattr(ctypes, "WinDLL"):
            raise OSError("此程序仅支持 Windows。")
        api = ctypes.WinDLL("iphlpapi.dll", use_last_error=True)
        api.IcmpCreateFile.argtypes = []
        api.IcmpCreateFile.restype = wintypes.HANDLE
        api.IcmpCloseHandle.argtypes = [wintypes.HANDLE]
        api.IcmpCloseHandle.restype = wintypes.BOOL
        api.IcmpSendEcho.argtypes = [
            wintypes.HANDLE,
            wintypes.ULONG,
            ctypes.c_void_p,
            wintypes.WORD,
            ctypes.c_void_p,
            ctypes.c_void_p,
            wintypes.DWORD,
            wintypes.DWORD,
        ]
        api.IcmpSendEcho.restype = wintypes.DWORD
        self._api = api

    def probe(self, address: IPv4Address, timeout_ms: int) -> ProbeResult:
        handle = self._api.IcmpCreateFile()
        if not handle or handle == INVALID_HANDLE_VALUE:
            return ProbeResult.UNKNOWN
        payload = ctypes.create_string_buffer(b"ping-candidate")
        reply = ctypes.create_string_buffer(ctypes.sizeof(_EchoReply) + len(payload) + 8)
        destination = int.from_bytes(socket.inet_aton(str(address)), "little")
        try:
            count = self._api.IcmpSendEcho(
                handle,
                destination,
                payload,
                len(payload) - 1,
                None,
                reply,
                len(reply),
                timeout_ms,
            )
            if count:
                status = ctypes.cast(reply, ctypes.POINTER(_EchoReply)).contents.status
            else:
                status = ctypes.get_last_error()
            if status == IP_SUCCESS:
                return ProbeResult.REPLIED
            if status == IP_REQ_TIMED_OUT:
                return ProbeResult.TIMED_OUT
            return ProbeResult.UNKNOWN
        finally:
            self._api.IcmpCloseHandle(handle)
