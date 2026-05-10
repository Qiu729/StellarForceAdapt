import struct

filepath = "D:/Projs/StellarForceAdapt/USBPcap捕获内容.pcapng"
data = open(filepath, 'rb').read()

pos = 0
frame_count = 0
while pos < len(data):
    blk_type = struct.unpack_from('<I', data, pos)[0]
    blk_len = struct.unpack_from('<I', data, pos+4)[0]
    if blk_len < 12 or blk_len > 100000:
        break

    if blk_type == 0x00000003:  # Enhanced Packet Block
        frame_count += 1
        cap_len = struct.unpack_from('<I', data, pos+20)[0]
        pkt_data_start = pos + 28
        pkt_data = data[pkt_data_start:pkt_data_start+cap_len]

        if cap_len >= 28:
            urb_func = struct.unpack_from('<I', pkt_data, 8)[0]
            irp_info = pkt_data[16]
            bus_id = pkt_data[17]
            dev_addr = pkt_data[18]
            endpoint = pkt_data[19]
            pkt_len = struct.unpack_from('<I', pkt_data, 20)[0]
            direction = "IN" if (endpoint & 0x80) else "OUT"

            # Show all non-zero URB functions
            if urb_func != 0 or pkt_len > 0:
                print(f"  Frame {frame_count}: addr={dev_addr} ep=0x{endpoint:02x} {direction} func=0x{urb_func:04x} len={pkt_len} status=0x{struct.unpack_from('<I', pkt_data, 4)[0]:08x}")

    elif blk_type == 0x00000001:  # Interface Description
        link_type = struct.unpack_from('<H', data, pos+12)[0]
        print(f"Interface: link_type={link_type}")

    pos += blk_len

print(f"\nTotal frames: {frame_count}")
