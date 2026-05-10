"""Debug: dump first few pcapng blocks raw."""
import struct
p = r"D:\Projs\StellarForceAdapt\USBPcap捕获内容.pcapng"
d = open(p, 'rb').read()
print(f"size={len(d)}")
print(f"first 32 bytes: {d[:32].hex()}")

pos = 0
cnt = 0
types_seen = {}
while pos < len(d) - 8 and cnt < 2000000:
    blk_type = struct.unpack_from('<I', d, pos)[0]
    blk_len = struct.unpack_from('<I', d, pos + 4)[0]
    if blk_len < 8 or blk_len > 10_000_000:
        print(f"[stop] pos={pos} blk_type=0x{blk_type:08x} blk_len={blk_len}")
        break
    types_seen[blk_type] = types_seen.get(blk_type, 0) + 1
    if cnt < 6:
        print(f"pos={pos} type=0x{blk_type:08x} len={blk_len} first16={d[pos:pos+16].hex()}")
    pos += blk_len
    cnt += 1

print(f"total blocks: {cnt}, pos={pos}")
print(f"types: { {hex(k): v for k,v in types_seen.items()} }")

# Sample the 'ISB-like' block at the first occurrence of 0x00000006
print()
pos = 0
found = False
while pos < len(d) - 8 and not found:
    t = struct.unpack_from('<I', d, pos)[0]
    l = struct.unpack_from('<I', d, pos + 4)[0]
    if t == 0x00000006:
        print(f"Sample type=0x06 block at pos={pos}, len={l}:")
        print(f"  header 28 bytes: {d[pos:pos+28].hex()}")
        print(f"  payload (first 80): {d[pos+12:pos+12+80].hex()}")
        found = True
    if l < 8 or l > 10_000_000:
        break
    pos += l
