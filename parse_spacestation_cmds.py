"""
Parse USBPcap pcapng and extract only the real host-originated SpaceStation commands:
  URB OUT + HID Report ID 0x03 + magic 5A A5.

Output: time-ordered command list with cmd/sub decoded using the SDL V2 protocol
skeleton (SDL_hidapi_flydigi.c FLYDIGI_V2_* constants).

Usage:
    python parse_spacestation_cmds.py [path\to\capture.pcapng]

Block formats reference:
- pcapng EPB:  [type(4), total_len(4), iface_id(4), ts_high(4), ts_low(4),
                cap_len(4), orig_len(4), packet_data, opts..., total_len(4)]
- USBPcap pseudo-header (min 27 bytes):
    offset  size  field
    0       2     headerLen
    2       8     irpId
    10      4     status
    14      2     function
    16      1     info
    17      2     bus
    19      2     device
    21      1     endpoint  (bit7 = IN when set)
    22      1     transfer
    23      4     dataLength
  Real HID payload starts at offset = headerLen.
"""

import struct
import sys
from pathlib import Path

DEFAULT_FILE = r"D:\Projs\StellarForceAdapt\USBPcap捕获内容.pcapng"

# V2 protocol command names, mostly from libsdl-org/SDL SDL_hidapi_flydigi.c
CMD_NAMES = {
    0x01: "GET_INFO",
    0x02: "PRIV_0x02",
    0x04: "PRIV_0x04",
    0x10: "GET_STATUS / HEARTBEAT",
    0x11: "SET_STATUS",
    0x12: "HAPTIC",
    0x1C: "ACQUIRE_CTRL",
    0x51: "PRIV_0x51 (SaveProfile?)",
    0xA1: "PRIV_0xA1",
    0xA4: "FORCE_BEGIN_CFG  (data=6B)",
    0xA5: "FORCE_SET_EFFECT (data=23B)",
    0xA6: "FORCE_END_CFG    (data=3B,CRC?)",
}


def decode_cmd(cmd: int) -> str:
    return CMD_NAMES.get(cmd, f"UNKNOWN_0x{cmd:02X}")


def parse(path: Path):
    data = path.read_bytes()
    print(f"[file] {path}  ({len(data):,} bytes)")

    if_tsresol_exp = 6  # microseconds by default (10^-6)
    commands = []  # (frame_idx, ts_ns, payload_bytes)
    pos = 0
    frame_idx = 0
    first_ts_ns = None

    # Scan IDB for if_tsresol option
    while pos < len(data) - 8:
        blk_type = struct.unpack_from('<I', data, pos)[0]
        blk_len = struct.unpack_from('<I', data, pos + 4)[0]
        if blk_len < 12 or blk_len > 10_000_000:
            break

        if blk_type == 0x00000001:  # IDB
            # Options start at pos+16 (type(4)+len(4)+linktype(2)+reserved(2)+snaplen(4))
            opt_pos = pos + 16
            while opt_pos < pos + blk_len - 4:
                opt_code = struct.unpack_from('<H', data, opt_pos)[0]
                opt_len = struct.unpack_from('<H', data, opt_pos + 2)[0]
                if opt_code == 0:  # opt_endofopt
                    break
                if opt_code == 9 and opt_len >= 1:  # if_tsresol
                    raw = data[opt_pos + 4]
                    if raw & 0x80:
                        # 2^-x seconds
                        if_tsresol_exp = -1  # flag as power of two (rare)
                    else:
                        if_tsresol_exp = raw
                opt_pos += 4 + ((opt_len + 3) & ~3)  # pad to 4

        elif blk_type == 0x00000006:  # EPB (Enhanced Packet Block)
            frame_idx += 1
            ts_high = struct.unpack_from('<I', data, pos + 12)[0]
            ts_low = struct.unpack_from('<I', data, pos + 16)[0]
            cap_len = struct.unpack_from('<I', data, pos + 20)[0]
            pkt_start = pos + 28
            pkt = data[pkt_start:pkt_start + cap_len]

            # Convert timestamp to nanoseconds for easy printing
            ts_raw = (ts_high << 32) | ts_low
            if if_tsresol_exp >= 0:
                # ts_raw * 10^-if_tsresol_exp seconds -> nanoseconds
                ts_ns = ts_raw * (10 ** (9 - if_tsresol_exp))
            else:
                ts_ns = ts_raw  # unknown; keep raw

            if cap_len >= 27:
                header_len = struct.unpack_from('<H', pkt, 0)[0]
                if header_len < 24 or header_len > cap_len:
                    pos += blk_len
                    continue
                endpoint = pkt[21]
                is_out = (endpoint & 0x80) == 0
                payload = pkt[header_len:]

                if (is_out and len(payload) >= 5
                        and payload[0] == 0x03
                        and payload[1] == 0x5A and payload[2] == 0xA5):
                    if first_ts_ns is None:
                        first_ts_ns = ts_ns
                    commands.append((frame_idx, ts_ns, bytes(payload)))

        pos += blk_len

    print(f"[scan] total frames: {frame_idx}")
    print(f"[scan] matching URB-OUT Report-03 5AA5 commands: {len(commands)}")
    print()

    if not commands:
        print("No matching commands found.")
        return

    # Group adjacent identical-cmd+sub packets to save space when SpaceStation spams heartbeats
    print(f"{'Frame':>7}  {'T+ms':>10}  {'Cmd':<8} {'Sub':<5}  Name")
    print("-" * 100)
    prev_ts_ns = first_ts_ns
    for frame, ts_ns, payload in commands:
        cmd = payload[3]
        sub = payload[4]
        rel_ms = (ts_ns - first_ts_ns) / 1_000_000
        name = decode_cmd(cmd)
        # Print the exact number of bytes indicated by sub (sub = payload length)
        raw_len = min(sub, len(payload) - 5)
        raw = payload[5:5 + raw_len]
        raw_hex = ' '.join(f'{b:02x}' for b in raw) if raw_len > 0 else '(none)'
        print(f"{frame:>7}  {rel_ms:>10.3f}  0x{cmd:02X}     0x{sub:02X}   {name}")
        print(f"         data[{raw_len}]: {raw_hex}")

    # Summary by cmd
    print()
    print("=== Summary by cmd ===")
    from collections import Counter
    cnt = Counter((c[2][3], c[2][4]) for c in commands)
    for (cmd, sub), n in sorted(cnt.items()):
        print(f"  cmd=0x{cmd:02X} sub=0x{sub:02X}  ({decode_cmd(cmd)}):  {n} occurrence(s)")


def main():
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(DEFAULT_FILE)
    if not path.exists():
        print(f"ERROR: not found: {path}", file=sys.stderr)
        sys.exit(1)
    parse(path)


if __name__ == "__main__":
    main()
