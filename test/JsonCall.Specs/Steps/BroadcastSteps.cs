using System.Collections.Concurrent;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;
using JsonCall.Server;
using JsonCall.Specs.Support;
using Reqnroll;

namespace JsonCall.Specs.Steps;

[Binding]
public sealed class BroadcastSteps
{
    private readonly World _world;
    private readonly List<JsonRpcPeer> _clients = new();
    private readonly List<ConcurrentQueue<int>> _received = new();
    private readonly List<JsonRpcPeer> _serverPeers = new();
    private JsonRpcConnectionSet _set = null!;

    public BroadcastSteps(World world) => _world = world;

    [Given(@"^a connection set with (\d+) attached peers$")]
    public void GivenSet(int count)
    {
        var server = Connections.CreateServer(_world.State);
        _set = new JsonRpcConnectionSet();
        _world.OnCleanup(_set.DisposeAsync);

        for (var i = 0; i < count; i++)
        {
            var (clientSide, serverSide) = InMemoryTransport.CreatePair();
            var queue = new ConcurrentQueue<int>();
            var client = new JsonRpcPeer(clientSide);
            client.OnNotification("tick", e => queue.Enqueue(e is { } v ? v[0].GetInt32() : -1));
            client.Start();
            _world.OnCleanup(client.DisposeAsync);

            _clients.Add(client);
            _received.Add(queue);
            _serverPeers.Add(_set.Attach(server, serverSide));
        }

        Assert.Equal(count, _set.Count);
    }

    [When(@"^the server broadcasts ""(.*)"" with value (\d+)$")]
    public async Task WhenBroadcast(string method, int value) =>
        _world.Values["sent"] = await _set.BroadcastAsync(method, JsonRpcParams.ByPosition(value));

    [When(@"^the server broadcasts ""(.*)"" with value (\d+) to the first peer only$")]
    public async Task WhenBroadcastFirst(string method, int value)
    {
        var first = _serverPeers[0];
        _world.Values["sent"] = await _set.BroadcastAsync(method, JsonRpcParams.ByPosition(value), p => p == first);
    }

    [When(@"^(\d+) peer disconnects$")]
    public async Task WhenDisconnect(int count)
    {
        for (var i = 0; i < count; i++)
            await _clients[i].DisposeAsync();
    }

    [Then(@"^every peer receives ""(.*)"" with value (\d+)$")]
    public async Task ThenEveryReceives(string method, int value)
    {
        await Wait.UntilAsync(() => _received.All(q => q.Count == 1));
        Assert.All(_received, q => Assert.Equal(value, q.Single()));
        Assert.Equal(_received.Count, (int)_world.Values["sent"]!);
    }

    [Then(@"^the connection set has (\d+) peers$")]
    public async Task ThenCount(int count) => await Wait.UntilAsync(() => _set.Count == count);

    [Then(@"^only the first peer receives ""(.*)""$")]
    public async Task ThenOnlyFirst(string method)
    {
        await Wait.UntilAsync(() => _received[0].Count == 1);
        await Task.Delay(100);
        Assert.All(_received.Skip(1), q => Assert.Empty(q));
        Assert.Equal(1, (int)_world.Values["sent"]!);
    }
}
