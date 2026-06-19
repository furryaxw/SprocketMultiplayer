using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppSprocket;
using Il2CppSprocket.Gameplay;
using Il2CppSprocket.VehicleControl;
using MelonLoader;
using SprocketMultiplayer.Core;
using SprocketMultiplayer.Unused;
using UnityEngine;
using UnityEngine.InputSystem;


namespace SprocketMultiplayer.Patches {
    
    // Hooks MissionScenarioGameState.Update
    // We trigger vehicle spawning once and never again for this scene.
    [HarmonyPatch(typeof(MissionScenarioGameState), "Update")]
    public static class SceneStartPatch
    {
        private static bool spawnTriggered = false;

        [HarmonyPostfix]
        static void Postfix()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsActiveMultiplayer)
                return;

            if (spawnTriggered) return;
            spawnTriggered = true;

            MelonLogger.Msg("[SceneStartPatch] First MissionScenarioGameState.Update in MP — triggering spawn.");
            MelonCoroutines.Start(TriggerSpawnAfterDelay());
        }

        private static System.Collections.IEnumerator TriggerSpawnAfterDelay()
        {
            // One frame so the Update completes cleanly before we start coroutines
            yield return null;

            MelonLogger.Msg("[SceneStartPatch] Calling MultiplayerManager.OnSceneLoaded()...");
            if (MultiplayerManager.Instance != null)
                MultiplayerManager.Instance.OnSceneLoaded();
        }

        public static void Reset() { spawnTriggered = false; }
    }
}