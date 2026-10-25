// The test infrastructure, declared once. Where docker-compose.yml would name an image, a port and a
// password, this names a resource: Aspire picks the image, a free host port and a generated password,
// and hands each consumer a whole connection string.

// #region apphost
var builder = DistributedApplication.CreateBuilder(args);

// One Postgres container with one database on it. "orders" is the name the spec assembly asks for
// the connection string by.
builder.AddPostgres("postgres")
    .AddDatabase("orders");

builder.Build().Run();
// #endregion apphost
