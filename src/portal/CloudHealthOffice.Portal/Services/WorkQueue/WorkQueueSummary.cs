namespace CloudHealthOffice.Portal.Services;

public class WorkQueueSummary
{
    public int NcciEditFailures { get; set; }
    public int MissingAuth { get; set; }
    public int ProviderNotContracted { get; set; }
    public int CobRequired { get; set; }
    public int MedicalReview { get; set; }
}
