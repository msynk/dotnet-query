using DotNetQuery;
using DotNetQuery.Tests.Generated;
using Xunit;

[assembly: GenerateQueries(typeof(IPetStoreClient), KeyPrefix = "petstore")]

// Stand-ins for Refit's HTTP verb attributes: the generator recognizes them by namespace and name.
namespace Refit
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class GetAttribute(string path) : Attribute
    {
        public string Path { get; } = path;
    }

    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class DeleteAttribute(string path) : Attribute
    {
        public string Path { get; } = path;
    }
}

namespace DotNetQuery.Tests.Generated
{
    public sealed record Todo(int Id, string Title, bool Done);

    public enum TodoFilter
    {
        All,
        Open,
        Done,
    }

    [GenerateQueries]
    public interface ITodoApi
    {
        Task<List<Todo>> GetTodosAsync(TodoFilter filter = TodoFilter.Open, CancellationToken cancellationToken = default);

        ValueTask<Todo> FindTodoAsync(int id, CancellationToken cancellationToken);

        [QueryMethod(Name = "Count")]
        Task<int> TotalAsync(string @class);

        [MutationMethod(Invalidates = [nameof(GetTodosAsync), "Count"])]
        Task<Todo> AddTodoAsync(string title, CancellationToken cancellationToken);

        Task<Todo> RenameTodoAsync(int id, string title, CancellationToken cancellationToken);

        [MutationMethod(Invalidates = [])]
        Task TouchAsync(int id);

        [Refit.Get("/todos/search")]
        Task<List<Todo>> SearchByTitleAsync(string term);

        [Refit.Delete("/todos/{id}")]
        ValueTask RemoveTodoAsync(int id);

        [QueryIgnore]
        Task<string> GetDebugInfoAsync();

        string BaseUrl { get; }
    }

    // Shaped like an NSwag client interface from an OpenAPI document: every operation with and without a token.
    public interface IPetStoreClient
    {
        Task<string> GetPetByIdAsync(long petId);

        Task<string> GetPetByIdAsync(long petId, CancellationToken cancellationToken);

        Task<string> AddPetAsync(string name);

        Task<string> AddPetAsync(string name, CancellationToken cancellationToken);
    }
}

namespace DotNetQuery.Tests
{
    public class GeneratedQueriesTests
    {
        private sealed class FakeTodoApi : ITodoApi
        {
            public List<Todo> Todos { get; } = [new(1, "Learn", false), new(2, "Ship", true)];

            public List<string> Calls { get; } = [];

            public string BaseUrl => "https://api.test";

            public Task<List<Todo>> GetTodosAsync(TodoFilter filter = TodoFilter.Open, CancellationToken cancellationToken = default)
            {
                Calls.Add($"GetTodos:{filter}");
                return Task.FromResult(Todos.Where(t => filter == TodoFilter.All || t.Done == (filter == TodoFilter.Done)).ToList());
            }

            public ValueTask<Todo> FindTodoAsync(int id, CancellationToken cancellationToken)
            {
                Calls.Add($"FindTodo:{id}");
                return ValueTask.FromResult(Todos.Single(t => t.Id == id));
            }

            public Task<int> TotalAsync(string @class) => Task.FromResult(Todos.Count);

            public Task<Todo> AddTodoAsync(string title, CancellationToken cancellationToken)
            {
                var todo = new Todo(Todos.Count + 1, title, false);
                Todos.Add(todo);
                return Task.FromResult(todo);
            }

            public Task<Todo> RenameTodoAsync(int id, string title, CancellationToken cancellationToken)
            {
                var index = Todos.FindIndex(t => t.Id == id);
                Todos[index] = Todos[index] with { Title = title };
                return Task.FromResult(Todos[index]);
            }

            public Task TouchAsync(int id)
            {
                Calls.Add($"Touch:{id}");
                return Task.CompletedTask;
            }

            public Task<List<Todo>> SearchByTitleAsync(string term) => Task.FromResult(Todos.Where(t => t.Title.Contains(term, StringComparison.Ordinal)).ToList());

            public ValueTask RemoveTodoAsync(int id)
            {
                Todos.RemoveAll(t => t.Id == id);
                return ValueTask.CompletedTask;
            }

            public Task<string> GetDebugInfoAsync() => Task.FromResult("debug");
        }

        private sealed class FakePetStore : IPetStoreClient
        {
            public int TokenCalls { get; private set; }

            public Task<string> GetPetByIdAsync(long petId) => Task.FromResult("no token");

            public Task<string> GetPetByIdAsync(long petId, CancellationToken cancellationToken)
            {
                TokenCalls++;
                return Task.FromResult($"pet {petId}");
            }

            public Task<string> AddPetAsync(string name) => Task.FromResult("no token");

            public Task<string> AddPetAsync(string name, CancellationToken cancellationToken)
            {
                TokenCalls++;
                return Task.FromResult(name);
            }
        }

        [Fact]
        public void Keys_are_hierarchical_under_the_api_prefix()
        {
            Assert.Equal(QueryKey.Create("TodoApi"), TodoApiQueries.All);
            Assert.Equal(QueryKey.Create("TodoApi", "GetTodos"), TodoApiQueries.Keys.GetTodosPrefix);
            Assert.Equal(QueryKey.Create("TodoApi", "GetTodos", TodoFilter.Done), TodoApiQueries.Keys.GetTodos(TodoFilter.Done));
            Assert.Equal(QueryKey.Create("TodoApi", "GetTodos", TodoFilter.Open), TodoApiQueries.Keys.GetTodos()); // default value kept
            Assert.Equal(QueryKey.Create("TodoApi", "FindTodo", 7), TodoApiQueries.Keys.FindTodo(7));
            Assert.Equal(QueryKey.Create("TodoApi", "Count", "x"), TodoApiQueries.Keys.Count("x")); // [QueryMethod(Name)]
            Assert.Equal(QueryKey.Create("TodoApi", "SearchByTitle", "a"), TodoApiQueries.Keys.SearchByTitle("a")); // Refit [Get]
        }

        [Fact]
        public async Task Query_definitions_fetch_through_the_api_and_cache_by_key()
        {
            using var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5) });
            var api = new FakeTodoApi();

            var open = await client.FetchAsync(TodoApiQueries.GetTodos(api));
            var done = await client.FetchAsync(TodoApiQueries.GetTodos(api, TodoFilter.Done));
            var todo = await client.FetchAsync(TodoApiQueries.FindTodo(api, 2)); // ValueTask<T> method
            await client.FetchAsync(TodoApiQueries.GetTodos(api)); // cached

            Assert.Equal("Learn", Assert.Single(open.Data!).Title);
            Assert.Equal("Ship", Assert.Single(done.Data!).Title);
            Assert.Equal("Ship", todo.Data!.Title);
            Assert.Equal(["GetTodos:Open", "GetTodos:Done", "FindTodo:2"], api.Calls);
            Assert.NotNull(client.Cache.Describe(TodoApiQueries.Keys.GetTodos(TodoFilter.Done)));
        }

        [Fact]
        public async Task Query_definitions_carry_options()
        {
            using var client = new QueryClient();
            var definition = TodoApiQueries.FindTodo(new FakeTodoApi(), 1, new QueryOptions { StaleTime = TimeSpan.FromHours(1) });

            await client.FetchAsync(definition);

            Assert.Equal(TimeSpan.FromHours(1), definition.Options!.StaleTime);
            Assert.False(client.Cache.Describe(definition.Key)!.IsStale);
        }

        [Fact]
        public async Task Mutations_invalidate_the_configured_queries()
        {
            using var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5) });
            var api = new FakeTodoApi();
            await client.FetchAsync(TodoApiQueries.GetTodos(api, TodoFilter.All));
            await client.FetchAsync(TodoApiQueries.Count(api, "x"));
            await client.FetchAsync(TodoApiQueries.FindTodo(api, 1));

            var add = TodoApiQueries.AddTodo(client, api);
            var result = await add.MutateAsync("Milk");

            Assert.Equal("Milk", result.Data!.Title);
            Assert.Equal(QueryKey.Create("TodoApi", "AddTodo"), add.Options.MutationKey);
            Assert.True(client.Cache.Describe(TodoApiQueries.Keys.GetTodos(TodoFilter.All))!.IsStale); // invalidated
            Assert.True(client.Cache.Describe(TodoApiQueries.Keys.Count("x"))!.IsStale); // invalidated
            Assert.False(client.Cache.Describe(TodoApiQueries.Keys.FindTodo(1))!.IsStale); // not listed
        }

        [Fact]
        public async Task Multi_parameter_mutations_take_a_generated_variables_record_and_invalidate_the_whole_api_by_default()
        {
            using var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5) });
            var api = new FakeTodoApi();
            await client.FetchAsync(TodoApiQueries.FindTodo(api, 1));

            var rename = TodoApiQueries.RenameTodo(client, api);
            var result = await rename.MutateAsync(new TodoApiQueries.RenameTodoVariables(1, "Learn more"));

            Assert.Equal("Learn more", result.Data!.Title);
            Assert.True(client.Cache.Describe(TodoApiQueries.Keys.FindTodo(1))!.IsStale);
        }

        [Fact]
        public async Task Mutations_without_a_result_and_options_overrides_work()
        {
            using var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5) });
            var api = new FakeTodoApi();
            await client.FetchAsync(TodoApiQueries.FindTodo(api, 1));

            var touch = TodoApiQueries.Touch(client, api); // Invalidates = [] - nothing
            var touched = await touch.MutateAsync(1);
            var remove = TodoApiQueries.RemoveTodo(client, api, new() { Invalidates = [TodoApiQueries.Keys.FindTodoPrefix] }); // ValueTask, Refit [Delete]
            await remove.MutateAsync(2);

            Assert.True(touched.Data);
            Assert.Contains("Touch:1", api.Calls);
            Assert.DoesNotContain(api.Todos, t => t.Id == 2);
            Assert.True(client.Cache.Describe(TodoApiQueries.Keys.FindTodo(1))!.IsStale);
            Assert.Empty(TodoApiQueries.TouchOptions().Invalidates!);
        }

        [Fact]
        public void Mutation_functions_compose_with_CreateMutation()
        {
            using var client = new QueryClient();
            var api = new FakeTodoApi();

            var mutation = client.CreateMutation(TodoApiQueries.AddTodoFn(api), TodoApiQueries.AddTodoOptions(new() { ThrowOnError = true }));

            Assert.True(mutation.Options.ThrowOnError);
            Assert.Equal(2, mutation.Options.Invalidates!.Count);
        }

        [Fact]
        public async Task Assembly_level_attribute_generates_for_interfaces_that_cannot_be_annotated_and_prefers_token_overloads()
        {
            using var client = new QueryClient();
            var api = new FakePetStore();

            var pet = await client.FetchAsync(PetStoreClientQueries.GetPetById(api, 7));
            var added = await PetStoreClientQueries.AddPet(client, api).MutateAsync("Rex");

            Assert.Equal("pet 7", pet.Data);
            Assert.Equal("Rex", added.Data);
            Assert.Equal(2, api.TokenCalls);
            Assert.Equal(QueryKey.Create("petstore", "GetPetById", 7L), PetStoreClientQueries.Keys.GetPetById(7));
        }
    }
}
