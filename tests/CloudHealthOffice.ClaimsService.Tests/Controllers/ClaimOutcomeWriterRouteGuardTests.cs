using System.Reflection;
using ClaimsService.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// Guard: the adjudication pipeline (consumer → ClaimAdjudicationOrchestrator
/// → PersistenceStage) is the only thing that sets a claim's adjudicated
/// outcome. Every mutating claims-service route (PUT / POST / PATCH /
/// DELETE) is enumerated by reflection and must appear in the reviewed
/// allow-list below, which says whether it can change a claim's status or
/// outcome and through which legitimate path. A new writer — or a renamed
/// one — fails this test until someone adds it here on purpose.
/// </summary>
public class ClaimOutcomeWriterRouteGuardTests
{
    private enum Kind
    {
        /// <summary>Changes no claim's status or adjudicated outcome.</summary>
        NoStatusChange,

        /// <summary>Changes status or outcome, through a reviewed path.</summary>
        StatusOrOutcomeWriter,
    }

    private static readonly IReadOnlyDictionary<string, (Kind Kind, string Why)> AllowList =
        new Dictionary<string, (Kind, string)>(StringComparer.Ordinal)
        {
            // ── Status / outcome writers ────────────────────────────────
            ["PUT api/claims/{id}/status"] = (Kind.StatusOrOutcomeWriter,
                "operational statuses only (Received / InAdjudication, Voided with claims:void); outcome statuses are 422"),
            ["POST api/claims/{id}/remittance"] = (Kind.StatusOrOutcomeWriter, "remittance / finalize: Paid or remittance denial"),
            ["POST api/claims/{id}/inbound-remittance"] = (Kind.StatusOrOutcomeWriter, "inbound payer 835"),
            ["POST api/claims/{id}/void"] = (Kind.StatusOrOutcomeWriter, "void (claims:void)"),
            ["DELETE api/claims/{id}"] = (Kind.StatusOrOutcomeWriter, "void (claims:void)"),
            ["POST api/claims/work-queue/{claimId}/override"] = (Kind.StatusOrOutcomeWriter, "examiner override; re-runs the pipeline"),
            ["POST api/claims/work-queue/{claimId}/resolve"] = (Kind.StatusOrOutcomeWriter, "examiner resolve; re-runs the pipeline"),
            ["POST api/claims/admin/accumulators/accumulator-commit-pends/rerun"] = (Kind.StatusOrOutcomeWriter, "accumulator pend re-run through the pipeline"),
            ["POST api/claims/admin/accumulators/prior-auth-unavailable-pends/rerun"] = (Kind.StatusOrOutcomeWriter, "prior-auth pend re-run through the pipeline"),
            ["POST api/v1/claims/{predecessorClaimId}/adjustments"] = (Kind.StatusOrOutcomeWriter, "adjustment: new version supersedes the predecessor"),
            ["POST api/claims/admin/lifecycle/deny-backfill"] = (Kind.StatusOrOutcomeWriter, "one-time opt-in backfill: unremitted legacy Denied -> Deny (same adjudicated outcome, non-final phase)"),
            ["POST api/v1/claims/import/cpcds"] = (Kind.StatusOrOutcomeWriter,
                "CPCDS import (claims:import): stores outcomes the plan's core adjudicated, never a CHO-adjudicated claim; " +
                "only claims carrying ExternalSource, a changed source claim is a new version superseding the old"),
            ["POST api/v1/claims/import/cpcds/drops/run"] = (Kind.StatusOrOutcomeWriter,
                "CPCDS drop run (claims:import): the same CPCDS import, on the files waiting in the tenant's drop"),

            // ── No status / outcome change ──────────────────────────────
            ["POST api/claims"] = (Kind.NoStatusChange, "legacy submission: creates a Submitted claim; the pipeline adjudicates it"),
            ["POST api/v1/claims"] = (Kind.NoStatusChange, "submission: creates a Submitted claim; the pipeline adjudicates it"),
            ["POST api/v1/claims/import/raw837"] = (Kind.NoStatusChange, "837 import: creates Submitted claims"),
            ["POST api/v1/claims/import/raw837/validate"] = (Kind.NoStatusChange, "validation only"),
            ["POST api/claims/search"] = (Kind.NoStatusChange, "read"),
            ["POST api/claims/authorization-usage"] = (Kind.NoStatusChange, "read: claim counts per prior authorization number"),
            ["POST api/claims/finance/ibnr"] = (Kind.NoStatusChange, "read: paid-claims lag triangle and IBNR estimate (POST for the member-month body)"),
            ["PUT api/claims/{id}/ai-examination"] = (Kind.NoStatusChange, "advisory AI recommendation"),
            ["POST api/claims/{id}/ai-examination/agreement"] = (Kind.NoStatusChange, "examiner feedback on the AI recommendation"),
            ["POST api/claims/{claimId}/notes"] = (Kind.NoStatusChange, "appends a reviewer note; never touches the claim"),
            ["POST api/claims/work-queue/{claimId}/assign"] = (Kind.NoStatusChange, "work-queue assignment"),
            ["POST api/claims/work-queue/{claimId}/accumulator-adjustment/resolve"] = (Kind.NoStatusChange, "clears an accumulator clamp review"),
            ["POST api/mass-adjudication/runs"] = (Kind.NoStatusChange, "stores a run summary"),
            ["PUT api/v1/claims/import/cpcds/drops/settings"] = (Kind.NoStatusChange,
                "saves the tenant's CPCDS drop settings (claims:import); writes no claim. Loads go through drops/run and the worker"),
            ["POST api/v1/claims/shadow-adjudication/runs"] = (Kind.NoStatusChange,
                "shadow adjudication: re-adjudicates imported core claims through read-only stages only (no Persistence), " +
                "stores comparisons in their own collection; the claim is never written"),
            ["PUT api/claims/extracts/claims"] = (Kind.NoStatusChange,
                "saves the tenant's warehouse extract settings (extracts:manage); writes no claim"),
            ["POST api/claims/extracts/claims/runs"] = (Kind.NoStatusChange,
                "starts a warehouse extract: reads claims and writes files to the tenant's drop; the claim is never written"),
            ["POST api/v1/claims/tpl-recoveries/sync"] = (Kind.NoStatusChange,
                "opens Medicaid TPL recovery cases in their own collection from paid claims; reads claims only"),
            ["POST api/v1/claims/tpl-recoveries/retroactive"] = (Kind.NoStatusChange,
                "opens TPL recovery cases for paid claims that later-found TPL covers; reads claims only"),
            ["POST api/v1/claims/tpl-recoveries/{id}/events"] = (Kind.NoStatusChange,
                "records a bill, payment, repayment plan or write-off on a recovery case; never touches the claim"),
            ["POST api/v1/claims/tpl-recoveries/apply-clocks"] = (Kind.NoStatusChange,
                "hands expired recovery cases to the state; never touches the claim"),
        };

    [Fact]
    public void Every_mutating_claims_route_is_reviewed()
    {
        var actual = MutatingRoutes().ToHashSet(StringComparer.Ordinal);

        actual.Should().BeEquivalentTo(AllowList.Keys,
            "every mutating claims-service route must be reviewed here — the adjudication pipeline is the " +
            "only writer of an adjudicated outcome, so a new status/outcome writer needs an explicit decision");
    }

    [Fact]
    public void Status_and_outcome_writers_are_exactly_the_reviewed_set()
    {
        AllowList.Where(kv => kv.Value.Kind == Kind.StatusOrOutcomeWriter).Select(kv => kv.Key)
            .Should().BeEquivalentTo(
                "PUT api/claims/{id}/status",
                "POST api/claims/{id}/remittance",
                "POST api/claims/{id}/inbound-remittance",
                "POST api/claims/{id}/void",
                "DELETE api/claims/{id}",
                "POST api/claims/work-queue/{claimId}/override",
                "POST api/claims/work-queue/{claimId}/resolve",
                "POST api/claims/admin/accumulators/accumulator-commit-pends/rerun",
                "POST api/claims/admin/accumulators/prior-auth-unavailable-pends/rerun",
                "POST api/v1/claims/{predecessorClaimId}/adjustments",
                "POST api/claims/admin/lifecycle/deny-backfill",
                "POST api/v1/claims/import/cpcds",
                "POST api/v1/claims/import/cpcds/drops/run");
    }

    [Theory]
    [InlineData("PUT api/claims/{id}/adjudication")]
    [InlineData("PUT api/claims/{id}/adjudication-summary")]
    [InlineData("PUT api/claims/{id}/pend")]
    public void Removed_adjudication_side_doors_stay_removed(string route)
    {
        MutatingRoutes().Should().NotContain(route);
    }

    private static IEnumerable<string> MutatingRoutes()
    {
        var controllers = typeof(ClaimsController).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t));

        foreach (var controller in controllers)
        {
            var classTemplate = controller.GetCustomAttribute<RouteAttribute>(inherit: true)?.Template;
            var controllerName = controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? controller.Name[..^"Controller".Length]
                : controller.Name;

            foreach (var method in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                foreach (var http in method.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
                {
                    foreach (var verb in http.HttpMethods.Where(IsMutating))
                    {
                        yield return $"{verb} {Combine(classTemplate, http.Template, controllerName)}";
                    }
                }
            }
        }
    }

    private static bool IsMutating(string verb) =>
        verb is "PUT" or "POST" or "PATCH" or "DELETE";

    private static string Combine(string? classTemplate, string? methodTemplate, string controllerName)
    {
        string route;
        if (methodTemplate is not null && (methodTemplate.StartsWith('/') || methodTemplate.StartsWith("~/", StringComparison.Ordinal)))
            route = methodTemplate.TrimStart('~', '/');
        else if (string.IsNullOrEmpty(classTemplate))
            route = methodTemplate ?? string.Empty;
        else if (string.IsNullOrEmpty(methodTemplate))
            route = classTemplate;
        else
            route = $"{classTemplate.TrimEnd('/')}/{methodTemplate}";

        return route.Replace("[controller]", controllerName, StringComparison.Ordinal).ToLowerInvariantPreservingParameters();
    }
}

internal static class RouteTemplateCasing
{
    /// <summary>Lower-cases literal segments (routing is case-insensitive) but keeps <c>{parameter}</c> names as written.</summary>
    public static string ToLowerInvariantPreservingParameters(this string route) =>
        string.Join('/', route.Split('/').Select(segment => segment.StartsWith('{') ? segment : segment.ToLowerInvariant()));
}
