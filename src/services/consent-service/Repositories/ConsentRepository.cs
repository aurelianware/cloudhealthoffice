using System.Net;
using ConsentService.Models;

namespace ConsentService.Repositories;

/// <summary>
/// Repository-local sink for appending <see cref="ConsentEvent"/> rows.
/// Lets the Cosmos and Mongo <see cref="IConsentRepository"/> implementations
/// share a single transition-and-append shape while keeping their own
/// storage choice for audit rows.
/// </summary>
public interface IConsentEventSink
{
    Task AppendAsync(ConsentEvent evt);
}
