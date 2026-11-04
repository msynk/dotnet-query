namespace DotNetQuery.Synchronization;

/// <summary>
/// What a <see cref="QueryEventSynchronizer"/> knows about one received event beyond its payload: its identity (for
/// duplicate detection), its position (entity version, stream sequence) for ordering, its scope and its correlation.
/// Every field is optional; supply what the server contract actually guarantees.
/// </summary>
/// <remarks>
/// Ordering decisions use <see cref="Version"/> and <see cref="Sequence"/> only - never <see cref="Timestamp"/>, because
/// clocks of different servers (and of server and client) do not establish an authoritative order.
/// </remarks>
public readonly record struct QueryEventMetadata
{
    /// <summary>
    /// A stable, unique identifier of the event (the same value on every redelivery), used to discard duplicates within
    /// the deduplication window. <see langword="null"/> when the contract has none.
    /// </summary>
    public string? EventId { get; init; }

    /// <summary>
    /// The entity or aggregate the event is about. Together with <see cref="Version"/> it lets the synchronizer reject an
    /// event older than one already applied for the same entity.
    /// </summary>
    public string? EntityId { get; init; }

    /// <summary>
    /// The entity's version after the change (monotonically increasing per <see cref="EntityId"/>, for example a row
    /// version). An event whose version is not greater than the last applied version of its entity is stale.
    /// </summary>
    public long? Version { get; init; }

    /// <summary>
    /// A server-assigned position of the event in its <see cref="Stream"/>. An event at or below the stream's checkpoint is
    /// stale; with <see cref="QueryEventSyncOptions.ContiguousSequences"/> a jump past the next position is a gap that
    /// triggers recovery.
    /// </summary>
    public long? Sequence { get; init; }

    /// <summary>The sequence stream the <see cref="Sequence"/> belongs to (for example a partition). <see langword="null"/> is the default stream.</summary>
    public string? Stream { get; init; }

    /// <summary>
    /// The scope (tenant, workspace, user) the event belongs to. When the synchronizer has a
    /// <see cref="QueryEventSynchronizer.Scope"/>, events of another scope are ignored; events without a scope apply everywhere.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Correlates the event with the operation that caused it - for example the id of a mutation this client sent - so a
    /// mapping configured with <see cref="QueryEventMapping{TEvent}.IgnoreLocalEchoes"/> can skip its own echo.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>When the server produced the event. For diagnostics only; never used for ordering.</summary>
    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>
/// Implemented by event payloads that describe their own <see cref="QueryEventMetadata"/>, so mappings need no
/// <see cref="QueryEventMapping{TEvent}.WithMetadata"/> extractor. <see cref="QueryEventEnvelope{TPayload}"/> implements it.
/// </summary>
public interface IQueryEventMetadataProvider
{
    /// <summary>Returns the metadata of this event.</summary>
    QueryEventMetadata GetQueryEventMetadata();
}

/// <summary>
/// An optional, ready-made event envelope carrying the metadata fields next to a typed payload. Servers are free to use
/// their own contracts instead - describe them with <see cref="QueryEventMapping{TEvent}.WithMetadata"/>.
/// </summary>
/// <typeparam name="TPayload">The event payload type.</typeparam>
public sealed record QueryEventEnvelope<TPayload> : IQueryEventMetadataProvider
{
    /// <summary>A stable, unique identifier of the event (see <see cref="QueryEventMetadata.EventId"/>).</summary>
    public string? EventId { get; init; }

    /// <summary>An application-defined event type name, for diagnostics.</summary>
    public string? EventType { get; init; }

    /// <summary>The entity the event is about (see <see cref="QueryEventMetadata.EntityId"/>).</summary>
    public string? EntityId { get; init; }

    /// <summary>The entity version after the change (see <see cref="QueryEventMetadata.Version"/>).</summary>
    public long? Version { get; init; }

    /// <summary>The server-assigned position in <see cref="Stream"/> (see <see cref="QueryEventMetadata.Sequence"/>).</summary>
    public long? Sequence { get; init; }

    /// <summary>The sequence stream (see <see cref="QueryEventMetadata.Stream"/>).</summary>
    public string? Stream { get; init; }

    /// <summary>The scope or tenant (see <see cref="QueryEventMetadata.Scope"/>).</summary>
    public string? Scope { get; init; }

    /// <summary>The correlation id (see <see cref="QueryEventMetadata.CorrelationId"/>).</summary>
    public string? CorrelationId { get; init; }

    /// <summary>When the server produced the event; diagnostics only.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>The event payload.</summary>
    public required TPayload Payload { get; init; }

    /// <inheritdoc />
    public QueryEventMetadata GetQueryEventMetadata() => new()
    {
        EventId = EventId,
        EntityId = EntityId,
        Version = Version,
        Sequence = Sequence,
        Stream = Stream,
        Scope = Scope,
        CorrelationId = CorrelationId,
        Timestamp = Timestamp,
    };
}
