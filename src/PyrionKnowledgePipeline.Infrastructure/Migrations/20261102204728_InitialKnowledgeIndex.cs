using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace PyrionKnowledgePipeline.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialKnowledgeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "EmbeddingSpaces",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ModelId = table.Column<string>(type: "text", nullable: false),
                    ModelVersion = table.Column<string>(type: "text", nullable: false),
                    Dimensions = table.Column<int>(type: "integer", nullable: false),
                    ConfigurationFingerprint = table.Column<string>(type: "text", nullable: false),
                    ProbeHash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmbeddingSpaces", x => x.Id);
                    table.UniqueConstraint("AK_EmbeddingSpaces_Id_Dimensions", x => new { x.Id, x.Dimensions });
                    table.CheckConstraint("CK_SpaceDimensions", "\"Dimensions\" BETWEEN 1 AND 16000");
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovedRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactHash = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: false),
                    SchemaVersion = table.Column<string>(type: "text", nullable: false),
                    ApprovedBy = table.Column<string>(type: "text", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovedRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovedRevisions_KnowledgeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "KnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IndexGenerations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactHash = table.Column<string>(type: "text", nullable: false),
                    SettingsFingerprint = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedChunks = table.Column<int>(type: "integer", nullable: false),
                    TotalChunks = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    SpaceId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexGenerations", x => x.Id);
                    table.UniqueConstraint("AK_IndexGenerations_Id_DocumentId", x => new { x.Id, x.DocumentId });
                    table.CheckConstraint("CK_RunStatus", "\"Status\" IN ('Pending','Running','Completed','Failed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_IndexGenerations_ApprovedRevisions_SourceDocumentId",
                        column: x => x.SourceDocumentId,
                        principalTable: "ApprovedRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndexGenerations_EmbeddingSpaces_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "EmbeddingSpaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndexGenerations_KnowledgeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "KnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActiveSelections",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveSelections", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_ActiveSelections_IndexGenerations_RunId_DocumentId",
                        columns: x => new { x.RunId, x.DocumentId },
                        principalTable: "IndexGenerations",
                        principalColumns: new[] { "Id", "DocumentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActiveSelections_KnowledgeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "KnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeChunks",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<string>(type: "text", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    DisplayText = table.Column<string>(type: "text", nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false),
                    SpaceId = table.Column<string>(type: "text", nullable: false),
                    Dimensions = table.Column<int>(type: "integer", nullable: false),
                    Vector = table.Column<Vector>(type: "vector", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeChunks", x => new { x.RunId, x.Id });
                    table.CheckConstraint("CK_VectorDimensions", "vector_dims(\"Vector\") = \"Dimensions\" AND vector_norm(\"Vector\") > 0");
                    table.ForeignKey(
                        name: "FK_KnowledgeChunks_EmbeddingSpaces_SpaceId_Dimensions",
                        columns: x => new { x.SpaceId, x.Dimensions },
                        principalTable: "EmbeddingSpaces",
                        principalColumns: new[] { "Id", "Dimensions" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunks_IndexGenerations_RunId",
                        column: x => x.RunId,
                        principalTable: "IndexGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActiveSelections_RunId",
                table: "ActiveSelections",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActiveSelections_RunId_DocumentId",
                table: "ActiveSelections",
                columns: new[] { "RunId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedRevisions_DocumentId_Revision",
                table: "ApprovedRevisions",
                columns: new[] { "DocumentId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_IndexGenerations_DocumentId",
                table: "IndexGenerations",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_IndexGenerations_SourceDocumentId_ArtifactHash_SettingsFing~",
                table: "IndexGenerations",
                columns: new[] { "SourceDocumentId", "ArtifactHash", "SettingsFingerprint", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_IndexGenerations_SpaceId",
                table: "IndexGenerations",
                column: "SpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_RunId_Ordinal",
                table: "KnowledgeChunks",
                columns: new[] { "RunId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_SpaceId_Dimensions",
                table: "KnowledgeChunks",
                columns: new[] { "SpaceId", "Dimensions" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_ProjectId_DocumentNumber",
                table: "KnowledgeDocuments",
                columns: new[] { "ProjectId", "DocumentNumber" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE "ApprovedRevisions" ADD CONSTRAINT "CK_ApprovedArtifact" CHECK (
                  (("Json"->'approval'->>'status') = 'approved' AND
                   ("Json"->>'documentId')::uuid = "Id" AND
                   "Json"->>'revision' = "Revision" AND "Json"->>'schemaVersion' = "SchemaVersion" AND
                   "Json"->'approval'->>'approvedBy' = "ApprovedBy") IS TRUE);
                CREATE FUNCTION pyrion_guard_generation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM "ApprovedRevisions" r WHERE r."Id" = NEW."SourceDocumentId"
                    AND r."DocumentId" = NEW."DocumentId" AND r."ArtifactHash" = NEW."ArtifactHash") THEN
                    RAISE EXCEPTION 'Generation source identity/hash mismatch';
                  END IF;
                  IF TG_OP = 'UPDATE' AND OLD."Status" = 'Completed' AND NEW IS DISTINCT FROM OLD THEN
                    RAISE EXCEPTION 'Completed generations are immutable';
                  END IF;
                  IF NEW."Status" = 'Completed' AND (NEW."SpaceId" IS NULL OR NEW."TotalChunks" < 1 OR
                    NEW."CompletedChunks" <> NEW."TotalChunks" OR NEW."FinishedAt" IS NULL OR
                    (SELECT count(*) FROM "KnowledgeChunks" c WHERE c."RunId" = NEW."Id"
                      AND c."SpaceId" = NEW."SpaceId") <> NEW."TotalChunks") THEN
                    RAISE EXCEPTION 'Incomplete generation cannot complete';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER pyrion_guard_generation BEFORE INSERT OR UPDATE ON "IndexGenerations"
                  FOR EACH ROW EXECUTE FUNCTION pyrion_guard_generation();
                CREATE FUNCTION pyrion_guard_active() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM "IndexGenerations" g WHERE g."Id" = NEW."RunId"
                    AND g."DocumentId" = NEW."DocumentId" AND g."Status" = 'Completed') THEN
                    RAISE EXCEPTION 'Only a completed matching generation may be active';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER pyrion_guard_active BEFORE INSERT OR UPDATE ON "ActiveSelections"
                  FOR EACH ROW EXECUTE FUNCTION pyrion_guard_active();
                CREATE FUNCTION pyrion_guard_chunk() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE run_status text;
                BEGIN
                  SELECT "Status" INTO run_status FROM "IndexGenerations" WHERE "Id" =
                    CASE WHEN TG_OP = 'DELETE' THEN OLD."RunId" ELSE NEW."RunId" END;
                  IF run_status = 'Completed' THEN RAISE EXCEPTION 'Completed chunks are immutable'; END IF;
                  IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER pyrion_guard_chunk BEFORE INSERT OR UPDATE OR DELETE ON "KnowledgeChunks"
                  FOR EACH ROW EXECUTE FUNCTION pyrion_guard_chunk();
                CREATE FUNCTION pyrion_guard_approval() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Imported approved snapshots are immutable; rebuild the derived database instead'; END $$;
                CREATE TRIGGER pyrion_guard_approval BEFORE UPDATE ON "ApprovedRevisions"
                  FOR EACH ROW EXECUTE FUNCTION pyrion_guard_approval();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER pyrion_guard_approval ON "ApprovedRevisions";
                DROP TRIGGER pyrion_guard_chunk ON "KnowledgeChunks";
                DROP TRIGGER pyrion_guard_active ON "ActiveSelections";
                DROP TRIGGER pyrion_guard_generation ON "IndexGenerations";
                DROP FUNCTION pyrion_guard_approval();
                DROP FUNCTION pyrion_guard_chunk();
                DROP FUNCTION pyrion_guard_active();
                DROP FUNCTION pyrion_guard_generation();
                """);
            migrationBuilder.DropTable(
                name: "ActiveSelections");

            migrationBuilder.DropTable(
                name: "KnowledgeChunks");

            migrationBuilder.DropTable(
                name: "IndexGenerations");

            migrationBuilder.DropTable(
                name: "ApprovedRevisions");

            migrationBuilder.DropTable(
                name: "EmbeddingSpaces");

            migrationBuilder.DropTable(
                name: "KnowledgeDocuments");
        }
    }
}
