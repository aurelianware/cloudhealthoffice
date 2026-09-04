using CloudHealthOffice.OperatingMode;

namespace AuthorizationService.Backends;

/// <summary>
/// Configuration for authorization backend selection. Bound from the
/// <c>Cms0057:Authorization</c> section.
///
/// <code>
/// "Cms0057": {
///   "Authorization": {
///     "OperatingMode": "Replace",   // Replace (CHO-native) | Augment (external core)
///     "AugmentBackend": "qnxt"      // which external backend when Augment
///   }
/// }
/// </code>
///
/// Defaults to <see cref="EngineOperatingMode.Replace"/> — consistent with
/// <c>OperatingModeConfiguration.GetEngineMode</c>, which also defaults engines
/// to Replace (Cloud Health Office authoritative). A deployment configured for
/// Augment never silently falls back to the CHO backend; selection fails loudly
/// if the configured external backend is not registered.
/// </summary>
public sealed class AuthorizationBackendOptions
{
    public const string SectionName = "Cms0057:Authorization";

    /// <summary>Replace = CHO-native authoritative; Augment = external core authoritative.</summary>
    public EngineOperatingMode OperatingMode { get; set; } = EngineOperatingMode.Replace;

    /// <summary>Backend key used when <see cref="OperatingMode"/> is Augment.</summary>
    public string AugmentBackend { get; set; } = QnxtAuthorizationBackend.Key;
}
