using AuthorizationService.Backends;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using NSubstitute;

namespace CloudHealthOffice.AuthorizationService.Tests;

public class AuthorizationDecisionIntegrityTests
{
    private static Authorization Request() => new()
    {
        AuthorizationNumber = "line-integrity", Status = AuthorizationStatus.Submitted,
        RequestedServices = new()
        {
            new() { LineNumber = 7, ProcedureCode = "72148", RequestedUnits = 4 },
            new() { LineNumber = 12, ProcedureCode = "72148", RequestedUnits = 2 },
        },
    };
    private static IAuthorizationRepository Repository()
    {
        var repo = Substitute.For<IAuthorizationRepository>();
        repo.UpdateAsync(Arg.Any<Authorization>()).Returns(c => c.Arg<Authorization>());
        repo.CreateAsync(Arg.Any<Authorization>()).Returns(c => c.Arg<Authorization>());
        return repo;
    }

    [Theory]
    [InlineData(7, 1)]
    [InlineData(0, 1)]
    [InlineData(12, 0)]
    public async Task InvalidRequestLineIdentityOrQuantity_IsRejectedBeforeCreate(int secondNumber, decimal units)
    {
        var auth = Request(); var repo = Repository();
        auth.RequestedServices[1].LineNumber = secondNumber;
        auth.RequestedServices[1].RequestedUnits = units;
        await Assert.ThrowsAsync<ArgumentException>(() => new ChoAuthorizationBackend(repo).CreateAsync(auth));
        await repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task MixedDecision_PersistsStableLinesAndIndependentAuditSnapshots()
    {
        var auth = Request();
        var backend = new ChoAuthorizationBackend(Repository());
        await backend.UpdateStatusAsync(auth, AuthorizationStatus.Modified, "A2", null,
            details: new() { LineDecisions = new()
            {
                new() { LineNumber = 12, ReviewDecision = "A3", DenialReasonCode = "NOTMEDNEC", DenialReason = "Second service is not medically necessary." },
                new() { LineNumber = 7, ReviewDecision = "A2", ApprovedUnits = 2, DenialReasonCode = "LIMIT", DenialReason = "Only two units meet criteria." },
            }});
        Assert.Equal("A2", auth.RequestedServices[0].ServiceStatus);
        Assert.Equal(2, auth.RequestedServices[0].ApprovedUnits);
        Assert.Equal("A3", auth.RequestedServices[1].ServiceStatus);
        Assert.Equal(0, auth.RequestedServices[1].ApprovedUnits);
        Assert.Null(auth.ApprovedUnits); // Different units are never summed into an ambiguous header quantity.
        var snapshot = Assert.Single(auth.StatusHistory).LineDecisions;
        await backend.UpdateStatusAsync(auth, AuthorizationStatus.Modified, "A2", null, details: new()
        {
            LineDecisions = new()
            {
                new() { LineNumber = 7, ReviewDecision = "A2", ApprovedUnits = 3, DenialReasonCode = "LIMIT", DenialReason = "Three units now meet criteria." },
                new() { LineNumber = 12, ReviewDecision = "A3", DenialReasonCode = "NOTMEDNEC", DenialReason = "Second service remains denied." },
            },
        });
        Assert.Equal("A3", snapshot[1].ReviewDecision);
        Assert.Equal(2, snapshot[0].ApprovedUnits);
        Assert.Equal(3, auth.RequestedServices[0].ApprovedUnits);
    }

    [Theory]
    [InlineData(null, "reason")]
    [InlineData("CODE", null)]
    [InlineData(" ", "reason")]
    public async Task DenialWithoutBothReasons_IsRejectedBeforeMutationOrPersistence(string? code, string? text)
    {
        var auth = Request(); var repo = Repository();
        await Assert.ThrowsAsync<ArgumentException>(() => new ChoAuthorizationBackend(repo).UpdateStatusAsync(
            auth, AuthorizationStatus.Denied, "A3", null,
            details: new() { DenialReasonCode = code, DenialReason = text }));
        Assert.Equal(AuthorizationStatus.Submitted, auth.Status);
        Assert.Empty(auth.StatusHistory);
        await repo.DidNotReceive().UpdateAsync(Arg.Any<Authorization>());
    }

    [Theory]
    [InlineData(7, "A1", 3)] // A1 cannot silently reduce units.
    [InlineData(99, "A3", 0)] // Unknown line.
    [InlineData(7, "A2", 5)] // Overapproval.
    [InlineData(7, "A2", 0)] // A denial cannot masquerade as a zero-unit approval.
    public void InvalidLineSet_IsRejected(int number, string decision, decimal units)
    {
        var details = new AuthorizationDecisionDetails { LineDecisions = new()
        {
            new() { LineNumber = number, ReviewDecision = decision, ApprovedUnits = units, DenialReasonCode = "CODE", DenialReason = "reason" },
            new() { LineNumber = 12, ReviewDecision = "A3", DenialReasonCode = "CODE", DenialReason = "reason" },
        }};
        Assert.Throws<ArgumentException>(() => AuthorizationDecisionPolicy.Prepare(Request(), AuthorizationStatus.Modified, "A2", details));
    }

    [Fact]
    public async Task AutomatedApproval_AssignsLegacyOrdinalsAndApprovedQuantities()
    {
        var auth = Request(); auth.RequestedServices.ForEach(s => s.LineNumber = null);
        await new ChoAuthorizationBackend(Repository()).RecordAutomatedDecisionAsync(auth,
            new() { Outcome = AutomatedDecisionOutcome.Approved });
        Assert.Equal(new int?[] { 1, 2 }, auth.RequestedServices.Select(s => s.LineNumber));
        Assert.All(auth.RequestedServices, s => { Assert.Equal("A1", s.ServiceStatus); Assert.Equal(s.RequestedUnits, s.ApprovedUnits); });
    }
}
