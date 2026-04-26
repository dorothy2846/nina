using SIPSorcery.Net;

namespace NINA.Headless.Services;

/// <summary>
/// `playout-delay` RTP header extension. Tells the receiver the desired
/// minimum and maximum playout delay in 10 ms units. For our LAN viewfinder
/// we send min=0/max=0 every packet, which pins the iOS WebRTC jitter
/// buffer's target playout delay to the floor and prevents the auto-tuner
/// from growing toward the 6.7 s steady state we measured before.
///
/// Wire format (3 bytes after the 1-byte RFC 5285 extension header):
///   bits  0-11: min delay (12 bits, 10 ms units)
///   bits 12-23: max delay (12 bits, 10 ms units)
///
/// URI: http://www.webrtc.org/experiments/rtp-hdrext/playout-delay
/// </summary>
public class PlayoutDelayExtension : RTPHeaderExtension
{
    public const string RTP_HEADER_EXTENSION_URI = "http://www.webrtc.org/experiments/rtp-hdrext/playout-delay";
    private const int RTP_HEADER_EXTENSION_SIZE = 3; // value bytes (header byte not counted)

    // 10 ms units. Both zero = "render the moment you can decode".
    private readonly int _minDelay;
    private readonly int _maxDelay;

    public PlayoutDelayExtension(int id, int minDelay = 0, int maxDelay = 0)
        : base(id, RTP_HEADER_EXTENSION_URI, RTP_HEADER_EXTENSION_SIZE, RTPHeaderExtensionType.OneByte, SDPMediaTypesEnum.video)
    {
        _minDelay = minDelay;
        _maxDelay = maxDelay;
    }

    public override void Set(object value)
    {
        // Constant value per packet — nothing to update at send time.
    }

    public override byte[] Marshal()
    {
        // RFC 5285 one-byte header: (id << 4) | (len - 1).
        // len = number of value bytes = 3, so low nibble = 2.
        var header = (byte)((Id << 4) | (RTP_HEADER_EXTENSION_SIZE - 1));
        var minClamped = _minDelay & 0xFFF;
        var maxClamped = _maxDelay & 0xFFF;
        return new byte[]
        {
            header,
            (byte)((minClamped >> 4) & 0xFF),                                  // top 8 bits of min
            (byte)(((minClamped & 0x0F) << 4) | ((maxClamped >> 8) & 0x0F)),   // low 4 of min | top 4 of max
            (byte)(maxClamped & 0xFF),                                         // low 8 of max
        };
    }

    public override object? Unmarshal(RTPHeader header, byte[] data)
    {
        // Sender-only — we never read this extension off the wire.
        return null;
    }
}
