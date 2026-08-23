namespace CloudHealthOffice.Infrastructure.Responders.Models;

/// <summary>
/// Transport outcome of an inbound eligibility inquiry. Distinct from
/// <see cref="EligibilityBusinessStatus"/>: a 200-class transport success may
/// still carry a payer business rejection (invalid subscriber, member not
/// found, etc.).
/// </summary>
public enum EligibilityTransportStatus
{
    Success,
    Failed
}

/// <summary>
/// Cloud Health Office payer-side business outcome. This is the domain error
/// model; X12 AAA codes and vendor rejection codes belong in transport
/// adapters, never here.
/// </summary>
public enum EligibilityBusinessStatus
{
    Success,
    InvalidRequest,
    InvalidSubscriber,
    SubscriberNotFound,
    SubscriberAmbiguous,
    DependentNotFound,
    InvalidDependent,
    InvalidProvider,
    InvalidPayer,
    AmbiguousPayer,
    UnsupportedServiceType,
    InvalidDate,
    UnableToRespond
}

/// <summary>Coverage status evaluated against the requested date of service.</summary>
public enum PayerEligibilityCoverageStatus
{
    Unknown,
    Active,
    Inactive,
    Future,
    Terminated
}

/// <summary>Network participation of the requesting provider, when resolved.</summary>
public enum PayerEligibilityNetworkStatus
{
    Unknown,
    InNetwork,
    OutOfNetwork,
    ProviderNotOnFile
}

/// <summary>Exact-match member / subscriber lookup result. Never fuzzy.</summary>
public enum MemberLookupStatus
{
    Matched,
    NotFound,
    Ambiguous,
    InvalidRequest
}
