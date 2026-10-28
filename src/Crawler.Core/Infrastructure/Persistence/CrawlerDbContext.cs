using Crawler.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Core.Infrastructure.Persistence;

public sealed class CrawlerDbContext(DbContextOptions<CrawlerDbContext> options) : DbContext(options)
{
    public DbSet<CrawlJob> Jobs => Set<CrawlJob>();
    public DbSet<CrawlPage> Pages => Set<CrawlPage>();
    public DbSet<CrawlEdge> Edges => Set<CrawlEdge>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CrawlJob>(job =>
        {
            job.ToTable("crawl_jobs");
            job.HasKey(j => j.Id);
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
            job.Property(j => j.InputUrl).IsRequired();
            job.Property(j => j.NormalizedStartUrl).IsRequired();
            job.Property(j => j.StartingHost).IsRequired().HasMaxLength(253);
            job.Property(j => j.FailureReason).HasMaxLength(2000);
            job.Property(j => j.LeaseOwner).HasMaxLength(128);
            job.HasMany(j => j.Pages).WithOne().HasForeignKey(p => p.JobId).OnDelete(DeleteBehavior.Cascade);
            // History listing: newest first with a deterministic tie-breaker.
            job.HasIndex(j => new { j.CreatedAt, j.Id }).IsDescending(true, true).HasDatabaseName("ix_crawl_jobs_created_at_id");
            // Recovery sweep: Running jobs whose lease expired.
            job.HasIndex(j => new { j.Status, j.LeaseExpiresAt }).HasDatabaseName("ix_crawl_jobs_status_lease_expires_at");
        });

        modelBuilder.Entity<CrawlPage>(page =>
        {
            page.ToTable("crawl_pages");
            page.HasKey(p => p.Id);
            page.Property(p => p.FetchStatus).HasConversion<string>().HasMaxLength(16);
            page.Property(p => p.NormalizedUrl).IsRequired();
            page.Property(p => p.ErrorMessage).HasMaxLength(2000);
            page.HasOne<CrawlPage>().WithMany().HasForeignKey(p => p.ParentPageId).OnDelete(DeleteBehavior.Restrict);
            // Idempotency: a normalized URL is processed at most once per job.
            page.HasIndex(p => new { p.JobId, p.NormalizedUrl }).IsUnique().HasDatabaseName("ux_crawl_pages_job_id_normalized_url");
            // Tree construction and frontier resume read all pages of a job in discovery order.
            page.HasIndex(p => new { p.JobId, p.DiscoveryOrder }).HasDatabaseName("ix_crawl_pages_job_id_discovery_order");
        });

        modelBuilder.Entity<CrawlEdge>(edge =>
        {
            edge.ToTable("crawl_edges");
            edge.HasKey(e => e.Id);
            edge.Property(e => e.Id).UseIdentityAlwaysColumn();
            edge.Property(e => e.TargetNormalizedUrl).IsRequired();
            edge.HasOne<CrawlJob>().WithMany().HasForeignKey(e => e.JobId).OnDelete(DeleteBehavior.Cascade);
            edge.HasOne<CrawlPage>().WithMany().HasForeignKey(e => e.SourcePageId).OnDelete(DeleteBehavior.Cascade);
            edge.HasOne<CrawlPage>().WithMany().HasForeignKey(e => e.TargetPageId).OnDelete(DeleteBehavior.Restrict);
            // Idempotency: one edge per (source, target) per job; occurrences are counted, not duplicated.
            edge.HasIndex(e => new { e.JobId, e.SourcePageId, e.TargetNormalizedUrl }).IsUnique().HasDatabaseName("ux_crawl_edges_job_source_target");
            edge.HasIndex(e => new { e.JobId, e.TargetPageId }).HasDatabaseName("ix_crawl_edges_job_id_target_page_id");
        });

        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_messages");
            outbox.HasKey(o => o.Id);
            outbox.Property(o => o.MessageType).IsRequired().HasMaxLength(64);
            outbox.Property(o => o.Payload).IsRequired();
            outbox.Property(o => o.LastError).HasMaxLength(2000);
            // The publisher only ever scans unpublished rows.
            outbox.HasIndex(o => o.CreatedAt).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_messages_unpublished");
        });
    }
}
