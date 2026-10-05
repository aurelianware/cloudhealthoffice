using CapitationService.Models;
using CapitationService.Repositories;

namespace CapitationService.Tests.Support;

/// <summary>
/// The conditional release writes as an uncontended repository answers them:
/// every Approved statement may start its payment, every Pending disbursement
/// may be claimed, and the in-memory record follows (like the real write).
/// </summary>
public static class ReleaseClaims
{
    public static void Uncontended(Mock<ICapitationStatementRepository> statements)
    {
        statements.Setup(r => r.TryStartPaymentAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
    }

    public static void Uncontended(Mock<ICapitationDisbursementRepository> disbursements)
    {
        disbursements.Setup(r => r.TryClaimForReleaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync(true);
    }

    public static void Uncontended(Mock<ICapitationStatementRepository> statements, Mock<ICapitationDisbursementRepository> disbursements)
    {
        Uncontended(statements);
        Uncontended(disbursements);
    }
}
