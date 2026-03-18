#!/bin/bash
# NINA Headless — Build on Mac, Deploy to Ubuntu VM
# Usage: bash scripts/deploy-to-vm.sh [vm-ip]
#
# Workflow:
#   1. Cross-compile for linux-arm64 on Mac
#   2. rsync to VM
#   3. Restart nina-headless service
#
# First time: bash scripts/deploy-to-vm.sh 192.168.64.4
# After that: bash scripts/deploy-to-vm.sh (uses saved IP)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
WORKTREE="/tmp/nina-ui-overhaul"
PUBLISH_DIR="/tmp/nina-headless-publish"
REMOTE_DIR="/opt/nina-headless"
VM_IP_FILE="$SCRIPT_DIR/.vm-ip"

# VM credentials
VM_USER="nina"
VM_PASS="nina"

SSH_OPTS="-o StrictHostKeyChecking=no -o PreferredAuthentications=password -o PubkeyAuthentication=no -o ConnectTimeout=10 -o LogLevel=ERROR"

vm_ssh() {
    sshpass -p "$VM_PASS" ssh $SSH_OPTS "${VM_USER}@${VM_IP}" "$@"
}
vm_rsync() {
    local attempt max_attempts=3
    for attempt in $(seq 1 $max_attempts); do
        if RSYNC_RSH="sshpass -p $VM_PASS ssh $SSH_OPTS" rsync "$@"; then
            return 0
        fi
        echo "      rsync attempt $attempt/$max_attempts failed, retrying in 2s..."
        sleep 2
    done
    echo "ERROR: rsync failed after $max_attempts attempts"
    return 1
}
vm_sudo() {
    vm_ssh "echo '$VM_PASS' | sudo -S $*"
}

# Determine source directory
if [ -d "$WORKTREE/NINA.Headless" ]; then
    SRC_DIR="$WORKTREE"
else
    SRC_DIR="$REPO_ROOT"
fi

# Get VM IP
if [ -n "${1:-}" ]; then
    VM_IP="$1"
    echo "$VM_IP" > "$VM_IP_FILE"
elif [ -f "$VM_IP_FILE" ]; then
    VM_IP=$(cat "$VM_IP_FILE")
else
    echo "Usage: $0 <vm-ip>"
    echo "Example: $0 192.168.64.4"
    exit 1
fi

echo "=== NINA Headless Deploy ==="
echo "Source:  $SRC_DIR"
echo "Target:  ${VM_USER}@${VM_IP}:${REMOTE_DIR}"
echo ""

# 1. Build
echo "[1/3] Building for linux-arm64..."
cd "$SRC_DIR"
dotnet publish NINA.Headless/NINA.Headless.csproj \
    -c Release \
    -r linux-arm64 \
    --self-contained false \
    -o "$PUBLISH_DIR" \
    -p:PublishSingleFile=false \
    -verbosity:quiet

echo "      Published to $PUBLISH_DIR ($(du -sh "$PUBLISH_DIR" | cut -f1))"

# 2. Deploy
echo "[2/3] Deploying to VM..."
vm_sudo "systemctl stop nina-headless 2>/dev/null || true"

vm_rsync -az --delete \
    "$PUBLISH_DIR/" \
    "${VM_USER}@${VM_IP}:/tmp/nina-deploy/"

vm_sudo "mkdir -p ${REMOTE_DIR}"
vm_sudo "cp -r /tmp/nina-deploy/. ${REMOTE_DIR}/"
vm_sudo "rm -rf /tmp/nina-deploy"

# 3. Restart
echo "[3/3] Restarting service..."
vm_sudo "systemctl start nina-headless"

sleep 2

HEALTH=$(vm_ssh "curl -s http://localhost:1888/api/v1/health 2>/dev/null" || echo "FAILED")
echo ""
echo "=== Deploy Complete ==="
echo "Health: $HEALTH"
echo "API:    http://${VM_IP}:1888/api/v1/equipment/devices"
echo ""
echo "Logs:   ssh ${VM_USER}@${VM_IP} journalctl -u nina-headless -f"
