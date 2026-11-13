# Living Documentation

A specification is documentation that runs. Living documentation is that documentation *with its
last result in it*: the Given / When / Then of `the underwriting rules`, every cell it checked, and
whether it passed the last time the suite ran. Storyteller's `dotnet stdocs` did this; Bobcat does it
from the [JSON results file](bobcat-tool.md#results-file), so anything that can write that file —
`bobcat run`, plain `dotnet test` with `BOBCAT_RESULTS`, a CI job — can feed a documentation site.

```bash
bobcat run --results artifacts/bobcat --docs docs/specs      # the run, then its pages
BOBCAT_RESULTS=artifacts/bobcat dotnet test                  # or plain dotnet test, then:
bobcat results-docs artifacts/bobcat --out docs/specs
```

Three forms come out, and a documentation page uses whichever fits:

| Form | What it is | Use it when |
|---|---|---|
| **Markdown** | one `.md` per specification, an index per feature and one for the run | the site is VitePress, Docusaurus, MkDocs or GitHub itself; text diffs in review; no JavaScript |
| **The results page, focused** | `results.html?spec=<identity>` shows one specification | the site can hold an iframe and you want the page's own rendering, as `bobcat ui` shows it |
| **The `<bobcat-spec>` element** | one script tag and one element, rendered from `results.json` | the site runs JavaScript and the specification should sit inline with the prose, in the console's own styling |

It is always **a run's result**. A page that shows what a suite *specifies* without a verdict is
[`bobcat preview`](bobcat-tool.md#run-preview-pick-watch); living documentation is what happened
when it ran.

## `bobcat results-docs` {#results-docs}

```
bobcat results-docs <results files or directories> [--out <dir>] [--no-script]
```

Reads every results file it is given — a file, or every `*.json` in a directory except the schema
beside them — and writes into `--out` (`docs` under the first directory, by default):

```
docs/specs/
  index.md                                   the run: suite, branch, counts, a table of features
  loan-underwriting/
    index.md                                 the feature: its specifications with verdicts
    the-underwriting-rules.md                one specification
    a-declined-application.md
  accounts/
    ...
  results.json                               every results file, folded, for <bobcat-spec>
  bobcat-spec.js                             the web component
```

- **File names are slugs of the titles**: lower case, letters and digits kept, every other run of
  characters one `-`, trimmed, at most 100 characters (`the underwriting rules` →
  `the-underwriting-rules`; `adds (a: 1, b: 2)` → `adds-a-1-b-2`). Two titles with one slug get
  `-2`, `-3` in title order. A specification with no feature is under `specifications/`. A data
  row's identity keeps its row part, so each row is its own page.
- **Deterministic and idempotent.** Nothing reads a clock: the same results write the same bytes,
  and a file whose content did not change is not touched, so a site build watching the tree sees
  nothing. The run's own start time is data and appears in the run index.
- **Several results files are one run.** A [supervised run](bobcat-tool.md#supervised) writes one
  file per worker request, so a retried specification is in several; its page shows the **latest
  attempt first** (by the file's finish time), marked `attempt 2 of 2`, and the earlier ones under
  *Earlier attempts*. The feature index says `2 attempts`.
- **Stale pages go, other files stay.** Every generated file begins with an HTML comment naming the
  command. A Markdown file under `--out` with that comment that this run did not write — a
  specification renamed or deleted — is removed, and a directory that leaves empty with it. A file
  without the comment is yours and is never touched, so `--out docs/specs` inside your documentation
  tree is safe.
- **Attachments stay files**, linked from each page relative to the page, into the results directory
  they were written to — keep the two together when you publish, or write the results under the
  docs tree (`--results docs/specs/results`). When the results directory was not kept (`bobcat run
  --docs` without `--results`), an attachment is named and linked to nothing, as the HTML page does.
- **`--no-script`** leaves out `bobcat-spec.js`. A tool built from source without
  `-p:EmbedFrontend=true` has no script to write and says so; the packed tool always has it.
- **It never fails a run.** `bobcat run --docs` reports a directory it cannot write on the terminal
  and the specifications decide the exit code. `--repeat` replays `--docs` as it replays `--html`.

## What a page looks like {#the-page}

```markdown
# a set of names missing one and expecting one that is not there

**Feature:** [Sets](index.md)

**✗ failed** · Rights: 3, Wrongs: 2, Errors: 0

- ✓ **Given** the names are

  | # | Name |
  |---|---|
  | 1 | Luke |
  | 2 | Han |
  | 3 | Chewie |

- ✗ **Then** the names in order should be

  | # | Name | Verdict |
  |---|---|---|
  | 1 | Luke | OK |
  | 2 | Han | OK |
  | 3 | Leia | MISSING |
  | 4 | Chewie | EXTRA |
```

The words are the console's: a judged cell reads `expected '6', got '8'`, or its value when it
agreed; a grid gets a **Verdict** column only when something in it was judged (an arrange table is
data, not verdicts); `MISSING`, `EXTRA`, `ORDER`, `ERROR` mark rows as the console does; a step the
run never reached is `— not run`; a marker comment nothing ran inside is `— declared`. An exception
is marked on its step and detailed in a fenced block below, with its type and message. Step logs
and the scenario's logs are fenced, reports are titled tables, attachments are links (an image is
shown inline). A failing [Acceptance specification](marker-steps.md#acceptance) reads
`◌ acceptance: failing — not yet passing, does not fail the build`.

The same rendering is available in process as `Bobcat.Rendering.MarkdownSpecText` over a
`SpecRender`, beside the console, JSON and plain-text renderers; the pages are rendered from the
results file read back through `ResultsFileReader`, so what the file says is what the page says.

## Putting it on a page {#recipes}

### VitePress

Include the Markdown where the prose wants it:

```md
<!--@include: ./specs/sets/a-set-of-names-missing-one-and-expecting-one-that-is-not-there.md-->
```

Relative links inside the included file (`index.md`, an attachment) resolve from the **including**
page, so include from the same directory level or link to the generated index instead. The page's
heading becomes part of yours; drop it with `<!--@include: ./specs/.../page.md{3,}-->` (VitePress's
line-range include) when your page already has a title.

For the web component, the script and the folded results have to be served as static files, which
in VitePress means `public/`:

```bash
bobcat results-docs artifacts/bobcat --out docs/specs
cp docs/specs/bobcat-spec.js docs/specs/results.json docs/public/
```

```js
// .vitepress/config.mjs
export default {
  head: [['script', { src: '/bobcat-spec.js' }]],
  vue: { template: { compilerOptions: { isCustomElement: (tag) => tag === 'bobcat-spec' } } },
}
```

```html
<bobcat-spec src="/results.json" spec="Sets/a set of names missing one and expecting one that is not there"></bobcat-spec>
```

The focused results page is an iframe over a page in `public/` too:

```html
<iframe src="/results.html?spec=Sets%2Fa%20set%20of%20names%20missing%20one%20and%20expecting%20one%20that%20is%20not%20there" width="100%" height="420"></iframe>
```

### Docusaurus

MDX imports a Markdown file as a component:

```mdx
import Spec from './specs/sets/a-set-of-names-missing-one-and-expecting-one-that-is-not-there.md';

<Spec />
```

For the element, put `bobcat-spec.js` and `results.json` under `static/`, add the script in
`docusaurus.config.js` (`scripts: ['/bobcat-spec.js']`) and write the element in MDX as above.

### GitHub

Commit `--out`'s directory and link to it: GitHub renders the Markdown, the run index links every
feature, each feature every specification. A pull request that changes a specification's behaviour
then shows the change to its page in the diff. For the links to work from the repository's file
view, the results directory with the attachments has to be committed beside it, or the attachments
appear as names.

### The element on any page

```html
<script src="bobcat-spec.js"></script>
<bobcat-spec src="results.json" spec="Loan Underwriting/the underwriting rules"></bobcat-spec>
```

`src` is the only thing it fetches: the `results.json` the command wrote, or a run's own results
file. `spec` is the identity, `{Feature}/{Scenario}`. It renders in a shadow root with the
console's own styles and dark palette, so the host page's CSS neither reaches in nor is reached. An
identity the results do not hold, or a file that cannot be fetched, is one line saying so.

### The focused results page

[`results.html`](bobcat-tool.md#results-page) with `?spec=<identity>` (URL-encoded;
`#spec=<identity>` where the query string is not kept) renders that one specification, open, with no
header and no filter bar, and a link back to the whole page. An unknown identity says *No
specification named … in this results page* with the same link.

## In CI {#ci}

Write the results, then the pages, then publish them with the site — or commit them:

```yaml
- run: bobcat run --results artifacts/bobcat --docs docs/specs
  continue-on-error: true                       # the pages are wanted whether or not it was green
- run: |
    git add docs/specs
    git diff --cached --quiet || git commit -m "Living docs from the latest run" && git push
```

Or from plain `dotnet test`:

```yaml
- run: dotnet test
  env:
    BOBCAT_RESULTS: artifacts/bobcat
- run: bobcat results-docs artifacts/bobcat --out docs/specs
  if: always()
```

Because the output is deterministic, a run that changed nothing produces no diff and no commit.

## What the pages cannot show {#limits}

They are rendered from the results file, so what the file leaves out, a page cannot have:

- **No stack traces.** The file carries an exception's type and message, not its frames; the fenced
  block shows those two.
- **A passing specification's reports, logs and attachments need `--verbose`** (or
  `BOBCAT_VERBOSE_REPORTS=1`): by default the file keeps them only for a failing scenario, the same
  rule as the console and the wire.
- **A step's input values are not set apart** — the file has no value spans, so a sentence renders
  as prose and its cells on their own lines.
- **A non-equality comparison reads in the equality shape** (`expected '10', got '3'` for a
  `ShouldBeGreaterThan`): the file does not carry the comparison word.
- **A skipped test is not there.** It asserted nothing, so the file has no scenario for it and no
  page is written. A failing Acceptance specification is there, under its own verdict.

**Versions.** The `bobcat` tool and the results file are read and written by the same release; a
results file from an earlier release still renders with what it carries (unknown properties are
ignored and missing ones take their defaults), and the schema's version moves only for a breaking
change.
