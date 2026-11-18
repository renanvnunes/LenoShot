using System.Net.Sockets;
using System.Text;

namespace LenoShot.Services;

/// <summary>
/// Garante uma única instância do app usando um socket Unix.
/// Chamadas como `leno-shot capture` apenas enviam o comando para a instância em execução.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Socket _server;
    private readonly CancellationTokenSource _cts = new();

    private static string SocketPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(dir))
                dir = Path.GetTempPath();
            return Path.Combine(dir, "leno-shot.sock");
        }
    }

    private SingleInstance(Socket server, Action<string> onCommand)
    {
        _server = server;
        _ = AcceptLoop(onCommand, _cts.Token);
    }

    public static bool TrySendToRunningInstance(string command)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(SocketPath));
            client.Send(Encoding.UTF8.GetBytes(command));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static SingleInstance StartServer(Action<string> onCommand)
    {
        var path = SocketPath;
        if (File.Exists(path))
            File.Delete(path); // socket órfão de uma execução anterior

        var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(path));
        server.Listen(4);
        return new SingleInstance(server, onCommand);
    }

    private async Task AcceptLoop(Action<string> onCommand, CancellationToken ct)
    {
        var buffer = new byte[256];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = await _server.AcceptAsync(ct);
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, ct);
                var command = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                if (command.Length > 0)
                    onCommand(command);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // ignora conexões com problema e continua ouvindo
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _server.Dispose();
        try { File.Delete(SocketPath); } catch { /* ignorado */ }
    }
}
