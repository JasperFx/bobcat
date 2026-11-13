using Bobcat.Console.Specs;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// bobcat#830: the .NET CLI can print "MSBuild server unavailable … Falling back to …" on stdout
/// ahead of a <c>-getProperty</c> answer. Read whole, that sentence became the test host's path and
/// broke the JSON of a multi-property query on CI. The readers take the answer and only the answer.
/// </summary>
public class MsBuildQueryTests
{
    private const string ServerNotice =
        "MSBuild server unavailable: could not connect to the server within the timeout window; "
        + "the server may have failed to start. Falling back to ";

    [Fact]
    public void a_single_property_is_the_last_non_empty_line()
    {
        var output = ServerNotice + "running in-process.\n"
                     + "/home/runner/work/bobcat/src/Specs/bin/Debug/net10.0/Specs.dll\n"
                     + "\n";

        MsBuildQuery.Property(output).ShouldBe("/home/runner/work/bobcat/src/Specs/bin/Debug/net10.0/Specs.dll");
    }

    [Fact]
    public void a_single_property_printed_alone_is_itself()
    {
        MsBuildQuery.Property("BankAccountES.Tests\n").ShouldBe("BankAccountES.Tests");
        MsBuildQuery.Property("  C:\\specs\\bin\\Specs.dll\r\n").ShouldBe("C:\\specs\\bin\\Specs.dll");
    }

    [Fact]
    public void no_output_is_an_empty_property()
    {
        MsBuildQuery.Property("").ShouldBe("");
        MsBuildQuery.Property("\n\n").ShouldBe("");
    }

    [Fact]
    public void several_properties_are_read_from_the_first_brace()
    {
        var output = ServerNotice + "running in-process.\n"
                     + "{\n  \"Properties\": {\n    \"TargetFramework\": \"\",\n    \"TargetFrameworks\": \"net9.0;net10.0\"\n  }\n}\n";

        var json = MsBuildQuery.Json(output);

        json.ShouldStartWith("{");
        using var document = System.Text.Json.JsonDocument.Parse(json);
        document.RootElement.GetProperty("Properties").GetProperty("TargetFrameworks").GetString().ShouldBe("net9.0;net10.0");
    }

    [Fact]
    public void output_with_no_json_is_handed_back_whole_so_the_error_shows_it()
    {
        MsBuildQuery.Json("error MSB1009: Project file does not exist.\n")
            .ShouldBe("error MSB1009: Project file does not exist.\n");
    }

    [Fact]
    public void a_property_query_never_uses_the_msbuild_server()
    {
        MsBuildQuery.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"].ShouldBe("0");
        MsBuildQuery.Environment["MSBUILDDISABLENODEREUSE"].ShouldBe("1");
        MsBuildQuery.Environment["DOTNET_NOLOGO"].ShouldBe("1");
        MsBuildQuery.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"].ShouldBe("1");
    }
}
