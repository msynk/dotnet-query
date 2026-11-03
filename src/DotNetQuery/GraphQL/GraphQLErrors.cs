namespace DotNetQuery.GraphQL;

/// <summary>A single error returned in a GraphQL response's <c>errors</c> array.</summary>
/// <param name="Message">The human-readable error message.</param>
public sealed record GraphQLError(string Message)
{
    /// <summary>
    /// The path of the response field that failed (field names and list indexes, as strings), when the server
    /// reported one.
    /// </summary>
    public IReadOnlyList<string>? Path { get; init; }

    /// <summary>
    /// The machine-readable error code from <c>extensions.code</c> (for example <c>UNAUTHENTICATED</c> or
    /// <c>BAD_USER_INPUT</c>), when the server reported one. Use it to decide whether an error is worth retrying.
    /// </summary>
    public string? Code { get; init; }
}

/// <summary>
/// Thrown when a GraphQL response contains one or more entries in its <c>errors</c> array. The individual
/// errors are available on <see cref="Errors"/>.
/// </summary>
public sealed class GraphQLException : Exception
{
    /// <summary>Creates an exception carrying the GraphQL <paramref name="errors"/>.</summary>
    public GraphQLException(IReadOnlyList<GraphQLError> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors ?? [];
    }

    /// <summary>The errors reported by the GraphQL server.</summary>
    public IReadOnlyList<GraphQLError> Errors { get; }

    /// <summary>Whether any error carries the given <c>extensions.code</c> (case-insensitive).</summary>
    public bool HasCode(string code) => Errors.Any(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase));

    private static string BuildMessage(IReadOnlyList<GraphQLError>? errors) =>
        errors is { Count: > 0 }
            ? string.Join("; ", errors.Select(e => e.Message))
            : "The GraphQL request failed.";
}
