# Conventions: naming history and generated code

Not renamed, deliberately: `override` members (the base declares the name), entry points (`Main`),
and anything a source generator emits. The only generated private members in the build today come
from .NET's `[GeneratedRegex]` (see `MarkerSpecNaming.cs`) — Bobcat's own generator emits none, so
there is nothing to fix in its templates.

The codebase was converted in one pass using a Roslyn `Renamer`, not by hand or by regex — a
textual rename of a name like `Record` would have hit unrelated identifiers. The same tool is the
right way to sweep up after a long-lived branch merges.

One gotcha when doing that: a member whose camel form is a C# keyword (`Class` → `class`) is
escaped to `@class` rather than rejected, so it compiles and the sweep looks clean. Grep the diff
for `@` identifiers afterwards and give those a real name instead — `testsInClass`, not `@class`.

**Generator note:** every type name the generator emits *as a type* (`typeof(...)` in the Event
Model source, interceptor signatures) must be `global::`-qualified: generated code lives in its own
namespace, where an unqualified name can bind to the wrong type.
