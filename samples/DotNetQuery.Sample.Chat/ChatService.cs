using System.Runtime.CompilerServices;

namespace DotNetQuery.Sample.Chat;

public sealed record Message(int Id, string User, string Text);

public sealed record MessagePage(int Offset, IReadOnlyList<Message> Messages, bool HasMore);

/// <summary>
/// A stand-in chat backend: a fixed block of history served in pages (newest first) plus a bounded live
/// message stream. In a real app the history pages would be an HTTP/GraphQL call and the live stream a
/// SignalR <c>HubConnection.StreamAsync&lt;Message&gt;</c>.
/// </summary>
public sealed class ChatService
{
    private const int PageSize = 10;
    private static readonly string[] Users = ["alice", "bob", "carol"];

    private readonly List<Message> _history =
        [.. Enumerable.Range(0, 50).Select(i => new Message(i, Users[i % Users.Length], $"history message {i}"))];

    /// <summary>Returns a page of history, newest first, starting <paramref name="offset"/> back from the end.</summary>
    public Task<MessagePage> GetHistoryPageAsync(int offset, CancellationToken cancellationToken)
    {
        var page = _history.AsEnumerable().Reverse().Skip(offset).Take(PageSize).ToList();
        var hasMore = offset + PageSize < _history.Count;
        return Task.FromResult(new MessagePage(offset, page, hasMore));
    }

    /// <summary>Emits <paramref name="count"/> live messages, one every ~200 ms, then completes.</summary>
    public async IAsyncEnumerable<Message> LiveMessagesAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Delay(200, cancellationToken);
            yield return new Message(1000 + i, Users[i % Users.Length], $"live message {i + 1}");
        }
    }
}
