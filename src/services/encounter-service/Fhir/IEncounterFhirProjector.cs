using System.Text.Json.Nodes;
using EncounterService.Models;

namespace EncounterService.Fhir;

/// <summary>
/// Projects a CHO encounter submission record to a FHIR R4 Encounter.
/// encounter-service owns this projection; fhir-service only translates FHIR
/// search into this service's structured query and serves what comes back
/// (ADR 018).
/// </summary>
public interface IEncounterFhirProjector
{
    JsonObject Project(Encounter encounter);
}
