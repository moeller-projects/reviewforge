using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace ReviewForge.Infrastructure.Chat;

/// <summary>
/// Bridges Microsoft.Extensions.AI chat messages to the OpenAI Responses API. The adapter owns
/// the translation so the application can use Responses SDK releases whose streaming API changes
/// independently of Microsoft.Extensions.AI.OpenAI.
/// </summary>
internal sealed class ResponsesChatClientAdapter(ResponsesClient responseClient, string? defaultModelId = null) : IChatClient
{
    private const string ReasoningItemIdKey = "reasoningItemId";
    private static readonly ChatRole DeveloperRole = new("developer");
    private readonly ChatClientMetadata _Metadata = new("openai", responseClient.Endpoint, defaultModelId);

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is not null ? null :
            serviceType == typeof(ChatClientMetadata) ? _Metadata :
            serviceType == typeof(ResponsesClient) ? responseClient :
            serviceType.IsInstanceOfType(this) ? this :
            null;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var messageList = messages as ChatMessage[] ?? messages.ToArray();
        var responseOptions = CreateResponseOptions(options);
        AddInputItems(responseOptions, messageList);

        return responseOptions.StreamingEnabled is true
            ? StreamResponseUpdatesAsync(responseOptions, cancellationToken).ToChatResponseAsync(cancellationToken)
            : CreateResponseAsync(responseOptions, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var responseOptions = CreateResponseOptions(options);
        responseOptions.StreamingEnabled = true;
        AddInputItems(responseOptions, messages);
        return StreamResponseUpdatesAsync(responseOptions, cancellationToken);
    }

    private async Task<ChatResponse> CreateResponseAsync(CreateResponseOptions options, CancellationToken cancellationToken)
    {
        var result = (ResponseResult)await responseClient.CreateResponseAsync(options, cancellationToken).ConfigureAwait(false);
        return ToChatResponse(result);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamResponseUpdatesAsync(
        CreateResponseOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var responseUpdates = await responseClient
            .CreateResponseStreamingAsync(options, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset? createdAt = null;
        string? responseId = null;
        string? modelId = options.Model;
        string? messageId = null;
        var hasFunctionCall = false;

        await foreach (var streamingUpdate in responseUpdates
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            ChatResponseUpdate CreateUpdate(AIContent? content = null) =>
                new(ChatRole.Assistant, content is null ? null : [content])
                {
                    CreatedAt = createdAt,
                    MessageId = messageId,
                    ModelId = modelId,
                    RawRepresentation = streamingUpdate,
                    ResponseId = responseId,
                };

            switch (streamingUpdate)
            {
                case StreamingResponseCreatedUpdate created:
                    createdAt = created.Response.CreatedAt;
                    responseId = created.Response.Id;
                    modelId = created.Response.Model;
                    yield return CreateUpdate();
                    break;

                case StreamingResponseInProgressUpdate inProgress:
                    createdAt = inProgress.Response.CreatedAt;
                    responseId = inProgress.Response.Id;
                    modelId = inProgress.Response.Model;
                    yield return CreateUpdate();
                    break;

                case StreamingResponseOutputItemAddedUpdate itemAdded:
                    messageId = itemAdded.Item.Id;
                    if (itemAdded.Item is FunctionCallResponseItem)
                    {
                        hasFunctionCall = true;
                    }

                    yield return CreateUpdate();
                    break;

                case StreamingResponseOutputTextDeltaUpdate textDelta:
                    yield return CreateUpdate(new TextContent(textDelta.Delta));
                    break;

                case StreamingResponseReasoningSummaryTextDeltaUpdate reasoningDelta:
                    yield return CreateUpdate(CreateReasoningContent(reasoningDelta.Delta, null, reasoningDelta.ItemId));
                    break;

                case StreamingResponseReasoningTextDeltaUpdate reasoningDelta:
                    yield return CreateUpdate(CreateReasoningContent(reasoningDelta.Delta, null, reasoningDelta.ItemId));
                    break;

                case StreamingResponseOutputItemDoneUpdate itemDone when itemDone.Item is FunctionCallResponseItem functionCall:
                    yield return CreateUpdate(ParseFunctionCall(
                        functionCall.FunctionArguments.ToString(), functionCall.CallId, functionCall.FunctionName));
                    break;

                case StreamingResponseRefusalDoneUpdate refusal:
                    yield return CreateUpdate(new ErrorContent(refusal.Refusal)
                    {
                        ErrorCode = nameof(ResponseContentPartKind.Refusal),
                    });
                    break;

                case StreamingResponseErrorUpdate error:
                    yield return CreateUpdate(new ErrorContent(error.Message)
                    {
                        ErrorCode = error.Code,
                        Details = error.Param,
                    });
                    break;

                case StreamingResponseCompletedUpdate completed:
                    createdAt = completed.Response.CreatedAt;
                    responseId = completed.Response.Id;
                    modelId = completed.Response.Model;
                    var usage = ToUsageDetails(completed.Response);
                    var finalUpdate = CreateUpdate(usage is null ? null : new UsageContent(usage));
                    finalUpdate.FinishReason = AsFinishReason(completed.Response.IncompleteStatusDetails?.Reason)
                        ?? (hasFunctionCall ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop);
                    yield return finalUpdate;
                    break;

                case StreamingResponseIncompleteUpdate incomplete:
                    createdAt = incomplete.Response.CreatedAt;
                    responseId = incomplete.Response.Id;
                    modelId = incomplete.Response.Model;
                    yield return CreateUpdate();
                    break;

                case StreamingResponseFailedUpdate failed:
                    createdAt = failed.Response.CreatedAt;
                    responseId = failed.Response.Id;
                    modelId = failed.Response.Model;
                    yield return CreateUpdate(failed.Response.Error is { } responseError
                        ? new ErrorContent(responseError.Message) { ErrorCode = responseError.Code.ToString() }
                        : null);
                    break;

                default:
                    yield return CreateUpdate();
                    break;
            }
        }
    }

    private ChatResponse ToChatResponse(ResponseResult response)
    {
        var message = new ChatMessage(ChatRole.Assistant, (string?)null);
        foreach (var item in response.OutputItems ?? [])
        {
            switch (item)
            {
                case MessageResponseItem outputMessage:
                    message.MessageId = outputMessage.Id;
                    message.CreatedAt = response.CreatedAt;
                    foreach (var part in outputMessage.Content)
                    {
                        if (part.Kind is ResponseContentPartKind.OutputText or ResponseContentPartKind.InputText)
                        {
                            message.Contents.Add(new TextContent(part.Text) { RawRepresentation = part });
                        }
                        else if (part.Kind == ResponseContentPartKind.Refusal)
                        {
                            message.Contents.Add(new ErrorContent(part.Refusal)
                            {
                                ErrorCode = nameof(ResponseContentPartKind.Refusal),
                                RawRepresentation = part,
                            });
                        }
                    }

                    break;

                case FunctionCallResponseItem functionCall:
                    message.Contents.Add(ParseFunctionCall(
                        functionCall.FunctionArguments.ToString(), functionCall.CallId, functionCall.FunctionName));
                    break;

                case ReasoningResponseItem reasoning:
                    message.Contents.Add(CreateReasoningContent(
                        reasoning.GetSummaryText(), reasoning.EncryptedContent, reasoning.Id, item));
                    break;
            }
        }

        var result = new ChatResponse
        {
            CreatedAt = response.CreatedAt,
            FinishReason = AsFinishReason(response.IncompleteStatusDetails?.Reason)
                ?? (message.Contents.OfType<FunctionCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop),
            ModelId = response.Model,
            RawRepresentation = response,
            ResponseId = response.Id,
            Usage = ToUsageDetails(response),
            Messages = message.Contents.Count == 0 ? [] : [message],
        };

        if (response.Error is { } error)
        {
            message.Contents.Add(new ErrorContent(error.Message) { ErrorCode = error.Code.ToString() });
            result.Messages = [message];
        }

        return result;
    }

    private CreateResponseOptions CreateResponseOptions(ChatOptions? options)
    {
        var result = options?.RawRepresentationFactory?.Invoke(this) as CreateResponseOptions ?? new();
        if (options is null)
        {
            result.Model ??= defaultModelId;
            return result;
        }

        result.BackgroundModeEnabled ??= options.AllowBackgroundResponses;
        result.MaxOutputTokenCount ??= options.MaxOutputTokens;
        result.Model ??= options.ModelId ?? defaultModelId;
        result.Temperature ??= options.Temperature;
        result.TopP ??= options.TopP;
        result.ReasoningOptions ??= ToResponseReasoningOptions(options.Reasoning);

        if (options.Instructions is { } instructions)
        {
            result.Instructions = string.IsNullOrEmpty(result.Instructions)
                ? instructions
                : $"{result.Instructions}{Environment.NewLine}{instructions}";
        }

        if (options.Tools is { Count: > 0 } tools)
        {
            foreach (var tool in tools)
            {
                if (tool is not AIFunctionDeclaration function)
                {
                    throw new NotSupportedException($"Responses adapter does not support tool type '{tool.GetType().FullName}'.");
                }

                var schema = BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(function.JsonSchema));
                result.Tools.Add(new FunctionTool(function.Name, schema, null)
                {
                    FunctionDescription = function.Description,
                });
            }

            result.ParallelToolCallsEnabled ??= options.AllowMultipleToolCalls;
            if (result.ToolChoice is null)
            {
                result.ToolChoice = options.ToolMode switch
                {
                    NoneChatToolMode => ResponseToolChoice.CreateNoneChoice(),
                    AutoChatToolMode => ResponseToolChoice.CreateAutoChoice(),
                    RequiredChatToolMode { RequiredFunctionName: { } name } => ResponseToolChoice.CreateFunctionChoice(name),
                    RequiredChatToolMode => ResponseToolChoice.CreateRequiredChoice(),
                    _ => null,
                };
            }
        }

        if (options.ResponseFormat is not null)
        {
            throw new NotSupportedException("Responses adapter does not support a custom response format.");
        }

        return result;
    }

    private static void AddInputItems(CreateResponseOptions options, IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                {
                    options.InputItems.Add(ResponseItem.CreateSystemMessageItem(message.Text));
                }

                continue;
            }

            if (message.Role == DeveloperRole)
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                {
                    options.InputItems.Add(ResponseItem.CreateDeveloperMessageItem(message.Text));
                }

                continue;
            }

            if (message.Role == ChatRole.User)
            {
                var parts = message.Contents.OfType<TextContent>()
                    .Select(content => ResponseContentPart.CreateInputTextPart(content.Text))
                    .ToList();
                if (parts.Count == 0)
                {
                    parts.Add(ResponseContentPart.CreateInputTextPart(string.Empty));
                }

                options.InputItems.Add(ResponseItem.CreateUserMessageItem(parts));
                continue;
            }

            if (message.Role == ChatRole.Assistant)
            {
                foreach (var content in message.Contents)
                {
                    switch (content)
                    {
                        case AIContent { RawRepresentation: ResponseItem rawItem }:
                            options.InputItems.Add(rawItem);
                            break;

                        case TextContent text:
                            options.InputItems.Add(ResponseItem.CreateAssistantMessageItem(text.Text));
                            break;

                        case FunctionCallContent functionCall:
                            var arguments = BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(functionCall.Arguments));
                            options.InputItems.Add(ResponseItem.CreateFunctionCallItem(functionCall.CallId, functionCall.Name, arguments));
                            break;

                        case TextReasoningContent reasoning:
                            var reasoningItem = new ReasoningResponseItem(reasoning.Text)
                            {
                                EncryptedContent = reasoning.ProtectedData,
                            };
                            if (TryGetReasoningItemId(reasoning, out var itemId))
                            {
                                reasoningItem.Id = itemId;
                            }

                            options.InputItems.Add(reasoningItem);
                            break;

                        default:
                            throw new NotSupportedException($"Responses adapter does not support assistant content '{content.GetType().FullName}'.");
                    }
                }

                continue;
            }

            if (message.Role == ChatRole.Tool)
            {
                foreach (var content in message.Contents)
                {
                    if (content is FunctionResultContent functionResult)
                    {
                        options.InputItems.Add(ResponseItem.CreateFunctionCallOutputItem(
                            functionResult.CallId, FormatFunctionResult(functionResult.Result)));
                    }
                    else if (content.RawRepresentation is ResponseItem rawItem)
                    {
                        options.InputItems.Add(rawItem);
                    }
                    else
                    {
                        throw new NotSupportedException($"Responses adapter does not support tool content '{content.GetType().FullName}'.");
                    }
                }

                continue;
            }

            throw new NotSupportedException($"Responses adapter does not support chat role '{message.Role}'.");
        }
    }

    private static string FormatFunctionResult(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        _ => JsonSerializer.Serialize(result),
    };

    private static FunctionCallContent ParseFunctionCall(string arguments, string callId, string name) =>
        FunctionCallContent.CreateFromParsedArguments(
            arguments,
            callId,
            name,
            static json => JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new Dictionary<string, object?>());

    private static TextReasoningContent CreateReasoningContent(
        string? text,
        string? protectedData,
        string? itemId,
        object? rawRepresentation = null)
    {
        var content = new TextReasoningContent(text)
        {
            ProtectedData = protectedData,
            RawRepresentation = rawRepresentation,
        };

        if (!string.IsNullOrEmpty(itemId))
        {
            (content.AdditionalProperties ??= [])[ReasoningItemIdKey] = itemId;
        }

        return content;
    }

    private static bool TryGetReasoningItemId(TextReasoningContent content, out string? itemId)
    {
        itemId = null;
        if (content.AdditionalProperties?.TryGetValue(ReasoningItemIdKey, out var value) is not true)
        {
            return false;
        }

        itemId = value as string ?? (value is JsonElement { ValueKind: JsonValueKind.String } json ? json.GetString() : null);
        return !string.IsNullOrEmpty(itemId);
    }

    private static ResponseReasoningOptions? ToResponseReasoningOptions(ReasoningOptions? reasoning)
    {
        if (reasoning is null)
        {
            return null;
        }

        ResponseReasoningEffortLevel? effort = reasoning.Effort switch
        {
            ReasoningEffort.None => ResponseReasoningEffortLevel.None,
            ReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
            ReasoningEffort.Medium => ResponseReasoningEffortLevel.Medium,
            ReasoningEffort.High => ResponseReasoningEffortLevel.High,
            ReasoningEffort.ExtraHigh => new ResponseReasoningEffortLevel("xhigh"),
            _ => null,
        };
        ResponseReasoningSummaryVerbosity? summary = reasoning.Output switch
        {
            ReasoningOutput.Summary => ResponseReasoningSummaryVerbosity.Concise,
            ReasoningOutput.Full => ResponseReasoningSummaryVerbosity.Detailed,
            _ => null,
        };

        return effort is null && summary is null
            ? null
            : new ResponseReasoningOptions { ReasoningEffortLevel = effort, ReasoningSummaryVerbosity = summary };
    }

    private static UsageDetails? ToUsageDetails(ResponseResult? response)
    {
        if (response?.Usage is not { } usage)
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = usage.InputTokenCount,
            OutputTokenCount = usage.OutputTokenCount,
            TotalTokenCount = usage.TotalTokenCount,
            CachedInputTokenCount = usage.InputTokenDetails?.CachedTokenCount,
            ReasoningTokenCount = usage.OutputTokenDetails?.ReasoningTokenCount,
        };
    }

    private static ChatFinishReason? AsFinishReason(ResponseIncompleteStatusReason? reason) =>
        reason == ResponseIncompleteStatusReason.ContentFilter ? ChatFinishReason.ContentFilter :
        reason == ResponseIncompleteStatusReason.MaxOutputTokens ? ChatFinishReason.Length :
        null;

    public void Dispose()
    {
        // The ResponsesClient's HTTP transport is owned by the factory's configured handler.
    }
}
