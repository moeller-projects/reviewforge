using System.Text.Json;
using Microsoft.Extensions.AI;
using ReviewForge.Infrastructure.Chat;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class OpenAIJsonContextCoverageTests
{
    [Fact]
    public void Source_generated_metadata_roundtrips_adapter_payload_shapes()
    {
        var textJson = JsonSerializer.Serialize("payload", OpenAIJsonContext.Default.String);
        Assert.Equal("payload", JsonSerializer.Deserialize(textJson, OpenAIJsonContext.Default.String));

        var stringsJson = JsonSerializer.Serialize(new[] { "one", "two" }, OpenAIJsonContext.Default.StringArray);
        Assert.Equal(new[] { "one", "two" }, JsonSerializer.Deserialize(stringsJson, OpenAIJsonContext.Default.StringArray));

        IEnumerable<string> values = new List<string> { "first", "second" };
        var enumerableJson = JsonSerializer.Serialize(values, OpenAIJsonContext.Default.IEnumerableString);
        Assert.Equal(new[] { "first", "second" }, JsonSerializer.Deserialize(enumerableJson, OpenAIJsonContext.Default.IEnumerableString));

        using var valuesDocument = JsonDocument.Parse("{\"name\":\"ReviewForge\",\"count\":3,\"enabled\":true}");
        var dictionary = JsonSerializer.Deserialize(valuesDocument.RootElement.GetRawText(), OpenAIJsonContext.Default.IDictionaryStringObject)!;
        Assert.Equal("ReviewForge", ((JsonElement)dictionary["name"]!).GetString());
        Assert.Equal(3, ((JsonElement)dictionary["count"]!).GetInt32());
        Assert.True(((JsonElement)dictionary["enabled"]!).GetBoolean());
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"required\":[\"id\"]}");
        var elementJson = JsonSerializer.Serialize(document.RootElement, OpenAIJsonContext.Default.JsonElement);
        using var roundtrippedDocument = JsonDocument.Parse(elementJson);
        Assert.Equal("object", roundtrippedDocument.RootElement.GetProperty("type").GetString());
        Assert.Equal("id", roundtrippedDocument.RootElement.GetProperty("required")[0].GetString());
    }

    [Fact]
    public void Function_output_elements_preserve_all_responses_content_fields()
    {
        var elements = new List<ResponsesChatClientAdapter.FunctionToolCallOutputElement>
        {
            new()
            {
                Type = "input_file",
                Text = "caption",
                ImageUrl = "data:image/png;base64,AQ==",
                FileId = "file-42",
                FileData = "data:application/pdf;base64,Ag==",
                FileUrl = "https://example.test/file.pdf",
                FileName = "report.pdf",
            },
        };

        var json = JsonSerializer.Serialize(elements, OpenAIJsonContext.Default.ListFunctionToolCallOutputElement);
        var result = Assert.Single(JsonSerializer.Deserialize(json, OpenAIJsonContext.Default.ListFunctionToolCallOutputElement)!);

        Assert.Equal("input_file", result.Type);
        Assert.Equal("caption", result.Text);
        Assert.Equal("data:image/png;base64,AQ==", result.ImageUrl);
        Assert.Equal("file-42", result.FileId);
        Assert.Equal("data:application/pdf;base64,Ag==", result.FileData);
        Assert.Equal("https://example.test/file.pdf", result.FileUrl);
        Assert.Equal("report.pdf", result.FileName);
    }
}
