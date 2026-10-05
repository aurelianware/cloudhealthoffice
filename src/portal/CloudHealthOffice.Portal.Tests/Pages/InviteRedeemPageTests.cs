using Bunit;
using Bunit.TestDoubles;
using CloudHealthOffice.Portal.Infrastructure;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>The /invite/{code} page.</summary>
public class InviteRedeemPageTests : TestContext
{
    private const string Code = "Zk3c9QmT2xY7bL0pN4vR8sW1aE6dH5jU3fG2kM9nB0q";
    private readonly Mock<IChoTokenProvider> _tokens = new();

    public InviteRedeemPageTests()
    {
        Services.AddSingleton(_tokens.Object);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:Mode"] = "Entra" })
            .Build());
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<InviteRedeem> Render()
        => RenderComponent<InviteRedeem>(p => p.Add(x => x.Code, Code));

    [Fact]
    public void Success_RedeemsTheCodeFromTheUrl_AndReloadsHome()
    {
        _tokens.Setup(t => t.RedeemInvitationAsync(Code, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoInvitationResult.Success(new ChoTokenExchangeResponse
            {
                AccessToken = "cho", TenantId = "tenant-invited", TenantName = "Acme Health",
            }));
        var navigation = Services.GetRequiredService<FakeNavigationManager>();

        var cut = Render();

        cut.Find("[data-testid=invite-success]").TextContent.Should().Contain("Acme Health");
        navigation.Uri.Should().Be(navigation.BaseUri);
        navigation.History.First().Options.ForceLoad.Should().BeTrue();
        _tokens.Verify(t => t.RedeemInvitationAsync(Code, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(ChoInvitationStatus.Expired, "Ask your administrator to resend it")]
    [InlineData(ChoInvitationStatus.Revoked, "withdrawn")]
    [InlineData(ChoInvitationStatus.AlreadyRedeemed, "already been used")]
    [InlineData(ChoInvitationStatus.NotFound, "not valid")]
    [InlineData(ChoInvitationStatus.IdentityInUse, "already linked to another user")]
    [InlineData(ChoInvitationStatus.RateLimited, "Too many attempts")]
    [InlineData(ChoInvitationStatus.NoAccess, "not available right now")]
    [InlineData(ChoInvitationStatus.Unavailable, "Try again in a few minutes")]
    public void Refusals_ShowAClearMessage_AndStayOnThePage(ChoInvitationStatus status, string expected)
    {
        _tokens.Setup(t => t.RedeemInvitationAsync(Code, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoInvitationResult.Failure(status));
        var navigation = Services.GetRequiredService<FakeNavigationManager>();
        var start = navigation.Uri;

        var cut = Render();

        var alert = cut.Find("[data-testid=invite-error]");
        alert.TextContent.Should().Contain(expected);
        alert.GetAttribute("data-status").Should().Be(status.ToString());
        navigation.Uri.Should().Be(start);
        cut.FindAll("[data-testid=invite-switch-account]").Should().BeEmpty();
    }

    [Fact]
    public void EmailMismatch_NamesTheMaskedAccount_AndOffersToSwitchAccount()
    {
        _tokens.Setup(t => t.RedeemInvitationAsync(Code, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoInvitationResult.Failure(ChoInvitationStatus.EmailMismatch, "p***@acme.com"));

        var cut = Render();

        cut.Find("[data-testid=invite-error]").TextContent.Should().Contain("p***@acme.com");
        cut.Markup.Should().Contain("sign in as");
        cut.Find("[data-testid=invite-switch-account]").GetAttribute("href")
            .Should().Be("/MicrosoftIdentity/Account/SignOut");
    }

    [Fact]
    public void ThePageNeverPutsTheCodeInItsMarkupOrLogs()
    {
        var logs = new ListLoggerProvider();
        Services.AddLogging(b => b.AddProvider(logs));
        _tokens.Setup(t => t.RedeemInvitationAsync(Code, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoInvitationResult.Failure(ChoInvitationStatus.Expired));

        var cut = Render();

        cut.Markup.Should().NotContain(Code);
        logs.Messages.Should().NotContain(m => m.Contains(Code));
    }

    [Fact]
    public void RequestUrlLogging_IsHeldBackSoInvitationPathsAreNotLogged()
    {
        var logs = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddFilter("Microsoft.AspNetCore", LogLevel.Trace); // as verbose as configuration could make it
            b.AddProvider(logs);
            b.KeepInvitationCodesOutOfLogs();
        });

        foreach (var category in InvitationPathLogging.UrlLoggingCategories)
            factory.CreateLogger(category).LogInformation("Request starting GET https://portal/invite/{Code}", Code);
        factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").LogDebug("GET /invite/{Code}", Code);
        factory.CreateLogger("CloudHealthOffice.Portal.Other").LogInformation("still logged");

        logs.Messages.Should().NotContain(m => m.Contains(Code));
        logs.Messages.Should().Contain("still logged");
    }

    [Fact]
    public void ThePageRequiresSignIn()
    {
        typeof(InviteRedeem).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Should().NotBeEmpty();
        typeof(InviteRedeem).GetCustomAttributes(typeof(RouteAttribute), true)
            .Cast<RouteAttribute>().Select(r => r.Template).Should().Contain("/invite/{Code}");
    }
}

internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();
    public IReadOnlyList<string> Messages => _messages.ToArray();
    public ILogger CreateLogger(string categoryName) => new Logger(_messages);
    public void Dispose()
    {
    }

    private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => messages.Enqueue(formatter(state, exception));
    }
}
