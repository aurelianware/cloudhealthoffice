using IdCardService.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CloudHealthOffice.IdCardService.Tests;

/// <summary>
/// Storage for in-process idcard-service hosts. Program.cs refuses to start
/// without MongoDb:ConnectionString in every environment, so the host gets a
/// connection string for a server that is never there, and the repositories
/// are swapped for the in-memory test doubles; nothing reaches MongoDB.
/// </summary>
internal static class IdCardTestHost
{
    /// <summary>A MongoDB address that is never listening; the driver connects lazily.</summary>
    public const string UnreachableMongo =
        "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100&connectTimeoutMS=100";

    public static IWebHostBuilder UseInMemoryStorage(this IWebHostBuilder builder)
    {
        // A host setting, not app configuration: Program.cs reads it while
        // registering services, before app-configuration callbacks apply.
        builder.UseSetting("MongoDb:ConnectionString", UnreachableMongo);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIdCardOrderRepository>();
            services.RemoveAll<IIdCardRecordRepository>();
            services.RemoveAll<IIdCardTemplateRepository>();
            services.AddSingleton<IIdCardOrderRepository, InMemoryIdCardOrderRepository>();
            services.AddSingleton<IIdCardRecordRepository, InMemoryIdCardRecordRepository>();
            services.AddSingleton<IIdCardTemplateRepository, InMemoryIdCardTemplateRepository>();
        });
        return builder;
    }
}
