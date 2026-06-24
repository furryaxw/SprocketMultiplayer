using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSprocket;
using Il2CppSprocket.DetectionSystems;
using Il2CppSprocket.Gameplay.VehicleControl;
using Il2CppSprocket.TechTrees;
using Il2CppSprocket.Spawning;
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

        private const bool UseStageEventSpawnExperiment = false;
        private const bool UseSpawnLocatorForDirectSpawn = true;
        private const bool ForceEnableBehaviourAfterSpawn = true;
        private const bool ForceEnableBehaviourRegardlessOfState = true;
        private const string SpawnPathDebugTag = "[DEBUG-spawn-path]";

        private static bool isInitialized = false;

        // blueprintName -- absolute file path
        private static readonly Dictionary<string, string> blueprintPaths = new Dictionary<string, string>();
        private static readonly List<string> availableTankIds             = new List<string>();
        private static string defaultTankId;

        // Cached scene components — cleared on scene change via ClearCaches()
        private static StageVehicleSpawnEvent cachedStageEvent;
        private static VehicleSource cachedSource;  // VehicleSource on the spawner GO
        private static Il2CppSystem.Object cachedFactory;  // VehicleFactories singleton (IVehicleFactory)
        private static Il2CppSystem.Object cachedTechFrame;  // ITechFrame found on the spawner GO
        private static IVehicleRegister cachedRegister;
        private static VehicleSpawner cachedSpawner;
        private static SpawnLocator cachedSpawnLocator;
        private static VehicleAssemblyResources cachedAssemblyResources;
        private static Il2CppReferenceArray<IVehicleAssemblyStageFactory> cachedStageFactories;
        private static VehicleController cachedController;
        private static bool techTreeInitiated;
        private static int nextBehaviourBuildNumber = 1;
        private static readonly HashSet<IntPtr> forcedBehaviourEnableAttempts = new HashSet<IntPtr>();

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
            bool stageEventExperiment = UseStageEventSpawnExperiment && cachedStageEvent != null;
            string spawnRoute = stageEventExperiment ? "stageEvent" : "direct";
            Vector3 finalSpawnPosition = position;
            Quaternion finalSpawnRotation = rotation;

            LogSpawnProbe(tankName, blueprint, position, rotation, stageEventExperiment);

            try
            {
                if (stageEventExperiment)
                {
                    PrimeStageEventForSpawn();
                    rawTask = cachedStageEvent.SpawnAsync(cts.Token);
                    MelonLogger.Msg($"{SpawnPathDebugTag} StageVehicleSpawnEvent.SpawnAsync() returned: {rawTask?.GetIl2CppType()?.FullName ?? "null"}");
                    SpawnSummaryLog.Info(
                        $"spawn task route=stageEvent tank={tankName} " +
                        $"task={GetValueTypeName(rawTask)} stage={DescribeStageEvent(cachedStageEvent)}");
                }
                else
                {
                    string instanceSource;
                    var spawnInstance = BuildSpawnInstance(position, rotation, out instanceSource);
                    finalSpawnPosition = spawnInstance.position;
                    finalSpawnRotation = spawnInstance.rotation;

                    rawTask = cachedSpawner.Spawn(
                        blueprint,
                        spawnInstance,
                        VehicleSpawnOptions.AppendNameID,
                        cts.Token);

                    MelonLogger.Msg($"[VehicleSpawner] Spawn() returned: {rawTask?.GetIl2CppType()?.FullName ?? "null"}");
                    SpawnSummaryLog.Info(
                        $"spawn task route=direct tank={tankName} " +
                        $"task={GetValueTypeName(rawTask)} " +
                        $"spawner={DescribeComponent(cachedSpawner)} " +
                        $"instanceSource={instanceSource} options=AppendNameID " +
                        $"instance={DescribeSpawnInstance(spawnInstance)}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[VehicleSpawner] Spawn route '{spawnRoute}' threw: {ex.Message}");
                if (ex.InnerException != null)
                    MelonLogger.Error($"[VehicleSpawner]   inner: {ex.InnerException.Message}");
                SpawnSummaryLog.Error($"spawn fail step=spawnCall route={spawnRoute} tank={tankName} reason={ex.Message}");
                yield break;
            }

            if (rawTask == null)
            {
                SpawnSummaryLog.Error($"spawn fail step=spawnCall route={spawnRoute} tank={tankName} reason=nullTask");
                MelonLogger.Error($"[VehicleSpawner] Could not spawn '{tankName}' — route '{spawnRoute}' returned null.");
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
                MelonLogger.Error($"[VehicleSpawner] Spawn route '{spawnRoute}' task timed out.");
                try { cts.Cancel(); } catch { }
                SpawnSummaryLog.Error($"spawn fail step=spawnTask route={spawnRoute} tank={tankName} reason=timeout");
                yield break;
            }

            string taskState = DescribeTaskState(rawTask);
            SpawnSummaryLog.Info($"spawn task done route={spawnRoute} tank={tankName} {taskState}");

            if (IsTaskFaulted(rawTask))
            {
                string reason = DescribeTaskException(rawTask);
                MelonLogger.Error($"[VehicleSpawner] Spawn route '{spawnRoute}' task faulted: {reason}");
                SpawnSummaryLog.Error($"spawn fail step=spawnTask route={spawnRoute} tank={tankName} reason={reason}");
                yield break;
            }

            IVehicleEditGateway gateway = null;
            try
            {
                var resultProp = rawTask.GetIl2CppType().GetProperty("Result");
                if (resultProp == null)
                {
                    MelonLogger.Error("[VehicleSpawner] Task has no Result property.");
                    SpawnSummaryLog.Error($"spawn fail step=result route={spawnRoute} tank={tankName} reason=noResultProperty");
                    yield break;
                }

                var raw = resultProp.GetValue(rawTask);
                if (raw == null)
                {
                    MelonLogger.Error("[VehicleSpawner] Task.Result is null.");
                    SpawnSummaryLog.Error($"spawn fail step=result route={spawnRoute} tank={tankName} reason=nullResult");
                    yield break;
                }

                string rawType;
                string resultSummary;
                gateway = TryExtractGatewayFromSpawnResult(raw, out rawType, out resultSummary);

                SpawnSummaryLog.Info(
                    $"spawn result route={spawnRoute} tank={tankName} rawType={rawType} " +
                    $"{resultSummary} " +
                    $"gateway={SpawnSummaryLog.YesNo(gateway != null)}");

                if (gateway == null)
                {
                    MelonLogger.Error($"[VehicleSpawner] Could not extract gateway from result type: {rawType}");
                    SpawnSummaryLog.Error($"spawn fail step=gateway route={spawnRoute} tank={tankName} rawType={rawType}");
                    yield break;
                }
            }
            catch (Exception ex)
            {
                string reason = DescribeException(ex);
                MelonLogger.Error($"[VehicleSpawner] Result extraction failed for route '{spawnRoute}': {reason}");
                SpawnSummaryLog.Error($"spawn fail step=result route={spawnRoute} tank={tankName} reason={reason}");
                yield break;
            }

            if (ForceEnableBehaviourAfterSpawn)
            {
                string enableDetail;
                var enabledBehaviour = EnsureBehaviourEnabled(gateway, out enableDetail);
                SpawnSummaryLog.Info(
                    $"forceEnableAfterSpawn route={spawnRoute} tank={tankName} " +
                    $"behaviour={GetValueTypeName(enabledBehaviour)} {DescribeGatewayBehaviourState(gateway)} " +
                    $"enable={enableDetail}");
            }

            if (!stageEventExperiment)
            {
                PositionVehicle(gateway, finalSpawnPosition, finalSpawnRotation);
                RegisterVehicle(gateway);
            }
            else
            {
                MelonLogger.Msg($"{SpawnPathDebugTag} Stage event route returned a gateway; native stage path owns position/register.");
            }

            MelonLogger.Msg($"[VehicleSpawner] '{tankName}' spawned via {spawnRoute}.");
            SpawnSummaryLog.Info(
                $"spawn success route={spawnRoute} tank={tankName} " +
                $"vehicle={DescribeGameObject(GetGameObjectFromGateway(gateway))} " +
                $"position={FormatVector(finalSpawnPosition)}");
            resultOut[0] = gateway;
        }

        // =====================================================================
        // EnableBehaviour
        // =====================================================================

#if false
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
#endif

        private static IVehicleBehaviour EnsureBehaviourEnabled(IVehicleEditGateway gateway, out string detail)
        {
            detail = "gateway=null";
            if (gateway == null) return null;

            bool enabledBefore;
            string enabledBeforeText = ReadGatewayBehaviourEnabled(gateway, out enabledBefore);
            var existing = GetVehicleBehaviourFromGateway(gateway);
            bool forceEnable = ShouldForceEnableBehaviour(gateway);
            if (enabledBefore && !forceEnable)
            {
                detail =
                    $"enabledBefore={enabledBeforeText} called=no " +
                    $"behaviour={GetValueTypeName(existing)}";
                return existing;
            }

            try
            {
                if (cachedStageFactories == null || cachedStageFactories.Length <= 0)
                    CacheAssemblyResources();

                var factories = cachedStageFactories ?? new Il2CppReferenceArray<IVehicleAssemblyStageFactory>(0);
                var id = BuildBehaviourEntityId();
                const BehaviourEnableOptions options =
                    BehaviourEnableOptions.FlattenTransformHeirarchy |
                    BehaviourEnableOptions.DisableTransformHeirarchyFlattenReversion |
                    BehaviourEnableOptions.Kinematic;

                var behaviour = gateway.EnableBehaviour(id, options, factories, AssemblyFlags.TrailEffects);
                if (forceEnable)
                    MarkForceEnableAttempted(gateway);

                bool enabledAfter;
                string enabledAfterText = ReadGatewayBehaviourEnabled(gateway, out enabledAfter);

                if (behaviour == null)
                    behaviour = GetVehicleBehaviourFromGateway(gateway);

                detail =
                    $"enabledBefore={enabledBeforeText} called=yes force={SpawnSummaryLog.YesNo(forceEnable)} " +
                    $"id={CleanLogValue(id.ToString())} " +
                    $"options={(int)options} factories={factories.Length} flags={AssemblyFlags.TrailEffects} " +
                    $"enabledAfter={enabledAfterText} behaviour={GetValueTypeName(behaviour)}";
                return behaviour;
            }
            catch (Exception ex)
            {
                if (forceEnable)
                    MarkForceEnableAttempted(gateway);

                detail =
                    $"enabledBefore={enabledBeforeText} called=error force={SpawnSummaryLog.YesNo(forceEnable)} " +
                    $"reason={DescribeException(ex)} behaviour={GetValueTypeName(existing)}";
                return existing;
            }
        }

        private static bool ShouldForceEnableBehaviour(IVehicleEditGateway gateway)
        {
            if (!ForceEnableBehaviourRegardlessOfState || gateway == null)
                return false;

            IntPtr pointer = GetGatewayPointer(gateway);
            return pointer == IntPtr.Zero || !forcedBehaviourEnableAttempts.Contains(pointer);
        }

        private static void MarkForceEnableAttempted(IVehicleEditGateway gateway)
        {
            IntPtr pointer = GetGatewayPointer(gateway);
            if (pointer != IntPtr.Zero)
                forcedBehaviourEnableAttempts.Add(pointer);
        }

        private static IntPtr GetGatewayPointer(IVehicleEditGateway gateway)
        {
            try { return gateway != null ? gateway.Pointer : IntPtr.Zero; }
            catch { return IntPtr.Zero; }
        }

        private static string ReadGatewayBehaviourEnabled(IVehicleEditGateway gateway, out bool enabled)
        {
            enabled = false;
            if (gateway == null) return "gateway=null";

            try
            {
                enabled = gateway.BehaviourEnabled;
                return SpawnSummaryLog.YesNo(enabled);
            }
            catch (Exception ex)
            {
                return "error:" + CleanLogValue(ex.Message);
            }
        }

        private static EntityID BuildBehaviourEntityId()
        {
            TeamID team = TeamID.Team1;
            GroupID group = GroupID.Group1;
            sbyte spawnerId = 1;

            try
            {
                if (cachedSpawner != null)
                {
                    team = cachedSpawner.TeamID;
                    group = cachedSpawner.GroupID;
                    spawnerId = cachedSpawner.SpawnerID;
                }
            }
            catch { }

            if (EqualityComparer<TeamID>.Default.Equals(team, default(TeamID)))
                team = TeamID.Team1;
            if (EqualityComparer<GroupID>.Default.Equals(group, default(GroupID)))
                group = GroupID.Group1;
            if (spawnerId == 0)
                spawnerId = 1;

            if (nextBehaviourBuildNumber > byte.MaxValue)
                nextBehaviourBuildNumber = 1;

            byte buildNumber = (byte)nextBehaviourBuildNumber++;
            return new EntityID(buildNumber, spawnerId, group, team);
        }

        // =====================================================================
        // Vehicle control
        // =====================================================================

        public static IEnumerator AssignVehicleControlWhenReady(
            IVehicleEditGateway gateway,
            int attempts = 12,
            float intervalSeconds = 0.25f,
            float initialDelaySeconds = 0.25f)
        {
            if (initialDelaySeconds > 0f)
                yield return new WaitForSeconds(initialDelaySeconds);

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                if (TryAssignVehicleControl(gateway, attempt))
                    yield break;

                if (attempt < attempts)
                    yield return new WaitForSeconds(intervalSeconds);
            }

            SpawnSummaryLog.Error($"controlAssign fail attempts={attempts}");
        }

        private static bool TryAssignVehicleControl(IVehicleEditGateway gateway, int attempt)
        {
            if (gateway == null)
            {
                MelonLogger.Warning("[VehicleSpawner] AssignVehicleControl: gateway is null.");
                SpawnSummaryLog.Warn($"controlAssign skipped attempt={attempt} reason=gatewayNull");
                return false;
            }

            try
            {
                var controller = GetVehicleController();
                if (controller == null)
                {
                    MelonLogger.Error("[VehicleSpawner] VehicleController not found.");
                    SpawnSummaryLog.Warn($"controlAssign retry attempt={attempt} reason=noController");
                    return false;
                }

                string behaviourDetail;
                var vehicle = EnsureBehaviourEnabled(gateway, out behaviourDetail);
                if (vehicle == null)
                {
                    MelonLogger.Error("[VehicleSpawner] Could not extract IVehicleBehaviour from gateway.");
                    SpawnSummaryLog.Warn($"controlAssign retry attempt={attempt} reason=noBehaviour {DescribeGatewayBehaviourState(gateway)} enable={behaviourDetail}");
                    return false;
                }

                SpawnSummaryLog.Info(
                    $"controlProbe attempt={attempt} {DescribeGatewayBehaviourState(gateway)} " +
                    $"enable={behaviourDetail} controller={DescribeComponent(controller)} " +
                    DescribeVehicleControlSurface(vehicle));
                controller.ControlledVehicle = vehicle;

                MelonLogger.Msg("[VehicleSpawner] Vehicle control assigned.");
                SpawnSummaryLog.Info($"controlAssign success attempt={attempt}");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[VehicleSpawner] AssignVehicleControl: {ex.Message}");
                SpawnSummaryLog.Warn($"controlAssign retry attempt={attempt} reason={DescribeException(ex)}");
                return false;
            }
        }

        private static string DescribeGatewayBehaviourState(IVehicleEditGateway gateway)
        {
            if (gateway == null) return "gateway=null";

            bool enabled;
            string enabledText = ReadGatewayBehaviourEnabled(gateway, out enabled);
            return $"gateway={GetValueTypeName(gateway)} behaviourEnabled={enabledText}";
        }

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

                string behaviourDetail;
                var vehicle = EnsureBehaviourEnabled(gateway, out behaviourDetail);
                if (vehicle == null)
                {
                    MelonLogger.Error("[VehicleSpawner] Could not extract IVehicleBehaviour from gateway.");
                    return;
                }

                SpawnSummaryLog.Info(
                    $"controlProbe attempt=1 {DescribeGatewayBehaviourState(gateway)} " +
                    $"enable={behaviourDetail} controller={DescribeComponent(controller)} " +
                    DescribeVehicleControlSurface(vehicle));
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
                string beforeCount = DescribeRegisterCount(reg, false);
                string beforeBehaviours = DescribeRegisterCount(reg, true);
                reg.Register(gateway);
                string afterCount = DescribeRegisterCount(reg, false);
                string afterBehaviours = DescribeRegisterCount(reg, true);
                SpawnSummaryLog.Info(
                    $"registerProbe register={GetValueTypeName(reg)} " +
                    $"vehiclesBefore={beforeCount} vehiclesAfter={afterCount} " +
                    $"behavioursBefore={beforeBehaviours} behavioursAfter={afterBehaviours}");
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

        private static string DescribeRegisterCount(IVehicleRegister register, bool behaviours)
        {
            if (register == null)
                return "null";

            try
            {
                return behaviours
                    ? DescribeCountedCollection(register.Behaviours, "")
                    : DescribeCountedCollection(register.RegisteredVehicles, "");
            }
            catch (Exception ex)
            {
                return "error:" + CleanLogValue(ex.Message);
            }
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
                SpawnSummaryLog.Info(
                    "sceneDeps ready cached=yes " +
                    $"stage={DescribeStageEvent(cachedStageEvent)} " +
                    $"source={DescribeComponent(cachedSource)} " +
                    $"spawner={DescribeComponent(cachedSpawner)} " +
                    $"register={GetValueTypeName(cachedRegister)} " +
                    $"stageFactories={(cachedStageFactories?.Length ?? 0)}");
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
            VehicleRuntimeDiagnostics.LogState("sceneDeps");
            GetVehicleRegister();
            EnsureDetectionRuntime();

            bool ready = cachedFactory != null &&
                         cachedTechFrame != null &&
                         cachedRegister != null &&
                         VehicleRuntimeDiagnostics.GetGlobalVehicleResources() != null &&
                         VehicleRuntimeDiagnostics.GetVehiclesMainResources() != null &&
                         IsDetectionRuntimeReady() &&
                         cachedStageFactories != null &&
                         cachedStageFactories.Length > 0;

            if (!ready)
            {
                SpawnSummaryLog.Warn($"sceneDeps incomplete missing={GetMissingSceneComponents()}");
                MelonLogger.Warning(
                    "[VehicleSpawner] Scene components incomplete: " +
                    $"factory={(cachedFactory != null ? "yes" : "no")} " +
                    $"factoryStatic={(VehicleRuntimeDiagnostics.GetStaticVehicleFactory() != null ? "yes" : "no")} " +
                    $"techFrame={(cachedTechFrame != null ? "yes" : "no")} " +
                    $"vehicleResources={(VehicleRuntimeDiagnostics.GetGlobalVehicleResources() != null ? "yes" : "no")} " +
                    $"vehiclesMain={(VehicleRuntimeDiagnostics.GetVehiclesMainResources() != null ? "yes" : "no")} " +
                    $"detection={(IsDetectionRuntimeReady() ? "yes" : "no")} " +
                    $"register={(cachedRegister != null ? "yes" : "no")} " +
                    $"stageFactories={(cachedStageFactories?.Length ?? 0)}");
            }
            else
            {
                SpawnSummaryLog.Info(
                    "sceneDeps ready cached=no " +
                    $"stage={DescribeStageEvent(cachedStageEvent)} " +
                    $"source={DescribeComponent(cachedSource)} " +
                    $"spawner={DescribeComponent(cachedSpawner)} " +
                    $"factoryStatic={SpawnSummaryLog.YesNo(VehicleRuntimeDiagnostics.GetStaticVehicleFactory() != null)} " +
                    $"vehicleResources={SpawnSummaryLog.YesNo(VehicleRuntimeDiagnostics.GetGlobalVehicleResources() != null)} " +
                    $"vehiclesMain={SpawnSummaryLog.YesNo(VehicleRuntimeDiagnostics.GetVehiclesMainResources() != null)} " +
                    $"detection={SpawnSummaryLog.YesNo(IsDetectionRuntimeReady())} " +
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

            cachedStageEvent = selected;
            SpawnSummaryLog.Info(
                "stageSelect " +
                $"selected={DescribeStageEvent(selected)} " +
                $"mode={GetStageMode(selected)}");

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

            try
            {
                if (cachedSpawnLocator == null && selected.spawnLocator != null)
                {
                    cachedSpawnLocator = selected.spawnLocator;
                    MelonLogger.Msg($"[VehicleSpawner] SpawnLocator selected from stage event '{GetPath(selected.transform)}'.");
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

            if (cachedStageEvent != null)
            {
                try
                {
                    var reg = cachedStageEvent.register;
                    if (reg != null)
                    {
                        cachedRegister = reg;
                        MelonLogger.Msg($"[VehicleSpawner] IVehicleRegister found on selected stage event '{GetPath(cachedStageEvent.transform)}'.");
                        return reg;
                    }
                }
                catch { }
            }

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

        private static bool EnsureDetectionRuntime()
        {
            bool serviceBefore = SafeDetectionServiceReady();
            bool registerBefore = SafeDetectionRegisterReady();
            bool visualBefore = SafeEntityVisualFeatureSourceReady();
            string source = "existing";
            string detail = "";

            try
            {
                if (!serviceBefore || !registerBefore)
                {
                    try { DetectionRegisterFactory.Initiate(); } catch { }

                    IDetectionController controller = null;

                    try
                    {
                        var factory = IDetectionRegisterFactory.Instance;
                        controller = factory?.Create(256);
                    }
                    catch (Exception ex)
                    {
                        detail = "factoryCreate=" + CleanLogValue(ex.Message);
                    }

                    if (controller == null)
                    {
                        try
                        {
                            var concrete = new DetectionRegister(256);
                            controller = concrete.TryCast<IDetectionController>() ??
                                         new Il2CppSystem.Object(concrete.Pointer).TryCast<IDetectionController>();
                        }
                        catch (Exception ex)
                        {
                            detail = "directCreate=" + CleanLogValue(ex.Message);
                        }
                    }

                    if (controller != null)
                    {
                        IDetectionService.Instance = controller.TryCast<IDetectionService>() ??
                                                     new Il2CppSystem.Object(controller.Pointer).TryCast<IDetectionService>();
                        IDetectionRegister.Instance = controller.TryCast<IDetectionRegister>() ??
                                                      new Il2CppSystem.Object(controller.Pointer).TryCast<IDetectionRegister>();
                        source = "created";
                    }
                }

                if (!SafeEntityVisualFeatureSourceReady() && cachedRegister != null)
                {
                    var visual = AsIl2CppObject(cachedRegister)?.TryCast<IEntityVisualFeatureSource>();
                    if (visual != null)
                    {
                        IEntityVisualFeatureSource.Instance = visual;
                        source = source == "existing" ? "vehicleRegister" : source + "+vehicleRegister";
                    }
                }
            }
            catch (Exception ex)
            {
                detail = CleanLogValue(ex.Message);
            }

            SpawnSummaryLog.Info(
                "detectionRuntime " +
                $"beforeService={SpawnSummaryLog.YesNo(serviceBefore)} " +
                $"beforeRegister={SpawnSummaryLog.YesNo(registerBefore)} " +
                $"beforeVisual={SpawnSummaryLog.YesNo(visualBefore)} " +
                $"service={SpawnSummaryLog.YesNo(SafeDetectionServiceReady())} " +
                $"register={SpawnSummaryLog.YesNo(SafeDetectionRegisterReady())} " +
                $"visual={SpawnSummaryLog.YesNo(SafeEntityVisualFeatureSourceReady())} " +
                $"source={source} detail={detail}");

            return IsDetectionRuntimeReady();
        }

        private static bool IsDetectionRuntimeReady()
        {
            return SafeDetectionServiceReady() &&
                   SafeDetectionRegisterReady() &&
                   SafeEntityVisualFeatureSourceReady();
        }

        private static bool SafeDetectionServiceReady()
        {
            try { return IDetectionService.Instance != null; }
            catch { return false; }
        }

        private static bool SafeDetectionRegisterReady()
        {
            try { return IDetectionRegister.Instance != null; }
            catch { return false; }
        }

        private static bool SafeEntityVisualFeatureSourceReady()
        {
            try { return IEntityVisualFeatureSource.Instance != null; }
            catch { return false; }
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

                // Otherwise check common gateway result properties.
                foreach (var propertyName in new[] { "Behaviour", "Vehicle", "ControlledVehicle" })
                {
                    var prop = obj.GetIl2CppType().GetProperty(propertyName);
                    if (prop == null) continue;

                    var raw = prop.GetValue(obj);
                    var behaviour = raw?.TryCast<IVehicleBehaviour>();
                    if (behaviour != null) return behaviour;
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
                var behaviourObject = behaviour?.TryCast<MonoBehaviour>()?.gameObject;
                if (behaviourObject != null)
                    return behaviourObject;

                return new Il2CppSystem.Object(gateway.Pointer).TryCast<MonoBehaviour>()?.gameObject;
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
            cachedStageEvent = null;
            cachedSource     = null;
            cachedFactory    = null;
            cachedTechFrame  = null;
            cachedRegister   = null;
            cachedSpawner    = null;
            cachedSpawnLocator = null;
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

        private static VehicleSpawnInstance BuildSpawnInstance(Vector3 fallbackPosition, Quaternion fallbackRotation, out string source)
        {
            source = "manual";
            var position = fallbackPosition;
            var rotation = fallbackRotation;
            var teamId = cachedSpawner != null ? cachedSpawner.TeamID : default(TeamID);
            var groupId = cachedSpawner != null ? cachedSpawner.GroupID : default(GroupID);

            if (UseSpawnLocatorForDirectSpawn && cachedSpawnLocator != null)
            {
                try
                {
                    SpawnPointInfo point;
                    cachedSpawnLocator.Get(out point);
                    position = point.Position;
                    rotation = point.Rotation;
                    if (!EqualityComparer<TeamID>.Default.Equals(point.TeamIdOverride, default(TeamID)))
                        teamId = point.TeamIdOverride;
                    if (!EqualityComparer<GroupID>.Default.Equals(point.GroupIdOverride, default(GroupID)))
                        groupId = point.GroupIdOverride;
                    source = "locator";
                }
                catch (Exception ex)
                {
                    source = "manual(locatorError=" + CleanLogValue(ex.Message) + ")";
                }
            }

            return new VehicleSpawnInstance
            {
                position = position,
                rotation = rotation,
                teamIdOverride = teamId,
                groupIdOverride = groupId
            };
        }

        private static IVehicleEditGateway TryExtractGatewayFromSpawnResult(object raw, out string rawType, out string summary)
        {
            rawType = GetValueTypeName(raw);
            try { rawType += " clr=" + raw.GetType().FullName; } catch { }
            summary = "vehicleSpawn=no spawned=no arrayCount=0";

            if (raw == null) return null;

            try
            {
                if (raw is Il2CppReferenceArray<VehicleSpawn> spawnArray)
                {
                    int count = spawnArray.Length;
                    for (int i = 0; i < count; i++)
                    {
                        var spawn = spawnArray[i];
                        if (spawn == null) continue;

                        bool spawned = false;
                        try { spawned = spawn.Spawned; } catch { }
                        summary = $"vehicleSpawn=yes spawned={SpawnSummaryLog.YesNo(spawned)} arrayCount={count}";
                        if (spawn.Vehicle != null)
                            return spawn.Vehicle;
                    }

                    summary = $"vehicleSpawn=yes spawned=no arrayCount={count}";
                    return null;
                }
            }
            catch (Exception ex)
            {
                summary = "vehicleSpawn=arrayError reason=" + CleanLogValue(ex.Message);
            }

            try
            {
                var il2Array = AsIl2CppObject(raw)?.TryCast<Il2CppSystem.Array>();
                if (il2Array != null)
                {
                    int count = il2Array.Length;
                    for (int i = 0; i < count; i++)
                    {
                        var element = il2Array.GetValue(i);
                        var spawn = AsIl2CppObject(element)?.TryCast<VehicleSpawn>();
                        if (spawn == null) continue;

                        bool spawned = false;
                        try { spawned = spawn.Spawned; } catch { }
                        summary = $"vehicleSpawn=yes spawned={SpawnSummaryLog.YesNo(spawned)} arrayCount={count} arrayKind=il2cpp";
                        if (spawn.Vehicle != null)
                            return spawn.Vehicle;
                    }

                    summary = $"vehicleSpawn=yes spawned=no arrayCount={count} arrayKind=il2cpp";
                    return null;
                }
            }
            catch (Exception ex)
            {
                summary = "vehicleSpawn=il2cppArrayError reason=" + CleanLogValue(ex.Message);
            }

            try
            {
                if (raw is VehicleSpawn[] managedArray)
                {
                    int count = managedArray.Length;
                    for (int i = 0; i < count; i++)
                    {
                        var spawn = managedArray[i];
                        if (spawn == null) continue;

                        bool spawned = false;
                        try { spawned = spawn.Spawned; } catch { }
                        summary = $"vehicleSpawn=yes spawned={SpawnSummaryLog.YesNo(spawned)} arrayCount={count}";
                        if (spawn.Vehicle != null)
                            return spawn.Vehicle;
                    }

                    summary = $"vehicleSpawn=yes spawned=no arrayCount={count}";
                    return null;
                }
            }
            catch (Exception ex)
            {
                summary = "vehicleSpawn=managedArrayError reason=" + CleanLogValue(ex.Message);
            }

            try
            {
                var vehicleSpawn = AsIl2CppObject(raw)?.TryCast<VehicleSpawn>();
                if (vehicleSpawn != null)
                {
                    bool spawned = false;
                    try { spawned = vehicleSpawn.Spawned; } catch { }
                    summary = $"vehicleSpawn=yes spawned={SpawnSummaryLog.YesNo(spawned)} arrayCount=0";
                    return vehicleSpawn.Vehicle;
                }
            }
            catch { }

            try
            {
                var behaviour = AsIl2CppObject(raw)?.TryCast<IVehicleBehaviour>();
                if (behaviour != null)
                {
                    MelonLogger.Msg("[VehicleSpawner] Result is IVehicleBehaviour — calling EnableBehaviour()...");
                    summary = "vehicleSpawn=no spawned=no arrayCount=0 behaviourDirect=yes";
                    return null;
                }
            }
            catch { }

            try
            {
                var gateway = AsIl2CppObject(raw)?.TryCast<IVehicleEditGateway>();
                if (gateway != null)
                {
                    summary = "vehicleSpawn=no spawned=no arrayCount=0 gatewayDirect=yes";
                    return gateway;
                }
            }
            catch { }

            return null;
        }

        private static bool IsTaskFaulted(Il2CppSystem.Object task)
        {
            string error;
            return ReadIl2CppBoolProperty(task, "IsFaulted", out error);
        }

        private static string DescribeTaskState(Il2CppSystem.Object task)
        {
            string completedError;
            string faultedError;
            string canceledError;
            bool completed = ReadIl2CppBoolProperty(task, "IsCompleted", out completedError);
            bool faulted = ReadIl2CppBoolProperty(task, "IsFaulted", out faultedError);
            bool canceled = ReadIl2CppBoolProperty(task, "IsCanceled", out canceledError);

            string status = ReadIl2CppPropertyString(task, "Status");
            string errors = "";
            if (!string.IsNullOrEmpty(completedError)) errors += " completedError=" + completedError;
            if (!string.IsNullOrEmpty(faultedError)) errors += " faultedError=" + faultedError;
            if (!string.IsNullOrEmpty(canceledError)) errors += " canceledError=" + canceledError;

            return CleanLogValue(
                $"completed={SpawnSummaryLog.YesNo(completed)} " +
                $"faulted={SpawnSummaryLog.YesNo(faulted)} " +
                $"canceled={SpawnSummaryLog.YesNo(canceled)} " +
                $"status={status}{errors}");
        }

        private static string DescribeTaskException(Il2CppSystem.Object task)
        {
            string error;
            object value = ReadIl2CppProperty(task, "Exception", out error);
            if (value == null)
                return string.IsNullOrEmpty(error) ? "taskException=null" : "taskExceptionError=" + error;

            return DescribeIl2CppExceptionObject(value);
        }

        private static object ReadIl2CppProperty(Il2CppSystem.Object target, string propertyName, out string error)
        {
            error = "";
            if (target == null)
            {
                error = "target=null";
                return null;
            }

            try
            {
                var type = target.GetIl2CppType();
                var property = type?.GetProperty(propertyName);
                if (property == null)
                {
                    error = "propertyMissing:" + propertyName;
                    return null;
                }

                return property.GetValue(target);
            }
            catch (Exception ex)
            {
                error = DescribeException(ex);
                return null;
            }
        }

        private static bool ReadIl2CppBoolProperty(Il2CppSystem.Object target, string propertyName, out string error)
        {
            object value = ReadIl2CppProperty(target, propertyName, out error);
            if (value == null) return false;

            var il2Value = AsIl2CppObject(value);
            try { return il2Value != null && il2Value.Unbox<bool>(); }
            catch { }

            try { return string.Equals(value.ToString(), "True", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static string ReadIl2CppPropertyString(Il2CppSystem.Object target, string propertyName)
        {
            string error;
            object value = ReadIl2CppProperty(target, propertyName, out error);
            if (value == null)
                return string.IsNullOrEmpty(error) ? "null" : error;

            return CompactLogValue(value.ToString());
        }

        private static string DescribeIl2CppExceptionObject(object value)
        {
            var current = AsIl2CppObject(value)?.TryCast<Il2CppSystem.Exception>();
            if (current == null)
                return CleanLogValue($"{GetValueTypeName(value)}:{CompactLogValue(value.ToString())}");

            var parts = new List<string>();
            int depth = 0;

            while (current != null && depth++ < 8)
            {
                string typeName = GetValueTypeName(current);
                string message = "";
                try { message = current.Message; } catch { }
                string stack = "";
                try { stack = CompactStackTrace(current.StackTrace); } catch { }

                parts.Add($"{typeName}:{CompactLogValue(message)}{stack}");

                try { current = current.InnerException; }
                catch { break; }
            }

            return string.Join(" <- ", parts.ToArray());
        }

        private static string CompactStackTrace(string stackTrace)
        {
            stackTrace = CleanLogValue(stackTrace);
            if (string.IsNullOrEmpty(stackTrace)) return "";

            const int maxLength = 420;
            if (stackTrace.Length > maxLength)
                stackTrace = stackTrace.Substring(0, maxLength) + "...";

            return " stack=" + stackTrace;
        }

        private static void LogSpawnProbe(string tankName, IVehicleBlueprint blueprint, Vector3 requestedPosition, Quaternion requestedRotation, bool stageEventExperiment)
        {
            string nativeBlueprint = "notRead";
            try
            {
                nativeBlueprint = DescribeBlueprint(cachedSource?.GetBlueprint());
            }
            catch (Exception ex)
            {
                nativeBlueprint = "error:" + CleanLogValue(ex.Message);
            }

            string route = stageEventExperiment ? "stageEvent" : "direct";
            string message =
                $"spawnProbe tag=DEBUG-spawn-path route={route} tank={tankName} " +
                $"selectedBlueprint={DescribeBlueprint(blueprint)} " +
                $"nativeSourceBlueprint={nativeBlueprint} " +
                $"stage={DescribeStageEvent(cachedStageEvent)} " +
                $"source={DescribeVehicleSource(cachedSource)} " +
                $"spawner={DescribeSpawner(cachedSpawner)} " +
                $"locator={DescribeSpawnLocator(cachedSpawnLocator)} " +
                $"requestedPosition={FormatVector(requestedPosition)} " +
                $"requestedRotation={FormatQuaternion(requestedRotation)}";

            SpawnSummaryLog.Info(message);
            MelonLogger.Msg($"{SpawnPathDebugTag} {message}");
        }

        private static void PrimeStageEventForSpawn()
        {
            if (cachedStageEvent == null)
                return;

            float initialBudget = 0f;
            float remainingBefore = 0f;
            float remainingAfter = 0f;
            bool primed = false;
            string detail = "skip";

            try { initialBudget = cachedStageEvent.InitialCostBudget; } catch { }
            try { remainingBefore = cachedStageEvent.RemainingCostBudget; } catch { }

            if (initialBudget > 0f && remainingBefore <= 0.001f)
            {
                primed = TrySetStageEventRemainingCostBudget(cachedStageEvent, initialBudget, out detail);
                try { remainingAfter = cachedStageEvent.RemainingCostBudget; } catch { remainingAfter = -1f; }
            }
            else
            {
                remainingAfter = remainingBefore;
                detail = "notNeeded";
            }

            SpawnSummaryLog.Info(
                $"stagePrime budget initial={initialBudget:0.##} before={remainingBefore:0.##} " +
                $"after={remainingAfter:0.##} primed={SpawnSummaryLog.YesNo(primed)} detail={detail}");
        }

        private static bool TrySetStageEventRemainingCostBudget(StageVehicleSpawnEvent stageEvent, float value, out string detail)
        {
            detail = "";
            if (stageEvent == null)
            {
                detail = "stageEvent null";
                return false;
            }

            try
            {
                var field = typeof(StageVehicleSpawnEvent).GetField(
                    "remainingCostBudget",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(stageEvent, value);
                    detail = "managed field";
                    return true;
                }
            }
            catch (Exception ex)
            {
                detail = "managed field failed: " + CleanLogValue(ex.Message);
            }

            try
            {
                if (stageEvent.Pointer != IntPtr.Zero)
                {
                    byte[] bytes = BitConverter.GetBytes(value);
                    Marshal.Copy(bytes, 0, IntPtr.Add(stageEvent.Pointer, 0x60), 4);
                    detail = "native offset 0x60";
                    return true;
                }
            }
            catch (Exception ex)
            {
                detail = "native write failed: " + CleanLogValue(ex.Message);
            }

            if (string.IsNullOrEmpty(detail))
                detail = "no write path";
            return false;
        }

        private static string DescribeBlueprint(IVehicleBlueprint blueprint)
        {
            if (blueprint == null) return "null";

            string pointer = "";
            try
            {
                if (blueprint is Il2CppObjectBase obj && obj.Pointer != IntPtr.Zero)
                    pointer = " ptr=0x" + obj.Pointer.ToInt64().ToString("X");
            }
            catch { }

            return CleanLogValue(GetValueTypeName(blueprint) + pointer);
        }

        private static string DescribeVehicleSource(VehicleSource source)
        {
            if (source == null) return "null";

            try
            {
                return CleanLogValue(
                    $"{DescribeComponent(source)} mode={source.Mode} blueprintName={source.BlueprintName ?? ""} " +
                    $"subdir={source.Subdirectory ?? ""} source={source.Source} minTech={source.MinTechDate} maxTech={source.MaxTechDate}");
            }
            catch (Exception ex)
            {
                return "error:" + CleanLogValue(ex.Message);
            }
        }

        private static string DescribeSpawner(VehicleSpawner spawner)
        {
            if (spawner == null) return "null";

            try
            {
                return CleanLogValue(
                    $"{DescribeComponent(spawner)} team={spawner.TeamID} group={spawner.GroupID} " +
                    $"spawnerId={spawner.SpawnerID} appendNameID={spawner.appendNameID} nameOverride={spawner.nameOverride ?? ""}");
            }
            catch (Exception ex)
            {
                return "error:" + CleanLogValue(ex.Message);
            }
        }

        private static string DescribeSpawnLocator(SpawnLocator locator)
        {
            if (locator == null) return "null";

            try
            {
                return CleanLogValue($"{DescribeComponent(locator)} max={locator.MaxSpawn} next={locator.NextSpawnIndex}");
            }
            catch (Exception ex)
            {
                return "error:" + CleanLogValue(ex.Message);
            }
        }

        private static string DescribeSpawnInstance(VehicleSpawnInstance instance)
        {
            return CleanLogValue(
                $"pos={FormatVector(instance.position)} rot={FormatQuaternion(instance.rotation)} " +
                $"team={instance.teamIdOverride} group={instance.groupIdOverride}");
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

        private static string DescribeStageEvent(StageVehicleSpawnEvent stageEvent)
        {
            if (stageEvent == null) return "null";
            return GetPath(stageEvent.transform);
        }

        private static string GetStageMode(StageVehicleSpawnEvent stageEvent)
        {
            if (stageEvent == null) return "null";

            try
            {
                return stageEvent.blueprintSource != null
                    ? stageEvent.blueprintSource.Mode.ToString()
                    : "none";
            }
            catch
            {
                return "unknown";
            }
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:0.##},{value.y:0.##},{value.z:0.##})";
        }

        private static string FormatQuaternion(Quaternion value)
        {
            return $"({value.x:0.##},{value.y:0.##},{value.z:0.##},{value.w:0.##})";
        }

        private static string CleanLogValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ');
        }

        private static string CompactLogValue(string value)
        {
            value = CleanLogValue(value);
            if (string.IsNullOrEmpty(value)) return "";

            int il2CppStack = value.IndexOf("--- BEGIN IL2CPP STACK TRACE ---", StringComparison.Ordinal);
            if (il2CppStack >= 0)
                value = value.Substring(0, il2CppStack).Trim();

            const int maxLength = 360;
            if (value.Length > maxLength)
                value = value.Substring(0, maxLength) + "...";

            return value;
        }

        private static string DescribeException(Exception ex)
        {
            if (ex == null) return "";

            var parts = new List<string>();
            Exception current = ex;
            int depth = 0;

            while (current != null && depth++ < 8)
            {
                parts.Add($"{current.GetType().Name}:{CompactLogValue(current.Message)}");

                if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
                {
                    current = aggregate.InnerExceptions[0];
                    continue;
                }

                current = current.InnerException;
            }

            return string.Join(" <- ", parts.ToArray());
        }

        private static string GetMissingSceneComponents()
        {
            var missing = new List<string>();
            if (cachedSource == null) missing.Add("source");
            if (cachedSpawner == null) missing.Add("officialSpawner");
            if (cachedFactory == null) missing.Add("factory");
            if (cachedTechFrame == null) missing.Add("techFrame");
            if (VehicleRuntimeDiagnostics.GetGlobalVehicleResources() == null) missing.Add("vehicleResources");
            if (VehicleRuntimeDiagnostics.GetVehiclesMainResources() == null) missing.Add("vehiclesMain");
            if (!IsDetectionRuntimeReady()) missing.Add("detection");
            if (cachedRegister == null) missing.Add("register");
            if (cachedStageFactories == null || cachedStageFactories.Length <= 0) missing.Add("stageFactories");
            return missing.Count == 0 ? "none" : string.Join(",", missing.ToArray());
        }

        private static string DescribeVehicleControlSurface(IVehicleBehaviour vehicle)
        {
            if (vehicle == null)
                return "vehicle=null";

            string moduleSummary = "";
            try
            {
                moduleSummary =
                    $"body={SpawnSummaryLog.YesNo(vehicle.Rigidbody != null)} " +
                    $"health={SpawnSummaryLog.YesNo(vehicle.Health != null)} " +
                    $"structure={SpawnSummaryLog.YesNo(vehicle.VehicleStructure != null)}";
            }
            catch (Exception ex)
            {
                moduleSummary = "coreProbeError=" + CleanLogValue(ex.Message);
            }

            string roleSummary = "";
            try
            {
                string moduleError;
                object modules = ReadIl2CppProperty(AsIl2CppObject(vehicle), "Modules", out moduleError);
                roleSummary = " modules=" + DescribeCountedCollection(modules, moduleError);
            }
            catch (Exception ex)
            {
                roleSummary = " roleProbeError=" + CleanLogValue(ex.Message);
            }

            return moduleSummary + roleSummary;
        }

        private static string DescribeCountedCollection(object value, string error)
        {
            if (value == null)
                return string.IsNullOrEmpty(error) ? "null" : "error:" + error;

            try
            {
                var prop = value.GetType().GetProperty("Count");
                if (prop != null)
                    return Convert.ToString(prop.GetValue(value, null));
            }
            catch { }

            try
            {
                var il2Object = AsIl2CppObject(value);
                var prop = il2Object?.GetIl2CppType()?.GetProperty("Count");
                if (prop != null)
                    return Convert.ToString(prop.GetValue(il2Object, null));
            }
            catch { }

            return GetValueTypeName(value);
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
