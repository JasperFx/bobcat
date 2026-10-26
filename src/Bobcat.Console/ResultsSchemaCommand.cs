using Bobcat.Rendering;
using JasperFx.CommandLine;

namespace Bobcat.Console;

public class ResultsSchemaInput
{
    [Description("Write the schema into this directory as bobcat-results-<version>.schema.json; omitted, it goes to standard output")]
    public string? OutFlag { get; set; }

    [Description("Compare with this baseline schema file (named bobcat-results-<version>.schema.json) and fail on a breaking change unless the schema version moved")]
    [FlagAlias("baseline", true)]
    public string? BaselineFlag { get; set; }

    [Description("What the baseline is, for the report (e.g. 'Bobcat 0.61.0 on nuget.org')")]
    [FlagAlias("baseline-name", true)]
    public string? BaselineNameFlag { get; set; }
}

/// <summary>
/// <c>bobcat results-schema</c> — the results file's JSON Schema, generated from the records the
/// file is written from (bobcat#604). The schema is not committed anywhere: this is how the Nuke
/// Pack target gets the copy it packs under <c>schemas/</c>, and how the CI compatibility check
/// compares it with the last released package's copy (<see cref="ResultsSchemaCompatibility"/>).
/// </summary>
[Description("Write the JSON Schema of the results file (--results), or check it against a released baseline", Name = "results-schema")]
public class ResultsSchemaCommand : JasperFxCommand<ResultsSchemaInput>
{
    public override bool Execute(ResultsSchemaInput input)
    {
        var schema = ResultsFile.GenerateSchema();

        if (input.OutFlag is { } directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, ResultsFile.SchemaFileName);
            File.WriteAllText(path, schema);
            System.Console.WriteLine($"Wrote {Path.GetFullPath(path)}");
        }
        else if (input.BaselineFlag is null)
        {
            System.Console.Out.Write(schema);
        }

        if (input.BaselineFlag is not { } baselinePath) return true;

        var baselineVersion = ResultsSchemaCompatibility.VersionOf(baselinePath);
        if (baselineVersion is null)
        {
            System.Console.Error.WriteLine($"{baselinePath} is not named bobcat-results-<version>.schema.json, so its schema version is unknown.");
            return false;
        }

        var (passed, lines) = ResultsSchemaCompatibility.Check(
            File.ReadAllText(baselinePath), baselineVersion,
            schema, ResultsFile.SchemaVersion,
            input.BaselineNameFlag ?? Path.GetFileName(baselinePath));

        foreach (var line in lines)
        {
            (passed ? System.Console.Out : System.Console.Error).WriteLine(line);
        }

        return passed;
    }
}
