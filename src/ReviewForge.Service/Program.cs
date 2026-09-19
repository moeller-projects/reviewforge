using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Console;
using ReviewForge.Service;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole(options => options.FormatterName = CompactConsoleFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<CompactConsoleFormatter, ConsoleFormatterOptions>();
builder.Services.AddReviewForge(builder.Configuration);
builder.Services.AddOpenApi(ApiDocsRegistration.Configure);

var app = builder.Build();
app.MapReviewForgeEndpoints();
app.MapDocsEndpoints();
app.Run();

// Exposed for WebApplicationFactory in tests; startup wiring itself is not unit-tested.
[ExcludeFromCodeCoverage]
public partial class Program;