using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MelonLoader;
using SprocketMultiplayer.UI;
using UnityEngine.SceneManagement;

namespace SprocketMultiplayer.Core
{
    public class LobbyPlayer
    {
        public string Name;
        public bool Ready;
        public string TankName = "None";
        public string TankHash = "";
        public string BlueprintBase64 = "";
    }

    public class LobbyManager
    {
        public static readonly LobbyManager Instance = new LobbyManager();

        public readonly Dictionary<string, LobbyPlayer> Players = new Dictionary<string, LobbyPlayer>();
        public string SelectedMap = "Railway";
        public int SelectedMapIndex;
        public bool MatchLoading;

        private LobbyManager() { }

        public void Reset()
        {
            Players.Clear();
            SelectedMap = "Railway";
            SelectedMapIndex = 0;
            MatchLoading = false;
            CustomBattleMultiplayerUI.Refresh();
        }

        public void StartHost(string playerName, int port)
        {
            NetworkManager.Instance.StartHost(port);
            if (!NetworkManager.Instance.IsHost) return;

            NetworkManager.Instance.LocalNickname = playerName;
            Players.Clear();
            Players[playerName] = new LobbyPlayer { Name = playerName, Ready = true };
            BroadcastLobbyState();
            CustomBattleMultiplayerUI.Refresh();
        }

        public bool JoinHost(string playerName, string ip, int port)
        {
            NetworkManager.Instance.LocalNickname = playerName;
            return NetworkManager.Instance.ConnectToHost(ip, port);
        }

        public void LeaveLobby()
        {
            if (NetworkManager.Instance == null) return;

            if (NetworkManager.Instance.IsHost)
                NetworkManager.Instance.SendEnvelope("HOST_CLOSED", "bye");
            else if (NetworkManager.Instance.IsClient)
                NetworkManager.Instance.SendEnvelope("LEAVE", NetworkManager.Instance.LocalNickname ?? "");

            NetworkManager.Instance.Shutdown();
            Reset();
        }

        public void SelectTank(TankInfo tank)
        {
            if (tank == null || NetworkManager.Instance == null) return;

            string name = NetworkManager.Instance.LocalNickname;
            if (string.IsNullOrEmpty(name))
                name = CustomBattleMultiplayerUI.PlayerName;

            if (NetworkManager.Instance.IsHost)
            {
                EnsurePlayer(name);
                Players[name].TankName = tank.Name;
                Players[name].TankHash = tank.Hash ?? "";
                Players[name].BlueprintBase64 = TankDatabase.ReadBlueprintBase64(tank.BlueprintPath) ?? "";
                BroadcastLobbyState();
            }
            else if (NetworkManager.Instance.IsClient)
            {
                NetworkManager.Instance.SendEnvelope("SELECT_TANK", name, tank.Name, tank.Hash ?? "");
            }

            CustomBattleMultiplayerUI.Refresh();
        }

        public void SetReady(bool ready)
        {
            string name = NetworkManager.Instance?.LocalNickname;
            if (string.IsNullOrEmpty(name)) return;

            if (NetworkManager.Instance.IsHost)
            {
                EnsurePlayer(name);
                Players[name].Ready = true;
                BroadcastLobbyState();
            }
            else if (NetworkManager.Instance.IsClient)
            {
                NetworkManager.Instance.SendEnvelope("READY", name, ready ? "1" : "0");
            }
        }

        public void SetMap(int index, string sceneName)
        {
            SelectedMapIndex = index;
            SelectedMap = string.IsNullOrEmpty(sceneName) ? "Railway" : sceneName;

            if (NetworkManager.Instance != null && NetworkManager.Instance.IsHost)
            {
                NetworkManager.Instance.SendEnvelope("SYNC_MAP", SelectedMapIndex.ToString(), SelectedMap);
                BroadcastLobbyState();
            }

            CustomBattleMultiplayerUI.Refresh();
        }

        public void StartMatch()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsHost)
                return;

            if (Players.Count == 0 || Players.Values.Any(p => !p.Ready || string.IsNullOrEmpty(p.TankHash)))
            {
                MelonLogger.Warning("[Lobby] Cannot start: players are not ready or have no tank selected.");
                return;
            }

            foreach (var player in Players.Values)
            {
                if (string.IsNullOrEmpty(player.BlueprintBase64) && player.Name == NetworkManager.Instance.LocalNickname)
                {
                    MelonLogger.Warning($"[Lobby] Host player {player.Name} has no local blueprint data.");
                    return;
                }
            }

            NetworkManager.Instance.SendEnvelope("REQUEST_BLUEPRINT");
            CollectLocalBlueprint();
            TryLoadWhenBlueprintsReady();
        }

        public void ProcessNetworkMessage(NetworkEnvelope message)
        {
            if (message == null || string.IsNullOrEmpty(message.Type)) return;

            try
            {
                switch (message.Type)
                {
                    case "JOIN": HandleJoin(message); break;
                    case "LEAVE": HandleLeave(message); break;
                    case "READY": HandleReady(message); break;
                    case "SELECT_TANK": HandleSelectTank(message); break;
                    case "LOBBY_STATE": HandleLobbyState(message); break;
                    case "SYNC_MAP": HandleSyncMap(message); break;
                    case "REQUEST_BLUEPRINT": HandleRequestBlueprint(); break;
                    case "BLUEPRINT_DATA": HandleBlueprintData(message); break;
                    case "LOAD_MAP": HandleLoadMap(message); break;
                    case "HOST_CLOSED": HandleHostClosed(); break;
                    default: MelonLogger.Msg($"[Lobby] Unhandled message: {message.Type}"); break;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Lobby] Failed to process {message.Type}: {ex.Message}");
            }
        }

        private void HandleJoin(NetworkEnvelope message)
        {
            if (!NetworkManager.Instance.IsHost) return;
            string name = message.Get(0, "Player");
            EnsurePlayer(name);
            BroadcastLobbyState();
        }

        private void HandleLeave(NetworkEnvelope message)
        {
            string name = message.Get(0, "");
            if (!string.IsNullOrEmpty(name))
                Players.Remove(name);

            if (NetworkManager.Instance.IsHost)
                BroadcastLobbyState();
            else
                CustomBattleMultiplayerUI.Refresh();
        }

        private void HandleReady(NetworkEnvelope message)
        {
            if (!NetworkManager.Instance.IsHost) return;
            string name = message.Get(0, "");
            bool ready = message.Get(1, "0") == "1";
            EnsurePlayer(name);
            Players[name].Ready = ready;
            BroadcastLobbyState();
        }

        private void HandleSelectTank(NetworkEnvelope message)
        {
            if (!NetworkManager.Instance.IsHost) return;
            string name = message.Get(0, "");
            EnsurePlayer(name);
            Players[name].TankName = message.Get(1, "None");
            Players[name].TankHash = message.Get(2, "");
            BroadcastLobbyState();
        }

        private void HandleLobbyState(NetworkEnvelope message)
        {
            if (!NetworkManager.Instance.IsClient) return;

            Players.Clear();
            SelectedMapIndex = int.TryParse(message.Get(0, "0"), out var idx) ? idx : 0;
            SelectedMap = message.Get(1, "Railway");

            string payload = message.Get(2, "");
            if (!string.IsNullOrEmpty(payload))
            {
                foreach (string entry in payload.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] fields = entry.Split(':');
                    if (fields.Length < 4) continue;
                    Players[fields[0]] = new LobbyPlayer
                    {
                        Name = fields[0],
                        Ready = fields[1] == "1",
                        TankName = DecodeField(fields[2]),
                        TankHash = fields[3]
                    };
                }
            }

            CustomBattleMultiplayerUI.ApplyMapSelection(SelectedMapIndex);
            CustomBattleMultiplayerUI.Refresh();
        }

        private void HandleSyncMap(NetworkEnvelope message)
        {
            if (NetworkManager.Instance.IsHost) return;
            SelectedMapIndex = int.TryParse(message.Get(0, "0"), out var idx) ? idx : 0;
            SelectedMap = message.Get(1, "Railway");
            CustomBattleMultiplayerUI.ApplyMapSelection(SelectedMapIndex);
            CustomBattleMultiplayerUI.Refresh();
        }

        private void HandleRequestBlueprint()
        {
            CollectLocalBlueprint();
        }

        private void HandleBlueprintData(NetworkEnvelope message)
        {
            if (!NetworkManager.Instance.IsHost) return;
            string name = message.Get(0, "");
            EnsurePlayer(name);
            Players[name].TankName = message.Get(1, Players[name].TankName);
            Players[name].TankHash = message.Get(2, Players[name].TankHash);
            Players[name].BlueprintBase64 = message.Get(3, "");
            TryLoadWhenBlueprintsReady();
        }

        private void HandleLoadMap(NetworkEnvelope message)
        {
            string sceneName = message.Get(0, SelectedMap);
            MatchLoading = true;
            CustomBattleMultiplayerUI.CloseForSceneLoad();
            MelonLogger.Msg($"[Lobby] Loading multiplayer scene: {sceneName}");
            SceneManager.LoadScene(sceneName);
        }

        private void HandleHostClosed()
        {
            MelonLogger.Warning("[Lobby] Host closed the lobby.");
            NetworkManager.Instance.Shutdown();
            Reset();
            CustomBattleMultiplayerUI.ShowNativeUI();
        }

        private void TryLoadWhenBlueprintsReady()
        {
            if (!NetworkManager.Instance.IsHost) return;

            foreach (var player in Players.Values)
            {
                if (string.IsNullOrEmpty(player.BlueprintBase64))
                    return;
            }

            MatchLoading = true;
            NetworkManager.Instance.SendEnvelope("LOAD_MAP", SelectedMap);
            HandleLoadMap(new NetworkEnvelope { Type = "LOAD_MAP", Args = new List<string> { SelectedMap } });
        }

        private void CollectLocalBlueprint()
        {
            TankInfo selected = CustomBattleMultiplayerUI.SelectedTank;
            if (selected == null) return;

            string data = TankDatabase.ReadBlueprintBase64(selected.BlueprintPath);
            if (string.IsNullOrEmpty(data))
            {
                MelonLogger.Warning("[Lobby] Selected tank blueprint could not be read.");
                return;
            }

            string name = NetworkManager.Instance.LocalNickname;
            if (NetworkManager.Instance.IsHost)
            {
                EnsurePlayer(name);
                Players[name].TankName = selected.Name;
                Players[name].TankHash = selected.Hash ?? "";
                Players[name].BlueprintBase64 = data;
            }
            else if (NetworkManager.Instance.IsClient)
            {
                NetworkManager.Instance.SendEnvelope("BLUEPRINT_DATA", name, selected.Name, selected.Hash ?? "", data);
            }
        }

        private void BroadcastLobbyState()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsHost) return;

            string players = string.Join(",", Players.Values.Select(p =>
                $"{p.Name}:{(p.Ready ? "1" : "0")}:{EncodeField(p.TankName)}:{p.TankHash}"));

            NetworkManager.Instance.SendEnvelope("LOBBY_STATE", SelectedMapIndex.ToString(), SelectedMap, players);
            CustomBattleMultiplayerUI.Refresh();
        }

        private void EnsurePlayer(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "Player";
            if (!Players.ContainsKey(name))
                Players[name] = new LobbyPlayer { Name = name };
        }

        private static string EncodeField(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string DecodeField(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch { return value; }
        }
    }
}
