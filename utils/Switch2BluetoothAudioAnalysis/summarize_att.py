"""Offline pcapng inventory. Never opens a radio or prints keys/payloads/addresses."""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path

# Import registers the Bluetooth RF link type before reading the capture.
from scapy.layers.bluetooth4LE import BTLE_DATA, BTLE_RF  # noqa: F401
from scapy.layers.bluetooth import ATT_Hdr
from scapy.utils import PcapNgReader

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("captures", nargs="+", type=Path)
args = parser.parse_args()

for path in args.captures:
    if path.suffix.lower() != ".pcapng" or path.stat().st_size > 32 * 1024 * 1024:
        raise ValueError("Select an explicit pcapng file no larger than 32 MiB")
    counts = Counter()
    packets = continuations = 0
    with PcapNgReader(str(path)) as reader:
        for packet in reader:
            packets += 1
            if BTLE_DATA in packet and packet[BTLE_DATA].LLID == 1:
                # Empty link-layer acknowledgements also use LLID=1.
                if packet[BTLE_DATA].len:
                    continuations += 1
            if ATT_Hdr not in packet:
                continue
            att = packet[ATT_Hdr]
            handle = getattr(att.payload, "gatt_handle", None)
            if handle is not None:
                counts[(int(att.opcode), int(handle))] += 1
    with path.open("rb") as capture:
        digest = hashlib.file_digest(capture, "sha256").hexdigest()
    print(json.dumps({
        "file": path.name, "sha256": digest, "packets": packets,
        "nonempty_ll_continuations": continuations,
        "decoded_handle_operations": [
            {"opcode": f"0x{opcode:02x}", "handle": f"0x{handle:04x}", "count": count}
            for (opcode, handle), count in sorted(counts.items())
        ],
        "scope": "Decoded ATT only; no decryption or fragment reassembly. Absence is not proof of unsupported hardware.",
    }))
