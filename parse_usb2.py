import struct

data = open(r'D:\Projs\StellarForceAdapt\USBPcap捕获内容.pcapng', 'rb').read()
print(f'File size: {len(data)} bytes')

pos = 0
epb_count = 0
spb_count = 0
idb_count = 0
devices = {}

while pos < len(data) - 8:
    blk_type = struct.unpack_from('<I', data, pos)[0]
    blk_len = struct.unpack_from('<I', data, pos+4)[0]
    if blk_len < 8 or blk_len > 1000000:
        break

    if blk_type == 0x00000001:
        idb_count += 1
        link_type = struct.unpack_from('<H', data, pos+12)[0]
        print(f'IDB #{idb_count}: link_type={link_type}')
    elif blk_type == 0x00000003:
        epb_count += 1
        if epb_count < 5:
            cap_len = struct.unpack_from('<I', data, pos+20)[0]
            pkt_start = pos + 28
            if cap_len >= 28:
                pkt = data[pkt_start:pkt_start+cap_len]
                urb_func = struct.unpack_from('<I', pkt, 8)[0]
                dev_addr = pkt[18]
                endpoint = pkt[19] & 0x8F
                pkt_len = struct.unpack_from('<I', pkt, 20)[0]
                direction = 'IN' if (pkt[19] & 0x80) else 'OUT'
                print(f'  EPB: addr={dev_addr} ep=0x{endpoint:02x} {direction} func=0x{urb_func:04x} len={pkt_len}')
        # Count all packets
        cap_len = struct.unpack_from('<I', data, pos+20)[0]
        pkt_start = pos + 28
        if cap_len >= 28:
            pkt = data[pkt_start:pkt_start+cap_len]
            dev_addr = pkt[18]
            endpoint = pkt[19] & 0x8F
            if dev_addr not in devices:
                devices[dev_addr] = {'total': 0, 'interrupt_out': 0, 'interrupt_in': 0, 'control': 0}
            devices[dev_addr]['total'] += 1
            urb_func = struct.unpack_from('<I', pkt, 8)[0]
            if urb_func == 9 and (pkt[19] & 0x80):
                devices[dev_addr]['interrupt_in'] += 1
            elif urb_func == 9 and not (pkt[19] & 0x80):
                devices[dev_addr]['interrupt_out'] += 1
            elif urb_func == 8:
                devices[dev_addr]['control'] += 1
    elif blk_type == 0x00000009:
        spb_count += 1
    pos += blk_len

print(f'\nTotal: {epb_count} packets, {spb_count} SPBs, {idb_count} IDBs')
for addr, info in sorted(devices.items()):
    print(f'  Device addr={addr}: total={info["total"]} (ctrl={info["control"]}, int_in={info["interrupt_in"]}, int_out={info["interrupt_out"]})')
