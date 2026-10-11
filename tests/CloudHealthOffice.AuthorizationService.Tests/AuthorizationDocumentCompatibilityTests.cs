using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Authorization = AuthorizationService.Models.Authorization;

namespace CloudHealthOffice.AuthorizationService.Tests;

/// <summary>
/// The stored authorization document across versions. A document written by a
/// newer build can carry fields this build does not know (PR #1300 added
/// <c>PatientReference</c>, <c>RequestHash</c> and <c>StatusHistory[].Source</c>).
/// The MongoDB driver throws on an unknown element by default, so after a
/// rollback every read of such a document would fail. Unknown elements are
/// ignored instead.
/// </summary>
public class AuthorizationDocumentCompatibilityTests
{
    [Fact]
    public void DocumentWithFieldsFromANewerBuild_StillReads()
    {
        var document = new BsonDocument
        {
            { "_id", "auth-1" },
            { "TenantId", "tenant-a" },
            { "AuthorizationNumber", "PAS-20261011-AAAA0001" },
            { "FieldFromANewerBuild", "x" },
            {
                "StatusHistory", new BsonArray
                {
                    new BsonDocument { { "Status", 1 }, { "FieldFromANewerBuild", "y" } },
                }
            },
            {
                "RequestedServices", new BsonArray
                {
                    new BsonDocument { { "ProcedureCode", "27447" }, { "FieldFromANewerBuild", "z" } },
                }
            },
            {
                "DiagnosisCodes", new BsonArray
                {
                    new BsonDocument { { "Code", "M17.11" }, { "FieldFromANewerBuild", "w" } },
                }
            },
        };

        var authorization = BsonSerializer.Deserialize<Authorization>(document);

        Assert.Equal("PAS-20261011-AAAA0001", authorization.AuthorizationNumber);
        Assert.Single(authorization.StatusHistory);
        Assert.Equal("27447", Assert.Single(authorization.RequestedServices).ProcedureCode);
        Assert.Equal("M17.11", Assert.Single(authorization.DiagnosisCodes).Code);
    }

    [Fact]
    public void ServiceTypeCode_DefaultsToNull_NotAnEmptyCode()
    {
        // An automated record whose request named no service type stores none,
        // not "" (which reads as a code that is present and blank).
        Assert.Null(new Authorization().ServiceTypeCode);
    }
}
