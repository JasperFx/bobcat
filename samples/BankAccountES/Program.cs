using BankAccountES;
using Fisher;
using JasperFx;
using JasperFx.Events.EventModeling;
using JasperFx.Events.Projections;
using Marten;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.FluentValidation;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

// The event store is a deployment choice, not a domain one. Every handler, projection and read
// endpoint in this sample is written against the store-agnostic vocabulary — Wolverine's
// [DeciderFunction] / [Entity] / Storage.StartStream() and JasperFx.Events' IEventStoreOperations /
// IDocumentReadOperations — so the only file that names a store is this one. `EventStore=Fisher` in
// configuration (or the environment) runs the same application on an embedded SQLite file with no
// Postgres at all; the default is Marten. See README.md and CLAUDE.md, "Bobcat.CritterStack is
// store-agnostic" (issue #103).
var eventStore = builder.Configuration["EventStore"] ?? "Marten";

switch (eventStore.ToLowerInvariant())
{
    case "marten":
        builder.Services.AddMarten(opts =>
            {
                var connectionString = builder.Configuration.GetConnectionString("Marten")
                    ?? "Host=localhost;Port=5433;Database=bank_account;Username=postgres;Password=postgres";

                opts.Connection(connectionString);
                opts.DatabaseSchemaName = "bank";

                // Inline snapshots — aggregates and the transaction-history read model are always up to date.
                opts.Projections.Snapshot<Account>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<Client>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<AccountTransactions>(SnapshotLifecycle.Inline);
            })
            .IntegrateWithWolverine()
            .UseLightweightSessions();
        break;

    case "fisher":
        builder.Services.AddFisher(opts =>
            {
                // An embedded SQLite file. Relative paths resolve against the working directory, so the
                // default lands next to the host's appsettings.json; a spec run points it somewhere else.
                var connectionString = builder.Configuration.GetConnectionString("Fisher")
                    ?? "Data Source=bank_account.db";

                opts.Connection(connectionString);

                // Identical registrations — Snapshot<T> is the one shape of projection every store spells
                // the same way, because a self-aggregating document needs no store-specific base class.
                opts.Projections.Snapshot<Account>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<Client>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<AccountTransactions>(SnapshotLifecycle.Inline);
            })
            // Not optional on a fresh database. Fisher builds its schema lazily, and the first append
            // on an empty file reaches AppendPlanner.ReadCurrentVersionAsync before anything has created
            // fi_streams / fi_events — "no such table" on the first three scenarios, then a pass once
            // something else has built the tables. Marten creates them on the way in; Fisher wants the
            // schema applied at startup. See docs/sample-wiring.md footgun 13.
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
        break;

    default:
        throw new InvalidOperationException(
            $"Unknown EventStore '{eventStore}'. This sample runs on 'Marten' (Postgres, the default) or 'Fisher' (SQLite).");
}

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.Policies.AutoApplyTransactions();
    opts.UseFluentValidation();
    opts.ServiceName = "BankAccount";
});

// The Event Model OVERLAY (jasperfx#687, decision D5): naming, grouping and trigger labels only —
// never a factual role. Roles (commands, emitted events, aggregates, read models) are DERIVED from
// the Wolverine chains and DECLARED by the Bobcat specs; the overlay contributes exactly the things
// no code can express. The model name matches opts.ServiceName above because upstream,
// EventModelDiscovery.Assemble folds descriptors together by model name — a mismatch here and the
// overlay floats off as its own model instead of merging (bobcat#172).
builder.Services.AddEventModel("BankAccount", model =>
{
    model.InDomain("Banking");

    // ⚠️ No TriggeredBy on the HTTP slices, for now: wolverine#4181. Wolverine's HTTP-derived
    // source claims TriggerLabel with the verb+route ("POST /api/…"), so a human label declared
    // here loses on the provenance ladder AND mints a SourceDisagreement noise hotspot per slice.
    // When #4181 ships, restore the labels — EventModel.feature asserts the current behaviour
    // with a pointer back here, so the fix will trip that spec and flag the restoration.
    model.Slice("EnrollClient");
    model.Slice("UpdateClient");
    model.Slice("OpenAccount");
    model.Slice("DepositFunds");
    model.Slice("WithdrawFunds");

    // A message-handled slice's chain claims no TriggerLabel, so this Declared label survives.
    model.Slice("FreezeAccount").TriggeredBy("The fraud desk");
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapWolverineEndpoints(opts =>
{
    opts.UseFluentValidationProblemDetailMiddleware();
});

// RunJasperFxCommands rather than RunAsync so `dotnet run -- event-model --url …` works against
// this host — the export command builds the host without starting it and PUTs the assembled,
// provenance-stamped descriptor to a Bobcat console (wolverine#3990, bobcat#171). A bare
// `dotnet run` still runs the app exactly as before.
return await app.RunJasperFxCommands(args);
