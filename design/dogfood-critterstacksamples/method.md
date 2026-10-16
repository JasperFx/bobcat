# Dogfood brief (bobcat#559)

Goal: adopt Bobcat in CritterStackSamples test projects AS A PACKAGE CONSUMER would, and record every
bit of friction. The findings are the deliverable; the ported code is scratch and is thrown away.

## Hard rules (shared machine)
- Edit ONLY under /tmp/dogfood559/samples/<YourSample>/ and write findings to /tmp/dogfood559/findings/<YourSample>.md.
  Never edit ~/code/CritterStackSamples, ~/code/bobcat*, ~/code/ai-skills, ~/code/wolverine. Read them freely.
- Never kill processes you did not start (no pkill/killall of dotnet/testhost/MSBuild), never touch docker
  containers (no stop/restart/rm/compose), no `dotnet build-server shutdown`, no clearing NuGet caches.
- No git push, no gh writes (no issues, no comments). Read-only gh is fine.
- Scratch files: prefix with your sample name.

## Setup to use
- Packages from nuget.org: Bobcat, Bobcat.Xunit, Bobcat.Generators 0.60.4; WolverineFx.Bobcat 6.51.0-alpha.1
  (it depends on Bobcat 0.60.4 and WolverineFx 6.51.0, so bump the sample's WolverineFx.* to 6.51.0 and
  Marten/Polecat to whatever 6.51.0 needs). xunit.v3 4.x on Microsoft.Testing.Platform 2. net10.0 for the test project
  (bump the app too if needed; record it).
- Postgres: Host=localhost;Port=5445;Database=dogfood_<lowercasesamplename>;Username=postgres;Password=postgres
  (the database already exists). Override the sample's connection string through config/env, not by hard-coding where avoidable.
- Learn Bobcat FIRST from the skill an outside agent would have: ~/code/ai-skills/skills/critterstack-sdd-bobcat-authoring/SKILL.md
  (and the sibling critterstack-sdd-* skills). Record anywhere the skill was wrong, missing, or misleading — that is a key finding.
  WolverineFx.Bobcat source is at ~/code/wolverine (src/Testing/WolverineFx.Bobcat or search) if you need to read the API.
- AFTER your own attempt, compare with the in-repo store-only port at ~/code/bobcat-main-dogfood-samples/samples/<Sample>
  (where one exists) — note differences worth reporting, but do not just copy it.

## What to do per sample
1. Convert the Tests project (xUnit v2/VSTest) into a Bobcat spec project. Record exactly what it took: packages,
   global.json (test runner MTP), InterceptorsNamespaces / BOBCAT037, base classes, host fixture, assembly attributes.
2. Port every existing test to a projected spec, preferring WolverineHttpSpec / WolverineSpec vocabulary (GivenEvents,
   WhenPosted, ThenEvents, ThenAggregate, ThenReadModel, ThenRefusedWith, ...), CritterStackSpec where Wolverine is absent.
   Bind specs to Event Model slices ([BobcatSpec(typeof(Command))], [assembly: EventModelName("...")]).
   Note every place the vocabulary was missing and you had to fall back to raw Alba/Shouldly.
3. Build and run (`dotnet test --project Tests` or run the test exe). Record every diagnostic (BOBCATnnn, CS errors in
   generated code), crash, surprising render (paste console output snippets), and the final pass/fail counts.
4. Check the spec manifest: `BOBCAT_LIST_SPECS=/tmp/dogfood559/<sample>-manifest.json <test exe> --list-tests` — does
   every spec appear with the identity you expect? Check the Event Model: obj/**/generated/**/BobcatEventModelSource.g.cs —
   are slices, patterns, commands/events/read models right?
5. Optional if cheap: BOBCAT_RESULTS=/tmp/dogfood559/<sample>-results run, glance at the JSON.
Time-box: about 45 minutes of effort per sample; if blocked, record the blocker precisely and move on.
A broker (RabbitMQ) or SQL Server is NOT available: if a sample needs one, try to stub the transport
(Wolverine's StubAllExternalTransports / DisableAllExternalWolverineTransports) and record it; if impossible, build-only.

## Findings file format (/tmp/dogfood559/findings/<Sample>.md)
# <Sample>
- Tests before: N (files, frameworks)   - Specs after: N, passing/failing/skipped
## Adoption steps (what it took)
## Diagnostics, crashes, surprising renders (each: symptom verbatim, cause, workaround, severity)
## Vocabulary used / missing (which spec base, which steps; each gap with the code you had to write instead)
## Event Model and manifest (right or wrong, with evidence)
## ai-skills gaps (what the skill said vs what was true)
## Candidate issues (one line each, with target repo: Bobcat / WolverineFx.Bobcat (wolverine) / JasperFx / ai-skills)
Be concrete and verbatim; no padding. Your final message: a 10-line summary of the most important findings.
