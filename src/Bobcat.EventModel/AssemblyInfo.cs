using System.Runtime.CompilerServices;

// The curated format's parser is internal as of issue #406 — see CuratedModelReader. Its tests are
// the only remaining caller, and they are what makes the retirement reviewable rather than a
// deletion of coverage.
[assembly: InternalsVisibleTo("Bobcat.EventModel.Tests")]
