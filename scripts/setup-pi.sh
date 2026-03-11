#!/bin/bash
# NINA-Pi Setup Script for Raspberry Pi 5
# Ubuntu Server 24.04 ARM64
# Usage: sudo bash setup-pi.sh

set -euo pipefail

NINA_USER="${SUDO_USER:-nina}"
NINA_HOME="/home/${NINA_USER}"
NINA_DATA="/mnt/nvme/nina"
LOG_FILE="/var/log/nina-setup.log"

log() { echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*" | tee -a "$LOG_FILE"; }

# 1. System update
log "Updating system packages..."
apt-get update -qq
apt-get upgrade -y -qq

# 2. Install base dependencies
log "Installing base dependencies..."
apt-get install -y -qq \
    curl wget git build-essential cmake \
    libusb-1.0-0-dev libudev-dev \
    libcfitsio-dev libgsl-dev \
    avahi-daemon avahi-utils \
    network-manager \
    hostapd dnsmasq \
    udev

# 3. Install .NET 9 SDK
log "Installing .NET 9 SDK..."
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0 --install-dir /usr/local/dotnet
ln -sf /usr/local/dotnet/dotnet /usr/local/bin/dotnet
echo 'export DOTNET_ROOT=/usr/local/dotnet' >> /etc/environment
echo 'export PATH=$PATH:/usr/local/dotnet' >> /etc/environment

# 4. Install INDI libraries
log "Installing INDI libraries..."
apt-get install -y -qq software-properties-common
add-apt-repository -y ppa:mutlaqja/ppa
apt-get update -qq
apt-get install -y -qq \
    indi-full \
    indi-asi \
    indi-eqmod \
    libindi-dev

# 5. Install PHD2 build dependencies
log "Installing PHD2 build dependencies..."
apt-get install -y -qq \
    libwxgtk3.2-dev \
    libindi-dev \
    libnova-dev \
    libcurl4-openssl-dev \
    gettext \
    wx-common

# 6. Build and install PHD2
log "Building PHD2..."
cd /tmp
git clone --depth 1 https://github.com/OpenPHDGuiding/phd2.git phd2-build
mkdir -p phd2-build/build
cd phd2-build/build
cmake .. -DOPENSOURCE_ONLY=1
make -j$(nproc)
make install
cd /
rm -rf /tmp/phd2-build

# 7. Install ASTAP (ARM64)
log "Installing ASTAP..."
ASTAP_DEB_URL="https://www.hnsky.org/astap_arm64.deb"
wget -q -O /tmp/astap_arm64.deb "$ASTAP_DEB_URL"
dpkg -i /tmp/astap_arm64.deb || apt-get install -f -y
rm -f /tmp/astap_arm64.deb

# 8. Install ASTAP star database (G17)
log "Installing ASTAP G17 star database..."
ASTAP_DB_URL="https://www.hnsky.org/G17.zip"
wget -q -O /tmp/G17.zip "$ASTAP_DB_URL"
mkdir -p /usr/share/astap
unzip -q /tmp/G17.zip -d /usr/share/astap/
rm -f /tmp/G17.zip

# 9. Configure udev rules for ZWO cameras
log "Configuring udev rules for ZWO cameras..."
cat > /etc/udev/rules.d/99-zwo-asi.rules << 'EOF'
# ZWO ASI Cameras
SUBSYSTEM=="usb", ATTR{idVendor}=="03c3", MODE="0666", GROUP="plugdev"
EOF
udevadm control --reload-rules
udevadm trigger

# 10. Create NINA user and directories
log "Setting up NINA user and directories..."
id -u "$NINA_USER" &>/dev/null || useradd -m -s /bin/bash "$NINA_USER"
usermod -aG plugdev,dialout,video "$NINA_USER"
mkdir -p "${NINA_DATA}/fits" "${NINA_DATA}/logs" "${NINA_DATA}/sequences"
chown -R "${NINA_USER}:${NINA_USER}" "${NINA_DATA}"

# 11. Configure avahi-daemon for mDNS
log "Configuring mDNS (avahi-daemon)..."
hostnamectl set-hostname nina-pi
systemctl enable avahi-daemon
systemctl start avahi-daemon

log "Setup complete! Reboot recommended."
log "After reboot, run: systemctl status nina.service"
