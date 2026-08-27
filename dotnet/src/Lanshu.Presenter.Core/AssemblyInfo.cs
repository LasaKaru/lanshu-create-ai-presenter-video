using System.Runtime.CompilerServices;

// Internal helpers — filter escaping, JSON path reads, request sanitizing, colour conversion —
// carry real correctness rules, so the test project verifies them directly.
[assembly: InternalsVisibleTo("Lanshu.Presenter.Tests")]
