using Microsoft.EntityFrameworkCore;

namespace Aldanma.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<IdentifierRecord> Identifiers => Set<IdentifierRecord>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ReportIdentifier> ReportIdentifiers => Set<ReportIdentifier>();
    public DbSet<BlocklistEntry> BlocklistEntries => Set<BlocklistEntry>();
    public DbSet<DomainRegistrationCache> DomainRegistrations => Set<DomainRegistrationCache>();
    public DbSet<Dispute> Disputes => Set<Dispute>();
    public DbSet<FeedbackEntry> Feedback => Set<FeedbackEntry>();
    public DbSet<Submission> Submissions => Set<Submission>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<IdentifierRecord>(e =>
        {
            e.ToTable("identifiers");
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.PublicValue).HasMaxLength(300);
            e.Property(x => x.Masked).HasMaxLength(300);
            e.HasIndex(x => new { x.Kind, x.KeyHash }).IsUnique();
        });
        b.Entity<Report>(e =>
        {
            e.ToTable("reports");
            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.Channel).HasMaxLength(32);
            e.Property(x => x.Loss).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.ReporterHash);
        });
        b.Entity<ReportIdentifier>(e =>
        {
            e.ToTable("report_identifiers");
            e.HasKey(x => new { x.ReportId, x.IdentifierId });
            e.HasOne(x => x.Report).WithMany(x => x.Identifiers).HasForeignKey(x => x.ReportId);
            e.HasOne(x => x.Identifier).WithMany(x => x.Reports).HasForeignKey(x => x.IdentifierId);
            e.HasIndex(x => x.IdentifierId);
        });
        b.Entity<BlocklistEntry>(e =>
        {
            e.ToTable("blocklist_entries");
            e.Property(x => x.Source).HasMaxLength(32);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Value).HasMaxLength(300);
            e.HasIndex(x => new { x.Source, x.Kind, x.Value }).IsUnique();
            e.HasIndex(x => new { x.Kind, x.Value });
        });
        b.Entity<DomainRegistrationCache>(e =>
        {
            e.ToTable("domain_registrations");
            e.HasKey(x => x.Domain);
            e.Property(x => x.Domain).HasMaxLength(253);
            e.Property(x => x.Registrar).HasMaxLength(200);
        });
        b.Entity<Dispute>(e =>
        {
            e.ToTable("disputes");
            e.Property(x => x.Contact).HasMaxLength(200);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasIndex(x => x.Status);
        });
        b.Entity<FeedbackEntry>(e =>
        {
            e.ToTable("feedback");
            e.Property(x => x.Level).HasMaxLength(16);
            e.Property(x => x.RulesVersion).HasMaxLength(20);
            e.HasIndex(x => x.CreatedAt);
        });
        b.Entity<Submission>(e =>
        {
            e.ToTable("submissions");
            e.Property(x => x.Text).HasMaxLength(5000);
            e.Property(x => x.Sender).HasMaxLength(16);
            e.Property(x => x.Claim).HasMaxLength(16);
            e.Property(x => x.Level).HasMaxLength(16);
            e.Property(x => x.RulesVersion).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Label).HasMaxLength(16);
            e.HasIndex(x => x.Status);
        });
    }
}
