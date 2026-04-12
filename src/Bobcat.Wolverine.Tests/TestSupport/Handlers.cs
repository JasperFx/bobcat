namespace Bobcat.Wolverine.Tests.TestSupport;

public static class PingHandler
{
    public static void Handle(PingMessage message)
    {
        // No-op: tracked session verifies handler was executed
    }
}

public static class CreateItemHandler
{
    public static ItemCreated Handle(CreateItem command)
        => new(Guid.NewGuid(), command.Name);
}
