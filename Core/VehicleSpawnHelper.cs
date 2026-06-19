using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using Il2CppSprocket.Gameplay.VehicleControl;
using Il2CppSprocket.TechTrees;
using UnityEngine;
using MelonLoader;
using Il2CppSprocket.Vehicles;
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
        private static VehicleRegister cachedRegister;
        private static VehicleController cachedController;

        private static readonly string blueprintBasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "Sprocket", "Factions", "AllowedVehicles", "Blueprints", "Vehicles"
        );

        // =====================================================================
        // Initialization — scans blueprint folder
        // =====================================================================

        public static void Initialize()
        {
            if (isInitialized) return;

            MelonLogger.Msg("[VehicleSpawner] Initializing...");

            if (!Directory.Exists(blueprintBasePath))
            {
                MelonLogger.Warning($"[VehicleSpawner] Blueprint folder not found: {blueprintBasePath}");
                isInitialized = true;
                return;
            }

            var files = Directory.GetFiles(blueprintBasePath, "*.blueprint", SearchOption.AllDirectories);
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
            EnsureInitialized();

            // Resolve blueprint name, falling back to default if needed
            if (!blueprintPaths.ContainsKey(tankName))
            {
                MelonLogger.Warning($"[VehicleSpawner] '{tankName}' not in blueprint list.");
                if (defaultTankId == null)
                {
                    MelonLogger.Error("[VehicleSpawner] No blueprints available.");
                    yield break;
                }
                tankName = defaultTankId;
                MelonLogger.Msg($"[VehicleSpawner] Falling back to default: {tankName}");
            }

            // Load blueprint from disk
            IVehicleBlueprint blueprint;
            try   { blueprint = LoadBlueprint(tankName); }
            catch (Exception ex) { MelonLogger.Error($"[VehicleSpawner] LoadBlueprint: {ex.Message}"); yield break; }
            if (blueprint == null) yield break;

            // Ensure scene components are cached
            if (!EnsureSceneComponents())
            {
                MelonLogger.Error($"[VehicleSpawner] Could not spawn '{tankName}' — scene components not ready.");
                yield break;
            }

            // -- Spawn via IVehicleFactory.Create(blueprint, techFrame, flags, ct) --
            // Per the game dev: this is the correct path.
            // cachedFactory is the VehicleFactories singleton (IVehicleFactory).
            // cachedTechFrame is the ITechFrame from the spawner GO (controls era unlock filter).
            // VehicleSpawnFlags and CancellationToken are passed as default/zero values.
            var cts = new Il2CppSystem.Threading.CancellationTokenSource();
            Il2CppSystem.Object rawTask = null;

            try
            {
                var il2Flags = Il2CppSystem.Reflection.BindingFlags.Public   |
                               Il2CppSystem.Reflection.BindingFlags.NonPublic |
                               Il2CppSystem.Reflection.BindingFlags.Instance;

                foreach (var m in cachedFactory.GetIl2CppType().GetMethods(il2Flags))
                {
                    if (m.Name != "Create" || m.GetParameters().Count != 4) continue;

                    var args = new[]
                    {
                        blueprint.Cast<Il2CppSystem.Object>(),
                        cachedTechFrame,
                        Il2CppSystem.Enum.ToObject(Il2CppType.Of<VehicleSpawnFlags>(), 0),
                        cts.Token.Cast<Il2CppSystem.Object>()
                    };

                    rawTask = m.Invoke(cachedFactory, args);
                    MelonLogger.Msg($"[VehicleSpawner] Create() returned: {rawTask?.GetIl2CppType()?.FullName ?? "null"}");
                    break;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[VehicleSpawner] Create() threw: {ex.Message}");
                if (ex.InnerException != null)
                    MelonLogger.Error($"[VehicleSpawner]   inner: {ex.InnerException.Message}");
                yield break;
            }

            if (rawTask == null)
            {
                MelonLogger.Error($"[VehicleSpawner] Could not spawn '{tankName}' — Create() returned null.");
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
                MelonLogger.Error("[VehicleSpawner] Create() task timed out.");
                try { cts.Cancel(); } catch { }
                yield break;
            }

            // Extract IVehicleEditGateway from task result
            // Per the dev: Create() returns Task<IVehicleEditGateway> directly.
            // After getting the gateway, call EnableBehaviour() to activate the vehicle.
            IVehicleEditGateway gateway = null;
            try
            {
                var resultProp = rawTask.GetIl2CppType().GetProperty("Result");
                if (resultProp == null)
                {
                    MelonLogger.Error("[VehicleSpawner] Task has no Result property.");
                    yield break;
                }

                var raw = resultProp.GetValue(rawTask);
                if (raw == null)
                {
                    MelonLogger.Error("[VehicleSpawner] Task.Result is null.");
                    yield break;
                }

                // Path A: result is IVehicleBehaviour - call EnableBehaviour() to get gateway
                var behaviour = raw.TryCast<IVehicleBehaviour>();
                if (behaviour != null)
                {
                    MelonLogger.Msg("[VehicleSpawner] Result is IVehicleBehaviour — calling EnableBehaviour()...");
                    gateway = EnableBehaviour(behaviour);
                }

                // Path B: result is already IVehicleEditGateway
                if (gateway == null)
                    gateway = raw.TryCast<IVehicleEditGateway>();

                if (gateway == null)
                {
                    MelonLogger.Error($"[VehicleSpawner] Could not extract gateway from result type: {raw.GetIl2CppType()?.FullName ?? "unknown"}");
                    yield break;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[VehicleSpawner] Result extraction failed: {ex.Message}");
                yield break;
            }

            PositionVehicle(gateway, position, rotation);
            RegisterVehicle(gateway);

            MelonLogger.Msg($"[VehicleSpawner] '{tankName}' spawned at {position}.");
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

        // Ensures VehicleSource, VehicleFactories, and ITechFrame are cached.
        // Returns true if the factory is ready to spawn.
        private static bool EnsureSceneComponents()
        {
            if (cachedSource != null && cachedFactory != null) return true;

            MelonLogger.Msg("[VehicleSpawner] Searching scene for spawner components...");

            var scenarioGO = GameObject.Find("Scenario");
            if (scenarioGO == null)
            {
                MelonLogger.Warning("[VehicleSpawner] 'Scenario' not found in scene.");
                return false;
            }

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

                if (cachedSource != null) break;
            }

            if (cachedSource == null)
            {
                MelonLogger.Warning("[VehicleSpawner] VehicleSource not found.");
                return false;
            }

            // Grab VehicleFactories singleton — this is the IVehicleFactory implementation
            if (cachedFactory == null)
            {
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
                            MelonLogger.Msg($"[VehicleSpawner] VehicleFactories instance cached.");
                        }
                        else
                        {
                            MelonLogger.Warning("[VehicleSpawner] VehicleFactories.instance is null — DI pipeline may not have run yet.");
                        }
                    }
                    else
                    {
                        MelonLogger.Warning("[VehicleSpawner] VehicleFactories instance property not found.");
                    }
                }
                catch (Exception ex) { MelonLogger.Warning($"[VehicleSpawner] VehicleFactories lookup: {ex.Message}"); }
            }

            return cachedFactory != null;
        }

        private static VehicleRegister GetVehicleRegister()
        {
            if (cachedRegister != null) return cachedRegister;

            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null || mb.Pointer == IntPtr.Zero) continue;
                try
                {
                    var reg = new Il2CppSystem.Object(mb.Pointer).TryCast<VehicleRegister>();
                    if (reg != null)
                    {
                        cachedRegister = reg;
                        MelonLogger.Msg("[VehicleSpawner] VehicleRegister found.");
                        return reg;
                    }
                }
                catch { }
            }

            MelonLogger.Warning("[VehicleSpawner] VehicleRegister not found.");
            return null;
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
            if (cachedFactory == null) return false;

            try
            {
                var il2Flags     = Il2CppSystem.Reflection.BindingFlags.Public   |
                                   Il2CppSystem.Reflection.BindingFlags.NonPublic |
                                   Il2CppSystem.Reflection.BindingFlags.Instance;
                var contextField = cachedFactory.GetIl2CppType().GetField("context", il2Flags);
                // If the field doesn't exist on this build, assume ready
                if (contextField == null) return true;
                return contextField.GetValue(cachedFactory) != null;
            }
            catch { return false; }
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

        // =====================================================================
        // Public query API
        // =====================================================================

        public static string       GetDefaultTankId()     { EnsureInitialized(); return defaultTankId; }
        public static List<string> GetAvailableTankIds()  { EnsureInitialized(); return new List<string>(availableTankIds); }
        public static bool         HasTank(string tankId) { EnsureInitialized(); return blueprintPaths.ContainsKey(tankId); }
        public static int          GetTankCount()         { EnsureInitialized(); return blueprintPaths.Count; }
    }
}