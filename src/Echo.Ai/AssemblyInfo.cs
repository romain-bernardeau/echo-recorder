using System.Runtime.CompilerServices;

// Expose internal helpers (Plain, ValidateMinutes, ParseJsonObject, FmtTs,
// UnwrapJsonContent, CrTypeForBase, BuildHtml…) to the test project so the
// riskiest ported behaviours can be exercised without a live Mistral call.
[assembly: InternalsVisibleTo("Echo.Tests")]
