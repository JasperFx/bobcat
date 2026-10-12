using Xunit.Sdk;
using Xunit.v3;

// One test at a time, in a known order. Two of the probes below ask "did anything else run in my
// process before me?", and that question only has a reliable answer when nothing runs alongside
// them and the order they run in is the order they are written in — the Gherkin worker this
// replaces registered its features explicitly for the same reason.
// xUnit 4 made CollectionBehavior.DisableTestParallelization an error; Parallelization.Mode replaces it.
[assembly: Parallelization(Mode = ParallelMode.None)]
[assembly: TestCollectionOrderer(typeof(Bobcat.Supervisor.ProjectedWorker.ByClassName))]
// xUnit 4 orders collection -> class -> METHOD -> case, and a test case orderer now only orders the
// cases WITHIN one method (a theory's rows). Declaration order across a class's methods needs the
// method orderer; the case orderer stays for theories.
[assembly: TestMethodOrderer(typeof(Bobcat.Supervisor.ProjectedWorker.InDeclarationOrder))]
[assembly: TestCaseOrderer(typeof(Bobcat.Supervisor.ProjectedWorker.InDeclarationOrder))]

namespace Bobcat.Supervisor.ProjectedWorker;

/// <summary>
/// Runs the collections — one per class here — by name, so <c>Basics</c> always runs before
/// <c>Fussy</c> when the supervisor batches them into one process.
/// </summary>
public sealed class ByClassName : ITestCollectionOrderer
{
    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(
        IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : ITestCollection
        => [.. testCollections.OrderBy(c => c.TestCollectionDisplayName, StringComparer.Ordinal)];
}

/// <summary>
/// Runs a class's tests in the order they are declared in source. xUnit's default orders by a
/// hash of the test's id, which is stable but says nothing a reader of this file could predict.
/// </summary>
public sealed class InDeclarationOrder : ITestMethodOrderer, ITestCaseOrderer
{
    public IReadOnlyCollection<TTestMethod> OrderTestMethods<TTestMethod>(IReadOnlyCollection<TTestMethod> testMethods)
        where TTestMethod : notnull, ITestMethod
        => [.. testMethods.OrderBy(m => m is IXunitTestMethod xunit ? xunit.Method.MetadataToken : int.MaxValue)];

    public IReadOnlyCollection<TTestCase> OrderTestCases<TTestCase>(IReadOnlyCollection<TTestCase> testCases)
        where TTestCase : notnull, ITestCase
        => [.. testCases.OrderBy(declarationOrder)];

    private static int declarationOrder<TTestCase>(TTestCase testCase) where TTestCase : notnull, ITestCase
        => testCase is IXunitTestCase xunit ? xunit.TestMethod.Method.MetadataToken : int.MaxValue;
}
