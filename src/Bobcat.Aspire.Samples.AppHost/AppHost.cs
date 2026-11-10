using Aspire.Hosting.ApplicationModel;

// The test infrastructure, declared once. Where docker-compose.yml would name an image, a port and a
// password, this names a resource: Aspire picks the image, a free host port and a generated password,
// and hands each consumer a whole connection string.

// #region apphost
var builder = DistributedApplication.CreateBuilder(args);

// One Postgres container with one database on it. "orders" is the name the spec assembly asks for
// the connection string by.
builder.AddPostgres("postgres")
    .AddDatabase("orders");

// The service under test, an HTTP endpoint the specs call by the name "web".
builder.AddProject<Projects.Bobcat_Aspire_Samples_Web>("web")
    .WithHttpEndpoint();

// A resource that runs nothing: it exists so the spec run can show its progress on the Aspire
// dashboard as this resource's state (see TestInfrastructure.PublishProgress).
builder.AddResource(new SpecsResource("specs"))
    .WithInitialState(new CustomResourceSnapshot
    {
        ResourceType = "Specs",
        State = new ResourceStateSnapshot("Waiting", KnownResourceStateStyles.Info),
        Properties = []
    })
    .ExcludeFromManifest();

builder.Build().Run();
// #endregion apphost

/// <summary>The resource the spec run reports its progress through.</summary>
public sealed class SpecsResource(string name) : Resource(name);
