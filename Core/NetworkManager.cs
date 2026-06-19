using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MelonLoader;
using Newtonsoft.Json;
using SprocketMultiplayer.Core;
using SprocketMultiplayer.UI;

namespace SprocketMultiplayer
{
    public class NetworkEnvelope
    {
        public string Type;
        public List<string> Args = new List<string>();

        public string Get(int index, string fallback = "")
        {
            return Args != null && index >= 0 && index < Args.Count ? Args[index] : fallback;
        }
    }

    public class NetworkManager
    {
        private const int DefaultBufferSize = 8192;

        private TcpListener server;
        private TcpClient client;
        private NetworkStream stream;
        private CancellationTokenSource cancellation;

        private readonly List<TcpClient> clients = new List<TcpClient>();
        private readonly Dictionary<TcpClient, string> clientNicknames = new Dictionary<TcpClient, string>();
        private readonly Dictionary<TcpClient, DateTime> pingSentTime = new Dictionary<TcpClient, DateTime>();
        private readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();

        private DateTime lastPingTime = DateTime.MinValue;
        private string hostNickname = "Host";

        public static NetworkManager Instance { get; private set; }

        public string HostNickname => hostNickname;
        public bool IsActiveMultiplayer => IsHost || IsClient;
        public bool IsHost { get; private set; }
        public bool IsClient { get; private set; }
        public int CurrentPort { get; private set; }
        public string CurrentIP { get; private set; } = "127.0.0.1";
        public string LocalNickname { get; set; }
        public int ClientCount => clients.Count;

        public NetworkManager()
        {
            Instance = this;
        }

        public void StartHost(int port)
        {
            try
            {
                if (IsHost || server != null)
                {
                    MelonLogger.Msg("[Network] Host already running.");
                    return;
                }

                Shutdown();
                cancellation = new CancellationTokenSource();
                IsHost = true;
                IsClient = false;

                hostNickname = !string.IsNullOrEmpty(LocalNickname)
                    ? LocalNickname
                    : MenuActions.GetSteamNickname();
                LocalNickname = hostNickname;

                server = new TcpListener(IPAddress.Any, port);
                server.Start();
                CurrentPort = port;

                MelonLogger.Msg($"[Network] Host started on TCP port {port}.");
                _ = AcceptClientsAsync(cancellation.Token);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Network] Failed to start host on port {port}: {ex.Message}");
                Shutdown();
            }
        }

        public bool ConnectToHost(string ip, int port)
        {
            try
            {
                if (IsHost || IsClient)
                {
                    MelonLogger.Msg("[Network] Already in multiplayer mode.");
                    return IsClient;
                }

                cancellation = new CancellationTokenSource();
                client = new TcpClient();
                client.Connect(IPAddress.Parse(ip), port);
                stream = client.GetStream();

                IsClient = true;
                CurrentIP = ip;
                CurrentPort = port;

                if (string.IsNullOrEmpty(LocalNickname))
                    LocalNickname = MenuActions.GetSteamNickname();

                MelonLogger.Msg($"[Network] Connected to host at {ip}:{port}.");
                SendEnvelope("JOIN", LocalNickname);
                _ = ReceiveLoopAsync(client, stream, cancellation.Token);
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Network] Failed to connect to {ip}:{port}: {ex.Message}");
                Shutdown();
                return false;
            }
        }

        private async Task AcceptClientsAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && server != null)
            {
                TcpClient accepted = null;
                try
                {
                    accepted = await server.AcceptTcpClientAsync();
                    clients.Add(accepted);
                    MelonLogger.Msg($"[Network] Client connected: {accepted.Client.RemoteEndPoint}");
                    _ = ReceiveLoopAsync(accepted, accepted.GetStream(), token);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[Network] Accept failed: {ex.Message}");
                    if (accepted != null) SafeClose(accepted);
                }
            }
        }

        private async Task ReceiveLoopAsync(TcpClient sourceClient, NetworkStream sourceStream, CancellationToken token)
        {
            byte[] buffer = new byte[DefaultBufferSize];
            var pending = new StringBuilder();

            while (!token.IsCancellationRequested && sourceClient.Connected)
            {
                int bytesRead;
                try
                {
                    bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead <= 0) break;
                }
                catch
                {
                    break;
                }

                pending.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                DrainMessages(pending, sourceClient);
            }

            mainThreadActions.Enqueue(() => HandleDisconnect(sourceClient));
        }

        private void DrainMessages(StringBuilder pending, TcpClient sourceClient)
        {
            string data = pending.ToString();
            int newline;

            while ((newline = data.IndexOf('\n')) >= 0)
            {
                string line = data.Substring(0, newline).Trim();
                data = data.Substring(newline + 1);

                if (!string.IsNullOrEmpty(line))
                    QueueIncomingLine(sourceClient, line);
            }

            pending.Clear();
            pending.Append(data);
        }

        private void QueueIncomingLine(TcpClient sourceClient, string line)
        {
            mainThreadActions.Enqueue(() =>
            {
                if (TryParseEnvelope(line, out var envelope))
                    HandleEnvelope(sourceClient, envelope);
                else
                    HandleLegacyMessage(sourceClient, line);
            });
        }

        public void PollEvents()
        {
            while (mainThreadActions.TryDequeue(out var action))
                action?.Invoke();

            if (IsHost)
                PollPings();
        }

        private void PollPings()
        {
            if ((DateTime.UtcNow - lastPingTime).TotalSeconds < 2)
                return;

            foreach (var c in clients.ToArray())
            {
                if (c?.Connected != true) continue;
                SendToClient(c, "Ping!");
                pingSentTime[c] = DateTime.UtcNow;
            }

            lastPingTime = DateTime.UtcNow;
        }

        private void HandleEnvelope(TcpClient sourceClient, NetworkEnvelope envelope)
        {
            if (envelope.Type == "JOIN" && IsHost)
            {
                string nick = envelope.Get(0, "Player");
                clientNicknames[sourceClient] = nick;
                MelonLogger.Msg($"[Network] Registered client nickname: {nick}");
            }

            if (envelope.Type == "PONG" && IsHost)
            {
                if (pingSentTime.TryGetValue(sourceClient, out var sent) &&
                    clientNicknames.TryGetValue(sourceClient, out var nick))
                {
                    int ping = (int)(DateTime.UtcNow - sent).TotalMilliseconds;
                    MelonLogger.Msg($"[Network] Ping {nick}: {ping} ms");
                }
                return;
            }

            LobbyManager.Instance.ProcessNetworkMessage(envelope);
        }

        private void HandleLegacyMessage(TcpClient sourceClient, string message)
        {
            if (message == "Ping!")
            {
                SendEnvelope("PONG");
                return;
            }

            if (message == "Pong!" && IsHost)
            {
                if (pingSentTime.TryGetValue(sourceClient, out var sent) &&
                    clientNicknames.TryGetValue(sourceClient, out var nick))
                {
                    int ping = (int)(DateTime.UtcNow - sent).TotalMilliseconds;
                    MelonLogger.Msg($"[Network] Ping {nick}: {ping} ms");
                }
                return;
            }

            MelonLogger.Msg($"[Network] Legacy message ignored: {message}");
        }

        private void HandleDisconnect(TcpClient disconnected)
        {
            if (disconnected == null) return;

            string nickname = null;
            clientNicknames.TryGetValue(disconnected, out nickname);

            clients.Remove(disconnected);
            clientNicknames.Remove(disconnected);
            pingSentTime.Remove(disconnected);
            SafeClose(disconnected);

            if (IsHost && !string.IsNullOrEmpty(nickname))
            {
                LobbyManager.Instance.Players.Remove(nickname);
                MelonLogger.Msg($"[Network] Client disconnected: {nickname}");
            }
        }

        public void Send(string msg)
        {
            if (IsHost)
            {
                foreach (var c in clients.ToArray())
                    SendToClient(c, msg);
            }
            else if (IsClient && client?.Connected == true)
            {
                SendToStream(stream, msg);
            }
        }

        public void SendEnvelope(string type, params string[] args)
        {
            var envelope = new NetworkEnvelope
            {
                Type = type,
                Args = args == null ? new List<string>() : new List<string>(args)
            };

            string line = JsonConvert.SerializeObject(envelope);
            Send(line);
        }

        private void SendToClient(TcpClient target, string msg)
        {
            if (target?.Connected != true) return;
            SendToStream(target.GetStream(), msg);
        }

        private void SendToStream(NetworkStream targetStream, string msg)
        {
            if (targetStream == null) return;
            byte[] data = Encoding.UTF8.GetBytes(msg + "\n");
            targetStream.Write(data, 0, data.Length);
        }

        private static bool TryParseEnvelope(string line, out NetworkEnvelope envelope)
        {
            envelope = null;
            if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith("{"))
                return false;

            try
            {
                envelope = JsonConvert.DeserializeObject<NetworkEnvelope>(line);
                return envelope != null && !string.IsNullOrEmpty(envelope.Type);
            }
            catch
            {
                return false;
            }
        }

        public void Shutdown()
        {
            try
            {
                cancellation?.Cancel();
                stream?.Close();
                client?.Close();
                server?.Stop();

                foreach (var c in clients.ToArray())
                    SafeClose(c);

                clients.Clear();
                clientNicknames.Clear();
                pingSentTime.Clear();
                server = null;
                client = null;
                stream = null;
                cancellation = null;
                IsHost = false;
                IsClient = false;

                MelonLogger.Msg("[Network] Shutdown complete.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Network] Shutdown error: {ex.Message}");
            }
        }

        private static void SafeClose(TcpClient tcp)
        {
            try { tcp?.Close(); }
            catch { }
        }
    }
}
