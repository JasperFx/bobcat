# ai-skills entry for bobcat#456, ready to apply

Per this repo's rule that a capability ships with the write-up that teaches a later agent, this is the
ai-skills text for #456 — **staged rather than opened as a PR, and the reason matters.**

## Why this is a file here and not a PR over there

`~/code/ai-skills` and its `origin/main` still carry the **Gherkin-era**
`critterstack-sdd-bobcat-authoring/SKILL.md` (660 lines, a `Given events for {aggregate}` table, a
`references/gherkin-grammar.md`). The rewritten, projected-only skill — the one an agent actually
reads, with a `references/vocabulary.md` whose "Values a step takes" table is exactly where this text
belongs — exists only in the **installed** copy (`~/.agents/skills/…`, written 2026-10-12 14:41) and
in that repo's untracked `AI-SKILLS-NOTES-BOBCAT-2026-10-08.md` staging notes.

So the three things a PR could do are all wrong:

- edit the repo's stale skill — the addition lands in a document about to be replaced, teaching the
  collection vocabulary beside `.feature` steps that no longer exist;
- carry the whole installed rewrite into the PR — a 400-line rewrite nobody asked this branch to land;
- edit the installed copy in place — invisible to the repo, and lost on the next install.

The dependency is therefore: **land the projected-only authoring-skill rewrite in ai-skills, then
apply the diff below.** Noted on the Bobcat PR rather than quietly skipped. A matching entry is
appended to that repo's own staging notes.

## 1. `references/vocabulary.md` — "Values a step takes"

Three rows after the `Expect.Value(…)` row:

```markdown
| `Expect.Exactly(…)` / `Expect.InAnyOrder(…)` / `Expect.Containing(…)` / `Expect.NoneOf(…)` / `Expect.NoItems()` | what a **collection member** must hold. A bare collection is already "exactly these, in any order", so `Expect.Exactly` is the only one of these that says something a plain list does not. Each item is itself an expectation — a whole object, a `Specify<T>()`, an `Expect.Value(…)`, or a `typeof(T)` |
| `Expect.Rows(table)` / `Expect.Rows(table, SetMode.Ordered)` / `Expect.NoRowsLike(table)` | the same, written as a table of one row per element. Named apart from the list forms because a `StepTable` converts implicitly from a string, and an `Expect.Containing(table)` overload would silently swallow `Expect.Containing("vip")` over a collection of strings |
| `Expect.Value(x).Approximately(e => e.Total, 0.01m)` / `.Within(e => e.At, 2.Seconds())` / `.InOrder(e => e.Lines)` | one member judged with slack, or one collection member whose order is part of the claim. Slack on a member that cannot have any — a window over a string, an amount over a date — is a `SpecCriticalException` naming the member |
```

And replace the first row's parenthetical so it stops implying positional collections:

```markdown
| a whole object | compared **structurally**, member by member, never through `Equals`. A nested member is followed; a **collection member is matched as a set** and a **dictionary member by key** |
```

## 2. `references/vocabulary.md` — a short block after that table

```markdown
### A collection member is a set, and unordered by default

A collection member is matched the way a top-level `ThenEventsInAnyOrder` is: exactly these items in
any order, an item nobody expected EXTRA, an expectation nothing satisfied MISSING, and **the right
item with a wrong member reported as that member** rather than a MISSING beside an EXTRA. So:

- A reordered collection is **not** a disagreement. Say `Expect.Exactly(…)` (or, on a whole object,
  `Expect.Value(x).InOrder(e => e.Lines)`) when the order is the claim.
- MISSING and EXTRA show up mostly when the **counts** differ, because an expectation with no exact
  match is paired with an unmatched item of the same type first. If you expected MISSING and got
  `Lines[1].Sku: expected XYZ, was DEF`, nothing is broken — that is the near-match reading.
- A difference inside a collection names the item by the position it is **actually** at:
  `Lines[1].Quantity`. An expectation nothing satisfied names the member with no index, because it
  was not found anywhere.
- A dictionary member compares by **key** (`Headers[auth]`), so enumeration order never matters and a
  key one side lacks reads as that one key. A `List<KeyValuePair<,>>` is an ordered list, not a
  dictionary.
- Ten disagreements are rendered, then `…and N more`.

`x.Lines.ShouldBe(lines)` inside `Specify<T>(…)` or `.Check(…)` stays **ordered**, because that is
Shouldly's own meaning, with `ShouldBe(lines, ignoreOrder: true)` as Shouldly's own opt-out — but
compared member by member rather than through `Equals`.
```

## 3. `SKILL.md` — the "Values" bullet

```markdown
- **Values** are whole objects compared structurally, or partial objects:
  `Specify<T>().With(x => x.Balance, 100m)` judges only the members it names, and builds a full
  object when handed to a Given or a When. A **collection member is matched as a set, unordered**
  (`Expect.Exactly` claims the order), and a dictionary member by key.
```

## 4. `description:` frontmatter

Add to the covered list, so an agent searching for the symptom finds the skill: `partial objects` →
`partial objects and collection expectations (Expect.Exactly / InAnyOrder / Containing / NoneOf /
Rows, tolerances)`.

## 5. Diagnostics table

Nothing to add. #456 introduces **no new diagnostic**: a collection expectation that cannot be built
and slack on a member that cannot have any are both `SpecCriticalException` at run time, named in the
text above, and BOBCAT032 already covers a stale member path. `Expect.Rows(table)` under a `.With(…)`
is a **new call shape BOBCAT032 does not check yet** — a follow-up on the Bobcat side, not a skill
entry.
