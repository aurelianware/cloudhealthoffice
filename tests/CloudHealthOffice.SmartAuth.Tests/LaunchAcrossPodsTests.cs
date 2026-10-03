using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// Two smart-auth-service hosts on one database, as two pods would be: a
/// launch registered through one is used through the other, exactly once.
/// HTTP only, so it runs unchanged against the previous in-memory store.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class LaunchAcrossPodsTests
{
    private const string Redirect = "http://localhost:5000/smart/callback";
    private const string Scope = "openid launch user/*.read";

    private readonly SmartAuthTestFixture _fixture;

    public LaunchAcrossPodsTests(SmartAuthTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ALaunchRegisteredOnOnePod_IsUsedOnAnother_ExactlyOnce()
    {
        await using var otherPod = _fixture.Factory.WithWebHostBuilder(_ => { });
        var podA = new SmartAuthDriver(_fixture.Factory);
        var podB = new SmartAuthDriver(otherPod);

        var launch = await RegisterAsync(podA);

        var first = await SmartAuthDriver.AuthorizeAsync(
            await podB.SignInAsync("demo-provider"), "cho-ehr-app", Scope, launch: launch, redirectUri: Redirect);
        first.Error.Should().BeNull("the launch was registered through the other pod");
        first.Code.Should().NotBeNull();

        var replay = await SmartAuthDriver.AuthorizeAsync(
            await podA.SignInAsync("demo-provider"), "cho-ehr-app", Scope, launch: launch, redirectUri: Redirect);
        replay.Code.Should().BeNull("a launch is single use on every pod");
        replay.Error.Should().Be("access_denied");
    }

    [Fact]
    public async Task ConcurrentAuthorizations_WithOneLaunch_YieldOneCode()
    {
        await using var otherPod = _fixture.Factory.WithWebHostBuilder(_ => { });
        var pods = new[] { new SmartAuthDriver(_fixture.Factory), new SmartAuthDriver(otherPod) };
        var launch = await RegisterAsync(pods[0]);

        var browsers = new List<(HttpClient Browser, int Pod)>();
        for (var i = 0; i < 6; i++)
            browsers.Add((await pods[i % 2].SignInAsync("demo-provider"), i % 2));

        var outcomes = await Task.WhenAll(browsers.Select(b =>
            SmartAuthDriver.AuthorizeAsync(b.Browser, "cho-ehr-app", Scope, launch: launch, redirectUri: Redirect)));

        outcomes.Count(o => o.Code != null).Should().Be(1);
        outcomes.Where(o => o.Code == null).Should().OnlyContain(o => o.Error == "access_denied");
    }

    private static async Task<string> RegisterAsync(SmartAuthDriver pod)
    {
        var resp = await pod.Admin("demo-tenant", ChoRolePermissions.MemberServices)
            .PostAsJsonAsync("/launch", new { patientId = "pat-001", clientId = "cho-ehr-app" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("launch").GetString()!;
    }
}
