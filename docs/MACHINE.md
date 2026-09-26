# Target machine survey

26 September 2026, fresh install, read-only survey. Facts that shape the setup scripts.

## Windows

| | |
|---|---|
| Edition | Windows 11 IoT Enterprise LTSC 2024 (`IoTEnterpriseS`; was `EnterpriseS` until the IoT key went in) |
| Build | 24H2, 26100.9457 after the September 2026 security update (installed from 26100.1742) |
| Activation | Activated (the first key, a MAK for plain Enterprise LTSC, failed with 0xC004F034) |
| VBS / memory integrity | Running (hypervisor present), which is why Hybrid Sleep is unavailable |

## Power

| | |
|---|---|
| Sleep states | **S3 available**; S0 Low Power Idle (Modern Standby) not supported; Hibernate available; Fast Startup on |
| Timeouts at install | Sleep 15 min, screen off 5 min (AC) |
| Wake-capable devices | Ethernet, Wi-Fi, USB keyboard, USB mouse, one USB HID device. Armed: Ethernet, keyboard, mouse |
| Firmware | AMI BIOS, 17 Sept 2024 (generic "Default string" board) |

The spec's top risk (Modern Standby only) does not apply to this box: it has classic S3.

## Graphics and decoding

| | |
|---|---|
| GPU | Intel UHD Graphics, Alder Lake-N (8086:46D1) |
| Driver | 32.0.101.7088, June 2026, from Windows Update, with Intel Graphics Software |
| Hardware decoders (DXVA) | H.264, HEVC Main and Main10, VP9 Profile 0 and Profile 2 (10-bit), AV1 Profile 0 |
| Output | HDMI, 3840x2160 at 60 Hz; the TV reports HDR10 |
| Media extensions | None installed (no HEVC, AV1 or VP9 Video Extensions) |

No newer Intel driver is needed for decoding; what is missing is the HEVC extension that Edge uses.

## Browser

| | |
|---|---|
| Edge | 122.0.2365.106 on the ISO; Edge Update brought it to 154.0.4258.37 the same day |
| WebView2 Runtime | 153.0.4234.48, machine-wide |
| Edge policies | None |

## USB

| Device | IDs | Notes |
|---|---|---|
| 8BitDo 2.4 GHz dongle | 2DC8:301C | Seen as a vendor-defined HID device only, **not XInput** (controller off or wrong mode) |
| Dell keyboard | 413C:2107 | Wake-armed |
| 2.4 GHz mouse | 0000:0538 | Wake-armed |
| USB audio + HID composite | 0573:1573 | Unidentified |
| Realtek Bluetooth | 0BDA:C821 | |

## Network and TV

| | |
|---|---|
| Box | Ethernet, 1 Gbps (Realtek). Wi-Fi (Realtek 8821CE) unused. Network profile: Public |
| Box's TV (per the user) | Bedroom TV: TCL 43S425-CA Roku TV, Roku OS 15.3.4, on Wi-Fi |
| Other Roku on the network | Living room TV: TCL 65S41-CA. Not to be used for tests |
| EDID on the box's HDMI | Maker TCL, name `65S41CA`, product code 0000, 2022 |
| Discovery | SSDP `ST: roku:ecp` finds both TVs, though the bedroom TV missed the first search; ECP on port 8060 |
| Power features | `supports-warm-standby` and `supports-wake-on-wlan` true on both |
| ECP mode | **limited** on both: `query/device-info` and `query/active-app` answer, `query/apps` is refused ("ECP command not allowed in Limited mode") |

The EDID names the living room model while the user says the box is on the bedroom TV, so
either TCL reuses EDID names across models or the cabling differs from what we think. Either way,
matching a TV profile by EDID alone (SPEC N7) may not tell two TCL Roku TVs apart; the TV step in
first-run setup should confirm the match, for example by checking which TV reports the box's input as active.
