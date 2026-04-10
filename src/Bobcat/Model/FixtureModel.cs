using System.Reflection;
using Bobcat.Engine;

namespace Bobcat.Model;

/// <summary>
/// The discovered model of a fixture class — its grammars, setup, and teardown methods.
/// Built once via reflection, reused across scenarios.
/// </summary>
public class FixtureModel
{
    private readonly Dictionary<string, IGrammar> _grammars = new();
    private readonly List<MethodInfo> _setUpMethods = new();
    private readonly List<MethodInfo> _tearDownMethods = new();

    public Type FixtureType { get; }
    public string Name { get; }

    public FixtureModel(Type fixtureType)
    {
        FixtureType = fixtureType;
        Name = fixtureType.Name;
        Discover();
    }

    private void Discover()
    {
        var methods = FixtureType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        foreach (var method in methods)
        {
            if (method.GetCustomAttribute<SetUpAttribute>() != null)
            {
                _setUpMethods.Add(method);
                continue;
            }

            if (method.GetCustomAttribute<TearDownAttribute>() != null)
            {
                _tearDownMethods.Add(method);
                continue;
            }

            var given = method.GetCustomAttribute<GivenAttribute>();
            if (given != null)
            {
                _grammars[method.Name] = new Sentence(method, given, StepKind.Given);
                continue;
            }

            var when = method.GetCustomAttribute<WhenAttribute>();
            if (when != null)
            {
                _grammars[method.Name] = new Sentence(method, when, StepKind.When);
                continue;
            }

            var then = method.GetCustomAttribute<ThenAttribute>();
            if (then != null)
            {
                _grammars[method.Name] = new Sentence(method, then, StepKind.Then);
                continue;
            }

            var fact = method.GetCustomAttribute<CheckAttribute>();
            if (fact != null)
            {
                _grammars[method.Name] = new Fact(method, fact);
            }
        }
    }

    public IGrammar? FindGrammar(string name)
    {
        return _grammars.GetValueOrDefault(name);
    }

    public IReadOnlyDictionary<string, IGrammar> Grammars => _grammars;
    public IReadOnlyList<MethodInfo> SetUpMethods => _setUpMethods;
    public IReadOnlyList<MethodInfo> TearDownMethods => _tearDownMethods;

    public FixtureInstance CreateInstance()
    {
        var instance = (Fixture)Activator.CreateInstance(FixtureType)!;
        return new FixtureInstance(this, instance);
    }
}

/// <summary>
/// A live fixture instance for a single scenario execution.
/// </summary>
public class FixtureInstance
{
    public FixtureModel Model { get; }
    public Fixture Instance { get; }

    public FixtureInstance(FixtureModel model, Fixture instance)
    {
        Model = model;
        Instance = instance;
    }

    public async Task RunSetUp()
    {
        await Instance.SetUp();
        foreach (var method in Model.SetUpMethods)
        {
            var result = method.Invoke(Instance, []);
            if (result is Task task) await task;
        }
    }

    public async Task RunTearDown()
    {
        await Instance.TearDown();
        foreach (var method in Model.TearDownMethods)
        {
            var result = method.Invoke(Instance, []);
            if (result is Task task) await task;
        }
    }
}
