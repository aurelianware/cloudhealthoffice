global using Xunit;
global using DaVinciInterop.Tests.Harness;

// Hl7.Fhir.Model defines a FHIR `Task` resource. Pin the bare `Task` used in
// async signatures to the TPL type, as the CMS acceptance suite does.
global using Task = System.Threading.Tasks.Task;
