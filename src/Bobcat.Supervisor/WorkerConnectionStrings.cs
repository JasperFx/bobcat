namespace Bobcat.Supervisor;

/// <summary>
/// The infrastructure each worker connects to, handed over as complete connection strings in the
/// Aspire convention, <c>ConnectionStrings__{name}</c> (issue #414). A suite that reads
/// <c>IConfiguration.GetConnectionString("postgres")</c> then runs unchanged under the
/// supervisor, under an Aspire AppHost's <c>WithReference(...)</c>, and by hand with
/// <c>ConnectionStrings__postgres=... dotnet run</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always a whole connection string, never a fragment.</b> A worker is given something it can
/// use as-is: not a database name, schema or port to stitch onto a base string. Stitching is where
/// parallel runs went wrong before: a <c>Replace("Initial Catalog=master", ...)</c> that matched
/// nothing once the base string changed and quietly pointed every worker at one database. The
/// same holds for brokers: RabbitMQ, Kafka, Azure Service Bus and NATS each get their full string
/// or URI.
/// </para>
/// <para>
/// <b>Creating the resources is not this class's job.</b> Provision one database (or vhost, or
/// namespace) per lane up front; this only says which one each worker gets. <see cref="WorkerLaunchContext.Lane"/>
/// is bounded by <c>MaxParallelWorkers</c>, so there are as many as workers asked for, and discovery,
/// isolated and recycled launches report lane 0.
/// </para>
/// <code>
/// new MtpWorkerFactory(path)
/// {
///     ConnectionStrings = new WorkerConnectionStrings()
///         .Add("postgres", worker => $"Host=localhost;Port=5433;Database=specs_w{worker.Lane};Username=postgres;Password=postgres")
///         .Add("rabbitmq", "amqp://guest:guest@localhost:5672")
/// }
/// </code>
/// </remarks>
public sealed class WorkerConnectionStrings
{
    /// <summary>The environment variable prefix .NET configuration reads as <c>ConnectionStrings:{name}</c>.</summary>
    public const string Prefix = "ConnectionStrings__";

    private readonly List<(string Name, Func<WorkerLaunchContext, string> ForWorker)> _resources = [];

    /// <summary>The resource names, in the order they were added.</summary>
    public IReadOnlyList<string> Names => _resources.Select(x => x.Name).ToList();

    /// <summary>A resource each worker gets its own of, e.g. one database per lane.</summary>
    /// <param name="name">The name the suite asks configuration for: <c>GetConnectionString(name)</c>.</param>
    /// <param name="forWorker">The worker's complete connection string.</param>
    public WorkerConnectionStrings Add(string name, Func<WorkerLaunchContext, string> forWorker)
    {
        validate(name);
        ArgumentNullException.ThrowIfNull(forWorker);
        _resources.Add((name, forWorker));
        return this;
    }

    /// <summary>A resource every worker shares, e.g. one broker, still handed over by name.</summary>
    public WorkerConnectionStrings Add(string name, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return Add(name, _ => connectionString);
    }

    /// <summary>The environment variable that carries <paramref name="name"/>: <c>ConnectionStrings__postgres</c>.</summary>
    public static string VariableFor(string name) => Prefix + name;

    /// <summary>The environment one worker is launched with: a <c>ConnectionStrings__{name}</c> per resource.</summary>
    /// <exception cref="InvalidOperationException">A resource gave this worker no connection string.</exception>
    public IReadOnlyDictionary<string, string> EnvironmentFor(WorkerLaunchContext worker)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, forWorker) in _resources)
        {
            var value = forWorker(worker);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"The connection string for '{name}' was empty for the {worker.Purpose} worker on lane {worker.Lane}. "
                    + "Every worker needs a complete connection string for every resource; provision one per lane up front.");
            }

            environment[VariableFor(name)] = value;
        }

        return environment;
    }

    private void validate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // What an environment variable name and a configuration key both survive: Aspire's own
        // resource names are letters, digits and hyphens
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            throw new ArgumentException(
                $"'{name}' cannot be a connection string name: use letters, digits, '-', '_' or '.', the way an Aspire resource is named.",
                nameof(name));
        }

        if (_resources.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            // Configuration keys ignore case, so 'Postgres' and 'postgres' would be one setting
            throw new ArgumentException($"A connection string named '{name}' was already added.", nameof(name));
        }
    }
}
