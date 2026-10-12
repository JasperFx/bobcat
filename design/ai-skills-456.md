# ai-skills entry for bobcat#456

**Shipped: [JasperFx/ai-skills#259](https://github.com/JasperFx/ai-skills/pull/259)**, commit
`3bed35a` on branch `bobcat-456-collection-expectations`. This file records what was written and the
dependency that had to be cleared first, because the next person doing the same thing will hit it.

## What was in the way, and what cleared it

The entry belongs in `critterstack-sdd-bobcat-authoring/references/vocabulary.md`, in its "Values a
step takes" table. ai-skills' `origin/main` has **no such file**: it still carries the Gherkin-era
authoring skill (660 lines, a `Given events for {aggregate}` table, a `references/gherkin-grammar.md`),
with no `Specify<T>` and no `Expect.Value` anywhere — so there was no table to add a collection row to.

The projected rewrite existed, unpushed, on a local branch: `sdd-skills-code-first`, in the worktree
the installed skills are installed *from* (`~/.agents/.skill-lock.json` names it, which is how it was
found). Eleven commits, four SDD skills rewritten, `gherkin-grammar.md` deleted. Being unpushed, it
had nowhere to be reviewed, which is also why Gherkin was still in the published catalogue.

So the PR is that branch **plus one commit** — the collection vocabulary — and it carries the Gherkin
removal with it. Splitting the two was offered in the PR body rather than assumed.

## What the entry teaches beyond the API

The two things an agent gets wrong on its own, neither guessable from the signatures:

- **`Expect.Rows` is named apart from `Exactly`/`InAnyOrder`/`Containing`/`NoneOf`.** A `StepTable`
  converts implicitly from a string, so an `Expect.Containing(StepTable)` overload beats
  `params object[]` for a single string argument: `Expect.Containing("vip")` over a collection of
  strings would have meant "a table that happens to have no rows" — an expectation that asserts
  nothing and passes. A Bobcat test caught it; a skill reader would not.
- **MISSING and EXTRA mostly appear when the counts differ.** An expectation with no exact match
  pairs with an unmatched item of the same type first, so an agent expecting MISSING and reading
  `Lines[1].Sku: expected XYZ, was DEF` concludes something is broken.

Plus the rest of the surface: unordered by default, `Expect.Exactly` for the order, keyed
dictionaries, the wrong-runtime-type difference, that a relaxation does not reach inside a
collection's elements and the composition that does, and `ShouldBe` staying ordered while comparing
member by member.

## Three surviving mentions of Gherkin, deliberately

`SKILL.md`'s "Bobcat 0.50.0 removed everything else" note, its `description:` line, and
`critterstack-sdd-specs-from-model`'s "There is no Gherkin and no `.feature` file to produce." These
are **negative** statements: a skill whose subject was removed is more useful saying so than saying
nothing, because an agent with stale training data reaches for `.feature` otherwise.

## No diagnostic entry

#456 introduces no new diagnostic. A collection expectation that cannot be built, and slack on a
member that cannot have any, are both `SpecCriticalException` at run time; BOBCAT032 already covers a
stale member path and now covers `Expect.Rows(table)` too.
