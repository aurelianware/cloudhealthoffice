global using Xunit;
global using Cms0057Acceptance.Tests.TestSupport;

// Several scenarios import Hl7.Fhir.Model (which defines a FHIR `Task`
// resource). Pin the bare `Task` used in async signatures to the TPL type.
global using Task = System.Threading.Tasks.Task;
