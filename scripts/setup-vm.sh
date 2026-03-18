#!/bin/bash
# NINA Headless — UTM Ubuntu VM Provisioning
# Run inside the VM after Ubuntu Server install:
#   curl -sSL https://raw.githubusercontent.com/.../setup-vm.sh | sudo bash
#   — or —
#   sudo bash /mnt/shared/nina/scripts/setup-vm.sh
#
# What this installs:
#   - .NET 9 Runtime (not SDK — we cross-compile on Mac)
#   - INDI full driver suite + Web Manager
#   - SSH server (for scp deploy)
#   - Shared folder mount (/mnt/shared)

set -euo pipefail

log() { echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*"; }

NINA_USER="${SUDO_USER:-$USER}"
INSTALL_DIR="/opt/nina-headless"

log "=== NINA Headless VM Setup ==="

# 1. System update
log "Updating system..."
apt-get update -qq
apt-get upgrade -y -qq

# 2. Base dependencies
log "Installing base dependencies..."
apt-get install -y -qq \
    curl wget openssh-server \
    libusb-1.0-0-dev libudev-dev \
    libcfitsio-dev \
    avahi-daemon avahi-utils \
    python3-pip python3-venv \
    udev

systemctl enable ssh
systemctl start ssh

# 3. .NET 9 Runtime
log "Installing .NET 9 Runtime..."
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0 --runtime dotnet --install-dir /usr/local/dotnet
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0 --runtime aspnetcore --install-dir /usr/local/dotnet
ln -sf /usr/local/dotnet/dotnet /usr/local/bin/dotnet
echo 'export DOTNET_ROOT=/usr/local/dotnet' >> /etc/environment

# 4. INDI
log "Installing INDI..."
apt-get install -y -qq software-properties-common
add-apt-repository -y ppa:mutlaqja/ppa
apt-get update -qq
apt-get install -y -qq \
    indi-full \
    indi-asi \
    indi-eqmod \
    libindi-dev

# 5. INDI Web Manager
log "Installing INDI Web Manager..."
pip3 install indiweb --break-system-packages 2>/dev/null || pip3 install indiweb

# 6. Systemd services
log "Creating systemd services..."

cat > /etc/systemd/system/indiwebmanager.service << 'EOF'
[Unit]
Description=INDI Web Manager
After=network.target

[Service]
User=root
ExecStart=/usr/local/bin/indi-web -v
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

cat > /etc/systemd/system/nina-headless.service << 'EOF'
[Unit]
Description=NINA Headless Server
After=network.target indiwebmanager.service
Wants=indiwebmanager.service

[Service]
WorkingDirectory=/opt/nina-headless
ExecStart=/usr/local/dotnet/dotnet /opt/nina-headless/NINA.Headless.dll
Restart=on-failure
RestartSec=5
Environment=DOTNET_ROOT=/usr/local/dotnet
Environment=ASPNETCORE_URLS=http://0.0.0.0:1888

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable indiwebmanager
systemctl enable nina-headless

# 7. Create install directory
mkdir -p "$INSTALL_DIR"

# 8. udev rules for astronomy devices
cat > /etc/udev/rules.d/99-astronomy-devices.rules << 'EOF'
SUBSYSTEM=="usb", ATTR{idVendor}=="03c3", MODE="0666", GROUP="plugdev"
SUBSYSTEM=="usb", ATTR{idVendor}=="1618", MODE="0666", GROUP="plugdev"
SUBSYSTEM=="usb", ATTR{idVendor}=="2a0e", MODE="0666", GROUP="plugdev"
SUBSYSTEM=="usb", ATTR{idVendor}=="04b4", MODE="0666", GROUP="plugdev"
EOF
udevadm control --reload-rules

# 9. Start INDI Web Manager and create profile
systemctl start indiwebmanager
sleep 3
curl -s -X POST http://localhost:8624/api/profiles/BeyondStellar || true
log "BeyondStellar INDI profile created"

# 10. Configure avahi for mDNS discovery
hostnamectl set-hostname nina-dev
systemctl enable avahi-daemon
systemctl restart avahi-daemon

log ""
log "=== Setup complete ==="
log "VM IP: $(hostname -I | awk '{print $1}')"
log "SSH:   ssh ${NINA_USER}@$(hostname -I | awk '{print $1}')"
log "INDI:  http://$(hostname -I | awk '{print $1}'):8624"
log "NINA:  http://$(hostname -I | awk '{print $1}'):1888 (after deploy)"
log ""
log "Next: run 'deploy-to-vm.sh' from Mac to deploy NINA Headless"
