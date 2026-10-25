namespace Bobcat.Aspire.Samples.AppHost;

/// <summary>
/// The other resources a Critter Stack suite usually needs, one line each. Nothing calls this, so the
/// sample starts only Postgres; it exists so the docs show RabbitMQ and SQL Server as code that compiles.
/// </summary>
public static class MoreInfrastructure
{
    public static IDistributedApplicationBuilder AddBrokerAndSqlServer(this IDistributedApplicationBuilder builder)
    {
        // #region more-resources
        builder.AddRabbitMQ("rabbitmq");                             // GetConnectionStringAsync("rabbitmq"): an amqp:// URI
        builder.AddSqlServer("sqlserver").AddDatabase("inventory");  // GetConnectionStringAsync("inventory")
        // #endregion more-resources

        return builder;
    }
}
