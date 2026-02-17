using Microsoft.AspNetCore.Mvc;
using MemberService.Controllers;
using MemberService.Models;
using MemberService.Repositories;

namespace MemberService.Tests.Controllers;

public class MembersControllerTests
{
    private readonly Mock<IMemberRepository> _mockRepository;
    private readonly MembersController _controller;

    public MembersControllerTests()
    {
        _mockRepository = new Mock<IMemberRepository>();
        _controller = new MembersController(_mockRepository.Object);
        
        // Simulate tenant context from middleware
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = "tenant-123";
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task GetMember_WithValidId_ReturnsMember()
    {
        // Arrange
        var memberId = "MBR-12345";
        var tenantId = "tenant-123";
        var member = new Member
        {
            Id = memberId,
            TenantId = tenantId,
            FirstName = "John",
            LastName = "Doe",
            DateOfBirth = new DateTime(1980, 5, 15),
            Gender = "M"
        };

        _mockRepository.Setup(x => x.GetMemberAsync(memberId, tenantId))
            .ReturnsAsync(member);

        // Act
        var result = await _controller.GetMember(memberId);

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedMember = okResult.Value.Should().BeOfType<Member>().Subject;
        returnedMember.Id.Should().Be(memberId);
        returnedMember.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task GetMember_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        var memberId = "INVALID-ID";
        var tenantId = "tenant-123";

        _mockRepository.Setup(x => x.GetMemberAsync(memberId, tenantId))
            .ReturnsAsync((Member?)null);

        // Act
        var result = await _controller.GetMember(memberId);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetMembers_WithTenantContext_ReturnsOnlyTenantMembers()
    {
        // Arrange
        var tenantId = "tenant-123";
        var members = new List<Member>
        {
            new Member { Id = "MBR-1", TenantId = tenantId, FirstName = "John", LastName = "Doe" },
            new Member { Id = "MBR-2", TenantId = tenantId, FirstName = "Jane", LastName = "Smith" }
        };

        _mockRepository.Setup(x => x.GetMembersAsync(tenantId, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(members);

        // Act
        var result = await _controller.GetMembers();

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedMembers = okResult.Value.Should().BeAssignableTo<IEnumerable<Member>>().Subject;
        returnedMembers.Should().HaveCount(2);
        returnedMembers.Should().OnlyContain(m => m.TenantId == tenantId);
    }

    [Fact]
    public async Task CreateMember_WithValidMember_SavesWithTenantId()
    {
        // Arrange
        var tenantId = "tenant-123";
        var newMember = new Member
        {
            FirstName = "New",
            LastName = "Member",
            DateOfBirth = new DateTime(1990, 1, 1),
            Gender = "F"
        };

        Member? capturedMember = null;
        _mockRepository.Setup(x => x.CreateMemberAsync(It.IsAny<Member>(), tenantId))
            .Callback<Member, string>((m, t) => capturedMember = m)
            .ReturnsAsync((Member m, string t) => m);

        // Act
        var result = await _controller.CreateMember(newMember);

        // Assert
        var createdResult = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        capturedMember.Should().NotBeNull();
        capturedMember!.TenantId.Should().Be(tenantId);
        capturedMember.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateMember_WithoutTenantContext_ReturnsBadRequest()
    {
        // Arrange
        _controller.HttpContext.Items.Remove("TenantId");
        var newMember = new Member
        {
            FirstName = "New",
            LastName = "Member",
            DateOfBirth = new DateTime(1990, 1, 1),
            Gender = "F"
        };

        // Act
        var result = await _controller.CreateMember(newMember);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateMember_WithValidMember_UpdatesWithTenantIsolation()
    {
        // Arrange
        var memberId = "MBR-12345";
        var tenantId = "tenant-123";
        var existingMember = new Member
        {
            Id = memberId,
            TenantId = tenantId,
            FirstName = "Old",
            LastName = "Name"
        };

        var updatedMember = new Member
        {
            Id = memberId,
            TenantId = tenantId,
            FirstName = "New",
            LastName = "Name"
        };

        _mockRepository.Setup(x => x.GetMemberAsync(memberId, tenantId))
            .ReturnsAsync(existingMember);
        _mockRepository.Setup(x => x.UpdateMemberAsync(memberId, It.IsAny<Member>(), tenantId))
            .ReturnsAsync(updatedMember);

        // Act
        var result = await _controller.UpdateMember(memberId, updatedMember);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _mockRepository.Verify(x => x.UpdateMemberAsync(memberId, It.Is<Member>(m => 
            m.TenantId == tenantId && m.FirstName == "New"), tenantId), Times.Once);
    }

    [Fact]
    public async Task DeleteMember_WithValidId_DeletesWithTenantIsolation()
    {
        // Arrange
        var memberId = "MBR-12345";
        var tenantId = "tenant-123";

        _mockRepository.Setup(x => x.DeleteMemberAsync(memberId, tenantId))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.DeleteMember(memberId);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _mockRepository.Verify(x => x.DeleteMemberAsync(memberId, tenantId), Times.Once);
    }

    [Fact]
    public async Task DeleteMember_WhenMemberNotFound_ReturnsNotFound()
    {
        // Arrange
        var memberId = "INVALID-ID";
        var tenantId = "tenant-123";

        _mockRepository.Setup(x => x.DeleteMemberAsync(memberId, tenantId))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.DeleteMember(memberId);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SearchMembers_WithCriteria_ReturnsFilteredResults()
    {
        // Arrange
        var tenantId = "tenant-123";
        var lastName = "Smith";
        var members = new List<Member>
        {
            new Member { Id = "MBR-1", TenantId = tenantId, FirstName = "Jane", LastName = "Smith" }
        };

        _mockRepository.Setup(x => x.SearchMembersAsync(tenantId, lastName, null, null))
            .ReturnsAsync(members);

        // Act
        var result = await _controller.SearchMembers(lastName: lastName);

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedMembers = okResult.Value.Should().BeAssignableTo<IEnumerable<Member>>().Subject;
        returnedMembers.Should().HaveCount(1);
        returnedMembers.First().LastName.Should().Be("Smith");
        returnedMembers.Should().OnlyContain(m => m.TenantId == tenantId);
    }
}
