using Bobcat.Rendering;
using Bobcat.Ui;
using JasperFx.CommandLine;

namespace Bobcat.Console;

/// <summary>
/// <c>bobcat generate-ui-contracts</c> as the tool registers it: <see cref="GenerateCommand"/> with
/// the results file's schema supplied, so the HTML results page's types (bobcat#541) are generated
/// with the wire's. Bobcat.Ui references nothing in core; this assembly references both.
/// </summary>
[Description("Contributors: generate the Bobcat UI SPA's TypeScript mirrors of the monitor event contracts and the results file (./build.sh UiContracts)", Name = "generate-ui-contracts")]
public class UiContractsCommand : GenerateCommand
{
    protected override string? ResultsSchema => ResultsFile.SchemaText;
}
