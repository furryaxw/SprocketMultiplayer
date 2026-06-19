using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using SprocketMultiplayer.Patches;
using Il2CppSprocket.Vehicles;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace SprocketMultiplayer.Core
{
    public class MultiplayerManager
    {
        public static MultiplayerManager Instance = new MultiplayerManager();

        // nickname - chosen tank name
        public Dictionary<string, string> PlayerChosenTanks = new Dictionary<string, string>();
        // nickname - spawned vehicle gateway
        public Dictionary<string, IVehicleEditGateway> SpawnedVehicles = new Dictionary<string, IVehicleEditGateway>();

        private bool sceneReady;
        private bool spawnStarted;

        // =====================================================================
        // Tank selection
        // =====================================================================

        public void SetPlayerTank(string nickname, string tankName)
        {
            if (string.IsNullOrEmpty(nickname) || string.IsNullOrEmpty(tankName)) return;

            MelonLogger.Msg($"[MP] SetPlayerTank: {nickname} -> {tankName}");
            PlayerChosenTanks[nickname] = tankName;

            if (Lobby.Panel != null)
                Lobby.SetPlayerTank(nickname, tankName);
        }

        public string GetPlayerTank(string nickname)
        {
            if (PlayerChosenTanks.TryGetValue(nickname, out var tank) && !string.IsNullOrEmpty(tank))
            {
                if (VehicleSpawnHelper.HasTank(tank))
                    return tank;

                MelonLogger.Warning($"[MP] Player {nickname} has invalid tank '{tank}', falling back to default.");
            }
            return VehicleSpawnHelper.GetDefaultTankId();
        }

        public List<string> GetAvailableTanks() => VehicleSpawnHelper.GetAvailableTankIds();

        // =====================================================================
        // Scene initialization (called by Main when a gameplay scene loads)
        // =====================================================================

        public void OnSceneLoaded()
        {
            SpawnSummaryLog.Info("sceneStart manager=OnSceneLoaded");
            MelonLogger.Msg("[MP] OnSceneLoaded — starting delayed init...");
            MelonCoroutines.Start(DelayedSceneInit());
        }

        private IEnumerator DelayedSceneInit()
        {
            MelonLogger.Msg("[MP] Waiting for scene to stabilize...");
            SpawnSummaryLog.Info("sceneInit waitForStabilize seconds=1.5");
            yield return new WaitForSeconds(1.5f);

            // Initialize blueprint database
            MelonLogger.Msg("[MP] Initializing VehicleSpawner blueprint database...");
            try
            {
                VehicleSpawnHelper.EnsureInitialized();
                MelonLogger.Msg($"[MP] VehicleSpawner ready with {VehicleSpawnHelper.GetTankCount()} blueprints.");
                SpawnSummaryLog.Info($"sceneInit blueprints={VehicleSpawnHelper.GetTankCount()}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[MP] VehicleSpawner init failed: {ex.Message}");
                SpawnSummaryLog.Error($"sceneInit fail step=blueprints reason={ex.Message}");
                yield break;
            }

            sceneReady = true;

            if (NetworkManager.Instance == null)
            {
                MelonLogger.Error("[MP] NetworkManager.Instance is null!");
                SpawnSummaryLog.Error("sceneInit fail step=network reason=nullInstance");
                yield break;
            }

            SpawnSummaryLog.Info(
                $"sceneInit network host={SpawnSummaryLog.YesNo(NetworkManager.Instance.IsHost)} " +
                $"client={SpawnSummaryLog.YesNo(NetworkManager.Instance.IsClient)} " +
                $"local={NetworkManager.Instance.LocalNickname}");
            MelonLogger.Msg($"[MP] IsHost={NetworkManager.Instance.IsHost}, IsClient={NetworkManager.Instance.IsClient}");

            if (!NetworkManager.Instance.IsHost)
            {
                SpawnSummaryLog.Info("sceneInit client waitingForHostSpawnCommands=yes");
                MelonLogger.Msg("[MP] CLIENT — waiting for spawn commands from host.");
                yield break;
            }

            MelonLogger.Msg("[MP] HOST — ensuring spawner components are cached...");
            SpawnSummaryLog.Info("sceneInit host waitForFactory start");
            yield return MelonCoroutines.Start(VehicleSpawnHelper.WaitForFactory(20));
            SpawnSummaryLog.Info("sceneInit host waitForFactory done");

            // Always attempt spawn — ExecuteVehicleBuildAsync doesn't need VehicleContext.
            MelonLogger.Msg("[MP] ✓ Components cached — starting spawn.");
            StartSpawnProcess();
        }

        // =====================================================================
        // Spawn process (host only)
        // =====================================================================

        public void StartSpawnProcess()
        {
            if (spawnStarted)
            {
                MelonLogger.Msg("[MP] Spawn already in progress.");
                SpawnSummaryLog.Warn("queue blocked reason=alreadyStarted");
                return;
            }
            if (!sceneReady)
            {
                MelonLogger.Warning("[MP] Scene not ready.");
                SpawnSummaryLog.Warn("queue blocked reason=sceneNotReady");
                return;
            }
            if (!NetworkManager.Instance.IsHost)
            {
                MelonLogger.Warning("[MP] Only host can spawn.");
                SpawnSummaryLog.Warn("queue blocked reason=notHost");
                return;
            }

            spawnStarted = true;

            // Collect all players: host first, then lobby members
            var players = new List<string>();

            if (!string.IsNullOrEmpty(NetworkManager.Instance.HostNickname))
                players.Add(NetworkManager.Instance.HostNickname);

            foreach (var kv in Lobby.Players)
                if (!players.Contains(kv.Key))
                    players.Add(kv.Key);

            MelonLogger.Msg($"[MP] Spawning for {players.Count} player(s)...");
            SpawnSummaryLog.Info($"queue start players={players.Count} names={string.Join(",", players.ToArray())}");
            MelonCoroutines.Start(SpawnQueueRoutine(players));
        }

        private IEnumerator SpawnQueueRoutine(List<string> players)
        {
            int successCount = 0;
            int failCount = 0;

            foreach (string nickname in players)
            {
                string tank = GetPlayerTank(nickname);
                if (string.IsNullOrEmpty(tank))
                {
                    MelonLogger.Error($"[MP] No tank available for {nickname}, skipping.");
                    failCount++;
                    SpawnSummaryLog.Error($"player fail name={nickname} step=selectTank reason=noTank");
                    continue;
                }

                Vector3 pos = GetSpawnPoint();
                MelonLogger.Msg($"[MP] Spawning '{tank}' for {nickname} at {pos}...");
                SpawnSummaryLog.Info($"player spawn start name={nickname} tank={tank} position=({pos.x:0.##},{pos.y:0.##},{pos.z:0.##})");

                var result = new IVehicleEditGateway[1];
                yield return MelonCoroutines.Start(
                    VehicleSpawnHelper.SpawnVehicleCoroutine(tank, pos, Quaternion.identity, result));

                IVehicleEditGateway gateway = result[0];
                if (gateway != null)
                {
                    SpawnedVehicles[nickname] = gateway;
                    successCount++;
                    SpawnSummaryLog.Info($"player spawn success name={nickname} tank={tank}");

                    if (nickname == NetworkManager.Instance.LocalNickname)
                    {
                        MelonLogger.Msg($"[MP] Assigning control to host ({nickname}).");
                        VehicleSpawnHelper.AssignVehicleControl(gateway);
                    }

                    NetworkManager.Instance?.Send($"SPAWN:{nickname}:{tank}");
                }
                else
                {
                    MelonLogger.Error($"[MP] Failed to spawn for {nickname}.");
                    failCount++;
                    SpawnSummaryLog.Error($"player spawn fail name={nickname} tank={tank}");
                }

                yield return new WaitForSeconds(0.5f);
            }

            MelonLogger.Msg("[MP] Spawn queue complete.");
            SpawnSummaryLog.Info($"queue complete success={successCount} fail={failCount}");
        }

        private Vector3 GetSpawnPoint()
        {
            var sp = GameObject.Find("SpawnPoint") ?? GameObject.Find("PlayerSpawn");
            if (sp != null) return sp.transform.position;

            return new Vector3(
                Random.Range(-10f, 10f),
                2f,
                Random.Range(-10f, 10f)
            );
        }

        // =====================================================================
        // Client-side spawn handling
        // =====================================================================


        // Called when the client receives a "SPAWN:{nickname}:{tankName}" message.
        public void OnClientSpawnMessage(string nickname, string tankName)
        {
            MelonLogger.Msg($"[MP] Client spawn: {nickname} -> {tankName}");
            SpawnSummaryLog.Info($"client spawnCommand name={nickname} tank={tankName}");

            if (SpawnedVehicles.ContainsKey(nickname) && SpawnedVehicles[nickname] != null)
            {
                MelonLogger.Msg($"[MP] Already have vehicle for {nickname}, skipping.");
                SpawnSummaryLog.Warn($"client spawnCommand skipped name={nickname} reason=alreadySpawned");
                return;
            }

            MelonCoroutines.Start(ClientSpawnCoroutine(nickname, tankName));
        }

        private IEnumerator ClientSpawnCoroutine(string nickname, string tankName)
        {
            Vector3 pos = GetSpawnPoint();
            var result = new IVehicleEditGateway[1];
            yield return MelonCoroutines.Start(
                VehicleSpawnHelper.SpawnVehicleCoroutine(tankName, pos, Quaternion.identity, result));

            IVehicleEditGateway gateway = result[0];
            if (gateway == null)
            {
                MelonLogger.Error($"[MP] Client failed to spawn '{tankName}' for {nickname}.");
                SpawnSummaryLog.Error($"client spawn fail name={nickname} tank={tankName}");
                yield break;
            }

            SpawnedVehicles[nickname] = gateway;
            SpawnSummaryLog.Info($"client spawn success name={nickname} tank={tankName}");

            if (NetworkManager.Instance != null && nickname == NetworkManager.Instance.LocalNickname)
            {
                MelonLogger.Msg("[MP] Assigning control to local client player...");
                yield return new WaitForSeconds(0.25f);
                VehicleSpawnHelper.AssignVehicleControl(gateway);
            }
        }

        // =====================================================================
        // Cleanup
        // =====================================================================

        public void Reset()
        {
            MelonLogger.Msg("[MP] Resetting state...");

            foreach (var kv in SpawnedVehicles)
            {
                if (kv.Value == null) continue;
                try
                {
                    VehicleSpawnHelper.DeregisterVehicle(kv.Value);
                    var go = VehicleSpawnHelper.GetGameObjectFromGateway(kv.Value);
                    if (go != null) Object.Destroy(go);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[MP] Reset: could not destroy vehicle for {kv.Key}: {ex.Message}");
                }
            }

            SpawnedVehicles.Clear();
            PlayerChosenTanks.Clear();
            sceneReady   = false;
            spawnStarted = false;

            MelonLogger.Msg("[MP] Reset complete.");
        }
    }
}
