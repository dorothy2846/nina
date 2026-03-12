#!/bin/bash
set -e

INSTALL_DIR=/opt/hips-tileserver
CACHE_DIR=/var/cache/hips-tileserver
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

echo "=== HiPS Tile Server Installation ==="

# Build and publish
echo "[1/5] Publishing HiPS.TileServer..."
dotnet publish "$PROJECT_DIR/HiPS.TileServer.csproj" -c Release -o "$INSTALL_DIR"

# Create cache directory
echo "[2/5] Creating cache directory..."
mkdir -p "$CACHE_DIR"
chown nina:nina "$CACHE_DIR"

# Install systemd service
echo "[3/5] Installing systemd service..."
cp "$SCRIPT_DIR/hips-tileserver.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable hips-tileserver

# Install Avahi mDNS service
echo "[4/5] Installing Avahi mDNS service..."
if [ -d /etc/avahi/services ]; then
    cp "$SCRIPT_DIR/hips-tileserver.avahi" /etc/avahi/services/hips-tileserver.service
    systemctl restart avahi-daemon 2>/dev/null || true
else
    echo "  Avahi not found, skipping mDNS setup"
fi

# Start service
echo "[5/5] Starting hips-tileserver service..."
systemctl start hips-tileserver

echo ""
echo "=== Installation complete ==="
echo "Service: systemctl status hips-tileserver"
echo "Health:  curl http://localhost:8080/api/v1/health"
echo "Cache:   $CACHE_DIR"
