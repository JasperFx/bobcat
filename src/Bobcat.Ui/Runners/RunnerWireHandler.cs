using Bobcat.Ui.Repositories;

namespace Bobcat.Ui.Runners;

/// <summary>
/// The runner wire's inbound messages, as ordinary Wolverine handlers (stoat#77). They arrive as
/// CloudEvents on <c>POST /api/runners/events</c>, are mapped by Wolverine's CloudEventsMapper, pass
/// <see cref="RunnerWire.Inbound"/>, and land here. Being handled is also what makes each type known
/// to Wolverine's handler graph, which is the table the mapper resolves a CloudEvents <c>type</c> in.
/// </summary>
public static class RunnerWireHandler
{
    /// <summary>
    /// A runner registered, or registered again after a rebuild. Its specifications are the suite's
    /// current ones, so any its spec half still binds that it no longer has are dropped from the
    /// canvas (bobcat#652) — a renamed spec used to stay bound to its slice until a run republished.
    /// </summary>
    public static async Task Handle(RunnerRegistered registered, RunnerRegistry runners, IRepositoryIdentity identity,
        EventModel.EventModelStore model, Wolverine.IMessageBus bus)
    {
        runners.Register(registered, identity.SlugFor(registered.Repository));

        if (registered.Suite is { Length: > 0 } suite && registered.Specs.Count > 0
            && model.PruneSpecifications(suite, registered.Specs.ToHashSet(StringComparer.Ordinal)))
        {
            await bus.PublishAsync(new Contracts.EventModelChanged(model.Name ?? string.Empty));
        }
    }

    public static void Handle(RunnerCommandAcknowledged acknowledged, RunnerRegistry runners)
        => runners.Acknowledge(acknowledged.RunnerId, acknowledged.CommandId, acknowledged.Accepted, acknowledged.Reason, acknowledged.Refusal);

    public static void Handle(RunnerStatusReported status, RunnerRegistry runners)
        => runners.ReportStatus(status.RunnerId, status.State, status.Detail);
}
