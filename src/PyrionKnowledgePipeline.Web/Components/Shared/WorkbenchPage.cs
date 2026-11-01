using Microsoft.AspNetCore.Components;
using PyrionKnowledgePipeline.Application;

namespace PyrionKnowledgePipeline.Web.Components.Shared;

public abstract class WorkbenchPage : ComponentBase, IDisposable
{
    protected bool Busy { get; private set; }
    protected string? Error { get; set; }
    protected string? Notice { get; set; }
    protected CancellationTokenSource Lifetime { get; } = new();
    private CancellationTokenSource? operation;

    protected async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (Busy) return;
        Busy = true;
        Error = Notice = null;
        operation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        try { await action(operation.Token); }
        catch (PreparationException ex) { Error = ex.Message; }
        catch (OperationCanceledException) { if (!Lifetime.IsCancellationRequested) Notice = "Operation canceled. No new approval was saved."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "The local file could not be read or saved. Check available disk space and folder permissions, then reopen the document."; }
        catch (System.Text.DecoderFallbackException) { Error = "The rules file is not valid text. Save it as UTF-8 and retry."; }
        finally { operation.Dispose(); operation = null; Busy = false; }
    }

    protected void Cancel() => operation?.Cancel();
    public void Dispose() { Lifetime.Cancel(); Lifetime.Dispose(); GC.SuppressFinalize(this); }
    protected static string StatusLabel(PyrionKnowledgePipeline.Domain.ReviewStatus status) => status switch
    {
        PyrionKnowledgePipeline.Domain.ReviewStatus.AwaitingReview => "Awaiting review",
        PyrionKnowledgePipeline.Domain.ReviewStatus.Extracted => "Ready for analysis",
        _ => status.ToString()
    };
}
