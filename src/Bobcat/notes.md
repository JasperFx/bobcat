# Bobcat Envisioning

## Problem Statement

Integration testing can be hard. Technical challenges abound, including:

* Understanding what's happening inside your system in the case of failures
* Testing timeouts, especially if you're needing to test asynchronous processing
* *Knowing* when asynchronous work is complete and delaying the *assertions* until those asynchronous actions are really complete
* Having a sense for whether a long running integration test is proceeding, or hung
* Data setup, especially in problem domains that require quite a bit of data in tests
* Making the expression of the test as declarative as possible to make the test clear in its intentions
* Preventing the test from being too tightly coupled to the internals of the system so the test isn't too brittle when the system internals change
* Being able to make the test suite *fail fast* when the system is detected to be in an invalid state -- don't blow this off, this can be a huge problem if you're not careful
* Selectively and intelligently retrying "blinking" tests -- and yeah, you should try really hard to not need this capability, but you might no matter how hard you try


Let's say that you're good at writing testable code, and you're able to write isolated unit tests for the mass majority
of your domain logic and much of your coordination workflow type of logic as well. That still leaves you with the desire
to write some type of integration test suite and maybe even some modicum of end to end tests through *shudder* Playwright,
Selenium, or Cypress.io.

## Goals

The primary goals are to:

* Provide a robust test runner strategy for integration testing that may include multiple processes, asynchronous processing,
  databases, and a substantial risk of "blinking" tests
* Provide intelligent test retries
* Understand when to abort the entire test suite
* Render a human readable report for the specification
* Expose troubleshooting information and diagnostics about the test run similar to what Storyteller did with its custom 
  [instrumentation & logging support](https://storyteller.github.io/documentation/using/instrumentation/), but make that easier to use
* Be able to provider status updates about the running test and running test suite
* Have an optimized command line mode suitable for CI usage
* Somehow, some way, make individual tests easily executable from the IDE
* Test data inputs and state management assertions should be as declarative as possible

The following are secondary goals:

* Be suitable for BDD specifications
* Be useful for creating technical documentation in project websites

Not even considering at all:

* Allow non-technical people to author specifications ala Cucumber. Not going even partially there at all this time


## Theoretical Approach

A lot of this is informed by lessons learned from [Storyteller](https://storyteller.github.io), usage of FitNesse back in the day, 
and observing Gherkin tools over the years. 


The general idea is to have tests written in C#, and relatively plain jane, low concept procedural C# at that something like:

```csharp
public class SomeFixture : BobcatFixture
{
    [FormatAs("Do something using {one} and {two}")] // for templating and rendering ala Storyteller or Gherkhin
    public void DoSomething(string one, string two)
    {
        // whatever it does
    }

    [Spec("When something or other")]
    public async Task some_spec()
    {
        DoSomething("value", "value");
        await DoSomeOtherSetup("value", 1, true);
        
        // Maybe try to make meaningful assertions, or 
        // even use Shouldly (my preference)
        Prop.ShouldBe(3);
    }
}
```

then have the test engine render that later as something like:

```
When something or other
-----------------------
Do something using value and value
Do some other set up with value, 1, and true
Prop should be 1, but was 3
```

but color code for successful matches, wrong values, errors and use italics or bolding to denote user inputs.

Right now, I'm contemplating writing or rendering the test results with:

* Console output using Spectre.Console
* Static HTML probably using HtmlTags
* Longer term, using an interactive UI ala Storyteller's React.js based UI, but don't even attempt to make that a test editor

Right now, I'm theorizing we could use a Source Generator to take the code in these "specifications" and turn that into
extra methods that:

* Execute a single test, mostly by injecting code into a rewrite of the original test method
* Render a preview of the test
* Sets up either an xUnit or NUnit adapter to run the same specifications at will through the IDE. Think this would be *way*
  quicker than writing custom dotnet test integrations. Not to mention the build in IDE support already for NUnit/xUnit.net

## Storyteller Inspiration and Contrast

* Use a mix of C# and maybe Markdown tables to define the tests. No defining tests in markdown like Storyteller or Gherkin
* Keep the kindof basic grammar/step idea from Storyteller in the generated code, but only start with sentences, facts, tables,
  and set verifications (have to figure out what that looks like in the new world here)
* Say that the tests automatically fail on the first exception in an action
* Still allow the test to proceed even with assertion failures
* The custom logging still rocks. Do that through the new Bobcat rendering model though
* Make a future UI really dumb this time
* Try to integrate and correlate .NET logging directly to the bobcat test output. That was awesome
