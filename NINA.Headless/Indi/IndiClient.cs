using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace NINA.Headless.Indi;

/// <summary>
/// Minimal INDI protocol client. Connects to indiserver over TCP (default port 7624),
/// sends getProperties to subscribe, and parses incoming property vectors into
/// an in-memory device model.
///
/// INDI protocol notes:
///   - The stream is a sequence of top-level XML elements (defXXX, setXXX, delProperty, message)
///     concatenated together without a single enclosing root. We scan for complete
///     top-level elements and parse each as its own fragment.
///   - See https://docs.indilib.org/protocol/INDI.pdf
/// </summary>
public sealed class IndiClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _log;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;

    private readonly Dictionary<string, IndiDevice> _devices = new();
    private readonly object _deviceLock = new();

    // Ring buffer of the most recent INDI <message> events, per device. Drivers emit these
    // when they hit errors that don't map to a property state change — classic examples are
    // "Cannot connect to camera: USB timeout", "Driver version mismatch", "Permission denied
    // on /dev/ttyUSB0". Without capturing these, a failed ConnectDeviceAsync just returns
    // false and the user sees the generic "INDI driver did not become ready" — which is
    // true but useless for diagnosis. Keyed by device name, or "" for broadcast messages.
    private readonly Dictionary<string, List<(DateTime At, string Text)>> _recentMessages = new();
    private readonly object _messagesLock = new();
    private const int MaxMessagesPerDevice = 20;

    public IndiClient(string host, int port, ILogger log)
    {
        _host = host;
        _port = port;
        _log = log;
    }

    /// <summary>Fires whenever the device list or a device's key properties change.</summary>
    public event Action? DevicesChanged;

    /// <summary>Fires when an INDI BLOB vector arrives (e.g. camera CCD1 image data).
    /// (deviceName, propertyName, elementName, bytes, format). Subscribers are expected to
    /// route based on the device+property+element names.</summary>
    public event Action<string, string, string, byte[], string?>? BlobReceived;

    public bool IsConnected => _tcp?.Connected == true;

    public IReadOnlyList<IndiDevice> Devices
    {
        get { lock (_deviceLock) return _devices.Values.ToList(); }
    }

    public IndiDevice? GetDevice(string name)
    {
        lock (_deviceLock) return _devices.TryGetValue(name, out var d) ? d : null;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(_host, _port, ct);
        _stream = _tcp.GetStream();

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token));

        // Subscribe to all device property announcements.
        await SendAsync("<getProperties version=\"1.7\"/>", ct);
        _log.LogInformation("INDI: connected to {Host}:{Port}", _host, _port);
    }

    public async Task SendAsync(string xml, CancellationToken ct)
    {
        if (_stream == null) throw new InvalidOperationException("Not connected");
        var bytes = Encoding.UTF8.GetBytes(xml);
        await _stream.WriteAsync(bytes, ct);
        await _stream.FlushAsync(ct);
    }

    /// <summary>Set a Switch property element to "On"/"Off".</summary>
    public Task SetSwitchAsync(string device, string property, string element, bool on, CancellationToken ct)
    {
        var xml = $"<newSwitchVector device=\"{Escape(device)}\" name=\"{Escape(property)}\">" +
                  $"<oneSwitch name=\"{Escape(element)}\">{(on ? "On" : "Off")}</oneSwitch>" +
                  $"</newSwitchVector>";
        return SendAsync(xml, ct);
    }

    /// <summary>
    /// Set multiple elements of a Switch vector at once — required for OneOfMany rules
    /// where the driver won't accept a partial update (e.g. CONNECTION's CONNECT/DISCONNECT pair).
    /// </summary>
    public Task SetSwitchManyAsync(string device, string property, IEnumerable<(string element, bool on)> entries, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"<newSwitchVector device=\"{Escape(device)}\" name=\"{Escape(property)}\">");
        foreach (var (element, on) in entries)
        {
            sb.Append($"<oneSwitch name=\"{Escape(element)}\">{(on ? "On" : "Off")}</oneSwitch>");
        }
        sb.Append("</newSwitchVector>");
        return SendAsync(sb.ToString(), ct);
    }

    /// <summary>Set a Number property element.</summary>
    public Task SetNumberAsync(string device, string property, string element, double value, CancellationToken ct)
    {
        var v = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var xml = $"<newNumberVector device=\"{Escape(device)}\" name=\"{Escape(property)}\">" +
                  $"<oneNumber name=\"{Escape(element)}\">{v}</oneNumber>" +
                  $"</newNumberVector>";
        return SendAsync(xml, ct);
    }

    /// <summary>
    /// Wait for a device to reach the state described by <paramref name="predicate"/>.
    /// Event-driven: subscribes to DevicesChanged so we react the instant the driver publishes.
    /// Returns true if the predicate becomes satisfied within <paramref name="timeout"/>, false otherwise
    /// (includes the case where the device or property never shows up).
    /// </summary>
    public async Task<bool> AwaitDeviceStateAsync(
        string deviceName,
        Func<IndiDevice, bool> predicate,
        TimeSpan timeout,
        CancellationToken ct)
    {
        // Fast-path: already satisfied.
        var now = GetDevice(deviceName);
        if (now != null && predicate(now)) return true;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action handler = () =>
        {
            var d = GetDevice(deviceName);
            if (d != null && predicate(d)) tcs.TrySetResult(true);
        };
        DevicesChanged += handler;
        try
        {
            // Re-check after subscription to close the race window between fast-path and subscribe.
            var d2 = GetDevice(deviceName);
            if (d2 != null && predicate(d2)) return true;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var reg = cts.Token.Register(() => tcs.TrySetResult(false));
            return await tcs.Task;
        }
        finally
        {
            DevicesChanged -= handler;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _readCts?.Cancel(); } catch { }
        try { if (_readTask != null) await _readTask; } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
    }

    // -------- stream parser --------

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_stream == null) return;
        var buffer = new byte[65536];
        var pending = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await _stream.ReadAsync(buffer, ct);
                if (n == 0) { _log.LogWarning("INDI: server closed connection"); break; }

                var chunk = Encoding.UTF8.GetString(buffer, 0, n);
                pending.Append(chunk);

                // TryExtractElement is O(buffer) per call because it snapshots the whole
                // StringBuilder. For huge BLOBs (single XML element ~20 MB) that's O(N²)
                // across the read stream. Base64 payloads can't contain '>', so '>' in the
                // new chunk is a reliable "maybe complete now" signal — skip the scan
                // otherwise.
                if (chunk.IndexOf('>') < 0) continue;

                while (TryExtractElement(pending, out var fragment))
                {
                    try { HandleElement(fragment!); }
                    catch (Exception ex) { _log.LogWarning(ex, "INDI: failed to handle element"); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "INDI: read loop error"); }
    }

    /// <summary>Pulls one complete top-level XML element off the front of sb, if present.</summary>
    private static bool TryExtractElement(StringBuilder sb, out string? fragment)
    {
        fragment = null;
        var s = sb.ToString();
        int i = 0;
        // skip leading whitespace
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i >= s.Length || s[i] != '<') { sb.Remove(0, i); return false; }

        // scan for matching close — track tag depth, respect strings, and handle self-closing
        int depth = 0;
        int pos = i;
        string? rootTag = null;
        while (pos < s.Length)
        {
            if (s[pos] != '<') { pos++; continue; }
            int tagStart = pos;
            int tagEnd = s.IndexOf('>', pos);
            if (tagEnd < 0) { return false; } // incomplete

            var tag = s.Substring(tagStart, tagEnd - tagStart + 1);

            // CDATA/comment blocks — skip to their end markers
            if (tag.StartsWith("<!--"))
            {
                int end = s.IndexOf("-->", tagStart);
                if (end < 0) return false;
                pos = end + 3;
                continue;
            }
            if (tag.StartsWith("<![CDATA["))
            {
                int end = s.IndexOf("]]>", tagStart);
                if (end < 0) return false;
                pos = end + 3;
                continue;
            }

            bool isClose = tag.StartsWith("</");
            bool isSelfClose = tag.EndsWith("/>");

            if (depth == 0 && !isClose)
            {
                // Remember root tag name to match the close.
                var nameStart = 1;
                var nameEnd = 1;
                while (nameEnd < tag.Length && !char.IsWhiteSpace(tag[nameEnd]) && tag[nameEnd] != '>' && tag[nameEnd] != '/')
                    nameEnd++;
                rootTag = tag.Substring(nameStart, nameEnd - nameStart);
            }

            if (isSelfClose)
            {
                if (depth == 0)
                {
                    fragment = s.Substring(i, tagEnd - i + 1);
                    sb.Remove(0, tagEnd + 1);
                    return true;
                }
            }
            else if (isClose)
            {
                depth--;
                if (depth == 0)
                {
                    fragment = s.Substring(i, tagEnd - i + 1);
                    sb.Remove(0, tagEnd + 1);
                    return true;
                }
            }
            else
            {
                depth++;
            }

            pos = tagEnd + 1;
        }
        return false;
    }

    private void HandleElement(string xml)
    {
        XElement el;
        try { el = XElement.Parse(xml); }
        catch { return; }

        var tag = el.Name.LocalName;

        // defXxxVector / setXxxVector — property announcement or update
        if (tag.StartsWith("def") && tag.EndsWith("Vector"))
        {
            OnDefProperty(el, tag);
        }
        else if (tag.StartsWith("set") && tag.EndsWith("Vector"))
        {
            OnSetProperty(el, tag);
        }
        else if (tag == "delProperty")
        {
            OnDelProperty(el);
        }
        else if (tag == "message")
        {
            OnMessage(el);
        }
        // getProperties from other clients, etc. — ignored
    }

    private void OnMessage(XElement el)
    {
        var deviceName = (string?)el.Attribute("device") ?? string.Empty;
        var text = (string?)el.Attribute("message") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;
        var ts = DateTime.UtcNow;
        lock (_messagesLock)
        {
            if (!_recentMessages.TryGetValue(deviceName, out var list))
            {
                list = new List<(DateTime, string)>();
                _recentMessages[deviceName] = list;
            }
            list.Add((ts, text));
            if (list.Count > MaxMessagesPerDevice)
                list.RemoveRange(0, list.Count - MaxMessagesPerDevice);
        }
        _log.LogInformation("INDI message [{Device}]: {Text}", string.IsNullOrEmpty(deviceName) ? "broadcast" : deviceName, text);
    }

    /// <summary>Return driver messages for <paramref name="deviceName"/> emitted after
    /// <paramref name="since"/>. Used by ConnectDeviceAsync to attach a human-readable
    /// reason when a connect fails — drivers typically emit the failure cause via &lt;message&gt;
    /// right before leaving CONNECTION in a Busy/Alert state.</summary>
    public IReadOnlyList<string> GetMessagesSince(string deviceName, DateTime since)
    {
        lock (_messagesLock)
        {
            var result = new List<string>();
            if (_recentMessages.TryGetValue(deviceName, out var list))
                result.AddRange(list.Where(m => m.At >= since).Select(m => m.Text));
            // Also include broadcast messages — some drivers don't scope their error output
            // to the device, especially during the initial handshake.
            if (_recentMessages.TryGetValue(string.Empty, out var broadcast))
                result.AddRange(broadcast.Where(m => m.At >= since).Select(m => m.Text));
            return result;
        }
    }

    private void OnDefProperty(XElement el, string tag)
    {
        var deviceName = (string?)el.Attribute("device");
        var propName = (string?)el.Attribute("name");
        if (string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(propName)) return;

        var type = tag switch
        {
            "defSwitchVector" => IndiPropertyType.Switch,
            "defTextVector" => IndiPropertyType.Text,
            "defNumberVector" => IndiPropertyType.Number,
            "defLightVector" => IndiPropertyType.Light,
            "defBLOBVector" => IndiPropertyType.Blob,
            _ => IndiPropertyType.Text
        };

        IndiDevice device;
        lock (_deviceLock)
        {
            if (!_devices.TryGetValue(deviceName, out device!))
            {
                device = new IndiDevice { Name = deviceName };
                _devices[deviceName] = device;
            }
        }

        var prop = new IndiProperty { Device = deviceName, Name = propName, Type = type };
        prop.Label = (string?)el.Attribute("label");
        prop.Group = (string?)el.Attribute("group");
        prop.State = ParseState((string?)el.Attribute("state"));
        prop.Perm = ParsePerm((string?)el.Attribute("perm"));
        prop.Rule = ParseRule((string?)el.Attribute("rule"));

        foreach (var child in el.Elements())
        {
            var elName = (string?)child.Attribute("name");
            if (string.IsNullOrEmpty(elName)) continue;

            var ie = new IndiElement { Name = elName, Label = (string?)child.Attribute("label") };
            ie.Value = (child.Value ?? string.Empty).Trim();

            if (type == IndiPropertyType.Number)
            {
                ie.Min = ParseD((string?)child.Attribute("min"));
                ie.Max = ParseD((string?)child.Attribute("max"));
                ie.Step = ParseD((string?)child.Attribute("step"));
                ie.Format = (string?)child.Attribute("format");
            }

            prop.Elements[elName] = ie;
        }

        device.Properties[propName] = prop;
        UpdateDriverInterface(device);
        DevicesChanged?.Invoke();
    }

    private void OnSetProperty(XElement el, string tag)
    {
        var deviceName = (string?)el.Attribute("device");
        var propName = (string?)el.Attribute("name");
        if (string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(propName)) return;

        IndiDevice? device;
        lock (_deviceLock) _devices.TryGetValue(deviceName, out device);
        if (device == null) return;

        if (!device.Properties.TryGetValue(propName, out var prop)) return;

        prop.State = ParseState((string?)el.Attribute("state"));

        foreach (var child in el.Elements())
        {
            var elName = (string?)child.Attribute("name");
            if (string.IsNullOrEmpty(elName)) continue;

            // BLOB elements (<oneBLOB>) carry base64-encoded binary payloads with a "format"
            // attribute identifying the MIME/extension (e.g. ".fits"). We don't store those
            // in the in-memory model — just raise an event so camera code can pick up the
            // image without polling. Text/number/switch elements fall through as before.
            if (tag == "setBLOBVector" && child.Name.LocalName == "oneBLOB")
            {
                var format = (string?)child.Attribute("format");
                var b64 = (child.Value ?? string.Empty).Trim();
                _log.LogInformation("INDI BLOB received: {D}.{P}.{E} format={Format} b64Len={Len}", deviceName, propName, elName, format, b64.Length);
                if (b64.Length > 0)
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(b64);
                        BlobReceived?.Invoke(deviceName, propName, elName, bytes, format);
                    }
                    catch (Exception ex) { _log.LogWarning(ex, "INDI: BLOB base64 decode failed for {D}.{P}.{E}", deviceName, propName, elName); }
                }
                continue;
            }

            if (!prop.Elements.TryGetValue(elName, out var ie)) continue;
            ie.Value = (child.Value ?? string.Empty).Trim();
        }

        UpdateDriverInterface(device);
        DevicesChanged?.Invoke();
    }

    /// <summary>Ask indiserver to deliver BLOB vectors for a device. Required — by default INDI
    /// only streams non-BLOB updates to keep text clients fast.</summary>
    public Task EnableBlobAsync(string deviceName, CancellationToken ct)
    {
        var xml = $"<enableBLOB device=\"{Escape(deviceName)}\">Also</enableBLOB>";
        _log.LogInformation("INDI: enableBLOB for {Device}", deviceName);
        return SendAsync(xml, ct);
    }

    private void OnDelProperty(XElement el)
    {
        var deviceName = (string?)el.Attribute("device");
        var propName = (string?)el.Attribute("name");
        if (string.IsNullOrEmpty(deviceName)) return;

        // Temporary: trace delProperty on CONNECTION/device to diagnose the disc→conn→miss bug.
        if (string.IsNullOrEmpty(propName))
            _log.LogWarning("INDI delProperty: whole device {Device} removed", deviceName);
        else if (propName == "CONNECTION")
            _log.LogWarning("INDI delProperty: {Device}.CONNECTION removed", deviceName);

        lock (_deviceLock)
        {
            if (string.IsNullOrEmpty(propName))
            {
                _devices.Remove(deviceName);
            }
            else if (_devices.TryGetValue(deviceName, out var d))
            {
                d.Properties.TryRemove(propName, out _);
            }
        }
        DevicesChanged?.Invoke();
    }

    private static void UpdateDriverInterface(IndiDevice device)
    {
        if (!device.Properties.TryGetValue("DRIVER_INFO", out var p)) return;
        var raw = p["DRIVER_INTERFACE"]?.Value;
        if (int.TryParse(raw, out var v)) device.DriverInterface = v;
    }

    private static IndiPropertyState ParseState(string? s) => s switch
    {
        "Ok" => IndiPropertyState.Ok,
        "Busy" => IndiPropertyState.Busy,
        "Alert" => IndiPropertyState.Alert,
        _ => IndiPropertyState.Idle
    };
    private static IndiPropertyPerm ParsePerm(string? s) => s switch
    {
        "ro" => IndiPropertyPerm.ReadOnly,
        "wo" => IndiPropertyPerm.WriteOnly,
        _ => IndiPropertyPerm.ReadWrite
    };
    private static IndiSwitchRule ParseRule(string? s) => s switch
    {
        "OneOfMany" => IndiSwitchRule.OneOfMany,
        "AtMostOne" => IndiSwitchRule.AtMostOne,
        _ => IndiSwitchRule.AnyOfMany
    };
    private static double? ParseD(string? s)
        => double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string Escape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
}
