using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Il2CppTMPro;
using MelonLoader;
using SprocketMultiplayer.Core;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Exception = System.Exception;

namespace SprocketMultiplayer.Patches
{
    public static class Lobby
    {
        public static GameObject CanvasGO;
        public static GameObject Panel;

        private static GameObject mainMenu;
        private static Transform tankScrollContent;
        private static TextMeshProUGUI headerTMP;
        private static TextMeshProUGUI mapTextTMP;
        private static string hostLobbyName;

        public  static List<TextMeshProUGUI> PlayerSlots   = new List<TextMeshProUGUI>();
        private static readonly Dictionary<GameObject, string> tankButtonMap = new Dictionary<GameObject, string>();
        public  static readonly Dictionary<string, PlayerInfo> Players = new Dictionary<string, PlayerInfo>();

        private const int    MAX_PLAYERS = 4;
        private const string EMPTY       = "Empty Slot";

        public static string SelectedTank ;
        public static bool LobbyUIReady;
        public static string PendingSceneName;

        private static bool LobbyUICreated;
        private static List<string> pendingLobbyState;

        public class PlayerInfo
        {
            public string Tank;
            public int Ping;
        }

        private static bool IsReady() =>
            TMP_Settings.instance != null && TMP_Settings.defaultFontAsset != null;

        // =====================================================================
        // Entry points
        // =====================================================================

        // Retry helper — called when Instantiate() receives a null Menu Panel.
        private static IEnumerator WaitForMenuAndRetry()
        {
            MelonLogger.Msg("[Lobby] Waiting for Menu Panel...");
            GameObject menu = null;
            int tries = 0;
            while ((menu = GameObject.Find("Menu Panel")) == null && tries++ < 600)
                yield return null;

            if (menu == null) { MelonLogger.Warning("[Lobby] Menu Panel not found."); yield break; }
            if (Panel != null) yield break;

            Instantiate(menu);
        }

        // Called on the client side when the lobby UI doesn't exist yet but
        // the host has already sent a LOBBY_STATE packet.
        public static IEnumerator WaitForLobbyCanvasThenCreateUI(List<string> namesFromServer)
        {
            if (LobbyUICreated) yield break;
            LobbyUICreated = true;

            GameObject mainMenuGO = null;
            int tries = 0;
            while (mainMenuGO == null && tries++ < 400)
            {
                mainMenuGO = GameObject.Find("Menu Panel");
                yield return null;
            }

            if (mainMenuGO == null)
            {
                MelonLogger.Warning("[Lobby] Menu Panel not found — aborting.");
                LobbyUICreated = false;
                yield break;
            }

            int tmpTries = 0;
            while (!IsReady() && tmpTries++ < 600)
                yield return null;

            Instantiate(mainMenuGO);
            HandleIncomingLobbyState(namesFromServer);
            LobbyUICreated = false;
        }

        public static void Instantiate(GameObject mainMenuGO)
        {
            if (Panel != null || LobbyUIReady)
            {
                MelonLogger.Msg("[Lobby] Already instantiated.");
                return;
            }

            if (!NetworkManager.Instance.IsHost && !NetworkManager.Instance.IsClient)
            {
                MelonLogger.Msg("[Lobby] Not connected — aborting.");
                if (mainMenu != null) mainMenu.SetActive(true);
                return;
            }

            if (mainMenuGO == null)
            {
                MelonLogger.Warning("[Lobby] Menu Panel null — retrying...");
                MelonCoroutines.Start(WaitForMenuAndRetry());
                return;
            }

            mainMenu = mainMenuGO;
            mainMenu.SetActive(false);

            CreateLobbyUI();

            string localNickname = "Player";
            try { localNickname = MenuActions.GetSteamNickname(); } catch { }

            if (NetworkManager.Instance.IsHost)
            {
                headerTMP.text = $"{localNickname}'s Lobby";
                TryAddPlayer(localNickname);

                if (pendingLobbyState != null)
                {
                    ApplyLobbyState(pendingLobbyState);
                    pendingLobbyState = null;
                }
            }
        }

        // =====================================================================
        // UI creation
        // =====================================================================

        private static void CreateLobbyUI()
        {
            try
            {
                CanvasGO = new GameObject("LobbyCanvas");
                var canvas = CanvasGO.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                CanvasGO.AddComponent<CanvasScaler>();
                CanvasGO.AddComponent<GraphicRaycaster>();
                CanvasGO.transform.SetAsLastSibling();

                AddFooterText(CanvasGO);
                BuildPanel(CanvasGO);

                LobbyUIReady = true;
                MelonLogger.Msg("[Lobby] UI ready.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Lobby] CreateLobbyUI crash: {ex}");
            }
        }

        private static void BuildPanel(GameObject canvasGO)
        {
            Panel = new GameObject("LobbyRootPanel");
            Panel.transform.SetParent(canvasGO.transform, false);
            var rect = Panel.AddComponent<RectTransform>();
            rect.sizeDelta        = new Vector2(900, 500);
            rect.anchorMin        = new Vector2(0.5f, 0.5f);
            rect.anchorMax        = new Vector2(0.5f, 0.5f);
            rect.pivot            = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            Panel.AddComponent<Image>().color = new Color(0, 0, 0, 0.7f);

            var left   = CreateSubPanel("LeftPanel",   Panel.transform, new Vector2(0.35f, 1f), new Vector2(0.00f, 0));
            var center = CreateSubPanel("CenterPanel", Panel.transform, new Vector2(0.45f, 1f), new Vector2(0.35f, 0));
            var right  = CreateSubPanel("RightPanel",  Panel.transform, new Vector2(0.20f, 1f), new Vector2(0.80f, 0));

            try   { SetupLeftPanel(left.transform); }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Lobby] SetupLeftPanel crash: {ex}");
                AddPlaceholderText(left.transform, "Tank list unavailable.\nCheck logs.");
            }

            SetupCenterPanel(center.transform);
            SetupRightPanel(right.transform);
        }

        private static GameObject CreateSubPanel(string name, Transform parent, Vector2 widthPercent, Vector2 anchorMin)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = new Vector2(anchorMin.x + widthPercent.x, 1f);
            rect.offsetMin = new Vector2(10, 10);
            rect.offsetMax = new Vector2(-10, -10);
            go.AddComponent<Image>().color = new Color(0.2f, 0.2f, 0.2f, 0.6f);
            return go;
        }

        // =====================================================================
        // Left panel — tank selection
        // =====================================================================

        private static void SetupLeftPanel(Transform parent)
        {
            if (!Main.VehicleManager.CheckFaction(Main.GetPlayerFaction()))
            {
                AddPlaceholderText(parent, "Restricted faction.\nSelect AllowedVehicles.");
                return;
            }

            // Scroll view
            var scrollGO = new GameObject("TankScrollView");
            scrollGO.transform.SetParent(parent, false);
            var scrollRect = scrollGO.AddComponent<RectTransform>();
            scrollRect.anchorMin = Vector2.zero;
            scrollRect.anchorMax = Vector2.one;
            scrollRect.offsetMin = scrollRect.offsetMax = Vector2.zero;
            var scroll = scrollGO.AddComponent<ScrollRect>();

            // Viewport
            var viewGO = new GameObject("Viewport");
            viewGO.transform.SetParent(scrollGO.transform, false);
            viewGO.AddComponent<Mask>().showMaskGraphic = false;
            viewGO.AddComponent<Image>().color = new Color(0, 0, 0, 0.3f);
            var viewRect = viewGO.GetComponent<RectTransform>();
            viewRect.anchorMin = Vector2.zero;
            viewRect.anchorMax = Vector2.one;
            viewRect.offsetMin = viewRect.offsetMax = Vector2.zero;

            // Content
            var contentGO = new GameObject("Content");
            contentGO.transform.SetParent(viewGO.transform, false);
            tankScrollContent = contentGO.transform;
            var contentRect = contentGO.AddComponent<RectTransform>();
            contentRect.anchorMin        = new Vector2(0, 1);
            contentRect.anchorMax        = new Vector2(1, 1);
            contentRect.pivot            = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta        = Vector2.zero;

            var grid = contentGO.AddComponent<GridLayoutGroup>();
            grid.cellSize        = new Vector2(120, 120);
            grid.spacing         = new Vector2(10, 10);
            grid.constraint      = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 2;
            grid.childAlignment  = TextAnchor.UpperCenter;
            grid.startCorner     = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis       = GridLayoutGroup.Axis.Horizontal;
            contentGO.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.content    = contentRect;
            scroll.viewport   = viewRect;
            scroll.horizontal = false;
            scroll.vertical   = true;

            var tanks = TankDatabase.LoadTanks();
            if (tanks == null || tanks.Count == 0)
            {
                AddPlaceholderText(parent, "No tanks found.");
                return;
            }

            foreach (var tank in tanks)
            {
                try   { CreateTankEntry(tank, contentGO.transform); }
                catch (Exception ex) { MelonLogger.Error($"[Lobby] Tank entry '{tank?.Name}': {ex.Message}"); }
            }

            MelonLogger.Msg($"[Lobby] Tank list built ({tanks.Count} entries).");
        }

        private static void CreateTankEntry(TankInfo tank, Transform parent)
        {
            if (tank == null) return;

            var go = new GameObject(tank.Name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>().sizeDelta = new Vector2(120, 120);

            var bgImg = go.AddComponent<Image>();
            bgImg.color = new Color(0.15f, 0.15f, 0.15f, 0.9f);

            if (!string.IsNullOrEmpty(tank.ImagePath) && File.Exists(tank.ImagePath))
            {
                try
                {
                    byte[]    bytes = File.ReadAllBytes(tank.ImagePath);
                    Texture2D tex   = new Texture2D(2, 2);
                    tex.LoadImage(bytes);

                    var imgGO   = new GameObject("TankImage");
                    imgGO.transform.SetParent(go.transform, false);
                    var imgRect = imgGO.AddComponent<RectTransform>();
                    imgRect.anchorMin = Vector2.zero;
                    imgRect.anchorMax = Vector2.one;
                    imgRect.offsetMin = new Vector2(5, 5);
                    imgRect.offsetMax = new Vector2(-5, -5);

                    var tankImg = imgGO.AddComponent<Image>();
                    tankImg.sprite         = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                    tankImg.preserveAspect = true;
                    tankImg.raycastTarget  = false;
                }
                catch (Exception ex) { MelonLogger.Warning($"[Lobby] Tank image '{tank.Name}': {ex.Message}"); }
            }

            tankButtonMap[go] = tank.Name;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = bgImg;
            btn.transition    = Selectable.Transition.ColorTint;
            var colors = btn.colors;
            colors.normalColor      = new Color(0.15f, 0.15f, 0.15f, 0.9f);
            colors.highlightedColor = new Color(0.25f, 0.25f, 0.25f, 1f);
            colors.pressedColor     = new Color(0.35f, 0.35f, 0.35f, 1f);
            btn.colors = colors;
            btn.onClick.AddListener((UnityAction)(() => OnTankClicked(go)));
        }

        private static void OnTankClicked(GameObject tankButton)
        {
            if (!tankButtonMap.TryGetValue(tankButton, out string tankName)) return;

            SelectedTank = tankName;
            MelonLogger.Msg($"[Lobby] Tank selected: {tankName}");

            if (NetworkManager.Instance == null) return;

            string nickname = NetworkManager.Instance.LocalNickname;
            if (NetworkManager.Instance.IsHost)
                MultiplayerManager.Instance.SetPlayerTank(nickname, tankName);
            else
                NetworkManager.Instance.Send($"TANK_SELECT:{nickname}:{tankName}");
        }

        // =====================================================================
        // Center panel — player slots
        // =====================================================================

        private static void SetupCenterPanel(Transform parent)
        {
            // Header
            var headerGO = new GameObject("LobbyHeader");
            headerGO.transform.SetParent(parent, false);
            headerTMP           = headerGO.AddComponent<TextMeshProUGUI>();
            headerTMP.fontSize  = 28;
            headerTMP.alignment = TextAlignmentOptions.Center;
            headerTMP.color     = Color.white;
            var headerRect = headerTMP.rectTransform;
            headerRect.anchorMin        = new Vector2(0, 1);
            headerRect.anchorMax        = new Vector2(1, 1);
            headerRect.pivot            = new Vector2(0.5f, 1f);
            headerRect.sizeDelta        = new Vector2(0, 40);
            headerRect.anchoredPosition = new Vector2(0, -10);

            // Slots container
            var slotsContainer = new GameObject("SlotsContainer");
            slotsContainer.transform.SetParent(parent, false);
            var scRect = slotsContainer.AddComponent<RectTransform>();
            scRect.anchorMin = new Vector2(0, 0);
            scRect.anchorMax = new Vector2(1, 1);
            scRect.offsetMin = new Vector2(10, 10);
            scRect.offsetMax = new Vector2(-10, -60);
            scRect.pivot     = new Vector2(0.5f, 0.5f);

            var vlg = slotsContainer.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth      = true;
            vlg.childControlHeight     = false;
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 8;
            vlg.padding = new RectOffset(6, 6, 6, 6);
            slotsContainer.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            PlayerSlots.Clear();
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                var slot = CreatePlayerSlot($"Player{i + 1}: {EMPTY}");
                slot.transform.SetParent(slotsContainer.transform, false);
                var tmp = slot.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp == null) MelonLogger.Warning($"[Lobby] Slot {i + 1}: TMP is null.");
                PlayerSlots.Add(tmp);
            }
        }

        private static GameObject CreatePlayerSlot(string initialText)
        {
            var go = new GameObject("PlayerSlot");
            go.AddComponent<RectTransform>().sizeDelta = new Vector2(0, 48);
            go.AddComponent<Image>().color = new Color(0.12f, 0.12f, 0.12f, 0.85f);
            var layout = go.AddComponent<LayoutElement>();
            layout.preferredHeight = 48;
            layout.flexibleWidth   = 1;

            var textGO = new GameObject("SlotText");
            textGO.transform.SetParent(go.transform, false);

            TextMeshProUGUI tmp = null;
            if (IsReady())
            {
                try   { tmp = textGO.AddComponent<TextMeshProUGUI>(); }
                catch (Exception ex) { MelonLogger.Error($"[Lobby] TMP add failed: {ex.Message}"); }
            }

            if (tmp == null)
            {
                // TMP not ready — fallback to legacy Text so the slot still renders
                var fallback       = textGO.AddComponent<UnityEngine.UI.Text>();
                fallback.text      = initialText;
                fallback.fontSize  = 20;
                fallback.alignment = TextAnchor.MiddleCenter;
                fallback.color     = Color.white;
            }
            else
            {
                tmp.text      = initialText;
                tmp.fontSize  = 20;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color     = Color.white;
                var tr = tmp.rectTransform;
                tr.anchorMin = Vector2.zero;
                tr.anchorMax = Vector2.one;
                tr.offsetMin = new Vector2(8, 6);
                tr.offsetMax = new Vector2(-8, -6);
            }

            var trect = textGO.GetComponent<RectTransform>() ?? textGO.AddComponent<RectTransform>();
            trect.anchorMin = Vector2.zero;
            trect.anchorMax = Vector2.one;

            return go;
        }

        // =====================================================================
        // Right panel — map / start button
        // =====================================================================

        private static void SetupRightPanel(Transform parent)
        {
            // Title
            var titleGO  = new GameObject("MapTitle");
            titleGO.transform.SetParent(parent, false);
            var titleTMP = titleGO.AddComponent<TextMeshProUGUI>();
            titleTMP.text      = "Selected Map";
            titleTMP.fontSize  = 26;
            titleTMP.alignment = TextAlignmentOptions.Center;
            titleTMP.color     = Color.white;
            var titleRect = titleTMP.rectTransform;
            titleRect.anchorMin        = new Vector2(0, 1);
            titleRect.anchorMax        = new Vector2(1, 1);
            titleRect.pivot            = new Vector2(0.5f, 1);
            titleRect.sizeDelta        = new Vector2(0, 40);
            titleRect.anchoredPosition = new Vector2(0, -10);

            // Map name label
            var mapGO  = new GameObject("SelectedMapText");
            mapGO.transform.SetParent(parent, false);
            mapTextTMP           = mapGO.AddComponent<TextMeshProUGUI>();
            mapTextTMP.text      = "Railway";
            mapTextTMP.fontSize  = 22;
            mapTextTMP.alignment = TextAlignmentOptions.Center;
            mapTextTMP.color     = Color.white;
            var mapRect = mapTextTMP.rectTransform;
            mapRect.anchorMin        = new Vector2(0, 1);
            mapRect.anchorMax        = new Vector2(1, 1);
            mapRect.pivot            = new Vector2(0.5f, 1);
            mapRect.sizeDelta        = new Vector2(0, 30);
            mapRect.anchoredPosition = new Vector2(0, -60);

            if (!NetworkManager.Instance.IsHost) return;

            // Start button
            var btnGO = new GameObject("StartButton");
            btnGO.transform.SetParent(parent, false);
            btnGO.AddComponent<Button>().onClick.AddListener((UnityAction)OnStartButtonPressed);
            var img = btnGO.AddComponent<Image>();
            img.color = new Color(0.3f, 0.3f, 0.3f, 0.9f);
            var btnRect = btnGO.GetComponent<RectTransform>();
            btnRect.anchorMin        = new Vector2(0.2f, 0);
            btnRect.anchorMax        = new Vector2(0.8f, 0);
            btnRect.pivot            = new Vector2(0.5f, 0);
            btnRect.sizeDelta        = new Vector2(0, 40);
            btnRect.anchoredPosition = new Vector2(0, 20);

            var labelGO = new GameObject("StartButtonLabel");
            labelGO.transform.SetParent(btnGO.transform, false);
            var label = labelGO.AddComponent<TextMeshProUGUI>();
            label.text      = "Start";
            label.fontSize  = 22;
            label.color     = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            var labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
        }

        // =====================================================================
        // Start button
        // =====================================================================

        private static void OnStartButtonPressed()
        {
            MelonLogger.Msg("[Lobby] Host pressed Start.");

            if (string.IsNullOrEmpty(SelectedTank))
            {
                MelonLogger.Warning("[Lobby] No tank selected — using default.");
                VehicleSpawnHelper.EnsureInitialized();
                SelectedTank = VehicleSpawnHelper.GetDefaultTankId();
                if (!string.IsNullOrEmpty(SelectedTank) && MultiplayerManager.Instance != null)
                    MultiplayerManager.Instance.SetPlayerTank(NetworkManager.Instance.LocalNickname, SelectedTank);
            }

            NetworkManager.Instance.Send("MAP:Railway");
            MelonCoroutines.Start(LoadScene("Railway"));
        }

        // Loads via Main first so the game's DI pipeline (VehicleFactories, VehicleContext)
        // runs before Railway loads. Main.cs detects PendingSceneName and loads Railway
        // after Main has finished its own init.
        private static IEnumerator LoadScene(string sceneName)
        {
            MelonLogger.Msg($"[Lobby] Loading via Main → {sceneName}...");
            PendingSceneName = sceneName;
            SceneManager.LoadScene("Main");
            yield break;
        }

        // =====================================================================
        // Footer
        // =====================================================================

        private static void AddFooterText(GameObject canvasGO)
        {
            var footerGO = new GameObject("FooterText");
            footerGO.transform.SetParent(canvasGO.transform, false);
            var rect = footerGO.AddComponent<RectTransform>();
            rect.anchorMin        = new Vector2(0, 0);
            rect.anchorMax        = new Vector2(1, 0);
            rect.pivot            = new Vector2(0.5f, 0);
            rect.anchoredPosition = new Vector2(0, 50);
            rect.sizeDelta        = new Vector2(0, 40);
            footerGO.AddComponent<Image>().color = new Color(0, 0, 0, 0.5f);

            var textGO = new GameObject("FooterTextTMP");
            textGO.transform.SetParent(footerGO.transform, false);
            var tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text      = "-> Mod is still in development. Things may break! <-";
            tmp.fontSize  = 20;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = Color.white;
            var textRect = tmp.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
        }

        // =====================================================================
        // Placeholder text
        // =====================================================================

        private static void AddPlaceholderText(Transform parent, string text)
        {
            var textGO = new GameObject("PlaceholderText");
            textGO.transform.SetParent(parent, false);
            var tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text      = text;
            tmp.fontSize  = 28;
            tmp.alignment = TextAlignmentOptions.Center;
            var rect = tmp.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        // =====================================================================
        // Player management
        // =====================================================================

        public static bool TryAddPlayer(string nickname)
        {
            if (string.IsNullOrEmpty(nickname)) return false;
            if (Players.ContainsKey(nickname))  return false;

            int filled = 0;
            for (int i = 0; i < PlayerSlots.Count; i++)
            {
                string text = PlayerSlots[i]?.text;
                if (!string.IsNullOrEmpty(text) && text != $"Player{i + 1}: {EMPTY}")
                    filled++;
            }
            if (filled >= MAX_PLAYERS) return false;

            for (int i = 0; i < PlayerSlots.Count; i++)
            {
                string current = PlayerSlots[i]?.text;
                if (string.IsNullOrEmpty(current) || current == $"Player{i + 1}: {EMPTY}")
                {
                    Players[nickname]   = new PlayerInfo { Tank = "", Ping = 0 };
                    PlayerSlots[i].text = $"Player{i + 1}: {nickname}";
                    MelonLogger.Msg($"[Lobby] Added {nickname} to slot {i + 1}.");
                    return true;
                }
            }
            return false;
        }

        public static void RemovePlayer(string nickname)
        {
            if (string.IsNullOrEmpty(nickname)) return;
            for (int i = 0; i < PlayerSlots.Count; i++)
            {
                if (PlayerSlots[i]?.text?.Contains($": {nickname}") == true)
                {
                    PlayerSlots[i].text = $"Player{i + 1}: {EMPTY}";
                    MelonLogger.Msg($"[Lobby] Removed {nickname} from slot {i + 1}.");
                    return;
                }
            }
        }

        public static void SetPlayerTank(string nickname, string tankName)
        {
            if (!Players.TryGetValue(nickname, out var info)) return;
            info.Tank = tankName;
            RefreshPlayerSlot(nickname);
        }

        public static void UpdatePlayerPing(string nickname, int ping)
        {
            if (!Players.TryGetValue(nickname, out var info)) return;
            info.Ping = ping;
            RefreshPlayerSlot(nickname);
        }

        private static void RefreshPlayerSlot(string nickname)
        {
            for (int i = 0; i < PlayerSlots.Count; i++)
            {
                var slot = PlayerSlots[i];
                if (slot == null || !slot.text.Contains($": {nickname}")) continue;

                var    info = Players[nickname];
                string tank = string.IsNullOrEmpty(info.Tank) ? "" : $" — {info.Tank}";
                string ping = info.Ping > 0 ? $", {info.Ping} ms" : "";
                slot.text = $"Player{i + 1}: {nickname}{tank}{ping}";
                return;
            }
        }

        public static void OnPlayerConnected(string nickname)
        {
            if (!TryAddPlayer(nickname))
            {
                MelonLogger.Msg($"[Lobby] Cannot add {nickname}: full or already present.");
                return;
            }
            if (!Players.ContainsKey(nickname))
                Players[nickname] = new PlayerInfo { Tank = "", Ping = 0 };
            RefreshPlayerSlot(nickname);
        }

        public static void OnPlayerDisconnected(string nickname)
        {
            Players.Remove(nickname);
            RemovePlayer(nickname);
        }

        // =====================================================================
        // Lobby state sync (client side)
        // =====================================================================

        public static void HandleIncomingLobbyState(List<string> nicknames)
        {
            if (!LobbyUIReady || Panel == null || PlayerSlots == null || PlayerSlots.Count == 0)
            {
                pendingLobbyState = new List<string>(nicknames);
                return;
            }
            ApplyLobbyState(nicknames);
        }

        public static void ApplyLobbyState(List<string> nicknames)
        {
            if (!LobbyUIReady || Panel == null || PlayerSlots == null || PlayerSlots.Count == 0)
            {
                pendingLobbyState = new List<string>(nicknames);
                return;
            }

            if (nicknames.Count > 0 && !string.IsNullOrEmpty(nicknames[0]))
            {
                hostLobbyName = nicknames[0];
                if (headerTMP != null && !NetworkManager.Instance.IsHost)
                    headerTMP.text = $"{hostLobbyName}'s Lobby";
            }

            for (int i = 0; i < PlayerSlots.Count; i++)
            {
                string nick = i < nicknames.Count ? nicknames[i] : null;
                if (!string.IsNullOrEmpty(nick))
                {
                    if (!Players.ContainsKey(nick))
                        Players[nick] = new PlayerInfo { Tank = "", Ping = 0 };
                    PlayerSlots[i].text = $"Player{i + 1}: {nick}";
                }
                else
                {
                    PlayerSlots[i].text = $"Player{i + 1}: {EMPTY}";
                }
            }
        }
    }
}