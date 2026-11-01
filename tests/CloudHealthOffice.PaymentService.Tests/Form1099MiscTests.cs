using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using PaymentService.Controllers;
using PaymentService.Repositories;
using PaymentService.Services.InformationReturns;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// 1099-MISC box 6 by payee TIN (gap 4 of the Medicaid core assessment): a
/// plan's year of provider payments, net of reversals, totalled across every
/// NPI billing under one TIN, against the year's threshold.
/// </summary>
public class Form1099MiscTests
{
    private static DateTime D(int y, int m, int d = 10) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<string, PayeeTaxIdentity?> Identities = new()
    {
        ["1111111111"] = new("1111111111", "74-1234567", "EIN", "Gulf Coast Pediatrics PA", true),
        ["2222222222"] = new("2222222222", "741234567", "EIN", "Gulf Coast Pediatrics PA", true),   // same TIN, other NPI
        ["3333333333"] = new("3333333333", "123-45-6789", "SSN", "Ana Ruiz MD", false),
        ["4444444444"] = new("4444444444", null, null, "No W-9 Clinic", false),
        ["5555555555"] = null,
    };

    [Theory]
    [InlineData(2025, 600)]
    [InlineData(2026, 2000)]
    public void Threshold_FollowsTheTaxYear(int year, decimal expected) =>
        Assert.Equal(expected, Form1099MiscBuilder.Threshold(year));

    [Fact]
    public void Totals_ByTin_NetOfReversals_AgainstTheThreshold()
    {
        var payments = new[]
        {
            new IssuedPayment("1111111111", "GCP North", 1500m, D(2026, 2), false),
            new IssuedPayment("2222222222", "GCP South", 900m, D(2026, 5), false),
            new IssuedPayment("1111111111", "GCP North", -300m, D(2026, 8), true),     // recoupment
            new IssuedPayment("3333333333", "Ana Ruiz", 1999.99m, D(2026, 3), false),  // under $2,000
            new IssuedPayment("3333333333", "Ana Ruiz", 5000m, D(2025, 12, 31), false), // other year
            new IssuedPayment("4444444444", "No W-9 Clinic", 2500m, D(2026, 4), false),
            new IssuedPayment("5555555555", "Unknown", 75m, D(2026, 4), false),
        };

        var s = Form1099MiscBuilder.Build(2026, payments, Identities);

        var gcp = s.Recipients.Single(r => r.TinLast4 == "4567");
        Assert.Equal(2100m, gcp.Box6MedicalPayments);
        Assert.Equal(new[] { "1111111111", "2222222222" }, gcp.Npis);
        Assert.True(gcp.Reportable);
        Assert.Equal("1111111111", gcp.RecipientKey);

        var ruiz = s.Recipients.Single(r => r.TinLast4 == "6789");
        Assert.Equal(1999.99m, ruiz.Box6MedicalPayments);
        Assert.False(ruiz.Reportable);

        Assert.Equal(2, s.Exceptions.Count);
        Assert.Contains(s.Exceptions, e => e.PayeeNpi == "4444444444" && e.Reason.Contains("W-9"));
        Assert.Contains(s.Exceptions, e => e.PayeeNpi == "5555555555");
        Assert.Equal(1, s.ReportableCount);
        Assert.Equal(2100m, s.ReportableTotal);
    }

    [Fact]
    public async Task Endpoint_LooksUpEachNpiOnce_AndNeverReturnsTheWholeTin()
    {
        var repo = Substitute.For<IPaymentRepository>();
        repo.GetIssuedPaymentsAsync(D(2026, 1, 1), D(2027, 1, 1), Arg.Any<CancellationToken>()).Returns(new[]
        {
            new IssuedPayment("1111111111", "GCP", 1500m, D(2026, 2), false),
            new IssuedPayment("1111111111", "GCP", 1500m, D(2026, 3), false),
        });
        var source = Substitute.For<IProviderTaxIdentitySource>();
        source.GetAsync("tenant-1", "1111111111", Arg.Any<CancellationToken>()).Returns(Identities["1111111111"]);
        var actor = Substitute.For<ICurrentActor>();
        actor.TenantId.Returns("tenant-1");
        var controller = new InformationReturnsController(repo, source, actor);

        var csv = await controller.Csv(2026, default);

        var text = System.Text.Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(csv).FileContents);
        Assert.Contains(",4567,EIN,Gulf Coast Pediatrics PA,1111111111,3000.00,2,Y,Y", text);
        Assert.DoesNotContain("741234567", text);
        Assert.DoesNotContain("74-1234567", text);
        await source.Received(1).GetAsync("tenant-1", "1111111111", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenProviderServiceCannotAnswer_NothingIsTotalled()
    {
        var repo = Substitute.For<IPaymentRepository>();
        repo.GetIssuedPaymentsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new IssuedPayment("1111111111", "GCP", 1500m, D(2026, 2), false) });
        var source = Substitute.For<IProviderTaxIdentitySource>();
        source.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<PayeeTaxIdentity?>(_ => throw new HttpRequestException("down"));
        var controller = new InformationReturnsController(repo, source, Substitute.For<ICurrentActor>());

        var result = await controller.Get(2026, default);

        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
    }
}
