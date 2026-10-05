using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.Infrastructure.Tests.Security;

/// <summary>
/// "Testing" accepts the development keys. A Kubernetes host refuses it unless
/// the override says the in-cluster test run is deliberate.
/// </summary>
public class ChoTestingEnvironmentTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static Func<string, string?> Vars(params (string Key, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Key, v => (string?)v.Value);
        return key => map.TryGetValue(key, out var v) ? v : null;
    }

    private static readonly Func<string, string?> InKubernetes =
        Vars((ChoTestingEnvironment.KubernetesMarkerVariable, "10.0.0.1"));

    [Fact]
    public void Testing_InKubernetes_WithoutOverride_RefusesToStart()
    {
        var act = () => ChoTestingEnvironment.EnsureNotDeployed(new Env("Testing"), InKubernetes);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Kubernetes*CHO_ALLOW_TESTING_ENVIRONMENT*");

        ((Func<bool>)(() => ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env("Testing"), InKubernetes)))
            .Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("1")]
    public void Testing_InKubernetes_WithAnythingButTrue_RefusesToStart(string value)
    {
        var vars = Vars((ChoTestingEnvironment.KubernetesMarkerVariable, "10.0.0.1"), (ChoTestingEnvironment.OverrideVariable, value));
        ((Action)(() => ChoTestingEnvironment.EnsureNotDeployed(new Env("Testing"), vars))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Testing_InKubernetes_WithExplicitOverride_IsAllowed()
    {
        var vars = Vars((ChoTestingEnvironment.KubernetesMarkerVariable, "10.0.0.1"), (ChoTestingEnvironment.OverrideVariable, "true"));
        ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env("Testing"), vars).Should().BeTrue();
    }

    [Fact]
    public void Testing_OutsideKubernetes_IsAllowed()
        => ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env("Testing"), Vars()).Should().BeTrue();

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("Development", true)]
    public void OtherEnvironments_AreUnaffectedByTheMarker(string environment, bool allowed)
    {
        ((Action)(() => ChoTestingEnvironment.EnsureNotDeployed(new Env(environment), InKubernetes))).Should().NotThrow();
        ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env(environment), InKubernetes).Should().Be(allowed);
    }
}
