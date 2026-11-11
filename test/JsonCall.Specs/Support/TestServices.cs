using System.Runtime.CompilerServices;
using System.Text.Json;
using JsonCall.Core;
using JsonCall.Core.Model;
using Microsoft.AspNetCore.Authorization;

namespace JsonCall.Specs.Support;

public sealed record Tick(int Index);

public sealed record Person(string Name, int Age);

[JsonRpcContract]
public interface IMath
{
    Task<int> AddAsync(int a, int b, CancellationToken ct = default);

    [JsonRpcMethod("sub")]
    Task<int> SubtractAsync(int minuend, int subtrahend);

    [JsonRpcNotification]
    Task LogAsync(string message);
}

[JsonRpcContract]
public interface IGreeter
{
    Task<string> GreetAsync(string name, string greeting = "Hello");
}

[JsonRpcContract]
public interface ITestService
{
    JsonElement Echo(JsonElement value);

    [JsonRpcMethod("delay_echo")]
    Task<string> DelayEchoAsync(string value, CancellationToken ct);

    [JsonRpcMethod("fail")]
    Task<int> FailAsync();

    [JsonRpcMethod("slow")]
    Task<int> SlowAsync(CancellationToken ct);

    [JsonRpcMethod("notify_me")]
    void NotifyMe(string what);
}

[JsonRpcContract]
public interface IEvents
{
    IAsyncEnumerable<Tick> TicksAsync(int count, int delayMs, CancellationToken ct);

    IAsyncEnumerable<Tick> FailAfterAsync(int items, CancellationToken ct);

    IAsyncEnumerable<Tick> SubscribeAsync(string topic, CancellationToken ct);

    IAsyncEnumerable<Tick> EndlessAsync(CancellationToken ct);

    [Authorize(Roles = "reader")]
    IAsyncEnumerable<Tick> SecureTicksAsync(int count, CancellationToken ct);
}

[JsonRpcContract]
public interface IProtocolService
{
    [JsonRpcMethod("subtract")]
    int Subtract(int minuend, int subtrahend);

    [JsonRpcMethod("sum")]
    int Sum(params int[] values);

    [JsonRpcMethod("get_data")]
    object[] GetData();

    [JsonRpcMethod("update")]
    void Update(params int[] values);

    [JsonRpcMethod("notify_hello")]
    void NotifyHello(int value);
}

[JsonRpcContract]
public interface IAdmin
{
    [JsonRpcMethod("admin_reset")]
    [Authorize(Roles = "admin")]
    Task<string> ResetAsync();

    [JsonRpcMethod("whoami")]
    Task<string> WhoAmIAsync();
}

/// <summary>Shared, observable state behind the test contracts.</summary>
public sealed class TestState
{
    public TimeSpan DefaultDelay { get; set; } = TimeSpan.Zero;

    public Dictionary<string, TimeSpan> Delays { get; } = new();

    public TaskCompletionSource ReleaseGate { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource DelayCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int FailCode { get; set; } = 1001;

    public int? AddErrorCode { get; set; }

    public List<string> Logs { get; } = new();

    public List<string> Notified { get; } = new();

    // streams
    public int Yielded;
    public int EndlessDisposed;
    public int EventsDisposed;
    public readonly TaskCompletionSource EndlessCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource FirstItemSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly List<string> Subscriptions = new();
    public int Started;

    // protocol
    public Action? SumBehavior { get; set; }
    public List<int[]> Updates { get; } = new();
    public List<int> Hellos { get; } = new();
}

public sealed class TestService : ITestService, IMath, IGreeter, IEvents, IProtocolService
{
    private readonly TestState _state;

    public TestService(TestState state) => _state = state;

    public JsonElement Echo(JsonElement value) => value;

    public async Task<string> DelayEchoAsync(string value, CancellationToken ct)
    {
        var delay = _state.Delays.TryGetValue(value, out var d) ? d : _state.DefaultDelay;

        try
        {
            if (delay > TimeSpan.Zero)
                await Task.WhenAny(Task.Delay(delay, ct), _state.ReleaseGate.Task).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            _state.DelayCancelled.TrySetResult();
            throw;
        }

        return value;
    }

    public Task<int> FailAsync() =>
        throw new JsonRpcApplicationException(_state.FailCode, "Quota exceeded", JsonDocument.Parse("{\"limit\":5}").RootElement);

    public async Task<int> SlowAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return 0;
    }

    public void NotifyMe(string what)
    {
        lock (_state.Notified)
            _state.Notified.Add(what);
    }

    // IMath
    public Task<int> AddAsync(int a, int b, CancellationToken ct = default) =>
        _state.AddErrorCode is { } code
            ? throw new JsonRpcApplicationException(code, "Add failed")
            : Task.FromResult(a + b);

    public Task<int> SubtractAsync(int minuend, int subtrahend) => Task.FromResult(minuend - subtrahend);

    public Task LogAsync(string message)
    {
        lock (_state.Logs)
            _state.Logs.Add(message);

        return Task.CompletedTask;
    }

    // IGreeter
    public Task<string> GreetAsync(string name, string greeting = "Hello") => Task.FromResult($"{greeting} {name}");

    // IEvents
    public async IAsyncEnumerable<Tick> TicksAsync(int count, int delayMs, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _state.Yielded);
            yield return new Tick(i);

            if (delayMs > 0)
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<Tick> FailAfterAsync(int items, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < items; i++)
            yield return new Tick(i);

        await Task.Yield();
        throw new InvalidOperationException("boom: secret details");
    }

    public async IAsyncEnumerable<Tick> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct)
    {
        lock (_state.Subscriptions)
            _state.Subscriptions.Add(topic);

        try
        {
            var i = 0;

            while (true)
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
                yield return new Tick(i++);
            }
        }
        finally
        {
            lock (_state.Subscriptions)
                _state.Subscriptions.Remove(topic);

            Interlocked.Increment(ref _state.EventsDisposed);
        }
    }

    public async IAsyncEnumerable<Tick> EndlessAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref _state.Started);
        ct.Register(() => _state.EndlessCancelled.TrySetResult());

        try
        {
            var i = 0;

            while (true)
            {
                await Task.Delay(5, ct).ConfigureAwait(false);
                yield return new Tick(i++);
            }
        }
        finally
        {
            Interlocked.Increment(ref _state.EndlessDisposed);
        }
    }

    public async IAsyncEnumerable<Tick> SecureTicksAsync(int count, [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref _state.Started);

        for (var i = 0; i < count; i++)
        {
            yield return new Tick(i);
            await Task.Yield();
        }
    }

    // IProtocolService
    public int Subtract(int minuend, int subtrahend) => minuend - subtrahend;

    public int Sum(params int[] values)
    {
        _state.SumBehavior?.Invoke();
        return values.Sum();
    }

    public object[] GetData() => new object[] { "hello", 5 };

    public void Update(params int[] values)
    {
        lock (_state.Updates)
            _state.Updates.Add(values);
    }

    public void NotifyHello(int value)
    {
        lock (_state.Hellos)
            _state.Hellos.Add(value);
    }
}
