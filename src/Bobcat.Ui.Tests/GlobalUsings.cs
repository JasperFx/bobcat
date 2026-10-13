// These types resolved implicitly while this project lived in the bobcat repository: its
// namespace was Bobcat.Console.*, and C# resolves an unqualified name by walking OUTWARD through
// the enclosing namespaces — so `Fixture` found Bobcat.Fixture without anyone saying so.
//
// Under Bobcat.Ui.* that walk no longer passes through Bobcat, and every one of those names
// became CS0246 at once. Stating them here is the honest form of what was always true.
global using Bobcat;
global using Bobcat.Runtime;
