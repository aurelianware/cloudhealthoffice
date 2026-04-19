using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemberService.Models;
using MemberService.Repositories;

namespace MemberService.Services;

/// <summary>
/// Service-layer enforcement of alert-based block rules. Centralised so the
/// rules table lives in one place and individual controllers don't drift.
/// See <c>docs/architecture/member-alerts-notes.md#block-rules</c>.
/// </summary>
public interface IMemberAlertGuard
{
    /// <summary>
    /// Evaluate whether <paramref name="action"/> is permitted for the given
    /// member. Returns the first violating active alert (if any).
    /// </summary>
    Task<MemberAlertBlock?> EvaluateAsync(
        string tenantId,
        string memberId,
        MemberAlertAction action,
        CancellationToken ct = default);
}

public enum MemberAlertAction
{
    Terminate = 1,
    HardDelete = 2,
    UpdatePii = 3,
    NewEnrollment = 4,
    OutboundCommunication = 5
}

/// <summary>
/// A specific alert that blocks a specific action. The reason / required action
/// come straight from the offending <see cref="MemberAlert"/>.
/// </summary>
public sealed record MemberAlertBlock(
    MemberAlert Alert,
    MemberAlertAction Action,
    string Reason);

public sealed class MemberAlertGuard : IMemberAlertGuard
{
    private readonly IMemberAlertRepository _alerts;

    // Block rules table — single source of truth.
    // Keep in sync with docs/architecture/member-alerts-notes.md#block-rules.
    private static readonly Dictionary<MemberAlertAction, MemberAlertType[]> Rules = new()
    {
        [MemberAlertAction.Terminate] = new[]
        {
            MemberAlertType.LitigationHold,
            MemberAlertType.EligibilityDispute
        },
        [MemberAlertAction.HardDelete] = new[]
        {
            MemberAlertType.LitigationHold
        },
        [MemberAlertAction.UpdatePii] = new[]
        {
            MemberAlertType.SecurityFreeze,
            MemberAlertType.KnownFraudRisk
        },
        [MemberAlertAction.NewEnrollment] = new[]
        {
            MemberAlertType.KnownFraudRisk
        },
        [MemberAlertAction.OutboundCommunication] = new[]
        {
            MemberAlertType.DoNotContact
        }
    };

    public MemberAlertGuard(IMemberAlertRepository alerts)
    {
        _alerts = alerts;
    }

    public async Task<MemberAlertBlock?> EvaluateAsync(
        string tenantId,
        string memberId,
        MemberAlertAction action,
        CancellationToken ct = default)
    {
        if (!Rules.TryGetValue(action, out var blockingTypes)) return null;

        var active = await _alerts.ListByMemberAsync(tenantId, memberId, activeOnly: true);
        var hit = active.FirstOrDefault(a => blockingTypes.Contains(a.AlertType));
        if (hit == null) return null;

        var reason = $"Action '{action}' blocked by active {hit.AlertType} alert: {hit.Reason}";
        return new MemberAlertBlock(hit, action, reason);
    }
}
