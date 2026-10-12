using System.Text;
using Bobcat.EventModel.Emlang;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.EventModel;

/// <summary>
/// The additive re-import: what <c>import-event-model</c> does, without <c>--force</c>, to a file that
/// already exists. It adds what the board has and the file does not — a slice declaration, a stub type,
/// a specification — and never changes or removes anything already there, because those files are
/// where the import's guesses get corrected.
/// </summary>
/// <remarks>
/// <para>
/// <b>Roslyn finds every insertion point; the edit is a text insertion.</b> The existing file is parsed
/// to find where a declaration goes (the end of a definition's <c>Configure</c> body, of the stub's
/// namespace, of the spec class), and the new text is inserted at that offset. Nothing is re-printed
/// from a syntax tree, so every byte the user wrote survives except at the insertion itself.
/// </para>
/// <para>
/// <b>What cannot be placed safely is reported, never forced</b>: a file that does not parse, a class
/// or namespace that is not there, more than one candidate, a type whose members differ from the
/// board's. Each is an <see cref="ImportCallout"/> with a code from <see cref="ImportCalloutCodes"/>.
/// </para>
/// </remarks>
public static class ImportMerge
{
    /// <summary>What one merge produced.</summary>
    /// <param name="Content">The file's new text, or null when nothing changed.</param>
    /// <param name="Added">What was added, by name, for the report.</param>
    /// <param name="Callouts">What was merged, and what could not be placed.</param>
    public sealed record Result(string? Content, IReadOnlyList<string> Added, IReadOnlyList<ImportCallout> Callouts)
    {
        /// <summary>What was added, said for the report line when the bare count would not say it, e.g. "1 slice declaration (X)".</summary>
        public string? Description { get; init; }

        public static Result Unchanged(params ImportCallout[] callouts) => new(null, [], callouts);
    }

    internal static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    /// <summary>The verbs that open a slice on an <c>EventModelBuilder</c>.</summary>
    internal static readonly HashSet<string> SliceVerbs = new(StringComparer.Ordinal)
        { "Command", "View", "Automation", "Translation", "Query", "Slice" };

    /// <summary>The calls that name an aggregate in a definition.</summary>
    internal static readonly HashSet<string> AggregateVerbs = new(StringComparer.Ordinal)
        { "Aggregate", "ForAggregate", "Against", "StartsStream" };

    internal static SyntaxTree? TryParse(string text, out string? error)
    {
        var tree = CSharpSyntaxTree.ParseText(text, Parse);
        var first = tree.GetDiagnostics().FirstOrDefault(x => x.Severity == DiagnosticSeverity.Error);
        error = first is null ? null : $"{first.Id} at line {first.Location.GetLineSpan().StartLinePosition.Line + 1}: {first.GetMessage()}";
        return first is null ? tree : null;
    }

    // ---------------------------------------------------------------- stubs

    /// <summary>
    /// Add the stub types <paramref name="generated"/> declares that the project does not, at the end of
    /// <paramref name="existing"/>'s namespace. A type the file or the project already declares is never
    /// touched; one whose members differ from the board's is reported.
    /// </summary>
    /// <param name="existing">The file as it is.</param>
    /// <param name="generated">The file a fresh import would write.</param>
    /// <param name="path">The file's full path, which callouts name.</param>
    /// <param name="index">Every type the project already declares, and where.</param>
    public static Result MergeStubs(string existing, string generated, string path, SourceIndex index)
    {
        var tree = TryParse(existing, out var error);
        if (tree is null) return Result.Unchanged(unparseable(path, error));

        var fresh = CSharpSyntaxTree.ParseText(generated, Parse).GetCompilationUnitRoot();
        var callouts = new List<ImportCallout>();
        var toAdd = new List<BaseTypeDeclarationSyntax>();

        foreach (var type in TopLevelTypes(fresh))
        {
            var name = type.Identifier.ValueText;
            var declared = index.TypesNamed(name);
            if (declared.Count == 0)
            {
                toAdd.Add(type);
                continue;
            }

            // The file's own declaration first, else wherever the project declares it
            var match = declared.Where(x => samePath(x.Path, path)).DefaultIfEmpty(declared[0]).First();
            if (Differences(type, match.Declaration) is { Count: > 0 } differences)
            {
                callouts.Add(callout(ImportCalloutCodes.TypeDiffersFromBoard, match.Path,
                    $"{name} in {Path.GetFileName(match.Path)} differs from the board ({string.Join("; ", differences)}). "
                    + "It was left as it is: the code is the correction, so change it by hand if the board is right.") with { Type = name });
            }
        }

        if (toAdd.Count == 0) return new Result(null, [], callouts);

        var root = tree.GetCompilationUnitRoot();
        var ns = NamespaceOf(fresh);
        var candidates = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
            .Where(x => x.Name.ToString() == ns)
            .ToList();

        // A file with no namespace at all is in the global one, which matches only a stub without one
        if (candidates.Count == 0 && !(ns is null && !root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Any()))
        {
            callouts.Add(noInsertionPoint(path,
                $"{Path.GetFileName(path)} declares no namespace {ns}, so {names(toAdd)} could not be added to it. Add {(toAdd.Count == 1 ? "it" : "them")} by hand, or move the file's types back into {ns}.")
                with { Type = toAdd[0].Identifier.ValueText });
            return new Result(null, [], callouts);
        }

        if (candidates.Count > 1)
        {
            callouts.Add(ambiguous(path, $"{Path.GetFileName(path)} declares namespace {ns} {candidates.Count} times, so where {names(toAdd)} would go is ambiguous. Add {(toAdd.Count == 1 ? "it" : "them")} by hand."));
            return new Result(null, [], callouts);
        }

        var text = existing;
        var newline = newlineOf(text);
        var block = string.Join("\n\n", toAdd.Select(x => x.ToFullString()).Select(x => Reindent(x, "")));

        var edits = new List<(int Position, string Text)>();
        if (candidates.Count == 0)
        {
            // Global namespace: after the last top-level member
            var members = root.Members;
            edits.Add(members.Count == 0
                ? insertion(text, root.EndOfFileToken.FullSpan.Start, Reindent(block, ""), newline, blankBefore: root.Usings.Count > 0)
                : insertion(text, members.Last().FullSpan.End, Reindent(block, ""), newline, blankBefore: true));
        }
        else
        {
            var target = candidates[0];
            if (target.Members.Count > 0)
            {
                var indent = IndentOf(text, target.Members[0].SpanStart);
                edits.Add(insertion(text, target.Members.Last().FullSpan.End, Reindent(block, indent), newline, blankBefore: true));
            }
            else if (target is NamespaceDeclarationSyntax blockNamespace)
            {
                var indent = IndentOf(text, blockNamespace.SpanStart) + "    ";
                edits.Add(insertion(text, blockNamespace.OpenBraceToken.FullSpan.End, Reindent(block, indent), newline, blankBefore: false));
            }
            else
            {
                var fileScoped = (FileScopedNamespaceDeclarationSyntax)target;
                edits.Add(insertion(text, fileScoped.SemicolonToken.FullSpan.End, Reindent(block, ""), newline, blankBefore: true));
            }
        }

        edits.AddRange(missingUsings(text, root, fresh, ns, newline));

        var added = toAdd.Select(x => x.Identifier.ValueText).ToList();
        callouts.InsertRange(0, added.Select(x => callout(ImportCalloutCodes.MergedType, path,
            $"Added {x} to {Path.GetFileName(path)}: the board names it and the project declared no type of that name.") with { Type = x }));

        return new Result(apply(text, edits), added, callouts);
    }

    /// <summary>
    /// A stub file that does not exist yet, minus every type the project already declares somewhere
    /// else — a type the layout now puts in this file may have been written to another on an earlier
    /// import. Null when nothing is left to write.
    /// </summary>
    public static string? WithoutDeclaredTypes(string generated, SourceIndex index, out IReadOnlyList<(string Type, string Path)> skipped)
    {
        var fresh = CSharpSyntaxTree.ParseText(generated, Parse).GetCompilationUnitRoot();
        var types = TopLevelTypes(fresh).ToList();
        var declared = types.Where(x => index.TypesNamed(x.Identifier.ValueText).Count > 0).ToList();
        skipped = declared.Select(x => (x.Identifier.ValueText, index.TypesNamed(x.Identifier.ValueText)[0].Path)).ToList();

        if (declared.Count == 0) return generated;
        if (declared.Count == types.Count) return null;

        return remove(generated, declared.Select(x => (x.FullSpan.Start, x.FullSpan.Length)).ToList());
    }

    /// <summary>
    /// The members the board names that the code's type lacks or types differently. Members the code
    /// adds beyond the board's are expected — that is the behaviour taking shape — and never reported.
    /// </summary>
    public static IReadOnlyList<string> Differences(BaseTypeDeclarationSyntax board, BaseTypeDeclarationSyntax code)
    {
        var have = membersOf(code).GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.First().Type, StringComparer.Ordinal);
        var differences = new List<string>();
        foreach (var (name, type) in membersOf(board))
        {
            if (!have.TryGetValue(name, out var actual)) differences.Add($"the board names {type} {name}, which the code does not declare");
            else if (normalizeType(actual) != normalizeType(type)) differences.Add($"the board types {name} as {type}, the code as {actual}");
        }

        return differences;
    }

    private static IEnumerable<(string Name, string Type)> membersOf(BaseTypeDeclarationSyntax type)
    {
        if (type is RecordDeclarationSyntax { ParameterList: { } parameters })
        {
            foreach (var parameter in parameters.Parameters)
            {
                if (parameter.Type is not null) yield return (parameter.Identifier.ValueText, parameter.Type.ToString());
            }
        }

        if (type is TypeDeclarationSyntax declaration)
        {
            foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
                yield return (property.Identifier.ValueText, property.Type.ToString());
        }
    }

    private static string normalizeType(string type)
        => new string(type.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace("global::", "").Replace("System.Collections.Generic.", "").Replace("System.", "");

    // ---------------------------------------------------------------- definitions

    /// <summary>
    /// Add the slices <paramref name="slices"/> names that no definition in the project declares, at the
    /// end of <paramref name="existing"/>'s <c>Configure</c>, written against what the definition already
    /// says at its top (<c>InChapter</c>, <c>InDomain</c>, <c>ForAggregate</c>). Existing declarations are
    /// never touched, even where the board now says something else.
    /// </summary>
    /// <param name="existing">The definition file as it is.</param>
    /// <param name="generated">The definition a fresh import would write, for the usings it needs.</param>
    /// <param name="slices">The slices that belong in this definition, in board order.</param>
    /// <param name="path">The file's full path.</param>
    /// <param name="index">Every slice, aggregate and type the project already declares.</param>
    public static Result MergeDefinition(string existing, string generated, IReadOnlyList<CuratedSlice> slices, string path, SourceIndex index)
    {
        var tree = TryParse(existing, out var error);
        if (tree is null) return Result.Unchanged(unparseable(path, error));

        var missing = slices.Where(x => !index.DeclaresSlice(SliceName(x))).ToList();

        var root = tree.GetCompilationUnitRoot();
        var definitions = DefinitionClasses(root).ToList();
        if (definitions.Count != 1)
        {
            if (missing.Count == 0) return Result.Unchanged();
            return Result.Unchanged(definitions.Count == 0
                ? noInsertionPoint(path, $"{Path.GetFileName(path)} has no EventModelDefinition class, so {sliceNames(missing)} could not be declared in it. Declare {(missing.Count == 1 ? "it" : "them")} by hand.")
                : ambiguous(path, $"{Path.GetFileName(path)} has {definitions.Count} EventModelDefinition classes, so which one {sliceNames(missing)} belong{(missing.Count == 1 ? "s" : "")} in is ambiguous. Declare {(missing.Count == 1 ? "it" : "them")} by hand."));
        }

        var configure = definitions[0].Members.OfType<MethodDeclarationSyntax>()
            .Where(x => x.Identifier.ValueText == "Configure" && x.ParameterList.Parameters.Count == 1)
            .ToList();
        if (configure is not [{ Body: { } body } method])
        {
            if (missing.Count == 0) return Result.Unchanged();
            return Result.Unchanged(noInsertionPoint(path,
                $"{definitions[0].Identifier.ValueText} has no Configure(EventModelBuilder) method with a block body, so {sliceNames(missing)} could not be declared in it. Declare {(missing.Count == 1 ? "it" : "them")} by hand."));
        }

        // What the definition says once at the top, which a slice added to it must agree with
        var parameter = method.ParameterList.Parameters[0].Identifier.ValueText;
        string? said(string verb) => body.Statements.OfType<ExpressionStatementSyntax>()
            .Select(x => x.Expression).OfType<InvocationExpressionSyntax>()
            .Where(x => x.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } access
                        && receiver.Identifier.ValueText == parameter && memberName(access) == verb)
            .Select(Named)
            .FirstOrDefault(x => x is not null);

        var chapter = said("InChapter");
        var domain = said("InDomain");
        var aggregate = said("ForAggregate");

        var stubbed = index.TypeNames;
        var differing = slices.Except(missing)
            .Select(x => sliceDiffers(x, body, CSharpModelWriter.SliceDeclaration(x, stubbed, chapter, domain, aggregate), path))
            .OfType<ImportCallout>()
            .ToList();
        if (missing.Count == 0) return Result.Unchanged(differing.ToArray());

        var statements = new List<string>();
        var aggregates = new List<string>();
        foreach (var name in missing.SelectMany(CSharpModelWriter.AggregatesOf).Select(CSharpModelWriter.Identifiers.Sanitize).Distinct(StringComparer.Ordinal))
        {
            if (name.Length == 0 || index.DeclaresAggregate(name) || name == aggregate) continue;
            aggregates.Add(name);
            statements.Add(CSharpModelWriter.AggregateDeclaration(name, stubbed));
        }

        statements.AddRange(missing.Select(x => CSharpModelWriter.SliceDeclaration(x, stubbed, chapter, domain, aggregate)));

        var text = existing;
        var newline = newlineOf(text);
        var block = string.Join("\n\n", statements.Select(x => Reindent(x, "")));
        var edits = new List<(int, string)>();
        if (body.Statements.Count > 0)
        {
            var indent = IndentOf(text, body.Statements[0].SpanStart);
            edits.Add(insertion(text, body.Statements.Last().FullSpan.End, Reindent(block, indent), newline, blankBefore: true));
        }
        else
        {
            var indent = IndentOf(text, method.SpanStart) + "    ";
            edits.Add(insertion(text, body.OpenBraceToken.FullSpan.End, Reindent(block, indent), newline, blankBefore: false));
        }

        var fresh = CSharpSyntaxTree.ParseText(generated, Parse).GetCompilationUnitRoot();
        edits.AddRange(missingUsings(text, root, fresh, NamespaceOf(fresh), newline));

        var callouts = missing.Select(x => callout(ImportCalloutCodes.MergedSliceDeclaration, path,
                $"Declared slice {x.Name} in {Path.GetFileName(path)}: the board has it and no definition in the project declared it.") with { Slice = x.Name })
            .Concat(aggregates.Select(x => callout(ImportCalloutCodes.MergedAggregateDeclaration, path,
                $"Declared aggregate {x} in {Path.GetFileName(path)}: a slice added there names it and no definition declared it.") with { Type = x }))
            .ToList();

        callouts.AddRange(differing);
        var description = counted(missing.Select(x => x.Name).ToList(), "slice declaration")
                          + (aggregates.Count > 0 ? " and " + counted(aggregates, "aggregate declaration") : "");
        return new Result(apply(text, edits), missing.Select(x => x.Name).Concat(aggregates).ToList(), callouts) { Description = description };
    }

    /// <summary>
    /// A definition file that does not exist yet, minus the slices another definition in the project
    /// already declares. Null when nothing is left to declare.
    /// </summary>
    public static string? WithoutDeclaredSlices(string generated, SourceIndex index, out IReadOnlyList<string> skipped)
    {
        var root = CSharpSyntaxTree.ParseText(generated, Parse).GetCompilationUnitRoot();
        var statements = DefinitionClasses(root)
            .SelectMany(x => x.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == "Configure"))
            .SelectMany(x => x.Body?.Statements ?? [])
            .Select(x => (Statement: x, Slice: x.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(IsOpener)
                .Select(Named).FirstOrDefault(n => n is not null)))
            .Where(x => x.Slice is not null)
            .ToList();

        var declared = statements.Where(x => index.DeclaresSlice(x.Slice!)).ToList();
        skipped = declared.Select(x => x.Slice!).ToList();
        if (declared.Count == 0) return generated;
        if (declared.Count == statements.Count) return null;

        return remove(generated, declared.Select(x => (x.Statement.FullSpan.Start, x.Statement.FullSpan.Length)).ToList());
    }

    /// <summary>
    /// The roles a slice declaration is compared on: what the board says the slice does, not how it is wired.
    /// <c>LinksToSpecification</c> is left out: a generated specification binds itself with <c>[BobcatSlice]</c>,
    /// and the spec project publishes its own links, so a missing one in the definition changes nothing.
    /// </summary>
    private static readonly HashSet<string> ComparedRoles = new(StringComparer.Ordinal)
        { "Emits", "Publishes", "On", "Reads", "Produces" };

    /// <summary>
    /// A <c>slice-differs-from-board</c> callout when the board gives a slice declared in this definition a
    /// role (an event it emits, a message it publishes, …) its declaration does not say; null otherwise.
    /// The declaration is never changed: the aggregate, trigger and pattern a person corrected stay theirs.
    /// </summary>
    private static ImportCallout? sliceDiffers(CuratedSlice slice, BlockSyntax body, string boardDeclaration, string path)
    {
        var name = SliceName(slice);
        var existing = body.Statements.FirstOrDefault(x => openerName(x) == name);
        if (existing is null) return null;

        var board = rolesOf(SyntaxFactory.ParseStatement(boardDeclaration, options: Parse));
        var code = rolesOf(existing).ToHashSet(StringComparer.Ordinal);
        var missing = board.Where(x => !code.Contains(x)).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count == 0) return null;

        return callout(ImportCalloutCodes.SliceDiffersFromBoard, path,
                $"The board gives slice {slice.Name} {string.Join(", ", missing.Select(x => x.Replace(":", "(") + ")"))}, which its declaration in {Path.GetFileName(path)} does not say. "
                + "The declaration was left as it is; add the call by hand if the board is right.")
            with { Slice = slice.Name };
    }

    private static string? openerName(StatementSyntax statement)
        => statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(IsOpener)
            .Select(Named).FirstOrDefault(n => n is not null);

    private static IEnumerable<string> rolesOf(StatementSyntax statement)
        => statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(c => c.Expression is MemberAccessExpressionSyntax access && ComparedRoles.Contains(memberName(access)))
            .Select(c => $"{memberName((MemberAccessExpressionSyntax)c.Expression)}:{Named(c)}")
            .Reverse(); // outermost call first in the tree, so reversed it reads in the order it was written

    /// <summary>The name a slice is declared under: the merge key.</summary>
    public static string SliceName(CuratedSlice slice)
    {
        var name = CSharpModelWriter.Identifiers.Sanitize(slice.Name);
        return name.Length == 0 ? "Unnamed" : name;
    }

    // ---------------------------------------------------------------- specifications

    /// <summary>
    /// Add the specifications <paramref name="generated"/>'s feature class has that no class bound to the
    /// same <c>[BobcatFeature]</c> in the spec project has, at the end of that class. Existing
    /// specification methods are never touched. The same rule <see cref="EmlangSpecWriter.MissingSpecs"/>
    /// reports by: a feature and a method name.
    /// </summary>
    /// <param name="existing">The file holding the feature class, as it is.</param>
    /// <param name="generated">The specification file a fresh import would write.</param>
    /// <param name="path">The existing file's full path.</param>
    /// <param name="index">Every feature class and specification method the spec project declares.</param>
    public static Result MergeSpecs(string existing, string generated, string path, SourceIndex index)
    {
        var tree = TryParse(existing, out var error);
        if (tree is null) return Result.Unchanged(unparseable(path, error));

        var fresh = CSharpSyntaxTree.ParseText(generated, Parse).GetCompilationUnitRoot();
        var root = tree.GetCompilationUnitRoot();
        var text = existing;
        var newline = newlineOf(text);
        var edits = new List<(int, string)>();
        var added = new List<string>();
        var callouts = new List<ImportCallout>();

        foreach (var feature in fresh.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (FeatureOf(feature) is not { } name) continue;

            var missing = feature.Members.OfType<MethodDeclarationSyntax>()
                .Where(x => !index.HasSpec(name, x.Identifier.ValueText))
                .ToList();
            if (missing.Count == 0) continue;

            var targets = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(x => FeatureOf(x) == name).ToList();
            if (targets.Count != 1)
            {
                callouts.Add(targets.Count == 0
                    ? noInsertionPoint(path, $"{Path.GetFileName(path)} has no class bound to [BobcatFeature(\"{name}\")], so {string.Join(", ", missing.Select(x => x.Identifier.ValueText))} could not be added. Copy {(missing.Count == 1 ? "it" : "them")} from a fresh import (--specs-out to an empty directory).") with { Slice = name }
                    : ambiguous(path, $"{Path.GetFileName(path)} has {targets.Count} classes bound to [BobcatFeature(\"{name}\")], so where the new specifications go is ambiguous.") with { Slice = name });
                continue;
            }

            var target = targets[0];
            if (target.OpenBraceToken.IsMissing || target.OpenBraceToken.Span.Length == 0)
            {
                callouts.Add(noInsertionPoint(path, $"The class bound to [BobcatFeature(\"{name}\")] has no body to add specifications to.") with { Slice = name });
                continue;
            }

            var block = string.Join("\n\n", missing.Select(x => Reindent(x.ToFullString(), "")));
            if (target.Members.Count > 0)
            {
                var indent = IndentOf(text, target.Members[0].SpanStart);
                edits.Add(insertion(text, target.Members.Last().FullSpan.End, Reindent(block, indent), newline, blankBefore: true));
            }
            else
            {
                var indent = IndentOf(text, target.SpanStart) + "    ";
                edits.Add(insertion(text, target.OpenBraceToken.FullSpan.End, Reindent(block, indent), newline, blankBefore: false));
            }

            foreach (var method in missing)
            {
                var spec = $"{name} / {method.Identifier.ValueText}";
                added.Add(spec);
                callouts.Add(callout(ImportCalloutCodes.MergedSpec, path,
                    $"Added the specification {method.Identifier.ValueText} to {target.Identifier.ValueText} in {Path.GetFileName(path)}: the board has the example and the spec project had no specification for it.") with { Slice = name });
            }
        }

        return new Result(edits.Count == 0 ? null : apply(text, edits), added, callouts);
    }

    /// <summary>The <c>global using</c> directives <paramref name="generated"/> has that <paramref name="existing"/> lacks, added after its last.</summary>
    public static Result MergeGlobalUsings(string existing, string generated, string path)
    {
        var tree = TryParse(existing, out var error);
        if (tree is null) return Result.Unchanged(unparseable(path, error));

        var root = tree.GetCompilationUnitRoot();
        var have = root.Usings.Where(x => x.GlobalKeyword.Span.Length > 0).Select(x => x.Name?.ToString()).ToHashSet(StringComparer.Ordinal);
        var fresh = CSharpSyntaxTree.ParseText(generated, Parse).GetCompilationUnitRoot();
        var missing = fresh.Usings.Select(x => x.Name?.ToString()).OfType<string>().Where(x => !have.Contains(x)).ToList();
        if (missing.Count == 0) return Result.Unchanged();

        var newline = newlineOf(existing);
        var block = string.Join("\n", missing.Select(x => $"global using {x};"));
        var position = root.Usings.Count > 0 ? root.Usings.Last().FullSpan.End : root.EndOfFileToken.FullSpan.Start;
        var content = apply(existing, [insertion(existing, position, block, newline, blankBefore: false)]);

        return new Result(content, missing, missing.Select(x => callout(ImportCalloutCodes.MergedUsing, path,
            $"Added global using {x} to {Path.GetFileName(path)}: the stubs now declare types there.")).ToList());
    }

    // ---------------------------------------------------------------- syntax helpers

    /// <summary>The types declared directly in the file or its namespaces — not nested in another type.</summary>
    public static IEnumerable<BaseTypeDeclarationSyntax> TopLevelTypes(CompilationUnitSyntax root)
    {
        IEnumerable<BaseTypeDeclarationSyntax> within(SyntaxList<MemberDeclarationSyntax> members)
        {
            foreach (var member in members)
            {
                if (member is BaseTypeDeclarationSyntax type) yield return type;
                else if (member is BaseNamespaceDeclarationSyntax ns)
                {
                    foreach (var inner in within(ns.Members)) yield return inner;
                }
            }
        }

        return within(root.Members);
    }

    /// <summary>The classes deriving from <c>EventModelDefinition</c>.</summary>
    public static IEnumerable<ClassDeclarationSyntax> DefinitionClasses(CompilationUnitSyntax root)
        => root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(x => x.BaseList?.Types.Any(t => rightmost(t.Type) == "EventModelDefinition") == true);

    /// <summary>Every slice name the definition classes in a file declare: each pattern verb's name, or its type's.</summary>
    public static IEnumerable<string> DeclaredSlices(CompilationUnitSyntax root)
        => calls(root, SliceVerbs).Where(IsOpener).Select(Named).OfType<string>();

    /// <summary>Every aggregate the definition classes in a file name.</summary>
    public static IEnumerable<string> DeclaredAggregates(CompilationUnitSyntax root)
        => calls(root, AggregateVerbs).Select(Named).OfType<string>();

    private static IEnumerable<InvocationExpressionSyntax> calls(CompilationUnitSyntax root, HashSet<string> verbs)
        => DefinitionClasses(root)
            .SelectMany(x => x.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == "Configure"))
            .SelectMany(x => x.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(x => x.Expression is MemberAccessExpressionSyntax access && verbs.Contains(memberName(access)));

    /// <summary>
    /// Whether a call OPENS a slice — <c>model.Command&lt;T&gt;()</c>, called on the builder itself — as against
    /// the same verb as a role further down a chain, <c>model.Slice("X").Command&lt;T&gt;()</c>.
    /// </summary>
    internal static bool IsOpener(InvocationExpressionSyntax call)
        => call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax } access && SliceVerbs.Contains(memberName(access));

    /// <summary>What a call names: its generic type argument, else its first argument when that is a string literal.</summary>
    internal static string? Named(InvocationExpressionSyntax call)
    {
        if (call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } && generic.TypeArgumentList.Arguments.Count > 0)
            return rightmost(generic.TypeArgumentList.Arguments[0]);

        return call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;
    }

    /// <summary>The feature a class is bound to by <c>[BobcatFeature("…")]</c>, or null.</summary>
    public static string? FeatureOf(ClassDeclarationSyntax type)
        => type.AttributeLists.SelectMany(x => x.Attributes)
            .Where(x => rightmost(x.Name) is "BobcatFeature" or "BobcatFeatureAttribute")
            .Select(x => x.ArgumentList?.Arguments.FirstOrDefault()?.Expression)
            .OfType<LiteralExpressionSyntax>()
            .Select(x => x.Token.ValueText)
            .FirstOrDefault();

    private static string memberName(MemberAccessExpressionSyntax access) => access.Name.Identifier.ValueText;

    private static string rightmost(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => rightmost(qualified.Right),
        AliasQualifiedNameSyntax alias => rightmost(alias.Name),
        GenericNameSyntax generic => generic.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => type.ToString()
    };

    private static string? NamespaceOf(CompilationUnitSyntax root)
        => root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

    /// <summary>The usings <paramref name="fresh"/> has that <paramref name="root"/> lacks, as an insertion after the file's last using.</summary>
    private static IEnumerable<(int, string)> missingUsings(string text, CompilationUnitSyntax root, CompilationUnitSyntax fresh, string? ownNamespace,
        string newline)
    {
        var have = root.Usings.Select(x => x.Name?.ToString()).ToHashSet(StringComparer.Ordinal);
        var missing = fresh.Usings.Where(x => x.GlobalKeyword.Span.Length == 0 && x.Alias is null && x.StaticKeyword.Span.Length == 0)
            .Select(x => x.Name?.ToString()).OfType<string>()
            .Where(x => x != ownNamespace && !have.Contains(x))
            .ToList();
        if (missing.Count == 0) yield break;

        var block = string.Join("\n", missing.Select(x => $"using {x};"));
        if (root.Usings.Count > 0)
        {
            yield return insertion(text, root.Usings.Last().FullSpan.End, block, newline, blankBefore: false);
        }
        else
        {
            // Before the first member, after any header comment
            var first = (SyntaxNode?)root.Members.FirstOrDefault();
            var at = first?.SpanStart ?? root.EndOfFileToken.FullSpan.Start;
            yield return (at, (block + "\n\n").Replace("\n", newline));
        }
    }

    /// <summary>
    /// Re-indent a block to <paramref name="indent"/>: leading and trailing blank lines dropped, the
    /// common indentation removed, every non-blank line prefixed.
    /// </summary>
    internal static string Reindent(string block, string indent)
    {
        var lines = block.Replace("\r\n", "\n").Split('\n').Select(x => x.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var common = lines.Where(x => x.Length > 0).Select(x => x.TakeWhile(char.IsWhiteSpace).Count()).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(x => x.Length == 0 ? "" : indent + x[common..]));
    }

    internal static string IndentOf(string text, int position)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        return new string(text.Skip(lineStart).Take(position - lineStart).TakeWhile(c => c is ' ' or '\t').ToArray());
    }

    private static string newlineOf(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    /// <summary>
    /// The text to insert at <paramref name="position"/>, which is normally the start of a line: the
    /// block on lines of its own, after a blank line when <paramref name="blankBefore"/>.
    /// </summary>
    private static (int, string) insertion(string text, int position, string block, string newline, bool blankBefore)
    {
        var builder = new StringBuilder();
        var atLineStart = position == 0 || text[position - 1] == '\n';
        if (!atLineStart) builder.Append('\n');
        if (blankBefore) builder.Append('\n');
        builder.Append(block);
        builder.Append('\n');

        // A block written at the very end of a file with no final newline keeps the file's last line its own
        return (position, builder.ToString().Replace("\n", newline));
    }

    /// <summary>Apply insertions, last first, so every offset still means what Roslyn said.</summary>
    private static string apply(string text, IReadOnlyList<(int Position, string Text)> edits)
    {
        var builder = new StringBuilder(text);
        foreach (var (position, inserted) in edits.OrderByDescending(x => x.Position)) builder.Insert(position, inserted);
        return builder.ToString();
    }

    private static string remove(string text, IReadOnlyList<(int Position, int Length)> removals)
    {
        var builder = new StringBuilder(text);
        foreach (var (position, length) in removals.OrderByDescending(x => x.Position)) builder.Remove(position, length);
        return builder.ToString();
    }

    private static bool samePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    private static string counted(IReadOnlyList<string> names, string noun)
        => $"{names.Count} {noun}{(names.Count == 1 ? "" : "s")} ({string.Join(", ", names)})";

    private static string names(IEnumerable<BaseTypeDeclarationSyntax> types) => string.Join(", ", types.Select(x => x.Identifier.ValueText));

    private static string sliceNames(IEnumerable<CuratedSlice> slices) => string.Join(", ", slices.Select(x => x.Name));

    private static ImportCallout callout(string code, string path, string message) => new("", code, [], message, null) { File = path };

    private static ImportCallout unparseable(string path, string? error)
        => callout(ImportCalloutCodes.MergeSkippedUnparseable, path,
            $"{Path.GetFileName(path)} does not parse ({error}), so nothing was merged into it. Fix it and import again.");

    private static ImportCallout noInsertionPoint(string path, string message) => callout(ImportCalloutCodes.MergeSkippedNoInsertionPoint, path, message);

    private static ImportCallout ambiguous(string path, string message) => callout(ImportCalloutCodes.MergeSkippedAmbiguous, path, message);
}

/// <summary>
/// What a directory of C# already declares — types, slices, aggregates, specifications — so the
/// additive re-import never declares anything twice, even where the user moved it to another file.
/// <c>bin</c>, <c>obj</c> and dot-folders are skipped.
/// </summary>
public sealed class SourceIndex
{
    private readonly Dictionary<string, FileFacts> _files = new(StringComparer.Ordinal);

    private sealed record FileFacts(
        IReadOnlyList<BaseTypeDeclarationSyntax> Types,
        IReadOnlyList<string> Slices,
        IReadOnlyList<string> Aggregates,
        IReadOnlyList<(string Feature, string Method)> Specs,
        IReadOnlyList<string> Features);

    /// <summary>Index every <c>.cs</c> file under <paramref name="directory"/>.</summary>
    public static SourceIndex Scan(string directory)
    {
        var index = new SourceIndex();
        if (!Directory.Exists(directory)) return index;

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar);
            if (relative.SkipLast(1).Any(x => x is "bin" or "obj" || x.StartsWith('.'))) continue;
            index.Update(file, File.ReadAllText(file));
        }

        return index;
    }

    /// <summary>(Re)index one file, after it is written.</summary>
    public void Update(string path, string content)
    {
        var root = CSharpSyntaxTree.ParseText(content, ImportMerge.Parse).GetCompilationUnitRoot();
        var features = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Select(x => (Feature: ImportMerge.FeatureOf(x), Class: x))
            .Where(x => x.Feature is not null)
            .ToList();
        var specs = features
            .SelectMany(x => x.Class.Members.OfType<MethodDeclarationSyntax>().Select(m => (x.Feature!, m.Identifier.ValueText)))
            .ToList();

        _files[Path.GetFullPath(path)] = new FileFacts(ImportMerge.TopLevelTypes(root).ToList(), ImportMerge.DeclaredSlices(root).ToList(),
            ImportMerge.DeclaredAggregates(root).ToList(), specs, features.Select(x => x.Feature!).ToList());
    }

    /// <summary>Every declaration of a type of this simple name, and the file it is in.</summary>
    public IReadOnlyList<(string Path, BaseTypeDeclarationSyntax Declaration)> TypesNamed(string name)
        => _files.SelectMany(f => f.Value.Types.Where(t => t.Identifier.ValueText == name).Select(t => (f.Key, t))).ToList();

    /// <summary>Every type name declared.</summary>
    public IReadOnlyCollection<string> TypeNames
        => _files.Values.SelectMany(x => x.Types).Select(x => x.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a definition already declares a slice of this name.</summary>
    public bool DeclaresSlice(string name) => _files.Values.Any(x => x.Slices.Contains(name, StringComparer.Ordinal));

    /// <summary>Whether a definition already names this aggregate (Aggregate, ForAggregate, Against, StartsStream).</summary>
    public bool DeclaresAggregate(string name) => _files.Values.Any(x => x.Aggregates.Contains(name, StringComparer.Ordinal));

    /// <summary>Whether a class bound to <paramref name="feature"/> already has a method of this name.</summary>
    public bool HasSpec(string feature, string method) => _files.Values.Any(x => x.Specs.Contains((feature, method)));

    /// <summary>The files holding a class bound to <paramref name="feature"/>.</summary>
    public IReadOnlyList<string> FilesWithFeature(string feature)
        => _files.Where(x => x.Value.Features.Contains(feature, StringComparer.Ordinal)).Select(x => x.Key).ToList();
}
