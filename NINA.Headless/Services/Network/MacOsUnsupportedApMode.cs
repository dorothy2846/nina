namespace NINA.Headless.Services.Network;

/// <summary>macOS has no programmatic API to enable its "Internet Sharing" feature since
/// Big Sur (11). Returning a clear unsupported message is all we can do; the iOS app
/// surfaces step-by-step manual instructions (System Settings → General → Sharing → Internet Sharing).</summary>
public class MacOsUnsupportedApMode : IApModeProvider
{
    public Task<ApCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
        Task.FromResult(new ApCapabilities(
            Supported: false,
            UnsupportedReason: "macOS는 AP 모드 자동 제어가 불가능합니다. 시스템 설정 → 일반 → 공유 → 인터넷 공유에서 수동으로 켜주세요.",
            InterfaceName: null));

    public Task<ApModeStatus> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new ApModeStatus(false, null, null, "수동 활성화 필요"));

    public Task<bool> EnableAsync(ApModeConfig config, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> DisableAsync(CancellationToken ct) => Task.FromResult(false);
}
