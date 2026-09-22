var builder = DistributedApplication.CreateBuilder(args);

// Secrets as parameters: the dashboard marks them and Aspire fails fast with a clear
// message when unset, instead of the service failing on its first ADO/Codex call.
// Values come from user secrets ("Parameters:ado-pat", "Parameters:openai-api-key",
// "Parameters:api-key"); names only here — never commit real credentials.
var adoPat = builder.AddParameter("ado-pat", secret: true);
var openAiKey = builder.AddParameter("openai-api-key", secret: true);
var apiKey = builder.AddParameter("api-key", secret: true);

// Dev-loop scratch state under temp (matches the prior env pass-through behavior).
var reviewforgeWorkDir = Path.Combine(Path.GetTempPath(), "reviewforge");
Directory.CreateDirectory(reviewforgeWorkDir);

var service = builder.AddProject<Projects.ReviewForge_Service>("reviewforge")
    .WithHttpEndpoint(name: "http")
    // Readiness: store-backed (/health) drives the dashboard health tile; liveness stays
    // /alive for orchestrators.
    .WithHttpHealthCheck("/health")
    .WithHttpHealthCheck("/alive")
    .WithExternalHttpEndpoints()
    .WithEnvironment("ReviewForge__WorkDir", reviewforgeWorkDir)
    .WithEnvironment(
        "ReviewForge__StoreConnectionString",
        $"Data Source={Path.Combine(reviewforgeWorkDir, "reviewforge.db")}")
    .WithEnvironment("REVIEWFORGE_ADO_PAT", adoPat)
    .WithEnvironment("OPENAI_API_KEY", openAiKey)
    .WithEnvironment("REVIEWFORGE_API_KEYS", apiKey)
    // Make the env-driven OTLP contract explicit and greppable. Aspire injects
    // OTEL_EXPORTER_OTLP_ENDPOINT for hosted projects automatically; this assertion
    // documents intent — do NOT set the endpoint here.
    .WithEnvironment("ReviewForge__AgentDebugLogging", "false");

builder.Build().Run();
