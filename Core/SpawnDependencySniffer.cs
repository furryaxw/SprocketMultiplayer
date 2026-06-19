using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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

            const int maxSeconds = 30;
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
                        $"spawner={DescribeUnityObject(snapshot.OfficialSpawner)} " +
                        $"techFrame={GetValueTypeName(snapshot.TechFrame)} " +
                        $"register={GetValueTypeName(snapshot.VehicleRegister)} " +
                        $"stageFactories={snapshot.AssemblyStageFactoryCount}");
                    LogDetailedSnapshot(snapshot, "ready");
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

        public static SpawnDependencySnapshot Capture()
        {
            var snapshot = new SpawnDependencySnapshot();

            snapshot.VehicleFactory = FindVehicleFactory();
            snapshot.FactoryType = snapshot.VehicleFactory?.GetIl2CppType()?.FullName ?? "";
            snapshot.FactoryContext = GetFactoryContext(snapshot.VehicleFactory);
            snapshot.FactoryContextReady = snapshot.FactoryContext != null;
            snapshot.DefaultVehicleContext = FindDefaultVehicleContext();

            snapshot.TechFrame = FindTechFrame();
            snapshot.TechFrameType = snapshot.TechFrame?.GetIl2CppType()?.FullName ?? "";

            snapshot.StageSpawnEvents = UnityEngine.Object.FindObjectsOfType<StageVehicleSpawnEvent>();
            snapshot.StageSpawnEventCount = snapshot.StageSpawnEvents?.Length ?? 0;
            snapshot.VehicleRegister = FindVehicleRegister(snapshot.StageSpawnEvents);
            snapshot.VehicleController = FindVehicleController();
            snapshot.SpawnLocator = FindSpawnLocator(snapshot.StageSpawnEvents);
            snapshot.OfficialSpawner = FindOfficialSpawner(snapshot.StageSpawnEvents) ?? UnityEngine.Object.FindObjectOfType<VehicleSpawner>();
            snapshot.VehicleAssemblyResources = FindVehicleAssemblyResources(snapshot.OfficialSpawner);
            snapshot.AssemblyStageFactories = FindAssemblyStageFactories(snapshot.OfficialSpawner, snapshot.VehicleAssemblyResources);
            snapshot.AssemblyStageFactoryCount = snapshot.AssemblyStageFactories?.Length ?? 0;

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

        private static IVehicleRegister FindVehicleRegister(StageVehicleSpawnEvent[] stageEvents)
        {
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

        private static VehicleSpawner FindOfficialSpawner(StageVehicleSpawnEvent[] stageEvents)
        {
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

        private static SpawnLocator FindSpawnLocator(StageVehicleSpawnEvent[] stageEvents)
        {
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
                return null;
            }
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
                $"context={(s.FactoryContextReady ? "yes" : "no")} " +
                $"defaultContext={(s.DefaultVehicleContext != null ? "yes" : "no")} " +
                $"techFrame={(s.TechFrame != null ? "yes" : "no")} " +
                $"officialSpawner={(s.OfficialSpawner != null ? "yes" : "no")} " +
                $"stageEvents={s.StageSpawnEventCount} " +
                $"locator={(s.SpawnLocator != null ? "yes" : "no")} " +
                $"stageFactories={s.AssemblyStageFactoryCount} " +
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
                $"factory={SpawnSummaryLog.YesNo(s.VehicleFactory != null)} " +
                $"context={SpawnSummaryLog.YesNo(s.FactoryContextReady)} " +
                $"defaultContext={SpawnSummaryLog.YesNo(s.DefaultVehicleContext != null)} " +
                $"techFrame={SpawnSummaryLog.YesNo(s.TechFrame != null)} " +
                $"officialSpawner={SpawnSummaryLog.YesNo(s.OfficialSpawner != null)} " +
                $"stageEvents={s.StageSpawnEventCount} " +
                $"locator={SpawnSummaryLog.YesNo(s.SpawnLocator != null)} " +
                $"stageFactories={s.AssemblyStageFactoryCount} " +
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
            if (s.VehicleRegister == null) missing.Add("register");
            if (s.VehicleController == null) missing.Add("controller");

            return missing.Count == 0 ? "none" : string.Join(",", missing.ToArray());
        }

        private static void LogDetailedSnapshot(SpawnDependencySnapshot s, string phase)
        {
            MelonLogger.Msg($"[SpawnSniffer:detail] phase={phase}");
            LogFactoryDetails(s.VehicleFactory, s.FactoryContext, s.DefaultVehicleContext);
            LogAssemblyResourcesDetails(s.VehicleAssemblyResources, s.AssemblyStageFactories);
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
        public string FactoryType;
        public bool FactoryContextReady;
        public Il2CppSystem.Object FactoryContext;
        public VehicleContext DefaultVehicleContext;
        public Il2CppSystem.Object TechFrame;
        public string TechFrameType;
        public StageVehicleSpawnEvent[] StageSpawnEvents;
        public int StageSpawnEventCount;
        public SpawnLocator SpawnLocator;
        public VehicleSpawner OfficialSpawner;
        public VehicleAssemblyResources VehicleAssemblyResources;
        public Il2CppReferenceArray<IVehicleAssemblyStageFactory> AssemblyStageFactories;
        public int AssemblyStageFactoryCount;
        public IVehicleRegister VehicleRegister;
        public VehicleController VehicleController;

        public bool IsReady =>
            VehicleFactory != null &&
            FactoryContextReady &&
            TechFrame != null &&
            OfficialSpawner != null &&
            AssemblyStageFactoryCount > 0 &&
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
}
