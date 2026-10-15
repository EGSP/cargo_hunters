using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BepInEx;
using UnityEngine;

namespace CargoHunters.InventoryMod;

/// <summary>
/// TCP обслуживает HTTP только на loopback. Unity API вызывается исключительно из Update.
/// Токен хранится локально, вне исходников, и проверяется также для команд чтения.
/// </summary>
public sealed class BridgeBehaviour : MonoBehaviour
{
    public BridgeBehaviour(IntPtr pointer) : base(pointer) { }
    private static readonly ConcurrentQueue<Work> Queue = new();
    private static TcpListener? listener;
    private static CancellationTokenSource? shutdown;
    private static string token = "";
    private static long frame;
    private static int pending;
    private static bool previousRunInBackground;
    private const int Port = 18741;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 128 };

    private sealed class Work
    {
        public string Path = "";
        public string Method = "";
        public string Body = "";
        public readonly TaskCompletionSource<object> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // CAS prevents a request that timed out while waiting from later changing game state.
        public int State; // 0 queued, 1 executing, 2 expired
    }

    public void Awake()
    {
        previousRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        var connectionPath = Path.Combine(Paths.ConfigPath, "cargo-dev-bridge.json");
        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        shutdown = new CancellationTokenSource();
        listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        File.WriteAllText(connectionPath, JsonSerializer.Serialize(new
        {
            url = $"http://127.0.0.1:{Port}", token, processId = Environment.ProcessId, version = Plugin.Version
        }, JsonOptions));
        _ = AcceptLoop(shutdown.Token);
    }

    public void Update()
    {
        Interlocked.Increment(ref frame);
        KeyboardCommands.Update();
        // Bound requests per frame; scene traversal is bounded independently in SceneCommands.
        for (var i = 0; i < 4 && Queue.TryDequeue(out var work); i++)
        {
            Interlocked.Decrement(ref pending);
            if (Interlocked.CompareExchange(ref work.State, 1, 0) != 0) continue;
            try { work.Completion.TrySetResult(SceneCommands.Execute(work.Method, work.Path, work.Body)); }
            catch (Exception error) { work.Completion.TrySetException(error); }
        }
    }

    public void OnDestroy()
    {
        Application.runInBackground = previousRunInBackground;
        Stop();
    }

    public static void Stop()
    {
        shutdown?.Cancel();
        listener?.Stop();
        while (Queue.TryDequeue(out var work))
        {
            Interlocked.Decrement(ref pending);
            Interlocked.Exchange(ref work.State, 2);
            work.Completion.TrySetCanceled();
        }
    }

    private static async Task AcceptLoop(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(cancellation);
                _ = Serve(client, cancellation);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (cancellation.IsCancellationRequested) { }
    }

    private static async Task Serve(TcpClient client, CancellationToken cancellation)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var stream = client.GetStream();
            var status = 200;
            object response;
            try
            {
                // Read bytes to avoid StreamReader buffering bytes from the UTF-8 body.
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 8192)
                {
                    if (await stream.ReadAsync(one.AsMemory(), deadline.Token) == 0) return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                if (header.Count >= 8192) throw new ArgumentException("Headers too large");
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var request = lines[0].Split(' ');
                if (request.Length != 3 || !request[1].StartsWith('/')) throw new ArgumentException("Invalid request");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).Where(x => x.Contains(':')))
                {
                    var split = line.IndexOf(':');
                    headers.Add(line[..split].Trim(), line[(split + 1)..].Trim());
                }
                if (headers.ContainsKey("Origin")) { status = 403; response = new { error = "Browser origins are not accepted" }; }
                else if (!headers.TryGetValue("Authorization", out var auth) || auth != "Bearer " + token)
                { status = 401; response = new { error = "Bearer token required" }; }
                else
                {
                    if (headers.ContainsKey("Transfer-Encoding")) throw new ArgumentException("Transfer-Encoding is unsupported");
                    var length = headers.TryGetValue("Content-Length", out var value) ? int.Parse(value) : 0;
                    if (length < 0 || length > 32768) throw new ArgumentException("Body must be at most 32768 bytes");
                    var bytes = new byte[length];
                    var offset = 0;
                    while (offset < length)
                    {
                        var read = await stream.ReadAsync(bytes.AsMemory(offset), deadline.Token);
                        if (read == 0) throw new ArgumentException("Incomplete body");
                        offset += read;
                    }
                    if (request[0] == "GET" && request[1] == "/health")
                        response = new { ready = true, frame = Interlocked.Read(ref frame), pending = Volatile.Read(ref pending), version = Plugin.Version, processId = Environment.ProcessId };
                    else
                    {
                        if (Interlocked.Increment(ref pending) > 64)
                        {
                            Interlocked.Decrement(ref pending);
                            status = 429;
                            response = new { error = "Command queue full" };
                        }
                        else
                        {
                            var work = new Work { Method = request[0], Path = request[1], Body = Encoding.UTF8.GetString(bytes) };
                            Queue.Enqueue(work);
                            try { response = await work.Completion.Task.WaitAsync(TimeSpan.FromSeconds(8), deadline.Token); }
                            catch (OperationCanceledException)
                            {
                                Interlocked.CompareExchange(ref work.State, 2, 0);
                                throw;
                            }
                            catch (TimeoutException)
                            {
                                var wasQueued = Interlocked.CompareExchange(ref work.State, 2, 0) == 0;
                                status = 504;
                                response = new { error = wasQueued ? "Main thread unavailable; command cancelled before execution" : "Command started; outcome unknown. Inspect state before retrying." };
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (JsonException error) { status = 400; response = new { error = error.Message }; }
            catch (ArgumentException error) { status = 400; response = new { error = error.Message }; }
            catch (Exception error) { status = 500; response = new { error = error.GetBaseException().Message }; }
            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {json.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
                await stream.WriteAsync(head.AsMemory(), deadline.Token);
                await stream.WriteAsync(json.AsMemory(), deadline.Token);
            }
            catch (Exception) when (deadline.IsCancellationRequested || !client.Connected) { }
            catch (IOException) { }
        }
    }
}
