using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

public class StediClaimAcknowledgmentMapperTests
{
    public const string AcceptedJson = """
        {
          "meta": { "transactionId": "71716ec5-0e96-462f-bb77-869941bb27ab" },
          "transactions": [{
            "controlNumber": "1001",
            "payers": [{
              "organizationName": "STEDI INC",
              "entityIdentifierCode": "AY",
              "claimStatusTransactions": [{
                "claimTransactionBatchNumber": "synthetic-sub-001",
                "providerClaimStatuses": [{
                  "providerStatuses": [{
                    "healthCareClaimStatusCategoryCode": "A1",
                    "healthCareClaimStatusCategoryCodeValue": "Acknowledgement/Receipt",
                    "statusCode": "20",
                    "statusCodeValue": "Accepted for processing."
                  }]
                }],
                "claimStatusDetails": [{
                  "patientClaimStatusDetails": [{
                    "subscriber": { "firstName": "JOHN", "lastName": "ANON", "memberId": "U7777788888" },
                    "claims": [{
                      "claimStatus": {
                        "clearinghouseTraceNumber": "synthetic-sub-001",
                        "patientAccountNumber": "CLM-P-1001",
                        "referencedTransactionTraceNumber": "CLM-P-1001",
                        "tradingPartnerClaimNumber": "synthetic-pcn-001",
                        "informationClaimStatuses": [{
                          "statusInformationActionCode": "WQ",
                          "informationStatuses": [{
                            "healthCareClaimStatusCategoryCode": "A1",
                            "statusCode": "20",
                            "statusCodeValue": "Accepted for processing."
                          }]
                        }]
                      }
                    }]
                  }]
                }]
              }]
            }]
          }]
        }
        """;

    public const string RejectedJson = """
        {
          "meta": { "transactionId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" },
          "transactions": [{
            "payers": [{
              "claimStatusTransactions": [{
                "claimTransactionBatchNumber": "synthetic-sub-002",
                "claimStatusDetails": [{
                  "patientClaimStatusDetails": [{
                    "subscriber": { "firstName": "JOHN", "lastName": "ANON", "memberId": "SECRETMEMBER123" },
                    "claims": [{
                      "claimStatus": {
                        "referencedTransactionTraceNumber": "CLM-P-1002",
                        "informationClaimStatuses": [{
                          "statusInformationActionCode": "U",
                          "informationStatuses": [{
                            "healthCareClaimStatusCategoryCode": "A3",
                            "healthCareClaimStatusCategoryCodeValue": "Returned as unprocessable",
                            "statusCode": "164",
                            "statusCodeValue": "Entity's contract/member number.",
                            "entityIdentifierCode": "IL"
                          }]
                        }]
                      },
                      "serviceLines": [{
                        "lineItemControlNumber": "1",
                        "serviceClaimStatuses": [{
                          "serviceStatuses": [{
                            "healthCareClaimStatusCategoryCode": "A3",
                            "statusCode": "164",
                            "entityIdentifierCode": "IL",
                            "statusCodeValue": "Entity's contract/member number."
                          }]
                        }]
                      }]
                    }]
                  }]
                }]
              }]
            }]
          }]
        }
        """;

    [Fact]
    public void AcceptedReport_MapsCanonicalIdentifiersAndStatus()
    {
        var dto = JsonSerializer.Deserialize<Stedi277ReportDto>(AcceptedJson, StediHttpSender.JsonOptions);
        var ack = StediClaimAcknowledgmentMapper.ToCanonical(dto, DateTimeOffset.UtcNow, "evt-1");

        ack.Gateway.Should().Be("Stedi");
        ack.Status.Should().Be(ClaimAcknowledgmentStatus.Accepted);
        ack.OriginalSubmissionId.Should().Be("synthetic-sub-001");
        ack.PatientControlNumber.Should().Be("CLM-P-1001");
        ack.ClaimControlNumber.Should().Be("synthetic-pcn-001");
        ack.AcknowledgmentId.Should().Be("71716ec5-0e96-462f-bb77-869941bb27ab");
        ack.EventId.Should().Be("evt-1");
        ack.ClaimLevelResults.Should().ContainSingle();
    }

    [Fact]
    public void RejectedReport_MapsInvalidSubscriberAndLine()
    {
        var dto = JsonSerializer.Deserialize<Stedi277ReportDto>(RejectedJson, StediHttpSender.JsonOptions);
        var ack = StediClaimAcknowledgmentMapper.ToCanonical(dto, DateTimeOffset.UtcNow, null);

        ack.Status.Should().Be(ClaimAcknowledgmentStatus.Rejected);
        ack.Errors.Should().Contain(e => e.Category == ClaimAcknowledgmentErrorCategory.InvalidSubscriber);
        ack.ServiceLineResults.Should().ContainSingle();
        ack.ServiceLineResults[0].Status.Should().Be(ClaimAcknowledgmentLineStatus.LineRejected);
        ack.ServiceLineResults[0].LineItemControlNumber.Should().Be("1");
        ack.ServiceLineResults[0].LineNumber.Should().Be(1);
    }

    [Fact]
    public void EmptyReport_IsMalformed()
    {
        var ack = StediClaimAcknowledgmentMapper.ToCanonical(
            new Stedi277ReportDto(), DateTimeOffset.UtcNow, "e");
        ack.Status.Should().Be(ClaimAcknowledgmentStatus.Malformed);
    }

    [Fact]
    public void CanonicalModel_DoesNotCopySubscriberPhi()
    {
        var dto = JsonSerializer.Deserialize<Stedi277ReportDto>(RejectedJson, StediHttpSender.JsonOptions);
        var json = JsonSerializer.Serialize(
            StediClaimAcknowledgmentMapper.ToCanonical(dto, DateTimeOffset.UtcNow, null));

        json.Should().NotContain("JOHN");
        json.Should().NotContain("ANON");
        json.Should().NotContain("SECRETMEMBER123");
    }
}
