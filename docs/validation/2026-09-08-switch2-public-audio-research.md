# Switch 2 Pro Bluetooth audio: public-source research, 2026-09-08

## Outcome and scope

This bounded public-source pass found a real decrypted console/controller capture and a useful console-facing emulator logger foundation, but **did not find a verified physical Switch 2 Pro Bluetooth headphone playback implementation or a downloadable successful headphone stream**. This is a finding about the inspected material, not proof that no such work exists.

The work was read-only apart from this report: GitHub/web source inspection and in-memory parsing of a public capture archive. No controller, app, task-scheduler, Bluetooth, firmware, or installed-program state was changed. No capture file was written locally. Pairing secrets and microphone/voice payloads are deliberately excluded from this report.

## BlueRetro: genuine decrypted capture, older firmware

Source: [BlueRetro issue 1249, corrected decrypted archive](https://github.com/darthcloud/BlueRetro/issues/1249#issuecomment-2981898794), posted June 17, 2025. The linked [ZIP attachment](https://github.com/user-attachments/files/20784479/sw2_ota_traces_pro2_gc_decrypted_fixup.zip) contains decrypted Pro and GameCube PCAP files and their original PCAPNG files. The author describes correcting the Nordic header's encryption flag after decryption.

The decrypted PCAP entries were parsed in memory using the little-endian PCAP record format and Nordic v3 framing checked against the [Wireshark Nordic BLE dissector](https://github.com/wireshark/wireshark/blob/master/epan/dissectors/packet-nordic_ble.c). The analysis admits CRC-valid, encryption-flag-clear data packets and complete L2CAP ATT PDUs. No non-empty clear continuation packets or fragmented clear ATT starts were encountered. Counts are capture records, not retransmission-deduplicated transactions. A redirected in-memory `tshark` invocation was rejected by tool policy before execution; this report therefore does not claim independent Wireshark decoding of these entries.

| Observation | Pro entry | GameCube entry |
| --- | ---: | ---: |
| PCAP records | 8,110 | 11,966 |
| Complete clear ATT PDUs | 4,173 | 4,146 |
| Console rumble writes to `0x0012` | 2,047 | 449 |
| Console command writes to `0x0016` | 20 | 19 |
| Decoded writes to `0x002c` | 0 | 0 |
| Decoded command `0x17` requests | 0 | 0 |
| Decoded command `0x18` requests | One `18/01` query | 0 |

Pro file: `sw2_pro2_reconn_sc2_rumble_crackle.pcap`.

- Frames 3617 and 3961 return firmware information `1001010110780000010104020C000000FFFFFFFF`. Using the documented [firmware-info fields](https://github.com/ndeadly/switch2_controller_research/blob/d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92/commands.md#subcommand-0x01---get-firmware-version-info), this is Pro controller firmware **1.1.4**, Bluetooth patch **12.0.0**, with `FF FF FF FF` rather than an installed DSP version in the final field.
- Frame 3972, 4.639188 seconds from the beginning of the capture, sends `18 91 01 01 00 00 00 00` through `0x0016`, after the Pro report's 33-byte prefix.
- Frame 3977, at 4.649594 seconds, responds on `0x001a` with exactly `18 00 01 01 10 78 00 00`. This is an **eight-byte, non-normal response**, with byte 1 equal to `00` rather than documented response direction `01` and no state body. It fails the current lab's successful 16-byte `18/01` admission contract. It does not demonstrate modern audio-state semantics or a successful audio setup.
- The two acknowledged feature startup requests use mask `0x27` in this historical trace. This does not replace the later pinned console sequence's `0x2f` evidence.
- No decoded `17/02` configuration, `18/03` command, headset-notification subscription, or headphone output stream was found.

### Do not assign today's audio UUID to this capture's numeric handle

The reconnect capture does **not** enumerate the complete characteristic UUID table. Its two Read-By-Type transactions read custom `...d281` and `...d283` values; they are not `0x2803` characteristic discovery. Numeric handles alone cannot establish the headphone UUID.

The author's [contemporary GATT table](https://github.com/darthcloud/BlueRetro/issues/1249#issuecomment-2961209354) instead places Generic Access at `0x002b`, a `0x2803` characteristic declaration at `0x002c`, and Device Name at `0x002d`. That table does **not** identify `0x002c` as headphone output. The later [ndeadly GATT documentation](https://github.com/ndeadly/switch2_controller_research/blob/d1c5a7f7ba298f83017fae84952a4e6d2ef8fc92/bluetooth_interface.md#gatt-attributes) explicitly notes that updated Pro firmware adds headset attributes, including the headphone characteristic ending `b06`.

The old firmware response and contemporary table support the inference that this trace predates the modern headset-capable layout. They do not prove the complete live GATT table from this reconnect PCAP itself.

### Coverage limitation

The Pro capture retains encrypted non-empty packets: an earlier connection has 1,748 peripheral-to-central length-74 packets; the inspected reconnect has nine central-to-peripheral length-53 packets and four peripheral-to-central length-74 packets. These were not treated as plaintext or classified by guessed payload content. Therefore, the zero counts above mean **no such traffic among decoded complete ATT records**, not an assertion that every byte of every connection was decoded. The file is valuable for historical initialization/rumble and the non-normal `18/01` response, but not a successful headphone-codec donor.

## Console-facing logger foundation, not a working audio donor

[espp PR 765](https://github.com/esp-cpp/espp/pull/765), inspected at head `65c28f9955dfdf5ce946cf1b98e4237b9d87f427`, implements Switch 2 Pro BLE emulation. The author reports console pairing, input, and reconnection on ESP32-C6/S3. Its headphone characteristic ending `b06` is registered and writes can reach a passive hexadecimal logger; it does not supply headphone encoding, playback, or validated audio initialization semantics.

This is a possible **separately powered microcontroller plus real-console** capture foundation. It is not a PC-only solution, a passive sniffer, a captured console audio stream, or evidence that a physical Pro controller accepts any candidate codec. The related [ESP32-BLE5-NSController-Emulator](https://github.com/zhantss/ESP32-BLE5-NSController-Emulator/tree/0ea0c62aeab2440c7f9c77f29cf87ffa3b4c192f) advertises a headphone characteristic but its checked write dispatch does not implement that endpoint. Neither finding authorizes hardware changes or establishes that the console would start audio without further protocol work.

## Other leads checked

- [ndeadly research](https://github.com/ndeadly/switch2_controller_research): inspected issues, PRs, discussions, comments, forks, and relevant commit changes. A final freshness check found two new `hid_reports.md` commits: [0x07 NFC-byte removal](https://github.com/ndeadly/switch2_controller_research/commit/17625508e1aea6ec0955913fafdb8b8e342b86cd) and [0x05 motion types/ranges](https://github.com/ndeadly/switch2_controller_research/commit/a3306b473acff0d6844fb1e288883a3940df0baf). Those diffs contain no audio setup or new capture. Earlier checked motion/reconnection captures remain distinct from headphone playback evidence.
- [switch2mac audio investigation](https://github.com/Peterksharma/switch2mac/blob/ea6719f0a1d6b6986c00aca9ed4169a85c8cc9ae/research/audio-investigation.md) and [capture analysis](https://github.com/Peterksharma/switch2mac/blob/ea6719f0a1d6b6986c00aca9ed4169a85c8cc9ae/research/capture-format-analysis.md): explicitly incomplete playback research; the analyzed 112-byte controller-to-Mac notifications are not console-to-controller headphone packets. Inspected PR/fork changes did not provide a successful headphone capture.
- [switch2bridge-macos issue 14](https://github.com/mlstr0m/switch2bridge-macos/issues/14) and its [protocol-tooling fork](https://github.com/kennethreitz/switch2bridge-macos/tree/real-gamepad-output): useful button, calibration, USB, and motion evidence. The discussion distinguishes the `...f9` headset input characteristic from other report layouts; no successful console-origin headphone stream was supplied in the inspected discussion/tree.
- [german77/JoyconDriver](https://github.com/german77/JoyconDriver/tree/6238c941078b224df7130dc0ba78a4d09a7cac68): useful dissector fields, input audio-status marker, and DSP-region identification; no checked headphone packet codec/capture implementation.
- [pexserver ProCon2Audio.js](https://github.com/pexserver/pexserver.github.io/blob/e9a629746f01ac51e5deb29abf9bdd92bdd7d1fc/tool/File/Switch/ProCon2Audio.js): an apparent audio lead that is browser Web Audio playback/analysis. `setHeadphoneOutput` only stores a flag and logs; `Music.js` only delegates to it. The checked core uses WebUSB/WebHID, and its plan lists Bluetooth as future work. It is not controller-jack playback.
- [Switch2ProWirelessViiper README](https://github.com/ZX8592/Switch2ProWirelessViiper/blob/0b7c34cb093ef94fe8bf35625ce6485d4e23c747/README.md): explicitly excludes headphone, speaker, and microphone audio. [Switch2Connect](https://github.com/TommyWabg/Switch2Connect#known-limitations) likewise explicitly excludes wireless Pro controller audio.

## What would materially change the implementation evidence

A later-firmware, real-console capture that includes actual UUID-to-handle discovery, the headset insertion/state transition, command setup and replies, and a non-silent console-to-headphone stream would answer questions this pass cannot. A verified working physical-controller implementation with the same provenance would also qualify. Another accepted host write, a motion notification, a USB-audio implementation, or a named but unimplemented audio endpoint would not by itself establish Bluetooth headphone playback.
