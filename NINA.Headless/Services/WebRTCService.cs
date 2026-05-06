using System.Collections.Concurrent;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace NINA.Headless.Services;

/// <summary>
/// WebRTC publisher for the camera live stream. Subscribes to
/// <see cref="H264Transcoder.NaluReady"/>, assembles NALs into Annex B access
/// units, and forwards them via <c>VideoStream.SendH264Frame</c> (which does
/// the RFC 6184 packetization the generic <c>SendVideo</c> path skips) to
/// every active peer. SDP offer/answer is exchanged once over
/// <c>/api/v1/rtc/offer</c>; ICE candidates trickle in the same payload.
/// </summary>
public class WebRTCService
{
    private readonly H264Transcoder _h264;
    private readonly CameraStreamService _stream;
    private readonly ILogger<WebRTCService> _log;

    private readonly ConcurrentDictionary<Guid, PeerEntry> _peers = new();
    private bool _naluHandlerAttached;

    // Send-side pacing. Encoder emits NALs at variable inter-frame timing
    // (~130 ms on libvpx, ~80 ms on h264_videotoolbox). Sending each one the
    // moment it arrives leaks that variance into the receiver's adaptive
    // jitter buffer, which then grows toward multi-second playout delay.
    // A fixed-cadence pacer eliminates the perceived jitter at the source.
    private readonly object _paceLock = new();
    private System.Threading.Timer? _paceTimer;
    /// <summary>Server-side send pace. 33 ms = 30 fps tick rate. Camera
    /// effective fps is well below this, so most ticks are no-ops; whenever
    /// a frame IS ready it leaves within ≤33 ms.</summary>
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
        _paceTimer = new System.Threading.Timer(_ => PaceTick(), null, PaceIntervalMs, PaceIntervalMs);
        _naluHandlerAttached = true;
    }

    // H.264 access unit assembly. ffmpeg hands us one NAL at a time (start
    // codes already stripped). SendH264Frame expects a complete access unit
    // with NALs concatenated using 4-byte Annex B start codes.
    private readonly List<byte[]> _frameNals = new();
    private byte[]? _pendingFrame;
    private byte[]? _cachedSps;
    private byte[]? _cachedPps;
    private static readonly byte[] StartCode = { 0x00, 0x00, 0x00, 0x01 };

    private void OnNaluReady(byte[] nalu)
    {
        if (nalu.Length < 1) return;
        Interlocked.Increment(ref _naluCount);
        byte nalType = (byte)(nalu[0] & 0x1F);

        // Cache parameter sets for any future "push to mid-GOP joiner" hook.
        // ffmpeg's `dump_extra=freq=keyframe` already re-prepends them ahead
        // of every IDR, so we don't need to inject them per-frame ourselves.
        if (nalType == 7) { _cachedSps = nalu; }
        else if (nalType == 8) { _cachedPps = nalu; }

        if (_peers.IsEmpty) return;

        bool isVcl = nalType == 1 || nalType == 5;
        lock (_paceLock)
        {
            _frameNals.Add(nalu);
            if (isVcl)
            {
                // Frame complete (single VCL slice per frame). Latest-wins:
                // any in-flight pending frame is overwritten.
                _pendingFrame = AssembleAccessUnit(_frameNals);
                _frameNals.Clear();
                Interlocked.Increment(ref _frameCount);
            }
        }
    }

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

        // SendH264Frame.duration advances the underlying RTP TS by N 90-kHz
        // ticks. Using duration (relative) instead of computing absolute TS
        // keeps inter-frame TS deltas perfectly uniform regardless of timer
        // jitter — the receiver sees a clean cadence.
        const uint duration = (uint)(PaceIntervalMs * 90);
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

    /// <summary>Accept an SDP offer from a new peer and return an SDP answer.
    /// The peer is retained until <see cref="ClosePeerAsync"/> is called or
    /// the connection drops.</summary>
    public async Task<(string answerSdp, string peerId)> CreatePeerForOfferAsync(string offerSdp)
    {
        await _stream.StartAsync(CancellationToken.None);
        AttachNaluHandler();

        // No STUN — LAN-only path. Saves ~500 ms ICE gathering. The remote
        // path (RendezvousClient) brings its own STUN/TURN list.
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>()
        };

        var peer = new RTCPeerConnection(config);
        var id = Guid.NewGuid();

        // High profile (640c1f) — matches what the iOS offer puts on PT 96
        // and what h264_videotoolbox produces on the encode side. iOS decodes
        // via VideoToolbox in HW even on the simulator.
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
        // SIPSorcery emits the m= line with `UDP/TLS/RTP/SAVP` even though
        // per-PT `rtcp-fb` attributes follow. Browsers expect `SAVPF` (RFC
        // 5124) when feedback is advertised.
        answerSdp = answerSdp.Replace(" UDP/TLS/RTP/SAVP ", " UDP/TLS/RTP/SAVPF ");

        _log.LogInformation("WebRTC peer {Id} created; total={Count}", id, _peers.Count);
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
