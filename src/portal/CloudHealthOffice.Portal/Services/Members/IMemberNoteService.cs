namespace CloudHealthOffice.Portal.Services;

public interface IMemberNoteService
{
    Task<MemberNotePage> ListAsync(string memberId, MemberNoteFilter filter);
    Task<MemberNoteView?> CreateAsync(string memberId, CreateMemberNotePayload payload);
}
