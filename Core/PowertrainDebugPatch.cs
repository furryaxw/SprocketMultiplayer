using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSprocket.Vehicles;
using Il2CppSprocket.Vehicles.PhysicsSystems;
using Il2CppSprocket.Vehicles.Powertrains;
using Il2CppVehicleDesigner.Powertrains;
using MelonLoader;
using UnityEngine;

namespace SprocketMultiplayer.Core
{
    [HarmonyPatch]
    internal static class PowertrainDebugPatch
    {
        private const string Tag = "[DEBUG-powertrain-body]";
        private static int logged;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PowertrainAssemblyStage), "Execute");
        }

        private static void Prefix(PowertrainAssemblyStage __instance)
        {
            try
            {
                object vehicle = ReadMember(__instance, "vehicle");
                object layout = ReadMember(__instance, "layout");
                string movementRegister = EnsureMovementRegister();
                if (vehicle == null)
                {
                    LogOnce("stage vehicle=null layout=" + TypeName(layout) + " movementRegister=" + movementRegister);
                    return;
                }

                Rigidbody before = ReadRigidbody(vehicle);
                object behaviour = ReadMember(layout, "Behaviour");
                object propulsion = ReadMember(layout, "Propulsion");

                LogOnce(
                    $"stage body={SpawnSummaryLog.YesNo(before != null)} " +
                    $"vehicle={TypeName(vehicle)} layout={TypeName(layout)} " +
                    $"movementRegister={movementRegister} " +
                    $"behaviour={TypeName(behaviour)} {DescribePowertrainBehaviour(behaviour)} " +
                    $"propulsion={DescribePropulsion(propulsion)}");

                if (before != null)
                    return;

                object gateway = ReadMember(vehicle, "VehicleStructure") ?? ReadMember(vehicle, "VehicleInfo");
                Rigidbody body = ReadRigidbody(gateway);
                string source = body != null ? "gateway" : "";

                if (body == null)
                {
                    var component = AsIl2CppObject(gateway)?.TryCast<Component>() ??
                                    AsIl2CppObject(vehicle)?.TryCast<Component>();
                    var go = component?.gameObject;
                    if (go != null)
                    {
                        body = go.GetComponent<Rigidbody>();
                        if (body == null)
                        {
                            float mass = ReadFloat(vehicle, "Mass");
                            if (mass <= 0f)
                                mass = ReadFloat(gateway, "Mass");
                            if (mass <= 0f)
                                mass = 1f;

                            try { body = VehiclePhysics.SetupRigidbody(go, mass); }
                            catch { body = go.AddComponent<Rigidbody>(); body.mass = mass; }

                            source = "created";
                        }
                        else
                        {
                            source = "component";
                        }
                    }
                }

                if (body == null)
                {
                    LogOnce("repair skipped body=null vehicle=" + TypeName(vehicle) + " gateway=" + TypeName(gateway));
                    return;
                }

                bool setVehicle = SetMember(vehicle, "body", body);
                bool setGateway = gateway != null && SetMember(gateway, "body", body);

                LogOnce(
                    $"repair source={source} setVehicle={SpawnSummaryLog.YesNo(setVehicle)} " +
                    $"setGateway={SpawnSummaryLog.YesNo(setGateway)} bodyMass={body.mass:0.##} " +
                    $"vehicle={TypeName(vehicle)} gateway={TypeName(gateway)}");
            }
            catch (Exception ex)
            {
                LogOnce("failed " + Clean(ex.Message));
            }
        }

        private static Rigidbody ReadRigidbody(object owner)
        {
            try
            {
                var body = ReadMember(owner, "Rigidbody") as Rigidbody;
                if (body != null)
                    return body;
            }
            catch { }

            try
            {
                var body = ReadMember(owner, "body") as Rigidbody;
                if (body != null)
                    return body;
            }
            catch { }

            try
            {
                var component = AsIl2CppObject(owner)?.TryCast<Component>();
                return component != null ? component.GetComponent<Rigidbody>() : null;
            }
            catch
            {
                return null;
            }
        }

        private static float ReadFloat(object owner, string name)
        {
            try
            {
                object value = ReadMember(owner, name);
                if (value == null)
                    return 0f;

                if (value is float f)
                    return f;

                var il2Value = AsIl2CppObject(value);
                if (il2Value != null)
                    return il2Value.Unbox<float>();

                return Convert.ToSingle(value);
            }
            catch
            {
                return 0f;
            }
        }

        private static string DescribePropulsion(object propulsion)
        {
            if (propulsion == null)
                return "null";

            int count = ReadCount(propulsion);
            object leftValue = ReadIndexedValue(propulsion, 0);
            object rightValue = ReadIndexedValue(propulsion, 1);
            bool left = leftValue != null;
            bool right = rightValue != null;

            return
                $"count={count} left={SpawnSummaryLog.YesNo(left)} leftType={TypeName(leftValue)} " +
                $"right={SpawnSummaryLog.YesNo(right)} rightType={TypeName(rightValue)} type={TypeName(propulsion)}";
        }

        private static string DescribePowertrainBehaviour(object behaviour)
        {
            if (behaviour == null)
                return "";

            object engine = ReadMember(behaviour, "Engine");
            object transmissions = ReadMember(behaviour, "Transmissions");
            object mass = ReadMember(behaviour, "mass");
            object verticalGravity = ReadMember(behaviour, "verticalGravity");

            return
                $"engine={TypeName(engine)} transmissions={DescribeCount(transmissions)} " +
                $"{DescribeNativeArray(behaviour, "leftState")} " +
                $"{DescribeNativeArray(behaviour, "rightState")} " +
                $"{DescribeNativeArray(behaviour, "outputs")} " +
                $"mass={CleanValue(mass)} verticalGravity={CleanValue(verticalGravity)}";
        }

        private static string DescribeNativeArray(object owner, string name)
        {
            object value = ReadMember(owner, name);
            if (value == null)
                return name + "=null";

            string created = CleanValue(ReadMember(value, "IsCreated"));
            string length = CleanValue(ReadMember(value, "Length"));

            if (string.IsNullOrEmpty(created))
                created = "?";
            if (string.IsNullOrEmpty(length))
                length = "?";

            return $"{name}=created:{created}/len:{length}/type:{TypeName(value)}";
        }

        private static string DescribeCount(object value)
        {
            if (value == null)
                return "null";

            int count = ReadCount(value);
            return count >= 0 ? $"count={count} type={TypeName(value)}" : TypeName(value);
        }

        private static string EnsureMovementRegister()
        {
            try
            {
                var existing = VehicleMovementRegister.Instance;
                if (existing != null)
                    return "existing " + DescribeMovementRegister(existing);

                var created = new VehicleMovementRegister();
                VehicleMovementRegister.Instance = created;
                return "created " + DescribeMovementRegister(created);
            }
            catch (Exception ex)
            {
                return "error:" + Clean(ex.Message);
            }
        }

        private static string DescribeMovementRegister(VehicleMovementRegister register)
        {
            if (register == null)
                return "null";

            object entries = ReadMember(register, "register");
            return "entries=" + DescribeCount(entries);
        }

        private static int ReadCount(object value)
        {
            if (value == null)
                return -1;

            try
            {
                var il2Object = AsIl2CppObject(value);
                if (il2Object != null && il2Object.Pointer != IntPtr.Zero)
                    return System.Runtime.InteropServices.Marshal.ReadInt32(il2Object.Pointer, 0x18);
            }
            catch { }

            try
            {
                var prop = value.GetType().GetProperty("Length");
                if (prop != null)
                    return Convert.ToInt32(prop.GetValue(value, null));
            }
            catch { }

            try
            {
                var prop = value.GetType().GetProperty("Count");
                if (prop != null)
                    return Convert.ToInt32(prop.GetValue(value, null));
            }
            catch { }

            try
            {
                var il2Object = AsIl2CppObject(value);
                var il2Type = il2Object?.GetIl2CppType();
                if (il2Type != null)
                {
                    var flags = Il2CppSystem.Reflection.BindingFlags.Public |
                                Il2CppSystem.Reflection.BindingFlags.NonPublic |
                                Il2CppSystem.Reflection.BindingFlags.Instance;
                    var prop = il2Type.GetProperty("Length", flags) ?? il2Type.GetProperty("Count", flags);
                    if (prop != null)
                        return Convert.ToInt32(prop.GetValue(il2Object, null));
                }
            }
            catch { }

            return -1;
        }

        private static object ReadIndexedValue(object value, int index)
        {
            if (value == null || index < 0)
                return null;

            try
            {
                var il2Object = AsIl2CppObject(value);
                if (il2Object != null && il2Object.Pointer != IntPtr.Zero)
                {
                    int length = System.Runtime.InteropServices.Marshal.ReadInt32(il2Object.Pointer, 0x18);
                    if (length <= index)
                        return null;

                    IntPtr item = System.Runtime.InteropServices.Marshal.ReadIntPtr(
                        il2Object.Pointer,
                        0x20 + index * IntPtr.Size);
                    return item != IntPtr.Zero ? new Il2CppSystem.Object(item) : null;
                }
            }
            catch { }

            try
            {
                if (value is Array array && array.Length > index)
                    return array.GetValue(index);
            }
            catch { }

            try
            {
                var prop = value.GetType().GetProperty("Item");
                if (prop != null)
                    return prop.GetValue(value, new object[] { index });
            }
            catch { }

            try
            {
                var method = value.GetType().GetMethod("GetValue", new[] { typeof(int) });
                if (method != null)
                    return method.Invoke(value, new object[] { index });
            }
            catch { }

            return null;
        }

        private static object ReadMember(object owner, string name)
        {
            if (owner == null)
                return null;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            try
            {
                var type = owner.GetType();
                var prop = type.GetProperty(name, flags);
                if (prop != null && prop.CanRead)
                    return prop.GetValue(owner, null);

                var field = type.GetField(name, flags);
                if (field != null)
                    return field.GetValue(owner);
            }
            catch { }

            try
            {
                var il2Object = AsIl2CppObject(owner);
                if (il2Object == null)
                    return null;

                var il2Flags = Il2CppSystem.Reflection.BindingFlags.Public |
                               Il2CppSystem.Reflection.BindingFlags.NonPublic |
                               Il2CppSystem.Reflection.BindingFlags.Instance;

                var il2Type = il2Object.GetIl2CppType();
                var prop = il2Type.GetProperty(name, il2Flags);
                if (prop != null)
                    return prop.GetValue(il2Object, null);

                var field = il2Type.GetField(name, il2Flags);
                if (field != null)
                    return field.GetValue(il2Object);
            }
            catch { }

            return null;
        }

        private static bool SetMember(object owner, string name, object value)
        {
            if (owner == null || value == null)
                return false;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            try
            {
                var type = owner.GetType();
                var field = type.GetField(name, flags);
                if (field != null)
                {
                    field.SetValue(owner, value);
                    return true;
                }

                var prop = type.GetProperty(name, flags);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(owner, value, null);
                    return true;
                }
            }
            catch { }

            try
            {
                var il2Object = AsIl2CppObject(owner);
                if (il2Object == null)
                    return false;

                var il2Flags = Il2CppSystem.Reflection.BindingFlags.Public |
                               Il2CppSystem.Reflection.BindingFlags.NonPublic |
                               Il2CppSystem.Reflection.BindingFlags.Instance;

                var il2Type = il2Object.GetIl2CppType();
                var field = il2Type.GetField(name, il2Flags);
                if (field != null)
                {
                    field.SetValue(il2Object, AsIl2CppObject(value));
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static Il2CppSystem.Object AsIl2CppObject(object value)
        {
            if (value == null)
                return null;

            if (value is Il2CppSystem.Object il2Object)
                return il2Object;

            if (value is Il2CppObjectBase obj && obj.Pointer != IntPtr.Zero)
                return new Il2CppSystem.Object(obj.Pointer);

            return null;
        }

        private static void LogOnce(string message)
        {
            if (logged >= 8)
                return;

            logged++;
            string line = $"{Tag} {message}";
            SpawnSummaryLog.Info(line);
            MelonLogger.Msg(line);
        }

        private static string TypeName(object value)
        {
            if (value == null)
                return "null";

            try
            {
                var il2Object = AsIl2CppObject(value);
                if (il2Object != null)
                    return Clean(il2Object.GetIl2CppType()?.FullName ?? value.GetType().FullName);
            }
            catch { }

            return Clean(value.GetType().FullName);
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            return value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        }

        private static string CleanValue(object value)
        {
            if (value == null)
                return "";

            try
            {
                var il2Value = AsIl2CppObject(value);
                if (il2Value != null)
                    return Clean(il2Value.ToString());
            }
            catch { }

            return Clean(value.ToString());
        }
    }
}
