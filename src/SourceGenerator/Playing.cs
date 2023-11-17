using Microsoft.CodeAnalysis;

namespace SourceGenerator;

[Generator]
public class HelloSourceGenerator : IIncrementalGenerator
{

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
    }
}