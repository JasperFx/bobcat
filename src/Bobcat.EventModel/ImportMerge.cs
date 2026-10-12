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
    /// Add what the board has and the definition lacks, at the end of <paramref name="existing"/>'s
    /// <c>Configure</c>: a declaration for each slice no definition in the project declares, written against
    /// what the definition already says at its top (<c>InChapter</c>, <c>InDomain</c>, <c>ForAggregate</c>),
    /// and, for a slice it does declare, a statement reopening it by name with the roles the board gives it and
    /// no declaration says. Existing statements are never touched; what cannot be added is reported.
    /// </summary>
    /// <param name="existing">The definition file as it is.</param>
    /// <param name="generated">The definition a fresh import would write, for the usings it needs.</param>
    /// <param name="slices">The slices that belong in this definition, in board order.</param>
    /// <param name="path">The file's full path.</param>
    /// <param name="index">Every slice, aggregate and type the project already declares.</param>
    public static Result MergeDefinition(string existing, string generated, IReadOnlyList<CuratedSlice> slices, string path, SourceIndex index)
        => MergeDefinition(existing, generated, slices, path, index, null);

    /// <inheritdoc cref="MergeDefinition(string, string, IReadOnlyList{CuratedSlice}, string, SourceIndex)"/>
    /// <param name="boardSlices">
    /// Every slice name on the board, every chapter, so a slice this definition declares and the board does not
    /// have is reported (<c>slice-removed-on-board</c>); null skips that check.
    /// </param>
    public static Result MergeDefinition(string existing, string generated, IReadOnlyList<CuratedSlice> slices, string path, SourceIndex index,
        IReadOnlyCollection<string>? boardSlices)
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

        // What the definition says on the builder itself, which a slice added to it must agree with
        var parameter = method.ParameterList.Parameters[0].Identifier.ValueText;
        var top = body.Statements.OfType<ExpressionStatementSyntax>()
            .Select(x => x.Expression).OfType<InvocationExpressionSyntax>()
            .Where(x => x.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver }
                        && receiver.Identifier.ValueText == parameter)
            .ToList();
        string? said(string verb) => top.Where(x => memberName((MemberAccessExpressionSyntax)x.Expression) == verb).Select(Named).FirstOrDefault(x => x is not null);

        // The default in force at a position: InChapter and the rest apply to the slices opened after them
        string? saidBefore(string verb, int position) => top
            .Where(x => x.SpanStart < position && memberName((MemberAccessExpressionSyntax)x.Expression) == verb)
            .Select(Named).LastOrDefault(x => x is not null);

        var chapter = said("InChapter");
        var domain = said("InDomain");
        var aggregate = said("ForAggregate");
        var stubbed = index.TypeNames;
        var file = Path.GetFileName(path);

        var reported = new List<ImportCallout>();
        var reopens = new List<(CuratedSlice Slice, IReadOnlyList<DeclaredRole> Roles, string Statement)>();
        foreach (var slice in slices.Except(missing))
        {
            var name = SliceName(slice);
            var board = SyntaxFactory.ParseStatement(CSharpModelWriter.SliceDeclaration(slice, stubbed, chapter, domain, aggregate), options: Parse);
            var boardRoles = RolesOf(board).DistinctBy(x => x.Key).ToList();
            var boardKeys = boardRoles.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            var declared = index.RolesOf(name);
            var declaredKeys = declared.Select(x => x.Role.Key).ToHashSet(StringComparer.Ordinal);
            var own = body.Statements.Where(x => openerName(x) == name).ToList();

            // The roles the board gives the slice and no declaration of it says, anywhere in the project
            var toAdd = boardRoles.Where(x => !declaredKeys.Contains(x.Key)).ToList();
            if (toAdd.Count > 0)
            {
                if (own.Count > 0)
                {
                    reopens.Add((slice, toAdd, reopen(name, toAdd, own, parameter, saidBefore, body.CloseBraceToken.SpanStart)));
                }
                else
                {
                    var where = index.FilesDeclaringSlice(name).Select(Path.GetFileName).ToList();
                    var at = where.Count == 0 ? "another file" : string.Join(", ", where);
                    reported.Add(callout(ImportCalloutCodes.SliceDiffersFromBoard, path,
                            $"The board gives slice {slice.Name} {displays(toAdd)}, which no declaration of it says. It is declared in {at}, not in {file}, "
                            + "so nothing was added; add the calls by hand if the board is right.")
                        with
                        {
                            Slice = slice.Name, Roles = toAdd.Select(x => x.Display).ToList(),
                            Fix = $"Add {string.Join("", toAdd.Select(x => x.Call))} to the declaration of slice {name} in {at}."
                        });
                }
            }

            // Roles a declaration says and the board no longer gives: a role added by hand looks the same
            var removed = declared.Where(x => !x.Role.Implied && !boardKeys.Contains(x.Role.Key)).DistinctBy(x => x.Role.Key).ToList();
            if (removed.Count > 0)
            {
                var where = string.Join(", ", removed.Select(x => Path.GetFileName(x.Path)).Distinct(StringComparer.Ordinal));
                reported.Add(callout(ImportCalloutCodes.SliceRoleRemovedOnBoard, path,
                        $"The board no longer gives slice {slice.Name} {displays(removed.Select(x => x.Role))}, which its declaration in {where} says. "
                        + "Nothing was removed: if the board is right, delete the call; if you added it by hand, leave it.")
                    with
                    {
                        Slice = slice.Name, Roles = removed.Select(x => x.Role.Display).ToList(),
                        Fix = $"Delete {string.Join(", ", removed.Select(x => x.Role.Call))} from the declaration of slice {name} in {where}."
                    });
            }

            if (own.Count == 0) continue;

            // Declared to declared: what the board's declaration would say against what the file's statements say
            var boardFacts = FactsOf([board]);
            var code = FactsOf(own);
            reported.AddRange(new[]
            {
                patternDiffers(slice, name, boardFacts, code, path),
                triggerDiffers(slice, name, boardFacts, code, path),
                aggregateDiffers(slice, name, boardFacts, aggregate, code, saidBefore("ForAggregate", own[0].SpanStart), path)
            }.OfType<ImportCallout>());
        }

        if (boardSlices is not null) reported.AddRange(removedSlices([body], path, boardSlices));

        var statements = new List<string>();
        var aggregates = new List<string>();
        foreach (var name in missing.SelectMany(CSharpModelWriter.AggregatesOf).Select(CSharpModelWriter.Identifiers.Sanitize).Distinct(StringComparer.Ordinal))
        {
            if (name.Length == 0 || index.DeclaresAggregate(name) || name == aggregate) continue;
            aggregates.Add(name);
            statements.Add(CSharpModelWriter.AggregateDeclaration(name, stubbed));
        }

        statements.AddRange(missing.Select(x => CSharpModelWriter.SliceDeclaration(x, stubbed, chapter, domain, aggregate)));
        statements.AddRange(reopens.Select(x => x.Statement));
        if (statements.Count == 0) return Result.Unchanged(reported.ToArray());

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
                $"Declared slice {x.Name} in {file}: the board has it and no definition in the project declared it.") with { Slice = x.Name })
            .Concat(aggregates.Select(x => callout(ImportCalloutCodes.MergedAggregateDeclaration, path,
                $"Declared aggregate {x} in {file}: a slice added there names it and no definition declared it.") with { Type = x }))
            .Concat(reopens.Select(x => callout(ImportCalloutCodes.MergedSliceRoles, path,
                    $"Added {displays(x.Roles)} to slice {x.Slice.Name} in {file}: the board gives it {(x.Roles.Count == 1 ? "that role" : "those roles")} and no declaration said {(x.Roles.Count == 1 ? "it" : "them")}. "
                    + $"A statement at the end of Configure reopens the slice by name; its declaration is unchanged.")
                with { Slice = x.Slice.Name, Roles = x.Roles.Select(r => r.Display).ToList() }))
            .ToList();
        callouts.AddRange(reported);

        var parts = new List<string>();
        if (missing.Count > 0) parts.Add(counted(missing.Select(x => x.Name).ToList(), "slice declaration"));
        if (aggregates.Count > 0) parts.Add(counted(aggregates, "aggregate declaration"));
        if (reopens.Count > 0)
        {
            var roles = reopens.Sum(x => x.Roles.Count);
            parts.Add($"{roles} role{(roles == 1 ? "" : "s")} ({string.Join("; ", reopens.Select(x => $"{x.Slice.Name}: {displays(x.Roles)}"))})");
        }

        var added = missing.Select(x => x.Name).Concat(aggregates)
            .Concat(reopens.SelectMany(x => x.Roles.Select(r => $"{x.Slice.Name}.{r.Display}")))
            .ToList();
        return new Result(apply(text, edits), added, callouts) { Description = string.Join(" and ", parts) };
    }

    /// <summary>
    /// The statement that reopens an existing slice by name to add <paramref name="roles"/> to it — JasperFx folds
    /// every statement naming a slice into one slice, unioning the roles. It repeats the declaration's chapter or
    /// domain when the default in force at the end of <c>Configure</c> is another, or the two would disagree.
    /// </summary>
    private static string reopen(string name, IReadOnlyList<DeclaredRole> roles, IReadOnlyList<StatementSyntax> own, string parameter,
        Func<string, int, string?> saidBefore, int end)
    {
        var declared = FactsOf(own);
        var calls = new List<string>();

        var chapter = declared.Chapter ?? saidBefore("InChapter", own[0].SpanStart);
        if (chapter is not null && chapter != saidBefore("InChapter", end)) calls.Add($".InChapter({literal(chapter)})");

        var domain = declared.Domain ?? saidBefore("InDomain", own[0].SpanStart);
        if (domain is not null && domain != saidBefore("InDomain", end)) calls.Add($".InDomain({literal(domain)})");

        calls.AddRange(roles.Select(x => x.Call));

        var opener = $"{parameter}.Slice({literal(name)})";
        if (calls.Count == 1) return $"{opener}{calls[0]};";

        // One call per line under the opener, the shape the import writes a declaration in
        return opener + "\n" + string.Join("\n", calls.Select((x, i) => "    " + x + (i == calls.Count - 1 ? ";" : "")));
    }

    private static string literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static string displays(IEnumerable<DeclaredRole> roles) => string.Join(", ", roles.Select(x => x.Display));

    private static ImportCallout? patternDiffers(CuratedSlice slice, string name, SliceFacts board, SliceFacts code, string path)
    {
        if (board.Pattern is not { } pattern || pattern == code.Pattern) return null;

        var file = Path.GetFileName(path);
        var opener = pattern is "Command" or "View" or "Automation" or "Translation"
            ? $"open it with model.{pattern}({literal(name)})"
            : $"add .Pattern(SlicePattern.{pattern})";
        return callout(ImportCalloutCodes.SlicePatternDiffers, path,
                $"The board says slice {slice.Name} is a {pattern} slice; its declaration in {file} says {(code.Pattern is null ? "no pattern" : $"{code.Pattern}")}. "
                + "The declaration was left as it is.")
            with
            {
                Slice = slice.Name,
                Fix = $"In {file}, {opener} in place of {code.Opener ?? "its opener"}" + (code.Pattern is null ? "." : $", or keep {code.Pattern} if the code is right.")
            };
    }

    private static ImportCallout? triggerDiffers(CuratedSlice slice, string name, SliceFacts board, SliceFacts code, string path)
    {
        var label = board.TriggerLabel is not null && board.TriggerLabel != code.TriggerLabel;
        var kind = board.TriggerKind is not null && board.TriggerKind != code.TriggerKind;
        if (!label && !kind) return null;

        var file = Path.GetFileName(path);
        return callout(ImportCalloutCodes.SliceTriggerDiffers, path,
                $"The board says slice {slice.Name} is triggered by {describeTrigger(board)}; its declaration in {file} says {describeTrigger(code)}. "
                + "The declaration was left as it is.")
            with
            {
                Slice = slice.Name,
                Fix = code.TriggerCall is null
                    ? $"Add {board.TriggerCall} to the declaration of slice {name} in {file}."
                    : $"Replace {code.TriggerCall} with {board.TriggerCall} in the declaration of slice {name} in {file}."
            };
    }

    private static string describeTrigger(SliceFacts facts)
        => (facts.TriggerLabel, facts.TriggerKind) switch
        {
            (null, null) => "nothing",
            ({ } label, null) => $"'{label}'",
            (null, { } kind) => kind,
            ({ } label, { } kind) => $"'{label}' ({kind})"
        };

    /// <summary>
    /// <c>slice-aggregate-differs</c> when the board claims an aggregate for the slice (a stream it starts, one
    /// it decides against) and the declaration says another. A board that claims none is no disagreement (the
    /// <c>missing-aggregate</c> callout asked for the declaration), nor is a declared DCB decider, which is what
    /// the import recommends for a slice on several streams.
    /// </summary>
    private static ImportCallout? aggregateDiffers(CuratedSlice slice, string name, SliceFacts board, string? boardDefault, SliceFacts code,
        string? codeDefault, string path)
    {
        var (boardStarts, boardAgainst) = board.Effective(boardDefault);
        if (boardStarts is null && boardAgainst.Count == 0) return null;
        if (code.Decider is not null) return null;

        var (codeStarts, codeAgainst) = code.Effective(codeDefault);
        if (!code.NoAggregate && boardStarts == codeStarts && boardAgainst.ToHashSet(StringComparer.Ordinal).SetEquals(codeAgainst)) return null;

        var file = Path.GetFileName(path);
        string calls(SliceFacts facts, string? fallback) => facts.AggregateCalls.Count > 0
            ? string.Join("", facts.AggregateCalls)
            : fallback is not null && facts.Pattern == "Command" ? $"nothing (the chapter's ForAggregate default, {fallback}, applies)" : "nothing";

        var inferred = slice.Inferred.Count > 0 ? " The board's aggregate is the import's inference, not something the board states." : "";

        // The flags that make the board say what the code says, so the callout goes away with the code kept
        var flags = code.NoAggregate
            ? []
            : (codeStarts is null ? [] : new[] { $"--starts-stream {slice.Name}={codeStarts}" })
            .Concat(codeAgainst.Select(x => $"--aggregate {slice.Name}={x}"))
            .ToList();

        return callout(ImportCalloutCodes.SliceAggregateDiffers, path,
                $"The board says slice {slice.Name} {describeAggregate(boardStarts, boardAgainst, false)}; its declaration in {file} says it {describeAggregate(codeStarts, codeAgainst, code.NoAggregate)}. "
                + "The declaration was left as it is." + inferred)
            with
            {
                Slice = slice.Name,
                Aggregates = new[] { boardStarts }.Concat(boardAgainst).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
                Fix = $"In {file}, give slice {name} {calls(board, boardDefault)} in place of {calls(code, codeDefault)}.",
                Override = flags.Count > 0 && slice.Inferred.Count > 0 ? string.Join(" ", flags) : null
            };
    }

    private static string describeAggregate(string? starts, IReadOnlyList<string> against, bool noAggregate)
    {
        if (noAggregate) return "has none (NoAggregate())";

        var parts = new List<string>();
        if (starts is not null) parts.Add($"starts the {starts} stream");
        if (against.Count > 0) parts.Add($"decides against {string.Join(", ", against)}");
        return parts.Count == 0 ? "has no aggregate" : string.Join(" and ", parts);
    }

    /// <summary>
    /// A <c>slice-removed-on-board</c> callout for each slice the definitions in <paramref name="bodies"/> declare
    /// and the board does not have. Report only: the slice may have been declared by hand.
    /// </summary>
    private static IEnumerable<ImportCallout> removedSlices(IEnumerable<BlockSyntax> bodies, string path, IReadOnlyCollection<string> boardSlices)
    {
        var file = Path.GetFileName(path);
        var board = boardSlices as IReadOnlySet<string> ?? boardSlices.ToHashSet(StringComparer.Ordinal);
        foreach (var group in bodies.SelectMany(x => x.Statements)
                     .Select(x => (Statement: x, Slice: openerName(x)))
                     .Where(x => x.Slice is not null && !board.Contains(x.Slice))
                     .GroupBy(x => x.Slice!, StringComparer.Ordinal))
        {
            var lines = group.Select(x => x.Statement.GetLocation().GetLineSpan().StartLinePosition.Line + 1).ToList();
            yield return callout(ImportCalloutCodes.SliceRemovedOnBoard, path,
                    $"{file} declares slice {group.Key}, which the board does not have. Nothing was removed: if the board removed it, delete the declaration; "
                    + "if you declared it by hand, leave it.")
                with
                {
                    Slice = group.Key,
                    Fix = $"Delete the statement{(lines.Count == 1 ? "" : "s")} declaring slice {group.Key} from {file} (line{(lines.Count == 1 ? "" : "s")} {string.Join(", ", lines)})."
                };
        }
    }

    /// <summary>
    /// <c>slice-removed-on-board</c> callouts for a definition file the import did not write to this time, such
    /// as one for a chapter the board no longer has. Only files carrying the import's own header are compared.
    /// </summary>
    /// <param name="existing">The definition file as it is.</param>
    /// <param name="path">Its full path.</param>
    /// <param name="boardSlices">Every slice name on the board.</param>
    public static IReadOnlyList<ImportCallout> SlicesRemovedOnBoard(string existing, string path, IReadOnlyCollection<string> boardSlices)
    {
        var tree = TryParse(existing, out _);
        if (tree is null) return [];

        var bodies = DefinitionClasses(tree.GetCompilationUnitRoot())
            .SelectMany(x => x.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == "Configure"))
            .Select(x => x.Body).OfType<BlockSyntax>();
        return removedSlices(bodies, path, boardSlices).ToList();
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
    /// The roles a slice is compared on and a re-import adds: what the board says the slice does, not how it is
    /// wired. <c>LinksToSpecification</c> is left out: a generated specification binds itself with
    /// <c>[BobcatSlice]</c>, and the spec project publishes its own links, so a missing one changes nothing.
    /// <c>From</c> is <c>On</c>'s other name.
    /// </summary>
    private static readonly Dictionary<string, string> ComparedRoles = new(StringComparer.Ordinal)
    {
        ["Emits"] = "Emits", ["Publishes"] = "Publishes", ["On"] = "On", ["From"] = "On", ["Reads"] = "Reads",
        ["Produces"] = "Produces", ["Projects"] = "Projects"
    };

    /// <summary>One role a slice statement declares.</summary>
    /// <param name="Verb">The role's verb, aliases folded: <c>Emits</c>, <c>On</c>, …</param>
    /// <param name="Name">The type it names: the generic argument's simple name, or the string.</param>
    /// <param name="Call">The call as written, e.g. <c>.Emits&lt;Booked&gt;()</c>.</param>
    /// <param name="Implied">Said by the opener (<c>View&lt;T&gt;()</c> produces T), so it cannot be deleted on its own.</param>
    public readonly record struct DeclaredRole(string Verb, string Name, string Call, bool Implied = false)
    {
        /// <summary>The identity roles are compared by.</summary>
        public string Key => $"{Verb}:{Name}";

        /// <summary>How a callout says it: <c>Emits(Booked)</c>.</summary>
        public string Display => $"{Verb}({Name})";
    }

    /// <summary>The roles a slice statement declares, in the order written, including any its opener implies.</summary>
    internal static IEnumerable<DeclaredRole> RolesOf(StatementSyntax statement)
    {
        var calls = statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Reverse().ToList();

        // View<T>() produces T and Query<T>() reads it, as JasperFx's verbs do
        if (calls.FirstOrDefault(IsOpener) is { } opener && Named(opener) is { } named)
        {
            var access = (MemberAccessExpressionSyntax)opener.Expression;
            var generic = access.Name is GenericNameSyntax;
            switch (memberName(access))
            {
                case "View":
                    yield return new DeclaredRole("Produces", named, generic ? $".Produces<{named}>()" : $".Produces({literal(named)})", Implied: true);
                    break;
                case "Query" when generic:
                    yield return new DeclaredRole("Reads", named, $".Reads<{named}>()", Implied: true);
                    break;
            }
        }

        foreach (var call in calls)
        {
            if (call.Expression is not MemberAccessExpressionSyntax access || IsOpener(call)) continue;
            if (!ComparedRoles.TryGetValue(memberName(access), out var verb) || Named(call) is not { } name) continue;

            var written = access.Name.ToString() + call.ArgumentList;
            yield return new DeclaredRole(verb, name, "." + (verb == memberName(access) ? written : verb + written[memberName(access).Length..]));
        }
    }

    /// <summary>What a slice's statements say about it beyond its roles: its pattern, trigger, aggregate, chapter and domain.</summary>
    internal sealed record SliceFacts
    {
        public string? Opener { get; init; }
        public string? Pattern { get; init; }
        public string? TriggerLabel { get; init; }
        public string? TriggerKind { get; init; }
        public string? TriggerCall { get; init; }
        public string? StartsStream { get; init; }
        public IReadOnlyList<string> Against { get; init; } = [];
        public bool NoAggregate { get; init; }
        public string? Decider { get; init; }
        public IReadOnlyList<string> AggregateCalls { get; init; } = [];
        public string? Chapter { get; init; }
        public string? Domain { get; init; }

        /// <summary>
        /// The stream the slice starts and the aggregates it decides against, as JasperFx resolves them: a command
        /// slice that names none, and says neither <c>NoAggregate()</c> nor a decider, takes the chapter default.
        /// </summary>
        public (string? Starts, IReadOnlyList<string> Against) Effective(string? chapterDefault)
        {
            var against = Against.Where(x => x != StartsStream).Distinct(StringComparer.Ordinal).ToList();
            if (against.Count == 0 && StartsStream is null && !NoAggregate && Decider is null && Pattern == "Command" && chapterDefault is not null)
                against.Add(chapterDefault);

            return (StartsStream, against);
        }
    }

    internal static SliceFacts FactsOf(IReadOnlyList<StatementSyntax> statements)
    {
        string? opener = null, pattern = null, label = null, kind = null, triggerCall = null, starts = null, decider = null, chapter = null, domain = null;
        var against = new List<string>();
        var aggregateCalls = new List<string>();
        var noAggregate = false;

        foreach (var call in statements.SelectMany(x => x.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Reverse()))
        {
            if (call.Expression is not MemberAccessExpressionSyntax access) continue;
            var verb = memberName(access);
            var written = "." + access.Name + call.ArgumentList;

            if (IsOpener(call))
            {
                opener ??= call.ToString();
                pattern ??= verb switch
                {
                    "Command" or "View" or "Automation" or "Translation" or "Query" => verb,
                    _ => null
                };

                if (verb == "Automation" && access.Name is GenericNameSyntax) kind ??= "JobScheduler";
                continue;
            }

            switch (verb)
            {
                case "Pattern" when call.ArgumentList.Arguments.FirstOrDefault()?.Expression is MemberAccessExpressionSyntax member:
                    pattern ??= member.Name.Identifier.ValueText;
                    break;

                case "TriggeredBy":
                    triggerCall ??= written;
                    foreach (var argument in call.ArgumentList.Arguments.Select(x => x.Expression))
                    {
                        if (argument is LiteralExpressionSyntax literalLabel && literalLabel.IsKind(SyntaxKind.StringLiteralExpression)) label ??= literalLabel.Token.ValueText;
                        else if (argument is MemberAccessExpressionSyntax member) kind ??= member.Name.Identifier.ValueText;
                    }

                    break;

                case "StartsStream":
                    starts ??= Named(call);
                    aggregateCalls.Add(written);
                    break;

                case "Against" or "UsesAggregate":
                    if (Named(call) is { } aggregate) against.Add(aggregate);
                    aggregateCalls.Add(written);
                    break;

                case "NoAggregate":
                    noAggregate = true;
                    aggregateCalls.Add(written);
                    break;

                case "DeciderModel" or "DcbModel":
                    decider ??= Named(call);
                    aggregateCalls.Add(written);
                    break;

                case "InChapter":
                    chapter ??= Named(call);
                    break;

                case "InDomain":
                    domain ??= Named(call);
                    break;
            }
        }

        return new SliceFacts
        {
            Opener = opener, Pattern = pattern, TriggerLabel = label, TriggerKind = kind, TriggerCall = triggerCall, StartsStream = starts,
            Against = against, NoAggregate = noAggregate, Decider = decider, AggregateCalls = aggregateCalls, Chapter = chapter, Domain = domain
        };
    }

    internal static string? openerName(StatementSyntax statement)
        => statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(IsOpener)
            .Select(Named).FirstOrDefault(n => n is not null);

    /// <summary>Every statement in a file's definition classes' <c>Configure</c> that opens a slice, with the slice's name.</summary>
    internal static IEnumerable<(StatementSyntax Statement, string Slice)> SliceStatements(CompilationUnitSyntax root)
        => DefinitionClasses(root)
            .SelectMany(x => x.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == "Configure"))
            .SelectMany(x => x.Body?.Statements ?? [])
            .Select(x => (Statement: x, Slice: openerName(x)))
            .Where(x => x.Slice is not null)
            .Select(x => (x.Statement, x.Slice!));

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
        IReadOnlyList<string> Features,
        IReadOnlyList<(string Slice, ImportMerge.DeclaredRole Role)> Roles,
        bool ImportedDefinition);

    /// <summary>The header the import writes atop a definition, which marks it as the import's own.</summary>
    internal const string ImportHeader = "by `bobcat import-event-model`";

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

        var roles = ImportMerge.SliceStatements(root)
            .SelectMany(x => ImportMerge.RolesOf(x.Statement).Select(r => (x.Slice, r)))
            .ToList();
        var imported = content.Contains(ImportHeader, StringComparison.Ordinal) && ImportMerge.DefinitionClasses(root).Any();

        _files[Path.GetFullPath(path)] = new FileFacts(ImportMerge.TopLevelTypes(root).ToList(), ImportMerge.DeclaredSlices(root).ToList(),
            ImportMerge.DeclaredAggregates(root).ToList(), specs, features.Select(x => x.Feature!).ToList(), roles, imported);
    }

    /// <summary>Every declaration of a type of this simple name, and the file it is in.</summary>
    public IReadOnlyList<(string Path, BaseTypeDeclarationSyntax Declaration)> TypesNamed(string name)
        => _files.SelectMany(f => f.Value.Types.Where(t => t.Identifier.ValueText == name).Select(t => (f.Key, t))).ToList();

    /// <summary>Every type name declared.</summary>
    public IReadOnlyCollection<string> TypeNames
        => _files.Values.SelectMany(x => x.Types).Select(x => x.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a definition already declares a slice of this name.</summary>
    public bool DeclaresSlice(string name) => _files.Values.Any(x => x.Slices.Contains(name, StringComparer.Ordinal));

    /// <summary>The files whose definitions declare a slice of this name.</summary>
    public IReadOnlyList<string> FilesDeclaringSlice(string name)
        => _files.Where(x => x.Value.Slices.Contains(name, StringComparer.Ordinal)).Select(x => x.Key).ToList();

    /// <summary>Every role any statement naming this slice declares, in any definition, and the file it is in.</summary>
    public IReadOnlyList<(ImportMerge.DeclaredRole Role, string Path)> RolesOf(string slice)
        => _files.SelectMany(f => f.Value.Roles.Where(r => r.Slice == slice).Select(r => (r.Role, f.Key))).ToList();

    /// <summary>The definition files carrying the import's header: the ones a re-import compares against the board.</summary>
    public IReadOnlyList<string> ImportedDefinitionFiles => _files.Where(x => x.Value.ImportedDefinition).Select(x => x.Key).ToList();

    /// <summary>Whether a definition already names this aggregate (Aggregate, ForAggregate, Against, StartsStream).</summary>
    public bool DeclaresAggregate(string name) => _files.Values.Any(x => x.Aggregates.Contains(name, StringComparer.Ordinal));

    /// <summary>Whether a class bound to <paramref name="feature"/> already has a method of this name.</summary>
    public bool HasSpec(string feature, string method) => _files.Values.Any(x => x.Specs.Contains((feature, method)));

    /// <summary>The files holding a class bound to <paramref name="feature"/>.</summary>
    public IReadOnlyList<string> FilesWithFeature(string feature)
        => _files.Where(x => x.Value.Features.Contains(feature, StringComparer.Ordinal)).Select(x => x.Key).ToList();
}
