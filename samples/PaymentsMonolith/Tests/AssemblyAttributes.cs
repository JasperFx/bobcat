using Microsoft.AspNetCore.Mvc.Testing;

// Tells ASP.NET Core's WebApplicationFactory (which Alba uses) where the host's content root
// is. Without it, discovery walks up from the test assembly's bin directory and synthesizes
// `samples/PaymentsMonolith/PaymentsMonolith/` from the host assembly name — a folder that does
// not exist — and throws DirectoryNotFoundException. The Tests project lives INSIDE the host
// project rather than beside it, which is not the layout the default assumes.
//
// The path is relative to the test assembly's bin directory at run time: from
// `samples/PaymentsMonolith/Tests/bin/<config>/net10.0/`, four levels up is
// `samples/PaymentsMonolith/`, where the host's appsettings.json lives.
[assembly: WebApplicationFactoryContentRoot(
    "PaymentsMonolith",
    "../../../..",
    "appsettings.json",
    "1")]
