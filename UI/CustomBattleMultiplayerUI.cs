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

        private static GameObject connectPanelObj;
        private static GameObject lobbyPanelObj;
        private static GameObject tankSelectPanelObj;
        private static TextMeshProUGUI rosterText;
        private static TextMeshProUGUI actionBtnText;
        private static TextMeshProUGUI previewNameText;
        private static Image previewImage;
        private static TMP_InputField nameInput;
        private static readonly List<GameObject> tankButtonObjects = new List<GameObject>();
        private static int tankPage;

        private static object mapMonitorCoroutine;
        private static string lastTankName = "None";
        private static string lastTankIconPath = "";
        private static int lastMapIndex = -1;

        private static readonly List<TankInfo> tanks = new List<TankInfo>();
        private static readonly string[] sceneNames = { "Railway" };

        public static TankInfo SelectedTank { get; private set; }

        public static string PlayerName
        {
            get => PlayerPrefs.GetString("SprocketMP.PlayerName", "Player" + UnityEngine.Random.Range(1000, 9999));
            private set => PlayerPrefs.SetString("SprocketMP.PlayerName", value);
        }

        public static bool IsInjected =>
            GameObject.Find("Root/Canvas/Content/Team config/Btn_Host_Injected") != null;

        public static bool ShouldInject()
        {
            return !IsInjected &&
                   GameObject.Find("Root/Canvas/Content/Team config") != null &&
                   GameObject.Find("Root/Canvas/Content/Map") != null;
        }

        public static void Inject()
        {
            InjectMultiplayerOptions();
        }

        public static void InjectMultiplayerOptions()
        {
            GameObject contentObj = GameObject.Find("Root/Canvas/Content/Team config");
            if (contentObj == null || contentObj.transform.Find("Btn_Host_Injected") != null)
                return;

            LockWeatherControls();
            ReloadTanks();

            CreateTextTMP(contentObj.transform, "NameLabel_Injected", "PLAYER NAME:", new Vector2(0, 150), 18, TextAlignmentOptions.Center);

            nameInput = CreateInputFieldTMP(contentObj.transform, "NameInput_Injected", PlayerName, new Vector2(0, 110), 250);
            nameInput.onEndEdit.AddListener((UnityAction<string>)(val =>
            {
                if (!string.IsNullOrWhiteSpace(val))
                    PlayerName = val.Trim();
            }));

            CreateNativeStyleButton(contentObj.transform, "Btn_Host_Injected", "HOST MULTIPLAYER", new Vector2(0, 50), OnHostClicked);
            CreateNativeStyleButton(contentObj.transform, "Btn_Connect_Injected", "CONNECT TO HOST", new Vector2(0, 0), OnConnectClicked);

            if (mapMonitorCoroutine == null)
                mapMonitorCoroutine = MelonCoroutines.Start(MonitorMapCoroutine());

            MelonLogger.Msg("[Multiplayer] Injected old-style Custom Battle multiplayer UI.");
        }

        private static IEnumerator MonitorMapCoroutine()
        {
            while (true)
            {
                if (NetworkManager.Instance != null && NetworkManager.Instance.IsHost)
                {
                    GameObject mapObj = GameObject.Find("Root/Canvas/Content/Map");
                    if (mapObj != null)
                    {
                        var dropdowns = mapObj.GetComponentsInChildren<Il2CppDynamicGUI.DropDown>();
                        if (dropdowns != null && dropdowns.Count >= 1)
                        {
                            int mapIndex = dropdowns[0].SelectedIndex;
                            if (mapIndex != lastMapIndex)
                            {
                                lastMapIndex = mapIndex;
                                ApplyMapSelection(mapIndex);
                                LobbyManager.Instance.SetMap(mapIndex, ResolveSceneName(mapIndex));
                            }
                        }
                    }
                }

                LockWeatherControls();
                yield return new WaitForSeconds(0.5f);
            }
        }

        public static void ApplyMapSelection(int index)
        {
            if (index < 0)
                index = 0;

            GameObject mapObj = GameObject.Find("Root/Canvas/Content/Map");
            if (mapObj != null)
            {
                var dropdowns = mapObj.GetComponentsInChildren<Il2CppDynamicGUI.DropDown>();
                if (dropdowns != null && dropdowns.Count >= 1 && dropdowns[0].SelectedIndex != index)
                {
                    dropdowns[0].SetHovered(index);
                    dropdowns[0].SetHoveredAsSelected();
                }
            }

            LobbyManager.Instance.SelectedMapIndex = index;
            LobbyManager.Instance.SelectedMap = ResolveSceneName(index);
            Refresh();
        }

        private static string ResolveSceneName(int index)
        {
            if (index >= 0 && index < sceneNames.Length)
                return sceneNames[index];

            return "Railway";
        }

        public static void Refresh()
        {
            RefreshLobbyData();
        }

        public static void ShowNativeUI()
        {
            ForceCloseAndShowNative();
        }

        public static void CloseForSceneLoad()
        {
            CloseAllUIForBattle();
        }

        private static void OnHostClicked()
        {
            HideNativeUI();
            LobbyManager.Instance.StartHost(GetCurrentPlayerName(), DefaultPort);
            SyncSelectedTankToLobby();
            DrawLobbyPanel();
            RefreshLobbyData();
        }

        private static void OnConnectClicked()
        {
            HideNativeUI();
            DrawConnectPanel();
        }

        private static void OnJoinConfirmClicked(string ip)
        {
            if (!LobbyManager.Instance.JoinHost(GetCurrentPlayerName(), ip, DefaultPort))
            {
                MelonLogger.Warning("[Multiplayer] Connection failed.");
                return;
            }

            if (connectPanelObj != null)
                connectPanelObj.SetActive(false);

            DrawLobbyPanel();
            LockClientBattleConfigUI();
            SyncSelectedTankToLobby();
            RefreshLobbyData();
        }

        private static void OnActionClicked()
        {
            if (SelectedTank == null)
            {
                MelonLogger.Warning("[Multiplayer] SELECT A TANK FIRST!");
                return;
            }

            if (NetworkManager.Instance != null && NetworkManager.Instance.IsHost)
                LobbyManager.Instance.StartMatch();
            else if (NetworkManager.Instance != null && NetworkManager.Instance.IsClient)
                LobbyManager.Instance.SetReady(!GetLocalReadyState());

            RefreshLobbyData();
        }

        private static void DrawConnectPanel()
        {
            GameObject contentObj = GameObject.Find("Root/Canvas/Content");
            if (contentObj == null) return;

            if (connectPanelObj == null)
            {
                connectPanelObj = new GameObject("MP_ConnectPanel");
                connectPanelObj.transform.SetParent(contentObj.transform, false);

                RectTransform rect = connectPanelObj.AddComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.2f, 0.08f);
                rect.anchorMax = new Vector2(0.8f, 0.92f);
                rect.sizeDelta = Vector2.zero;
                rect.anchoredPosition = Vector2.zero;

                Image bgImg = connectPanelObj.AddComponent<Image>();
                bgImg.color = new Color(0.12f, 0.12f, 0.12f, 0.98f);
                connectPanelObj.AddComponent<Outline>().effectColor = Color.black;

                CreateTextTMP(connectPanelObj.transform, "Title", "JOIN SERVER", new Vector2(0, 200), 36, TextAlignmentOptions.Center);

                CreateNativeStyleButton(connectPanelObj.transform, "Btn_JoinLocal", "JOIN 127.0.0.1", new Vector2(0, 50), () => OnJoinConfirmClicked("127.0.0.1"));

                CreateNativeStyleButton(connectPanelObj.transform, "Btn_Cancel", "CANCEL", new Vector2(0, -100), () =>
                {
                    connectPanelObj.SetActive(false);
                    ShowNativeOnly();
                });
            }

            connectPanelObj.SetActive(true);
        }

        private static void DrawLobbyPanel()
        {
            GameObject contentObj = GameObject.Find("Root/Canvas/Content");
            if (contentObj == null) return;

            if (lobbyPanelObj == null)
            {
                lobbyPanelObj = new GameObject("MP_LobbyPanel");
                lobbyPanelObj.transform.SetParent(contentObj.transform, false);

                RectTransform rect = lobbyPanelObj.AddComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.2f, 0.08f);
                rect.anchorMax = new Vector2(0.8f, 0.92f);
                rect.sizeDelta = Vector2.zero;
                rect.anchoredPosition = Vector2.zero;

                Image bgImg = lobbyPanelObj.AddComponent<Image>();
                bgImg.color = new Color(0.12f, 0.12f, 0.12f, 0.98f);
                lobbyPanelObj.AddComponent<Outline>().effectColor = Color.black;

                CreateTextTMP(lobbyPanelObj.transform, "Title", "MULTIPLAYER ROSTER", new Vector2(0, 300), 40, TextAlignmentOptions.Center);

                GameObject listBgObj = new GameObject("ListBg");
                listBgObj.transform.SetParent(lobbyPanelObj.transform, false);

                RectTransform listBgRect = listBgObj.AddComponent<RectTransform>();
                listBgRect.anchoredPosition = new Vector2(-200, 50);
                listBgRect.sizeDelta = new Vector2(500, 400);

                Image listImg = listBgObj.AddComponent<Image>();
                listImg.color = new Color(0.05f, 0.05f, 0.05f, 0.9f);
                listBgObj.AddComponent<Outline>().effectColor = Color.black;

                rosterText = CreateTextTMP(listBgObj.transform, "RosterText", "Loading...", new Vector2(0, 0), 24, TextAlignmentOptions.TopLeft);
                rosterText.rectTransform.sizeDelta = new Vector2(460, 360);

                GameObject previewObj = new GameObject("TankPreview");
                previewObj.transform.SetParent(lobbyPanelObj.transform, false);

                RectTransform previewRect = previewObj.AddComponent<RectTransform>();
                previewRect.anchorMin = new Vector2(0.5f, 0.5f);
                previewRect.anchorMax = new Vector2(0.5f, 0.5f);
                previewRect.anchoredPosition = new Vector2(300, 50);
                previewRect.sizeDelta = new Vector2(250, 140);

                Image prevBgImg = previewObj.AddComponent<Image>();
                prevBgImg.color = new Color(0.05f, 0.05f, 0.05f, 1f);
                previewObj.AddComponent<Outline>().effectColor = Color.black;

                GameObject imgObj = new GameObject("Image");
                imgObj.transform.SetParent(previewObj.transform, false);

                RectTransform imgRect = imgObj.AddComponent<RectTransform>();
                imgRect.anchoredPosition = Vector2.zero;
                imgRect.sizeDelta = new Vector2(240, 130);

                previewImage = imgObj.AddComponent<Image>();
                previewImage.preserveAspect = true;

                previewNameText = CreateTextTMP(lobbyPanelObj.transform, "PreviewName", "No Tank Selected", new Vector2(300, -40), 22, TextAlignmentOptions.Center);
                previewNameText.rectTransform.sizeDelta = new Vector2(250, 40);
                previewNameText.enableWordWrapping = false;
                previewNameText.overflowMode = TextOverflowModes.Ellipsis;

                string btnTxt = NetworkManager.Instance != null && NetworkManager.Instance.IsHost ? "START MATCH" : "READY";
                GameObject actionBtn = CreateNativeStyleButton(lobbyPanelObj.transform, "Btn_Action", btnTxt, new Vector2(300, -110), OnActionClicked);
                actionBtnText = actionBtn.GetComponentInChildren<TextMeshProUGUI>();

                CreateNativeStyleButton(lobbyPanelObj.transform, "Btn_Disconnect", "DISCONNECT", new Vector2(300, -210), OnDisconnectClicked);
            }

            DrawTankSelectPanel(contentObj.transform);
            lobbyPanelObj.SetActive(true);
            UpdateTankPreview();
        }

        private static void DrawTankSelectPanel(Transform contentTransform)
        {
            if (tankSelectPanelObj == null)
            {
                tankSelectPanelObj = new GameObject("MP_TankSelectPanel");
                tankSelectPanelObj.transform.SetParent(contentTransform, false);

                RectTransform rect = tankSelectPanelObj.AddComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.2f, 0.08f);
                rect.anchorMax = new Vector2(0.2f, 0.92f);
                rect.pivot = new Vector2(1f, 0.5f);
                rect.sizeDelta = new Vector2(384f, 0f);
                rect.anchoredPosition = Vector2.zero;

                Image bgImg = tankSelectPanelObj.AddComponent<Image>();
                bgImg.color = new Color(0.05f, 0.05f, 0.05f, 0.96f);
                tankSelectPanelObj.AddComponent<Outline>().effectColor = Color.black;

                CreateTankGrid(tankSelectPanelObj.transform);
            }

            tankSelectPanelObj.SetActive(true);
        }

        private static void CreateTankGrid(Transform parent)
        {
            tankButtonObjects.Clear();

            const int columns = 2;
            const int rows = 10;
            const float cellWidth = 178f;
            const float cellHeight = 58f;
            const float gapX = 8f;
            const float gapY = 5f;
            const int pageSize = columns * rows;
            float startX = -((columns - 1) * (cellWidth + gapX)) / 2f;
            float startY = 280f;

            TextMeshProUGUI title = CreateTextTMP(parent, "TankListTitle", "SELECT TANK", new Vector2(0, 345), 18, TextAlignmentOptions.Center);
            title.rectTransform.sizeDelta = new Vector2(360, 32);

            if (tanks.Count == 0)
            {
                TextMeshProUGUI emptyText = CreateTextTMP(parent, "TankListEmpty", "No blueprint files found.", new Vector2(0, 0), 18, TextAlignmentOptions.Center);
                emptyText.rectTransform.sizeDelta = new Vector2(340, 80);
                return;
            }

            int pageCount = Math.Max(1, (tanks.Count + pageSize - 1) / pageSize);
            if (tankPage >= pageCount) tankPage = pageCount - 1;
            if (tankPage < 0) tankPage = 0;

            int start = tankPage * pageSize;
            int max = Math.Min(tanks.Count, start + pageSize);
            for (int i = start; i < max; i++)
            {
                TankInfo tank = tanks[i];
                TankInfo captured = tank;
                int slot = i - start;
                int row = slot / columns;
                int column = slot % columns;
                Vector2 pos = new Vector2(startX + column * (cellWidth + gapX), startY - row * (cellHeight + gapY));
                tankButtonObjects.Add(CreateTankButton(parent, "Tank_" + i, tank, pos, new Vector2(cellWidth, cellHeight), () => SelectTank(captured)));
            }

            if (pageCount > 1)
            {
                CreateSmallButton(parent, "Btn_TankPagePrev", "<", new Vector2(-95, -345), new Vector2(72, 34), () =>
                {
                    if (tankPage <= 0) return;
                    tankPage--;
                    RebuildTankGrid();
                });

                TextMeshProUGUI pageText = CreateTextTMP(parent, "TankPageText", $"{tankPage + 1}/{pageCount}", new Vector2(0, -345), 14, TextAlignmentOptions.Center);
                pageText.rectTransform.sizeDelta = new Vector2(80, 34);

                CreateSmallButton(parent, "Btn_TankPageNext", ">", new Vector2(95, -345), new Vector2(72, 34), () =>
                {
                    if (tankPage >= pageCount - 1) return;
                    tankPage++;
                    RebuildTankGrid();
                });
            }
        }

        private static void RebuildTankGrid()
        {
            if (tankSelectPanelObj == null) return;

            Transform parent = tankSelectPanelObj.transform;
            for (int i = parent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);

            CreateTankGrid(parent);
        }

        private static GameObject CreateTankButton(Transform parent, string name, TankInfo tank, Vector2 pos, Vector2 size, Action onClick)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            RectTransform rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;

            Image img = obj.AddComponent<Image>();
            img.color = new Color(0.16f, 0.16f, 0.16f, 1f);

            Button btn = obj.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.normalColor = new Color(0.16f, 0.16f, 0.16f, 1f);
            cb.highlightedColor = new Color(0.28f, 0.28f, 0.28f, 1f);
            cb.pressedColor = new Color(0.1f, 0.1f, 0.1f, 1f);
            cb.selectedColor = cb.normalColor;
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.1f;
            btn.colors = cb;
            btn.onClick.AddListener((UnityAction)(() => onClick?.Invoke()));

            obj.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);

            GameObject iconObj = new GameObject("Icon");
            iconObj.transform.SetParent(obj.transform, false);

            RectTransform iconRect = iconObj.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0.5f, 1f);
            iconRect.anchorMax = new Vector2(0.5f, 1f);
            iconRect.anchoredPosition = new Vector2(0, -22);
            iconRect.sizeDelta = new Vector2(160, 36);

            Image iconImg = iconObj.AddComponent<Image>();
            iconImg.preserveAspect = true;
            Sprite sprite = LoadSpriteFromFile(tank.ImagePath);
            if (sprite != null)
            {
                iconImg.sprite = sprite;
                iconImg.color = Color.white;
            }
            else
            {
                iconImg.color = new Color(0, 0, 0, 0);
            }

            GameObject textObj = new GameObject("Txt");
            textObj.transform.SetParent(obj.transform, false);

            RectTransform textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(4, 2);
            textRect.offsetMax = new Vector2(-4, -38);

            TextMeshProUGUI tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = tank.Name;
            tmp.fontSize = 11;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Bottom;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;

            return obj;
        }

        private static GameObject CreateSmallButton(Transform parent, string name, string text, Vector2 pos, Vector2 size, Action onClick)
        {
            GameObject obj = CreateNativeStyleButton(parent, name, text, pos, onClick);
            RectTransform rect = obj.GetComponent<RectTransform>();
            if (rect != null)
                rect.sizeDelta = size;

            TextMeshProUGUI tmp = obj.GetComponentInChildren<TextMeshProUGUI>();
            if (tmp != null)
            {
                tmp.fontSize = 16;
                tmp.rectTransform.sizeDelta = size;
            }

            return obj;
        }

        private static void SelectTank(TankInfo tank)
        {
            SelectedTank = tank;
            lastTankName = tank?.Name ?? "None";
            lastTankIconPath = tank?.ImagePath ?? "";
            LobbyManager.Instance.SelectTank(tank);
            UpdateTankPreview();
            RefreshLobbyData();
        }

        private static void SyncSelectedTankToLobby()
        {
            if (SelectedTank == null || NetworkManager.Instance == null)
                return;

            if (!NetworkManager.Instance.IsHost && !NetworkManager.Instance.IsClient)
                return;

            LobbyManager.Instance.SelectTank(SelectedTank);
        }

        private static void UpdateTankPreview()
        {
            if (previewNameText != null)
                previewNameText.text = lastTankName == "None" ? "<color=#FF0000>No Tank Selected</color>" : lastTankName;

            if (previewImage == null) return;

            Sprite sp = LoadSpriteFromFile(lastTankIconPath);
            if (sp != null)
            {
                previewImage.sprite = sp;
                previewImage.color = Color.white;
            }
            else
            {
                previewImage.sprite = null;
                previewImage.color = new Color(0, 0, 0, 0);
            }
        }

        private static Sprite LoadSpriteFromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                byte[] fileData = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Bilinear;
                UnityEngine.ImageConversion.LoadImage(tex, fileData);

                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            }
            catch
            {
                return null;
            }
        }

        public static void RefreshLobbyData()
        {
            if (rosterText != null)
            {
                string listStr = "";
                foreach (var p in LobbyManager.Instance.Players.Values)
                {
                    string readyIcon = p.Ready ? "<color=#00FF00>[READY]</color>" : "<color=#FF0000>[WAITING]</color>";

                    if (NetworkManager.Instance != null && NetworkManager.Instance.IsHost && p.Name == NetworkManager.Instance.HostNickname)
                        readyIcon = "<color=#FFAA00>[HOST]</color>";

                    listStr += $"{readyIcon} {p.Name} - <size=18>{p.TankName}</size>\n";
                }
                rosterText.text = string.IsNullOrEmpty(listStr) ? "No players." : listStr;
            }

            if (actionBtnText != null)
            {
                if (NetworkManager.Instance != null && NetworkManager.Instance.IsHost)
                {
                    int total = LobbyManager.Instance.Players.Count;
                    int ready = LobbyManager.Instance.Players.Values.Count(p => p.Ready && !string.IsNullOrEmpty(p.TankHash));
                    actionBtnText.text = total > 0 && ready == total ? "START MATCH" : $"WAITING ({ready}/{total})";
                }
                else
                {
                    actionBtnText.text = GetLocalReadyState() ? "CANCEL READY" : "READY";
                }
            }
        }

        private static void OnDisconnectClicked()
        {
            LobbyManager.Instance.LeaveLobby();

            if (lobbyPanelObj != null)
                lobbyPanelObj.SetActive(false);
            if (tankSelectPanelObj != null)
                tankSelectPanelObj.SetActive(false);

            ShowNativeOnly();
        }

        private static TMP_InputField CreateInputFieldTMP(Transform parent, string name, string defaultText, Vector2 pos, float width)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            RectTransform rect = obj.AddComponent<RectTransform>();
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(width, 40);

            Image bg = obj.AddComponent<Image>();
            bg.color = new Color(0.1f, 0.1f, 0.1f, 1f);
            obj.AddComponent<Outline>().effectColor = Color.black;

            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(obj.transform, false);

            RectTransform textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10, 0);
            textRect.offsetMax = new Vector2(-10, 0);

            TextMeshProUGUI tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = 20;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;

            TMP_InputField input = obj.AddComponent<TMP_InputField>();
            input.textComponent = tmp;
            input.targetGraphic = bg;
            input.interactable = true;
            input.text = defaultText;

            if (UnityEngine.EventSystems.EventSystem.current == null)
            {
                GameObject esObj = new GameObject("EventSystem_Injected");
                UnityEngine.Object.DontDestroyOnLoad(esObj);
                esObj.AddComponent<UnityEngine.EventSystems.EventSystem>();
                esObj.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
            }

            return input;
        }

        private static GameObject CreateNativeStyleButton(Transform parent, string name, string text, Vector2 pos, Action onClick)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            RectTransform rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(250, 50);

            Image img = obj.AddComponent<Image>();
            img.color = Color.white;

            Button btn = obj.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.normalColor = new Color(0.18f, 0.18f, 0.18f, 1f);
            cb.highlightedColor = new Color(0.28f, 0.28f, 0.28f, 1f);
            cb.pressedColor = new Color(0.1f, 0.1f, 0.1f, 1f);
            cb.selectedColor = cb.normalColor;
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.1f;
            btn.colors = cb;

            btn.onClick.AddListener((UnityAction)(() => onClick?.Invoke()));

            obj.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);

            GameObject textObj = new GameObject("Txt");
            textObj.transform.SetParent(obj.transform, false);

            RectTransform textRect = textObj.AddComponent<RectTransform>();
            textRect.anchoredPosition = Vector2.zero;
            textRect.sizeDelta = rect.sizeDelta;

            TextMeshProUGUI tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 20;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;

            return obj;
        }

        private static TextMeshProUGUI CreateTextTMP(Transform parent, string name, string text, Vector2 pos, float size, TextAlignmentOptions align)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            RectTransform rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(600, 80);

            TextMeshProUGUI tmp = obj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = Color.white;
            tmp.alignment = align;

            return tmp;
        }

        private static readonly string[] PathsToHide =
        {
            "Root/Canvas/Content/Roster",
            "Root/Canvas/Content/Units",
            "Root/Canvas/Content/Unit config",
            "Root/Canvas/Content/Team config",
            "Root/Canvas/Content/Teams",
            "Root/Canvas/Content/Bar",
            "Root/Canvas/Content/Map/Confirm",
            "Root/Canvas/Content/Team config/NameLabel_Injected",
            "Root/Canvas/Content/Team config/NameInput_Injected"
        };

        private static void HideNativeUI()
        {
            foreach (string path in PathsToHide)
            {
                GameObject targetObj = GameObject.Find(path);
                if (targetObj != null)
                    targetObj.SetActive(false);
            }

            LockWeatherControls();
        }

        private static void ShowNativeOnly()
        {
            foreach (string path in PathsToHide)
            {
                Transform targetTransform = GameObject.Find("Root/Canvas/Content")?.transform.Find(path.Replace("Root/Canvas/Content/", ""));
                if (targetTransform != null)
                    targetTransform.gameObject.SetActive(true);
            }

            UnlockClientBattleConfigUI();
            LockWeatherControls();
        }

        public static void ForceCloseAndShowNative()
        {
            if (connectPanelObj != null) connectPanelObj.SetActive(false);
            if (lobbyPanelObj != null) lobbyPanelObj.SetActive(false);
            if (tankSelectPanelObj != null) tankSelectPanelObj.SetActive(false);
            ShowNativeOnly();
        }

        public static void CloseAllUIForBattle()
        {
            if (connectPanelObj != null) connectPanelObj.SetActive(false);
            if (lobbyPanelObj != null) lobbyPanelObj.SetActive(false);
            if (tankSelectPanelObj != null) tankSelectPanelObj.SetActive(false);
        }

        public static void LockClientBattleConfigUI()
        {
            GameObject mapObj = GameObject.Find("Root/Canvas/Content/Map");
            if (mapObj != null)
            {
                var cg = mapObj.GetComponent<CanvasGroup>() ?? mapObj.AddComponent<CanvasGroup>();
                cg.interactable = false;
                cg.blocksRaycasts = false;
                cg.alpha = 0.6f;
            }
        }

        private static void UnlockClientBattleConfigUI()
        {
            GameObject mapObj = GameObject.Find("Root/Canvas/Content/Map");
            if (mapObj == null) return;

            var cg = mapObj.GetComponent<CanvasGroup>();
            if (cg == null) return;

            cg.interactable = true;
            cg.blocksRaycasts = true;
            cg.alpha = 1f;
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

        private static void ReloadTanks()
        {
            tanks.Clear();
            tanks.AddRange(TankDatabase.LoadTanks());
        }

        private static string GetCurrentPlayerName()
        {
            string value = nameInput != null ? nameInput.text : PlayerName;
            if (string.IsNullOrWhiteSpace(value))
                value = PlayerName;

            PlayerName = value.Trim();
            return PlayerName;
        }

        private static bool GetLocalReadyState()
        {
            string name = NetworkManager.Instance?.LocalNickname;
            return !string.IsNullOrEmpty(name) &&
                   LobbyManager.Instance.Players.TryGetValue(name, out var player) &&
                   player.Ready;
        }
    }
}
