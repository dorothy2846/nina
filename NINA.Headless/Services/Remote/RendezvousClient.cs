using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace NINA.Headless.Services.Remote;

/// <summary>
/// Observatory-side client for the rendezvous/signaling server. On boot it connects over
/// WebSocket as <c>role=observatory</c>, waits for SDP offers forwarded from a controller
/// (iOS app), answers them via SIPSorcery, and opens a WebRTC DataChannel for remote RPC.
///
/// DataChannel carries a tiny HTTP-ish envelope: the controller sends a JSON message
/// <c>{type:"rpc", id, method, path, headers?, body?}</c>, we forward it to the local
/// ASP.NET Core listener (port 1888), and ship the response back under the same id.
/// Lets the iOS app reuse every existing REST endpoint over the WebRTC tunnel without
/// the server growing a second API surface.
///
/// Connection is self-healing: reconnect with 10 s backoff on any WS drop. A single
/// active peer is tracked at a time; a fresh offer kills the previous peer.
/// </summary>
public class RendezvousClient : BackgroundService, IRemoteEventSink
{
    private readonly RendezvousConfigStore _store;
    private readonly RemoteEventBus _eventBus;
    private readonly PairedDeviceStore _paired;
    private readonly OwnerAccountStore _owner;
    private readonly SupabaseAuthService _supabase;
    private readonly NINA.Headless.Services.H264Transcoder _h264;
    private readonly NINA.Headless.Services.CameraStreamService _stream;
    private readonly ILogger<RendezvousClient> _log;
    private readonly HttpClient _loopback;

    // Per-peer video transport state. Track is non-null only while a remote
    // viewer is connected; we attach NALU/FrameBoundary handlers lazily on
    // first hello-ack and detach on peer close so encoder ticks don't fan
    // out into a dead RTCPeerConnection.
    private MediaStreamTrack? _videoTrack;
    private long _videoStreamStartTicks;
    private uint _videoRtpTimestamp;
    private bool _videoHandlersAttached;

    private ClientWebSocket? _ws;
    private RTCPeerConnection? _peer;
    private RTCDataChannel? _activeChannel;
    private PairedDevice? _authenticatedDevice;
    private List<RTCIceServer> _iceServers = new() { new RTCIceServer { urls = "stun:stun.l.google.com:19302" } };
    private readonly List<RTCIceCandidateInit> _pendingIce = new();
    private readonly object _peerLock = new();

    public RendezvousClient(RendezvousConfigStore store, RemoteEventBus eventBus, PairedDeviceStore paired,
        OwnerAccountStore owner, SupabaseAuthService supabase,
        NINA.Headless.Services.H264Transcoder h264, NINA.Headless.Services.CameraStreamService stream,
        ILogger<RendezvousClient> log)
    {
        _store = store;
        _eventBus = eventBus;
        _paired = paired;
        _owner = owner;
        _supabase = supabase;
        _h264 = h264;
        _stream = stream;
        _log = log;
        _loopback = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:1888"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _eventBus.AddSink(this);
    }

    /// <summary>Push a server event to the currently active DataChannel. The LAN path
    /// still goes through SignalR via RemoteEventBus — this is the remote counterpart.</summary>
    public void Emit(string topic, object payload)
    {
        var dc = _activeChannel;
        if (dc == null || dc.readyState != RTCDataChannelState.open) return;
        try
        {
            var json = JsonSerializer.Serialize(new { type = "event", topic, payload });
            dc.send(json);
        }
        catch (Exception ex) { _log.LogDebug(ex, "dc emit event failed"); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _store.Load();
            if (!cfg.Enabled)
            {
                _log.LogInformation("Rendezvous disabled in config; sleeping");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                continue;
            }

            try
            {
                await ConnectAndPumpAsync(cfg, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Rendezvous loop errored; reconnecting in 10s");
            }
            finally
            {
                await CleanupAsync();
            }

            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { }
        }
    }

    private async Task ConnectAndPumpAsync(RendezvousConfig cfg, CancellationToken ct)
    {
        var url = $"{cfg.RendezvousUrl}/ws?role=observatory&machineId={Uri.EscapeDataString(cfg.MachineId)}";
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await _ws.ConnectAsync(new Uri(url), ct);
        _log.LogInformation("Rendezvous connected as {MachineId}", cfg.MachineId);

        var buf = new byte[64 * 1024];
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _log.LogInformation("Rendezvous closed the WebSocket");
                    return;
                }
                ms.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.ToArray());
            try { await HandleServerMsgAsync(json, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Rendezvous message handling failed"); }
        }
    }

    private async Task HandleServerMsgAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        _log.LogDebug("rendezvous recv: {Type}", type);

        switch (type)
        {
            case "ice-config":
                if (root.TryGetProperty("payload", out var cfgPayload))
                    _iceServers = ParseIceServers(cfgPayload);
                break;
            case "hello":
                // Controller MUST authenticate before we'll accept an offer —
                // rejecting here keeps an unauthenticated caller from ever
                // triggering the WebRTC stack. We echo back success so the iPhone
                // knows it can proceed; unknown tokens get an error + silence.
                HandleHello(root);
                break;
            case "offer":
                if (_authenticatedDevice == null)
                {
                    _log.LogWarning("rejecting offer from unauthenticated peer");
                    await SendServerAsync(new { type = "error", error = "unauthenticated" });
                    return;
                }
                if (root.TryGetProperty("payload", out var offerP))
                    await HandleOfferAsync(offerP);
                break;
            case "ice":
                if (root.TryGetProperty("payload", out var iceP))
                    HandleIce(iceP);
                break;
            case "error":
                _log.LogWarning("rendezvous error: {Err}", root.TryGetProperty("error", out var e) ? e.GetString() : "(none)");
                break;
            default:
                _log.LogDebug("rendezvous: ignoring type={Type}", type);
                break;
        }
    }

    private static List<RTCIceServer> ParseIceServers(JsonElement payload)
    {
        var list = new List<RTCIceServer>();
        if (!payload.TryGetProperty("iceServers", out var servers)) return list;
        foreach (var s in servers.EnumerateArray())
        {
            if (!s.TryGetProperty("urls", out var urlsEl)) continue;
            var urls = urlsEl.ValueKind == JsonValueKind.Array
                ? string.Join(",", urlsEl.EnumerateArray().Select(x => x.GetString()).Where(x => x != null))
                : urlsEl.GetString();
            var rtc = new RTCIceServer { urls = urls ?? "" };
            if (s.TryGetProperty("username", out var u)) rtc.username = u.GetString();
            if (s.TryGetProperty("credential", out var c)) rtc.credential = c.GetString();
            list.Add(rtc);
        }
        return list;
    }

    private async Task HandleOfferAsync(JsonElement offerPayload)
    {
        var sdp = offerPayload.GetProperty("sdp").GetString();
        if (string.IsNullOrEmpty(sdp)) { _log.LogWarning("empty SDP in offer"); return; }

        lock (_peerLock)
        {
            if (_peer != null)
            {
                try { _peer.Close("replaced by new offer"); } catch { }
                _peer = null;
            }
            _pendingIce.Clear();
            _videoTrack = null;
        }

        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = _iceServers });

        // Attach a VP8 send-only track if the controller's offer requested
        // video. Mirror the LAN WebRTCService path so the rendezvous tunnel
        // delivers the same viewfinder stream — same ffmpeg pipeline, same
        // NALU events, same wall-clock RTP timestamps. Sniff the offer for
        // an `m=video` line; if absent, the controller is on the data-only
        // path and we skip the video setup entirely so the peer doesn't
        // negotiate a video transceiver it can't render.
        if (sdp.Contains("\nm=video", StringComparison.Ordinal) || sdp.StartsWith("m=video", StringComparison.Ordinal))
        {
            // CameraStreamService.StartAsync is idempotent and ensures the
            // INDI BLOB → ffmpeg → IVF pipeline is producing frames before
            // we hand the track to the peer. Without this, a remote viewer
            // that arrives before any LAN viewer ever connected sees a
            // black canvas because ffmpeg has no input.
            try { await _stream.StartAsync(CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "rendezvous: stream start failed; video track may be silent"); }

            var videoFormat = new VideoFormat(VideoCodecsEnum.VP8, 96);
            var track = new MediaStreamTrack(videoFormat, MediaStreamStatusEnum.SendOnly);
            pc.addTrack(track);
            _videoTrack = track;
            AttachVideoHandlers();
        }

        pc.onicecandidate += async (cand) =>
        {
            if (cand == null) return;
            // SIPSorcery's ToString() emits the SDP attribute form ("a=candidate:..."),
            // but browsers / Google WebRTC iOS expect the value-only form without "a=".
            // Leaving the prefix on is what was causing "addIceCandidate: Expected
            // candidate got" across Safari + the native iOS client — the entire ICE
            // negotiation fails because no remote candidate is acceptable.
            var candStr = cand.ToString() ?? string.Empty;
            // Normalise to the WebRTC addIceCandidate wire form. SIPSorcery's ToString
            // omits the "candidate:" prefix (emits only "<foundation> <component> ...")
            // while Safari/Chrome/iOS all demand it — without this the browser throws
            // "addIceCandidate: Expected candidate got" and ICE fails.
            if (candStr.StartsWith("a=candidate:")) candStr = candStr[2..];
            else if (!candStr.StartsWith("candidate:")) candStr = "candidate:" + candStr;
            candStr = candStr.TrimEnd('\r', '\n');
            if (candStr == "candidate:" || string.IsNullOrEmpty(candStr)) return;
            var payload = new
            {
                candidate = candStr,
                sdpMid = cand.sdpMid,
                sdpMLineIndex = cand.sdpMLineIndex
            };
            await SendServerAsync(new { type = "ice", payload });
        };

        pc.onconnectionstatechange += (state) =>
        {
            _log.LogInformation("peer state: {State}", state);
            if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
            {
                lock (_peerLock)
                {
                    if (_peer == pc) _peer = null;
                    _activeChannel = null;
                    _videoTrack = null;
                }
                DetachVideoHandlers();
            }
        };

        pc.ondatachannel += (dc) =>
        {
            _log.LogInformation("data channel opened by peer: {Label}", dc.label);
            _activeChannel = dc;
            WireDataChannel(dc);
        };

        var setRem = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
        if (setRem != SetDescriptionResultEnum.OK)
        {
            _log.LogWarning("setRemoteDescription failed: {Res}", setRem);
            pc.Close("bad remote sdp");
            return;
        }

        var answer = pc.createAnswer(null);
        await pc.setLocalDescription(answer);

        // Same fix-up as the LAN WebRTC path: SIPSorcery emits the m= line
        // with `UDP/TLS/RTP/SAVP` even with rtcp-fb advertised, while
        // browsers + iOS WebRTC expect `SAVPF` (RFC 5124). Without this the
        // peer rejects the answer and the video transceiver never opens.
        var answerSdp = pc.localDescription.sdp.ToString();
        answerSdp = answerSdp.Replace(" UDP/TLS/RTP/SAVP ", " UDP/TLS/RTP/SAVPF ");

        await SendServerAsync(new
        {
            type = "answer",
            payload = new { type = "answer", sdp = answerSdp }
        });

        lock (_peerLock)
        {
            _peer = pc;
            foreach (var ice in _pendingIce)
            {
                try { pc.addIceCandidate(ice); } catch (Exception ex) { _log.LogDebug(ex, "flush ice"); }
            }
            _pendingIce.Clear();
        }
    }

    /// <summary>Subscribe to the shared H264Transcoder events. Idempotent —
    /// re-attaching is a no-op so we can call this on every fresh offer.
    /// The detach side is the symmetric cleanup; called when the peer dies
    /// so encoder ticks don't leak into a closed RTCPeerConnection.</summary>
    private void AttachVideoHandlers()
    {
        if (_videoHandlersAttached) return;
        _h264.FrameBoundary += OnVideoFrameBoundary;
        _h264.NaluReady += OnVideoNaluReady;
        _videoHandlersAttached = true;
    }

    private void DetachVideoHandlers()
    {
        if (!_videoHandlersAttached) return;
        _h264.FrameBoundary -= OnVideoFrameBoundary;
        _h264.NaluReady -= OnVideoNaluReady;
        _videoHandlersAttached = false;
        _videoStreamStartTicks = 0;
        _videoRtpTimestamp = 0;
    }

    /// <summary>Wall-clock RTP timestamp for the rendezvous peer's video
    /// stream. Same algorithm as WebRTCService.OnFrameBoundary — 90 kHz,
    /// computed from elapsed ticks since the first frame of this session.
    /// Independent of the LAN path's counter so two simultaneous viewers
    /// can each have their own monotonic clock.</summary>
    private void OnVideoFrameBoundary()
    {
        var startTicks = Volatile.Read(ref _videoStreamStartTicks);
        if (startTicks == 0)
        {
            startTicks = DateTime.UtcNow.Ticks;
            Interlocked.CompareExchange(ref _videoStreamStartTicks, startTicks, 0);
            startTicks = Volatile.Read(ref _videoStreamStartTicks);
        }
        var elapsedTicks = DateTime.UtcNow.Ticks - startTicks;
        _videoRtpTimestamp = (uint)(elapsedTicks * 9 / 1000);
    }

    private void OnVideoNaluReady(byte[] nalu)
    {
        // Snapshot under lock so a concurrent peer-state-change can't null
        // the track between the check and the send. SendVideo on a closed
        // peer just throws; we swallow because the next FrameBoundary will
        // re-attempt with the new state.
        RTCPeerConnection? pc;
        MediaStreamTrack? track;
        lock (_peerLock) { pc = _peer; track = _videoTrack; }
        if (pc == null || track == null) return;
        try { pc.SendVideo(_videoRtpTimestamp, nalu); }
        catch (Exception ex) { _log.LogDebug(ex, "rendezvous SendVideo failed"); }
    }

    private void HandleHello(JsonElement root)
    {
        // Token + deviceId arrive nested under "payload" — same envelope the
        // iOS client uses for offer/answer/ice. Earlier this read the root
        // directly and silently rejected every authenticated dial.
        var source = root.TryGetProperty("payload", out var p) ? p : root;
        var token = source.TryGetProperty("token", out var t) ? t.GetString() : null;
        var deviceId = source.TryGetProperty("deviceId", out var d) ? d.GetString() : null;
        if (string.IsNullOrEmpty(token))
        {
            _log.LogWarning("hello without token");
            _ = SendServerAsync(new { type = "error", error = "missing_token" });
            return;
        }
        // Fire-and-forget the Supabase verify so we don't block the WS receive
        // loop on a remote HTTP call. The result feeds back into _authenticatedDevice
        // and the hello-ack the same way the legacy synchronous path did.
        _ = HandleHelloAuthAsync(token, deviceId);
    }

    private async Task HandleHelloAuthAsync(string token, string? deviceId)
    {
        // Account-based auth path: if the token is a Supabase access token
        // and the verified UUID is (or can become) the observatory's owner,
        // accept. We synthesise a minimal PairedDevice so the rest of the
        // signaling pipeline — which keys off _authenticatedDevice — keeps
        // working without a wider refactor.
        var uuid = await _supabase.VerifyAsync(token);
        if (uuid != null)
        {
            var authorized = _owner.IsAuthorized(uuid) || _owner.TryClaim(uuid);
            if (authorized)
            {
                _authenticatedDevice = new PairedDevice
                {
                    Id = deviceId ?? uuid,
                    Nickname = "Supabase",
                    PublicKey = "",
                    TokenHash = "",
                    PairedAt = DateTime.UtcNow,
                };
                _log.LogInformation("hello authenticated via Supabase user {Uuid} (device={DeviceId})", uuid, deviceId);
                await SendServerAsync(new { type = "hello-ack", nickname = "Supabase" });
                return;
            }
            _log.LogWarning("hello: Supabase user {Uuid} not authorized for this observatory", uuid);
            await SendServerAsync(new { type = "error", error = "not_owner" });
            return;
        }

        // Supabase verify said null — either offline or this is a legacy
        // LAN-trust bearer token. Try that path so already-paired phones
        // keep working.
        var device = _paired.Verify(token);
        if (device == null)
        {
            _log.LogWarning("hello with invalid token from {DeviceId}", deviceId);
            await SendServerAsync(new { type = "error", error = "invalid_token" });
            return;
        }
        _authenticatedDevice = device;
        _log.LogInformation("hello authenticated via legacy bearer: device={DeviceId} ({Nickname})", device.Id, device.Nickname);
        await SendServerAsync(new { type = "hello-ack", nickname = device.Nickname });
    }

    private void HandleIce(JsonElement payload)
    {
        var init = new RTCIceCandidateInit
        {
            candidate = payload.TryGetProperty("candidate", out var c) ? c.GetString() ?? "" : "",
            sdpMid = payload.TryGetProperty("sdpMid", out var m) ? m.GetString() : null,
            sdpMLineIndex = payload.TryGetProperty("sdpMLineIndex", out var i) && i.ValueKind == JsonValueKind.Number ? (ushort)i.GetInt32() : (ushort)0
        };
        lock (_peerLock)
        {
            if (_peer == null || _peer.remoteDescription == null)
            {
                _pendingIce.Add(init);
                return;
            }
            try { _peer.addIceCandidate(init); }
            catch (Exception ex) { _log.LogDebug(ex, "addIceCandidate failed"); }
        }
    }

    private void WireDataChannel(RTCDataChannel dc)
    {
        dc.onopen += () => _log.LogInformation("data channel '{Label}' open", dc.label);
        dc.onclose += () => _log.LogInformation("data channel '{Label}' closed", dc.label);
        dc.onmessage += (channel, protocol, data) =>
        {
            if (protocol != DataChannelPayloadProtocols.WebRTC_String)
            {
                _log.LogDebug("dc: ignoring binary frame ({Len}B)", data.Length);
                return;
            }
            // Deliberate fire-and-forget: RPCs run in parallel. Errors are caught inside
            // the handler and reported as {status:500} back over the channel.
            _ = Task.Run(() => HandleRpcAsync(channel, Encoding.UTF8.GetString(data)));
        };
    }

    /// <summary>
    /// Wire format:
    ///   request  → {"type":"rpc","id":"<uuid>","method":"GET","path":"/api/...","body":"<string>"}
    ///   response ← {"type":"rpc","id":"<uuid>","status":200,"headers":{...},"body":"<string>"}
    /// The request's path is joined onto <c>http://127.0.0.1:1888</c> so only our own
    /// endpoints are reachable — never arbitrary hosts.
    /// </summary>
    /// Treat a response as text iff its Content-Type is JSON, text/*, or one of the
    /// XML-ish application media types. Anything else (images, FITS, octet-stream,
    /// audio, video) goes down the binary path so the bytes survive intact.
    private static bool IsTextualContent(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType)) return true;
        mediaType = mediaType.ToLowerInvariant();
        return mediaType.StartsWith("text/")
            || mediaType == "application/json"
            || mediaType == "application/xml"
            || mediaType == "application/problem+json"
            || mediaType.EndsWith("+json")
            || mediaType.EndsWith("+xml");
    }

    private async Task HandleRpcAsync(RTCDataChannel channel, string json)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t) && t.GetString() != "rpc") return;
            id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var method = (root.TryGetProperty("method", out var m) ? m.GetString() : "GET") ?? "GET";
            var path = (root.TryGetProperty("path", out var p) ? p.GetString() : "/") ?? "/";
            if (!path.StartsWith("/")) path = "/" + path;
            var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;

            var req = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), path);
            if (body != null)
            {
                var contentType = "application/json";
                if (root.TryGetProperty("headers", out var hs) && hs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var h in hs.EnumerateObject())
                    {
                        if (string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        { contentType = h.Value.GetString() ?? contentType; continue; }
                        try { req.Headers.TryAddWithoutValidation(h.Name, h.Value.GetString()); } catch { }
                    }
                }
                req.Content = new StringContent(body, Encoding.UTF8, contentType);
            }

            using var resp = await _loopback.SendAsync(req);
            var respBytes = await resp.Content.ReadAsByteArrayAsync();
            var respHeaders = new Dictionary<string, string>();
            foreach (var h in resp.Headers) respHeaders[h.Key] = string.Join(",", h.Value);
            foreach (var h in resp.Content.Headers) respHeaders[h.Key] = string.Join(",", h.Value);

            if (IsTextualContent(resp.Content.Headers.ContentType?.MediaType))
            {
                var respBody = Encoding.UTF8.GetString(respBytes);
                var out_ = JsonSerializer.Serialize(new
                {
                    type = "rpc",
                    id,
                    status = (int)resp.StatusCode,
                    headers = respHeaders,
                    body = respBody
                });
                try { channel.send(out_); }
                catch (Exception ex) { _log.LogDebug(ex, "dc send rpc response failed"); }
            }
            else
            {
                // Binary frame layout: [uint16 LE header length][JSON header bytes][body bytes].
                // Receiver extracts the id from the JSON header and dispatches the remaining
                // bytes as the response body — keeps concurrent RPCs untangled on one channel.
                var headerJson = JsonSerializer.Serialize(new
                {
                    type = "rpc",
                    id,
                    status = (int)resp.StatusCode,
                    headers = respHeaders,
                    bodyBinary = true,
                    bodyLength = respBytes.Length
                });
                var headerBytes = Encoding.UTF8.GetBytes(headerJson);
                if (headerBytes.Length > ushort.MaxValue)
                {
                    _log.LogWarning("rpc binary header too large ({Len})", headerBytes.Length);
                    return;
                }
                var frame = new byte[2 + headerBytes.Length + respBytes.Length];
                frame[0] = (byte)(headerBytes.Length & 0xFF);
                frame[1] = (byte)((headerBytes.Length >> 8) & 0xFF);
                Buffer.BlockCopy(headerBytes, 0, frame, 2, headerBytes.Length);
                Buffer.BlockCopy(respBytes, 0, frame, 2 + headerBytes.Length, respBytes.Length);
                try { channel.send(frame); }
                catch (Exception ex) { _log.LogDebug(ex, "dc send rpc binary response failed"); }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RPC dispatch failed");
            try
            {
                channel.send(JsonSerializer.Serialize(new
                {
                    type = "rpc",
                    id,
                    status = (int)HttpStatusCode.InternalServerError,
                    body = ex.Message
                }));
            }
            catch { }
        }
    }

    private async Task SendServerAsync(object obj)
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task CleanupAsync()
    {
        lock (_peerLock)
        {
            if (_peer != null)
            {
                try { _peer.Close("cleanup"); } catch { }
                _peer = null;
            }
            _pendingIce.Clear();
            // Auth is per-WS-session — new connection must re-send hello with
            // a still-valid token, even if it's the same physical iPhone.
            _authenticatedDevice = null;
        }
        if (_ws != null)
        {
            try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            _ws.Dispose();
            _ws = null;
        }
    }
}
