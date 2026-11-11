using System.Collections.Concurrent;
using System.Reflection;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;

namespace JsonCall.Client;

/// <summary>Creates typed clients from contract interfaces.</summary>
public static class JsonRpcProxy
{
    public static T Create<T>(JsonRpcPeer peer) where T : class
    {
        if (!typeof(T).IsInterface)
            throw new ArgumentException("A proxy can only be created for an interface.", nameof(T));

        var proxy = DispatchProxy.Create<T, ProxyImplementation>();
        ((ProxyImplementation)(object)proxy).Initialize(peer, ContractReflection.GetParamStyle(typeof(T)));
        return proxy;
    }

    public class ProxyImplementation : DispatchProxy
    {
        private static readonly ConcurrentDictionary<MethodInfo, ContractMethod> Cache = new();

        private JsonRpcPeer _peer = null!;
        private JsonRpcParamStyle _style;

        internal void Initialize(JsonRpcPeer peer, JsonRpcParamStyle style)
        {
            _peer = peer;
            _style = style;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                throw new InvalidOperationException();

            var method = Cache.GetOrAdd(targetMethod, ContractReflection.Describe);
            args ??= Array.Empty<object?>();

            var cancellation = CancellationToken.None;
            var values = new List<object?>();
            var names = new List<string>();
            var declared = targetMethod.GetParameters();

            for (var i = 0; i < declared.Length; i++)
            {
                if (declared[i].ParameterType == typeof(CancellationToken))
                {
                    cancellation = (CancellationToken)args[i]!;
                    continue;
                }

                values.Add(args[i]);
                names.Add(declared[i].Name!);
            }

            var options = _peer.SerializerOptions;

            var @params = _style == JsonRpcParamStyle.ByName
                ? JsonRpcParams.ByName(options, names.Select((n, i) => new KeyValuePair<string, object?>(n, values[i])))
                : JsonRpcParams.ByPosition(options, values);

            if (method.Kind == ResultKind.Stream)
            {
                return typeof(ProxyImplementation).GetMethod(nameof(Stream), BindingFlags.NonPublic | BindingFlags.Instance)!
                    .MakeGenericMethod(method.ResultType!)
                    .Invoke(this, new object?[] { method.Name, @params, cancellation });
            }

            if (method.IsNotification)
            {
                var notification = _peer.NotifyAsync(method.Name, @params, cancellation).AsTask();

                return Adapt(notification, method);
            }

            if (method.Kind == ResultKind.Void)
                return Adapt(CallVoid(method.Name, @params, cancellation), method);

            var task = typeof(ProxyImplementation).GetMethod(nameof(CallValue), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(method.ResultType!)
                .Invoke(this, new object?[] { method.Name, @params, cancellation })!;

            return Adapt(task, method);
        }

        private async Task CallVoid(string name, JsonRpcParams @params, CancellationToken ct) =>
            await _peer.InvokeAsync(name, @params, null, ct).ConfigureAwait(false);

        private async Task<T> CallValue<T>(string name, JsonRpcParams @params, CancellationToken ct) =>
            (await _peer.InvokeAsync<T>(name, @params, null, ct).ConfigureAwait(false))!;

        private IAsyncEnumerable<T> Stream<T>(string name, JsonRpcParams @params, CancellationToken ct) =>
            _peer.StreamAsync<T>(name, @params, ct);

        /// <summary>Converts a Task to the shape of the declared return type.</summary>
        private static object? Adapt(object task, ContractMethod method)
        {
            var returnType = method.Method.ReturnType;

            if (returnType == typeof(void))
            {
                ((Task)task).GetAwaiter().GetResult();
                return null;
            }

            if (returnType == typeof(Task) || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)))
                return task;

            if (returnType == typeof(ValueTask))
                return new ValueTask((Task)task);

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                return Activator.CreateInstance(returnType, task);

            // synchronous return type: block
            var awaited = (Task)task;
            awaited.GetAwaiter().GetResult();
            return awaited.GetType().GetProperty("Result")?.GetValue(awaited);
        }
    }
}
