## Design Notes

* `Fixture` classes. 
* `IExecutionStep` will be the primary **runtime** model. The "display" model will be different, and maybe that can be more metadata driven
* The rendering model will be model-driven or metadata driven to allow for more variance in grammars
* `Grammar` abstraction will be responsible for building 

## Fixture classes

* *This* time, they should be scoped to a specification and not reused
* Make creating a fixture object cheap. All "grammar" creation should be based on reflection so there's no need to build things up


```csharp
[Scoped] // This would make it use a separate scoped container for the fixture and below
public class SomethingFixture
{
    // Here we *could* say that you try to find it in state, and fall back to services
    public SomethingFixture(Service, Service, Service, [State] state)
    {
    
    }
}

```

## Signatures

