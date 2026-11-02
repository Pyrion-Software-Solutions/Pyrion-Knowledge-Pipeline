using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pgvector.EntityFrameworkCore;

namespace PyrionKnowledgePipeline.Infrastructure;

public sealed class KnowledgeDesignFactory : IDesignTimeDbContextFactory<KnowledgeDbContext>
{
    public KnowledgeDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<KnowledgeDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__KnowledgeIndex") ??
            "Host=localhost;Database=pyrion_knowledge;Username=pyrion", o => o.UseVector()).Options);
}
