namespace Bobcat.Monitor.Specs;

// One fixture per feature is the mapping rule, and these four features speak one vocabulary —
// so each fixture is a shell that binds its feature to the shared ViewerSteps module. The
// shells carry no steps of their own on purpose: the day a feature needs a word nobody else
// does, it goes here and stays out of the shared module.

[FixtureTitle("Live Runs")]
[IncludeGrammars(typeof(ViewerSteps))]
public class LiveRunsFixture : Fixture;

[FixtureTitle("Retries")]
[IncludeGrammars(typeof(ViewerSteps))]
public class RetriesFixture : Fixture;

[FixtureTitle("Ejection")]
[IncludeGrammars(typeof(ViewerSteps))]
public class EjectionFixture : Fixture;

[FixtureTitle("Exports")]
[IncludeGrammars(typeof(ViewerSteps))]
public class ExportsFixture : Fixture;
