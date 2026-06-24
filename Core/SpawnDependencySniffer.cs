using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSprocket;
using Il2CppSprocket.Gameplay.VehicleControl;
using Il2CppSprocket.Spawning;
using Il2CppSprocket.TechTrees;
using Il2CppSprocket.Vehicles;
using Il2CppSprocket.Vehicles.Missions;
using Il2CppSprocket.Vehicles.Spawning;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SprocketMultiplayer.Core
{
    public static class SpawnDependencySniffer
    {
        private static bool running;
        private static bool techTreeInitiated;

        public static void Start()
        {
            if (running) return;
            running = true;
            MelonCoroutines.Start(Run());
        }

        private static IEnumerator Run()
        {
            MelonLogger.Msg("[SpawnSniffer] Starting dependency sniff.");
            SpawnSummaryLog.Info("deps start");

            const int maxSeconds = 5;
            for (int i = 0; i < maxSeconds; i++)
            {
                var snapshot = Capture();
                LogSnapshot(snapshot, i + 1);
                LogSummarySnapshot(snapshot, i + 1);

                if (i == 0)
                    LogDetailedSnapshot(snapshot, "initial");

                if (snapshot.IsReady)
                {
                    MelonLogger.Msg("[SpawnSniffer] All required spawn dependencies are available.");
                    SpawnSummaryLog.Info(
                        "deps ready " +
                        $"stage={DescribeUnityObject(snapshot.PreferredStageEvent)} " +
                        $"spawner={DescribeUnityObject(snapshot.OfficialSpawner)} " +
                        $"techFrame={GetValueTypeName(snapshot.TechFrame)} " +
                        $"register={GetValueTypeName(snapshot.VehicleRegister)} " +
                        $"stageFactories={snapshot.AssemblyStageFactoryCount}");
                    LogDetailedSnapshot(snapshot, "ready");
                    TriggerMultiplayerSceneStart();
                    running = false;
                    yield break;
                }

                yield return new WaitForSeconds(1f);
            }

            MelonLogger.Warning("[SpawnSniffer] Dependency sniff timed out. See previous snapshots for missing pieces.");
            var timeoutSnapshot = Capture();
            SpawnSummaryLog.Warn($"deps timeout missing={GetMissingDependencies(timeoutSnapshot)}");
            LogDetailedSnapshot(timeoutSnapshot, "timeout");
            running = false;
        }

        private static void TriggerMultiplayerSceneStart()
        {
            if (MultiplayerManager.Instance == null)
            {
                SpawnSummaryLog.Error("deps ready trigger=OnSceneLoaded result=noManager");
                return;
            }

            SpawnSummaryLog.Info("deps ready trigger=OnSceneLoaded");
            MultiplayerManager.Instance.OnSceneLoaded();
        }

        public static SpawnDependencySnapshot Capture()
        {
            var snapshot = new SpawnDependencySnapshot();

            snapshot.VehicleFactory = FindVehicleFactory();
            snapshot.FactoryType = snapshot.VehicleFactory?.GetIl2CppType()?.FullName ?? "";
            VehicleRuntimeDiagnostics.EnsureStaticVehicleFactory(out _, out _);
            snapshot.StaticVehicleFactory = VehicleRuntimeDiagnostics.GetStaticVehicleFactory();
            snapshot.FactoryContext = GetFactoryContext(snapshot.VehicleFactory);
            snapshot.FactoryContextReady = snapshot.FactoryContext != null;
            snapshot.DefaultVehicleContext = FindDefaultVehicleContext();

            snapshot.TechFrame = FindTechFrame();
            snapshot.TechFrameType = snapshot.TechFrame?.GetIl2CppType()?.FullName ?? "";

            snapshot.StageSpawnEvents = UnityEngine.Object.FindObjectsOfType<StageVehicleSpawnEvent>();
            snapshot.StageSpawnEventCount = snapshot.StageSpawnEvents?.Length ?? 0;
            snapshot.PreferredStageEvent = FindPreferredStageEvent(snapshot.StageSpawnEvents);
            snapshot.VehicleRegister = FindVehicleRegister(snapshot.StageSpawnEvents, snapshot.PreferredStageEvent);
            snapshot.VehicleController = FindVehicleController();
            snapshot.SpawnLocator = FindSpawnLocator(snapshot.StageSpawnEvents, snapshot.PreferredStageEvent);
            snapshot.OfficialSpawner = FindOfficialSpawner(snapshot.StageSpawnEvents, snapshot.PreferredStageEvent) ?? UnityEngine.Object.FindObjectOfType<VehicleSpawner>();
            snapshot.VehicleAssemblyResources = FindVehicleAssemblyResources(snapshot.OfficialSpawner);
            snapshot.AssemblyStageFactories = FindAssemblyStageFactories(snapshot.OfficialSpawner, snapshot.VehicleAssemblyResources);
            snapshot.AssemblyStageFactoryCount = snapshot.AssemblyStageFactories?.Length ?? 0;
            VehicleRuntimeDiagnostics.EnsureVehiclesMainResources(out _, out _);
            snapshot.VehicleResourcesGlobal = VehicleRuntimeDiagnostics.GetGlobalVehicleResources();
            if (snapshot.VehicleResourcesGlobal == null)
                VehicleRuntimeDiagnostics.EnsureVehicleResourcesGlobal(out _, out _);
            snapshot.VehicleResourcesGlobal = VehicleRuntimeDiagnostics.GetGlobalVehicleResources();
            snapshot.VehiclesMainCount = VehicleRuntimeDiagnostics.CountVehiclesMain();
            snapshot.VehiclesMainAllCount = VehicleRuntimeDiagnostics.CountVehiclesMainAll();
            snapshot.VehiclesMainResources = VehicleRuntimeDiagnostics.GetVehiclesMainResources();

            return snapshot;
        }

        private static Il2CppSystem.Object FindVehicleFactory()
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var type = typeof(VehicleFactories);
                var prop = type.GetProperty("instance", flags) ?? type.GetProperty("Instance", flags);
                var value = prop?.GetValue(null) as VehicleFactories;
                return value?.Cast<Il2CppSystem.Object>();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SpawnSniffer] VehicleFactories lookup failed: {ex.Message}");
                return null;
            }
        }

        private static Il2CppSystem.Object GetFactoryContext(Il2CppSystem.Object factory)
        {
            if (factory == null) return null;

            try
            {
                var flags = Il2CppSystem.Reflection.BindingFlags.Public |
                            Il2CppSystem.Reflection.BindingFlags.NonPublic |
                            Il2CppSystem.Reflection.BindingFlags.Instance;

                var prop = factory.GetIl2CppType().GetProperty("Context", flags) ??
                           factory.GetIl2CppType().GetProperty("context", flags);
                if (prop != null)
                {
                    var value = prop.GetValue(factory);
                    var obj = AsIl2CppObject(value);
                    if (obj != null)
                        return obj;
                }

                var field = factory.GetIl2CppType().GetField("context", flags);
                if (field != null)
                {
                    var value = field.GetValue(factory);
                    var obj = AsIl2CppObject(value);
                    if (obj != null)
                        return obj;
                }
            }
            catch
            {
            }

            return null;
        }

        private static VehicleContext FindDefaultVehicleContext()
        {
            try
            {
                return VehicleContext.Default;
            }
            catch
            {
                return null;
            }
        }

        private static Il2CppSystem.Object FindTechFrame()
        {
            Il2CppSystem.Object candidate = FindTechFrameInScenarioSpawners();
            if (candidate != null) return candidate;

            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null || mb.Pointer == IntPtr.Zero) continue;
                try
                {
                    var obj = new Il2CppSystem.Object(mb.Pointer);
                    if (obj.TryCast<ITechFrame>() != null)
                        return obj;
                }
                catch { }
            }

            return FindTechFrameFromTechTree();
        }

        private static Il2CppSystem.Object FindTechFrameInScenarioSpawners()
        {
            GameObject scenario = GameObject.Find("Scenario");
            if (scenario == null) return null;

            foreach (var t in scenario.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.Contains("Spawner")) continue;

                foreach (var mb in t.GetComponents<MonoBehaviour>())
                {
                    if (mb == null || mb.Pointer == IntPtr.Zero) continue;
                    try
                    {
                        var obj = new Il2CppSystem.Object(mb.Pointer);
                        if (obj.TryCast<ITechFrame>() != null)
                            return obj;
                    }
                    catch { }
                }
            }

            return null;
        }

        private static StageVehicleSpawnEvent FindPreferredStageEvent(StageVehicleSpawnEvent[] stageEvents)
        {
            if (stageEvents == null || stageEvents.Length == 0) return null;

            StageVehicleSpawnEvent fallback = null;
            foreach (var stageEvent in stageEvents)
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
                        return stageEvent;
                    }
                }
                catch
                {
                }
            }

            return fallback;
        }

        private static IVehicleRegister FindVehicleRegister(StageVehicleSpawnEvent[] stageEvents, StageVehicleSpawnEvent preferredStageEvent)
        {
            if (preferredStageEvent != null)
            {
                try
                {
                    if (preferredStageEvent.register != null)
                        return preferredStageEvent.register;
                }
                catch { }
            }

            if (stageEvents != null)
            {
                foreach (var stageEvent in stageEvents)
                {
                    if (stageEvent == null) continue;
                    try
                    {
                        if (stageEvent.register != null)
                            return stageEvent.register;
                    }
                    catch { }
                }
            }

            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null || mb.Pointer == IntPtr.Zero) continue;
                try
                {
                    var reg = new Il2CppSystem.Object(mb.Pointer).TryCast<IVehicleRegister>();
                    if (reg != null) return reg;
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
                        InjectVehicleRegisterIntoStageEvents(stageEvents, reg);
                        return reg;
                    }
                }
                catch { }
            }

            return CreateAndInjectVehicleRegister(stageEvents);
        }

        private static VehicleSpawner FindOfficialSpawner(StageVehicleSpawnEvent[] stageEvents, StageVehicleSpawnEvent preferredStageEvent)
        {
            if (preferredStageEvent != null)
            {
                try
                {
                    if (preferredStageEvent.spawner != null)
                        return preferredStageEvent.spawner;
                }
                catch { }
            }

            if (stageEvents == null) return null;

            foreach (var stageEvent in stageEvents)
            {
                if (stageEvent == null) continue;
                try
                {
                    if (stageEvent.spawner != null)
                        return stageEvent.spawner;
                }
                catch { }
            }

            return null;
        }

        private static SpawnLocator FindSpawnLocator(StageVehicleSpawnEvent[] stageEvents, StageVehicleSpawnEvent preferredStageEvent)
        {
            if (preferredStageEvent != null)
            {
                try
                {
                    if (preferredStageEvent.spawnLocator != null)
                        return preferredStageEvent.spawnLocator;
                }
                catch { }
            }

            if (stageEvents == null) return null;

            foreach (var stageEvent in stageEvents)
            {
                if (stageEvent == null) continue;
                try
                {
                    if (stageEvent.spawnLocator != null)
                        return stageEvent.spawnLocator;
                }
                catch { }
            }

            return null;
        }

        private static Il2CppSystem.Object FindTechFrameFromTechTree()
        {
            try
            {
                if (!techTreeInitiated)
                {
                    TechTreeLoader.Initiate();
                    techTreeInitiated = true;
                }
            }
            catch { }

            try
            {
                TechDate date = new TechDate(1945, 0, 0);
                ITechFrame frame = null;

                try
                {
                    var factory = ITechFrameFactory.Instance;
                    if (factory != null)
                        frame = factory.GetTechFrameAtDate(date);
                }
                catch { }

                if (frame == null)
                    frame = new TechTreeLoader().GetTechFrameAtDate(date);

                if (frame != null && frame.Pointer != IntPtr.Zero)
                    return new Il2CppSystem.Object(frame.Pointer);
            }
            catch { }

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

        private static IVehicleRegister CreateAndInjectVehicleRegister(StageVehicleSpawnEvent[] stageEvents)
        {
            try
            {
                var concrete = new VehicleRegister();
                var register = concrete.TryCast<IVehicleRegister>() ?? new Il2CppSystem.Object(concrete.Pointer).TryCast<IVehicleRegister>();
                if (register != null)
                    InjectVehicleRegisterIntoStageEvents(stageEvents, register);
                return register;
            }
            catch
            {
                return null;
            }
        }

        private static void InjectVehicleRegisterIntoStageEvents(StageVehicleSpawnEvent[] stageEvents, IVehicleRegister register)
        {
            if (stageEvents == null || register == null) return;

            foreach (var stageEvent in stageEvents)
            {
                if (stageEvent == null) continue;
                try { stageEvent.SetVehicleRegister(register); } catch { }
            }
        }

        private static VehicleController FindVehicleController()
        {
            var controllers = UnityEngine.Object.FindObjectsOfType<VehicleController>();
            return controllers != null && controllers.Length > 0 ? controllers[0] : null;
        }

        private static VehicleAssemblyResources FindVehicleAssemblyResources(VehicleSpawner spawner)
        {
            try
            {
                if (spawner != null && spawner.assemblyResourcesOverride != null)
                    return spawner.assemblyResourcesOverride;
            }
            catch { }

            try
            {
                return VehicleAssemblyResources.Default;
            }
            catch
            {
            }

            try
            {
                var resources = UnityEngine.Resources.FindObjectsOfTypeAll<VehicleAssemblyResources>();
                if (resources != null)
                {
                    foreach (var candidate in resources)
                    {
                        try
                        {
                            var factories = candidate?.GetFactories();
                            if (factories != null && factories.Length > 0)
                                return candidate;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return null;
        }

        private static Il2CppReferenceArray<IVehicleAssemblyStageFactory> FindAssemblyStageFactories(VehicleSpawner spawner, VehicleAssemblyResources resources)
        {
            if (resources != null)
            {
                try
                {
                    var factories = resources.GetFactories();
                    if (factories != null && factories.Length > 0)
                        return factories;
                }
                catch { }
            }

            if (spawner == null) return null;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var field in spawner.GetType().GetFields(flags))
            {
                try
                {
                    var result = TryCastAssemblyStageFactories(field.GetValue(spawner));
                    if (result != null && result.Length > 0)
                        return result;
                }
                catch { }
            }

            foreach (var prop in spawner.GetType().GetProperties(flags))
            {
                if (!prop.CanRead) continue;
                try
                {
                    var result = TryCastAssemblyStageFactories(prop.GetValue(spawner, null));
                    if (result != null && result.Length > 0)
                        return result;
                }
                catch { }
            }

            return null;
        }

        private static Il2CppReferenceArray<IVehicleAssemblyStageFactory> TryCastAssemblyStageFactories(object value)
        {
            if (value == null) return null;

            if (value is Il2CppReferenceArray<IVehicleAssemblyStageFactory> direct)
                return direct;

            if (value is Il2CppObjectBase obj)
                return obj.TryCast<Il2CppReferenceArray<IVehicleAssemblyStageFactory>>();

            return null;
        }

        private static void LogSnapshot(SpawnDependencySnapshot s, int attempt)
        {
            MelonLogger.Msg(
                "[SpawnSniffer] " +
                $"attempt={attempt} " +
                $"factory={(s.VehicleFactory != null ? "yes" : "no")} " +
                $"factoryStatic={(s.StaticVehicleFactory != null ? "yes" : "no")} " +
                $"context={(s.FactoryContextReady ? "yes" : "no")} " +
                $"defaultContext={(s.DefaultVehicleContext != null ? "yes" : "no")} " +
                $"techFrame={(s.TechFrame != null ? "yes" : "no")} " +
                $"officialSpawner={(s.OfficialSpawner != null ? "yes" : "no")} " +
                $"stageEvents={s.StageSpawnEventCount} " +
                $"locator={(s.SpawnLocator != null ? "yes" : "no")} " +
                $"stageFactories={s.AssemblyStageFactoryCount} " +
                $"vehicleResources={(s.VehicleResourcesGlobal != null ? "yes" : "no")} " +
                $"vehiclesMain={(s.VehiclesMainResources != null ? "yes" : "no")} " +
                $"vehiclesMainActive={s.VehiclesMainCount} " +
                $"vehiclesMainAll={s.VehiclesMainAllCount} " +
                $"register={(s.VehicleRegister != null ? "yes" : "no")} " +
                $"controller={(s.VehicleController != null ? "yes" : "no")}");
        }

        private static void LogSummarySnapshot(SpawnDependencySnapshot s, int attempt)
        {
            SpawnSummaryLog.Info(
                "deps " +
                $"attempt={attempt} " +
                $"ready={SpawnSummaryLog.YesNo(s.IsReady)} " +
                $"missing={GetMissingDependencies(s)} " +
                $"stage={DescribeUnityObject(s.PreferredStageEvent)} " +
                $"factory={SpawnSummaryLog.YesNo(s.VehicleFactory != null)} " +
                $"factoryStatic={SpawnSummaryLog.YesNo(s.StaticVehicleFactory != null)} " +
                $"context={SpawnSummaryLog.YesNo(s.FactoryContextReady)} " +
                $"defaultContext={SpawnSummaryLog.YesNo(s.DefaultVehicleContext != null)} " +
                $"techFrame={SpawnSummaryLog.YesNo(s.TechFrame != null)} " +
                $"officialSpawner={SpawnSummaryLog.YesNo(s.OfficialSpawner != null)} " +
                $"stageEvents={s.StageSpawnEventCount} " +
                $"locator={SpawnSummaryLog.YesNo(s.SpawnLocator != null)} " +
                $"stageFactories={s.AssemblyStageFactoryCount} " +
                $"vehicleResources={SpawnSummaryLog.YesNo(s.VehicleResourcesGlobal != null)} " +
                $"vehiclesMain={SpawnSummaryLog.YesNo(s.VehiclesMainResources != null)} " +
                $"vehiclesMainActive={s.VehiclesMainCount} " +
                $"vehiclesMainAll={s.VehiclesMainAllCount} " +
                $"register={SpawnSummaryLog.YesNo(s.VehicleRegister != null)} " +
                $"controller={SpawnSummaryLog.YesNo(s.VehicleController != null)}");
        }

        private static string GetMissingDependencies(SpawnDependencySnapshot s)
        {
            var missing = new List<string>();
            if (s.VehicleFactory == null) missing.Add("factory");
            if (!s.FactoryContextReady) missing.Add("context");
            if (s.TechFrame == null) missing.Add("techFrame");
            if (s.OfficialSpawner == null) missing.Add("officialSpawner");
            if (s.AssemblyStageFactoryCount <= 0) missing.Add("stageFactories");
            if (s.VehicleResourcesGlobal == null) missing.Add("vehicleResources");
            if (s.VehiclesMainResources == null) missing.Add("vehiclesMain");
            if (s.VehicleRegister == null) missing.Add("register");
            if (s.VehicleController == null) missing.Add("controller");

            return missing.Count == 0 ? "none" : string.Join(",", missing.ToArray());
        }

        private static void LogDetailedSnapshot(SpawnDependencySnapshot s, string phase)
        {
            MelonLogger.Msg($"[SpawnSniffer:detail] phase={phase}");
            LogFactoryDetails(s.VehicleFactory, s.FactoryContext, s.DefaultVehicleContext);
            LogAssemblyResourcesDetails(s.VehicleAssemblyResources, s.AssemblyStageFactories);
            VehicleRuntimeDiagnostics.LogState("sniffer-" + phase);
            LogStageSpawnDetails(s.StageSpawnEvents);
            LogSpawnerDetails();
            LogSceneComponentCandidates();
        }

        private static void LogFactoryDetails(Il2CppSystem.Object factory, Il2CppSystem.Object context, VehicleContext defaultContext)
        {
            if (factory == null)
            {
                MelonLogger.Msg("[SpawnSniffer:factory] VehicleFactories.instance is null.");
                return;
            }

            var type = factory.GetIl2CppType();
            MelonLogger.Msg($"[SpawnSniffer:factory] type={type?.FullName ?? "unknown"}");
            MelonLogger.Msg($"[SpawnSniffer:factory.context] active={GetValueTypeName(context)} default={GetValueTypeName(defaultContext)}");

            try
            {
                var flags = Il2CppSystem.Reflection.BindingFlags.Public |
                            Il2CppSystem.Reflection.BindingFlags.NonPublic |
                            Il2CppSystem.Reflection.BindingFlags.Instance;

                foreach (var field in type.GetFields(flags))
                {
                    if (!IsInterestingMember(field.Name, field.FieldType?.FullName))
                        continue;

                    string valueType = "null";
                    try
                    {
                        var value = field.GetValue(factory);
                        valueType = GetValueTypeName(value);
                    }
                    catch (Exception ex)
                    {
                        valueType = "read failed: " + ex.Message;
                    }

                    MelonLogger.Msg($"[SpawnSniffer:factory.field] {field.Name}:{field.FieldType?.FullName ?? "?"} value={valueType}");
                }

                foreach (var method in type.GetMethods(flags))
                {
                    if (method.Name != "Create" && method.Name != "EnableBehaviour")
                        continue;

                    MelonLogger.Msg($"[SpawnSniffer:factory.method] {method.Name}({FormatParameters(method.GetParameters())}) -> {method.ReturnType?.FullName ?? "void"}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SpawnSniffer:factory] detail failed: {ex.Message}");
            }
        }

        private static void LogAssemblyResourcesDetails(VehicleAssemblyResources resources, Il2CppReferenceArray<IVehicleAssemblyStageFactory> factories)
        {
            if (resources == null)
            {
                MelonLogger.Msg("[SpawnSniffer:assembly] resources=null");
                return;
            }

            int builtInStageCount = 0;
            try { builtInStageCount = resources.builtInStages?.Length ?? 0; } catch { }

            MelonLogger.Msg(
                "[SpawnSniffer:assembly] " +
                $"type={resources.GetIl2CppType()?.FullName ?? resources.GetType().FullName} " +
                $"name={resources.name} " +
                $"builtInStages={builtInStageCount} " +
                $"factories={(factories?.Length ?? 0)}");

            if (factories == null) return;

            for (int i = 0; i < factories.Length && i < 16; i++)
                MelonLogger.Msg($"[SpawnSniffer:assembly.factory] #{i} {GetValueTypeName(factories[i])}");

            if (factories.Length > 16)
                MelonLogger.Msg($"[SpawnSniffer:assembly.factory] omitted={factories.Length - 16}");
        }

        private static void LogStageSpawnDetails(StageVehicleSpawnEvent[] stageEvents)
        {
            int count = stageEvents?.Length ?? 0;
            MelonLogger.Msg($"[SpawnSniffer:stageEvent] count={count}");
            if (stageEvents == null) return;

            for (int i = 0; i < stageEvents.Length && i < 8; i++)
            {
                var stageEvent = stageEvents[i];
                if (stageEvent == null) continue;

                MelonLogger.Msg(
                    "[SpawnSniffer:stageEvent] " +
                    $"#{i} path={GetPath(stageEvent.transform)} " +
                    $"type={stageEvent.GetIl2CppType()?.FullName ?? stageEvent.GetType().FullName}");

                int spawns = 0;
                try { spawns = stageEvent.spawns?.Length ?? 0; } catch { }

                MelonLogger.Msg(
                    "[SpawnSniffer:stageEvent.refs] " +
                    $"spawner={DescribeUnityObject(stageEvent.spawner)} " +
                    $"source={DescribeUnityObject(stageEvent.blueprintSource)} " +
                    $"locator={DescribeUnityObject(stageEvent.spawnLocator)} " +
                    $"register={GetValueTypeName(stageEvent.register)} " +
                    $"spawns={spawns}");

                MelonLogger.Msg(
                    "[SpawnSniffer:stageEvent.cfg] " +
                    $"spawnLimit={SafeRead(() => stageEvent.spawnLimit.ToString())} " +
                    $"useBudget={SafeRead(() => stageEvent.useBudget.ToString())} " +
                    $"costBudget={SafeRead(() => stageEvent.costBudget.ToString())} " +
                    $"remaining={SafeRead(() => stageEvent.RemainingCostBudget.ToString())} " +
                    $"delay={SafeRead(() => stageEvent.delay.ToString())}");

                LogVehicleSourceDetails(stageEvent.blueprintSource);
                LogSpawnLocatorDetails(stageEvent.spawnLocator);
            }

            if (stageEvents.Length > 8)
                MelonLogger.Msg($"[SpawnSniffer:stageEvent] omitted={stageEvents.Length - 8}");
        }

        private static void LogVehicleSourceDetails(VehicleSource source)
        {
            if (source == null) return;

            int classificationCount = 0;
            try { classificationCount = source.ClassificationFilters?.Length ?? 0; } catch { }

            MelonLogger.Msg(
                "[SpawnSniffer:vehicleSource] " +
                $"path={GetPath(source.transform)} " +
                $"mode={SafeRead(() => source.Mode.ToString())} " +
                $"blueprint={SafeRead(() => source.BlueprintName)} " +
                $"subdirectory={SafeRead(() => source.Subdirectory)} " +
                $"source={SafeRead(() => source.Source.ToString())} " +
                $"minTech={SafeRead(() => source.MinTechDate.ToString())} " +
                $"maxTech={SafeRead(() => source.MaxTechDate.ToString())} " +
                $"minMass={SafeRead(() => source.MinMass.ToString())} " +
                $"maxMass={SafeRead(() => source.MaxMass.ToString())} " +
                $"classFilters={classificationCount}");
        }

        private static void LogSpawnLocatorDetails(SpawnLocator locator)
        {
            if (locator == null) return;

            MelonLogger.Msg(
                "[SpawnSniffer:spawnLocator] " +
                $"path={GetPath(locator.transform)} " +
                $"type={locator.GetIl2CppType()?.FullName ?? locator.GetType().FullName} " +
                $"maxSpawn={SafeRead(() => locator.MaxSpawn.ToString())} " +
                $"nextSpawnIndex={SafeRead(() => locator.NextSpawnIndex.ToString())}");
        }

        private static void LogSpawnerDetails()
        {
            var spawners = UnityEngine.Object.FindObjectsOfType<VehicleSpawner>();
            MelonLogger.Msg($"[SpawnSniffer:spawner] count={(spawners == null ? 0 : spawners.Length)}");

            if (spawners == null) return;

            int index = 0;
            foreach (var spawner in spawners)
            {
                if (spawner == null) continue;
                if (index >= 8)
                {
                    MelonLogger.Msg("[SpawnSniffer:spawner] more spawners omitted.");
                    break;
                }

                MelonLogger.Msg($"[SpawnSniffer:spawner] #{index} path={GetPath(spawner.transform)} type={spawner.GetIl2CppType()?.FullName ?? spawner.GetType().FullName}");
                LogSpawnerMembers(spawner);
                index++;
            }
        }

        private static void LogSpawnerMembers(VehicleSpawner spawner)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            int logged = 0;

            foreach (var field in spawner.GetType().GetFields(flags))
            {
                if (!IsInterestingMember(field.Name, field.FieldType?.FullName))
                    continue;

                LogSpawnerMember("field", field.Name, field.FieldType?.FullName, () => field.GetValue(spawner));
                logged++;
                if (logged >= 24) return;
            }

            foreach (var prop in spawner.GetType().GetProperties(flags))
            {
                if (!prop.CanRead || !IsInterestingMember(prop.Name, prop.PropertyType?.FullName))
                    continue;

                LogSpawnerMember("prop", prop.Name, prop.PropertyType?.FullName, () => prop.GetValue(spawner, null));
                logged++;
                if (logged >= 24) return;
            }

            if (logged == 0)
                MelonLogger.Msg("[SpawnSniffer:spawner.member] no interesting managed wrapper members found.");
        }

        private static void LogSpawnerMember(string kind, string name, string declaredType, Func<object> readValue)
        {
            string valueType = "null";
            string extra = "";

            try
            {
                object value = readValue();
                valueType = GetValueTypeName(value);

                var factories = TryCastAssemblyStageFactories(value);
                if (factories != null)
                    extra = $" assemblyStageFactories={factories.Length}";
            }
            catch (Exception ex)
            {
                valueType = "read failed: " + ex.Message;
            }

            MelonLogger.Msg($"[SpawnSniffer:spawner.{kind}] {name}:{declaredType ?? "?"} value={valueType}{extra}");
        }

        private static void LogSceneComponentCandidates()
        {
            var techFrames = new List<string>();
            var registers = new List<string>();
            var vehicleSources = new List<string>();
            var stageEvents = new List<string>();
            var spawnLocators = new List<string>();
            var stageFactories = new List<string>();
            var spawnerNamedComponents = new List<string>();

            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null || mb.Pointer == IntPtr.Zero) continue;

                Il2CppSystem.Object obj = null;
                string typeName = "";
                try
                {
                    obj = new Il2CppSystem.Object(mb.Pointer);
                    typeName = obj.GetIl2CppType()?.FullName ?? mb.GetType().FullName;
                }
                catch
                {
                    typeName = mb.GetType().FullName;
                }

                string descriptor = $"{GetPath(mb.transform)} [{typeName}]";

                try { if (obj != null && obj.TryCast<ITechFrame>() != null) techFrames.Add(descriptor); } catch { }
                try { if (obj != null && obj.TryCast<IVehicleRegister>() != null) registers.Add(descriptor); } catch { }
                try { if (obj != null && obj.TryCast<VehicleSource>() != null) vehicleSources.Add(descriptor); } catch { }
                try { if (obj != null && obj.TryCast<StageVehicleSpawnEvent>() != null) stageEvents.Add(descriptor); } catch { }
                try { if (obj != null && obj.TryCast<SpawnLocator>() != null) spawnLocators.Add(descriptor); } catch { }
                try { if (obj != null && obj.TryCast<IVehicleAssemblyStageFactory>() != null) stageFactories.Add(descriptor); } catch { }

                if (descriptor.IndexOf("spawner", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("spawner", StringComparison.OrdinalIgnoreCase) >= 0)
                    spawnerNamedComponents.Add(descriptor);
            }

            LogList("techFrameCandidate", techFrames, 16);
            LogList("vehicleRegisterCandidate", registers, 16);
            LogList("vehicleSourceCandidate", vehicleSources, 16);
            LogList("stageEventCandidate", stageEvents, 16);
            LogList("spawnLocatorCandidate", spawnLocators, 16);
            LogList("assemblyStageFactoryCandidate", stageFactories, 16);
            LogList("spawnerNamedComponent", spawnerNamedComponents, 24);
        }

        private static void LogList(string label, List<string> values, int max)
        {
            MelonLogger.Msg($"[SpawnSniffer:{label}] count={values.Count}");

            for (int i = 0; i < values.Count && i < max; i++)
                MelonLogger.Msg($"[SpawnSniffer:{label}] #{i} {values[i]}");

            if (values.Count > max)
                MelonLogger.Msg($"[SpawnSniffer:{label}] omitted={values.Count - max}");
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

        private static bool IsInterestingMember(string name, string typeName)
        {
            string text = ((name ?? "") + " " + (typeName ?? "")).ToLowerInvariant();
            return text.Contains("context") ||
                   text.Contains("tech") ||
                   text.Contains("stage") ||
                   text.Contains("assembly") ||
                   text.Contains("factory") ||
                   text.Contains("spawn") ||
                   text.Contains("blueprint") ||
                   text.Contains("locator") ||
                   text.Contains("spawner") ||
                   text.Contains("source") ||
                   text.Contains("register") ||
                   text.Contains("vehicle");
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

        private static string DescribeUnityObject(UnityEngine.Object value)
        {
            if (value == null) return "null";

            if (value is Component component)
                return $"{GetPath(component.transform)} [{component.GetIl2CppType()?.FullName ?? component.GetType().FullName}]";

            return $"{value.name} [{value.GetType().FullName}]";
        }

        private static string SafeRead(Func<string> readValue)
        {
            try
            {
                return readValue() ?? "null";
            }
            catch (Exception ex)
            {
                return "read failed: " + ex.Message;
            }
        }

        private static string FormatParameters(Il2CppSystem.Reflection.ParameterInfo[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
                return "";

            var parts = new List<string>();
            for (int i = 0; i < parameters.Length; i++)
                parts.Add(parameters[i].ParameterType?.FullName ?? "?");

            return string.Join(", ", parts.ToArray());
        }
    }

    public class SpawnDependencySnapshot
    {
        public Il2CppSystem.Object VehicleFactory;
        public IVehicleFactory StaticVehicleFactory;
        public string FactoryType;
        public bool FactoryContextReady;
        public Il2CppSystem.Object FactoryContext;
        public VehicleContext DefaultVehicleContext;
        public Il2CppSystem.Object TechFrame;
        public string TechFrameType;
        public StageVehicleSpawnEvent[] StageSpawnEvents;
        public int StageSpawnEventCount;
        public StageVehicleSpawnEvent PreferredStageEvent;
        public SpawnLocator SpawnLocator;
        public VehicleSpawner OfficialSpawner;
        public VehicleAssemblyResources VehicleAssemblyResources;
        public Il2CppReferenceArray<IVehicleAssemblyStageFactory> AssemblyStageFactories;
        public int AssemblyStageFactoryCount;
        public VehicleResources VehicleResourcesGlobal;
        public int VehiclesMainCount;
        public int VehiclesMainAllCount;
        public VehicleResources VehiclesMainResources;
        public IVehicleRegister VehicleRegister;
        public VehicleController VehicleController;

        public bool IsReady =>
            VehicleFactory != null &&
            FactoryContextReady &&
            TechFrame != null &&
            OfficialSpawner != null &&
            AssemblyStageFactoryCount > 0 &&
            VehicleResourcesGlobal != null &&
            VehiclesMainResources != null &&
            VehicleRegister != null &&
            VehicleController != null;
    }

    internal static class SpawnSummaryLog
    {
        public static void Info(string message)
        {
            MelonLogger.Msg("[SpawnSummary] " + Clean(message));
        }

        public static void Warn(string message)
        {
            MelonLogger.Warning("[SpawnSummary] " + Clean(message));
        }

        public static void Error(string message)
        {
            MelonLogger.Error("[SpawnSummary] " + Clean(message));
        }

        public static string YesNo(bool value)
        {
            return value ? "yes" : "no";
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            return value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ');
        }
    }

    internal static class VehicleRuntimeDiagnostics
    {
        private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static VehiclesMain runtimeVehiclesMain;

        public static VehicleResources GetGlobalVehicleResources()
        {
            try
            {
                var type = typeof(VehicleResources);
                var prop = type.GetProperty("Global", AnyStatic);
                if (prop != null)
                    return prop.GetValue(null, null) as VehicleResources;

                var getter = type.GetMethod("get_Global", AnyStatic);
                if (getter != null)
                    return getter.Invoke(null, null) as VehicleResources;

                var field = type.GetField("global", AnyStatic) ?? type.GetField("Global", AnyStatic);
                return field?.GetValue(null) as VehicleResources;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[VehicleRuntime] VehicleResources.Global read failed: {ex.Message}");
                return null;
            }
        }

        public static IVehicleFactory GetStaticVehicleFactory()
        {
            try
            {
                var field = typeof(IVehicleFactory).GetField("Factory", AnyStatic);
                var direct = field?.GetValue(null) as IVehicleFactory;
                if (direct != null)
                    return direct;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[VehicleRuntime] IVehicleFactory.Factory read failed: {ex.Message}");
            }

            var factory = GetVehicleFactoriesInstance();
            return factory != null
                ? (factory.TryCast<IVehicleFactory>() ?? new Il2CppSystem.Object(factory.Pointer).TryCast<IVehicleFactory>())
                : null;
        }

        public static bool EnsureStaticVehicleFactory(out string source, out string detail)
        {
            source = "existing";
            detail = "";

            IVehicleFactory existing = GetStaticVehicleFactory();
            if (existing != null)
            {
                detail = DescribeObject(existing);
                return true;
            }

            try
            {
                var register = typeof(VehicleFactories).GetMethod("Register", AnyStatic);
                register?.Invoke(null, null);
            }
            catch (Exception ex)
            {
                source = "VehicleFactories.Register";
                detail = ex.Message;
            }

            try
            {
                var factory = GetVehicleFactoriesInstance();
                if (factory == null)
                {
                    source = "none";
                    detail = "VehicleFactories.instance null";
                    return false;
                }

                existing = GetStaticVehicleFactory();
                if (existing != null)
                {
                    source = "VehicleFactories.instance";
                    detail = DescribeObject(existing);
                    return true;
                }

                var field = typeof(IVehicleFactory).GetField("Factory", AnyStatic);
                if (field == null)
                    return true;

                field.SetValue(null, factory);
                existing = GetStaticVehicleFactory();
                bool ok = existing != null;
                source = "VehicleFactories.instance";
                detail = ok ? DescribeObject(existing) : "field still null after set";
                return ok;
            }
            catch (Exception ex)
            {
                source = "VehicleFactories.instance";
                detail = ex.Message;
                return false;
            }
        }

        public static int CountVehiclesMain()
        {
            try
            {
                var mains = Object.FindObjectsOfType<VehiclesMain>();
                return mains?.Length ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        public static int CountVehiclesMainAll()
        {
            try
            {
                var mains = UnityEngine.Resources.FindObjectsOfTypeAll<VehiclesMain>();
                return mains?.Length ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        public static VehicleResources GetVehiclesMainResources()
        {
            try
            {
                var main = GetVehiclesMainInstance();
                if (main == null)
                    return null;

                return GetVehiclesMainResources(main);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[VehicleRuntime] VehiclesMain.Resources read failed: {ex.Message}");
                return null;
            }
        }

        public static bool EnsureVehiclesMainResources(out string source, out string detail)
        {
            source = "none";
            detail = "";

            VehiclesMain main = GetVehiclesMainInstance();
            VehicleResources existing = main != null ? GetVehiclesMainResources(main) : null;
            if (existing != null)
            {
                source = "VehiclesMain.Instance.resources";
                detail = Describe(existing);
                return true;
            }

            VehiclesMain candidateMain = FindVehiclesMainWithResources(out VehicleResources candidateResources);
            if (main == null && candidateMain != null)
            {
                SetVehiclesMainInstance(candidateMain);
                main = GetVehiclesMainInstance();
                existing = main != null ? GetVehiclesMainResources(main) : null;
                if (existing != null)
                {
                    source = "VehiclesMain.FindObjectsOfTypeAll";
                    detail = Describe(existing);
                    return true;
                }
            }

            if (candidateResources == null)
                candidateResources = FindVehicleResourcesCandidate(out source, out detail);

            if (main == null)
            {
                var assemblyResources = FindVehicleAssemblyResourcesCandidate(out string assemblySource, out string assemblyDetail);
                if (candidateResources != null &&
                    assemblyResources != null &&
                    TryCreateRuntimeVehiclesMain(candidateResources, assemblyResources, out main, out string createDetail))
                {
                    SetVehiclesMainInstance(main);
                    existing = GetVehiclesMainResources(main);
                    if (existing != null)
                    {
                        source = "VehiclesMain.runtime";
                        detail = $"{Describe(existing)} assembly={assemblySource} create={createDetail}";
                        return true;
                    }
                }

                detail = candidateResources == null
                    ? "no VehiclesMain instance and no VehicleResources candidate"
                    : "no VehiclesMain instance";
                return false;
            }

            if (candidateResources == null)
                return false;

            if (!TrySetVehiclesMainResources(main, candidateResources, out detail))
            {
                source = "VehiclesMain.resources.injected";
                return false;
            }

            SetVehiclesMainInstance(main);
            EnsureVehicleResourcesGlobal(out _, out _);

            VehicleResources confirmed = GetVehiclesMainResources();
            bool ok = confirmed != null;
            source = "VehiclesMain.resources.injected";
            detail = ok ? Describe(confirmed) : "resources still null after injection";
            return ok;
        }

        public static bool EnsureVehicleResourcesGlobal(out string source, out string detail)
        {
            source = "existing";
            detail = "";

            VehicleResources current = GetGlobalVehicleResources();
            if (current != null)
            {
                detail = Describe(current);
                return true;
            }

            VehicleResources candidate = FindVehicleResourcesCandidate(out source, out detail);
            if (candidate == null)
                return false;

            try
            {
                var setter = typeof(VehicleResources).GetMethod("SetInstance", AnyStatic);
                if (setter == null)
                {
                    detail = "SetInstance not found";
                    return false;
                }

                setter.Invoke(null, new object[] { candidate });
                current = GetGlobalVehicleResources();
                bool ok = current != null;
                detail = ok ? Describe(current) : "SetInstance returned but global is still null";
                return ok;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        public static void LogState(string phase)
        {
            bool factoryReady = EnsureStaticVehicleFactory(out string factorySource, out string factoryDetail);
            bool mainReady = EnsureVehiclesMainResources(out string mainSource, out string mainDetail);
            bool repaired = EnsureVehicleResourcesGlobal(out string source, out string detail);
            SpawnSummaryLog.Info(
                "vehicleRuntime " +
                $"phase={phase} " +
                $"factoryStatic={SpawnSummaryLog.YesNo(GetStaticVehicleFactory() != null)} " +
                $"factoryRepair={SpawnSummaryLog.YesNo(factoryReady)} " +
                $"factorySource={factorySource} " +
                $"factoryDetail={factoryDetail} " +
                $"vehiclesMain={SpawnSummaryLog.YesNo(GetVehiclesMainResources() != null)} " +
                $"vehiclesMainActive={CountVehiclesMain()} " +
                $"vehiclesMainAll={CountVehiclesMainAll()} " +
                $"resourcesGlobal={SpawnSummaryLog.YesNo(GetGlobalVehicleResources() != null)} " +
                $"mainRepair={SpawnSummaryLog.YesNo(mainReady)} " +
                $"mainSource={mainSource} " +
                $"mainDetail={mainDetail} " +
                $"repair={SpawnSummaryLog.YesNo(repaired)} " +
                $"source={source} " +
                $"detail={detail}");
        }

        private static VehicleResources FindVehicleResourcesCandidate(out string source, out string detail)
        {
            source = "none";
            detail = "no candidate";

            try
            {
                var mains = Object.FindObjectsOfType<VehiclesMain>();
                if (mains != null)
                {
                    foreach (var main in mains)
                    {
                        if (main == null)
                            continue;

                        var resources = GetVehiclesMainResources(main);
                        if (resources != null)
                        {
                            source = "VehiclesMain.resources";
                            detail = Describe(resources);
                            return resources;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                detail = "VehiclesMain read failed: " + ex.Message;
            }

            try
            {
                var resources = UnityEngine.Resources.FindObjectsOfTypeAll<VehicleResources>();
                if (resources != null && resources.Length > 0)
                {
                    source = "Resources.FindObjectsOfTypeAll";
                    detail = Describe(resources[0]);
                    return resources[0];
                }
            }
            catch (Exception ex)
            {
                detail = "Resources scan failed: " + ex.Message;
            }

            return null;
        }

        private static VehicleAssemblyResources FindVehicleAssemblyResourcesCandidate(out string source, out string detail)
        {
            source = "none";
            detail = "no candidate";

            try
            {
                if (VehicleAssemblyResources.Default != null)
                {
                    source = "VehicleAssemblyResources.Default";
                    detail = VehicleAssemblyResources.Default.name;
                    return VehicleAssemblyResources.Default;
                }
            }
            catch (Exception ex)
            {
                detail = "default read failed: " + ex.Message;
            }

            try
            {
                var resources = UnityEngine.Resources.FindObjectsOfTypeAll<VehicleAssemblyResources>();
                if (resources != null)
                {
                    foreach (var candidate in resources)
                    {
                        if (candidate == null)
                            continue;

                        try
                        {
                            var factories = candidate.GetFactories();
                            if (factories != null && factories.Length > 0)
                            {
                                source = "Resources.FindObjectsOfTypeAll";
                                detail = candidate.name;
                                return candidate;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                detail = "assembly scan failed: " + ex.Message;
            }

            return null;
        }

        private static VehiclesMain GetVehiclesMainInstance()
        {
            if (runtimeVehiclesMain != null && runtimeVehiclesMain.Pointer != IntPtr.Zero)
                return runtimeVehiclesMain;

            try
            {
                var prop = typeof(VehiclesMain).GetProperty("Instance", AnyStatic);
                if (prop != null)
                {
                    var instance = prop.GetValue(null, null) as VehiclesMain;
                    if (instance != null)
                        return instance;
                }
            }
            catch
            {
            }

            try
            {
                var found = Object.FindObjectOfType<VehiclesMain>();
                if (found != null)
                    return found;

                var all = UnityEngine.Resources.FindObjectsOfTypeAll<VehiclesMain>();
                if (all != null)
                {
                    foreach (var main in all)
                    {
                        if (main != null && main.Pointer != IntPtr.Zero && GetVehiclesMainResources(main) != null)
                            return main;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static void SetVehiclesMainInstance(VehiclesMain main)
        {
            if (main == null)
                return;

            try
            {
                Type current = typeof(VehiclesMain);
                while (current != null)
                {
                    var field = current.GetField("instance", AnyStatic);
                    if (field != null && field.FieldType.IsAssignableFrom(typeof(VehiclesMain)))
                    {
                        field.SetValue(null, main);
                        return;
                    }

                    current = current.BaseType;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[VehicleRuntime] VehiclesMain instance injection failed: {ex.Message}");
            }
        }

        private static VehiclesMain FindVehiclesMainWithResources(out VehicleResources resources)
        {
            resources = null;

            try
            {
                var mains = UnityEngine.Resources.FindObjectsOfTypeAll<VehiclesMain>();
                if (mains == null)
                    return null;

                foreach (var main in mains)
                {
                    if (main == null)
                        continue;

                    resources = GetVehiclesMainResources(main);
                    if (resources != null)
                        return main;
                }
            }
            catch
            {
            }

            return null;
        }

        private static VehicleResources GetVehiclesMainResources(VehiclesMain main)
        {
            if (main == null)
                return null;

            try
            {
                var prop = typeof(VehiclesMain).GetProperty("Resources", AnyInstance);
                if (prop != null)
                {
                    var value = prop.GetValue(main, null) as VehicleResources;
                    if (value != null)
                        return value;
                }
            }
            catch
            {
            }

            try
            {
                var field = typeof(VehiclesMain).GetField("resources", AnyInstance);
                var value = field?.GetValue(main) as VehicleResources;
                if (value != null)
                    return value;
            }
            catch
            {
            }

            try
            {
                var obj = main.Cast<Il2CppSystem.Object>();
                var flags = Il2CppSystem.Reflection.BindingFlags.Public |
                            Il2CppSystem.Reflection.BindingFlags.NonPublic |
                            Il2CppSystem.Reflection.BindingFlags.Instance;

                var type = obj.GetIl2CppType();
                var field = type.GetField("resources", flags) ?? type.GetField("Resources", flags);
                var value = field?.GetValue(obj);
                return value?.TryCast<VehicleResources>();
            }
            catch
            {
            }

            return ReadIl2CppObjectField<VehicleResources>(main, 0x20);
        }

        private static VehicleAssemblyResources GetVehiclesMainAssemblyResources(VehiclesMain main)
        {
            if (main == null)
                return null;

            try
            {
                var field = typeof(VehiclesMain).GetField("defaultAssemblyResources", AnyInstance);
                var value = field?.GetValue(main) as VehicleAssemblyResources;
                if (value != null)
                    return value;
            }
            catch
            {
            }

            try
            {
                var obj = main.Cast<Il2CppSystem.Object>();
                var flags = Il2CppSystem.Reflection.BindingFlags.Public |
                            Il2CppSystem.Reflection.BindingFlags.NonPublic |
                            Il2CppSystem.Reflection.BindingFlags.Instance;
                var field = obj.GetIl2CppType().GetField("defaultAssemblyResources", flags);
                var value = field?.GetValue(obj);
                var cast = value?.TryCast<VehicleAssemblyResources>();
                if (cast != null)
                    return cast;
            }
            catch
            {
            }

            return ReadIl2CppObjectField<VehicleAssemblyResources>(main, 0x28);
        }

        private static bool TrySetVehiclesMainResources(VehiclesMain main, VehicleResources resources, out string detail)
        {
            detail = "";
            if (main == null || resources == null)
            {
                detail = "main/resources null";
                return false;
            }

            try
            {
                var resourcesField = typeof(VehiclesMain).GetField("resources", AnyInstance);
                if (resourcesField != null)
                {
                    resourcesField.SetValue(main, resources);
                    return true;
                }
            }
            catch (Exception ex)
            {
                detail = "managed field set failed: " + ex.Message;
            }

            try
            {
                var obj = main.Cast<Il2CppSystem.Object>();
                var value = resources.Cast<Il2CppSystem.Object>();
                var flags = Il2CppSystem.Reflection.BindingFlags.Public |
                            Il2CppSystem.Reflection.BindingFlags.NonPublic |
                            Il2CppSystem.Reflection.BindingFlags.Instance;

                var type = obj.GetIl2CppType();
                var field = type.GetField("resources", flags) ?? type.GetField("Resources", flags);
                if (field == null)
                {
                    detail = "VehiclesMain.resources il2cpp field not found fields=" + DescribeIl2CppFields(type);
                    return false;
                }

                field.SetValue(obj, value);
                return true;
            }
            catch (Exception ex)
            {
                detail = "il2cpp field set failed: " + ex.Message;
            }

            if (WriteIl2CppObjectField(main, 0x20, resources))
            {
                detail = "native offset 0x20";
                return true;
            }

            if (string.IsNullOrEmpty(detail))
                detail = "all field set methods failed";
            return false;
        }

        private static bool TrySetVehiclesMainAssemblyResources(VehiclesMain main, VehicleAssemblyResources resources, out string detail)
        {
            detail = "";
            if (main == null || resources == null)
            {
                detail = "main/resources null";
                return false;
            }

            try
            {
                var field = typeof(VehiclesMain).GetField("defaultAssemblyResources", AnyInstance);
                if (field != null)
                {
                    field.SetValue(main, resources);
                    return true;
                }
            }
            catch (Exception ex)
            {
                detail = "managed field set failed: " + ex.Message;
            }

            try
            {
                var obj = main.Cast<Il2CppSystem.Object>();
                var value = resources.Cast<Il2CppSystem.Object>();
                var flags = Il2CppSystem.Reflection.BindingFlags.Public |
                            Il2CppSystem.Reflection.BindingFlags.NonPublic |
                            Il2CppSystem.Reflection.BindingFlags.Instance;
                var field = obj.GetIl2CppType().GetField("defaultAssemblyResources", flags);
                if (field != null)
                {
                    field.SetValue(obj, value);
                    return true;
                }
            }
            catch (Exception ex)
            {
                detail = "il2cpp field set failed: " + ex.Message;
            }

            if (WriteIl2CppObjectField(main, 0x28, resources))
            {
                detail = "native offset 0x28";
                return true;
            }

            if (string.IsNullOrEmpty(detail))
                detail = "all field set methods failed";
            return false;
        }

        private static bool TryCreateRuntimeVehiclesMain(
            VehicleResources resources,
            VehicleAssemblyResources assemblyResources,
            out VehiclesMain main,
            out string detail)
        {
            main = null;
            detail = "";

            try
            {
                var go = new GameObject("__MP_VehiclesMain_Runtime");
                go.hideFlags = HideFlags.HideAndDontSave;
                go.SetActive(false);

                main = go.AddComponent<VehiclesMain>();
                if (main == null)
                {
                    detail = "AddComponent returned null";
                    return false;
                }

                runtimeVehiclesMain = main;

                TrySetVehiclesMainResources(main, resources, out string resDetail);
                TrySetVehiclesMainAssemblyResources(main, assemblyResources, out string assemblyDetail);
                SetVehicleAssemblyResourcesDefault(assemblyResources);
                EnsureVehicleResourcesGlobal(out _, out _);
                SetVehiclesMainInstance(main);

                go.SetActive(true);

                if (GetVehiclesMainResources(main) == null || GetVehiclesMainAssemblyResources(main) == null)
                {
                    TrySetVehiclesMainResources(main, resources, out resDetail);
                    TrySetVehiclesMainAssemblyResources(main, assemblyResources, out assemblyDetail);
                }

                if (TryInvokeVehiclesMainOnAwake(main, out string awakeDetail))
                    detail = $"res={resDetail}; assembly={assemblyDetail}; awake={awakeDetail}";
                else
                    detail = $"res={resDetail}; assembly={assemblyDetail}; awakeSkipped={awakeDetail}";

                return GetVehiclesMainResources(main) != null &&
                       GetVehiclesMainAssemblyResources(main) != null;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        private static bool TryInvokeVehiclesMainOnAwake(VehiclesMain main, out string detail)
        {
            detail = "";
            if (main == null)
            {
                detail = "main null";
                return false;
            }

            try
            {
                var method = typeof(VehiclesMain).GetMethod("OnAwake", AnyInstance);
                if (method == null)
                {
                    detail = "OnAwake method not found";
                    return false;
                }

                method.Invoke(main, null);
                detail = "invoked";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        private static void SetVehicleAssemblyResourcesDefault(VehicleAssemblyResources resources)
        {
            if (resources == null)
                return;

            try
            {
                var field = typeof(VehicleAssemblyResources).GetField("Default", AnyStatic);
                field?.SetValue(null, resources);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[VehicleRuntime] VehicleAssemblyResources.Default set failed: {ex.Message}");
            }
        }

        private static VehicleFactories GetVehicleFactoriesInstance()
        {
            try
            {
                var prop = typeof(VehicleFactories).GetProperty("instance", AnyStatic) ??
                           typeof(VehicleFactories).GetProperty("Instance", AnyStatic);
                var value = prop?.GetValue(null, null) as VehicleFactories;
                if (value != null)
                    return value;
            }
            catch
            {
            }

            try
            {
                var field = typeof(VehicleFactories).GetField("instance", AnyStatic) ??
                            typeof(VehicleFactories).GetField("Instance", AnyStatic);
                return field?.GetValue(null) as VehicleFactories;
            }
            catch
            {
                return null;
            }
        }

        private static T ReadIl2CppObjectField<T>(Il2CppObjectBase owner, int offset) where T : Il2CppObjectBase
        {
            if (owner == null || owner.Pointer == IntPtr.Zero)
                return null;

            try
            {
                IntPtr ptr = Marshal.ReadIntPtr(IntPtr.Add(owner.Pointer, offset));
                if (ptr == IntPtr.Zero)
                    return null;

                return new Il2CppSystem.Object(ptr).TryCast<T>();
            }
            catch
            {
                return null;
            }
        }

        private static bool WriteIl2CppObjectField(Il2CppObjectBase owner, int offset, Il2CppObjectBase value)
        {
            if (owner == null || value == null || owner.Pointer == IntPtr.Zero || value.Pointer == IntPtr.Zero)
                return false;

            try
            {
                Marshal.WriteIntPtr(IntPtr.Add(owner.Pointer, offset), value.Pointer);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string DescribeIl2CppFields(Il2CppSystem.Type type)
        {
            if (type == null)
                return "noType";

            try
            {
                var flags = Il2CppSystem.Reflection.BindingFlags.Public |
                            Il2CppSystem.Reflection.BindingFlags.NonPublic |
                            Il2CppSystem.Reflection.BindingFlags.Instance |
                            Il2CppSystem.Reflection.BindingFlags.Static;

                var names = new List<string>();
                foreach (var field in type.GetFields(flags))
                {
                    if (names.Count >= 12)
                        break;

                    names.Add(field.Name + ":" + (field.FieldType?.FullName ?? "?"));
                }

                return names.Count > 0 ? string.Join(",", names) : "none";
            }
            catch (Exception ex)
            {
                return "fieldListFailed:" + ex.Message;
            }
        }

        private static string Describe(VehicleResources resources)
        {
            if (resources == null)
                return "null";

            string name = "";
            try { name = resources.name; } catch { }
            return string.IsNullOrEmpty(name) ? resources.GetType().FullName : name;
        }

        private static string DescribeObject(object value)
        {
            if (value == null)
                return "null";

            try
            {
                if (value is Il2CppObjectBase obj)
                    return obj.Pointer != IntPtr.Zero
                        ? (new Il2CppSystem.Object(obj.Pointer).GetIl2CppType()?.FullName ?? obj.GetType().FullName)
                        : obj.GetType().FullName;
            }
            catch
            {
            }

            return value.GetType().FullName;
        }
    }
}
