using System.Collections.Immutable;
using DotNetQuery.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DotNetQuery.SourceGenerators.Tests;

public class QueriesGeneratorTests
{
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)),
        MetadataReference.CreateFromFile(typeof(QueryKey).Assembly.Location),
    ];

    // A consumer that builds XML documentation, has nullable enabled and treats warnings as errors.
    private static CSharpCompilation Compile(params string[] sources) =>
        CSharpCompilation.Create(
            "Consumer",
            sources.Select(source => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Diagnose))),
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, generalDiagnosticOption: ReportDiagnostic.Error));

    private static (Compilation Output, ImmutableArray<Diagnostic> GeneratorDiagnostics, GeneratorDriverRunResult Result) Run(params string[] sources)
    {
        var driver = CSharpGeneratorDriver.Create(new QueriesGenerator());
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(Compile(sources), out var output, out var diagnostics);
        return (output, diagnostics, driver.GetRunResult());
    }

    private static void AssertCompilesCleanly(Compilation output)
    {
        var problems = output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private const string Api = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using DotNetQuery;

        namespace Sample;

        /// <summary>A todo.</summary>
        public sealed record Todo(int Id, string Title);

        /// <summary>Filters.</summary>
        public enum Filter { /// <summary>All.</summary>
        All, /// <summary>Open.</summary>
        Open }

        /// <summary>The API.</summary>
        [GenerateQueries]
        public interface ITodoApi
        {
            /// <summary>Lists.</summary>
            Task<List<Todo>> GetTodosAsync(Filter filter = Filter.Open, string? search = null, CancellationToken cancellationToken = default);

            /// <summary>Overload without a token.</summary>
            Task<Todo> GetTodoAsync(int id);

            /// <summary>Overload with a token.</summary>
            Task<Todo> GetTodoAsync(int id, CancellationToken cancellationToken);

            /// <summary>Keyword parameter.</summary>
            ValueTask<int> CountAsync(string @class, long api, CancellationToken cancellationToken);

            /// <summary>Adds.</summary>
            [MutationMethod(Invalidates = new[] { nameof(GetTodosAsync) })]
            Task<Todo> AddAsync(string title, bool urgent, CancellationToken cancellationToken);

            /// <summary>Deletes.</summary>
            Task DeleteAsync(int id);

            /// <summary>No variables.</summary>
            ValueTask<bool> ClearAsync();
        }
        """;

    [Fact]
    public void Generated_code_compiles_without_warnings_in_a_documented_nullable_consumer()
    {
        var (output, generatorDiagnostics, result) = Run(Api);

        Assert.Empty(generatorDiagnostics);
        AssertCompilesCleanly(output);
        var source = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("public static partial class TodoApiQueries", source);
        Assert.Contains("global::Sample.Filter filter = global::Sample.Filter.Open", source);
        Assert.Contains("string? search = default", source);
        Assert.Contains("CountAsync(@class, api, cancellationToken).AsTask()", source); // keyword parameter, no clash with "api"
        Assert.Contains("api_", source); // the generated api parameter renamed around the user's "api"
        Assert.Contains("public sealed record AddVariables(string Title, bool Urgent);", source);
        Assert.Equal(1, CountOccurrences(source, "QueryDefinition<global::Sample.Todo> GetTodo(")); // token overload wins
        Assert.Contains("api.GetTodoAsync(id, cancellationToken)", source);
    }

    [Fact]
    public void Assembly_level_attribute_generates_for_an_unannotated_interface_with_a_custom_prefix_and_class_name()
    {
        var (output, generatorDiagnostics, result) = Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using DotNetQuery;

            [assembly: GenerateQueries(typeof(Generated.IPetsClient), KeyPrefix = "pets", ClassName = "Pets")]

            namespace Generated
            {
                /// <summary>Generated client.</summary>
                public interface IPetsClient
                {
                    /// <summary>Gets.</summary>
                    Task<string> GetPetAsync(long id, CancellationToken cancellationToken);
                }
            }
            """);

        Assert.Empty(generatorDiagnostics);
        AssertCompilesCleanly(output);
        var source = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("public static partial class Pets", source);
        Assert.Contains("QueryKey.Create(\"pets\")", source);
    }

    [Fact]
    public void Unsupported_annotated_methods_are_reported_and_skipped()
    {
        var (output, generatorDiagnostics, _) = Run("""
            using System.Threading.Tasks;
            using DotNetQuery;

            [GenerateQueries]
            public interface IApi
            {
                [InfiniteQueryMethod("cursor")] Task<int> GetPageAsync(int offset);
                [QueryMethod] Task RefreshAsync();
                [MutationMethod] Task<int> SaveAsync(ref int value);
                [MutationMethod] int Sync(int value);
                Task<int> GetCountAsync();
            }
            """);

        Assert.Equal(4, generatorDiagnostics.Count(d => d.Id == "DNQ001"));
        Assert.All(generatorDiagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.DefaultSeverity)); // errors here only because the consumer treats warnings as errors
        Assert.Contains(generatorDiagnostics, d => d.GetMessage().Contains("no parameter named 'cursor'", StringComparison.Ordinal));
        Assert.Contains(generatorDiagnostics, d => d.GetMessage().Contains("a query must return data", StringComparison.Ordinal));
        Assert.Contains("GetCount(", output.SyntaxTrees.Last().ToString()); // the valid method is still generated
    }

    [Fact]
    public void Unknown_invalidation_targets_are_reported()
    {
        var (_, generatorDiagnostics, _) = Run("""
            using System.Threading.Tasks;
            using DotNetQuery;

            [GenerateQueries]
            public interface IApi
            {
                Task<int> GetCountAsync();
                [MutationMethod(Invalidates = new[] { "GetCountAsync", "Missing" })] Task<int> AddAsync(int value);
            }
            """);

        var diagnostic = Assert.Single(generatorDiagnostics);
        Assert.Equal("DNQ002", diagnostic.Id);
        Assert.Contains("'Missing'", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Static_classes_and_generic_types_are_rejected()
    {
        var (_, generatorDiagnostics, result) = Run("""
            using System.Threading.Tasks;
            using DotNetQuery;

            [assembly: GenerateQueries(typeof(Helpers))]
            [assembly: GenerateQueries(typeof(IRepository<>))]

            public static class Helpers { public static Task<int> GetAsync() => Task.FromResult(1); }
            public interface IRepository<T> { Task<T> GetAsync(int id); }
            """);

        Assert.Equal(2, generatorDiagnostics.Count(d => d.Id == "DNQ003"));
        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void A_class_generates_from_its_public_instance_methods_including_inherited_ones()
    {
        var (output, generatorDiagnostics, result) = Run("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using DotNetQuery;

            namespace Kiota;

            /// <summary>Base.</summary>
            public abstract class ApiBase : IAsyncDisposable
            {
                /// <summary>Gets the version.</summary>
                public Task<string> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult("1");

                /// <summary>Disposes.</summary>
                public ValueTask DisposeAsync() => default;
            }

            /// <summary>A thin wrapper over a Kiota client.</summary>
            [GenerateQueries(KeyPrefix = "users")]
            public sealed class UsersApi : ApiBase
            {
                /// <summary>Gets.</summary>
                public Task<string> GetUserAsync(int id, CancellationToken cancellationToken) => Task.FromResult("u" + id);

                /// <summary>Renames.</summary>
                public Task RenameAsync(int id, string name) => Task.CompletedTask;

                private Task<int> GetSecretAsync() => Task.FromResult(42);

                /// <summary>Not async.</summary>
                public override string ToString() => "users";
            }
            """);

        Assert.Empty(generatorDiagnostics);
        AssertCompilesCleanly(output);
        var source = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("public static partial class UsersApiQueries", source);
        Assert.Contains("GetUser(global::Kiota.UsersApi api, int id", source);
        Assert.Contains("GetVersion(global::Kiota.UsersApi api", source);
        Assert.Contains("Rename(global::DotNetQuery.IQueryClient client", source);
        Assert.DoesNotContain("Secret", source);
        Assert.DoesNotContain("DisposeAsync", source);
    }

    [Fact]
    public void Generic_methods_generate_generic_members_keyed_by_type()
    {
        var (output, generatorDiagnostics, result) = Run("""
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using DotNetQuery;

            namespace Sample;

            /// <summary>An entity.</summary>
            public interface IEntity { }

            /// <summary>The API.</summary>
            [GenerateQueries]
            public interface IStore
            {
                /// <summary>Gets one.</summary>
                Task<T?> GetAsync<T>(int id, CancellationToken cancellationToken) where T : class, IEntity, new();

                /// <summary>Lists.</summary>
                Task<List<T>> ListAsync<T>() where T : struct;

                /// <summary>Saves.</summary>
                Task<T> SaveAsync<T>(T entity, bool overwrite) where T : notnull;
            }
            """);

        Assert.Empty(generatorDiagnostics);
        AssertCompilesCleanly(output);
        var source = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("Get<T>(int id) where T : class, global::Sample.IEntity, new() => GetPrefix.Append(global::DotNetQuery.QueryKey.Create<T>()[0], id)", source);
        Assert.Contains("api.GetAsync<T>(id, cancellationToken)", source);
        Assert.Contains("public sealed record SaveVariables<T>(T Entity, bool Overwrite) where T : notnull;", source);
    }

    [Fact]
    public void Infinite_query_methods_generate_keys_page_functions_and_factories()
    {
        var (output, generatorDiagnostics, result) = Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using DotNetQuery;

            namespace Sample;

            /// <summary>A page.</summary>
            public sealed record FeedPage(string[] Items, string? Next);

            /// <summary>The API.</summary>
            [GenerateQueries]
            public interface IFeedApi
            {
                /// <summary>Pages through the feed.</summary>
                [InfiniteQueryMethod("cursor")]
                Task<FeedPage> GetFeedAsync(string filter, string? cursor, CancellationToken cancellationToken);

                /// <summary>Posts.</summary>
                [MutationMethod(Invalidates = new[] { nameof(GetFeedAsync) })]
                Task PostAsync(string text);
            }
            """);

        Assert.Empty(generatorDiagnostics);
        AssertCompilesCleanly(output);
        var source = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("public static global::DotNetQuery.QueryKey GetFeed(string filter) => GetFeedPrefix.Append(filter);", source);
        Assert.Contains("GetFeedPageFn(global::Sample.IFeedApi api, string filter)", source);
        Assert.Contains("InfiniteQuery<string?, global::Sample.FeedPage> GetFeed(global::DotNetQuery.IQueryClient client, global::Sample.IFeedApi api, global::DotNetQuery.InfiniteQueryOptions<string?, global::Sample.FeedPage> options, string filter)", source);
        Assert.Contains("new global::DotNetQuery.QueryKey[] { Keys.GetFeedPrefix }", source);
    }

    [Fact]
    public void Unrelated_edits_reuse_the_generated_output()
    {
        var compilation = Compile(Api, "public static class Other { }");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new QueriesGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);

        var edited = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees.Last(),
            CSharpSyntaxTree.ParseText("public static class Other { public static int Value => 1; }", new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Diagnose)));
        driver = driver.RunGenerators(edited);

        var outputs = driver.GetRunResult().Results.Single().TrackedOutputSteps.SelectMany(step => step.Value).SelectMany(step => step.Outputs);
        Assert.All(outputs, output => Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, output.Reason.ToString()));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
