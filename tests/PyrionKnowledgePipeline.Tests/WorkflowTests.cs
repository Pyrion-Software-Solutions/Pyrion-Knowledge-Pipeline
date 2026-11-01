using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PyrionKnowledgePipeline.Application;
using PyrionKnowledgePipeline.Domain;
using PyrionKnowledgePipeline.Infrastructure;

namespace PyrionKnowledgePipeline.Tests;

public class WorkflowTests
{
    [Fact]
    public async Task Complete_journey_preserves_original_and_reopens_approved_JSON()
    {
        using var w = new TestWorkspace();
        await w.Settings.SaveAsync(new(ModelId: "test-model"), default);
        var workflow = w.Workflow();
        var doc = await workflow.UploadAsync("../../unsafe.docx", new MemoryStream(Fixture.Docx), Fixture.Metadata, "Keep words", default);
        Assert.Equal(ReviewStatus.Extracted, doc.Status);
        Assert.Null(await w.Store.ReadApprovedJsonAsync(doc.Id, default));
        doc = await workflow.AnalyzeAsync(doc.Id, "Keep words", null, default);
        Assert.Equal(ReviewStatus.AwaitingReview, doc.Status);
        Assert.Null(await w.Store.ReadApprovedJsonAsync(doc.Id, default));
        doc = await workflow.ApproveAsync(doc.Id, doc.Draft!.Id, "Test reviewer", default);
        Assert.Equal(ReviewStatus.Approved, doc.Status);
        Assert.Equal(doc.Extraction.Blocks.Select(x => x.Text), doc.Approved!.Blocks.Select(x => x.Text));
        Assert.Equal(doc.Extraction.Blocks[4].Rows, doc.Approved.Blocks[4].Rows);
        Assert.Equal(BlockType.Heading, doc.Approved.Blocks[1].Type);
        var reopenedStore = new LocalDocumentStore(Options.Create(new StorageOptions { RootPath = w.Root }));
        var reopened = await reopenedStore.GetAsync(doc.Id, default);
        Assert.Equal(ReviewStatus.Approved, reopened!.Status);
        Assert.NotNull(reopened.Draft);
        Assert.NotNull(await reopenedStore.ReadApprovedJsonAsync(doc.Id, default));
        var storedOriginal = await File.ReadAllBytesAsync(Path.Combine(w.Root, "documents", doc.Id.ToString("N"), "original.docx"));
        Assert.Equal(SHA256.HashData(Fixture.Docx), SHA256.HashData(storedOriginal));
        Assert.Single(await reopenedStore.ListAsync(default));
    }

    [Fact]
    public async Task Cannot_approve_before_analysis_or_after_rejection_or_with_stale_draft()
    {
        using var w = new TestWorkspace();
        await w.Settings.SaveAsync(new(ModelId: "test-model"), default);
        var workflow = w.Workflow();
        var doc = await workflow.UploadAsync("test.docx", new MemoryStream(Fixture.Docx), Fixture.Metadata, "", default);
        await Assert.ThrowsAsync<PreparationException>(() => workflow.ApproveAsync(doc.Id, Guid.NewGuid(), "Test", default));
        doc = await workflow.AnalyzeAsync(doc.Id, "", null, default);
        var oldDraft = doc.Draft!.Id;
        doc = await workflow.AnalyzeAsync(doc.Id, "Updated rules", null, default);
        await Assert.ThrowsAsync<PreparationException>(() => workflow.ApproveAsync(doc.Id, oldDraft, "Test", default));
        doc = await workflow.RejectAsync(doc.Id, doc.Draft!.Id, "Reclassify headings", default);
        Assert.Equal(ReviewStatus.Rejected, doc.Status);
        await Assert.ThrowsAsync<PreparationException>(() => workflow.ApproveAsync(doc.Id, doc.Draft!.Id, "Test", default));
        Assert.Null(await w.Store.ReadApprovedJsonAsync(doc.Id, default));
    }

    [Fact]
    public async Task Manual_revision_creates_a_new_reviewable_draft_and_approved_revision_is_immutable()
    {
        using var w = new TestWorkspace();
        await w.Settings.SaveAsync(new(ModelId: "test-model"), default);
        var workflow = w.Workflow();
        var doc = await workflow.UploadAsync("test.docx", new MemoryStream(Fixture.Docx), Fixture.Metadata, "", default);
        doc = await workflow.AnalyzeAsync(doc.Id, "", null, default);
        var oldDraft = doc.Draft!.Id;
        var blocks = doc.Draft.Proposal.Blocks.ToList();
        blocks[1] = blocks[1] with { HeadingLevel = 2 };
        doc = await workflow.ReviseAsync(doc.Id, oldDraft, blocks, "Use a subsection", default);
        Assert.NotEqual(oldDraft, doc.Draft!.Id);
        Assert.Single(doc.Draft.Proposal.Issues);
        Assert.StartsWith("Reviewer revision:", doc.Draft.Proposal.Issues[0].Reason);
        Assert.Equal(ReviewStatus.AwaitingReview, doc.Status);
        doc = await workflow.ApproveAsync(doc.Id, doc.Draft.Id, "Test", default);
        Assert.Equal(2, doc.Approved!.Blocks[1].HeadingLevel);
        await Assert.ThrowsAsync<PreparationException>(() => workflow.AnalyzeAsync(doc.Id, "", null, default));
        await Assert.ThrowsAsync<PreparationException>(() => workflow.ApproveAsync(doc.Id, doc.Draft!.Id, "Test", default));
    }

    [Fact]
    public async Task Failed_analysis_keeps_previous_saved_draft_and_never_creates_approval()
    {
        using var w = new TestWorkspace();
        await w.Settings.SaveAsync(new(ModelId: "test-model"), default);
        var mock = new MockLmStudio();
        var workflow = w.Workflow(mock);
        var doc = await workflow.UploadAsync("test.docx", new MemoryStream(Fixture.Docx), Fixture.Metadata, "", default);
        doc = await workflow.AnalyzeAsync(doc.Id, "Original rules", null, default);
        mock.Failure = new PreparationException("Model timed out");
        await Assert.ThrowsAsync<PreparationException>(() => workflow.AnalyzeAsync(doc.Id, "New rules", null, default));
        var saved = await w.Store.GetAsync(doc.Id, default);
        Assert.Equal(doc.Draft!.Id, saved!.Draft!.Id);
        Assert.Equal("Original rules", saved.Rules);
        Assert.Null(saved.Approved);
    }

    [Fact]
    public async Task Rejects_concurrent_stale_saves()
    {
        using var w = new TestWorkspace();
        var doc = await w.Workflow().UploadAsync("test.docx", new MemoryStream(Fixture.Docx), Fixture.Metadata, "", default);
        var first = await w.Store.GetAsync(doc.Id, default);
        var second = await w.Store.GetAsync(doc.Id, default);
        await w.Store.SaveAsync(first!, first!.Version, default);
        await Assert.ThrowsAsync<PreparationException>(() => w.Store.SaveAsync(second!, second!.Version, default));
    }

    [Theory]
    [InlineData("wrong.pdf", false)]
    [InlineData("empty.docx", true)]
    public async Task Rejects_wrong_extension_and_empty_upload(string name, bool empty)
    {
        using var w = new TestWorkspace();
        await Assert.ThrowsAsync<PreparationException>(() => w.Workflow().UploadAsync(name, new MemoryStream(empty ? [] : Fixture.Docx), Fixture.Metadata, "", default));
        Assert.Empty(await w.Store.ListAsync(default));
    }

    [Fact]
    public async Task Rejects_oversized_upload_and_missing_metadata()
    {
        using var w = new TestWorkspace();
        await Assert.ThrowsAsync<PreparationException>(() => w.Workflow().UploadAsync("huge.docx", new MemoryStream(new byte[PreparationWorkflow.MaxUploadBytes + 1]), Fixture.Metadata, "", default));
        await Assert.ThrowsAsync<PreparationException>(() => w.Workflow().UploadAsync("test.docx", new MemoryStream(Fixture.Docx), Fixture.Metadata with { ProjectId = "" }, "", default));
        Assert.Empty(await w.Store.ListAsync(default));
    }
}
