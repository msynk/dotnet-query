namespace DotNetQuery;

/// <summary>
/// Generates query keys, query definitions and mutations for an API interface (a Refit interface, an interface
/// generated from an OpenAPI document, or a hand-written service interface). The generator ships in the DotNetQuery
/// package; the generated code is plain C# with no reflection, so it is trim- and NativeAOT-safe.
/// </summary>
/// <remarks>
/// <para>
/// Put the attribute on the interface, or - for an interface you cannot edit, such as one generated from an OpenAPI
/// document or defined in another assembly - on the assembly with the interface's type:
/// <c>[assembly: GenerateQueries(typeof(IPetStoreApi))]</c>.
/// </para>
/// <para>
/// A static class named <c>&lt;Name&gt;Queries</c> is generated next to the interface (<c>ITodoApi</c> gives
/// <c>TodoApiQueries</c>). For every query method it contains a key factory (<c>Keys.GetTodo(id)</c>) and a
/// definition factory (<c>GetTodo(api, id)</c>, a <see cref="QueryDefinition{T}"/>). For every mutation method it
/// contains the mutation function (<c>AddTodoFn(api)</c>), its options with a mutation key and invalidation
/// (<c>AddTodoOptions()</c>), and a factory creating the <see cref="Mutation{TVariables, TData}"/>
/// (<c>AddTodo(client, api)</c>). A mutation with several parameters gets a generated variables record.
/// </para>
/// <para>
/// Methods are classified by <see cref="QueryMethodAttribute"/> / <see cref="MutationMethodAttribute"/>, else by
/// Refit's HTTP verb attributes (<c>[Get]</c> and <c>[Head]</c> are queries; <c>[Post]</c>, <c>[Put]</c>,
/// <c>[Patch]</c> and <c>[Delete]</c> are mutations), else by name (methods starting with <c>Get</c>, <c>List</c>,
/// <c>Find</c>, <c>Search</c>, <c>Fetch</c>, <c>Load</c>, <c>Query</c>, <c>Count</c> or <c>Read</c> are queries).
/// Methods must return <see cref="Task{TResult}"/> or <see cref="ValueTask{TResult}"/> (mutations may also return
/// <see cref="Task"/> or <see cref="ValueTask"/>); a <see cref="CancellationToken"/> parameter receives the
/// query's or mutation's token. Overloads that differ only by a cancellation token generate one member.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class GenerateQueriesAttribute : Attribute
{
    /// <summary>Generates queries for the interface the attribute is placed on.</summary>
    public GenerateQueriesAttribute()
    {
    }

    /// <summary>Generates queries for <paramref name="api"/> (use on the assembly for interfaces you cannot edit).</summary>
    /// <param name="api">The interface type.</param>
    public GenerateQueriesAttribute(Type api) => Api = api;

    /// <summary>Gets the interface to generate for, when the attribute is placed on the assembly.</summary>
    public Type? Api { get; }

    /// <summary>
    /// Gets or sets the first key segment shared by every generated key (default: the interface name without its
    /// leading <c>I</c>). Invalidating it refreshes every query of the API.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>Gets or sets the name of the generated class (default: <c>&lt;Name&gt;Queries</c>).</summary>
    public string? ClassName { get; set; }
}

/// <summary>Marks an API method as a query for <see cref="GenerateQueriesAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class QueryMethodAttribute : Attribute
{
    /// <summary>Gets or sets the name used for the generated members and the key segment (default: the method name without <c>Async</c>).</summary>
    public string? Name { get; set; }
}

/// <summary>Marks an API method as a mutation for <see cref="GenerateQueriesAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class MutationMethodAttribute : Attribute
{
    /// <summary>Gets or sets the name used for the generated members and the mutation key (default: the method name without <c>Async</c>).</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the queries invalidated when the mutation settles, as names of query methods of the same
    /// interface (for example <c>nameof(ITodoApi.GetTodosAsync)</c>); every query of that method is invalidated,
    /// whatever its arguments. Default: every query of the API. An empty array invalidates nothing.
    /// </summary>
    public string[]? Invalidates { get; set; }
}

/// <summary>Excludes an API method from <see cref="GenerateQueriesAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class QueryIgnoreAttribute : Attribute;
