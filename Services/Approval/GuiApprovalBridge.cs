// Licensed under the repository's Business Source License 1.1. See LICENSE.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kirin_Tool.Services.Approval;

/// <summary>Why a GUI-delivered approval could not be obtained.</summary>
public sealed class GuiNotRunningException : Exception
{
    public GuiNotRunningException(string message) : base(message) { }
}

/// <summary>The operator's answer, as it comes back over the IPC channel.</summary>
public sealed record GuiApprovalAnswer(ApprovalPromptOutcome Outcome, bool TypedConfirm = false, string? Reason = null, ApprovalScope? Scope = null);

/// <summary>
/// Out-of-band approval channel to a running GUI. This is what makes "confirm:true + human in the
/// loop" possible for MCP/other automated callers; when no GUI is listening the caller must be
/// told to switch entry point instead of being allowed through.
/// </summary>
public interface IGuiApprovalBridge
{
    string Name { get; }

    bool IsGuiRunning();

    /// <exception cref="GuiNotRunningException">no GUI is listening.</exception>
    Task<GuiApprovalAnswer> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Location of the approval socket (overridable for tests and for portable installs).</summary>
public static class ApprovalIpc
{
    public static string SocketPath
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable("KIRIN_APPROVAL_SOCKET");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden!;

            string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } dir
                ? dir
                : Path.GetTempPath();
            return Path.Combine(runtime, "kirin-tool-approval.sock");
        }
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Client side of the approval channel (used by the MCP gate).</summary>
public sealed class SocketGuiApprovalBridge : IGuiApprovalBridge
{
    private readonly string _socketPath;

    public SocketGuiApprovalBridge(string? socketPath = null) => _socketPath = socketPath ?? ApprovalIpc.SocketPath;

    public string Name => "unix:" + _socketPath;

    public bool IsGuiRunning()
    {
        if (!File.Exists(_socketPath)) return false;
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(_socketPath));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<GuiApprovalAnswer> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(_socketPath))
            throw new GuiNotRunningException("no GUI approval socket at " + _socketPath);

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new GuiNotRunningException("cannot connect to the GUI approval socket: " + ex.Message);
        }

        using var stream = new NetworkStream(socket, ownsSocket: false);
        string payload = JsonSerializer.Serialize(request, ApprovalIpc.Json) + "\n";
        byte[] outbound = Encoding.UTF8.GetBytes(payload);
        await stream.WriteAsync(outbound, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
            throw new GuiNotRunningException("the GUI closed the approval channel without an answer");

        return JsonSerializer.Deserialize<GuiApprovalAnswer>(line!, ApprovalIpc.Json)
            ?? throw new GuiNotRunningException("unparseable approval answer from the GUI");
    }
}

/// <summary>
/// Server side: lives inside the GUI process, hands every incoming request to the modal approval
/// dialog on the UI thread. One request per connection, one answer line back.
/// </summary>
public sealed class GuiApprovalServer : IDisposable
{
    private readonly string _socketPath;
    private readonly Func<ApprovalRequest, CancellationToken, Task<GuiApprovalAnswer>> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private Socket? _listener;
    private Task? _loop;

    public GuiApprovalServer(Func<ApprovalRequest, CancellationToken, Task<GuiApprovalAnswer>> handler, string? socketPath = null)
    {
        _handler = handler;
        _socketPath = socketPath ?? ApprovalIpc.SocketPath;
    }

    public string SocketPath => _socketPath;

    /// <summary>Binds the socket and starts serving; returns false when another GUI already owns it.</summary>
    public bool Start()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_socketPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(_socketPath)) File.Delete(_socketPath);

            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            _listener.Listen(4);
        }
        catch (Exception ex)
        {
            global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("GuiApprovalServer", ex.ToString());
            _listener?.Dispose();
            _listener = null;
            return false;
        }

        _loop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        return true;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null)
        {
            Socket connection;
            try
            {
                connection = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("GuiApprovalServer", ex.ToString());
                return;
            }

            _ = Task.Run(() => ServeOneAsync(connection, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeOneAsync(Socket connection, CancellationToken cancellationToken)
    {
        try
        {
            using (connection)
            using (var stream = new NetworkStream(connection, ownsSocket: false))
            using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                GuiApprovalAnswer answer;
                if (string.IsNullOrWhiteSpace(line))
                {
                    answer = new GuiApprovalAnswer(ApprovalPromptOutcome.Denied, false, "empty approval request");
                }
                else
                {
                    ApprovalRequest? request = null;
                    try
                    {
                        request = JsonSerializer.Deserialize<ApprovalRequest>(line!, ApprovalIpc.Json);
                    }
                    catch (JsonException ex)
                    {
                        global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("GuiApprovalServer", ex.ToString());
                    }

                    answer = request == null
                        ? new GuiApprovalAnswer(ApprovalPromptOutcome.Denied, false, "unparseable approval request")
                        : await _handler(request, cancellationToken).ConfigureAwait(false);
                }

                byte[] outbound = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(answer, ApprovalIpc.Json) + "\n");
                await stream.WriteAsync(outbound, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("GuiApprovalServer", ex.ToString());
        }
    }

    public void Dispose()
    {
        try { _shutdown.Cancel(); } catch { }
        try { _listener?.Dispose(); } catch { }
        try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _shutdown.Dispose();
    }
}
