// Compiled into every *Tests project by the repository's Directory.Build.targets.
// A "Testing" host starts only when CHO_TESTING=1 is set (see
// CloudHealthOffice.Infrastructure.Security.ChoTestingEnvironment); test
// assemblies set it for their own process before any test runs, so every
// WebApplicationFactory and test host they start may use "Testing".
// Nothing deployed sets it.
namespace CloudHealthOffice.TestingMarker;

#pragma warning disable CA2255 // A module initializer is exactly what is wanted here.
internal static class ChoTestingMarker
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Set() => System.Environment.SetEnvironmentVariable("CHO_TESTING", "1");
}
