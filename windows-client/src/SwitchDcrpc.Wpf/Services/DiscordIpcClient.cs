using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwitchDcrpc.Wpf.Models;

namespace SwitchDcrpc.Wpf.Services;

public sealed class DiscordIpcClient : IAsyncDisposable
{
    private const int OpcodeHandshake = 0;
    private const int OpcodeFrame = 1;
    private const int OpcodeClose = 2;
    private const int OpcodePing = 3;
    private const int OpcodePong = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _appId;
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;

    public DiscordIpcClient(string appId)
    {
        _appId = appId;
    }

    public bool IsConnected => _pipe is { IsConnected: true };

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return;
        }

        DisconnectInternal();

        for (var i = 0; i <= 9; i++)
        {
            var candidate = new NamedPipeClientStream(
                ".",
                $"discord-ipc-{i}",
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(500);
                await candidate.ConnectAsync(timeout.Token);
                _pipe = candidate;

                await SendHandshakeAsync(cancellationToken);
                StartReadLoop();
                return;
            }
            catch
            {
                candidate.Dispose();
                _pipe = null;
            }
        }
    }

    public async Task SetActivityAsync(ActivityPayload activity, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return;
        }

        object? assets = null;
        if (!string.IsNullOrWhiteSpace(activity.LargeImage) ||
            !string.IsNullOrWhiteSpace(activity.LargeText) ||
            !string.IsNullOrWhiteSpace(activity.SmallImage) ||
            !string.IsNullOrWhiteSpace(activity.SmallText))
        {
            assets = new
            {
                large_image = string.IsNullOrWhiteSpace(activity.LargeImage) ? null : activity.LargeImage,
                large_text = string.IsNullOrWhiteSpace(activity.LargeText) ? null : activity.LargeText,
                small_image = string.IsNullOrWhiteSpace(activity.SmallImage) ? null : activity.SmallImage,
                small_text = string.IsNullOrWhiteSpace(activity.SmallText) ? null : activity.SmallText
            };
        }

        object[]? buttons = null;
        if (!string.IsNullOrWhiteSpace(activity.Button1Label) &&
            !string.IsNullOrWhiteSpace(activity.Button1Url))
        {
            buttons =
            [
                new
                {
                    label = activity.Button1Label,
                    url = activity.Button1Url
                }
            ];
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                cmd = "SET_ACTIVITY",
                args = new
                {
                    pid = Environment.ProcessId,
                    activity = new
                    {
                        name = string.IsNullOrWhiteSpace(activity.Name) ? null : activity.Name,
                        details = activity.Details,
                        state = activity.State,
                        timestamps = new { start = activity.StartUnix },
                        assets,
                        buttons
                    }
                },
                nonce = Guid.NewGuid().ToString("N")
            },
            JsonOptions
        );

        await WriteFrameSafeAsync(OpcodeFrame, payload, cancellationToken);
    }

    public async Task ClearActivityAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                cmd = "SET_ACTIVITY",
                args = new
                {
                    pid = Environment.ProcessId,
                    activity = (object?)null
                },
                nonce = Guid.NewGuid().ToString("N")
            },
            JsonOptions
        );

        await WriteFrameSafeAsync(OpcodeFrame, payload, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _readLoopCts?.Cancel();
            if (_readLoopTask is not null)
            {
                await _readLoopTask;
            }
        }
        catch
        {
            // Ignore shutdown errors.
        }
        finally
        {
            DisconnectInternal();
            _writeGate.Dispose();
        }
    }

    private async Task SendHandshakeAsync(CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { v = 1, client_id = _appId }, JsonOptions);
        await WriteFrameAsync(OpcodeHandshake, payload, cancellationToken);
    }

    private void StartReadLoop()
    {
        _readLoopCts = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var header = new byte[8];

        try
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                if (!await ReadExactAsync(header, cancellationToken))
                {
                    break;
                }

                var opcode = BitConverter.ToInt32(header, 0);
                var length = BitConverter.ToInt32(header, 4);
                if (length < 0 || length > 1024 * 1024)
                {
                    break;
                }

                var payload = new byte[length];
                if (!await ReadExactAsync(payload, cancellationToken))
                {
                    break;
                }

                if (opcode == OpcodePing)
                {
                    var pingBody = Encoding.UTF8.GetString(payload);
                    await WriteFrameSafeAsync(OpcodePong, pingBody, cancellationToken);
                }
                else if (opcode == OpcodeClose)
                {
                    break;
                }
            }
        }
        catch
        {
            // Pipe disconnected.
        }
        finally
        {
            DisconnectInternal();
        }
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        if (_pipe is null || !_pipe.IsConnected)
        {
            return false;
        }

        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _pipe.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private async Task WriteFrameSafeAsync(int opcode, string json, CancellationToken cancellationToken)
    {
        try
        {
            await WriteFrameAsync(opcode, json, cancellationToken);
        }
        catch
        {
            DisconnectInternal();
        }
    }

    private async Task WriteFrameAsync(int opcode, string json, CancellationToken cancellationToken)
    {
        if (_pipe is null || !_pipe.IsConnected)
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BitConverter.GetBytes(opcode).CopyTo(header, 0);
        BitConverter.GetBytes(payload.Length).CopyTo(header, 4);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _pipe.WriteAsync(header.AsMemory(0, header.Length), cancellationToken);
            await _pipe.WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken);
            await _pipe.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void DisconnectInternal()
    {
        try
        {
            _readLoopCts?.Cancel();
        }
        catch
        {
            // Ignore cancellation errors.
        }

        _readLoopCts?.Dispose();
        _readLoopCts = null;
        _readLoopTask = null;

        if (_pipe is not null)
        {
            try
            {
                _pipe.Dispose();
            }
            catch
            {
                // Ignore disposal errors.
            }

            _pipe = null;
        }
    }
}
