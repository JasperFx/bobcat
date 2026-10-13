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
    public static void Handle(RunnerRegistered registered, RunnerRegistry runners, IRepositoryIdentity identity)
        => runners.Register(registered, identity.SlugFor(registered.Repository));

    public static void Handle(RunnerCommandAcknowledged acknowledged, RunnerRegistry runners)
        => runners.Acknowledge(acknowledged.RunnerId, acknowledged.CommandId, acknowledged.Accepted, acknowledged.Reason, acknowledged.Refusal);

    public static void Handle(RunnerStatusReported status, RunnerRegistry runners)
        => runners.ReportStatus(status.RunnerId, status.State, status.Detail);
}
