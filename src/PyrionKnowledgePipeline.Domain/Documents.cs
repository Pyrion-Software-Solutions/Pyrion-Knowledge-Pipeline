namespace PyrionKnowledgePipeline.Domain;

public enum BlockType { Paragraph, Heading, List, Table }
public enum ReviewStatus { Extracted, AwaitingReview, Rejected, Approved }

public sealed record DocumentMetadata(string ProjectId, string DocumentNumber, string Title, string Revision, string Language);
public sealed record TextEvidence(string? Style, bool Bold, decimal? FontSizePoints, string? NumberingId, int? ListLevel);
public sealed record TableCell(string Text, string SourceReference);
public sealed record TableRow(List<TableCell> Cells);
public sealed record DocumentBlock(string SourceReference, BlockType Type, int? HeadingLevel, string Text,
    List<TableRow>? Rows = null, TextEvidence? Formatting = null);
public sealed record ExtractionResult(List<DocumentBlock> Blocks, List<string> Warnings);
public sealed record ProposedBlock(string SourceReference, BlockType Type, int? HeadingLevel);
public sealed record ReviewIssue(string SourceReference, BlockType SuggestedType, int? SuggestedHeadingLevel, string Reason);
public sealed record AnalysisProposal(List<ReviewIssue> Issues, List<ProposedBlock> Blocks);
public sealed record ReviewDraft(Guid Id, string ModelId, DateTimeOffset CreatedAt, AnalysisProposal Proposal);
public sealed record ApprovalMetadata(ReviewStatus Status, string? ApprovedBy, DateTimeOffset? ApprovedAt, Guid DraftId, string ModelId);
public sealed record NormalizedDocument(string SchemaVersion, Guid DocumentId, string ProjectId, string DocumentNumber,
    string Title, string Revision, string Language, List<DocumentBlock> Blocks, ApprovalMetadata Approval,
    List<string>? ExtractionWarnings = null);

public sealed class PreparedDocument
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public required DocumentMetadata Metadata { get; set; }
    public required string OriginalFileName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ReviewStatus Status { get; set; } = ReviewStatus.Extracted;
    public required ExtractionResult Extraction { get; set; }
    public string Rules { get; set; } = "";
    public ReviewDraft? Draft { get; set; }
    public string? ReviewNote { get; set; }
    public NormalizedDocument? Approved { get; set; }
}

public sealed record DocumentSummary(Guid Id, string Title, string DocumentNumber, string Revision, ReviewStatus Status, DateTimeOffset CreatedAt);
