using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using PyrionKnowledgePipeline.Application;

namespace PyrionKnowledgePipeline.Infrastructure;

public sealed class KnowledgeDbFactory(IConfiguration configuration)
{
    public KnowledgeDbContext Create()
    {
        var connection = configuration.GetConnectionString("KnowledgeIndex");
        if (string.IsNullOrWhiteSpace(connection))
            throw new PreparationException("PostgreSQL is not configured. Set ConnectionStrings__KnowledgeIndex using user secrets or an environment variable, then restart the app.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        if (!LocalEndpoint.IsLoopbackHost(builder.Host ?? ""))
            throw new PreparationException("The knowledge database must use a single local loopback host.");
        builder.IncludeErrorDetail = false;
        builder.Timeout = 5;
        builder.CommandTimeout = 30;
        return new(new DbContextOptionsBuilder<KnowledgeDbContext>().UseNpgsql(builder.ConnectionString, o => o.UseVector()).Options);
    }
}

public sealed class KnowledgeDbContext(DbContextOptions<KnowledgeDbContext> options) : DbContext(options)
{
    public DbSet<KnowledgeDocumentRow> Documents => Set<KnowledgeDocumentRow>();
    public DbSet<ApprovedRevisionRow> Revisions => Set<ApprovedRevisionRow>();
    public DbSet<EmbeddingSpaceRow> Spaces => Set<EmbeddingSpaceRow>();
    public DbSet<IndexGenerationRow> Runs => Set<IndexGenerationRow>();
    public DbSet<KnowledgeChunkRow> Chunks => Set<KnowledgeChunkRow>();
    public DbSet<ActiveSelectionRow> Active => Set<ActiveSelectionRow>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("vector");
        model.Entity<KnowledgeDocumentRow>(e =>
        {
            e.ToTable("KnowledgeDocuments"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ProjectId, x.DocumentNumber }).IsUnique();
            e.Property(x => x.ProjectId).HasMaxLength(200); e.Property(x => x.DocumentNumber).HasMaxLength(200);
        });
        model.Entity<ApprovedRevisionRow>(e =>
        {
            e.ToTable("ApprovedRevisions"); e.HasKey(x => x.Id);
            e.HasOne<KnowledgeDocumentRow>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Json).HasColumnType("jsonb");
            e.HasIndex(x => new { x.DocumentId, x.Revision });
        });
        model.Entity<EmbeddingSpaceRow>(e =>
        {
            e.ToTable("EmbeddingSpaces", t => t.HasCheckConstraint("CK_SpaceDimensions", "\"Dimensions\" BETWEEN 1 AND 16000"));
            e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.Id, x.Dimensions });
        });
        model.Entity<IndexGenerationRow>(e =>
        {
            e.ToTable("IndexGenerations", t => t.HasCheckConstraint("CK_RunStatus", "\"Status\" IN ('Pending','Running','Completed','Failed','Cancelled')"));
            e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.Id, x.DocumentId });
            e.HasOne<ApprovedRevisionRow>().WithMany().HasForeignKey(x => x.SourceDocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<KnowledgeDocumentRow>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<EmbeddingSpaceRow>().WithMany().HasForeignKey(x => x.SpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SourceDocumentId, x.ArtifactHash, x.SettingsFingerprint, x.Status });
        });
        model.Entity<KnowledgeChunkRow>(e =>
        {
            e.ToTable("KnowledgeChunks", t => t.HasCheckConstraint("CK_VectorDimensions", "vector_dims(\"Vector\") = \"Dimensions\" AND vector_norm(\"Vector\") > 0"));
            e.HasKey(x => new { x.RunId, x.Id }); e.HasIndex(x => new { x.RunId, x.Ordinal }).IsUnique();
            e.HasOne<IndexGenerationRow>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<EmbeddingSpaceRow>().WithMany().HasForeignKey(x => new { x.SpaceId, x.Dimensions })
                .HasPrincipalKey(x => new { x.Id, x.Dimensions }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Json).HasColumnType("jsonb"); e.Property(x => x.Vector).HasColumnType("vector");
        });
        model.Entity<ActiveSelectionRow>(e =>
        {
            e.ToTable("ActiveSelections"); e.HasKey(x => x.DocumentId); e.HasIndex(x => x.RunId).IsUnique();
            e.HasOne<KnowledgeDocumentRow>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<IndexGenerationRow>().WithMany().HasForeignKey(x => new { x.RunId, x.DocumentId })
                .HasPrincipalKey(x => new { x.Id, x.DocumentId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class KnowledgeDocumentRow
{
    public Guid Id { get; set; }
    public string ProjectId { get; set; } = "";
    public string DocumentNumber { get; set; } = "";
}
public sealed class ApprovedRevisionRow
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string ArtifactHash { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Title { get; set; } = "";
    public string Language { get; set; } = "";
    public string SchemaVersion { get; set; } = "";
    public string ApprovedBy { get; set; } = "";
    public DateTimeOffset ApprovedAt { get; set; }
    public string Json { get; set; } = "";
}
public sealed class EmbeddingSpaceRow
{
    public string Id { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public int Dimensions { get; set; }
    public string ConfigurationFingerprint { get; set; } = "";
    public string ProbeHash { get; set; } = "";
}
public sealed class IndexGenerationRow
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid SourceDocumentId { get; set; }
    public string ArtifactHash { get; set; } = "";
    public string SettingsFingerprint { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int CompletedChunks { get; set; }
    public int TotalChunks { get; set; }
    public string? Error { get; set; }
    public string? SpaceId { get; set; }
}
public sealed class KnowledgeChunkRow
{
    public Guid RunId { get; set; }
    public string Id { get; set; } = "";
    public int Ordinal { get; set; }
    public string DisplayText { get; set; } = "";
    public string Json { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public int Dimensions { get; set; }
    public Vector Vector { get; set; } = null!;
}
public sealed class ActiveSelectionRow
{
    public Guid DocumentId { get; set; }
    public Guid RunId { get; set; }
}
