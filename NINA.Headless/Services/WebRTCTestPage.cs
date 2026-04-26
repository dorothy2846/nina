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
        // SIPSorcery's SDP answer doesn't include `a=msid`, so ev.streams is
        // empty even though the track itself arrives. Wrap the bare track in
        // a fresh MediaStream — that's what the video element will render.
        const stream = ev.streams && ev.streams[0] ? ev.streams[0] : new MediaStream([ev.track]);
        document.getElementById('vid').srcObject = stream;
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
  </script>
</body>
</html>
""";
}
