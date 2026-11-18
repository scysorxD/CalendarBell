using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CalendarBell.Infrastructure.Windows;

/// <summary>A command forwarded from a secondary process: open-settings, open-upcoming, notification-action, exit.</summary>
public sealed record InstanceMessage(string Command, string? Arguments, Dictionary<string, string>? Inputs);

/// <summary>
/// One resident instance per user (spec §7.4): a named mutex scoped by the user SID (and data folder), plus a
/// named pipe restricted to the same user through which secondary processes forward their request and exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int MaxMessageBytes = 64 * 1024;

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listener;
    private bool _ownsMutex;

    private SingleInstance(Mutex mutex, bool ownsMutex, string pipeName)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _pipeName = pipeName;
    }

    public event EventHandler<InstanceMessage>? MessageReceived;

    public bool IsPrimary => _ownsMutex;

    /// <param name="scope">Distinguishes independent instances (e.g. a test data folder); combined with the user SID.</param>
    public static SingleInstance Create(string scope)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid + "|" + scope.ToUpperInvariant())).AsSpan(0, 8));
        var mutex = new Mutex(initiallyOwned: true, @"Local\CalendarBell." + hash, out var createdNew);
        var owns = createdNew;
        if (!owns)
        {
            try
            {
                owns = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owns = true; // the previous primary crashed
            }
        }

        return new SingleInstance(mutex, owns, "CalendarBell." + hash);
    }

    public void StartListening()
    {
        if (!_ownsMutex)
        {
            throw new InvalidOperationException("Only the primary instance listens.");
        }

        _listener ??= Task.Run(() => ListenAsync(_cts.Token));
    }

    public async Task<bool> SendAsync(InstanceMessage message, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            if (bytes.Length > MaxMessageBytes)
            {
                return false;
            }

            await client.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
            await client.FlushAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // released from a different thread at shutdown; the handle close below frees it anyway
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
        _cts.Dispose();
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = NamedPipeServerStreamAcl.Create(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while ((read = await server.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0 && buffer.Length <= MaxMessageBytes)
                {
                    buffer.Write(chunk, 0, read);
                }

                if (buffer.Length is > 0 and <= MaxMessageBytes)
                {
                    var message = JsonSerializer.Deserialize<InstanceMessage>(buffer.ToArray());
                    if (message is not null && !string.IsNullOrWhiteSpace(message.Command))
                    {
                        MessageReceived?.Invoke(this, message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // malformed or interrupted message: ignore and keep listening
            }
        }
    }
}
