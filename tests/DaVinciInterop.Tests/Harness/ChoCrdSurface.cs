using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Reads the CRD surface Cloud Health Office actually advertises, from the real
/// <see cref="CrdController"/>.
///
/// Comparisons against an external implementation are only worth recording if the
/// CHO side is CHO's production code. Restating CHO's service ids in the test
/// would compare the external payer against a copy of the test's own assumptions,
/// and would keep passing after CHO's discovery changed. Instantiating the
/// controller follows the same idiom the CMS acceptance suite uses.
/// </summary>
public static class ChoCrdSurface
{
    /// <summary>CHO's CDS Hooks discovery document, in the harness's shared shape.</summary>
    public static CdsHooksDiscovery Discovery()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("cho.interop.test");

        var controller = new CrdController(
            new NoOpCrdService(),
            Options.Create(new CrdConfig()),
            NullLogger<CrdController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var payload = (controller.Discovery() as ObjectResult)?.Value as CrdDiscoveryResponse
            ?? throw new InvalidOperationException(
                "CHO's CrdController.Discovery() did not return a CrdDiscoveryResponse.");

        return new CdsHooksDiscovery
        {
            Services = payload.Services
                .Select(service => new CdsHooksService
                {
                    Id = service.Id,
                    Hook = service.Hook,
                    Title = service.Title,
                    Description = service.Description,
                    Prefetch = service.Prefetch is null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(service.Prefetch),
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Satisfies the controller's constructor for the discovery endpoint, which
    /// does not touch the service. Every member throws so this can never
    /// accidentally become a stand-in for CHO's real CRD evaluation — the harness
    /// compares advertised surfaces, never decisions computed by a stub.
    /// </summary>
    private sealed class NoOpCrdService : ICrdService
    {
        private static InvalidOperationException NotSupported() => new(
            "ChoCrdSurface reads CHO's advertised CRD discovery only. Evaluating a CRD hook through a stub " +
            "would compare the external payer against a fake; run CHO's own service if a decision is needed.");

        public Task<CrdEvaluationResult> EvaluateCoverageRequirementsAsync(
            CrdHookRequest request, string tenantId, CancellationToken ct = default) => throw NotSupported();

        public CrdCodeClassification GetClassification(string tenantId) => throw NotSupported();

        public CrdCodeClassification? GetClassificationOrNull(string tenantId) => throw NotSupported();

        public void SetClassification(string tenantId, CrdCodeClassification classification) => throw NotSupported();
    }
}
