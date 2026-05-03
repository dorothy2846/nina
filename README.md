# N.I.N.A. - Nighttime Imaging 'N' Astronomy #
[![Website](https://img.shields.io/badge/website-nighttime--imaging.eu-blue)](https://nighttime-imaging.eu/)
[![Latest Release](https://img.shields.io/badge/download-latest-blue)](https://nighttime-imaging.eu/download/)
[![Discord](https://img.shields.io/discord/436650817295089664)](https://discord.gg/nighttime-imaging)
[![License: MPL 2.0](https://img.shields.io/badge/License-MPL%202.0-brightgreen.svg)](https://www.mozilla.org/en-US/MPL/2.0/)
[![Become a Patron](https://img.shields.io/badge/Patreon-support-orange?logo=patreon)](https://www.patreon.com/stefanberg?fan_landing=true)

## 🥧 NINA-Pi: Headless Raspberry Pi Port

This fork (`nina-pi` branch) contains a headless port of N.I.N.A. that runs
cross-platform on **Linux, macOS, and Windows**.

**Key Features:**
- Headless operation (no GUI) for remote observatory control and local dev
- REST API on port 1888 (Touch'N'Stars compatible)
- Equipment bridge via INDI → indi_alpaca_server → Alpaca protocol (Linux)
- Native ASCOM/Alpaca on Windows, ASCOM Alpaca on macOS
- PHD2 auto-spawning as subprocess for guiding
- mDNS discovery via avahi-daemon (Linux) / Bonjour (macOS)
- WiFi hotspot mode (Linux only, for field deployment)

**Supported Platforms:**
- Linux: Ubuntu 22.04+ / Debian 12+ (x64, ARM64) — full feature set
- macOS: 13+ (Intel, Apple Silicon) — dev/test + native camera SDKs
- Windows: 10/11 (x64) — dev/test + ASCOM drivers

**Build Requirements:** .NET 10.0 SDK

---

This repository contains the source code of the **N.I.N.A. - Nighttime Imaging 'N' Astronomy** imaging software.

---

## 🧭 About

**N.I.N.A.** (Nighttime Imaging 'N' Astronomy) is a modular astrophotography suite designed to simplify and streamline image acquisition.  
Originally created with DSO imaging in mind, the platform now supports a wide range of astrophotography and astronomy workflows via a powerful plugin system.

Whether you're new to astrophotography or a seasoned imager, N.I.N.A. aims to make your sessions easier, faster, and more comfortable.

---

## 🌐 Resources

- 🏠 Project website: [nighttime-imaging.eu](https://nighttime-imaging.eu/)
- 📦 Latest release: [nighttime-imaging.eu/download](https://nighttime-imaging.eu/download/)
- 📖 Documentation: [nina.docs](https://github.com/isbeorn/nina.docs)
- 💬 Community support: [Discord](https://discord.gg/nighttime-imaging)

---

## 🤝 Contributing

Interested in contributing code, reporting bugs, or improving documentation?  
Please check out our [Contributing Guidelines](https://github.com/isbeorn/nina.docs/blob/develop/CONTRIBUTING.md).

We welcome all kinds of contributions — from small fixes to large feature proposals.

---

## ⚖ License

This project is licensed under the **Mozilla Public License 2.0**.  
See the [`LICENSE`](./LICENSE.txt) file for details.
