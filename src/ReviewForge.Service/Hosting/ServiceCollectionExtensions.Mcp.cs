namespace ReviewForge.Service;

public static partial class ServiceCollectionExtensions
{
    private static IServiceCollection AddReviewForgeMcp(this IServiceCollection services)
    {
        services.AddSingleton<RunSubmissionService>();
        services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<ReviewForgeMcpTools>();
        return services;
    }
}