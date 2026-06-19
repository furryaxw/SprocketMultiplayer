using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSprocket;
using Il2CppSprocket.Gameplay.VehicleControl;
using Il2CppSprocket.TechTrees;
using UnityEngine;
using MelonLoader;
using Il2CppSprocket.Vehicles;
using Il2CppSprocket.Vehicles.Missions;
using Il2CppSprocket.Vehicles.Serialization;
using Il2CppSprocket.Vehicles.Spawning;

namespace SprocketMultiplayer.Core {
    
    // To avoid a name collision with Il2CppSprocket.Vehicles.Spawning.VehicleSpawner,
    // the class is named "Helper"
    public static class VehicleSpawnHelper {

        private static bool isInitialized = false;

        // blueprintName -- absolute file path
        private static readonly Dictionary<string, string> blueprintPaths = new Dictionary<string, string>();
        private static readonly List<string> availableTankIds             = new List<string>();
        private static string defaultTankId;

        // Cached scene components — cleared on scene change via ClearCaches()
        private static VehicleSource cachedSource;  // VehicleSource on the spawner GO
        private static Il2CppSystem.Object cachedFactory;  // VehicleFactories singleton (IVehicleFactory)
        private static Il2CppSystem.Object cachedTechFrame;  // ITechFrame found on the spawner GO
        private static IVehicleRegister cachedRegister;
        private static VehicleSpawner cachedSpawner;
        private static VehicleAssemblyResources cachedAssemblyResources;
        private static Il2CppReferenceArray<IVehicleAssemblyStageFactory> cachedStageFactories;
        private static VehicleController cachedController;
        private static bool techTreeInitiated;

        private static readonly string factionsBasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "Sprocket", "Factions"
        );

        // =====================================================================
        // Initialization — scans blueprint folder
        // =====================================================================

        public static void Initialize()
        {
            if (isInitialized) return;

            MelonLogger.Msg("[VehicleSpawner] Initializing...");

            if (!Directory.Exists(factionsBasePath))
            {
                MelonLogger.Warning($"[VehicleSpawner] Factions folder not found: {factionsBasePath}");
                isInitialized = true;
                return;
            }

            foreach (string factionDir in Directory.GetDirectories(factionsBasePath))
            {
                string vehicleDir = Path.Combine(factionDir, "Blueprints", "Vehicles");
                if (!Directory.Exists(vehicleDir))
                    continue;

                var files = Directory.GetFiles(vehicleDir, "*.blueprint", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!blueprintPaths.ContainsKey(name))
                    {
                        blueprintPaths[name] = file;
                        availableTankIds.Add(name);
                        if (defaultTankId == null)
                            defaultTankId = name;
                    }
                }
            }

            isInitialized = true;
            MelonLogger.Msg($"[VehicleSpawner] ✓ {blueprintPaths.Count} blueprints loaded. Default: {defaultTankId ?? "None"}");
        }

        public static void EnsureInitialized()
        {
            if (!isInitialized) Initialize();
        }

        // =====================================================================
        // Spawn entry point
        // =====================================================================

        // Spawns a vehicle by blueprint name at the given world position/rotation.
        // Coroutine — yield return from SpawnQueueRoutine.
        // Sets resultOut[0] to the gateway on success, null on failure.
        public static IEnumerator SpawnVehicleCoroutine(
            string tankName, Vector3 position, Quaternion rotation,
            IVehicleEditGateway[] resultOut)
        {
            resultOut[0] = null;
            string requestedTankName = tankName;
            SpawnSummaryLog.Info($"spawn start requestedTank={requestedTankName} position={FormatVector(position)}");
            EnsureInitialized();

            // Resolve blueprint name, falling back to default if needed
            if (!blueprintPaths.ContainsKey(tankName))
            {
                MelonLogger.Warning($"[VehicleSpawner] '{tankName}' not in blueprint list.");
                if (defaultTankId == null)
                {
                    MelonLogger.Error("[VehicleSpawner] No blueprints available.");
                    SpawnSummaryLog.Error($"spawn fail step=resolve requestedTank={requestedTankName} reason=noBlueprints");
                    yield break;
                }
                tankName = defaultTankId;
                MelonLogger.Msg($"[VehicleSpawner] Falling back to default: {tankName}");
                SpawnSummaryLog.Warn($"spawn fallback requestedTank={requestedTankName} tank={tankName}");
            }

            // Load blueprint from disk
            IVehicleBlueprint blueprint;
            try
            {
                blueprint = LoadBlueprint(tankName);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[VehicleSpawner] LoadBlueprint: {ex.Message}");
                SpawnSummaryLog.Error($"spawn fail step=load tank={tankName} reason={ex.Message}");
                yield break;
            }

            if (blueprint == null)
            {
                SpawnSummaryLog.Error($"spawn fail step=load tank={tankName} reason=nullBlueprint");
                yield break;
            }

            // Ensure scene components are cached
            if (!EnsureSceneComponents())
            {
                SpawnSummaryLog.Error($"spawn fail step=sceneDeps tank={tankName} missing={GetMissingSceneComponents()}");
                MelonLogger.Error($"[VehicleSpawner] Could not spawn '{tankName}' — scene components not ready.");
                yield break;
            }

            var cts = new Il2CppSystem.Threading.CancellationTokenSource();
            Il2CppSystem.Object rawTask = null;

            try
            {
                var spawnInstance = new VehicleSpawnInstance
                {
                    position = position,
                    rotation = rotation,
                    teamIdOverride = cachedSpawner.TeamID,
                    groupIdOverride = cachedSpawner.GroupID
                };

                rawTask = cachedSpawner.Spawn(
                    blueprint,
                    spawnInstance,
                    VehicleSpawnOptions.None,
                    cts.Token);

                MelonLogger.Msg($"[VehicleSpawner] Spawn() returned: {rawTask?.GetIl2CppType()?.FullName ?? "null"}");
                SpawnSummaryLog.Info($"spawn task tank={tankName} task={GetValueTypeName(rawTask)} spawner={DescribeComponent(cachedSpawner)}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[VehicleSpawner] Spawn() threw: {ex.Message}");
                if (ex.InnerException != null)
                    MelonLogger.Error($"[VehicleSpawner]   inner: {ex.InnerException.Message}");
                SpawnSummaryLog.Error($"spawn fail step=spawnCall tank={tankName} reason={ex.Message}");
                yield break;
            }

            if (rawTask == null)
            {
                SpawnSummaryLog.Error($"spawn fail step=spawnCall tank={tankName} reason=nullTask");
                MelonLogger.Error($"[VehicleSpawner] Could not spawn '{tankName}' — Spawn() returned null.");
                yield break;
            }

            // Poll until the task completes (up to ~10s)
            int maxFrames = 600;
            bool taskDone = false;
            while (maxFrames-- > 0 && !taskDone)
            {
                try
                {
                    var prop = rawTask.GetIl2CppType().GetProperty("IsCompleted");
                    if (prop != null)
                    {
                        var val = prop.GetValue(rawTask);
                        if (val != null)
                        {
                            try   { taskDone = val.Unbox<bool>(); }
                            catch { taskDone = string.Equals(val.ToString(), "True", StringComparison.OrdinalIgnoreCase); }
                        }
                    }
                }
                catch { }

                if (!taskDone) yield return null;
            }

            if (!taskDone)
            {
                MelonLogger.Error("[VehicleSpawner] Spawn() task timed out.");
                try { cts.Cancel(); } catch { }
                SpawnSummaryLog.Error($"spawn fail step=spawnTask tank={tankName} reason=timeout");
                yield break;
            }

            IVehicleEditGateway gateway = null;
            try
            {
                var resultProp = rawTask.GetIl2CppType().GetProperty("Result");
                if (resultProp == null)
                {
                    MelonLogger.Error("[VehicleSpawner] Task has no Result property.");
                    SpawnSummaryLog.Error($"spawn fail step=result tank={tankName} reason=noResultProperty");
                    yield break;
                }

                var raw = resultProp.GetValue(rawTask);
                if (raw == null)
                {
                    MelonLogger.Error("[VehicleSpawner] Task.Result is null.");
                    SpawnSummaryLog.Error($"spawn fail step=result tank={tankName} reason=nullResult");
                    yield break;
                }

                string rawType = GetValueTypeName(raw);
                bool isVehicleSpawn = false;
                bool vehicleSpawned = false;
                var vehicleSpawn = raw.TryCast<VehicleSpawn>();
                if (vehicleSpawn != null)
                {
                    isVehicleSpawn = true;
                    try { vehicleSpawned = vehicleSpawn.Spawned; } catch { }
                    gateway = vehicleSpawn.Vehicle;
                    MelonLogger.Msg($"[VehicleSpawner] VehicleSpawn received. Spawned={vehicleSpawn.Spawned}");
                }

                if (gateway == null)
                {
                    var behaviour = raw.TryCast<IVehicleBehaviour>();
                    if (behaviour != null)
                    {
                        MelonLogger.Msg("[VehicleSpawner] Result is IVehicleBehaviour — calling EnableBehaviour()...");
                        gateway = EnableBehaviour(behaviour);
                    }
                }

                if (gateway == null)
                    gateway = raw.TryCast<IVehicleEditGateway>();

                SpawnSummaryLog.Info(
                    $"spawn result tank={tankName} rawType={rawType} " +
                    $"vehicleSpawn={SpawnSummaryLog.YesNo(isVehicleSpawn)} " +
                    $"spawned={SpawnSummaryLog.YesNo(vehicleSpawned)} " +
                    $"gateway={SpawnSummaryLog.YesNo(gateway != null)}");

                if (gateway == null)
                {
                    MelonLogger.Error($"[VehicleSpawner] Could not extract gateway from result type: {raw.GetIl2CppType()?.FullName ?? "unknown"}");
                    SpawnSummaryLog.Error($"spawn fail step=gateway tank={tankName} rawType={rawType}");
                    yield break;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[VehicleSpawner] Result extraction failed: {ex.Message}");
                SpawnSummaryLog.Error($"spawn fail step=result tank={tankName} reason={ex.Message}");
                yield break;
            }

            PositionVehicle(gateway, position, rotation);
            RegisterVehicle(gateway);

            MelonLogger.Msg($"[VehicleSpawner] '{tankName}' spawned at {position}.");
            SpawnSummaryLog.Info($"spawn success tank={tankName} vehicle={DescribeGameObject(GetGameObjectFromGateway(gateway))} position={FormatVector(position)}");
            resultOut[0] = gateway;
        }

        // =====================================================================
        // EnableBehaviour
        // =====================================================================

        // Calls IVehicleBehaviour.EnableBehaviour(EntityID, BehaviourEnableOptions,
        // IVehicleAssemblyStageFactory[], AssemblyFlags) and returns the IVehicleEditGateway.
        // Per the dev: this is required after Create() to activate the vehicle.
        // Value-type params get default instances; the factory array gets an empty array (not null).
        private static IVehicleEditGateway EnableBehaviour(IVehicleBehaviour behaviour)
        {
            try
            {
                var il2Flags  = Il2CppSystem.Reflection.BindingFlags.Public   |
                                Il2CppSystem.Reflection.BindingFlags.NonPublic |
                                Il2CppSystem.Reflection.BindingFlags.Instance;
                var rawObj    = new Il2CppSystem.Object(behaviour.Pointer);
                var bType     = rawObj.GetIl2CppType();

                foreach (var m in bType.GetMethods(il2Flags))
                {
                    if (m.Name != "EnableBehaviour") continue;

                    var parms = m.GetParameters();
                    var args  = new Il2CppSystem.Object[parms.Count];

                    for (int i = 0; i < parms.Count; i++)
                    {
                        string ptn = parms[i].ParameterType?.FullName ?? "";
                        string pnn = parms[i].ParameterType?.Name     ?? "";

                        if (pnn.Contains("[]") || ptn.Contains("Factory"))
                        {
                            // IVehicleAssemblyStageFactory[] — empty array, never null
                            try
                            {
                                var elemType = parms[i].ParameterType.GetElementType();
                                args[i] = elemType != null
                                    ? Il2CppSystem.Array.CreateInstance(elemType, 0).Cast<Il2CppSystem.Object>()
                                    : null;
                            }
                            catch { args[i] = null; }
                        }
                        else
                        {
                            // Structs (EntityID, BehaviourEnableOptions, AssemblyFlags) — zero-value box
                            try { args[i] = Il2CppSystem.Activator.CreateInstance(parms[i].ParameterType); }
                            catch { args[i] = null; }
                        }
                    }

                    var result = m.Invoke(rawObj, args);
                    if (result != null)
                    {
                        var gw = result.TryCast<IVehicleEditGateway>();
                        if (gw != null)
                        {
                            MelonLogger.Msg("[VehicleSpawner] EnableBehaviour() returned gateway.");
                            return gw;
                        }
                    }
                }

                MelonLogger.Warning("[VehicleSpawner] EnableBehaviour() found but returned no gateway.");
                return null;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[VehicleSpawner] EnableBehaviour() threw: {ex.Message}");
                return null;
            }
        }

        // =====================================================================
        // Vehicle control
        // =====================================================================

        public static void AssignVehicleControl(IVehicleEditGateway gateway)
        {
            if (gateway == null)
            {
                MelonLogger.Warning("[VehicleSpawner] AssignVehicleControl: gateway is null.");
                return;
            }

            try
            {
                var controller = GetVehicleController();
                if (controller == null)
                {
                    MelonLogger.Error("[VehicleSpawner] VehicleController not found.");
                    return;
                }

                var vehicle = GetVehicleBehaviourFromGateway(gateway);
                if (vehicle == null)
                {
                    MelonLogger.Error("[VehicleSpawner] Could not extract IVehicleBehaviour from gateway.");
                    return;
                }

                controller.ControlledVehicle = vehicle;
                MelonLogger.Msg("[VehicleSpawner] ✓ Vehicle control assigned.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[VehicleSpawner] AssignVehicleControl: {ex.Message}");
            }
        }

        // =====================================================================
        // Register / Deregister
        // =====================================================================

        public static void RegisterVehicle(IVehicleEditGateway gateway)
        {
            if (gateway == null) return;
            try
            {
                var reg = GetVehicleRegister();
                if (reg == null) { MelonLogger.Warning("[VehicleSpawner] VehicleRegister not found."); return; }
                reg.Register(gateway);
                MelonLogger.Msg("[VehicleSpawner] ✓ Vehicle registered.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[VehicleSpawner] RegisterVehicle: {ex.Message}"); }
        }

        public static void DeregisterVehicle(IVehicleEditGateway gateway)
        {
            if (gateway == null) return;
            try
            {
                var reg = GetVehicleRegister();
                if (reg == null) return;
                reg.Deregister(gateway);
                MelonLogger.Msg("[VehicleSpawner] ✓ Vehicle deregistered.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[VehicleSpawner] DeregisterVehicle: {ex.Message}"); }
        }

        // =====================================================================
        // Blueprint loading
        // =====================================================================

        private static IVehicleBlueprint LoadBlueprint(string tankName)
        {
            if (!blueprintPaths.TryGetValue(tankName, out string filePath))
            {
                MelonLogger.Error($"[VehicleSpawner] No path for '{tankName}'.");
                return null;
            }

            try
            {
                var blueprint = new VehicleBlueprintSerializer().LoadFromPath(filePath);
                if (blueprint == null) MelonLogger.Error($"[VehicleSpawner] LoadFromPath returned null for '{tankName}'.");
                else                   MelonLogger.Msg($"[VehicleSpawner] Blueprint loaded: '{tankName}'");
                return blueprint;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[VehicleSpawner] LoadBlueprint '{tankName}': {ex.Message}");
                return null;
            }
        }

        // =====================================================================
        // Scene component locators
        // =====================================================================

        // Ensures VehicleSource, VehicleSpawner, VehicleFactories, ITechFrame, and register are cached.
        // Returns true if the factory is ready to spawn.
        private static bool EnsureSceneComponents()
        {
            if (cachedSource != null &&
                cachedSpawner != null &&
                cachedFactory != null &&
                cachedTechFrame != null &&
                cachedRegister != null &&
                cachedStageFactories != null &&
                cachedStageFactories.Length > 0)
            {
                SpawnSummaryLog.Info("sceneDeps ready cached=yes");
                return true;
            }

            MelonLogger.Msg("[VehicleSpawner] Searching scene for spawner components...");

            var scenarioGO = GameObject.Find("Scenario");
            if (scenarioGO == null)
            {
                MelonLogger.Warning("[VehicleSpawner] 'Scenario' not found in scene.");
                return false;
            }

            CacheStageSpawner();

            foreach (var t in scenarioGO.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.Contains("Spawner")) continue;

                foreach (var mb in t.GetComponents<MonoBehaviour>())
                {
                    if (mb == null || mb.Pointer == IntPtr.Zero) continue;

                    // Cache VehicleSource
                    if (cachedSource == null)
                    {
                        var src = TryCast<VehicleSource>(mb);
                        if (src != null)
                        {
                            cachedSource = src;
                            MelonLogger.Msg($"[VehicleSpawner] VehicleSource found on '{t.name}'.");
                        }
                    }

                    if (cachedSpawner == null)
                    {
                        var spawner = TryCast<VehicleSpawner>(mb);
                        if (spawner != null)
                        {
                            cachedSpawner = spawner;
                            MelonLogger.Msg($"[VehicleSpawner] VehicleSpawner found on '{t.name}'.");
                        }
                    }

                    // Cache ITechFrame
                    if (cachedTechFrame == null)
                    {
                        try
                        {
                            var obj = new Il2CppSystem.Object(mb.Pointer);
                            var tf  = obj.TryCast<ITechFrame>();
                            if (tf != null)
                            {
                                cachedTechFrame = obj;
                                MelonLogger.Msg($"[VehicleSpawner] ITechFrame found: {obj.GetIl2CppType()?.FullName}");
                            }
                        }
                        catch { }
                    }
                }

                if (cachedSource != null && cachedSpawner != null) break;
            }

            if (cachedSource == null)
            {
                SpawnSummaryLog.Warn("sceneDeps incomplete missing=source");
                MelonLogger.Warning("[VehicleSpawner] VehicleSource not found.");
                return false;
            }
            if (cachedSpawner == null)
            {
                SpawnSummaryLog.Warn("sceneDeps incomplete missing=officialSpawner");
                MelonLogger.Warning("[VehicleSpawner] VehicleSpawner not found.");
                return false;
            }

            // Grab VehicleFactories singleton — this is the IVehicleFactory implementation
            CacheVehicleFactory();
            CacheTechFrame();
            CacheAssemblyResources();
            GetVehicleRegister();

            bool ready = cachedFactory != null &&
                         cachedTechFrame != null &&
                         cachedRegister != null &&
                         cachedStageFactories != null &&
                         cachedStageFactories.Length > 0;

            if (!ready)
            {
                SpawnSummaryLog.Warn($"sceneDeps incomplete missing={GetMissingSceneComponents()}");
                MelonLogger.Warning(
                    "[VehicleSpawner] Scene components incomplete: " +
                    $"factory={(cachedFactory != null ? "yes" : "no")} " +
                    $"techFrame={(cachedTechFrame != null ? "yes" : "no")} " +
                    $"register={(cachedRegister != null ? "yes" : "no")} " +
                    $"stageFactories={(cachedStageFactories?.Length ?? 0)}");
            }
            else
            {
                SpawnSummaryLog.Info(
                    "sceneDeps ready cached=no " +
                    $"source={DescribeComponent(cachedSource)} " +
                    $"spawner={DescribeComponent(cachedSpawner)} " +
                    $"techFrame={GetValueTypeName(cachedTechFrame)} " +
                    $"register={GetValueTypeName(cachedRegister)} " +
                    $"stageFactories={(cachedStageFactories?.Length ?? 0)}");
            }

            return ready;
        }

        private static void CacheStageSpawner()
        {
            StageVehicleSpawnEvent fallback = null;
            StageVehicleSpawnEvent player = null;

            foreach (var stageEvent in UnityEngine.Object.FindObjectsOfType<StageVehicleSpawnEvent>())
            {
                if (stageEvent == null) continue;
                if (fallback == null) fallback = stageEvent;

                try
                {
                    string path = GetPath(stageEvent.transform);
                    string mode = stageEvent.blueprintSource != null ? stageEvent.blueprintSource.Mode.ToString() : "";
                    if (path.IndexOf("Player Mission", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        mode.IndexOf("EditorExitSave", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        player = stageEvent;
                        break;
                    }
                }
                catch { }
            }

            var selected = player ?? fallback;
            if (selected == null) return;

            try
            {
                if (cachedSource == null && selected.blueprintSource != null)
                {
                    cachedSource = selected.blueprintSource;
                    MelonLogger.Msg($"[VehicleSpawner] VehicleSource selected from stage event '{GetPath(selected.transform)}'.");
                }
            }
            catch { }

            try
            {
                if (cachedSpawner == null && selected.spawner != null)
                {
                    cachedSpawner = selected.spawner;
                    MelonLogger.Msg($"[VehicleSpawner] VehicleSpawner selected from stage event '{GetPath(selected.transform)}'.");
                }
            }
            catch { }
        }

        private static void CacheVehicleFactory()
        {
            if (cachedFactory != null) return;

            try
            {
                var staticFlags  = System.Reflection.BindingFlags.Public   |
                                   System.Reflection.BindingFlags.NonPublic |
                                   System.Reflection.BindingFlags.Static;
                var managedType  = typeof(VehicleFactories);
                var instanceProp = managedType.GetProperty("instance", staticFlags)
                                ?? managedType.GetProperty("Instance", staticFlags);

                if (instanceProp != null)
                {
                    var vf = instanceProp.GetValue(null) as VehicleFactories;
                    if (vf != null)
                    {
                        cachedFactory = vf.Cast<Il2CppSystem.Object>();
                        MelonLogger.Msg("[VehicleSpawner] VehicleFactories instance cached.");
                    }
                    else
                    {
                        MelonLogger.Warning("[VehicleSpawner] VehicleFactories.instance is null.");
                    }
                }
                else
                {
                    MelonLogger.Warning("[VehicleSpawner] VehicleFactories instance property not found.");
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[VehicleSpawner] VehicleFactories lookup: {ex.Message}"); }
        }

        private static void CacheTechFrame()
        {
            if (cachedTechFrame != null) return;

            try
            {
                if (!techTreeInitiated)
                {
                    TechTreeLoader.Initiate();
                    techTreeInitiated = true;
                    MelonLogger.Msg("[VehicleSpawner] TechTreeLoader initiated.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[VehicleSpawner] TechTreeLoader.Initiate: {ex.Message}");
            }

            try
            {
                var date = GetSpawnTechDate();
                ITechFrame frame = null;

                try
                {
                    var frameFactory = ITechFrameFactory.Instance;
                    if (frameFactory != null)
                        frame = frameFactory.GetTechFrameAtDate(date);
                }
                catch { }

                if (frame == null)
                    frame = new TechTreeLoader().GetTechFrameAtDate(date);

                if (frame != null && frame.Pointer != IntPtr.Zero)
                {
                    cachedTechFrame = new Il2CppSystem.Object(frame.Pointer);
                    MelonLogger.Msg($"[VehicleSpawner] ITechFrame generated for {date}: {cachedTechFrame.GetIl2CppType()?.FullName}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[VehicleSpawner] ITechFrame generation failed: {ex.Message}");
            }
        }

        private static TechDate GetSpawnTechDate()
        {
            try
            {
                if (cachedSource != null && cachedSource.MaxTechDate.Valid)
                    return cachedSource.MaxTechDate;
            }
            catch { }

            return new TechDate(1945, 0, 0);
        }

        private static void CacheAssemblyResources()
        {
            if (cachedStageFactories != null && cachedStageFactories.Length > 0) return;

            try
            {
                if (cachedSpawner != null && cachedSpawner.assemblyResourcesOverride != null)
                    cachedAssemblyResources = cachedSpawner.assemblyResourcesOverride;
            }
            catch { }

            try
            {
                if (cachedAssemblyResources == null)
                    cachedAssemblyResources = VehicleAssemblyResources.Default;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[VehicleSpawner] VehicleAssemblyResources.Default: {ex.Message}");
            }

            try
            {
                cachedStageFactories = cachedAssemblyResources?.GetFactories();
                MelonLogger.Msg($"[VehicleSpawner] Assembly stage factories cached: {cachedStageFactories?.Length ?? 0}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[VehicleSpawner] Assembly stage factories lookup: {ex.Message}");
            }
        }

        private static IVehicleRegister GetVehicleRegister()
        {
            if (cachedRegister != null) return cachedRegister;

            foreach (var stageEvent in UnityEngine.Object.FindObjectsOfType<StageVehicleSpawnEvent>())
            {
                if (stageEvent == null) continue;
                try
                {
                    var reg = stageEvent.register;
                    if (reg != null)
                    {
                        cachedRegister = reg;
                        MelonLogger.Msg($"[VehicleSpawner] IVehicleRegister found on stage event '{stageEvent.name}'.");
                        return reg;
                    }
                }
                catch { }
            }

            foreach (var gameMode in UnityEngine.Object.FindObjectsOfType<GameMode>())
            {
                if (gameMode == null || gameMode.Pointer == IntPtr.Zero) continue;

                try
                {
                    if (TryReadVehicleRegisterFromObject(new Il2CppSystem.Object(gameMode.Pointer), out var reg))
                    {
                        cachedRegister = reg;
                        MelonLogger.Msg($"[VehicleSpawner] IVehicleRegister found on game mode '{gameMode.name}'.");
                        InjectVehicleRegisterIntoStageEvents(cachedRegister);
                        return reg;
                    }
                }
                catch { }
            }

            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null || mb.Pointer == IntPtr.Zero) continue;
                try
                {
                    var reg = new Il2CppSystem.Object(mb.Pointer).TryCast<IVehicleRegister>();
                    if (reg != null)
                    {
                        cachedRegister = reg;
                        MelonLogger.Msg("[VehicleSpawner] IVehicleRegister found.");
                        InjectVehicleRegisterIntoStageEvents(cachedRegister);
                        return reg;
                    }
                }
                catch { }
            }

            cachedRegister = CreateVehicleRegister();
            if (cachedRegister != null)
            {
                InjectVehicleRegisterIntoStageEvents(cachedRegister);
                MelonLogger.Msg("[VehicleSpawner] IVehicleRegister created and injected.");
                return cachedRegister;
            }

            MelonLogger.Warning("[VehicleSpawner] IVehicleRegister not found.");
            return null;
        }

        private static bool TryReadVehicleRegisterFromObject(Il2CppSystem.Object obj, out IVehicleRegister register)
        {
            register = null;
            if (obj == null || obj.Pointer == IntPtr.Zero) return false;

            try
            {
                var direct = obj.TryCast<IVehicleRegister>();
                if (direct != null)
                {
                    register = direct;
                    return true;
                }
            }
            catch { }

            try
            {
                var flags = Il2CppSystem.Reflection.BindingFlags.Public |
                            Il2CppSystem.Reflection.BindingFlags.NonPublic |
                            Il2CppSystem.Reflection.BindingFlags.Instance;

                foreach (var propName in new[] { "VehicleRegister", "vehicleRegister", "register", "SharedState", "sharedState", "activeState", "State" })
                {
                    var prop = obj.GetIl2CppType().GetProperty(propName, flags);
                    if (prop == null) continue;

                    var value = prop.GetValue(obj);
                    if (value == null) continue;

                    try
                    {
                        var candidate = value.TryCast<IVehicleRegister>();
                        if (candidate != null)
                        {
                            register = candidate;
                            return true;
                        }
                    }
                    catch { }

                    var nested = AsIl2CppObject(value);
                    if (nested != null && nested.Pointer != obj.Pointer && TryReadVehicleRegisterFromObject(nested, out register))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static IVehicleRegister CreateVehicleRegister()
        {
            try
            {
                var register = new VehicleRegister();
                return register.TryCast<IVehicleRegister>() ?? new Il2CppSystem.Object(register.Pointer).TryCast<IVehicleRegister>();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[VehicleSpawner] CreateVehicleRegister: {ex.Message}");
                return null;
            }
        }

        private static void InjectVehicleRegisterIntoStageEvents(IVehicleRegister register)
        {
            if (register == null) return;

            foreach (var stageEvent in UnityEngine.Object.FindObjectsOfType<StageVehicleSpawnEvent>())
            {
                if (stageEvent == null) continue;
                try
                {
                    stageEvent.SetVehicleRegister(register);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[VehicleSpawner] SetVehicleRegister on '{stageEvent.name}': {ex.Message}");
                }
            }
        }

        private static VehicleController GetVehicleController()
        {
            if (cachedController != null) return cachedController;

            var controllers = UnityEngine.Object.FindObjectsOfType<VehicleController>();
            if (controllers != null && controllers.Length > 0)
            {
                cachedController = controllers[0];
                MelonLogger.Msg("[VehicleSpawner]VehicleController found.");
                return cachedController;
            }

            MelonLogger.Warning("[VehicleSpawner] VehicleController not found.");
            return null;
        }

        // =====================================================================
        // Gateway helpers
        // =====================================================================

        private static IVehicleBehaviour GetVehicleBehaviourFromGateway(IVehicleEditGateway gateway)
        {
            if (gateway == null) return null;
            try
            {
                if (gateway.Pointer == IntPtr.Zero) return null;
                var obj = new Il2CppSystem.Object(gateway.Pointer);

                // The gateway itself may implement IVehicleBehaviour
                var direct = obj.TryCast<IVehicleBehaviour>();
                if (direct != null) return direct;

                // Otherwise check for a .Vehicle property
                var prop = obj.GetIl2CppType().GetProperty("Vehicle");
                if (prop != null)
                {
                    var raw = prop.GetValue(obj);
                    if (raw != null) return raw.TryCast<IVehicleBehaviour>();
                }

                MelonLogger.Warning("[VehicleSpawner] Could not extract IVehicleBehaviour from gateway.");
                return null;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[VehicleSpawner] GetVehicleBehaviourFromGateway: {ex.Message}");
                return null;
            }
        }

        public static GameObject GetGameObjectFromGateway(IVehicleEditGateway gateway)
        {
            if (gateway == null) return null;
            try
            {
                var behaviour = GetVehicleBehaviourFromGateway(gateway);
                return behaviour?.TryCast<MonoBehaviour>()?.gameObject;
            }
            catch { return null; }
        }

        private static void PositionVehicle(IVehicleEditGateway gateway, Vector3 position, Quaternion rotation)
        {
            try
            {
                var go = GetGameObjectFromGateway(gateway);
                if (go == null) { MelonLogger.Warning("[VehicleSpawner] Could not get GO to position vehicle."); return; }
                go.transform.SetPositionAndRotation(position, rotation);
                MelonLogger.Msg($"[VehicleSpawner] Vehicle positioned at {position}.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[VehicleSpawner] PositionVehicle: {ex.Message}"); }
        }

        // =====================================================================
        // Factory availability / waiting
        // =====================================================================

        // Returns true when VehicleFactories singleton is present and its internal
        // context field is non-null (meaning the DI pipeline has fully run).
        public static bool IsFactoryAvailable()
        {
            EnsureSceneComponents();
            return cachedFactory != null &&
                   cachedSpawner != null &&
                   cachedTechFrame != null &&
                   cachedRegister != null &&
                   cachedStageFactories != null &&
                   cachedStageFactories.Length > 0;
        }

        // Ensures components are cached, then waits a brief settle frame.
        // Call this from MultiplayerManager before starting the spawn queue.
        public static IEnumerator WaitForFactory(int maxWaitSeconds = 20)
        {
            MelonLogger.Msg("[VehicleSpawner] WaitForFactory — caching scene components...");

            float elapsed = 0f;
            while (elapsed < maxWaitSeconds)
            {
                if (IsFactoryAvailable())
                {
                    MelonLogger.Msg("[VehicleSpawner] Factory ready.");
                    yield return new WaitForSeconds(0.5f); // one settle frame
                    yield break;
                }

                yield return new WaitForSeconds(1f);
                elapsed += 1f;
                MelonLogger.Msg($"[VehicleSpawner] Waiting for factory... ({elapsed}s)");
            }

            MelonLogger.Warning("[VehicleSpawner] Factory not ready after timeout — proceeding anyway.");
        }

        // =====================================================================
        // Cache management
        // =====================================================================

        public static void ClearCaches()
        {
            cachedSource     = null;
            cachedFactory    = null;
            cachedTechFrame  = null;
            cachedRegister   = null;
            cachedSpawner    = null;
            cachedAssemblyResources = null;
            cachedStageFactories = null;
            cachedController = null;
            MelonLogger.Msg("[VehicleSpawner] Caches cleared.");
        }

        // =====================================================================
        // Utility
        // =====================================================================

        private static T TryCast<T>(MonoBehaviour mb) where T : Il2CppSystem.Object
        {
            try
            {
                if (mb == null || mb.Pointer == IntPtr.Zero) return null;
                return new Il2CppSystem.Object(mb.Pointer).TryCast<T>();
            }
            catch { return null; }
        }

        private static Il2CppSystem.Object AsIl2CppObject(object value)
        {
            if (value == null) return null;

            if (value is Il2CppSystem.Object il2Object)
                return il2Object;

            if (value is Il2CppObjectBase obj && obj.Pointer != IntPtr.Zero)
                return new Il2CppSystem.Object(obj.Pointer);

            return null;
        }

        private static string GetValueTypeName(object value)
        {
            if (value == null) return "null";

            if (value is Il2CppSystem.Object il2Object)
                return il2Object.GetIl2CppType()?.FullName ?? il2Object.GetType().FullName;

            if (value is Il2CppObjectBase obj)
                return obj.GetType().FullName;

            return value.GetType().FullName;
        }

        private static string DescribeGameObject(GameObject go)
        {
            if (go == null) return "null";
            return GetPath(go.transform);
        }

        private static string DescribeComponent(Component component)
        {
            if (component == null) return "null";
            return GetPath(component.transform);
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:0.##},{value.y:0.##},{value.z:0.##})";
        }

        private static string GetMissingSceneComponents()
        {
            var missing = new List<string>();
            if (cachedSource == null) missing.Add("source");
            if (cachedSpawner == null) missing.Add("officialSpawner");
            if (cachedFactory == null) missing.Add("factory");
            if (cachedTechFrame == null) missing.Add("techFrame");
            if (cachedRegister == null) missing.Add("register");
            if (cachedStageFactories == null || cachedStageFactories.Length <= 0) missing.Add("stageFactories");
            return missing.Count == 0 ? "none" : string.Join(",", missing.ToArray());
        }

        private static string GetPath(Transform transform)
        {
            if (transform == null) return "<no transform>";

            var parts = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        // =====================================================================
        // Public query API
        // =====================================================================

        public static string       GetDefaultTankId()     { EnsureInitialized(); return defaultTankId; }
        public static List<string> GetAvailableTankIds()  { EnsureInitialized(); return new List<string>(availableTankIds); }
        public static bool         HasTank(string tankId) { EnsureInitialized(); return blueprintPaths.ContainsKey(tankId); }
        public static int          GetTankCount()         { EnsureInitialized(); return blueprintPaths.Count; }
    }
}
