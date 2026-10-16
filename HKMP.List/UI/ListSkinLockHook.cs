using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using Hkmp.Api.Server;
using Hkmp.Game;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HKMP.List.Server
{
    internal static class ListSkinLockHook
    {
        private static readonly object Gate = new object();

        private static Hook _hook;
        private static ListServerAddon _serverAddon;
        private static FieldInfo _teamStoreField;
        private static DateTime _lastFailureLogUtc = DateTime.MinValue;

        internal static bool IsInstalled
        {
            get
            {
                lock (Gate)
                {
                    return _hook != null;
                }
            }
        }

        internal static void Configure(ListServerAddon serverAddon)
        {
            lock (Gate)
            {
                _serverAddon = serverAddon;
                _teamStoreField = typeof(ListServerAddon).GetField(
                    "_teamStore",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            Install();
        }

        internal static bool Install()
        {
            lock (Gate)
            {
                if (_hook != null)
                {
                    return true;
                }

                try
                {
                    Assembly hkmpAssembly = typeof(Team).Assembly;
                    Type serverManagerType = hkmpAssembly.GetType(
                        "Hkmp.Game.Server.ServerManager",
                        false);

                    if (serverManagerType == null)
                    {
                        throw new TypeLoadException(
                            "HKMP ServerManager type was not found.");
                    }

                    MethodInfo directTarget = FindMethod(
                        serverManagerType,
                        "TryUpdatePlayerSkin",
                        new[]
                        {
                            typeof(ushort),
                            typeof(byte),
                            typeof(string).MakeByRefType()
                        });

                    if (directTarget != null &&
                        !directTarget.IsStatic &&
                        directTarget.ReturnType == typeof(bool))
                    {
                        InstallSkinIdMethodHook(directTarget);
                        Debug.Log(
                            "HKMP.List: server-side skin lock is guarding ServerManager.TryUpdatePlayerSkin.");
                        return true;
                    }

                    MethodInfo packetTarget = FindPacketSkinHandler(
                        serverManagerType,
                        "OnPlayerSkinUpdate");

                    if (packetTarget == null)
                    {
                        throw new MissingMethodException(
                            "HKMP ServerManager exposes neither TryUpdatePlayerSkin(ushort, byte, out string) nor OnPlayerSkinUpdate(ushort, packet).");
                    }

                    InstallSkinPacketMethodHook(packetTarget);
                    Debug.Log(
                        "HKMP.List: server-side skin lock is guarding the legacy ServerManager.OnPlayerSkinUpdate packet handler.");
                    return true;
                }
                catch (Exception exception)
                {
                    _hook = null;
                    LogInstallFailure(exception);
                    return false;
                }
            }
        }

        private static void InstallSkinIdMethodHook(MethodInfo target)
        {
            Type stringReference = typeof(string).MakeByRefType();
            Type instanceType = target.DeclaringType;

            Type originalDelegateType = Expression.GetDelegateType(
                instanceType,
                typeof(ushort),
                typeof(byte),
                stringReference,
                typeof(bool));

            Type detourDelegateType = Expression.GetDelegateType(
                originalDelegateType,
                instanceType,
                typeof(ushort),
                typeof(byte),
                stringReference,
                typeof(bool));

            DynamicMethod detourMethod = new DynamicMethod(
                "HKMPListEnforceLockedTeamSkin",
                typeof(bool),
                new[]
                {
                    originalDelegateType,
                    instanceType,
                    typeof(ushort),
                    typeof(byte),
                    stringReference
                },
                typeof(ListSkinLockHook).Module,
                true);

            ILGenerator il = detourMethod.GetILGenerator();
            LocalBuilder enforcedSkinId = il.DeclareLocal(typeof(byte));
            Label useRequestedSkin = il.DefineLabel();

            MethodInfo resolver = typeof(ListSkinLockHook).GetMethod(
                nameof(TryResolveLockedTeamSkin),
                BindingFlags.Static | BindingFlags.NonPublic);

            MethodInfo originalInvoke = originalDelegateType.GetMethod("Invoke");
            if (resolver == null || originalInvoke == null)
            {
                throw new MissingMethodException(
                    "Could not resolve the skin-lock helper or original HKMP delegate.");
            }

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldloca_S, enforcedSkinId);
            il.Emit(OpCodes.Call, resolver);
            il.Emit(OpCodes.Brfalse_S, useRequestedSkin);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldloc, enforcedSkinId);
            il.Emit(OpCodes.Ldarg_S, (byte)4);
            il.Emit(OpCodes.Callvirt, originalInvoke);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(useRequestedSkin);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Ldarg_S, (byte)4);
            il.Emit(OpCodes.Callvirt, originalInvoke);
            il.Emit(OpCodes.Ret);

            Delegate detour = detourMethod.CreateDelegate(detourDelegateType);
            _hook = new Hook(target, detour);

            if (_hook == null)
            {
                throw new InvalidOperationException(
                    "MonoMod did not create the HKMP skin-lock hook.");
            }
        }

        private static void InstallSkinPacketMethodHook(MethodInfo target)
        {
            ParameterInfo[] parameters = target.GetParameters();
            Type instanceType = target.DeclaringType;
            Type packetType = parameters[1].ParameterType;

            Type originalDelegateType = Expression.GetDelegateType(
                instanceType,
                typeof(ushort),
                packetType,
                typeof(void));

            Type detourDelegateType = Expression.GetDelegateType(
                originalDelegateType,
                instanceType,
                typeof(ushort),
                packetType,
                typeof(void));

            DynamicMethod detourMethod = new DynamicMethod(
                "HKMPListEnforceLockedTeamSkinPacket",
                typeof(void),
                new[]
                {
                    originalDelegateType,
                    instanceType,
                    typeof(ushort),
                    packetType
                },
                typeof(ListSkinLockHook).Module,
                true);

            ILGenerator il = detourMethod.GetILGenerator();
            Label callOriginal = il.DefineLabel();

            MethodInfo preparePacket = typeof(ListSkinLockHook).GetMethod(
                nameof(PrepareSkinPacketForOriginal),
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo originalInvoke = originalDelegateType.GetMethod("Invoke");

            if (preparePacket == null || originalInvoke == null)
            {
                throw new MissingMethodException(
                    "Could not resolve the packet skin-lock helper or original HKMP delegate.");
            }

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            if (packetType.IsValueType)
            {
                il.Emit(OpCodes.Box, packetType);
            }
            il.Emit(OpCodes.Call, preparePacket);
            il.Emit(OpCodes.Brtrue_S, callOriginal);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(callOriginal);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Callvirt, originalInvoke);
            il.Emit(OpCodes.Ret);

            Delegate detour = detourMethod.CreateDelegate(detourDelegateType);
            _hook = new Hook(target, detour);

            if (_hook == null)
            {
                throw new InvalidOperationException(
                    "MonoMod did not create the legacy HKMP skin packet hook.");
            }
        }

        private static MethodInfo FindMethod(
            Type targetType,
            string methodName,
            Type[] parameterTypes)
        {
            for (Type current = targetType;
                 current != null;
                 current = current.BaseType)
            {
                MethodInfo method = current.GetMethod(
                    methodName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly,
                    null,
                    parameterTypes,
                    null);

                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo FindPacketSkinHandler(
            Type targetType,
            string methodName)
        {
            for (Type current = targetType;
                 current != null;
                 current = current.BaseType)
            {
                MethodInfo[] methods = current.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                foreach (MethodInfo method in methods)
                {
                    if (!string.Equals(
                            method.Name,
                            methodName,
                            StringComparison.Ordinal) ||
                        method.IsStatic ||
                        method.ReturnType != typeof(void))
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 2 &&
                        parameters[0].ParameterType == typeof(ushort) &&
                        parameters[1].ParameterType.IsClass &&
                        !parameters[1].ParameterType.IsByRef &&
                        !parameters[1].ParameterType.IsPointer)
                    {
                        return method;
                    }
                }
            }

            return null;
        }

        private static bool PrepareSkinPacketForOriginal(
            object serverManagerObject,
            ushort playerId,
            object packet)
        {
            if (packet == null)
            {
                return false;
            }

            byte enforcedSkinId;
            if (!TryResolveLockedTeamSkin(
                    serverManagerObject,
                    playerId,
                    out enforcedSkinId))
            {
                return true;
            }

            if (TrySetPacketSkinId(packet, enforcedSkinId))
            {
                return true;
            }

            Debug.LogError(
                "HKMP.List: a team skin is locked, but HKMP's legacy skin packet could not be rewritten. The original packet was blocked.");
            return false;
        }

        private static bool TrySetPacketSkinId(
            object packet,
            byte skinId)
        {
            if (packet == null)
            {
                return false;
            }

            Type packetType = packet.GetType();
            PropertyInfo property = packetType.GetProperty(
                "SkinId",
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic);

            if (property != null &&
                property.PropertyType == typeof(byte))
            {
                MethodInfo setter = property.GetSetMethod(true);
                if (setter != null)
                {
                    try
                    {
                        setter.Invoke(packet, new object[] { skinId });
                        return true;
                    }
                    catch
                    {
                    }
                }
            }

            string[] fieldNames =
            {
                "<SkinId>k__BackingField",
                "_skinId",
                "skinId"
            };

            foreach (string fieldName in fieldNames)
            {
                FieldInfo field = packetType.GetField(
                    fieldName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

                if (field == null || field.FieldType != typeof(byte))
                {
                    continue;
                }

                try
                {
                    field.SetValue(packet, skinId);
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TryResolveLockedTeamSkin(
            object serverManagerObject,
            ushort playerId,
            out byte skinId)
        {
            skinId = 0;

            IServerManager serverManager =
                serverManagerObject as IServerManager;

            if (serverManager == null)
            {
                return false;
            }

            IServerPlayer player;
            try
            {
                player = serverManager.GetPlayer(playerId);
            }
            catch
            {
                return false;
            }

            if (player == null)
            {
                return false;
            }

            ListServerAddon serverAddon;
            FieldInfo teamStoreField;
            lock (Gate)
            {
                serverAddon = _serverAddon;
                teamStoreField = _teamStoreField;
            }

            if (serverAddon == null || teamStoreField == null)
            {
                skinId = player.SkinId;
                return true;
            }

            ListHostTeamStore teamStore;
            try
            {
                teamStore = teamStoreField.GetValue(serverAddon) as ListHostTeamStore;
            }
            catch
            {
                teamStore = null;
            }

            if (teamStore == null)
            {
                skinId = player.SkinId;
                return true;
            }

            bool hasTeamSkin;
            byte teamSkinId;
            bool teamSkinLocked;
            long updatedAtUtcTicks;

            if (!teamStore.TryGetTeamAppearance(
                    player.Team,
                    out hasTeamSkin,
                    out teamSkinId,
                    out teamSkinLocked,
                    out updatedAtUtcTicks) ||
                !hasTeamSkin ||
                !teamSkinLocked)
            {
                return false;
            }

            skinId = teamSkinId;
            return true;
        }

        private static void LogInstallFailure(Exception exception)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastFailureLogUtc).TotalSeconds < 10d)
            {
                return;
            }

            _lastFailureLogUtc = now;
            Debug.LogError(
                "HKMP.List: could not install the server-side team skin lock: " +
                exception.GetType().Name +
                ": " +
                exception.Message);
        }
    }
}
