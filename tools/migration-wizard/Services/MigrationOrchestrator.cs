using MigrationWizard.Models;

namespace MigrationWizard.Services;

/// <summary>
/// Main orchestrator service for the legacy system to Cloud Health Office migration
/// </summary>
public class MigrationOrchestrator : IDisposable
{
    private readonly TriZettoOpenAccessClient _trizettoClient;
    private readonly CosmosDbExportService _cosmosExportService;
    private readonly MappingReportGenerator _reportGenerator;
    private readonly ApiManagementCutoverService _cutoverService;
    private readonly ILogger<MigrationOrchestrator> _logger;
    
    private MigrationStatus _status = new();
    private MappingReport? _latestReport;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed;

    // In-memory storage for current migration batch
    private readonly List<BackendMember> _members = new();
    private readonly List<BackendProvider> _providers = new();
    private readonly List<BackendBenefitPlan> _benefitPlans = new();

    public event Action<MigrationStatus>? OnStatusChanged;

    public MigrationOrchestrator(
        TriZettoOpenAccessClient trizettoClient,
        CosmosDbExportService cosmosExportService,
        MappingReportGenerator reportGenerator,
        ApiManagementCutoverService cutoverService,
        ILogger<MigrationOrchestrator> logger)
    {
        _trizettoClient = trizettoClient ?? throw new ArgumentNullException(nameof(trizettoClient));
        _cosmosExportService = cosmosExportService ?? throw new ArgumentNullException(nameof(cosmosExportService));
        _reportGenerator = reportGenerator ?? throw new ArgumentNullException(nameof(reportGenerator));
        _cutoverService = cutoverService ?? throw new ArgumentNullException(nameof(cutoverService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Get current migration status
    /// </summary>
    public MigrationStatus GetStatus() => _status;

    /// <summary>
    /// Get latest mapping report
    /// </summary>
    public MappingReport? GetLatestReport() => _latestReport;

    /// <summary>
    /// Start the full migration process
    /// </summary>
    public async Task StartMigrationAsync()
    {
        if (_status.CurrentPhase != MigrationPhase.NotStarted && 
            _status.CurrentPhase != MigrationPhase.Failed)
        {
            throw new InvalidOperationException($"Migration already in progress. Current phase: {_status.CurrentPhase}");
        }

        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        _status = new MigrationStatus
        {
            CurrentPhase = MigrationPhase.Connecting,
            StartedAt = DateTime.UtcNow
        };
        NotifyStatusChanged();

        try
        {
            // Phase 1: Test connections
            _logger.LogInformation("Phase 1: Testing connections...");
            
            var trizettoConnected = await _trizettoClient.TestConnectionAsync();
            if (!trizettoConnected)
            {
                throw new Exception("Failed to connect to backend system Open Access APIs");
            }

            var cosmosConnected = await _cosmosExportService.TestConnectionAsync();
            if (!cosmosConnected)
            {
                throw new Exception("Failed to connect to Cosmos DB");
            }

            // Phase 2: Export Members
            _logger.LogInformation("Phase 2: Exporting members...");
            _status.CurrentPhase = MigrationPhase.ExportingMembers;
            NotifyStatusChanged();

            _members.Clear();
            await foreach (var member in _trizettoClient.ExportMembersAsync(cancellationToken: cancellationToken))
            {
                _members.Add(member);
                _status.TotalMembers = _members.Count;
                
                if (_members.Count % 100 == 0)
                {
                    NotifyStatusChanged();
                }
            }

            // Export members to Cosmos DB
            var (memberSucceeded, memberFailed) = await _cosmosExportService.ExportMembersBatchAsync(
                _members,
                new Progress<int>(count =>
                {
                    _status.MigratedMembers = count;
                    if (count % 100 == 0) NotifyStatusChanged();
                }),
                cancellationToken);

            _status.MigratedMembers = memberSucceeded;
            if (memberFailed > 0)
            {
                _status.Errors.Add(new MigrationError
                {
                    EntityType = "Member",
                    ErrorMessage = $"{memberFailed} members failed to export"
                });
            }

            // Phase 3: Export Providers
            _logger.LogInformation("Phase 3: Exporting providers...");
            _status.CurrentPhase = MigrationPhase.ExportingProviders;
            NotifyStatusChanged();

            _providers.Clear();
            await foreach (var provider in _trizettoClient.ExportProvidersAsync(cancellationToken))
            {
                _providers.Add(provider);
                _status.TotalProviders = _providers.Count;
                
                if (_providers.Count % 50 == 0)
                {
                    NotifyStatusChanged();
                }
            }

            var (providerSucceeded, providerFailed) = await _cosmosExportService.ExportProvidersBatchAsync(
                _providers,
                new Progress<int>(count =>
                {
                    _status.MigratedProviders = count;
                    if (count % 50 == 0) NotifyStatusChanged();
                }),
                cancellationToken);

            _status.MigratedProviders = providerSucceeded;
            if (providerFailed > 0)
            {
                _status.Errors.Add(new MigrationError
                {
                    EntityType = "Provider",
                    ErrorMessage = $"{providerFailed} providers failed to export"
                });
            }

            // Phase 4: Export Benefit Plans
            _logger.LogInformation("Phase 4: Exporting benefit plans...");
            _status.CurrentPhase = MigrationPhase.ExportingBenefitPlans;
            NotifyStatusChanged();

            _benefitPlans.Clear();
            await foreach (var plan in _trizettoClient.ExportBenefitPlansAsync(cancellationToken))
            {
                _benefitPlans.Add(plan);
                _status.TotalBenefitPlans = _benefitPlans.Count;
                NotifyStatusChanged();
            }

            var (planSucceeded, planFailed) = await _cosmosExportService.ExportBenefitPlansBatchAsync(
                _benefitPlans,
                new Progress<int>(count =>
                {
                    _status.MigratedBenefitPlans = count;
                    NotifyStatusChanged();
                }),
                cancellationToken);

            _status.MigratedBenefitPlans = planSucceeded;
            if (planFailed > 0)
            {
                _status.Errors.Add(new MigrationError
                {
                    EntityType = "BenefitPlan",
                    ErrorMessage = $"{planFailed} benefit plans failed to export"
                });
            }

            // Phase 5: Generate Mapping Report
            _logger.LogInformation("Phase 5: Generating mapping report...");
            _status.CurrentPhase = MigrationPhase.GeneratingMappingReport;
            NotifyStatusChanged();

            _latestReport = _reportGenerator.GenerateReport(_members, _providers, _benefitPlans);
            _status.AutoMatchedRecords = _latestReport.Summary.AutoMatched;
            _status.ManualReviewRequired = _latestReport.Summary.PartialMatch + _latestReport.Summary.NoMatch;

            // Phase 6: Ready for cutover
            _status.CurrentPhase = MigrationPhase.ReadyForCutover;
            NotifyStatusChanged();

            _logger.LogInformation("Migration completed. Ready for cutover. Auto-match rate: {Rate:F1}%", 
                _status.MatchPercentage);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Migration cancelled by user");
            _status.CurrentPhase = MigrationPhase.Failed;
            _status.LastError = "Migration cancelled by user";
            NotifyStatusChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Migration failed");
            _status.CurrentPhase = MigrationPhase.Failed;
            _status.LastError = ex.Message;
            _status.Errors.Add(new MigrationError
            {
                ErrorMessage = ex.Message,
                OccurredAt = DateTime.UtcNow
            });
            NotifyStatusChanged();
        }
    }

    /// <summary>
    /// Execute the cutover - flip routing keys in API Management
    /// </summary>
    public async Task<CutoverResult> ExecuteCutoverAsync()
    {
        if (_status.CurrentPhase != MigrationPhase.ReadyForCutover)
        {
            throw new InvalidOperationException($"Cannot execute cutover in current phase: {_status.CurrentPhase}");
        }

        _status.CurrentPhase = MigrationPhase.CutoverInProgress;
        NotifyStatusChanged();

        try
        {
            var result = await _cutoverService.ExecuteCutoverAsync();
            
            if (result.Success)
            {
                _status.CurrentPhase = MigrationPhase.Completed;
                _status.IsCutoverComplete = true;
                _status.CompletedAt = DateTime.UtcNow;
                
                _logger.LogInformation("Cutover successful. Migration completed at {Time}", _status.CompletedAt);
            }
            else
            {
                _status.CurrentPhase = MigrationPhase.ReadyForCutover;
                _status.LastError = result.ErrorMessage;
                _status.Errors.Add(new MigrationError
                {
                    ErrorMessage = result.ErrorMessage ?? "Cutover failed",
                    OccurredAt = DateTime.UtcNow
                });
                
                _logger.LogError("Cutover failed: {Error}", result.ErrorMessage);
            }
            
            NotifyStatusChanged();
            return result;
        }
        catch (Exception ex)
        {
            _status.CurrentPhase = MigrationPhase.ReadyForCutover;
            _status.LastError = ex.Message;
            NotifyStatusChanged();
            throw;
        }
    }

    /// <summary>
    /// Rollback the cutover
    /// </summary>
    public async Task<CutoverResult> RollbackCutoverAsync()
    {
        if (!_status.IsCutoverComplete)
        {
            throw new InvalidOperationException("Cannot rollback - cutover has not been completed");
        }

        try
        {
            var result = await _cutoverService.RollbackCutoverAsync();
            
            if (result.Success)
            {
                _status.IsCutoverComplete = false;
                _status.CurrentPhase = MigrationPhase.ReadyForCutover;
                _logger.LogWarning("Cutover rolled back. Traffic now routed to legacy backend");
            }
            
            NotifyStatusChanged();
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback failed");
            throw;
        }
    }

    /// <summary>
    /// Cancel ongoing migration
    /// </summary>
    public void CancelMigration()
    {
        _cancellationTokenSource?.Cancel();
    }

    /// <summary>
    /// Reset migration state
    /// </summary>
    public void Reset()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        
        _status = new MigrationStatus();
        _latestReport = null;
        _members.Clear();
        _providers.Clear();
        _benefitPlans.Clear();
        
        NotifyStatusChanged();
    }

    /// <summary>
    /// Get current routing status from API Management
    /// </summary>
    public async Task<RoutingStatus> GetRoutingStatusAsync()
    {
        return await _cutoverService.GetRoutingStatusAsync();
    }

    private void NotifyStatusChanged()
    {
        OnStatusChanged?.Invoke(_status);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _trizettoClient?.Dispose();
            _disposed = true;
        }
    }
}
