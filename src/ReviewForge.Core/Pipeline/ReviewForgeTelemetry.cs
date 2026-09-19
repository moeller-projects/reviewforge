using System.Diagnostics;

namespace ReviewForge.Core.Pipeline;

/// <summary>Single ActivitySource for the whole pipeline; the service host wires it into OTel.</summary>
public static class ReviewForgeTelemetry
{
    public const string SourceName = "ReviewForge";

    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}