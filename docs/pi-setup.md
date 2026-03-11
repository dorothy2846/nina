# Raspberry Pi 5 NINA Setup Guide

This guide covers the complete hardware and software setup for running NINA (N.I.N.A.) on a Raspberry Pi 5 with Ubuntu Server 24.04 ARM64.

## Hardware Requirements

### Minimum Specifications
- **Raspberry Pi 5** (8GB RAM recommended, 4GB minimum)
- **NVMe SSD** (256GB+ recommended for FITS storage)
  - NVMe HAT for Pi 5 (e.g., Pimoroni NVMe Base)
  - M.2 2230 or 2242 form factor
- **Active Cooling**
  - Official Pi 5 Active Cooler or equivalent
  - Recommended: Heatsink + 40mm fan for sustained operation
- **Power Supply**
  - 27W USB-C power adapter (official Pi 5 PSU recommended)
  - Stable power critical for NVMe and camera operations
- **USB Hub** (optional but recommended)
  - For connecting multiple cameras and devices
  - Powered hub recommended for camera power draw

### Camera Support
- **ZWO ASI Cameras** (primary support)
  - USB 2.0/3.0 compatible
  - Vendor ID: `03c3`
  - Examples: ASI533MC-Pro, ASI462MC, ASI120MM-S
- **Other INDI-compatible cameras**
  - Supported via INDI library ecosystem

### Network
- **Ethernet** (recommended for stability)
  - USB 3.0 Gigabit Ethernet adapter
- **WiFi** (optional)
  - Built-in WiFi 6E on Pi 5
  - Can configure as WiFi hotspot (SSID: "NINA-Pi")

## Operating System Installation

### Prerequisites
- USB drive (8GB+) for Ubuntu Server image
- Raspberry Pi Imager or similar tool
- Network connection for initial setup

### Installation Steps

1. **Download Ubuntu Server 24.04 ARM64**
   - Visit: https://ubuntu.com/download/raspberry-pi
   - Download: `ubuntu-24.04-preinstalled-server-arm64+raspi.img.xz`

2. **Flash to USB Drive**
   ```bash
   # Using Raspberry Pi Imager (recommended)
   # Or using dd on Linux/macOS:
   xzcat ubuntu-24.04-preinstalled-server-arm64+raspi.img.xz | sudo dd of=/dev/sdX bs=4M conv=fsync
   ```

3. **Boot and Initial Setup**
   - Insert USB drive into Pi 5
   - Connect Ethernet and power
   - Wait 2-3 minutes for first boot
   - Default credentials: `ubuntu` / `ubuntu`
   - Change password on first login

4. **Update System**
   ```bash
   sudo apt-get update
   sudo apt-get upgrade -y
   ```

## NVMe SSD Configuration

### Physical Installation
1. Power off Pi 5 completely
2. Install NVMe HAT according to manufacturer instructions
3. Insert M.2 NVMe SSD into HAT
4. Secure with provided screws
5. Reboot Pi 5

### Partition and Mount

1. **Identify NVMe device**
   ```bash
   lsblk
   # Look for /dev/nvme0n1 (or similar)
   ```

2. **Create partition (if new drive)**
   ```bash
   sudo parted /dev/nvme0n1 mklabel gpt
   sudo parted /dev/nvme0n1 mkpart primary ext4 0% 100%
   sudo mkfs.ext4 /dev/nvme0n1p1
   ```

3. **Create mount point and mount**
   ```bash
   sudo mkdir -p /mnt/nvme
   sudo mount /dev/nvme0n1p1 /mnt/nvme
   ```

4. **Persistent mount (add to /etc/fstab)**
   ```bash
   # Get UUID
   sudo blkid /dev/nvme0n1p1
   
   # Add to /etc/fstab
   sudo nano /etc/fstab
   # Add line: UUID=<your-uuid> /mnt/nvme ext4 defaults,nofail 0 2
   
   # Test mount
   sudo mount -a
   ```

5. **Verify mount**
   ```bash
   df -h /mnt/nvme
   ```

## ZWO Camera USB Configuration

### Verify Camera Detection

1. **Connect camera via USB**
   ```bash
   lsusb | grep "03c3"
   # Should show: ZWO ASI Camera
   ```

2. **Check device permissions**
   ```bash
   ls -la /dev/bus/usb/*/
   # Camera should be readable by plugdev group
   ```

### Configure udev Rules

The setup script automatically configures udev rules for ZWO cameras:

```bash
# File: /etc/udev/rules.d/99-zwo-asi.rules
SUBSYSTEM=="usb", ATTR{idVendor}=="03c3", MODE="0666", GROUP="plugdev"
```

If manually configuring:
```bash
sudo udevadm control --reload-rules
sudo udevadm trigger
```

### Test Camera Access

```bash
# As NINA user (not root)
indi_asi_ccd
# Should connect without permission errors
```

## NINA-Pi Software Installation

### Automated Setup

Run the provided setup script:

```bash
# Download or copy setup-pi.sh to Pi
sudo bash scripts/setup-pi.sh
```

This script will:
- Update system packages
- Install .NET 9 SDK
- Install INDI libraries and drivers
- Build and install PHD2
- Install ASTAP and G17 star database
- Configure udev rules for cameras
- Create NINA user and directories
- Configure mDNS (avahi-daemon)

### Manual Installation (if needed)

See `scripts/setup-pi.sh` for detailed installation steps.

### Verify Installation

```bash
# Check .NET installation
dotnet --version
# Should show: 9.0.x

# Check INDI
indiserver -v
# Should show version info

# Check ASTAP
astap --help
# Should show ASTAP help

# Check PHD2
phd2 --help
# Should show PHD2 help
```

## Directory Structure

After setup, the following directories are created:

```
/mnt/nvme/nina/
├── fits/          # FITS image storage
├── logs/          # Application logs
└── sequences/     # Sequence files
```

Permissions:
- Owner: `nina` user
- Group: `nina` group
- Mode: `755` (rwxr-xr-x)

## Service Status and Management

### Check Service Status

```bash
# After reboot, verify NINA service
systemctl status nina.service

# View service logs
journalctl -u nina.service -f
```

### Manual Service Control

```bash
# Start NINA service
sudo systemctl start nina.service

# Stop NINA service
sudo systemctl stop nina.service

# Restart NINA service
sudo systemctl restart nina.service

# Enable auto-start on boot
sudo systemctl enable nina.service
```

## Network Configuration

### mDNS Hostname

After setup, the Pi is accessible via:
```
nina-pi.local
```

Example:
```bash
ping nina-pi.local
ssh ubuntu@nina-pi.local
```

### WiFi Hotspot (Optional)

To configure Pi as WiFi hotspot:

```bash
# Edit hostapd configuration
sudo nano /etc/hostapd/hostapd.conf

# Add:
interface=wlan0
driver=nl80211
ssid=NINA-Pi
hw_mode=a
channel=36
wpa=2
wpa_passphrase=YourPassword123
wpa_key_mgmt=WPA-PSK
wpa_pairwise=CCMP
```

## Troubleshooting

### Camera Not Detected

1. **Check USB connection**
   ```bash
   lsusb | grep "03c3"
   ```

2. **Verify udev rules**
   ```bash
   cat /etc/udev/rules.d/99-zwo-asi.rules
   ```

3. **Reload udev**
   ```bash
   sudo udevadm control --reload-rules
   sudo udevadm trigger
   ```

4. **Check user group membership**
   ```bash
   groups nina
   # Should include: plugdev, dialout, video
   ```

### NVMe Not Mounting

1. **Check device**
   ```bash
   lsblk
   sudo dmesg | tail -20
   ```

2. **Verify filesystem**
   ```bash
   sudo fsck.ext4 /dev/nvme0n1p1
   ```

3. **Check fstab syntax**
   ```bash
   sudo mount -a
   ```

### .NET Runtime Issues

1. **Verify installation**
   ```bash
   /usr/local/dotnet/dotnet --version
   ```

2. **Check environment variables**
   ```bash
   echo $DOTNET_ROOT
   echo $PATH
   ```

3. **Reload environment**
   ```bash
   source /etc/environment
   ```

### INDI Connection Issues

1. **Check INDI server**
   ```bash
   indiserver -v
   ```

2. **Verify library paths**
   ```bash
   ldconfig -p | grep indi
   ```

3. **Check device permissions**
   ```bash
   ls -la /dev/ttyUSB*
   ls -la /dev/ttyACM*
   ```

### PHD2 Build Failures

1. **Check dependencies**
   ```bash
   apt-get install -y libwxgtk3.2-dev libindi-dev libnova-dev
   ```

2. **Clean and rebuild**
   ```bash
   cd /tmp/phd2-build/build
   rm -rf *
   cmake .. -DOPENSOURCE_ONLY=1
   make -j$(nproc)
   ```

### ASTAP Database Issues

1. **Verify database location**
   ```bash
   ls -la /usr/share/astap/
   ```

2. **Check disk space**
   ```bash
   df -h /usr/share/astap/
   ```

3. **Reinstall if corrupted**
   ```bash
   sudo rm -rf /usr/share/astap/*
   wget -q -O /tmp/G17.zip https://www.hnsky.org/G17.zip
   sudo unzip -q /tmp/G17.zip -d /usr/share/astap/
   ```

## Performance Optimization

### CPU Frequency Scaling

```bash
# Check current frequency
cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq

# Set to performance mode (if needed)
echo performance | sudo tee /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor
```

### Memory Management

```bash
# Check available memory
free -h

# Monitor memory usage
watch -n 1 free -h
```

### Thermal Management

```bash
# Check CPU temperature
vcgencmd measure_temp

# Monitor in real-time
watch -n 1 vcgencmd measure_temp
```

## Security Considerations

### User Permissions

The NINA user is configured with minimal required permissions:
- `plugdev`: USB device access
- `dialout`: Serial port access
- `video`: Video device access

### Firewall Configuration

```bash
# Enable UFW firewall
sudo ufw enable

# Allow SSH
sudo ufw allow 22/tcp

# Allow INDI (optional, if remote access needed)
sudo ufw allow 7624/tcp
```

### SSH Key Authentication

```bash
# Generate SSH key on client
ssh-keygen -t ed25519

# Copy to Pi
ssh-copy-id -i ~/.ssh/id_ed25519.pub ubuntu@nina-pi.local

# Disable password authentication
sudo nano /etc/ssh/sshd_config
# Set: PasswordAuthentication no
sudo systemctl restart ssh
```

## Additional Resources

- **NINA Project**: https://nighttime-imaging.eu/
- **INDI Library**: https://indilib.org/
- **PHD2 Guiding**: https://openphdguiding.org/
- **ASTAP**: https://www.hnsky.org/astap.html
- **Raspberry Pi 5**: https://www.raspberrypi.com/products/raspberry-pi-5/
- **Ubuntu Server ARM64**: https://ubuntu.com/download/raspberry-pi

## Support and Feedback

For issues or questions:
1. Check the Troubleshooting section above
2. Review NINA documentation: https://nighttime-imaging.eu/
3. Check INDI forums: https://indilib.org/forum/
4. Review PHD2 documentation: https://openphdguiding.org/

---

**Last Updated**: 2026-03-11
**Target OS**: Ubuntu Server 24.04 ARM64
**Target Hardware**: Raspberry Pi 5 (8GB RAM)
