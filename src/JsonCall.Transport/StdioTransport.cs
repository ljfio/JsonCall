using System.Diagnostics;
using JsonCall.Core;

namespace JsonCall.Transport;

public static class StdioMessageTransport
{
    /// <summary>Uses this process's standard input and output (for a server launched by a client).</summary>
    public static IMessageTransport FromConsole() =>
        new StreamMessageTransport(Console.OpenStandardInput(), Console.OpenStandardOutput());

    /// <summary>Launches a child process and talks to it over its standard input and output.</summary>
    public static StdioProcessTransport Launch(ProcessStartInfo startInfo) => new(startInfo);
}

/// <summary>A child process speaking JSON-RPC on stdio. Standard error is exposed on <see cref="Diagnostics"/>.</summary>
public sealed class StdioProcessTransport : IMessageTransport
{
    private readonly Process _process;
    private readonly StreamMessageTransport _inner;

    internal StdioProcessTransport(ProcessStartInfo startInfo)
    {
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                DiagnosticsReceived?.Invoke(e.Data);
        };

        _process.Start();
        _process.BeginErrorReadLine();

        _inner = new StreamMessageTransport(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
    }

    /// <summary>Raised for each line the child writes to standard error.</summary>
    public event Action<string>? DiagnosticsReceived;

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
        _inner.SendAsync(message, cancellationToken);

    public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _inner.ReceiveAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // already exited
        }

        _process.Dispose();
    }
}
