namespace ReviewForge.Core.Pipeline;

public interface IReviewStage
{
    string Name { get; }

    Task ExecuteAsync(ReviewContext ctx, CancellationToken ct);
}