#!/bin/bash
# WiFi AP Watchdog — restarts hostapd if AP is down
# Runs as systemd service

IFACE="wlan0"
CHECK_INTERVAL=30
FAIL_COUNT=0
MAX_FAILURES=3

while true; do
    # Check if hostapd is running AND interface is in AP mode
    if ! iw dev "$IFACE" info 2>/dev/null | grep -q "type AP"; then
        FAIL_COUNT=$((FAIL_COUNT + 1))
        logger -t wifi-watchdog "AP check failed ($FAIL_COUNT/$MAX_FAILURES)"
        
        if [ "$FAIL_COUNT" -ge "$MAX_FAILURES" ]; then
            logger -t wifi-watchdog "Restarting WiFi AP stack"
            systemctl restart hostapd
            systemctl restart dnsmasq
            FAIL_COUNT=0
            sleep 10
        fi
    else
        FAIL_COUNT=0
    fi
    
    sleep "$CHECK_INTERVAL"
done
