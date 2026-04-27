namespace ProviderService.Models;

/// <summary>
/// Lifecycle state of a single <see cref="Organization"/> version document.
/// Mirrors <see cref="ProviderVersionState"/> (capability 5.1) — same Draft →
/// Active → Suspended → Superseded → Terminated state machine.
///
/// <para>String-only / no-integer enforcement is delegated to the shared
/// MVC JSON options registered by <c>AddCloudHealthOfficeJsonOptions</c>;
/// declaring a type-level converter here would override that with the lax
/// default.</para>
/// </summary>
public enum OrganizationVersionState
{
    Draft = 0,
    Active = 1,
    Suspended = 2,
    Superseded = 3,
    Terminated = 4
}
