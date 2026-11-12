using System.Collections.Concurrent;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;

namespace JsonCall.Server;

/// <summary>
/// Tracks the peers created by <see cref="JsonRpcServer.Attach"/> so a daemon can push notifications
/// (an event stream) to every connected client. Peers leave the set when their connection ends.
/// </summary>
public sealed class JsonRpcConnectionSet : IAsyncDisposable
{
    private readonly ConcurrentDictionary<JsonRpcPeer, byte> _peers = new();

    /// <summary>Number of live connections.</summary>
    public int Count => _peers.Count;

    /// <summary>The live connections (a snapshot).</summary>
    public IReadOnlyCollection<JsonRpcPeer> Peers => _peers.Keys.ToArray();

    /// <summary>Raised after a peer is added.</summary>
    public event Action<JsonRpcPeer>? Connected;

    /// <summary>Raised after a peer's connection ends and it has been removed.</summary>
    public event Action<JsonRpcPeer>? Disconnected;

    /// <summary>Serves <paramref name="transport"/> on <paramref name="server"/> and tracks the resulting peer.</summary>
    public JsonRpcPeer Attach(JsonRpcServer server, IMessageTransport transport, IMessageSerializer? serializer = null,
        Action<JsonRpcPeerOptions>? configure = null) =>
        Add(server.Attach(transport, serializer, configure));

    /// <summary>Tracks a peer created elsewhere. It is removed when its <see cref="JsonRpcPeer.Completion"/> finishes.</summary>
    public JsonRpcPeer Add(JsonRpcPeer peer)
    {
        if (!_peers.TryAdd(peer, 0))
            return peer;

        _ = peer.Completion.ContinueWith(completed =>
        {
            if (_peers.TryRemove(peer, out _))
                Disconnected?.Invoke(peer);
        }, TaskScheduler.Default);

        Connected?.Invoke(peer);
        return peer;
    }

    /// <summary>
    /// Sends a notification to every connection (or those accepted by <paramref name="filter"/>). A peer that
    /// fails to send is dropped from the set. Returns the number of peers that received it.
    /// </summary>
    public async ValueTask<int> BroadcastAsync(string method, JsonRpcParams? @params = null,
        Func<JsonRpcPeer, bool>? filter = null, CancellationToken cancellationToken = default)
    {
        var targets = _peers.Keys.Where(p => filter?.Invoke(p) ?? true).ToArray();

        var results = await Task.WhenAll(targets.Select(async peer =>
        {
            try
            {
                await peer.NotifyAsync(method, @params, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                if (_peers.TryRemove(peer, out _))
                    Disconnected?.Invoke(peer);

                return false;
            }
        })).ConfigureAwait(false);

        return results.Count(sent => sent);
    }

    /// <summary>Disposes every tracked peer and empties the set.</summary>
    public async ValueTask DisposeAsync()
    {
        var peers = _peers.Keys.ToArray();
        _peers.Clear();

        foreach (var peer in peers)
            await peer.DisposeAsync().ConfigureAwait(false);
    }
}
