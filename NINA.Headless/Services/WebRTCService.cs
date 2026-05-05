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

    // Paced sender — see StartPacer() / OnNaluReady() / PaceTick().
    // The encoder emits NALs at variable rate (~131-139 ms inter-frame on
    // libvpx software). Sending each NAL the moment it arrives leaks that
    // 8 ms variance into the iOS receiver's inter-arrival timing, where
    // its adaptive jitter buffer interprets the variance as network jitter
    // and grows `jitterBufferTargetDelay` toward 4-5 s. Pacing send-side
    // to a fixed 125 ms tick (8 fps) eliminates the perceived jitter at
    // its source — receiver target stays low, end-to-end latency stays
    // bounded by encoder time + 1 tick instead of growing without bound.
    private readonly object _paceLock = new();
    private byte[]? _pendingNalu;
    private System.Threading.Timer? _paceTimer;
    /// <summary>Server-side send pace. 33 ms = 30 fps tick rate. Worst-case
    /// wait between encode-done and send drops to one tick. Camera tops
    /// out around 8-14 fps, so most ticks fire with no pending NAL and
    /// are no-ops — the 30 fps tick rate just means whenever a frame IS
    /// ready it leaves within ≤33 ms instead of waiting up to 100 ms.</summary>
    private const int PaceIntervalMs = 33;

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
        _paceTimer = new System.Threading.Timer(_ => PaceTick(), null, PaceIntervalMs, PaceIntervalMs);
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

    // H.264 access unit assembly. ffmpeg emits NALs one at a time via
    // NaluReady (no start codes — already stripped by ReadNaluLoopAsync).
    // SIPSorcery's `VideoStream.SendH264Frame(duration, pt, accessUnit)`
    // does proper RFC 6184 packetization (Single-NAL / STAP-A / FU-A) when
    // given a complete Annex B access unit (NALs concatenated with
    // 4-byte start codes). The generic SendVideo() call we used before
    // does NOT do H.264 packetization — that's why the iOS decoder
    // rejected every frame.
    private readonly List<byte[]> _frameNals = new();
    private byte[]? _pendingFrame;            // serialized access unit, ready to send
    private byte[]? _cachedSps;
    private byte[]? _cachedPps;
    private static readonly byte[] StartCode = { 0x00, 0x00, 0x00, 0x01 };

    private void OnNaluReady(byte[] nalu)
    {
        if (nalu.Length < 1) return;
        Interlocked.Increment(ref _naluCount);
        byte nalType = (byte)(nalu[0] & 0x1F);

        // Cache parameter sets — re-prepended ahead of every IDR by
        // ffmpeg's `dump_extra=freq=keyframe`, but our own copy lets a
        // peer that joins MID-GOP get them immediately if we ever wire
        // up an on-attach push. Don't queue these into the slice list;
        // SendH264Frame handles them when they prefix the access unit.
        if (nalType == 7) { _cachedSps = nalu; }
        else if (nalType == 8) { _cachedPps = nalu; }

        if (_peers.IsEmpty) return;

        bool isVcl = nalType == 1 || nalType == 5;
        lock (_paceLock)
        {
            _frameNals.Add(nalu);
            if (isVcl)
            {
                // Frame complete (one VCL slice per frame in baseline
                // single-slice config). Serialise to Annex B and replace
                // any in-flight pending frame (latest-wins).
                _pendingFrame = AssembleAccessUnit(_frameNals);
                _frameNals.Clear();
            }
        }
    }

    /// Concatenate NALs with 4-byte Annex B start codes between them.
    /// SendH264Frame parses start codes to identify NAL boundaries.
    private static byte[] AssembleAccessUnit(List<byte[]> nals)
    {
        int total = 0;
        foreach (var n in nals) total += StartCode.Length + n.Length;
        var au = new byte[total];
        int o = 0;
        foreach (var n in nals)
        {
            Buffer.BlockCopy(StartCode, 0, au, o, StartCode.Length);
            o += StartCode.Length;
            Buffer.BlockCopy(n, 0, au, o, n.Length);
            o += n.Length;
        }
        return au;
    }

    private void PaceTick()
    {
        if (_peers.IsEmpty) return;
        byte[]? au;
        lock (_paceLock)
        {
            au = _pendingFrame;
            _pendingFrame = null;
        }
        if (au == null) return;

        // SendH264Frame's `duration` argument advances the underlying RTP
        // session's timestamp by that many 90 kHz ticks. Pace interval =
        // 200 ms = 18 000 ticks. Using duration (relative) instead of
        // computing absolute TS keeps RTP TS perfectly uniform regardless
        // of when our timer actually fires (avoids drift from .NET timer
        // jitter that earlier confused the receiver's jitter buffer).
        const uint duration = (uint)(PaceIntervalMs * 90); // 90 kHz
        const int payloadTypeId = 96;

        foreach (var kv in _peers)
        {
            try
            {
                kv.Value.Peer.VideoStream.SendH264Frame(duration, payloadTypeId, au);
                Interlocked.Increment(ref _sendOk);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WebRTC: SendH264Frame failed for peer {Id}", kv.Key);
                Interlocked.Increment(ref _sendFail);
            }
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

        // H.264 video track via SIPSorcery's *codec-specific* SendH264Frame
        // path (NOT the generic SendVideo, which does not RFC 6184 packetize
        // and was the actual cause of the previous "chrome rejected every
        // assembled frame" symptom). High profile (640c1f) matches what the
        // iOS offer puts on PT 96. iOS decodes H.264 in hardware via
        // VideoToolbox even on the simulator — way faster than SW VP8.
        var fmtp = "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=640c1f";
        var videoFormat = new VideoFormat(VideoCodecsEnum.H264, 96, 90000, fmtp);
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
