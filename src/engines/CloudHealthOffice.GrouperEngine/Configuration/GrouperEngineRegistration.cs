using CloudHealthOffice.GrouperEngine.Data;
using CloudHealthOffice.GrouperEngine.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.GrouperEngine.Configuration;

/// <summary>
/// DI registration for the grouper engine.
///
/// <para>
/// Which grouper is bound is a deployment decision (ADR 024 §4), so the
/// registration names it:
/// </para>
/// <code>
///   builder.Services.AddGrouperEngine().UseTableDrivenMsDrgGrouper();
/// </code>
/// <para>
/// Until the definitions-manual importer lands, a host that wants the
/// grouper to answer at all in development also calls
/// <c>UseSeedTables()</c>. A production host must not: with no table
/// provider registered the grouper returns
/// <c>GroupingStatus.NoTablesForDischargeDate</c>, which is the correct
/// answer when no CMS version has been loaded.
/// </para>
/// </summary>
public static class GrouperEngineServiceCollectionExtensions
{
    /// <summary>Registers the grouper engine. Chain a <c>Use…</c> call to bind an implementation.</summary>
    public static GrouperEngineBuilder AddGrouperEngine(this IServiceCollection services) => new(services);

    /// <summary>Fluent builder for the grouper engine's bindings.</summary>
    public sealed class GrouperEngineBuilder
    {
        private readonly IServiceCollection _services;

        internal GrouperEngineBuilder(IServiceCollection services) => _services = services;

        /// <summary>
        /// Binds the platform's own table-driven MS-DRG grouper. Requires an
        /// <see cref="IMsDrgTableProvider"/>; register one with
        /// <see cref="UseSeedTables"/> or (next phase) the Mongo provider.
        /// </summary>
        public GrouperEngineBuilder UseTableDrivenMsDrgGrouper()
        {
            _services.AddScoped<IMsDrgGrouper, TableDrivenMsDrgGrouper>();
            return this;
        }

        /// <summary>
        /// Serves the hand-built development slice in
        /// <see cref="MsDrgSeedTables"/>. Development and test hosts only —
        /// see that type's remarks.
        /// </summary>
        public GrouperEngineBuilder UseSeedTables()
        {
            _services.AddSingleton<IMsDrgTableProvider>(
                _ => new InMemoryMsDrgTableProvider(MsDrgSeedTables.Build()));
            return this;
        }
    }
}
