using AuthorizationService.Backends;
using AuthorizationService.Models;
using CloudHealthOffice.OperatingMode;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// Backend selection and Augment-mode behavior for the authorization vertical
/// slice, against the REAL <see cref="AuthorizationBackendSelector"/>,
/// <see cref="ChoAuthorizationBackend"/>, and <see cref="QnxtAuthorizationBackend"/>.
///
/// Proves the two-dimension model:
///   - Replace  -> CHO-native backend (product capability: PASSABLE)
///   - Augment  -> configured external core; QNXT is a documented stub
///                 (integration capability: GAP) and is NEVER a silent fallback.
/// </summary>
public class AuthorizationBackendSelectionTests
{
    private static IAuthorizationBackend[] BothBackends() =>
        new IAuthorizationBackend[]
        {
            new ChoAuthorizationBackend(new InMemoryAuthorizationRepository()),
            new QnxtAuthorizationBackend(),
        };

    private static AuthorizationBackendSelector Selector(
        EngineOperatingMode mode, string augment = "qnxt", IAuthorizationBackend[]? backends = null) =>
        new(backends ?? BothBackends(),
            Options.Create(new AuthorizationBackendOptions { OperatingMode = mode, AugmentBackend = augment }));

    [Fact]
    [Trait("Scenario", "PAS-03")]
    [Trait("Backend", "Replace")]
    public void Config_ReplaceMode_SelectsChoNativeBackend()
    {
        var selector = Selector(EngineOperatingMode.Replace);

        selector.Mode.Should().Be(EngineOperatingMode.Replace);
        selector.ActiveBackendKey.Should().Be("cho");
        selector.IsAuthoritative.Should().BeTrue();
        selector.Resolve().Should().BeOfType<ChoAuthorizationBackend>();
    }

    [Fact]
    [Trait("Scenario", "PAS-03")]
    [Trait("Backend", "Augment")]
    public void Config_AugmentMode_SelectsConfiguredExternalBackend()
    {
        var selector = Selector(EngineOperatingMode.Augment, augment: "qnxt");

        selector.ActiveBackendKey.Should().Be("qnxt");
        selector.IsAuthoritative.Should().BeFalse("the external core remains authoritative in Augment mode");
        selector.Resolve().Should().BeOfType<QnxtAuthorizationBackend>();
    }

    [Fact]
    [Trait("Scenario", "PAS-03")]
    [Trait("Backend", "Augment")]
    [Trait("Kind", "GAP")]
    public async Task PAS03_Augment_QnxtBackendIsNotImplementedStub()
    {
        // GAP: creating an authorization in QNXT is per-engagement integration
        // work. The same PAS-03 workflow PASSES on the CHO backend (Replace).
        var selector = Selector(EngineOperatingMode.Augment, augment: "qnxt");
        var backend = selector.Resolve();

        var act = async () => await backend.CreateAsync(new Authorization());
        await act.Should().ThrowAsync<NotImplementedException>()
            .WithMessage("*QNXT authorization backend not yet implemented*");
    }

    [Fact]
    [Trait("Scenario", "PAS-03")]
    [Trait("Backend", "Augment")]
    public void Config_AugmentMode_NeverSilentlyFallsBackToCho()
    {
        // Augment configured, but the external backend is NOT registered.
        // The selector must fail loudly rather than serve CHO data as if the
        // external core were connected.
        var selector = Selector(
            EngineOperatingMode.Augment, augment: "qnxt",
            backends: new IAuthorizationBackend[]
            {
                new ChoAuthorizationBackend(new InMemoryAuthorizationRepository()),
            });

        var act = () => selector.Resolve();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no silent fallback to CHO*");
    }

    [Fact]
    [Trait("Scenario", "PAS-03")]
    [Trait("Backend", "Augment")]
    public void Config_InvalidAugmentBackend_FailsClearly()
    {
        // Augment pointed at an unregistered external backend (e.g. "facets"
        // before that integration exists) fails clearly.
        var selector = Selector(EngineOperatingMode.Augment, augment: "facets");

        var act = () => selector.Resolve();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not registered*");
    }
}
