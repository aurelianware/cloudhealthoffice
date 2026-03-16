namespace CloudHealthOffice.OperatingMode;

/// <summary>
/// Defines the operating mode for a CHO engine within a tenant.
/// In Augment mode, CHO runs alongside a legacy system (e.g., QNXT)
/// and compares results. In Replace mode, CHO is authoritative.
/// </summary>
public interface IOperatingMode
{
    /// <summary>
    /// The current operating mode for this engine/tenant combination.
    /// </summary>
    EngineOperatingMode Mode { get; }

    /// <summary>
    /// Whether CHO's result is the authoritative (official) result.
    /// True in Replace mode; false in Augment mode.
    /// </summary>
    bool IsAuthoritative { get; }
}

/// <summary>
/// Operating mode for a CHO engine.
/// </summary>
public enum EngineOperatingMode
{
    /// <summary>
    /// CHO runs alongside a legacy system. Both results are computed
    /// and compared, but the legacy system remains authoritative.
    /// </summary>
    Augment,

    /// <summary>
    /// CHO is the authoritative system. Legacy system is not consulted.
    /// </summary>
    Replace
}

/// <summary>
/// Default implementation of IOperatingMode.
/// </summary>
public class OperatingModeInfo : IOperatingMode
{
    public EngineOperatingMode Mode { get; init; }
    public bool IsAuthoritative => Mode == EngineOperatingMode.Replace;
}
