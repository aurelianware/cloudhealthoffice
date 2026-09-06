using FhirService.Controllers;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Reads the PAS surface Cloud Health Office actually advertises, from the real
/// <see cref="MetadataController"/>.
///
/// Same reasoning as <see cref="ChoCrdSurface"/>: a comparison against an
/// independent implementation is only worth recording when the CHO side is CHO's
/// production code. Restating CHO's operation canonicals inside the harness would
/// compare the external payer against the test's own assumptions, and would keep
/// passing after CHO's CapabilityStatement changed — which is exactly the failure
/// this surface exists to catch. It caught one: CHO advertised an
/// <c>$inquire</c> canonical no published PAS version defines.
/// </summary>
public static class ChoPasSurface
{
    /// <summary>CHO's production CapabilityStatement.</summary>
    public static CapabilityStatement CapabilityStatement()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("cho.interop.test");

        var controller = new MetadataController(new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var payload = (controller.GetCapabilityStatement() as ObjectResult)?.Value as CapabilityStatement
            ?? throw new InvalidOperationException(
                "CHO's MetadataController.GetCapabilityStatement() did not return a CapabilityStatement.");

        return payload;
    }

    /// <summary>Every operation CHO advertises on Claim — the PAS operations.</summary>
    public static IReadOnlyList<CapabilityStatement.OperationComponent> ClaimOperations() =>
        CapabilityStatement().Rest
            .SelectMany(rest => rest.Resource)
            .Where(resource => resource.Type == "Claim")
            .SelectMany(resource => resource.Operation)
            .ToList();
}
