using CloudHealthOffice.OperatingMode;
using Microsoft.Extensions.Options;

namespace AuthorizationService.Backends;

/// <summary>
/// Resolves the active <see cref="IAuthorizationBackend"/> from the configured
/// operating mode. Keeps backend selection in one place so controllers and the
/// PAS workflow never branch on vendor specifics.
/// </summary>
public interface IAuthorizationBackendSelector
{
    /// <summary>The configured operating mode.</summary>
    EngineOperatingMode Mode { get; }

    /// <summary>The active backend's key (e.g. "cho", "qnxt").</summary>
    string ActiveBackendKey { get; }

    /// <summary>True when the active backend owns the authoritative record.</summary>
    bool IsAuthoritative { get; }

    /// <summary>Resolve the backend to use for this request.</summary>
    IAuthorizationBackend Resolve();
}

/// <summary>
/// Default selector.
///
/// Replace mode  -> the CHO-native backend (<see cref="ChoAuthorizationBackend"/>).
/// Augment mode  -> the configured external backend (default "qnxt").
///
/// Selection fails clearly if the required backend is not registered, and it
/// NEVER silently substitutes the CHO backend for a configured Augment
/// integration — that would mask a missing external integration as if the core
/// were connected.
/// </summary>
public sealed class AuthorizationBackendSelector : IAuthorizationBackendSelector
{
    private readonly IReadOnlyDictionary<string, IAuthorizationBackend> _backends;
    private readonly AuthorizationBackendOptions _options;

    public AuthorizationBackendSelector(
        IEnumerable<IAuthorizationBackend> backends,
        IOptions<AuthorizationBackendOptions> options)
    {
        _options = options.Value;
        _backends = backends.ToDictionary(b => b.BackendKey, StringComparer.OrdinalIgnoreCase);
    }

    public EngineOperatingMode Mode => _options.OperatingMode;

    public string ActiveBackendKey => Mode == EngineOperatingMode.Replace
        ? ChoAuthorizationBackend.Key
        : NormalizeAugmentKey(_options.AugmentBackend);

    public bool IsAuthoritative => Resolve().IsAuthoritative;

    public IAuthorizationBackend Resolve()
    {
        var key = ActiveBackendKey;

        if (!_backends.TryGetValue(key, out var backend))
        {
            throw new InvalidOperationException(
                $"Authorization backend '{key}' is not registered for operating mode " +
                $"'{Mode}'. Configure {AuthorizationBackendOptions.SectionName}:OperatingMode " +
                $"and :AugmentBackend to a registered backend. Registered backends: " +
                $"[{string.Join(", ", _backends.Keys)}]. Replace mode requires the " +
                $"'{ChoAuthorizationBackend.Key}' backend; Augment mode requires the configured " +
                $"external backend — there is no silent fallback to CHO.");
        }

        return backend;
    }

    private static string NormalizeAugmentKey(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"Augment operating mode requires {AuthorizationBackendOptions.SectionName}:AugmentBackend " +
                "to name an external backend (e.g. 'qnxt'). It was empty; refusing to fall back to CHO.");
        }

        return configured.Trim();
    }
}
