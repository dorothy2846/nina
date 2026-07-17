// Astellar rendezvous/signaling relay.
// Pairs one observatory socket with controller sockets by machineId and
// relays JSON text frames verbatim ({type, payload, ...} envelopes:
// hello/hello-ack, offer/answer/ice, rpc, event). Sends a server-generated
// ice-config to every socket on connect. STUN-only (no TURN yet).
const http = require('http');
const { WebSocketServer } = require('ws');
const url = require('url');

const PORT = 8080;
const observatories = new Map();   // machineId -> ws
const controllers = new Map();     // machineId -> Set<ws>

const ICE_CONFIG = JSON.stringify({
  type: 'ice-config',
  payload: { iceServers: [
    { urls: ['stun:stun.l.google.com:19302'] },
    { urls: [
        'turn:astellar-rdv.koreasouth.cloudapp.azure.com:3478?transport=udp',
        'turn:astellar-rdv.koreasouth.cloudapp.azure.com:3478?transport=tcp'
      ],
      username: 'astellar',
      credential: process.env.TURN_CREDENTIAL || 'CHANGE_ME' }
  ] }
});

const server = http.createServer((req, res) => {
  if (req.url === '/health') { res.writeHead(200); res.end('ok'); return; }
  res.writeHead(404); res.end();
});
const wss = new WebSocketServer({ server, path: '/ws' });

function log(...a) { console.log(new Date().toISOString(), ...a); }

wss.on('connection', (ws, req) => {
  const q = url.parse(req.url, true).query;
  const role = q.role, machineId = q.machineId;
  if (!machineId || !['observatory', 'controller'].includes(role)) { ws.close(4000, 'bad params'); return; }
  ws.isAlive = true;
  ws.on('pong', () => { ws.isAlive = true; });
  log(`connect role=${role} machineId=${machineId}`);
  ws.send(ICE_CONFIG);

  if (role === 'observatory') {
    const old = observatories.get(machineId);
    if (old && old !== ws) { try { old.close(4001, 'replaced'); } catch {} }
    observatories.set(machineId, ws);
    ws.on('message', (data, isBinary) => {
      if (isBinary) return;
      const peers = controllers.get(machineId);
      if (peers) for (const p of peers) { try { p.send(data.toString()); } catch {} }
    });
    ws.on('close', () => {
      if (observatories.get(machineId) === ws) observatories.delete(machineId);
      const peers = controllers.get(machineId);
      if (peers) for (const p of peers) { try { p.send(JSON.stringify({ type: 'error', error: 'observatory-offline' })); } catch {} }
      log(`observatory gone machineId=${machineId}`);
    });
  } else {
    if (!controllers.has(machineId)) controllers.set(machineId, new Set());
    controllers.get(machineId).add(ws);
    const obs = observatories.get(machineId);
    if (!obs || obs.readyState !== 1) {
      try { ws.send(JSON.stringify({ type: 'error', error: 'observatory-offline' })); } catch {}
    }
    ws.on('message', (data, isBinary) => {
      if (isBinary) return;
      const o = observatories.get(machineId);
      if (o && o.readyState === 1) { try { o.send(data.toString()); } catch {} }
      else { try { ws.send(JSON.stringify({ type: 'error', error: 'observatory-offline' })); } catch {} }
    });
    ws.on('close', () => {
      controllers.get(machineId)?.delete(ws);
      log(`controller gone machineId=${machineId}`);
    });
  }
});

setInterval(() => {
  for (const ws of wss.clients) {
    if (!ws.isAlive) { try { ws.terminate(); } catch {} continue; }
    ws.isAlive = false;
    try { ws.ping(); } catch {}
  }
}, 30000);

server.listen(PORT, () => log(`rendezvous relay listening :${PORT}`));
