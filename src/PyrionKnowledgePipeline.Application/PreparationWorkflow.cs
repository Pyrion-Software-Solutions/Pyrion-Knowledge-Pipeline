using PyrionKnowledgePipeline.Domain;

namespace PyrionKnowledgePipeline.Application;

public sealed class PreparationWorkflow(IDocumentParser parser, IDocumentStore store, ISettingsStore settingsStore,
    ILmStudioClient lmStudio, IDocumentContractValidator validator)
{
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    public const int MaxRulesCharacters = 20_000;

    public async Task<PreparedDocument> UploadAsync(string fileName, Stream stream, DocumentMetadata metadata, string rules, CancellationToken ct)
    {
        ValidateMetadata(metadata);
        ValidateRules(rules);
        if (!string.Equals(Path.GetExtension(fileName), ".docx", StringComparison.OrdinalIgnoreCase))
            throw new PreparationException("Choose a DOCX file. XLSX and PDF are not supported in this milestone.");
        using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + read > MaxUploadBytes) throw new PreparationException("The document exceeds the 10 MB upload limit.");
            await buffer.WriteAsync(bytes.AsMemory(0, read), ct);
        }
        if (buffer.Length == 0) throw new PreparationException("The uploaded file is empty.");
        buffer.Position = 0;
        var extraction = await parser.ExtractAsync(buffer, ct);
        if (extraction.Blocks.Count == 0) throw new PreparationException("The DOCX contains no supported body paragraphs or tables.");
        var document = new PreparedDocument
        {
            Id = Guid.NewGuid(),
            Metadata = metadata,
            OriginalFileName = Path.GetFileName(fileName),
            Extraction = extraction,
            Rules = rules
        };
        buffer.Position = 0;
        await store.CreateAsync(document, buffer, ct);
        return document;
    }

    public async Task<PreparedDocument> AnalyzeAsync(Guid id, string rules, Action<string>? reportStatus, CancellationToken ct)
    {
        ValidateRules(rules);
        var document = await RequireAsync(id, ct);
        RequireNotApproved(document);
        var settings = await settingsStore.LoadAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.ModelId)) throw new PreparationException("Choose and save a model in LM Studio Settings first.");
        var proposal = await lmStudio.AnalyzeAsync(settings, document.Extraction, rules, reportStatus, ct);
        ProposalIntegrity.Validate(proposal, document.Extraction);
        document.Rules = rules;
        document.Draft = new ReviewDraft(Guid.NewGuid(), settings.ModelId, DateTimeOffset.UtcNow, proposal);
        document.Status = ReviewStatus.AwaitingReview;
        document.ReviewNote = null;
        await store.SaveAsync(document, document.Version, ct);
        return document;
    }

    public async Task<PreparedDocument> ReviseAsync(Guid id, Guid draftId, List<ProposedBlock> blocks, string note, CancellationToken ct)
    {
        var document = await RequireDraftAsync(id, draftId, ct);
        if (string.IsNullOrWhiteSpace(note) || note.Length > 1800) throw new PreparationException("Describe your revision in 1–1,800 characters.");
        var previous = document.Draft!.Proposal;
        if (blocks.Count != previous.Blocks.Count || blocks.Where((x, i) => x.SourceReference != previous.Blocks[i].SourceReference).Any())
            throw new PreparationException("Manual revisions must retain every source block in its original order.");
        var issues = previous.Issues.Where(issue => blocks.Any(x => x.SourceReference == issue.SourceReference &&
            x.Type == issue.SuggestedType && x.HeadingLevel == issue.SuggestedHeadingLevel)).ToList();
        foreach (var (block, index) in blocks.Select((value, i) => (value, i)))
        {
            var before = previous.Blocks[index];
            if (block.Type != before.Type || block.HeadingLevel != before.HeadingLevel)
                issues.Add(new ReviewIssue(block.SourceReference, block.Type, block.HeadingLevel, "Reviewer revision: " + note));
        }
        var proposal = new AnalysisProposal(issues, blocks);
        ProposalIntegrity.Validate(proposal, document.Extraction);
        document.Draft = new ReviewDraft(Guid.NewGuid(), document.Draft!.ModelId, DateTimeOffset.UtcNow, proposal);
        document.ReviewNote = note;
        await store.SaveAsync(document, document.Version, ct);
        return document;
    }

    public async Task<PreparedDocument> RejectAsync(Guid id, Guid draftId, string note, CancellationToken ct)
    {
        var document = await RequireDraftAsync(id, draftId, ct);
        if (note.Length > 2000) throw new PreparationException("Keep the review note under 2,000 characters.");
        document.Status = ReviewStatus.Rejected;
        document.ReviewNote = string.IsNullOrWhiteSpace(note) ? "Rejected by local reviewer." : note;
        await store.SaveAsync(document, document.Version, ct);
        return document;
    }

    public async Task<PreparedDocument> ApproveAsync(Guid id, Guid draftId, string reviewer, CancellationToken ct)
    {
        var document = await RequireDraftAsync(id, draftId, ct);
        if (string.IsNullOrWhiteSpace(reviewer) || reviewer.Length > 200) throw new PreparationException("Enter a reviewer name (up to 200 characters).");
        ProposalIntegrity.Validate(document.Draft!.Proposal, document.Extraction);
        document.Approved = Preview(document, new ApprovalMetadata(ReviewStatus.Approved, reviewer.Trim(), DateTimeOffset.UtcNow,
            draftId, document.Draft.ModelId));
        await validator.ValidateNormalizedAsync(document.Approved, ct);
        document.Status = ReviewStatus.Approved;
        await store.SaveAsync(document, document.Version, ct);
        return document;
    }

    public static NormalizedDocument Preview(PreparedDocument document, ApprovalMetadata? approval = null)
    {
        var draft = document.Draft ?? throw new PreparationException("Run analysis before previewing a draft.");
        ProposalIntegrity.Validate(draft.Proposal, document.Extraction);
        var blocks = document.Extraction.Blocks.Select((source, i) => source with
        {
            Type = draft.Proposal.Blocks[i].Type,
            HeadingLevel = draft.Proposal.Blocks[i].HeadingLevel
        }).ToList();
        var m = document.Metadata;
        return new NormalizedDocument("1.1", document.Id, m.ProjectId, m.DocumentNumber, m.Title, m.Revision, m.Language, blocks,
            approval ?? new ApprovalMetadata(document.Status, null, null, draft.Id, draft.ModelId), document.Extraction.Warnings.ToList());
    }

    public async Task<NormalizedDocument?> ExportDraftAsync(Guid id, CancellationToken ct)
    {
        var document = await store.GetAsync(id, ct);
        if (document is not { Status: ReviewStatus.AwaitingReview, Draft: not null }) return null;
        var result = Preview(document);
        await validator.ValidateDraftAsync(result, ct);
        return result;
    }

    private async Task<PreparedDocument> RequireAsync(Guid id, CancellationToken ct) =>
        await store.GetAsync(id, ct) ?? throw new PreparationException("This document no longer exists. Return to the dashboard.");

    private async Task<PreparedDocument> RequireDraftAsync(Guid id, Guid draftId, CancellationToken ct)
    {
        var document = await RequireAsync(id, ct);
        if (document.Status != ReviewStatus.AwaitingReview || document.Draft?.Id != draftId)
            throw new PreparationException("This draft is no longer awaiting review. Reopen the document to see its current state.");
        return document;
    }

    private static void RequireNotApproved(PreparedDocument document)
    {
        if (document.Status == ReviewStatus.Approved) throw new PreparationException("This revision is approved. Upload a new revision to continue.");
    }

    private static void ValidateMetadata(DocumentMetadata m)
    {
        if (new[] { m.ProjectId, m.DocumentNumber, m.Title, m.Revision }.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200))
            throw new PreparationException("Project, number, title and revision are required (up to 200 characters each).");
        if (m.Language is not ("en" or "fa" or "mixed")) throw new PreparationException("Select English, Persian or mixed language.");
    }

    private static void ValidateRules(string rules)
    {
        if (rules.Length > MaxRulesCharacters) throw new PreparationException("Reference rules exceed the 20,000 character limit.");
    }
}
