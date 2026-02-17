using EnrollmentImportService.Models;
using Microsoft.Azure.Cosmos;

namespace EnrollmentImportService.Services;

public interface IEnrollmentRepository
{
    Task<Member?> GetMemberByIdAsync(string memberId, string tenantId);
    Task<Member?> GetMemberBySubscriberIdAsync(string subscriberId, string tenantId);
    Task<Member> CreateMemberAsync(Member member);
    Task<Member> UpdateMemberAsync(Member member);
    Task<Coverage> CreateCoverageAsync(Coverage coverage);
    Task<Coverage> UpdateCoverageAsync(Coverage coverage);
    Task<SponsorEntity?> GetSponsorByIdAsync(string sponsorId, string tenantId);
    Task<SponsorEntity> CreateSponsorAsync(SponsorEntity sponsor);
    Task<SponsorEntity> UpdateSponsorAsync(SponsorEntity sponsor);
}

public class EnrollmentRepository : IEnrollmentRepository
{
    private readonly CosmosClient _cosmosClient;
    private readonly IConfiguration _config;
    private readonly ILogger<EnrollmentRepository> _logger;
    
    private Container MembersContainer => _cosmosClient.GetContainer(
        _config["CosmosDb:DatabaseName"] ?? "CloudHealthOffice",
        _config["CosmosDb:MembersContainerName"] ?? "Members");
    
    private Container CoverageContainer => _cosmosClient.GetContainer(
        _config["CosmosDb:DatabaseName"] ?? "CloudHealthOffice",
        _config["CosmosDb:CoverageContainerName"] ?? "Coverage");
    
    private Container SponsorsContainer => _cosmosClient.GetContainer(
        _config["CosmosDb:DatabaseName"] ?? "CloudHealthOffice",
        _config["CosmosDb:SponsorsContainerName"] ?? "Sponsors");
    
    public EnrollmentRepository(CosmosClient cosmosClient, IConfiguration config, ILogger<EnrollmentRepository> logger)
    {
        _cosmosClient = cosmosClient;
        _config = config;
        _logger = logger;
    }
    
    public async Task<Member?> GetMemberByIdAsync(string memberId, string tenantId)
    {
        try
        {
            var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.memberId = @memberId AND c.tenantId = @tenantId")
                .WithParameter("@memberId", memberId)
                .WithParameter("@tenantId", tenantId);
            
            var iterator = MembersContainer.GetItemQueryIterator<Member>(query);
            var results = await iterator.ReadNextAsync();
            
            return results.FirstOrDefault();
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
    
    public async Task<Member?> GetMemberBySubscriberIdAsync(string subscriberId, string tenantId)
    {
        try
        {
            var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.subscriberId = @subscriberId AND c.tenantId = @tenantId")
                .WithParameter("@subscriberId", subscriberId)
                .WithParameter("@tenantId", tenantId);
            
            var iterator = MembersContainer.GetItemQueryIterator<Member>(query);
            var results = await iterator.ReadNextAsync();
            
            return results.FirstOrDefault();
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
    
    public async Task<Member> CreateMemberAsync(Member member)
    {
        member.CreatedAt = DateTime.UtcNow;
        member.UpdatedAt = DateTime.UtcNow;
        
        var response = await MembersContainer.CreateItemAsync(member, new PartitionKey(member.Id));
        _logger.LogInformation("Created member {MemberId} for tenant {TenantId}", member.MemberId, member.TenantId);
        
        return response.Resource;
    }
    
    public async Task<Member> UpdateMemberAsync(Member member)
    {
        member.UpdatedAt = DateTime.UtcNow;
        
        var response = await MembersContainer.ReplaceItemAsync(member, member.Id, new PartitionKey(member.Id));
        _logger.LogInformation("Updated member {MemberId} for tenant {TenantId}", member.MemberId, member.TenantId);
        
        return response.Resource;
    }
    
    public async Task<Coverage> CreateCoverageAsync(Coverage coverage)
    {
        coverage.CreatedAt = DateTime.UtcNow;
        coverage.UpdatedAt = DateTime.UtcNow;
        
        var response = await CoverageContainer.CreateItemAsync(coverage, new PartitionKey(coverage.Id));
        _logger.LogInformation("Created coverage {CoverageId} for member {MemberId}", coverage.Id, coverage.MemberId);
        
        return response.Resource;
    }
    
    public async Task<Coverage> UpdateCoverageAsync(Coverage coverage)
    {
        coverage.UpdatedAt = DateTime.UtcNow;
        
        var response = await CoverageContainer.ReplaceItemAsync(coverage, coverage.Id, new PartitionKey(coverage.Id));
        _logger.LogInformation("Updated coverage {CoverageId} for member {MemberId}", coverage.Id, coverage.MemberId);
        
        return response.Resource;
    }
    
    public async Task<SponsorEntity?> GetSponsorByIdAsync(string sponsorId, string tenantId)
    {
        try
        {
            var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.sponsorId = @sponsorId AND c.tenantId = @tenantId")
                .WithParameter("@sponsorId", sponsorId)
                .WithParameter("@tenantId", tenantId);
            
            var iterator = SponsorsContainer.GetItemQueryIterator<SponsorEntity>(query);
            var results = await iterator.ReadNextAsync();
            
            return results.FirstOrDefault();
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
    
    public async Task<SponsorEntity> CreateSponsorAsync(SponsorEntity sponsor)
    {
        sponsor.CreatedAt = DateTime.UtcNow;
        sponsor.UpdatedAt = DateTime.UtcNow;
        
        var response = await SponsorsContainer.CreateItemAsync(sponsor, new PartitionKey(sponsor.Id));
        _logger.LogInformation("Created sponsor {SponsorId} for tenant {TenantId}", sponsor.SponsorId, sponsor.TenantId);
        
        return response.Resource;
    }
    
    public async Task<SponsorEntity> UpdateSponsorAsync(SponsorEntity sponsor)
    {
        sponsor.UpdatedAt = DateTime.UtcNow;
        
        var response = await SponsorsContainer.ReplaceItemAsync(sponsor, sponsor.Id, new PartitionKey(sponsor.Id));
        _logger.LogInformation("Updated sponsor {SponsorId} for tenant {TenantId}", sponsor.SponsorId, sponsor.TenantId);
        
        return response.Resource;
    }
}
