using ASCOM.Alpaca.Discovery;
using ASCOM.Common;
using ASCOM.Common.Alpaca;

namespace NINA.Headless.Services;

/// <summary>Alpaca discovery via UDP :32227 broadcast. Runs in parallel with INDI for
/// ASCOM-only gear that doesn't speak INDI. Cameras also update the legacy
/// <see cref="CameraSelectionService"/> for unmigrated callers.</summary>
public class AlpacaDiscoveryService : BackgroundService
{
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(30);

    private readonly EquipmentSelectionService _equipment;
    private readonly CameraSelectionService _cameraSelection;
    private readonly ILogger<AlpacaDiscoveryService> _logger;

    // Change detection: remember the last-published id-set per kind so identical scans
    // (e.g. 30s ticks with nothing changed) don't fire UpdateProviderDevices and
    // re-render every consumer of EquipmentSelectionService.
    private readonly Dictionary<DeviceKind, HashSet<string>> _lastPublished = new();

    public AlpacaDiscoveryService(
        EquipmentSelectionService equipment,
        CameraSelectionService cameraSelection,
        ILogger<AlpacaDiscoveryService> logger)
    {
        _equipment = equipment;
        _cameraSelection = cameraSelection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay one tick so Kestrel finishes binding before we start spraying the LAN.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ScanOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Alpaca discovery iteration failed (non-fatal)"); }

            try { await Task.Delay(DiscoveryInterval, stoppingToken); } catch { break; }
        }
    }

    public async Task ScanOnceAsync(CancellationToken ct)
    {
        // One broadcast → all device types in parallel. The ASCOM lib handles UDP
        // sendto / receive, DNS resolution flags, IPv4/v6 filtering.
        var discoverable = AlpacaDeviceTypes.Discoverable;
        var queries = discoverable.Select(pair => AlpacaDiscovery.GetAscomDevicesAsync(
            pair.AscomType, numberOfPolls: 2, pollInterval: 200, discoveryPort: 32227,
            discoveryDuration: 2000, resolveDnsName: false, useIpV4: true,
            useIpV6: false, serviceType: ServiceType.Http, cancellationToken: ct)).ToArray();
        var results = await Task.WhenAll(queries);

        var descriptorsByKind = new Dictionary<DeviceKind, List<EquipmentDescriptor>>();
        foreach (var kind in Enum.GetValues<DeviceKind>()) descriptorsByKind[kind] = new();

        for (int i = 0; i < discoverable.Count; i++)
        {
            var (kind, ascomType) = discoverable[i];
            foreach (var d in results[i])
            {
                if (string.IsNullOrWhiteSpace(d.UniqueId)) continue;
                var id = $"alpaca:{d.UniqueId}";
                var name = string.IsNullOrWhiteSpace(d.AscomDeviceName)
                    ? $"{ascomType} @ {d.IpAddress}"
                    : $"{d.AscomDeviceName} (Alpaca)";
                descriptorsByKind[kind].Add(new EquipmentDescriptor(
                    Id: id, Name: name, Kind: kind, Provider: EquipmentProvider.Alpaca,
                    UniqueId: d.UniqueId, Host: d.IpAddress?.ToString(), Port: d.IpPort));
            }
        }

        foreach (var (kind, list) in descriptorsByKind)
        {
            var newIds = new HashSet<string>(list.Select(d => d.Id ?? ""));
            if (_lastPublished.TryGetValue(kind, out var prev) && prev.SetEquals(newIds)) continue;
            _lastPublished[kind] = newIds;
            _equipment.UpdateProviderDevices(kind, EquipmentProvider.Alpaca, list);
        }

        // Legacy camera path — same change-detection via CameraSelectionService's own dedupe.
        var cameraIdx = discoverable.ToList().FindIndex(p => p.Kind == DeviceKind.Camera);
        var cameraDescriptors = results[cameraIdx]
            .Where(d => !string.IsNullOrWhiteSpace(d.UniqueId))
            .Select(d => new CameraDescriptor(
                Id: $"alpaca:{d.UniqueId}",
                Name: string.IsNullOrWhiteSpace(d.AscomDeviceName) ? $"Alpaca Camera @ {d.IpAddress}" : d.AscomDeviceName,
                Provider: CameraProvider.Alpaca,
                Host: d.IpAddress?.ToString(), Port: d.IpPort,
                DeviceNumber: d.AlpacaDeviceNumber, UniqueId: d.UniqueId))
            .ToList();
        _cameraSelection.UpdateAlpacaDevices(cameraDescriptors);

        var total = descriptorsByKind.Values.Sum(l => l.Count);
        if (total > 0) _logger.LogInformation("Alpaca discovery: {Count} device(s) across {Kinds} kind(s)",
            total, descriptorsByKind.Count(kv => kv.Value.Count > 0));
    }
}
