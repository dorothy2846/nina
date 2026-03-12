# NINA Headless Deployment

## Requirements
- Ubuntu Server 22.04+ or Debian 12+
- .NET 9.0 Runtime: `apt install dotnet-runtime-9.0`
- Avahi (optional, for mDNS): `apt install avahi-daemon`

## Quick Install

1. Build and publish:
   ```bash
   dotnet publish NINA.Headless/NINA.Headless.csproj -c Release -r linux-x64 --self-contained false -o publish/
   ```

2. Copy to target machine and run:
   ```bash
   sudo bash deploy/install.sh
   ```

3. Verify:
   ```bash
   curl http://localhost:1888/api/v1/health
   # → {"status":"ok","version":"1.0.0","platform":"linux-x64"}
   ```

## Discovery
- Local IP: `http://{ip}:1888`
- mDNS: `http://{hostname}.local:1888` (requires avahi-daemon)

## Logs
```bash
journalctl -u nina-headless -f
```

## Update
```bash
sudo systemctl stop nina-headless
sudo cp -r publish/. /opt/nina-headless/
sudo systemctl start nina-headless
```
