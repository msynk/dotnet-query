namespace DotNetQuery.GraphQL;

/// <summary>A single error returned in a GraphQL response's <c>errors</c> array.</summary>
/// <param name="Message">The human-readable error message.</param>
public sealed record GraphQLError(string Message);

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

    private static string BuildMessage(IReadOnlyList<GraphQLError>? errors) =>
        errors is { Count: > 0 }
            ? string.Join("; ", errors.Select(e => e.Message))
            : "The GraphQL request failed.";
}
