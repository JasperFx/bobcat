using Bobcat.Ui.Specs;

// One viewer for the whole suite, emptied before every test -- the shape Bobcat.Mtp's resource gave the
// Gherkin features. Sharing it is what makes serial execution mandatory: a test that asserts about the
// WHOLE board (the bulk ejects, issue #197) cannot have another test's runs landing on it.
[assembly: AssemblyFixture(typeof(MonitorHost))]
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
