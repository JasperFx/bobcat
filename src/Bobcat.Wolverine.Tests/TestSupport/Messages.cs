namespace Bobcat.Wolverine.Tests.TestSupport;

public record PingMessage(string Text);

public record CreateItem(string Name);

public record ItemCreated(Guid Id, string Name);
