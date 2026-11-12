using System.Text.Json.Nodes;

namespace Layal.Services;

public enum AgentModelProvider
{
    Cloud,
    Local
}

public sealed record AgentModelRequest(
    AgentModelProvider Provider,
    string Model,
    JsonArray Messages,
    JsonArray ToolDefinitions,
    string? BaseUrl = null,
    string? ApiKey = null);

/// <summary>Provider-neutral interface for one non-streaming, tool-capable agent turn.</summary>
public interface IAgentModelClient
{
    Task<AgentTurnResult> AgentTurnAsync(AgentModelRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a no-side-effect tool-call capability check. Cloud providers retain their
    /// existing behavior; local providers must demonstrate a structured tool call before a task starts.
    /// </summary>
    Task ValidateToolCallingAsync(AgentModelProvider provider, string model, CancellationToken cancellationToken = default);
}

/// <summary>Selects exactly one configured model transport. There is deliberately no fallback path.</summary>
public sealed class AgentModelClientRouter : IAgentModelClient
{
    private readonly IAgentModelClient _cloud;
    private readonly IAgentModelClient _local;

    public AgentModelClientRouter(IAgentModelClient cloud, IAgentModelClient local)
    {
        _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
        _local = local ?? throw new ArgumentNullException(nameof(local));
    }

    public Task<AgentTurnResult> AgentTurnAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Provider switch
        {
            AgentModelProvider.Cloud => _cloud.AgentTurnAsync(request, cancellationToken),
            AgentModelProvider.Local => _local.AgentTurnAsync(request, cancellationToken),
            _ => throw new InvalidOperationException("The requested agent model provider is not supported.")
        };
    }

    public Task ValidateToolCallingAsync(AgentModelProvider provider, string model, CancellationToken cancellationToken = default) =>
        provider switch
        {
            AgentModelProvider.Cloud => _cloud.ValidateToolCallingAsync(provider, model, cancellationToken),
            AgentModelProvider.Local => _local.ValidateToolCallingAsync(provider, model, cancellationToken),
            _ => throw new InvalidOperationException("The requested agent model provider is not supported.")
        };
}
