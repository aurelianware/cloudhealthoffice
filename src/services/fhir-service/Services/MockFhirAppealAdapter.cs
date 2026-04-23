using CloudHealthOffice.Appeals.Contracts;

namespace FhirService.Services;

/// <summary>
/// In-memory adapter for local development (for example, when no
/// appeals-service is running) and for test runs that want predictable
/// appeal data without standing up a real downstream. Registered as
/// <see cref="IFhirAppealAdapter"/> when <c>Appeals:UseMockAdapter</c> is
/// true (default in Development environments), or when the configuration
/// explicitly selects the mock implementation.
/// </summary>
public sealed class MockFhirAppealAdapter : IFhirAppealAdapter
{
    private static readonly List<AppealDto> Seed = new()
    {
        new AppealDto
        {
            TenantId = "test-tenant",
            Id = "apl-001",
            AppealNumber = "APL-20260401-AB12CD34",
            ClaimId = "clm-001",
            ClaimNumber = "CLM-001",
            MemberId = "pat-001",
            PatientName = "John A Smith",
            ProviderNPI = "1234567890",
            ProviderName = "Dr. Jane Doe",
            DenialReasonCode = "CO-45",
            DenialReason = "Charge exceeds fee schedule.",
            DeniedAmount = 2500.00m,
            AppealedAmount = 2500.00m,
            AppealType = AppealType.Reconsideration,
            AppealLevel = AppealLevel.FirstLevel,
            LineOfBusiness = LineOfBusiness.Commercial,
            Status = AppealStatus.InReview,
            AppealReason = "Medical necessity supported by op notes.",
            Source = AppealSource.ProviderPortal,
            SubmittedDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            TargetResponseDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IsUrgent = false,
            CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            AssignedReviewerId = "rev-01",
            Notes =
            [
                new AppealNoteDto
                {
                    NoteId = "note-001",
                    CreatedBy = "prov-001",
                    NoteText = "Initial submission with operative report.",
                    IsInternal = false,
                    CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            ],
            Attachments =
            [
                new AppealAttachmentDto
                {
                    AttachmentId = "att-001",
                    ControlNumber = "275-20260401000000-A1B2C3",
                    AttachmentTypeCode = "OZ",
                    TransmissionCode = "EL",
                    FileName = "op-report.pdf",
                    BlobUrl = "mds://doc-1",
                    ContentType = "application/pdf",
                    UploadedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    Description = "Operative report."
                }
            ]
        },
        new AppealDto
        {
            TenantId = "other-tenant",
            Id = "apl-100",
            AppealNumber = "APL-20260401-OT100001",
            ClaimId = "clm-100",
            ClaimNumber = "CLM-100",
            MemberId = "pat-100",
            PatientName = "Jane B Doe",
            ProviderNPI = "9876543210",
            AppealType = AppealType.Reconsideration,
            AppealLevel = AppealLevel.FirstLevel,
            LineOfBusiness = LineOfBusiness.Commercial,
            Status = AppealStatus.InReview,
            AppealReason = "Other tenant appeal.",
            Source = AppealSource.ProviderPortal,
            CreatedAt = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc),
            Notes = [],
            Attachments = []
        }
    };

    public Task<AppealDto?> GetAppealAsync(string id, string tenantId, CancellationToken ct = default)
        => Task.FromResult(Seed.FirstOrDefault(a => a.Id == id && a.TenantId == tenantId));

    public Task<(IReadOnlyList<AppealDto> Items, int Total)> SearchAppealsAsync(
        AppealSearchQuery query, string tenantId, CancellationToken ct = default)
    {
        var filtered = Seed.Where(a => a.TenantId == tenantId).AsEnumerable();
        if (!string.IsNullOrEmpty(query.MemberId))
            filtered = filtered.Where(a => a.MemberId == query.MemberId);
        if (!string.IsNullOrEmpty(query.ClaimId))
            filtered = filtered.Where(a => a.ClaimId == query.ClaimId);
        if (!string.IsNullOrEmpty(query.AssignedReviewerId))
            filtered = filtered.Where(a => a.AssignedReviewerId == query.AssignedReviewerId);
        if (query.ClosureReasonCode.HasValue)
            filtered = filtered.Where(a => a.ClosureReasonCode == query.ClosureReasonCode.Value);
        if (!string.IsNullOrEmpty(query.Status) &&
            Enum.TryParse<AppealStatus>(query.Status, true, out var status))
            filtered = filtered.Where(a => a.Status == status);

        var all = filtered.ToList();
        var page = all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();
        return Task.FromResult<(IReadOnlyList<AppealDto>, int)>((page, all.Count));
    }

    public Task<IReadOnlyList<AppealSubmitChildOutcome>> SubmitAppealAsync(
        AppealSubmitBundleDto bundle, string tenantId, CancellationToken ct = default)
    {
        var assigned = string.IsNullOrEmpty(bundle.Appeal.Id)
            ? Guid.NewGuid().ToString()
            : bundle.Appeal.Id;

        var outcomes = new List<AppealSubmitChildOutcome>
        {
            new AppealSubmitChildOutcome
            {
                Kind = AppealSubmitChildKind.Appeal,
                ChildRef = bundle.Appeal.Id,
                EntryIndex = bundle.AppealEntryIndex,
                Success = true,
                AssignedId = assigned,
                HttpStatus = 201,
                FailureKind = AppealSubmitFailureKind.None
            }
        };

        for (var i = 0; i < bundle.Notes.Count; i++)
        {
            var n = bundle.Notes[i];
            outcomes.Add(new AppealSubmitChildOutcome
            {
                Kind = AppealSubmitChildKind.Note,
                ChildRef = n.NoteId,
                EntryIndex = i < bundle.NoteEntryIndices.Count ? bundle.NoteEntryIndices[i] : i + 1,
                Success = true,
                AssignedId = n.NoteId,
                HttpStatus = 200,
                FailureKind = AppealSubmitFailureKind.None
            });
        }

        for (var i = 0; i < bundle.Attachments.Count; i++)
        {
            var a = bundle.Attachments[i];
            outcomes.Add(new AppealSubmitChildOutcome
            {
                Kind = AppealSubmitChildKind.Attachment,
                ChildRef = a.AttachmentId,
                EntryIndex = i < bundle.AttachmentEntryIndices.Count ? bundle.AttachmentEntryIndices[i] : bundle.Notes.Count + i + 1,
                Success = true,
                AssignedId = a.AttachmentId,
                HttpStatus = 200,
                FailureKind = AppealSubmitFailureKind.None
            });
        }

        return Task.FromResult<IReadOnlyList<AppealSubmitChildOutcome>>(outcomes);
    }

    public Task<(AppealDto Appeal, AppealNoteDto Note)?> GetNoteByIdAsync(
        string noteId, string tenantId, CancellationToken ct = default)
    {
        foreach (var appeal in Seed.Where(a => a.TenantId == tenantId))
        {
            var note = appeal.Notes.FirstOrDefault(n => n.NoteId == noteId);
            if (note is not null)
                return Task.FromResult<(AppealDto, AppealNoteDto)?>((appeal, note));
        }
        return Task.FromResult<(AppealDto, AppealNoteDto)?>(null);
    }

    public Task<(AppealDto Appeal, AppealAttachmentDto Attachment)?> GetAttachmentByIdAsync(
        string attachmentId, string tenantId, CancellationToken ct = default)
    {
        foreach (var appeal in Seed.Where(a => a.TenantId == tenantId))
        {
            var attachment = appeal.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId);
            if (attachment is not null)
                return Task.FromResult<(AppealDto, AppealAttachmentDto)?>((appeal, attachment));
        }
        return Task.FromResult<(AppealDto, AppealAttachmentDto)?>(null);
    }
}
