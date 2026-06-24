using System;
using System.Collections;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using SprocketMultiplayer.Core;
using SprocketMultiplayer.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SprocketMultiplayer
{
    public class Main : MelonMod
    {
        public static string GetPlayerFaction() => "AllowedVehicles";

        private static NetworkManager network;
        private bool consoleSpawned;
        private string lastSceneName = "";
        private float nextCustomBattleProbeTime;

        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("========================================");
            MelonLogger.Msg("Sprocket Multiplayer Mod Initializing...");
            MelonLogger.Msg("========================================");

            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<UI.Console>();
                MelonLogger.Msg("[Init] Registered IL2CPP types.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Init] Failed to register IL2CPP types: {ex.Message}");
            }

            try
            {
                new HarmonyLib.Harmony("qiany.sprocketmultiplayer").PatchAll(typeof(Main).Assembly);
                MelonLogger.Msg("[Init] Harmony patches installed.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Init] Harmony patch install failed: {ex.Message}");
            }

            try
            {
                network = new NetworkManager();
                MelonLogger.Msg("[Init] NetworkManager initialized.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Init] NetworkManager initialization failed: {ex.Message}");
                network = null;
            }

            MelonLogger.Msg("[Init] CustomBattle multiplayer flow enabled.");
            MelonLogger.Msg("========================================");
        }

        public static class VehicleManager
        {
            private const string AllowedFaction = "AllowedVehicles";

            public static bool CheckFaction(string playerFaction)
            {
                if (playerFaction == AllowedFaction) return true;
                MelonLogger.Msg("[VehicleManager] Faction not allowed. Select AllowedVehicles to pick a tank.");
                return false;
            }
        }

        public override void OnUpdate()
        {
            network?.PollEvents();

            if (!consoleSpawned)
                TrySpawnConsole();

            CheckSceneChange();
            ProbeCustomBattleUi();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName == "CustomBattleCreation")
            {
                MelonLogger.Msg("[SceneLoad] CustomBattleCreation loaded. Waiting to inject multiplayer UI...");
                MelonCoroutines.Start(InjectCustomBattleUiAfterDelay());
            }
        }

        private void CheckSceneChange()
        {
            var activeScene = SceneManager.GetActiveScene();
            if (activeScene.name == lastSceneName || string.IsNullOrEmpty(activeScene.name))
                return;

            string oldScene = lastSceneName;
            lastSceneName = activeScene.name;
            OnSceneChanged(activeScene, oldScene);
        }

        private void OnSceneChanged(Scene scene, string oldScene)
        {
            MelonLogger.Msg("========================================");
            MelonLogger.Msg($"SCENE CHANGED: {scene.name}");
            MelonLogger.Msg("========================================");
            SpawnSummaryLog.Info($"sceneChanged old={oldScene} new={scene.name}");

            VehicleSpawnHelper.ClearCaches();

            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsActiveMultiplayer)
            {
                SpawnSummaryLog.Info("sceneChanged multiplayer=no");
                return;
            }

            if (LobbyManager.Instance.MatchLoading)
            {
                MelonLogger.Msg("[SceneLoad] Multiplayer scene loaded. Starting spawn dependency sniff.");
                SpawnSummaryLog.Info($"sceneChanged multiplayer=yes matchLoading=yes scene={scene.name}");
                MultiplayerManager.Instance?.PrepareForMultiplayerSceneLoad();
                SpawnDependencySniffer.Start();
            }
            else
            {
                SpawnSummaryLog.Info($"sceneChanged multiplayer=yes matchLoading=no scene={scene.name}");
            }
        }

        private IEnumerator InjectCustomBattleUiAfterDelay()
        {
            yield return new WaitForSeconds(0.5f);
            if (CustomBattleMultiplayerUI.ShouldInject())
                CustomBattleMultiplayerUI.Inject();
        }

        private void ProbeCustomBattleUi()
        {
            if (Time.unscaledTime < nextCustomBattleProbeTime)
                return;

            nextCustomBattleProbeTime = Time.unscaledTime + 0.5f;

            if (CustomBattleMultiplayerUI.ShouldInject())
            {
                MelonLogger.Msg("[CustomBattleUI] Custom battle UI detected by probe.");
                CustomBattleMultiplayerUI.Inject();
            }
        }

        private void TrySpawnConsole()
        {
            try
            {
                var consoleGO = new GameObject("SprocketConsole");
                GameObject.DontDestroyOnLoad(consoleGO);

                var comp = consoleGO.AddComponent(Il2CppType.From(typeof(UI.Console)))?.Cast<UI.Console>();
                if (comp != null)
                {
                    MelonLogger.Msg("Console spawned successfully.");
                    consoleSpawned = true;
                }
                else
                {
                    MelonLogger.Error("Failed to attach Console component.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Failed to spawn console: {ex.Message}");
            }
        }

        public override void OnApplicationQuit()
        {
            MelonLogger.Msg("Sprocket Multiplayer shutting down...");
            network?.Shutdown();
            MelonLogger.Msg("[Shutdown] Sprocket Multiplayer shut down.");
        }
    }
}
