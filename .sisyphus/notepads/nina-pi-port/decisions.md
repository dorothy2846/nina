# NINA-Pi Architecture Decisions

## [2026-03-11] Architecture Decisions
- Fork strategy: Independent fork (not upstream PR)
- Target OS: Ubuntu Server 24.04 ARM64
- .NET version: 9.0 (not 10.0-windows)
- API port: 1888 (Touch'N'Stars compatible)
- Equipment bridge: INDI → indi_alpaca_server → Alpaca → NINA
- PHD2: NINA auto-spawns as subprocess, user-transparent
- WiFi: Hotspot mode (SSID: NINA-Pi)
- mDNS: nina-pi.local via avahi-daemon
