using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppTMPro;
using MelonLoader;
using SprocketMultiplayer.Core;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SprocketMultiplayer.UI
{
    public static class CustomBattleMultiplayerUI
    {
        private const int DefaultPort = 7777;

        private static GameObject injectedRoot;
        private static GameObject lobbyPanel;
        private static GameObject connectPanel;
        private static TextMeshProUGUI rosterText;
        private static TextMeshProUGUI statusText;
        private static TextMeshProUGUI mapText;
        private static TextMeshProUGUI selectedTankText;
        private static TextMeshProUGUI actionText;
        private static TMP_InputField nameInput;
        private static TMP_InputField ipInput;
        private static Transform tankListContent;

        private static readonly List<TankInfo> tanks = new List<TankInfo>();
        private static readonly string[] sceneNames = { "Railway" };

        public static TankInfo SelectedTank { get; private set; }
        public static string PlayerName
        {
            get => PlayerPrefs.GetString("SprocketMP.PlayerName", "Player" + UnityEngine.Random.Range(1000, 9999));
            private set => PlayerPrefs.SetString("SprocketMP.PlayerName", value);
        }

        public static void Inject()
        {
            if (injectedRoot != null) return;

            GameObject content = GameObject.Find("Root/Canvas/Content");
            if (content == null)
            {
                MelonLogger.Warning("[CustomBattleUI] Root/Canvas/Content not found.");
                return;
            }

            LockWeatherControls();

            injectedRoot = new GameObject("SprocketMP_InjectedRoot");
            injectedRoot.transform.SetParent(content.transform, false);
            var rect = injectedRoot.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 1);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            BuildEntryControls(injectedRoot.transform);
            BuildConnectPanel(injectedRoot.transform);
            BuildLobbyPanel(injectedRoot.transform);

            ReloadTanks();
            Refresh();

            MelonLogger.Msg("[CustomBattleUI] Multiplayer UI injected.");
        }

        public static void Refresh()
        {
            if (rosterText != null)
                rosterText.text = BuildRosterText();

            if (statusText != null)
                statusText.text = BuildStatusText();

            if (mapText != null)
                mapText.text = "Map: " + LobbyManager.Instance.SelectedMap;

            if (selectedTankText != null)
                selectedTankText.text = SelectedTank == null ? "Tank: None" : "Tank: " + SelectedTank.Name;

            if (actionText != null)
                actionText.text = BuildActionText();
        }

        public static void ApplyMapSelection(int index)
        {
            if (index < 0 || index >= sceneNames.Length)
                index = 0;

            LobbyManager.Instance.SelectedMapIndex = index;
            LobbyManager.Instance.SelectedMap = sceneNames[index];
            Refresh();
        }

        public static void CloseForSceneLoad()
        {
            if (connectPanel != null) connectPanel.SetActive(false);
            if (lobbyPanel != null) lobbyPanel.SetActive(false);
            if (injectedRoot != null) injectedRoot.SetActive(false);
        }

        public static void ShowNativeUI()
        {
            if (injectedRoot != null) injectedRoot.SetActive(true);
        }

        private static void BuildEntryControls(Transform parent)
        {
            var panel = CreatePanel("MP_EntryPanel", parent, new Vector2(0.02f, 0.54f), new Vector2(0.28f, 0.96f));

            CreateText(panel.transform, "Title", "MULTIPLAYER", new Vector2(0, -24), 28);
            CreateText(panel.transform, "NameLabel", "PLAYER NAME", new Vector2(0, -72), 16);
            nameInput = CreateInput(panel.transform, "NameInput", PlayerName, new Vector2(0, -108), 230);
            nameInput.onEndEdit.AddListener((UnityAction<string>)(value =>
            {
                if (!string.IsNullOrWhiteSpace(value))
                    PlayerName = value.Trim();
            }));

            CreateButton(panel.transform, "HostButton", "HOST", new Vector2(0, -166), OnHostClicked);
            CreateButton(panel.transform, "JoinButton", "JOIN", new Vector2(0, -224), OnJoinClicked);
        }

        private static void BuildConnectPanel(Transform parent)
        {
            connectPanel = CreatePanel("MP_ConnectPanel", parent, new Vector2(0.31f, 0.54f), new Vector2(0.62f, 0.86f));
            CreateText(connectPanel.transform, "Title", "CONNECT", new Vector2(0, -26), 26);
            ipInput = CreateInput(connectPanel.transform, "IpInput", "127.0.0.1", new Vector2(0, -86), 250);
            CreateButton(connectPanel.transform, "ConnectButton", "CONNECT", new Vector2(0, -148), () =>
            {
                string ip = string.IsNullOrWhiteSpace(ipInput.text) ? "127.0.0.1" : ipInput.text.Trim();
                LobbyManager.Instance.JoinHost(GetCurrentPlayerName(), ip, DefaultPort);
                connectPanel.SetActive(false);
                lobbyPanel.SetActive(true);
                Refresh();
            });
            connectPanel.SetActive(false);
        }

        private static void BuildLobbyPanel(Transform parent)
        {
            lobbyPanel = CreatePanel("MP_LobbyPanel", parent, new Vector2(0.31f, 0.08f), new Vector2(0.96f, 0.96f));

            CreateText(lobbyPanel.transform, "Title", "MULTIPLAYER LOBBY", new Vector2(0, -28), 30);
            mapText = CreateText(lobbyPanel.transform, "MapText", "Map: Railway", new Vector2(-210, -72), 20);
            CreateButton(lobbyPanel.transform, "MapRailway", "RAILWAY", new Vector2(-210, -122), () =>
            {
                LobbyManager.Instance.SetMap(0, sceneNames[0]);
            });

            CreateText(lobbyPanel.transform, "WeatherLock", "Weather: Default (locked)", new Vector2(170, -72), 18);

            var rosterBg = CreatePanel("Roster", lobbyPanel.transform, new Vector2(0.04f, 0.10f), new Vector2(0.48f, 0.66f));
            rosterText = CreateText(rosterBg.transform, "RosterText", "", new Vector2(0, -18), 18);
            rosterText.alignment = TextAlignmentOptions.TopLeft;
            rosterText.rectTransform.sizeDelta = new Vector2(330, 250);

            var tankBg = CreatePanel("Tanks", lobbyPanel.transform, new Vector2(0.52f, 0.22f), new Vector2(0.96f, 0.66f));
            CreateText(tankBg.transform, "TankTitle", "TANKS", new Vector2(0, -20), 22);
            BuildTankList(tankBg.transform);
            selectedTankText = CreateText(lobbyPanel.transform, "SelectedTank", "Tank: None", new Vector2(190, -420), 18);

            actionText = null;
            GameObject actionButton = CreateButton(lobbyPanel.transform, "ActionButton", "READY", new Vector2(-120, -420), OnActionClicked);
            actionText = actionButton.GetComponentInChildren<TextMeshProUGUI>();
            CreateButton(lobbyPanel.transform, "LeaveButton", "LEAVE", new Vector2(-120, -480), () =>
            {
                LobbyManager.Instance.LeaveLobby();
                lobbyPanel.SetActive(false);
                connectPanel.SetActive(false);
                Refresh();
            });

            statusText = CreateText(lobbyPanel.transform, "Status", "", new Vector2(190, -480), 16);
            lobbyPanel.SetActive(false);
        }

        private static void BuildTankList(Transform parent)
        {
            var scrollGO = new GameObject("TankScroll");
            scrollGO.transform.SetParent(parent, false);
            var scrollRect = scrollGO.AddComponent<RectTransform>();
            scrollRect.anchorMin = new Vector2(0.08f, 0.08f);
            scrollRect.anchorMax = new Vector2(0.92f, 0.78f);
            scrollRect.offsetMin = Vector2.zero;
            scrollRect.offsetMax = Vector2.zero;
            var scroll = scrollGO.AddComponent<ScrollRect>();
            scroll.horizontal = false;

            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollGO.transform, false);
            viewport.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            var viewportRect = viewport.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;

            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            tankListContent = content.transform;
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;

            var layout = content.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewportRect;
            scroll.content = contentRect;
        }

        private static void ReloadTanks()
        {
            tanks.Clear();
            tanks.AddRange(TankDatabase.LoadTanks());

            if (tankListContent == null) return;

            foreach (Transform child in tankListContent)
                UnityEngine.Object.Destroy(child.gameObject);

            foreach (TankInfo tank in tanks)
            {
                TankInfo captured = tank;
                CreateButton(tankListContent, "Tank_" + tank.Name, tank.Name, Vector2.zero, () =>
                {
                    SelectedTank = captured;
                    LobbyManager.Instance.SelectTank(captured);
                }, 280, 36);
            }
        }

        private static void OnHostClicked()
        {
            lobbyPanel.SetActive(true);
            connectPanel.SetActive(false);
            LobbyManager.Instance.StartHost(GetCurrentPlayerName(), DefaultPort);
        }

        private static void OnJoinClicked()
        {
            connectPanel.SetActive(true);
            lobbyPanel.SetActive(false);
        }

        private static void OnActionClicked()
        {
            if (SelectedTank == null)
            {
                MelonLogger.Warning("[CustomBattleUI] Select a tank before ready/start.");
                return;
            }

            if (NetworkManager.Instance.IsHost)
                LobbyManager.Instance.StartMatch();
            else if (NetworkManager.Instance.IsClient)
                LobbyManager.Instance.SetReady(!GetLocalReadyState());

            Refresh();
        }

        private static bool GetLocalReadyState()
        {
            string name = NetworkManager.Instance?.LocalNickname;
            return !string.IsNullOrEmpty(name) &&
                   LobbyManager.Instance.Players.TryGetValue(name, out var player) &&
                   player.Ready;
        }

        private static string GetCurrentPlayerName()
        {
            string value = nameInput != null ? nameInput.text : PlayerName;
            if (string.IsNullOrWhiteSpace(value))
                value = PlayerName;

            PlayerName = value.Trim();
            return PlayerName;
        }

        private static string BuildRosterText()
        {
            var players = LobbyManager.Instance.Players.Values.ToList();
            if (players.Count == 0)
                return "No players.";

            return string.Join("\n", players.Select(p =>
            {
                string role = NetworkManager.Instance != null && NetworkManager.Instance.IsHost && p.Name == NetworkManager.Instance.HostNickname
                    ? "HOST"
                    : (p.Ready ? "READY" : "WAIT");
                return $"[{role}] {p.Name} - {p.TankName}";
            }));
        }

        private static string BuildStatusText()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsActiveMultiplayer)
                return "Disconnected";

            if (NetworkManager.Instance.IsHost)
                return $"Host on port {NetworkManager.Instance.CurrentPort}";

            return $"Client -> {NetworkManager.Instance.CurrentIP}:{NetworkManager.Instance.CurrentPort}";
        }

        private static string BuildActionText()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsActiveMultiplayer)
                return "READY";

            if (NetworkManager.Instance.IsHost)
            {
                int total = LobbyManager.Instance.Players.Count;
                int ready = LobbyManager.Instance.Players.Values.Count(p => p.Ready && !string.IsNullOrEmpty(p.TankHash));
                return total > 0 && ready == total ? "START" : $"WAIT ({ready}/{total})";
            }

            return GetLocalReadyState() ? "CANCEL READY" : "READY";
        }

        private static void LockWeatherControls()
        {
            GameObject mapObj = GameObject.Find("Root/Canvas/Content/Map");
            if (mapObj == null) return;

            try
            {
                var dropdowns = mapObj.GetComponentsInChildren<Il2CppDynamicGUI.DropDown>(true);
                for (int i = 1; i < dropdowns.Count; i++)
                {
                    dropdowns[i].SetHovered(0);
                    dropdowns[i].SetHoveredAsSelected();
                    var cg = dropdowns[i].gameObject.GetComponent<CanvasGroup>() ?? dropdowns[i].gameObject.AddComponent<CanvasGroup>();
                    cg.interactable = false;
                    cg.blocksRaycasts = false;
                    cg.alpha = 0.55f;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomBattleUI] Weather lock failed: {ex.Message}");
            }
        }

        private static GameObject CreatePanel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.08f, 0.08f, 0.08f, 0.92f);
            go.AddComponent<Outline>().effectColor = Color.black;
            return go;
        }

        private static TextMeshProUGUI CreateText(Transform parent, string name, string text, Vector2 pos, int size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            var rect = tmp.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 1);
            rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(360, 40);
            return tmp;
        }

        private static TMP_InputField CreateInput(Transform parent, string name, string text, Vector2 pos, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1);
            rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(width, 38);
            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.02f, 0.02f, 0.02f, 0.95f);

            var textGO = new GameObject("Text");
            textGO.transform.SetParent(go.transform, false);
            var tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = 18;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            var textRect = tmp.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8, 0);
            textRect.offsetMax = new Vector2(-8, 0);

            var input = go.AddComponent<TMP_InputField>();
            input.textComponent = tmp;
            input.targetGraphic = bg;
            input.text = text;
            return input;
        }

        private static GameObject CreateButton(Transform parent, string name, string label, Vector2 pos, Action onClick, float width = 190, float height = 44)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1);
            rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(width, height);

            var img = go.AddComponent<Image>();
            img.color = Color.white;
            var button = go.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = new Color(0.18f, 0.18f, 0.18f, 1f);
            colors.highlightedColor = new Color(0.28f, 0.28f, 0.28f, 1f);
            colors.pressedColor = new Color(0.10f, 0.10f, 0.10f, 1f);
            button.colors = colors;
            button.onClick.AddListener((UnityAction)(() => onClick?.Invoke()));

            var textGO = new GameObject("Text");
            textGO.transform.SetParent(go.transform, false);
            var tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 18;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            var textRect = tmp.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            return go;
        }
    }
}
