using Microsoft.AspNetCore.Mvc.Testing;

// Only for AlbaContentRootResolutionTests: "My.Web" is a synthetic entry point that exists in no
// assembly, so this attribute can never influence a real host. It lets the tests prove that a
// [WebApplicationFactoryContentRoot] carried by a test assembly is honoured with its marker file.
[assembly: WebApplicationFactoryContentRoot("My.Web", "../../../..", "appsettings.json", "1")]
