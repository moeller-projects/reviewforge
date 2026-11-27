using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ReviewForge.Core.Profiling;

/// <summary>Physical adapter-call boundary, below governor, retries, guards and function invocation.
/// SDK-hidden attempts and provider cost are unavailable; no estimates or raw properties are read.</summary>
public sealed class ProfiledChatClient(IChatClient inner, string? provider, string? model) : DelegatingChatClient(inner)
{
    // The adapter projects a typed provider status into this one closed metadata field.
    public const string ProviderOutcomeKey = "reviewforge.profile.outcome";
    private static string? ProviderOutcome(AdditionalPropertiesDictionary? properties)
        => properties?.TryGetValue(ProviderOutcomeKey, out var value) == true && value is string text
            && text is "failed" or "incomplete" ? text : null;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var context = ModelCallContext.Current;
        var call = context?.Collector.Begin(context.Stage, context.Profile, provider, options?.ModelId ?? model);
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            call?.LinkTools(response.Messages.SelectMany(m => m.Contents));
            call?.Complete(ProviderOutcome(response.AdditionalProperties) ?? "completed", response.Usage);
            return response;
        }
        catch (Exception ex)
        {
            call?.Complete(ex is OperationCanceledException ? "cancelled" : "failed", null);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var context = ModelCallContext.Current;
        var call = context?.Collector.Begin(context.Stage, context.Profile, provider, options?.ModelId ?? model);
        UsageDetails? usage = null;
        var outcome = "interrupted";
        string? providerOutcome = null;
        Exception? failure = null;
        IAsyncEnumerator<ChatResponseUpdate> enumerator;
        try { enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken); }
        catch (Exception ex)
        {
            call?.Complete(ex is OperationCanceledException ? "cancelled" : "failed", null);
            throw;
        }
        try
        {
            while (true)
            {
                bool next;
                try { next = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception ex) { failure = ex; outcome = ex is OperationCanceledException ? "cancelled" : "failed"; throw; }
                if (!next) break;
                var update = enumerator.Current;
                call?.LinkTools(update.Contents);
                providerOutcome = ProviderOutcome(update.AdditionalProperties) ?? providerOutcome;
                // Responses usage updates are cumulative snapshots. Retain last supported values, never sum snapshots.
                foreach (var content in update.Contents.OfType<UsageContent>()) usage = content.Details;
                yield return update;
            }
            outcome = providerOutcome ?? "completed";
        }
        finally
        {
            try { await enumerator.DisposeAsync().ConfigureAwait(false); }
            catch when (failure is not null) { /* Preserve the original streaming failure; a secondary disposal failure must not replace it. */ }
            catch (Exception ex) { outcome = ex is OperationCanceledException ? "cancelled" : "failed"; throw; }
            finally { call?.Complete(outcome, usage); }
        }
    }
}

/// <summary>Assigns a fixed agent profile for each invocation without mutating a shared provider client.</summary>
public sealed class ModelProfileChatClient(IChatClient inner, ModelCallProfile profile, Guid? invocationId = null) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var current = ModelCallContext.Current;
        using var scope = current is null ? null : ModelCallContext.Enter(current.Collector, current.Stage, profile, newInvocation: true, invocationId: invocationId);
        return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var current = ModelCallContext.Current;
        using var scope = current is null ? null : ModelCallContext.Enter(current.Collector, current.Stage, profile, newInvocation: true, invocationId: invocationId);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false)) yield return update;
    }
}
