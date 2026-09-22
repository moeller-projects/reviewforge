namespace ReviewForge.Core.Pipeline;

public interface IReviewStage
{
    /// <summary>Pipeline position. Lower runs first; must be unique and strictly increasing in the injected sequence.</summary>
    int Order { get; }

    string Name { get; }

    Task ExecuteAsync(ReviewContext ctx, CancellationToken ct);
}