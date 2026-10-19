namespace CloudHealthOffice.Portal.Services;

public interface IFamilyRelationshipService
{
    Task<List<FamilyRelationshipRow>> ListForMemberAsync(string memberId);
    Task<FamilyRelationshipRow?> AddDependentAsync(string subscriberMemberId, AddDependentPayload payload);
    Task EndRelationshipAsync(string memberId, string relationshipId, DateTime? endDate = null);
    Task<FamilyRelationshipRow?> UpdateRelationshipAsync(string memberId, string relationshipId, UpdateRelationshipPayload payload);
    Task SoftDeleteAsync(string memberId, string relationshipId, string reason);
}
