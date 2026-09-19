using Anchor.Core.Models;

namespace Anchor.Core.Services;

public interface IEventStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task AppendAsync(DerivedEvent item, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DerivedEvent>> QuerySessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}
