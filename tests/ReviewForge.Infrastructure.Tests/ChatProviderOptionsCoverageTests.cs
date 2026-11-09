using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Chat;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class ChatProviderOptionsCoverageTests
{
    [Theory]
    [InlineData("openai", ChatProviderKind.OpenAi)]
    [InlineData("OPENAI-CODEX", ChatProviderKind.OpenAiCodex)]
    public void Configuration_provider_names_parse_case_insensitively(string value, ChatProviderKind expected)
    {
        Assert.True(ChatProviderKindExtensions.TryParseConfigurationValue(value, out var provider));
        Assert.Equal(expected, provider);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("codex")]
    public void Unknown_configuration_provider_name_is_rejected(string? value)
    {
        Assert.False(ChatProviderKindExtensions.TryParseConfigurationValue(value, out var provider));
        Assert.Equal(ChatProviderKind.Unspecified, provider);
    }

    [Fact]
    public void Provider_validation_accepts_matching_and_unprefixed_models()
    {
        var withPrefix = new ChatProviderOptions
        {
            Provider = ChatProviderKind.OpenAi,
            Model = "openai:gpt-5",
            FollowUpModel = "openai:gpt-5-mini",
        };
        var withoutPrefix = new ChatProviderOptions
        {
            Provider = ChatProviderKind.OpenAiCodex,
            Model = "gpt-5",
            FollowUpModel = "gpt-5-mini",
        };

        Assert.Empty(withPrefix.Validate(new ValidationContext(withPrefix)));
        Assert.Empty(withoutPrefix.Validate(new ValidationContext(withoutPrefix)));
    }

    [Fact]
    public void Provider_validation_reports_invalid_kind_and_mismatched_model_prefix()
    {
        var options = new ChatProviderOptions
        {
            Provider = (ChatProviderKind)99,
            Model = "openai:gpt-5",
            FollowUpModel = "openai-codex:gpt-5-mini",
        };

        var failures = options.Validate(new ValidationContext(options)).ToArray();

        Assert.Equal(2, failures.Length);
        Assert.Contains(failures, failure => failure.MemberNames.Contains(nameof(options.Provider)));
        Assert.Contains(failures, failure => failure.MemberNames.Contains(nameof(options.FollowUpModel)));
    }

    [Fact]
    public void Continuation_token_roundtrips_response_id_and_optional_sequence_number()
    {
        var token = new ResponsesClientContinuationToken("resp_42")
        {
            SequenceNumber = 17,
        };
        using var document = JsonDocument.Parse(token.ToBytes());
        Assert.Equal("resp_42", document.RootElement.GetProperty("responseId").GetString());
        Assert.Equal(17, document.RootElement.GetProperty("sequenceNumber").GetInt32());

        var parsed = ResponsesClientContinuationToken.FromToken(
            new SerializedContinuationToken(Encoding.UTF8.GetBytes("{\"responseId\":\"resp_42\",\"sequenceNumber\":17}")));
        Assert.Equal("resp_42", parsed.ResponseId);
        Assert.Equal(17, parsed.SequenceNumber);
        Assert.Same(token, ResponsesClientContinuationToken.FromToken(token));
    }

    [Fact]
    public void Continuation_token_rejects_empty_missing_and_unknown_payloads()
    {
        Assert.Throws<ArgumentException>(() => ResponsesClientContinuationToken.FromToken(new SerializedContinuationToken(ReadOnlyMemory<byte>.Empty)));
        Assert.Throws<ArgumentException>(() => ResponsesClientContinuationToken.FromToken(
            new SerializedContinuationToken(Encoding.UTF8.GetBytes("{\"sequenceNumber\":3}"))));
        Assert.Throws<ArgumentException>(() => ResponsesClientContinuationToken.FromToken(
            new SerializedContinuationToken(Encoding.UTF8.GetBytes("{\"responseId\":\"resp_1\",\"other\":true}"))));
    }

    private sealed class SerializedContinuationToken(ReadOnlyMemory<byte> bytes) : ResponseContinuationToken
    {
        public override ReadOnlyMemory<byte> ToBytes() => bytes;
    }
}
