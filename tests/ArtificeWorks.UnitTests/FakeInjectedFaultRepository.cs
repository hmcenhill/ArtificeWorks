using ArtificeWorks.Application.Chaos;
using ArtificeWorks.Application.Interfaces;

namespace ArtificeWorks.UnitTests;

/// <summary>
/// An in-memory stand-in for the injected-fault registry (12.1), shared by the chaos and inspection
/// unit tests. The real one fires once via a conditional UPDATE only Postgres can run, so the
/// routing is proved in the integration tests; this keeps the service wiring — read once, apply to
/// every unit, consume, disarm — testable without a database.
/// </summary>
internal sealed class FakeInjectedFaultRepository : IInjectedFaultRepository
{
    private sealed class Entry(Guid workOrderId, InjectedFaultKind kind)
    {
        public Guid WorkOrderId { get; } = workOrderId;
        public InjectedFaultKind Kind { get; } = kind;
        public bool Fired { get; set; }
    }

    private readonly List<Entry> _entries = [];

    public Task<InjectedFault> Arm(
        Guid workOrderId, InjectedFaultKind kind, string armedBy, CancellationToken cancellationToken = default)
    {
        _entries.Add(new Entry(workOrderId, kind));
        return Task.FromResult(new InjectedFault(
            Guid.NewGuid(), workOrderId, kind, DateTime.UtcNow, armedBy, null));
    }

    public Task<bool> TryConsume(
        Guid workOrderId, InjectedFaultKind kind, CancellationToken cancellationToken = default)
    {
        var entry = _entries.FirstOrDefault(e => e.WorkOrderId == workOrderId && e.Kind == kind && !e.Fired);
        if (entry is null)
        {
            return Task.FromResult(false);
        }
        entry.Fired = true;
        return Task.FromResult(true);
    }

    public Task<int> DisarmUnfired(CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.RemoveAll(e => !e.Fired));

    /// <summary>Test helper: is there an unfired fault of this kind for the order?</summary>
    public Task<bool> IsArmed(Guid workOrderId, InjectedFaultKind kind) =>
        Task.FromResult(_entries.Any(e => e.WorkOrderId == workOrderId && e.Kind == kind && !e.Fired));
}
