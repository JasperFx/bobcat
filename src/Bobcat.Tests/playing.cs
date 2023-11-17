using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq.Expressions;

namespace Bobcat.Tests;

public class playing
{
    
}

public class SomethingFixture
{
    public void Go() => Debug.WriteLine("Go");

    public void GoWhat(string direction) => Debug.WriteLine("Go " + direction);

    public void DoSomething()
    {
        // Starting do something
        Go();
        
        GoWhat("North");

        Go();
    }
}