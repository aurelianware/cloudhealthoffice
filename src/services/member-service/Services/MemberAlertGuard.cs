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

/// <summary>
/// A (type, minimum severity) pair. An alert triggers the rule when its type
/// matches and its severity is at least the minimum — so a LitigationHold
/// downgraded to Warning won't block terminate, and a DoNotContact raised at
/// Info won't gate outbound comms.
/// </summary>
internal sealed record MemberAlertRule(MemberAlertType Type, MemberAlertSeverity MinSeverity);

public sealed class MemberAlertGuard : IMemberAlertGuard
{
    private readonly IMemberAlertRepository _alerts;

    // Block rules table — single source of truth.
    // Keep in sync with docs/architecture/member-alerts-notes.md#block-rules.
    // MinSeverity is inclusive: severity >= MinSeverity triggers the block.
    private static readonly Dictionary<MemberAlertAction, MemberAlertRule[]> Rules = new()
    {
        [MemberAlertAction.Terminate] = new[]
        {
            new MemberAlertRule(MemberAlertType.LitigationHold, MemberAlertSeverity.Critical),
            new MemberAlertRule(MemberAlertType.EligibilityDispute, MemberAlertSeverity.Warning)
        },
        [MemberAlertAction.HardDelete] = new[]
        {
            new MemberAlertRule(MemberAlertType.LitigationHold, MemberAlertSeverity.Critical)
        },
        [MemberAlertAction.UpdatePii] = new[]
        {
            new MemberAlertRule(MemberAlertType.SecurityFreeze, MemberAlertSeverity.Critical),
            new MemberAlertRule(MemberAlertType.KnownFraudRisk, MemberAlertSeverity.Critical)
        },
        [MemberAlertAction.NewEnrollment] = new[]
        {
            new MemberAlertRule(MemberAlertType.KnownFraudRisk, MemberAlertSeverity.Critical)
        },
        [MemberAlertAction.OutboundCommunication] = new[]
        {
            new MemberAlertRule(MemberAlertType.DoNotContact, MemberAlertSeverity.Warning)
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
        ct.ThrowIfCancellationRequested();

        if (!Rules.TryGetValue(action, out var blockingRules)) return null;

        var active = await _alerts.ListByMemberAsync(tenantId, memberId, activeOnly: true);

        ct.ThrowIfCancellationRequested();

        var hit = active.FirstOrDefault(a =>
            blockingRules.Any(r => r.Type == a.AlertType && a.Severity >= r.MinSeverity));
        if (hit == null) return null;

        var reason = $"Action '{action}' blocked by active {hit.AlertType} alert ({hit.Severity}): {hit.Reason}";
        return new MemberAlertBlock(hit, action, reason);
    }
}
