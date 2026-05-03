namespace NINA.Headless.Services;

/// <summary>
/// Static HTML page served at /rtc-test.html. Lets us exercise the WebRTC server without
/// building an iOS client — confirms ICE + DTLS + H.264 negotiation all work end-to-end.
/// </summary>
internal static class WebRTCTestPage
{
    public const string Html = """
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <title>NINA WebRTC test</title>
  <style>
    body { background:#111; color:#eee; font-family:-apple-system,sans-serif; padding:16px; }
    video { width: 100%; max-width: 960px; background:#000; border: 1px solid #333; }
    #log { white-space: pre-wrap; font-family: monospace; font-size: 11px; margin-top: 12px; max-height: 200px; overflow: auto; background:#000; padding:8px; }
    button { padding: 6px 14px; font-size: 14px; margin-right: 8px; }
  </style>
</head>
<body>
  <div>
    <button id="start">Start WebRTC</button>
    <button id="stop">Stop</button>
    <span id="status">idle</span>
  </div>
  <video id="vid" autoplay playsinline muted></video>
  <div style="margin-top:12px;padding:8px;background:#1a1a1a;border:1px solid #333;border-radius:4px;">
    <strong>Still capture (independent of stream)</strong><br>
    <label>Exposure (s): <input id="capExp" type="number" step="0.01" value="1" style="width:70px"></label>
    <label>Gain: <input id="capGain" type="number" value="100" style="width:70px"></label>
    <label>Bin: <input id="capBin" type="number" value="1" style="width:50px"></label>
    <label>Type: <select id="capType"><option>Light</option><option>Dark</option><option>Bias</option><option>Flat</option></select></label>
    <button id="capture">Capture</button>
    <span id="capStatus" style="margin-left:8px;color:#aaa"></span>
  </div>
  <div style="margin-top:8px;padding:8px;background:#1a1a1a;border:1px solid #333;border-radius:4px;">
    <strong>Video record (SER, runs alongside stream)</strong><br>
    <label>Filename: <input id="recName" type="text" value="session" style="width:140px"></label>
    <label>Duration (s, max 60): <input id="recDur" type="number" min="1" max="60" value="60" style="width:80px"></label>
    <button id="recStart">Start Record</button>
    <button id="recStop">Stop Record</button>
    <span id="recStatus" style="margin-left:8px;color:#aaa">idle</span>
  </div>
  <div id="log"></div>
  <script>
    const logEl = document.getElementById('log');
    const statusEl = document.getElementById('status');
    function log(msg) { logEl.textContent += msg + '\n'; logEl.scrollTop = logEl.scrollHeight; }
    let pc = null, peerId = null;
    document.getElementById('start').onclick = async () => {
      statusEl.textContent = 'creating peer...';
      pc = new RTCPeerConnection({ iceServers: [{ urls: 'stun:stun.l.google.com:19302' }] });
      pc.addTransceiver('video', { direction: 'recvonly' });
      pc.onconnectionstatechange = () => { statusEl.textContent = pc.connectionState; log('state=' + pc.connectionState); };
      pc.ontrack = ev => {
        log('track kind=' + ev.track.kind);
        // Tell chrome to play frames out as soon as they're decoded — bypasses
        // the default jitter buffer build-up. Chrome 120+ honors this down to
        // about one frame; on LAN we drop ~40ms of buffer that wasn't doing
        // anything (loss is essentially zero).
        if (ev.receiver && 'playoutDelayHint' in ev.receiver) {
          ev.receiver.playoutDelayHint = 0;
        }
        const stream = ev.streams && ev.streams[0] ? ev.streams[0] : new MediaStream([ev.track]);
        document.getElementById('vid').srcObject = stream;
        // Self-report inbound video stats every 2s so we can see whether the
        // problem is "packets never arrive" vs "packets arrive but decoder
        // can't make a frame" without leaving the page for chrome://webrtc-internals.
        setInterval(async () => {
          if (!pc) return;
          const stats = await pc.getStats();
          stats.forEach(s => {
            if (s.type === 'inbound-rtp' && s.kind === 'video') {
              const jbMs = s.jitterBufferEmittedCount ? (s.jitterBufferDelay / s.jitterBufferEmittedCount * 1000) : 0;
              log('inbound video: packets=' + (s.packetsReceived||0) + ' framesDecoded=' + (s.framesDecoded||0) + ' fps=' + (s.framesPerSecond||0) + ' jitterBuf=' + jbMs.toFixed(0) + 'ms keyFrames=' + (s.keyFramesDecoded||0));
            }
          });
        }, 2000);
      };
      const offer = await pc.createOffer();
      await pc.setLocalDescription(offer);
      // Wait for ICE gathering to complete (no trickle yet).
      await new Promise(r => {
        if (pc.iceGatheringState === 'complete') return r();
        pc.onicegatheringstatechange = () => { if (pc.iceGatheringState === 'complete') r(); };
      });
      const resp = await fetch('/api/v1/rtc/offer', {
        method: 'POST', headers: { 'Content-Type': 'application/sdp' },
        body: pc.localDescription.sdp
      });
      const data = await resp.json();
      peerId = data.peerId;
      log('peerId=' + peerId);
      await pc.setRemoteDescription({ type: 'answer', sdp: data.sdp });
    };
    document.getElementById('stop').onclick = async () => {
      if (peerId) await fetch('/api/v1/rtc/close/' + peerId, { method: 'POST' });
      if (pc) { pc.close(); pc = null; }
      statusEl.textContent = 'stopped';
    };

    document.getElementById('recStart').onclick = async () => {
      const name = document.getElementById('recName').value || 'session';
      const durRaw = document.getElementById('recDur').value;
      const dur = durRaw ? parseInt(durRaw) : null;
      const recStatusEl = document.getElementById('recStatus');
      recStatusEl.textContent = 'starting...';
      recStatusEl.style.color = '#fa3';
      log('RECORD start name=' + name + ' duration=' + (dur ?? 'manual'));
      const body = { mode: dur ? 'duration' : 'manual', filename: name };
      if (dur) body.durationSeconds = dur;
      try {
        const resp = await fetch('/api/v1/camera/record/start', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body)
        });
        const data = await resp.json();
        if (resp.ok && data.success) {
          recStatusEl.textContent = 'recording: ' + (data.path || name);
          recStatusEl.style.color = '#3f3';
          log('RECORD started: ' + JSON.stringify(data));
        } else {
          recStatusEl.textContent = 'fail: ' + (data.message || resp.status);
          recStatusEl.style.color = '#f44';
        }
      } catch (e) {
        recStatusEl.textContent = 'error: ' + e.message;
        recStatusEl.style.color = '#f44';
      }
    };

    document.getElementById('recStop').onclick = async () => {
      const recStatusEl = document.getElementById('recStatus');
      recStatusEl.textContent = 'stopping...';
      try {
        const resp = await fetch('/api/v1/camera/record/stop', { method: 'POST' });
        const data = await resp.json();
        if (resp.ok && data.success) {
          recStatusEl.textContent = 'stopped (frames=' + (data.frameCount ?? '?') + ')';
          recStatusEl.style.color = '#aaa';
          log('RECORD stopped: ' + JSON.stringify(data));
        } else {
          recStatusEl.textContent = 'fail: ' + (data.message || resp.status);
          recStatusEl.style.color = '#f44';
        }
      } catch (e) {
        recStatusEl.textContent = 'error: ' + e.message;
        recStatusEl.style.color = '#f44';
      }
    };

    document.getElementById('capture').onclick = async () => {
      const capStatusEl = document.getElementById('capStatus');
      const exp = parseFloat(document.getElementById('capExp').value);
      const gain = parseInt(document.getElementById('capGain').value);
      const bin = parseInt(document.getElementById('capBin').value);
      const type = document.getElementById('capType').value;
      if (!(exp > 0)) { capStatusEl.textContent = 'invalid exposure'; return; }
      const t0 = performance.now();
      capStatusEl.textContent = 'capturing...';
      capStatusEl.style.color = '#fa3';
      log('CAPTURE start exp=' + exp + 's gain=' + gain + ' bin=' + bin + ' type=' + type);
      try {
        const resp = await fetch('/api/v1/camera/capture', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ exposureTime: exp, gain: gain, offset: 10, binning: bin, imageType: type })
        });
        const data = await resp.json();
        const elapsed = ((performance.now() - t0) / 1000).toFixed(2);
        if (resp.ok && data.success) {
          capStatusEl.textContent = 'OK in ' + elapsed + 's, id=' + (data.captureId || '?');
          capStatusEl.style.color = '#3f3';
          log('CAPTURE done in ' + elapsed + 's id=' + data.captureId + ' thumb=' + (data.thumbnailUrl || '-'));
        } else {
          capStatusEl.textContent = 'fail: ' + (data.message || resp.status);
          capStatusEl.style.color = '#f44';
          log('CAPTURE fail: ' + JSON.stringify(data));
        }
      } catch (e) {
        capStatusEl.textContent = 'error: ' + e.message;
        capStatusEl.style.color = '#f44';
        log('CAPTURE error: ' + e.message);
      }
    };
  </script>
</body>
</html>
""";
}
