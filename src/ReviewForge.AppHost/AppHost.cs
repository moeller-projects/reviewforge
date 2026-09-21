var builder = DistributedApplication.CreateBuilder(args);
var reviewforgeWorkDir = Path.Combine(Path.GetTempPath(), "reviewforge");
Directory.CreateDirectory(reviewforgeWorkDir);
builder.AddProject("reviewforge", "../ReviewForge.Service/ReviewForge.Service.csproj")
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints()
    .WithEnvironment("ReviewForge__WorkDir", reviewforgeWorkDir)
    .WithEnvironment(
        "ReviewForge__StoreConnectionString",
        $"Data Source={Path.Combine(reviewforgeWorkDir, "reviewforge.db")}")
    .WithEnvironment(
        "REVIEWFORGE_ADO_PAT",
        Environment.GetEnvironmentVariable("REVIEWFORGE_ADO_PAT") ?? string.Empty)
    .WithEnvironment(
        "OPENAI_API_KEY",
        Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty);

builder.Build().Run();