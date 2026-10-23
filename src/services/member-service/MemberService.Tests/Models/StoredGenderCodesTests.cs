using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using MemberService.Controllers;
using MemberService.Models;

namespace MemberService.Tests.Models;

/// <summary>
/// The defect these cover: nothing constrained <c>Gender</c> on the way in, so a
/// value CHO could not interpret was accepted, stored, and then projected as US
/// Core <c>gender: "unknown"</c> — a statement about the member that CHO was
/// never told, and indistinguishable from one who declined to say.
/// </summary>
public class StoredGenderCodesTests
{
    private static IReadOnlyList<ValidationResult> Validate(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    private static CreateMemberRequest CreateRequest(string? gender) => new()
    {
        MemberId = "M-001",
        GroupNumber = "G-001",
        IsSubscriber = true,
        FirstName = "Alice",
        LastName = "Example",
        DateOfBirth = new System.DateTime(1985, 6, 15),
        EffectiveDate = new System.DateTime(2024, 1, 1),
        Gender = gender,
    };

    private static AddDependentMember Dependent(string? gender) => new()
    {
        MemberId = "M-002",
        FirstName = "Bob",
        LastName = "Example",
        DateOfBirth = new System.DateTime(2010, 3, 2),
        EffectiveDate = new System.DateTime(2024, 1, 1),
        Gender = gender,
    };

    [Theory]
    [InlineData("M")]
    [InlineData("F")]
    [InlineData("U")]
    [InlineData("m")]
    [InlineData("f")]
    [InlineData("u")]
    public void The_834_DMG03_codes_are_accepted(string gender)
    {
        Validate(CreateRequest(gender)).Should().BeEmpty();
        Validate(Dependent(gender)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_absent_gender_is_accepted_because_not_stated_is_a_real_answer(string? gender)
    {
        Validate(CreateRequest(gender)).Should().BeEmpty();
        Validate(Dependent(gender)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("X")]
    [InlineData("Male")]
    [InlineData("female")]
    [InlineData("MF")]
    [InlineData("1")]
    [InlineData(" ")]
    public void A_code_CHO_cannot_interpret_is_refused_rather_than_stored(string gender)
    {
        Validate(CreateRequest(gender)).Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(CreateMemberRequest.Gender));

        Validate(Dependent(gender)).Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(AddDependentMember.Gender));
    }

    [Fact]
    public void The_refusal_says_what_it_would_otherwise_have_asserted()
    {
        Validate(CreateRequest("X")).Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(StoredGenderCodes.ValidationMessage);
    }

    [Theory]
    [InlineData("M", true)]
    [InlineData("f", true)]
    [InlineData("U", true)]
    [InlineData("X", false)]
    [InlineData("Male", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_recognises_only_a_single_DMG03_code(string? code, bool expected)
    {
        StoredGenderCodes.IsValid(code).Should().Be(expected);
    }
}
