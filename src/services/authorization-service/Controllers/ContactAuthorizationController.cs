using AuthorizationService.Exceptions;
using AuthorizationService.Backends;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Contacts;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace AuthorizationService.Controllers;

[ApiController, Route("api/authorizations/from-contact")]
public sealed class ContactAuthorizationController(
    IContactIntakeReader contacts, IAuthorizationRepository repository,
    IAuthorizationBackendSelector backends, ICurrentActor actor) : ControllerBase
{
    [HttpPost("{contactId}")]
    [RequirePermission(ChoRolePermissions.ServiceContact)]
    public async Task<ActionResult<ContactCaseResult>> Create(string contactId, CancellationToken ct)
    {
        // Customer-specific external core intake is not implemented by this native path.
        var backend = backends.Resolve();
        if (backend.BackendKey != ChoAuthorizationBackend.Key || !backend.IsAuthoritative)
            return Conflict("Contact coverage intake requires the native authoritative authorization backend.");
        ContactIntakeReceipt? c;
        try { c = await contacts.GetAsync(contactId, ct); }
        catch (HttpRequestException) { return StatusCode(503, "Contact receipt is unavailable; no new authorization was created."); }
        if (c is null || c.Id != contactId || c.TenantId != actor.TenantId) return NotFound();
        if (c.Classification != "CoverageRequest" || c.Caller.Type != "Member"
            || string.IsNullOrWhiteSpace(c.MemberId) || c.CaseIntent?.Coverage is not { } clinical)
            return BadRequest("A saved member-self coverage intake is required.");
        var validation = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(clinical,
                new System.ComponentModel.DataAnnotations.ValidationContext(clinical), validation, true)
            || clinical.DateOfBirth == default || clinical.ServiceDate == default
            || clinical.DateOfBirth > DateTime.UtcNow || c.StartedAt == default || c.StartedAt > DateTime.UtcNow.AddMinutes(5))
            return BadRequest("Complete patient, provider, service and date information is required.");
        if (!Enum.TryParse<LineOfBusiness>(c.CaseIntent.LineOfBusiness, out var lob) || !Enum.IsDefined(lob))
            return BadRequest("Unknown line of business.");
        var id = ContactIntakeIdentity.For(actor.TenantId, c.Id);
        if (await repository.GetByIdAsync(id) is { } existing) return Ok(Receipt(existing));
        var authorization = new Authorization {
            Id = id, SourceContactId = c.Id, SourceContactIntakeHash = ContactIntakeIdentity.Hash(c.CaseIntent), TenantId = actor.TenantId, AuthorizationNumber = "CT-" + id[8..],
            AuthorizationType = AuthorizationType.PreAuthorization,
            MemberId = c.MemberId, PatientFirstName = clinical.FirstName, PatientLastName = clinical.LastName,
            PatientDateOfBirth = clinical.DateOfBirth, LineOfBusiness = lob,
            RequestingProviderNPI = clinical.ProviderNpi, ServiceTypeCode = clinical.ServiceTypeCode,
            RequestedServiceDateFrom = clinical.ServiceDate, LevelOfService = clinical.Expedited ? "U" : "E",
            SubmittedDate = c.StartedAt, CreatedDate = DateTime.UtcNow, LastUpdatedDate = DateTime.UtcNow,
            CreatedBy = actor.UserId, LastUpdatedBy = actor.UserId, Status = AuthorizationStatus.Submitted,
            RequestedServices = [new RequestedService { ProcedureCode = clinical.ProcedureCode, RequestedUnits = clinical.Units }]
        };
        try { return Ok(Receipt(await backend.CreateAsync(authorization, ct))); }
        catch (DuplicateAuthorizationNumberException)
        {
            var saved = await repository.GetByIdAsync(id);
            return saved is null ? Conflict("Authorization identity conflict.") : Ok(Receipt(saved));
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var saved = await repository.GetByIdAsync(id);
            return saved is null ? Conflict("Authorization identity conflict.") : Ok(Receipt(saved));
        }
    }

    private static ContactCaseResult Receipt(Authorization a) => new() {
        IntakeHash = a.SourceContactIntakeHash, Id = a.Id, Kind = "Authorization", Status = a.Status.ToString(), ReceivedAt = a.SubmittedDate,
        DueAt = a.DecisionDueAt, RulePackVersion = a.DecisionDeadlinePolicy
    };
}
