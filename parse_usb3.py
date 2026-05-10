import struct

data = open(r'D:\Projs\StellarForceAdapt\USBPcap捕获内容.pcapng', 'rb').read()
print(f'File size: {len(data)} bytes')

# Look at all blocks by type
block_types = {}
pos = 0
while pos < len(data) - 8:
    blk_type = struct.unpack_from('<I', data, pos)[0]
    blk_len = struct.unpack_from('<I', data, pos+4)[0]
    if blk_len < 8 or blk_len > 10000000:
        break
    if blk_type not in block_types:
        block_types[blk_type] = 0
    block_types[blk_type] += 1
    pos += blk_len

print(f'Total bytes consumed: {pos}')
print(f'Block types:')
type_names = {
    0x0A0D0D0A: 'SHB',
    0x00000001: 'IDB',
    0x00000002: 'PB',
    0x00000003: 'EPB',
    0x00000004: 'SPB',
    0x00000005: 'NRB',
    0x00000006: 'ISB',
    0x00000007: 'DCB',
}
for t, count in sorted(block_types.items()):
    name = type_names.get(t, f'UNKNOWN_0x{t:08x}')
    print(f'  0x{t:08x} ({name}): {count}')
