using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Servers.Ws;

namespace SimpleTranslatorCS;

[Injectable(InjectionType.Singleton)]
public sealed class LocaleNotificationSocket(SaveServer saveServer) : IWebSocketConnectionHandler
{
    private sealed record Subscriber(WebSocket Socket, Channel<string> Updates);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Subscriber> Subscribers = new(StringComparer.OrdinalIgnoreCase);
    private static string _latest = "";
    private const string Path = "/simpletranslator/v1/workbench/locale-notify";

    public string GetHookUrl() => Path;
    public string GetSocketId() => "simpletranslator-locale";

    internal static void Publish(string revision)
    {
        lock (Sync)
        {
            _latest = revision;
            foreach (var entry in Subscribers.Values) entry.Updates.Writer.TryWrite(revision);
        }
    }

#if SPT40
    public Task OnConnection(WebSocket socket, HttpContext context, string socketId) => ConnectAsync(socket, context);
    public Task OnMessage(byte[] data, WebSocketMessageType type, WebSocket socket, HttpContext context) => Task.CompletedTask;
    public Task OnClose(WebSocket socket, HttpContext context, string socketId) => DisconnectAsync(socket, context);
#else
    public Task OnConnectionAsync(WebSocket socket, HttpContext context, string socketId) => ConnectAsync(socket, context);
    public Task OnMessageAsync(byte[] data, WebSocketMessageType type, WebSocket socket, HttpContext context) => Task.CompletedTask;
    public Task OnCloseAsync(WebSocket socket, HttpContext context, string socketId) => DisconnectAsync(socket, context);
#endif

    private Task ConnectAsync(WebSocket socket, HttpContext context)
    {
        var session = context.Request.Headers["X-SimpleTranslator-Session"].ToString();
        if (context.Request.Path != Path || session.Length is 0 or > 128 ||
            !SimpleTranslatorRouter.HasProfile(saveServer, session))
        {
            socket.Abort();
            return Task.CompletedTask;
        }
        var entry = new Subscriber(socket, Channel.CreateBounded<string>(new BoundedChannelOptions(1)
            { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest }));
        Subscriber? previous;
        lock (Sync)
        {
            Subscribers.TryGetValue(session, out previous);
            if (previous == null && Subscribers.Count >= 64) { socket.Abort(); return Task.CompletedTask; }
            Subscribers[session] = entry;
            entry.Updates.Writer.TryWrite(_latest);
        }
        previous?.Updates.Writer.TryComplete();
        previous?.Socket.Abort();
        _ = SendAsync(session, entry, context.RequestAborted);
        return Task.CompletedTask;
    }

    private static async Task SendAsync(string session, Subscriber entry, CancellationToken token)
    {
        try
        {
            await foreach (var revision in entry.Updates.Reader.ReadAllAsync(token))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await entry.Socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(revision)),
                    WebSocketMessageType.Text, true, deadline.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidOperationException)
        {
            entry.Socket.Abort();
        }
        finally { Remove(session, entry.Socket); }
    }

    private static Task DisconnectAsync(WebSocket socket, HttpContext context)
    {
        Remove(context.Request.Headers["X-SimpleTranslator-Session"].ToString(), socket);
        return Task.CompletedTask;
    }

    private static void Remove(string session, WebSocket socket)
    {
        lock (Sync)
        {
            if (!Subscribers.TryGetValue(session, out var current) || !ReferenceEquals(current.Socket, socket)) return;
            Subscribers.Remove(session);
            current.Updates.Writer.TryComplete();
        }
    }
}
