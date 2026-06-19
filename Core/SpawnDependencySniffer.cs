using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSprocket.Gameplay.VehicleControl;
using Il2CppSprocket.TechTrees;
using Il2CppSprocket.Vehicles;
using Il2CppSprocket.Vehicles.Spawning;
using MelonLoader;
using UnityEngine;

namespace SprocketMultiplayer.Core
{
    public static class SpawnDependencySniffer
    {
        private static bool running;

        public static void Start()
        {
            if (running) return;
            running = true;
            MelonCoroutines.Start(Run());
        }

        private static IEnumerator Run()
        {
            MelonLogger.Msg("[SpawnSniffer] Starting dependency sniff.");

            const int maxSeconds = 30;
            for (int i = 0; i < maxSeconds; i++)
            {
                var snapshot = Capture();
                LogSnapshot(snapshot, i + 1);

                if (i == 0)
                    LogDetailedSnapshot(snapshot, "initial");

                if (snapshot.IsReady)
                {
                    MelonLogger.Msg("[SpawnSniffer] All required spawn dependencies are available.");
                    LogDetailedSnapshot(snapshot, "ready");
                    running = false;
                    yield break;
                }

                yield return new WaitForSeconds(1f);
            }

            MelonLogger.Warning("[SpawnSniffer] Dependency sniff timed out. See previous snapshots for missing pieces.");
            LogDetailedSnapshot(Capture(), "timeout");
            running = false;
        }

        public static SpawnDependencySnapshot Capture()
        {
            var snapshot = new SpawnDependencySnapshot();

            snapshot.VehicleFactory = FindVehicleFactory();
            snapshot.FactoryType = snapshot.VehicleFactory?.GetIl2CppType()?.FullName ?? "";
            snapshot.FactoryContextReady = HasReadyFactoryContext(snapshot.VehicleFactory);

            snapshot.TechFrame = FindTechFrame();
            snapshot.TechFrameType = snapshot.TechFrame?.GetIl2CppType()?.FullName ?? "";

            snapshot.VehicleRegister = FindVehicleRegister();
            snapshot.VehicleController = FindVehicleController();
            snapshot.OfficialSpawner = UnityEngine.Object.FindObjectOfType<VehicleSpawner>();
            snapshot.AssemblyStageFactories = FindAssemblyStageFactories(snapshot.OfficialSpawner);
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

        private static bool HasReadyFactoryContext(Il2CppSystem.Object factory)
        {
            if (factory == null) return false;

            try
            {
                var flags = Il2CppSystem.Reflection.BindingFlags.Public |
                            Il2CppSystem.Reflection.BindingFlags.NonPublic |
                            Il2CppSystem.Reflection.BindingFlags.Instance;
                var field = factory.GetIl2CppType().GetField("context", flags);
                if (field == null) return true;
                return field.GetValue(factory) != null;
            }
            catch
            {
                return false;
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

            return null;
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

        private static VehicleRegister FindVehicleRegister()
        {
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null || mb.Pointer == IntPtr.Zero) continue;
                try
                {
                    var reg = new Il2CppSystem.Object(mb.Pointer).TryCast<VehicleRegister>();
                    if (reg != null) return reg;
                }
                catch { }
            }

            return null;
        }

        private static VehicleController FindVehicleController()
        {
            var controllers = UnityEngine.Object.FindObjectsOfType<VehicleController>();
            return controllers != null && controllers.Length > 0 ? controllers[0] : null;
        }

        private static Il2CppReferenceArray<IVehicleAssemblyStageFactory> FindAssemblyStageFactories(VehicleSpawner spawner)
        {
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
                $"techFrame={(s.TechFrame != null ? "yes" : "no")} " +
                $"officialSpawner={(s.OfficialSpawner != null ? "yes" : "no")} " +
                $"stageFactories={s.AssemblyStageFactoryCount} " +
                $"register={(s.VehicleRegister != null ? "yes" : "no")} " +
                $"controller={(s.VehicleController != null ? "yes" : "no")}");
        }

        private static void LogDetailedSnapshot(SpawnDependencySnapshot s, string phase)
        {
            MelonLogger.Msg($"[SpawnSniffer:detail] phase={phase}");
            LogFactoryDetails(s.VehicleFactory);
            LogSpawnerDetails();
            LogSceneComponentCandidates();
        }

        private static void LogFactoryDetails(Il2CppSystem.Object factory)
        {
            if (factory == null)
            {
                MelonLogger.Msg("[SpawnSniffer:factory] VehicleFactories.instance is null.");
                return;
            }

            var type = factory.GetIl2CppType();
            MelonLogger.Msg($"[SpawnSniffer:factory] type={type?.FullName ?? "unknown"}");

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
                try { if (obj != null && obj.TryCast<VehicleRegister>() != null) registers.Add(descriptor); } catch { }
                try { if (obj != null && obj.TryCast<VehicleSource>() != null) vehicleSources.Add(descriptor); } catch { }
                try { if (obj != null && obj.TryCast<IVehicleAssemblyStageFactory>() != null) stageFactories.Add(descriptor); } catch { }

                if (descriptor.IndexOf("spawner", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("spawner", StringComparison.OrdinalIgnoreCase) >= 0)
                    spawnerNamedComponents.Add(descriptor);
            }

            LogList("techFrameCandidate", techFrames, 16);
            LogList("vehicleRegisterCandidate", registers, 16);
            LogList("vehicleSourceCandidate", vehicleSources, 16);
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
                   text.Contains("spawner") ||
                   text.Contains("source") ||
                   text.Contains("register") ||
                   text.Contains("vehicle");
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
        public Il2CppSystem.Object TechFrame;
        public string TechFrameType;
        public VehicleSpawner OfficialSpawner;
        public Il2CppReferenceArray<IVehicleAssemblyStageFactory> AssemblyStageFactories;
        public int AssemblyStageFactoryCount;
        public VehicleRegister VehicleRegister;
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
}
