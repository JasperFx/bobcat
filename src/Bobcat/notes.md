# Review Notes

## ITestResource

* have this implement `IHostedService`
* `Start` --> `StartAsync`






## Random

* `TestSuite` rename to `TestResources`. Discuss how to integrate that into xUnit or TUnit maybe
* How if all are we using BobcatRunner 
* In `BobcatRunner.ScanForFeatures(Assembly assembly)`, use the assembly scanning support in JasperFx
* Eliminate `IHttpResource` completely. Just use Alba raw.
* I think we can completely kill the Bobcat.Alba package and strictly use AI Skills and documentation
* `AlbaContentRoot` should be in Bobcat core
* Let's replace Bobcat.Marten. Anything built in needs to only be using the JasperFx.Events abstractions. Use AI Skills and documentation for Marten usage
* Replace `IGlobalAction` by using `IHostedService`, then look to embed more in Wolverine/Marten etc.
* Eliminate Bobcat.Wolverine. AI Skills and documentation. Lift a tracked session helper out of Wolverine testing. Move helpers into Wolverine proper
* Bobcat.CritterStack could mostly move into Bobcat proper
* Kill the entire `Specification` model and the Given/When/Then fluent interface. The code centric approach will use our approach of selective attributes and comments leading to rendered views. No fluent interface