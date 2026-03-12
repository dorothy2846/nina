#!/bin/bash
set -e
echo "=== Setting up WiFi AP for Beyond Stellar ==="

# Install dependencies
apt-get update
apt-get install -y hostapd dnsmasq

# Stop services during configuration
systemctl stop hostapd 2>/dev/null || true
systemctl stop dnsmasq 2>/dev/null || true

# Configure static IP for wlan0
cat > /etc/netplan/99-beyondstellar-ap.yaml << 'NETPLAN'
network:
  version: 2
  wifis:
    wlan0:
      dhcp4: false
      addresses:
        - 192.168.4.1/24
NETPLAN
netplan apply

# Disable WiFi power management
iw dev wlan0 set power_save off 2>/dev/null || true

# Install configs
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cp "$SCRIPT_DIR/hostapd.conf" /etc/hostapd/hostapd.conf
cp "$SCRIPT_DIR/dnsmasq-ap.conf" /etc/dnsmasq.d/beyondstellar.conf

# Unmask and enable hostapd
systemctl unmask hostapd
systemctl enable hostapd
systemctl enable dnsmasq

# Install watchdog
cp "$SCRIPT_DIR/wifi-watchdog.sh" /opt/nina-headless/wifi-watchdog.sh
chmod +x /opt/nina-headless/wifi-watchdog.sh
cp "$SCRIPT_DIR/wifi-watchdog.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable wifi-watchdog

# Start everything
systemctl start hostapd
systemctl start dnsmasq
systemctl start wifi-watchdog

echo ""
echo "=== WiFi AP Setup Complete ==="
echo "SSID: BeyondStellar"
echo "Password: BeyondStellar!"
echo "Server IP: 192.168.4.1"
echo "DHCP Range: 192.168.4.2 - 192.168.4.20"
echo ""
echo "Connect your iPhone to 'BeyondStellar' WiFi"
echo "Then open Beyond Stellar app → connect to 192.168.4.1:1888"
