using System;
using MelonLoader;
using UnityEngine;
using System.Collections;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using SprocketMultiplayer.Core;
using SprocketMultiplayer.UI;
using SprocketMultiplayer.Patches;
using UnityEngine.SceneManagement;
using Il2CppSprocket;

namespace SprocketMultiplayer
{
    public class Main : MelonMod
    {
        public static string GetPlayerFaction() => "AllowedVehicles";

        private static NetworkManager network;
        private bool consoleSpawned = false;
        private string lastSceneName = "";

        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("========================================");
            MelonLogger.Msg("Sprocket Multiplayer Mod Initializing...");
            MelonLogger.Msg("========================================");

            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<Menu.HandleClicks>();
                ClassInjector.RegisterTypeInIl2Cpp<UI.Console>();
                MelonLogger.Msg("✓ IL2CPP types registered");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Failed to register IL2CPP types: {ex.Message}");
            }

            try
            {
                network = new NetworkManager();
                MelonLogger.Msg("✓ NetworkManager initialized");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"NetworkManager initialization failed: {ex.Message}");
                network = null;
            }

            // PatchAll() picks up SceneStartPatch via its [HarmonyPatch] attribute.
            // We also apply it manually in a separate Harmony instance so a failure
            // in another patch class can't prevent SceneStartPatch from loading.
            try
            {
                new HarmonyLib.Harmony("SprocketMultiplayer").PatchAll();
                MelonLogger.Msg("✓ Harmony patches applied");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Some Harmony patches failed: {ex.Message}");
            }

            try
            {
                var harmony  = new HarmonyLib.Harmony("SprocketMultiplayer.SceneStart");
                var original = HarmonyLib.AccessTools.Method(typeof(MissionScenarioGameState), "Update");
                if (original != null)
                {
                    var postfix = new HarmonyLib.HarmonyMethod(
                        typeof(SceneStartPatch).GetMethod("Postfix",
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
                    harmony.Patch(original, postfix: postfix);
                    MelonLogger.Msg("✓ SceneStartPatch applied to MissionScenarioGameState.Update");
                }
                else
                {
                    MelonLogger.Error("✗ SceneStartPatch: MissionScenarioGameState.Update not found!");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"SceneStartPatch failed: {ex.Message}");
            }

            MelonLogger.Msg("========================================");
            MelonLogger.Msg("✓ Initialization complete");
            MelonLogger.Msg("========================================");
        }

        // =====================================================================
        // Faction helper — used by Lobby to gate tank selection
        // =====================================================================

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

        // =====================================================================
        // Update loop
        // =====================================================================

        public override void OnUpdate()
        {
            network?.PollEvents();

            if (!consoleSpawned)
                TrySpawnConsole();

            CheckSceneChange();
        }

        // =====================================================================
        // Scene change handling
        // =====================================================================

        private void CheckSceneChange()
        {
            var activeScene = SceneManager.GetActiveScene();
            if (activeScene.name != lastSceneName && !string.IsNullOrEmpty(activeScene.name))
            {
                string oldScene = lastSceneName;
                lastSceneName = activeScene.name;
                if (!string.IsNullOrEmpty(oldScene))
                    OnSceneChanged(activeScene);
            }
        }

        private void OnSceneChanged(Scene scene)
        {
            MelonLogger.Msg("========================================");
            MelonLogger.Msg($"SCENE CHANGED: {scene.name}");
            MelonLogger.Msg("========================================");

            VehicleSpawnHelper.ClearCaches();
            SceneStartPatch.Reset();

            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsActiveMultiplayer)
            {
                MelonLogger.Msg("[SceneLoad] Not in multiplayer mode, ignoring.");
                return;
            }

            // When Lobby loads 'Main' as the DI intermediary, PendingSceneName is set.
            // We wait for Main's pipeline to finish, then load the real target scene.
            // SceneStartPatch fires on the first MissionScenarioGameState.Update in
            // that target scene and calls MultiplayerManager.OnSceneLoaded().
            if (scene.name == "Main" && Lobby.PendingSceneName != null)
            {
                string target = Lobby.PendingSceneName;
                Lobby.PendingSceneName = null;
                MelonLogger.Msg($"[SceneLoad] Main loaded — transitioning to {target} after DI init...");
                MelonCoroutines.Start(LoadTargetSceneFromMain(target));
            }
        }

        private IEnumerator LoadTargetSceneFromMain(string targetScene)
        {
            yield return new WaitForSeconds(2f);
            MelonLogger.Msg($"[SceneLoad] Loading {targetScene}...");
            SceneManager.LoadScene(targetScene);
        }

        // =====================================================================
        // Console spawning
        // =====================================================================

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

        // =====================================================================
        // Shutdown
        // =====================================================================

        public override void OnApplicationQuit()
        {
            MelonLogger.Msg("Sprocket Multiplayer shutting down...");
            network?.Shutdown();
            MelonLogger.Msg("✓ Sprocket Multiplayer shut down.");
        }
    }
}