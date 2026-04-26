using System.Collections.Concurrent;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace NINA.Headless.Services;

/// <summary>
/// WebRTC publisher for the camera live stream. Consumes H.264 NAL units from the existing
/// <see cref="H264Transcoder"/> and forwards them as RTP-packetized video to every active
/// <see cref="RTCPeerConnection"/>. Signaling happens over a single HTTP POST endpoint
/// (<c>/api/v1/rtc/offer</c>) — we act as the "answerer": the client sends an SDP offer, we
/// set up a peer, return the answer.
///
/// Control flow:
///   client → POST /api/v1/rtc/offer (SDP)
///          ← 200 { sdp: answer }
///   client/server ICE candidates trickled over the same SDP (no trickle endpoint yet)
///   media: server → client RTP/UDP (P2P on LAN, STUN-assisted on internet later)
///
/// Keeps the WebSocket H.264 path alongside so clients that can't do WebRTC still work.
/// </summary>
public class WebRTCService
{
    private readonly H264Transcoder _h264;
    private readonly CameraStreamService _stream;
    private readonly ILogger<WebRTCService> _log;

    private readonly ConcurrentDictionary<Guid, PeerEntry> _peers = new();
    private uint _rtpTimestamp;
    private bool _naluHandlerAttached;

    public WebRTCService(H264Transcoder h264, CameraStreamService stream, ILogger<WebRTCService> log)
    {
        _h264 = h264;
        _stream = stream;
        _log = log;
    }

    public int PeerCount => _peers.Count;

    private void AttachNaluHandler()
    {
        if (_naluHandlerAttached) return;
        _h264.NaluReady += OnNaluReady;
        _h264.FrameBoundary += OnFrameBoundary;
        _naluHandlerAttached = true;
    }

    /// <summary>RTP timestamp from wall-clock (90 kHz). The previous fixed
    /// +9000-per-frame variant assumed actual fps == target fps; if delivery
    /// drops to 5 fps (long exposure, driver hiccup) the receiver perceives
    /// every frame as "100 ms late" and grows its jitter buffer back to the
    /// 6 s territory we're trying to escape. Wall-clock makes RTP TS deltas
    /// match real send cadence, so any fps wobble shows up uniformly on both
    /// sender and receiver sides — no perceived inter-arrival jitter.
    ///
    /// Pairs with the `playout-delay` RTP header extension we attach to
    /// every outgoing video packet: that's what actually pins the iOS
    /// receiver's playout delay to zero and keeps it there regardless of
    /// what its jitter algorithm computes.</summary>
    private long _streamStartTicks;
    private void OnFrameBoundary()
    {
        var startTicks = Volatile.Read(ref _streamStartTicks);
        if (startTicks == 0)
        {
            startTicks = DateTime.UtcNow.Ticks;
            Interlocked.CompareExchange(ref _streamStartTicks, startTicks, 0);
            startTicks = Volatile.Read(ref _streamStartTicks);
        }
        var elapsedTicks = DateTime.UtcNow.Ticks - startTicks;
        _rtpTimestamp = (uint)(elapsedTicks * 9 / 1000);
        Interlocked.Increment(ref _frameCount);
        MaybeLog();
    }

    private void OnNaluReady(byte[] nalu)
    {
        if (_peers.IsEmpty) return;
        Interlocked.Increment(ref _naluCount);
        foreach (var kv in _peers)
        {
            try { kv.Value.Peer.SendVideo(_rtpTimestamp, nalu); Interlocked.Increment(ref _sendOk); }
            catch (Exception ex) { _log.LogWarning(ex, "WebRTC: SendVideo failed for peer {Id}", kv.Key); Interlocked.Increment(ref _sendFail); }
        }
        MaybeLog();
    }

    private long _frameCount, _naluCount, _sendOk, _sendFail;
    private long _lastLogTicks;

    private void MaybeLog()
    {
        var now = DateTime.UtcNow.Ticks;
        var prev = Interlocked.Read(ref _lastLogTicks);
        if (now - prev < TimeSpan.FromSeconds(3).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastLogTicks, now, prev) != prev) return;
        _log.LogInformation("WebRTC video: frames={F} nalus={N} sendOK={Ok} sendFail={Fail} peers={P}",
            _frameCount, _naluCount, _sendOk, _sendFail, _peers.Count);
    }

    /// <summary>Accept an SDP offer from a new peer and return an SDP answer. The peer is
    /// retained until <see cref="ClosePeerAsync"/> is called or the connection drops.</summary>
    public async Task<(string answerSdp, string peerId)> CreatePeerForOfferAsync(string offerSdp)
    {
        await _stream.StartAsync(CancellationToken.None); // ensures H264Transcoder is running
        AttachNaluHandler();

        // No STUN servers — this path is LAN-only; the iOS LiveView WebRTC
        // path strips them client-side too. Skipping STUN cuts ~500 ms off
        // ICE gathering. Remote-mode video (RendezvousRemoteClient) brings
        // its own STUN/TURN list when that path lands.
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>()
        };

        var peer = new RTCPeerConnection(config);
        var id = Guid.NewGuid();

        // VP8 video track. The H.264 path hit chrome decoder rejecting every
        // assembled frame for reasons we couldn't pin to a single SDP/SPS
        // tweak; VP8 is wire-tested in SIPSorcery and chrome accepts its
        // packetization with no parameter-set negotiation.
        var videoFormat = new VideoFormat(VideoCodecsEnum.VP8, 96);
        var videoTrack = new MediaStreamTrack(videoFormat, MediaStreamStatusEnum.SendOnly);
        peer.addTrack(videoTrack);

        peer.onconnectionstatechange += (state) =>
        {
            _log.LogInformation("WebRTC peer {Id} state={State}", id, state);
            if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
            {
                _peers.TryRemove(id, out _);
                try { peer.Close("connection ended"); } catch { }
            }
        };

        var offerInit = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offerSdp
        };
        var setRemoteResult = peer.setRemoteDescription(offerInit);
        if (setRemoteResult != SetDescriptionResultEnum.OK)
        {
            peer.Close("bad remote SDP");
            throw new InvalidOperationException($"setRemoteDescription failed: {setRemoteResult}");
        }

        var answer = peer.createAnswer(null);
        await peer.setLocalDescription(answer);

        _peers[id] = new PeerEntry(peer);
        var answerSdp = peer.localDescription.sdp.ToString();
        // SIPSorcery emits the m= line with `UDP/TLS/RTP/SAVP` even though the
        // SDP also lists per-payload-type `rtcp-fb` attributes. Browsers
        // expect `SAVPF` (RFC 5124) when feedback is advertised.
        answerSdp = answerSdp.Replace(" UDP/TLS/RTP/SAVP ", " UDP/TLS/RTP/SAVPF ");

        // (profile-level-id rewrite no longer applies — codec is VP8 now.)
        _log.LogInformation("WebRTC peer {Id} created; total={Count}\n--- SDP ANSWER ---\n{Sdp}\n--- END SDP ---", id, _peers.Count, answerSdp);
        return (answerSdp, id.ToString());
    }

    public Task ClosePeerAsync(string peerId)
    {
        if (!Guid.TryParse(peerId, out var id)) return Task.CompletedTask;
        if (_peers.TryRemove(id, out var entry))
        {
            try { entry.Peer.Close("client requested close"); } catch { }
        }
        return Task.CompletedTask;
    }

    private record PeerEntry(RTCPeerConnection Peer);
}
