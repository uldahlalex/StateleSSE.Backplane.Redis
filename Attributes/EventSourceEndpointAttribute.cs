namespace StateleSSE.Backplane.Redis.Attributes;

/// <summary>
/// Marks an endpoint as a Server-Sent Events (EventSource) endpoint
/// Used by code generators to create typed EventSource client methods
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EventSourceEndpointAttribute : Attribute
{
    /// <summary>
    /// The event DTO type this endpoint streams
    /// </summary>
    public Type EventType { get; }

    /// <summary>
    /// Marks a controller method as an EventSource endpoint
    /// </summary>
    /// <param name="eventType">The type of event DTO this endpoint streams (e.g., typeof(RoundStartedEvent))</param>
    public EventSourceEndpointAttribute(Type eventType)
    {
        EventType = eventType;
    }
}
