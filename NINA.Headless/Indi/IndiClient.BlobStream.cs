using System.Text;

namespace NINA.Headless.Indi;

/// Streaming BLOB decoder. The generic path accumulates a whole XML element
/// as a string, snapshots it, parses it with XElement and copies the payload
/// twice more before base64-decoding — ~400 ms and several 20 MB+ string
/// allocations for one full-frame FITS (measured). BLOBs don't need any of
/// that: this state machine recognizes a <setBLOBVector><oneBLOB …> opening
/// in the byte stream and decodes the base64 payload incrementally, chunk by
/// chunk, straight into a MemoryStream. Everything that is not a BLOB vector
/// still flows through the untouched generic XML path.
public sealed partial class IndiClient
{
    private enum BlobPhase { Idle, Payload, Tail }

    private BlobPhase _blobPhase = BlobPhase.Idle;
    private string _blobDevice = "", _blobProp = "", _blobVectorState = "";
    private string _blobElName = "", _blobFormat = "";
    private MemoryStream? _blobBytes;
    private readonly List<(string El, string? Format, byte[] Bytes)> _blobElements = new();
    // base64 chars accumulate in 4-aligned blocks; whitespace already stripped.
    private readonly char[] _b64Block = new char[64 * 1024];
    private int _b64Len;
    private readonly StringBuilder _blobTail = new();
    private long _blobStartTicks;

    /// <summary>Entry point from the read loop. Consumes as much of `chunk`
    /// (starting at `pos`) as the current BLOB state allows; returns the new
    /// position. When it returns with phase Idle, the caller resumes the
    /// generic path from that position.</summary>
    private int ConsumeBlob(string chunk, int pos)
    {
        while (pos < chunk.Length)
        {
            switch (_blobPhase)
            {
                case BlobPhase.Payload:
                    pos = ConsumePayload(chunk, pos);
                    break;
                case BlobPhase.Tail:
                    pos = ConsumeTail(chunk, pos);
                    break;
                default:
                    return pos;
            }
            if (_blobPhase == BlobPhase.Idle) return pos;
        }
        return pos;
    }

    /// <summary>Try to recognize a BLOB-vector opening at the START of
    /// `pending` (after whitespace). On success the consumed header is
    /// removed from `pending`, any remaining content is re-routed through
    /// ConsumeBlob, and true is returned.</summary>
    private bool TryEnterBlobMode(StringBuilder pending)
    {
        if (_blobPhase != BlobPhase.Idle) return false;
        // Cheap pre-check without a full snapshot.
        if (pending.Length < 20) return false;

        var s = pending.ToString();
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (!s.AsSpan(i).StartsWith("<setBLOBVector")) return false;

        int vecEnd = s.IndexOf('>', i);
        if (vecEnd < 0) return false; // wait for more data

        var vecTag = s.Substring(i, vecEnd - i + 1);
        int j = vecEnd + 1;
        while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
        if (j >= s.Length) return false; // need the next tag to decide

        // A vector can legitimately be empty (state-only update): let the
        // generic path handle anything that isn't an immediate oneBLOB.
        if (!s.AsSpan(j).StartsWith("<oneBLOB")) return false;
        int oneEnd = s.IndexOf('>', j);
        if (oneEnd < 0) return false; // wait for the full oneBLOB open tag

        var oneTag = s.Substring(j, oneEnd - j + 1);
        var device = TagAttr(vecTag, "device");
        var prop = TagAttr(vecTag, "name");
        if (device == null || prop == null) return false; // malformed — generic path

        _blobDevice = device;
        _blobProp = prop;
        _blobVectorState = TagAttr(vecTag, "state") ?? "";
        _blobElName = TagAttr(oneTag, "name") ?? "";
        _blobFormat = TagAttr(oneTag, "format") ?? "";
        var sizeHint = long.TryParse(TagAttr(oneTag, "size"), out var sz) ? sz : 4 * 1024 * 1024;
        _blobBytes = new MemoryStream((int)Math.Min(Math.Max(sizeHint, 1024), 128L * 1024 * 1024));
        _blobElements.Clear();
        _b64Len = 0;
        _blobTail.Clear();
        _blobStartTicks = Environment.TickCount64;
        _blobPhase = BlobPhase.Payload;

        var rest = s.Substring(oneEnd + 1);
        pending.Clear();
        if (rest.Length > 0)
        {
            var consumed = ConsumeBlob(rest, 0);
            if (consumed < rest.Length) pending.Append(rest, consumed, rest.Length - consumed);
        }
        return true;
    }

    /// Accumulate base64 chars (whitespace skipped) until the closing '<'.
    private int ConsumePayload(string chunk, int pos)
    {
        while (pos < chunk.Length)
        {
            var c = chunk[pos];
            if (c == '<')
            {
                FlushB64(final: true);
                _blobPhase = BlobPhase.Tail;
                return pos;
            }
            if (!char.IsWhiteSpace(c))
            {
                _b64Block[_b64Len++] = c;
                if (_b64Len == _b64Block.Length) FlushB64(final: false);
            }
            pos++;
        }
        return pos;
    }

    private void FlushB64(bool final)
    {
        if (_b64Len == 0) return;
        // Keep a 4-aligned prefix; carry the remainder unless finalizing.
        var usable = final ? _b64Len : _b64Len - (_b64Len % 4);
        if (usable > 0 && _blobBytes != null)
        {
            try
            {
                var bytes = Convert.FromBase64CharArray(_b64Block, 0, usable);
                _blobBytes.Write(bytes, 0, bytes.Length);
            }
            catch (FormatException ex)
            {
                _log.LogWarning(ex, "INDI: streaming base64 decode failed for {D}.{P}.{E} — dropping BLOB",
                    _blobDevice, _blobProp, _blobElName);
                _blobBytes = null; // payload poisoned; tail handling still consumes the XML
            }
        }
        var remainder = _b64Len - usable;
        if (remainder > 0) Array.Copy(_b64Block, usable, _b64Block, 0, remainder);
        _b64Len = remainder;
    }

    /// After a payload: expect `</oneBLOB>`, then either another `<oneBLOB …>`
    /// (multi-element vector) or `</setBLOBVector>` to finish.
    private int ConsumeTail(string chunk, int pos)
    {
        // Accumulate a little — the closing tags are tiny.
        var take = Math.Min(chunk.Length - pos, 512);
        _blobTail.Append(chunk, pos, take);
        pos += take;

        while (true)
        {
            var t = _blobTail.ToString();
            int i = 0;
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;

            if (t.AsSpan(i).StartsWith("</oneBLOB>"))
            {
                if (_blobBytes != null)
                {
                    _blobElements.Add((_blobElName, string.IsNullOrEmpty(_blobFormat) ? null : _blobFormat, _blobBytes.ToArray()));
                }
                _blobBytes = null;
                _blobTail.Remove(0, i + "</oneBLOB>".Length);
                continue;
            }
            if (t.AsSpan(i).StartsWith("</setBLOBVector>"))
            {
                _blobTail.Remove(0, i + "</setBLOBVector>".Length);
                FinishBlobVector();
                _blobPhase = BlobPhase.Idle;
                // Push whatever trailed the vector back to the caller.
                var leftover = _blobTail.ToString();
                _blobTail.Clear();
                if (leftover.Length > 0)
                {
                    // Prepend by returning an adjusted position is impossible —
                    // stash it for the read loop via _blobLeftover instead.
                    _blobLeftover = leftover;
                }
                return pos;
            }
            if (t.AsSpan(i).StartsWith("<oneBLOB"))
            {
                int oneEnd = t.IndexOf('>', i);
                if (oneEnd < 0) break; // need more data for the open tag
                var oneTag = t.Substring(i, oneEnd - i + 1);
                _blobElName = TagAttr(oneTag, "name") ?? "";
                _blobFormat = TagAttr(oneTag, "format") ?? "";
                var sizeHint = long.TryParse(TagAttr(oneTag, "size"), out var sz) ? sz : 4 * 1024 * 1024;
                _blobBytes = new MemoryStream((int)Math.Min(Math.Max(sizeHint, 1024), 128L * 1024 * 1024));
                _b64Len = 0;
                var rest = t.Substring(oneEnd + 1);
                _blobTail.Clear();
                _blobPhase = BlobPhase.Payload;
                if (rest.Length > 0)
                {
                    var consumed = ConsumePayload(rest, 0);
                    if (_blobPhase == BlobPhase.Tail && consumed < rest.Length)
                    {
                        _blobTail.Append(rest, consumed, rest.Length - consumed);
                        continue; // keep resolving the tail we already hold
                    }
                }
                return pos;
            }
            // Unrecognized or incomplete tail: if it can't possibly match yet,
            // wait for more; if it's clearly foreign, bail out to protect the
            // connection (reconnect machinery recovers).
            if (t.Length - i > 32)
            {
                _log.LogWarning("INDI: unexpected content inside BLOB vector tail ({Head}…) — abandoning BLOB parse",
                    t.Substring(i, Math.Min(24, t.Length - i)));
                _blobPhase = BlobPhase.Idle;
                _blobTail.Clear();
                _blobBytes = null;
            }
            break;
        }
        return pos;
    }

    private string? _blobLeftover;

    private void FinishBlobVector()
    {
        var elapsedMs = Environment.TickCount64 - _blobStartTicks;
        foreach (var (el, format, bytes) in _blobElements)
        {
            _log.LogInformation("INDI BLOB streamed: {D}.{P}.{E} format={Format} bytes={Len} decodeMs={Ms}",
                _blobDevice, _blobProp, el, format, bytes.Length, elapsedMs);
        }

        // Mirror OnSetProperty's bookkeeping for the vector itself.
        IndiDevice? device;
        lock (_deviceLock) _devices.TryGetValue(_blobDevice, out device);
        if (device != null && device.Properties.TryGetValue(_blobProp, out var prop))
        {
            prop.State = ParseState(string.IsNullOrEmpty(_blobVectorState) ? null : _blobVectorState);
            prop.LastUpdated = DateTime.UtcNow;
        }

        foreach (var (el, format, bytes) in _blobElements)
        {
            try { BlobReceived?.Invoke(_blobDevice, _blobProp, el, bytes, format); }
            catch (Exception ex) { _log.LogWarning(ex, "INDI: BlobReceived handler threw"); }
        }
        _blobElements.Clear();
        if (device != null)
        {
            UpdateDriverInterface(device);
            // Guarded: this runs on the BLOB fast path, outside HandleElement's
            // try/catch — a throwing subscriber used to propagate into the read
            // loop and tear down the whole connection because an image arrived.
            try { DevicesChanged?.Invoke(); }
            catch (Exception ex) { _log.LogWarning(ex, "DevicesChanged handler threw during BLOB finish"); }
        }
    }

    /// Minimal attribute extraction from a single open tag — the tags in a
    /// BLOB header are small and driver-generated (no exotic quoting).
    private static string? TagAttr(string tag, string name)
    {
        var key = name + "=\"";
        var start = tag.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        var end = tag.IndexOf('"', start);
        if (end < 0) return null;
        return tag.Substring(start, end - start);
    }
}
