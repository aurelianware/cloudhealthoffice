using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemberService.Models;
using MemberService.Repositories;
using MemberService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace FamilyRelationshipsBackfill;

/// <summary>
/// Resumable, idempotent backfill of the FamilyRelationship graph from legacy
/// Member.SubscriberMemberId values. Runs per-tenant; progress is checkpointed to
/// a backfill-jobs collection so a crashed run can resume where it left off.
///
/// Usage:
///   dotnet run --project tools/FamilyRelationshipsBackfill -- \
///     --tenant &lt;TENANT_ID&gt; [--batch 500] [--dry-run] [--reset]
///
/// Configuration (via appsettings.json / env vars):
///   MongoDb:ConnectionString   required
///   MongoDb:DatabaseName       default CloudHealthOffice
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        }));
        var logger = loggerFactory.CreateLogger("Backfill");

        var tenant = config["tenant"];
        if (string.IsNullOrWhiteSpace(tenant))
        {
            Console.Error.WriteLine("--tenant <TENANT_ID> is required.");
            return 2;
        }

        var batchSize = int.TryParse(config["batch"], out var b) ? b : 500;
        var dryRun = bool.TryParse(config["dry-run"], out var d) && d;
        var reset = bool.TryParse(config["reset"], out var r) && r;

        var mongoCs = config["MongoDb:ConnectionString"]
            ?? throw new InvalidOperationException("MongoDb:ConnectionString is required.");
        var dbName = config["MongoDb:DatabaseName"] ?? "CloudHealthOffice";

        var client = new MongoClient(mongoCs);
        var db = client.GetDatabase(dbName);

        var memberRepo = new MemberRepositoryMongo(db);
        var relRepo = new FamilyRelationshipRepositoryMongo(client, db);
        var service = new FamilyRelationshipService(relRepo, memberRepo);

        // Checkpoint collection — each run stores { tenantId, lastProcessedMemberId, completed }.
        var jobs = db.GetCollection<BackfillJob>("BackfillJobs");
        var jobFilter = Builders<BackfillJob>.Filter.Eq(x => x.TenantId, tenant);

        if (reset)
        {
            await jobs.DeleteManyAsync(jobFilter);
            logger.LogInformation("Reset checkpoint for tenant {Tenant}", tenant);
        }

        var job = await jobs.Find(jobFilter).FirstOrDefaultAsync()
            ?? new BackfillJob { TenantId = tenant, StartedAt = DateTime.UtcNow };

        if (job.Completed)
        {
            logger.LogInformation("Tenant {Tenant} backfill already marked completed; pass --reset to re-run.", tenant);
            return 0;
        }

        logger.LogInformation("Backfill starting — tenant={Tenant} batchSize={Batch} dryRun={DryRun} resumeFrom={Resume}",
            tenant, batchSize, dryRun, job.LastProcessedMemberId ?? "(start)");

        var members = db.GetCollection<Member>("Members");

        var cursorFilter = Builders<Member>.Filter.Eq(m => m.TenantId, tenant);
        if (!string.IsNullOrEmpty(job.LastProcessedMemberId))
        {
            cursorFilter &= Builders<Member>.Filter.Gt(m => m.MemberId, job.LastProcessedMemberId);
        }

        int created = 0, skippedAlreadyLinked = 0, skippedNoSubscriber = 0, errors = 0;

        while (true)
        {
            var page = await members.Find(cursorFilter)
                .Sort(Builders<Member>.Sort.Ascending(m => m.MemberId))
                .Limit(batchSize)
                .ToListAsync();
            if (page.Count == 0) break;

            foreach (var member in page)
            {
                if (member.IsSubscriber)
                {
                    job.LastProcessedMemberId = member.MemberId;
                    continue;
                }

#pragma warning disable CS0618 // reading obsolete field is the whole point of the backfill
                var legacySubscriber = member.SubscriberMemberId;
                var code = string.IsNullOrWhiteSpace(member.RelationshipCode) ? "19" : member.RelationshipCode!;
#pragma warning restore CS0618

                if (string.IsNullOrWhiteSpace(legacySubscriber))
                {
                    skippedNoSubscriber++;
                    job.LastProcessedMemberId = member.MemberId;
                    continue;
                }

                try
                {
                    if (!FamilyRelationshipCodes.IsValid(code)) code = "19";

                    var req = new CreateFamilyRelationshipRequest
                    {
                        SubjectMemberId = member.MemberId,
                        RelatedMemberId = legacySubscriber!,
                        RelationshipCode = code,
                        StartDate = member.EffectiveDate == default ? member.CreatedDate : member.EffectiveDate,
                        EndDate = member.TerminationDate,
                        IsCustodial = false,
                    };

                    if (dryRun)
                    {
                        logger.LogInformation("[dry-run] {Dep} → {Sub} ({Code})",
                            req.SubjectMemberId, req.RelatedMemberId, req.RelationshipCode);
                    }
                    else
                    {
                        await service.CreateAsync(tenant, req, "backfill", CancellationToken.None);
                        created++;
                    }
                }
                catch (DuplicateFamilyRelationshipException)
                {
                    // Pair is already in place (shim or prior backfill run).
                    skippedAlreadyLinked++;
                }
                catch (FamilyRelationshipValidationException ex)
                {
                    logger.LogWarning("Skipped {Member}: {Reason}", member.MemberId, ex.Message);
                    errors++;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected failure on member {Member}; continuing", member.MemberId);
                    errors++;
                }

                job.LastProcessedMemberId = member.MemberId;
            }

            // Checkpoint after every page so a crash preserves progress.
            job.UpdatedAt = DateTime.UtcNow;
            await jobs.ReplaceOneAsync(jobFilter, job, new ReplaceOptions { IsUpsert = true });

            logger.LogInformation("Page checkpoint: last={Last} created={Created} existing={Existing} noSub={NoSub} errors={Errors}",
                job.LastProcessedMemberId, created, skippedAlreadyLinked, skippedNoSubscriber, errors);

            cursorFilter = Builders<Member>.Filter.Eq(m => m.TenantId, tenant)
                & Builders<Member>.Filter.Gt(m => m.MemberId, job.LastProcessedMemberId!);
        }

        job.Completed = true;
        job.CompletedAt = DateTime.UtcNow;
        await jobs.ReplaceOneAsync(jobFilter, job, new ReplaceOptions { IsUpsert = true });

        logger.LogInformation(
            "Done. tenant={Tenant} created={Created} alreadyLinked={Existing} noSubscriber={NoSub} errors={Errors} dryRun={DryRun}",
            tenant, created, skippedAlreadyLinked, skippedNoSubscriber, errors, dryRun);

        return errors == 0 ? 0 : 1;
    }
}

public sealed class BackfillJob
{
    /// <summary>
    /// TenantId IS the document id. That guarantees exactly one checkpoint per tenant
    /// across concurrent or repeated runs — Mongo rejects a second document with the
    /// same _id instead of silently creating a parallel checkpoint.
    /// </summary>
    [BsonId]
    public string TenantId { get; set; } = string.Empty;

    public string? LastProcessedMemberId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool Completed { get; set; }
}
