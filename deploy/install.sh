#!/bin/bash
set -e

INSTALL_DIR="/opt/nina-headless"
SERVICE_USER="nina"
PUBLISH_DIR="$(dirname "$0")/../publish"

echo "=== Installing NINA Headless ==="

# Create user
if ! id "$SERVICE_USER" &>/dev/null; then
    useradd --system --no-create-home --shell /bin/false "$SERVICE_USER"
    echo "Created user: $SERVICE_USER"
fi

# Create install directory
mkdir -p "$INSTALL_DIR"
cp -r "$PUBLISH_DIR"/. "$INSTALL_DIR/"
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/NINA.Headless"

# Install systemd service
cp "$(dirname "$0")/nina-headless.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable nina-headless
systemctl start nina-headless

# Install Avahi mDNS
if command -v avahi-daemon &>/dev/null; then
    cp "$(dirname "$0")/nina-headless.avahi" /etc/avahi/services/
    systemctl restart avahi-daemon
    echo "mDNS: beyondstellar.local registered"
else
    echo "Warning: avahi-daemon not found. Install with: apt install avahi-daemon"
fi

echo ""
echo "=== Installation complete ==="
echo "Service status:"
systemctl status nina-headless --no-pager
echo ""
echo "API available at: http://$(hostname -I | awk '{print $1}'):1888/api/v1/health"
echo "mDNS: http://$(hostname).local:1888/api/v1/health"
