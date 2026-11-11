using Reqnroll;

namespace JsonCall.Specs.Support;

/// <summary>Per-scenario state shared by step classes (injected by Reqnroll).</summary>
public sealed class World
{
    private readonly List<Func<ValueTask>> _cleanup = new();

    public TestState State { get; } = new();

    public Connection? Connection { get; set; }

    public RawPeer? Raw { get; set; }

    public Exception? Error { get; set; }

    public object? Result { get; set; }

    public Dictionary<string, object?> Values { get; } = new();

    public void OnCleanup(Func<ValueTask> cleanup) => _cleanup.Add(cleanup);

    public async Task CleanupAsync()
    {
        if (Connection is not null)
            await Connection.DisposeAsync();

        if (Raw is not null)
            await Raw.DisposeAsync();

        foreach (var cleanup in Enumerable.Reverse(_cleanup))
        {
            try
            {
                await cleanup();
            }
            catch
            {
            }
        }
    }
}

[Binding]
public sealed class Hooks
{
    private readonly World _world;

    public Hooks(World world) => _world = world;

    [AfterScenario]
    public Task AfterScenario() => _world.CleanupAsync();
}
