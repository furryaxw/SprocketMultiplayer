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

                if (snapshot.IsReady)
                {
                    MelonLogger.Msg("[SpawnSniffer] All required spawn dependencies are available.");
                    running = false;
                    yield break;
                }

                yield return new WaitForSeconds(1f);
            }

            MelonLogger.Warning("[SpawnSniffer] Dependency sniff timed out. See previous snapshots for missing pieces.");
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
