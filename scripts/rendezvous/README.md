# Astellar rendezvous relay (astellar-rdv, testsb RG)

Runs on the Azure VM `astellar-rdv` (Standard_B1s, Korea South,
astellar-rdv.koreasouth.cloudapp.azure.com):

- `server.js` — Node 20 WS relay at `/ws?role=observatory|controller&machineId=…`
  under systemd unit `rdv` (/opt/rdv). Pairs sockets by machineId, relays
  hello/offer/answer/ice/rpc/event envelopes verbatim, hands out `ice-config`
  (Google STUN + on-box coturn TURN) on connect. The deployed copy carries the
  real TURN credential; this copy reads TURN_CREDENTIAL from the environment.
- `Caddyfile` — auto-TLS :443 → localhost:8080.
- coturn on the same VM: `/etc/turnserver.conf`, listening 3478 (UDP+TCP),
  relay range 49152-65535/UDP, lt-cred user `astellar` (credential on the VM
  only). NSG opens 443, 80, 3478, 49152-65535/udp.
