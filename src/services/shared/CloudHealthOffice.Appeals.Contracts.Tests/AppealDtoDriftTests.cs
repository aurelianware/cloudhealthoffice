using System.Reflection;
using AppealsService.Models;
using CloudHealthOffice.Appeals.Contracts;

namespace CloudHealthOffice.Appeals.Contracts.Tests;

/// <summary>
/// Structural drift guard between the domain Appeal aggregate in
/// appeals-service and the cross-service <see cref="AppealDto"/>.
///
/// For every public property on the domain Appeal, the DTO must have a
/// property with the same name, an equivalent type, and equivalent
/// nullability. Equivalent type is defined as:
///   - same underlying runtime type, OR
///   - same enum name + same underlying numeric type + same member list
///     (this covers the parallel-enum pattern — see AppealDtoEnums.cs).
///   - for collections, same element-type equivalence.
///   - for nested complex types (AppealAttachment, AppealNote, etc.),
///     equivalence of the corresponding DTO type (AppealAttachmentDto,
///     AppealNoteDto).
///
/// A string field silently becoming string? must fail this test — hence
/// the explicit nullability assertion. A new domain field with no DTO
/// counterpart must fail this test — hence the "every domain property
/// has a DTO property" direction. A new DTO-only field does NOT fail
/// this test (fhir-service may add its own derived/metadata fields that
/// don't correspond to domain state) — a future PR can add a second
/// direction if that becomes a problem.
/// </summary>
public class AppealDtoDriftTests
{
    private static readonly NullabilityInfoContext NullabilityCtx = new();

    [Fact]
    public void Every_domain_Appeal_property_has_equivalent_DTO_property()
    {
        var mismatches = CompareShapes(typeof(Appeal), typeof(AppealDto)).ToList();
        mismatches.Should().BeEmpty(
            "AppealDto must mirror AppealsService.Models.Appeal — name, " +
            "type, and nullability must match. Mismatches:\n  " +
            string.Join("\n  ", mismatches));
    }

    [Fact]
    public void Every_domain_AppealAttachment_property_has_equivalent_DTO_property()
    {
        var mismatches = CompareShapes(typeof(AppealAttachment), typeof(AppealAttachmentDto)).ToList();
        mismatches.Should().BeEmpty("AppealAttachmentDto must mirror AppealAttachment. Mismatches:\n  "
            + string.Join("\n  ", mismatches));
    }

    [Fact]
    public void Every_domain_AppealNote_property_has_equivalent_DTO_property()
    {
        var mismatches = CompareShapes(typeof(AppealNote), typeof(AppealNoteDto)).ToList();
        mismatches.Should().BeEmpty("AppealNoteDto must mirror AppealNote. Mismatches:\n  "
            + string.Join("\n  ", mismatches));
    }

    [Fact]
    public void Every_domain_AppealDecision_property_has_equivalent_DTO_property()
    {
        var mismatches = CompareShapes(typeof(AppealDecision), typeof(AppealDecisionDto)).ToList();
        mismatches.Should().BeEmpty("AppealDecisionDto must mirror AppealDecision. Mismatches:\n  "
            + string.Join("\n  ", mismatches));
    }

    [Fact]
    public void Every_domain_ClinicalDocument_property_has_equivalent_DTO_property()
    {
        var mismatches = CompareShapes(typeof(ClinicalDocument), typeof(ClinicalDocumentDto)).ToList();
        mismatches.Should().BeEmpty("ClinicalDocumentDto must mirror ClinicalDocument. Mismatches:\n  "
            + string.Join("\n  ", mismatches));
    }

    [Theory]
    [InlineData(typeof(AppealsService.Models.AppealType), typeof(AppealType))]
    [InlineData(typeof(AppealsService.Models.AppealLevel), typeof(AppealLevel))]
    [InlineData(typeof(AppealsService.Models.AppealStatus), typeof(AppealStatus))]
    [InlineData(typeof(AppealsService.Models.AppealDecisionType), typeof(AppealDecisionType))]
    [InlineData(typeof(AppealsService.Models.AttachmentStatus), typeof(AttachmentStatus))]
    [InlineData(typeof(AppealsService.Models.LineOfBusiness), typeof(LineOfBusiness))]
    [InlineData(typeof(AppealsService.Models.AppealClosureReasonCode), typeof(AppealClosureReasonCode))]
    [InlineData(typeof(AppealsService.Models.AppealSource), typeof(AppealSource))]
    public void Parallel_enums_have_same_underlying_type_and_members(Type domainEnum, Type contractsEnum)
    {
        domainEnum.IsEnum.Should().BeTrue($"{domainEnum.FullName} must be an enum");
        contractsEnum.IsEnum.Should().BeTrue($"{contractsEnum.FullName} must be an enum");

        Enum.GetUnderlyingType(domainEnum).Should().Be(Enum.GetUnderlyingType(contractsEnum));

        var domainMembers = Enum.GetNames(domainEnum).OrderBy(n => n).ToArray();
        var contractsMembers = Enum.GetNames(contractsEnum).OrderBy(n => n).ToArray();
        contractsMembers.Should().BeEquivalentTo(domainMembers,
            $"members of {contractsEnum.FullName} must exactly mirror {domainEnum.FullName}");

        foreach (var name in domainMembers)
        {
            var domainValue = Convert.ToInt64(Enum.Parse(domainEnum, name));
            var contractsValue = Convert.ToInt64(Enum.Parse(contractsEnum, name));
            contractsValue.Should().Be(domainValue,
                $"{contractsEnum.Name}.{name} must have the same numeric value as {domainEnum.Name}.{name}");
        }
    }

    // ── Shape comparison ────────────────────────────────────────────────

    private static IEnumerable<string> CompareShapes(Type domain, Type dto)
    {
        var domainProps = domain
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !IsIgnored(p))
            .ToDictionary(p => p.Name, p => p);
        var dtoProps = dto
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !IsIgnored(p))
            .ToDictionary(p => p.Name, p => p);

        foreach (var (name, domainProp) in domainProps)
        {
            if (!dtoProps.TryGetValue(name, out var dtoProp))
            {
                yield return $"DTO {dto.Name} is missing property '{name}' present on {domain.Name}";
                continue;
            }

            var typeIssue = CompareTypes(domainProp.PropertyType, dtoProp.PropertyType);
            if (typeIssue is not null)
            {
                yield return $"Property '{name}': {typeIssue}";
            }

            var domainNullable = IsNullable(domainProp);
            var dtoNullable = IsNullable(dtoProp);
            if (domainNullable != dtoNullable)
            {
                yield return $"Property '{name}' nullability mismatch: domain={domainNullable}, dto={dtoNullable}";
            }
        }
    }

    private static bool IsIgnored(PropertyInfo prop)
    {
        // IsOverdue / ObservedStatus are computed read-only projections
        // on the domain; they don't travel on the wire. IsDeleted etc.
        // follow the same convention.
        if (!prop.CanWrite) return true;

        // [BsonIgnore] properties are persistence-only computed helpers.
        if (prop.GetCustomAttributes()
            .Any(a => a.GetType().FullName == "MongoDB.Bson.Serialization.Attributes.BsonIgnoreAttribute"))
            return true;

        return false;
    }

    /// <returns>null when equivalent; an explanation string when drifted.</returns>
    private static string? CompareTypes(Type domain, Type dto)
    {
        // Unwrap Nullable<T>.
        var domainUnderlying = Nullable.GetUnderlyingType(domain) ?? domain;
        var dtoUnderlying = Nullable.GetUnderlyingType(dto) ?? dto;

        if (domainUnderlying == dtoUnderlying) return null;

        // Enums in parallel namespaces — same name + same underlying numeric type.
        if (domainUnderlying.IsEnum && dtoUnderlying.IsEnum)
        {
            if (domainUnderlying.Name != dtoUnderlying.Name)
                return $"enum name differs: {domainUnderlying.Name} vs {dtoUnderlying.Name}";

            if (Enum.GetUnderlyingType(domainUnderlying) != Enum.GetUnderlyingType(dtoUnderlying))
                return $"enum underlying type differs for {domainUnderlying.Name}";

            var domainNames = Enum.GetNames(domainUnderlying);
            var dtoNames = Enum.GetNames(dtoUnderlying);
            if (!domainNames.OrderBy(n => n).SequenceEqual(dtoNames.OrderBy(n => n)))
                return $"enum members differ for {domainUnderlying.Name}";

            return null;
        }

        // Generic List<T> / IEnumerable<T> — recurse on the element type.
        if (IsGenericList(domainUnderlying, out var domainElement) &&
            IsGenericList(dtoUnderlying, out var dtoElement))
        {
            if (domainElement is null || dtoElement is null)
                return "unable to determine element types";
            var inner = CompareTypes(domainElement!, dtoElement!);
            return inner is null ? null : $"List element: {inner}";
        }

        // Nested complex types — match by stripping "Dto" suffix.
        if (!domainUnderlying.IsPrimitive &&
            !dtoUnderlying.IsPrimitive &&
            domainUnderlying != typeof(string))
        {
            var expectedDtoName = domainUnderlying.Name + "Dto";
            if (dtoUnderlying.Name == expectedDtoName)
            {
                // Recurse structurally on nested complex types.
                var nested = CompareShapes(domainUnderlying, dtoUnderlying).ToList();
                return nested.Count == 0 ? null : "nested: " + string.Join(", ", nested);
            }
        }

        return $"type mismatch: domain={FullName(domain)}, dto={FullName(dto)}";
    }

    private static bool IsGenericList(Type type, out Type? elementType)
    {
        elementType = null;
        if (!type.IsGenericType) return false;
        var def = type.GetGenericTypeDefinition();
        if (def == typeof(List<>) || def == typeof(IReadOnlyList<>) || def == typeof(IList<>) ||
            def == typeof(IEnumerable<>) || def == typeof(ICollection<>))
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }
        return false;
    }

    private static bool IsNullable(PropertyInfo prop)
    {
        // Value-type nullables.
        if (Nullable.GetUnderlyingType(prop.PropertyType) is not null) return true;
        if (prop.PropertyType.IsValueType) return false;

        // Reference-type nullable-annotation.
        var info = NullabilityCtx.Create(prop);
        return info.WriteState == NullabilityState.Nullable;
    }

    private static string FullName(Type t) => t.FullName ?? t.Name;
}
