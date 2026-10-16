using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.Infrastructure.Tests.Security;

/// <summary>
/// "Testing" accepts the development keys. It needs the explicit test marker
/// (CHO_TESTING=1, set by every test assembly), and a deployed host
/// (Kubernetes, App Service, Container Apps) refuses it unless the override
/// says the in-cluster test run is deliberate.
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

    private static readonly (string, string) Marker = (ChoTestingEnvironment.MarkerVariable, "1");

    private static readonly Func<string, string?> InKubernetes =
        Vars(Marker, (ChoTestingEnvironment.KubernetesMarkerVariable, "10.0.0.1"));

    [Fact]
    public void TestAssemblies_SetTheMarker_ForTheirOwnProcess()
        => Environment.GetEnvironmentVariable(ChoTestingEnvironment.MarkerVariable).Should().Be("1",
            "Directory.Build.targets compiles tests/Shared/ChoTestingMarker.cs into every *Tests project");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("yes")]
    public void Testing_WithoutTheMarker_RefusesToStart_EvenOffAnyPlatform(string? marker)
    {
        var vars = marker is null ? Vars() : Vars((ChoTestingEnvironment.MarkerVariable, marker));

        ((Action)(() => ChoTestingEnvironment.EnsureNotDeployed(new Env("Testing"), vars)))
            .Should().Throw<InvalidOperationException>().WithMessage("*CHO_TESTING=1*");
        ((Func<bool>)(() => ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env("Testing"), vars)))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Testing_WithoutTheMarker_RefusesToStart_EvenWithTheOverride()
    {
        var vars = Vars((ChoTestingEnvironment.OverrideVariable, "true"));
        ((Action)(() => ChoTestingEnvironment.EnsureNotDeployed(new Env("Testing"), vars))).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public void Testing_WithTheMarker_OffAnyPlatform_IsAllowed(string marker)
        => ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env("Testing"), Vars((ChoTestingEnvironment.MarkerVariable, marker)))
            .Should().BeTrue();

    [Fact]
    public void Testing_InKubernetes_WithoutOverride_RefusesToStart()
    {
        var act = () => ChoTestingEnvironment.EnsureNotDeployed(new Env("Testing"), InKubernetes);
        act.Should().Throw<InvalidOperationException>().WithMessage("*KUBERNETES_SERVICE_HOST*CHO_ALLOW_TESTING_ENVIRONMENT*");

        ((Func<bool>)(() => ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env("Testing"), InKubernetes)))
            .Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(ChoTestingEnvironment.AppServiceMarkerVariable, "cho-sponsor-service")]
    [InlineData(ChoTestingEnvironment.ContainerAppsMarkerVariable, "sponsor-service")]
    public void Testing_OnAppServiceOrContainerApps_WithoutOverride_RefusesToStart(string platformVariable, string value)
    {
        var vars = Vars(Marker, (platformVariable, value));
        ((Action)(() => ChoTestingEnvironment.EnsureNotDeployed(new Env("Testing"), vars)))
            .Should().Throw<InvalidOperationException>().WithMessage($"*{platformVariable}*");

        var allowed = Vars(Marker, (platformVariable, value), (ChoTestingEnvironment.OverrideVariable, "true"));
        ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env("Testing"), allowed).Should().BeTrue();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("1")]
    public void Testing_InKubernetes_WithAnythingButTrue_RefusesToStart(string value)
    {
        var vars = Vars(Marker, (ChoTestingEnvironment.KubernetesMarkerVariable, "10.0.0.1"), (ChoTestingEnvironment.OverrideVariable, value));
        ((Action)(() => ChoTestingEnvironment.EnsureNotDeployed(new Env("Testing"), vars))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Testing_InKubernetes_WithExplicitOverride_IsAllowed()
    {
        var vars = Vars(Marker, (ChoTestingEnvironment.KubernetesMarkerVariable, "10.0.0.1"), (ChoTestingEnvironment.OverrideVariable, "true"));
        ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env("Testing"), vars).Should().BeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ProductionAndStaging_AreUnaffectedByTheMarkers(string environment)
    {
        // Neither the test marker's absence nor a platform marker matters to them.
        foreach (var vars in new[] { Vars(), Vars((ChoTestingEnvironment.KubernetesMarkerVariable, "10.0.0.1")) })
        {
            ((Action)(() => ChoTestingEnvironment.EnsureNotDeployed(new Env(environment), vars))).Should().NotThrow();
            ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env(environment), vars).Should().BeFalse();
        }
    }

    // ── Development: allowed anywhere except a deployed host, unless overridden ──

    [Fact]
    public void Development_OffAnyPlatform_IsAllowed_WithoutAnyMarker()
    {
        ((Action)(() => ChoTestingEnvironment.EnsureNotDeployed(new Env("Development"), Vars()))).Should().NotThrow();
        ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env("Development"), Vars()).Should().BeTrue();
    }

    [Theory]
    [InlineData(ChoTestingEnvironment.KubernetesMarkerVariable, "10.0.0.1")]
    [InlineData(ChoTestingEnvironment.AppServiceMarkerVariable, "cho-member-service")]
    [InlineData(ChoTestingEnvironment.ContainerAppsMarkerVariable, "member-service")]
    public void Development_OnADeployedHost_WithoutOverride_RefusesToStart(string platformVariable, string value)
    {
        var vars = Vars((platformVariable, value));

        ((Action)(() => ChoTestingEnvironment.EnsureNotDeployed(new Env("Development"), vars)))
            .Should().Throw<InvalidOperationException>()
            .WithMessage($"*{platformVariable}*{ChoTestingEnvironment.DevelopmentOverrideVariable}=true*");
        ((Func<bool>)(() => ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env("Development"), vars)))
            .Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("1")]
    public void Development_InKubernetes_WithAnythingButTrue_RefusesToStart(string value)
    {
        var vars = Vars((ChoTestingEnvironment.KubernetesMarkerVariable, "10.0.0.1"),
            (ChoTestingEnvironment.DevelopmentOverrideVariable, value));
        ((Action)(() => ChoTestingEnvironment.EnsureNotDeployed(new Env("Development"), vars))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Development_InKubernetes_TheTestingOverride_DoesNotCount()
    {
        var vars = Vars(Marker, (ChoTestingEnvironment.KubernetesMarkerVariable, "10.0.0.1"),
            (ChoTestingEnvironment.OverrideVariable, "true"));
        ((Action)(() => ChoTestingEnvironment.EnsureNotDeployed(new Env("Development"), vars))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Development_InKubernetes_WithExplicitOverride_IsAllowed_AndWarnsOnStderr()
    {
        var vars = Vars((ChoTestingEnvironment.KubernetesMarkerVariable, "10.0.0.1"),
            (ChoTestingEnvironment.DevelopmentOverrideVariable, "true"));
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            ChoTestingEnvironment.AllowsDevelopmentSecrets(new Env("Development"), vars).Should().BeTrue();
        }
        finally
        {
            Console.SetError(original);
        }

        stderr.ToString().Should().Contain("WARNING").And.Contain(ChoTestingEnvironment.DevelopmentOverrideVariable);
    }
}
