#!/usr/bin/env python3
"""
FlyDigi ForceAdapt A5 data[21] 1-byte checksum verification and builder.

Discovery: A5[21] = (SUM(data[0..20]) + 0xBC) & 0xFF
This is a simple sum-modulo-256 with a fixed offset of 0xBC (188 decimal).

Verification: 27+ unique A5 packets from V1 and V2 USBPcap captures all pass.
"""

import sys

SAMPLES = [
    # === Side A (LT, A5 byte[2]=0x32) ===
    ("LT Mode0", "00 00 32 00 00 00 00 00 0A 32 01 01 01 80 00 00 00 00 00 00 00 AD 00"),
    ("LT Mode1", "00 00 32 00 00 00 01 00 0A 0A 64 01 FF 46 00 00 0A 1E 00 00 00 D5 00"),
    ("LT Mode2", "00 00 32 00 00 00 02 00 0A 0A 64 01 FF 46 00 00 1E 32 32 11 01 42 00"),
    ("LT Mode3", "00 00 32 00 00 00 03 00 0A 0A 64 01 FF 46 00 00 32 3C 3C 00 01 5A 00"),
    ("LT Mode4", "00 00 32 00 00 00 04 00 0A 0A 64 01 FF 46 00 00 50 FF 01 00 00 00 00"),
    ("LT Mode5", "00 00 32 00 00 00 05 02 0A 32 01 01 01 80 00 00 01 80 01 5A 00 90 00"),
    # LT variants (v2 capture)
    ("LT Mode0 v2",    "00 00 32 00 00 00 00 00 0A C8 01 01 01 FF 00 00 00 FF 00 00 00 C1 00"),
    ("LT Mode2 v2",    "00 00 32 00 00 00 02 00 0A 32 01 01 01 80 00 00 1E 32 32 11 01 43 00"),
    ("LT Mode2 p0=c0", "00 00 32 00 00 00 02 00 0A 32 01 01 01 80 00 00 C0 32 32 11 01 E5 00"),
    ("LT Mode2 p3=ff", "00 00 32 00 00 00 02 00 0A 32 01 01 01 80 00 00 C0 32 32 FF 01 D3 00"),
    ("LT Mode2 v2p19", "00 00 32 00 00 00 02 00 0A C8 01 01 01 FF 00 00 C0 32 32 FF 01 E8 00"),
    ("LT Mode5 v2p17", "00 00 32 00 00 00 05 02 0A C8 01 01 01 80 00 00 01 80 01 5A 00 26 00"),
    ("LT Mode5 v2p18", "00 00 32 00 00 00 05 02 0A C8 01 01 01 FF 00 00 01 FF 01 5A 00 24 00"),
    # LT variants (v1 capture, Mode2 parameter sweeps)
    ("LT Mode2 p0=45", "00 00 32 00 00 00 02 00 0A 0A 64 01 FF 46 00 00 45 32 32 11 01 69 00"),
    ("LT Mode2 p0=92", "00 00 32 00 00 00 02 00 0A 0A 64 01 FF 46 00 00 92 32 32 11 01 B6 00"),
    ("LT Mode2 p1=5b", "00 00 32 00 00 00 02 00 0A 0A 64 01 FF 46 00 00 92 5B 32 11 01 DF 00"),
    ("LT Mode2 p2=b5","00 00 32 00 00 00 02 00 0A 0A 64 01 FF 46 00 00 92 B5 32 11 01 39 00"),
    ("LT Mode2 p3=9c","00 00 32 00 00 00 02 00 0A 0A 64 01 FF 46 00 00 92 B5 9C 11 01 A3 00"),
    ("LT Mode2 p3=85","00 00 32 00 00 00 02 00 0A 0A 64 01 FF 46 00 00 92 B5 9C 85 01 17 00"),
    ("LT Mode2 p3=4d","00 00 32 00 00 00 02 00 0A 0A 64 01 FF 46 00 00 92 B5 9C 4D 01 DF 00"),
    ("LT Mode2 p0=28","00 00 32 00 00 00 02 00 0A 0A 64 01 FF 46 00 00 28 B5 9C 4D 01 75 00"),
    ("LT Mode2 p0=28b","00 00 32 00 00 00 02 00 0A 0A 64 01 FF 46 00 00 28 B5 9C 21 01 49 00"),

    # === Side B (RT, A5 byte[2]=0x00) ===
    ("RT Mode0", "00 00 00 00 00 00 00 00 0A 32 01 01 01 80 00 00 00 00 00 00 00 7B 00"),
    ("RT Mode1", "00 00 00 00 00 00 01 00 0A 0A 64 01 FF 46 00 00 0A 1E 00 00 00 A3 00"),
    ("RT Mode2", "00 00 00 00 00 00 02 00 0A 0A 64 01 FF 46 00 00 1E 32 32 11 01 10 00"),
    ("RT Mode3", "00 00 00 00 00 00 03 00 0A 0A 64 01 FF 46 00 00 32 3C 3C 00 01 28 00"),
    ("RT Mode4", "00 00 00 00 00 00 04 00 0A 0A 64 01 FF 46 00 00 50 FF 01 00 00 CE 00"),
    ("RT Mode5", "00 00 00 00 00 00 05 02 0A 32 01 01 01 80 00 00 01 80 01 5A 00 5E 00"),
    # RT variants
    ("RT Mode0 v2",    "00 00 00 00 00 00 00 00 0A 32 01 01 01 80 00 00 00 FF 00 00 00 7A 00"),
    ("RT Mode1 alt",   "00 00 00 00 00 00 01 00 0A 32 01 01 01 80 00 00 0A 1E 00 00 00 A4 00"),
    ("RT Mode4 v2p21", "00 00 32 00 00 00 04 00 0A C8 01 01 01 FF 00 00 50 FF 01 00 00 16 00"),
]


def compute_checksum(data: bytes) -> int:
    """Compute A5 data[21] checksum: sum of bytes 0..20, plus 0xBC, mod 256."""
    return (sum(data[:21]) + 0xBC) & 0xFF


def verify_all() -> bool:
    ok = True
    for name, hex_str in SAMPLES:
        data = bytes.fromhex(hex_str)
        cs = compute_checksum(data)
        if cs != data[21]:
            print(f"FAIL {name}: computed=0x{cs:02X} expected=0x{data[21]:02X}")
            ok = False
        else:
            print(f"PASS {name}")
    print(f"\n{len(SAMPLES)} samples: {'ALL PASS' if ok else 'SOME FAILED'}")
    return ok


def build_payload(hex_bytes: list[str]) -> str:
    """Build full 23-byte A5 payload from 21 hex byte strings."""
    data21 = bytes.fromhex("".join(hex_bytes))
    if len(data21) != 21:
        print(f"Error: expected 21 bytes, got {len(data21)}", file=sys.stderr)
        sys.exit(1)
    cs = compute_checksum(data21 + b"\x00\x00")
    data23 = data21 + bytes([cs, 0x00])
    return data23.hex(" ")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--build":
        result = build_payload(sys.argv[2:])
        print(result)
    else:
        sys.exit(0 if verify_all() else 1)
