
using Bobcat.Engine;
using Spectre.Console.Rendering;

namespace Bobcat.Model;

public class Section
{

}

// Want this to be dumb. And it'll be an n-deep thing this time
public class Step
{
    private Dictionary<string, Step> _headers = new();
    private readonly List<Step> _children = new();
    private readonly Dictionary<string, object> _values = new();
    
    public IGrammar Grammar { get; }
    
    public Step Parent { get; private set; }
    
    /*
     * If spec, use "root"
     * if parent is "root", use index + 1
     * if step is under a section, use index-parent name-index
     * if step is a header, use {parent id}-{key}
     */
    public string Id { get; internal set; }

    public Step(IGrammar grammar, string id)
    {
        Grammar = grammar;
        Id = id;
    }

    public Step Add(IGrammar grammar)
    {
        // Determine the id. Top level, use the grammar key. If more than one, add suffixes

        //var id = Id == RootName ? grammar.Name;
        
        //var step = new Step(grammar, )
            
            // add to children

            throw new NotImplementedException();
    }

    public int FixtureCount() => _children.Select(x => x.Grammar).OfType<Fixture>().Count();

    public static Step ForSpecification(string title)
    {
        return new Step(new SpecificationRoot(title), RootName);
    }

    public const string RootName = "Root";
}

// do we even want this? Might not be necessary
// Could say there's no rendering of any of this if there's only one used
// Mostly necessary for set up / teardown issues
public class Fixture : IGrammar
{
    public Fixture(Type fixtureType)
    {
        
    }

    public string Name { get; }
    public void CreatePlan(ExecutionPlan plan, Step step, Step? parent = null)
    {
        throw new NotImplementedException();
    }

    public IRenderable RenderPreviewForConsole(Step step)
    {
        throw new NotImplementedException();
    }

    public IRenderable RenderResultsForConsole(Step step, ExecutionResults results)
    {
        throw new NotImplementedException();
    }
}

public interface IGrammar
{
    // Use the method name
    string Name { get; }

    void CreatePlan(ExecutionPlan plan, Step step, Step? parent = null);

    IRenderable RenderPreviewForConsole(Step step);
    IRenderable RenderResultsForConsole(Step step, ExecutionResults results);

}

public class SpecificationRoot : IGrammar
{
    public string Title { get; }

    public SpecificationRoot(string title)
    {
        Title = title;
    }

    public string Name { get; } = Step.RootName;
    public void CreatePlan(ExecutionPlan plan, Step step, Step? parent = null)
    {
        throw new NotImplementedException();
    }

    public IRenderable RenderPreviewForConsole(Step step)
    {
        throw new NotImplementedException();
    }

    public IRenderable RenderResultsForConsole(Step step, ExecutionResults results)
    {
        throw new NotImplementedException();
    }
}