using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using Hkmp.Game;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HKMP.List.UI
{
    internal static class ListChatKeybindHook
    {
        private static readonly object Gate = new object();

        private static Hook _hook;
        private static MethodInfo _hideChatInput;
        private static FieldInfo _isOpenField;
        private static object _lastHiddenChatBox;
        private static bool _listVisible;
        private static DateTime _lastFailureLogUtc = DateTime.MinValue;
        private static DateTime _lastBindingFailureLogUtc = DateTime.MinValue;

        private static object _savedChatOpenAction;
        private static MemberInfo _savedChatOpenEnabledMember;
        private static bool _savedChatOpenEnabled;
        private static bool _hasSavedChatOpenEnabled;
        private static object _lastObservedSettings;

        public static bool IsInstalled
        {
            get
            {
                lock (Gate)
                {
                    return _hook != null;
                }
            }
        }

        public static void OnListVisibilityChanged(bool visible)
        {
            object settings;

            lock (Gate)
            {
                _listVisible = visible;
                settings = _lastObservedSettings;

                if (!visible)
                {
                    _lastHiddenChatBox = null;
                }
            }

            if (visible)
            {
                Install();

                if (settings != null)
                {
                    DisableChatOpenBinding(settings);
                }

                EnforceChatClosedWhileVisible();
            }
            else
            {
                RestoreChatOpenBinding();
            }
        }

        public static bool Install()
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

                    Type chatBoxType = hkmpAssembly.GetType(
                        "Hkmp.Ui.Chat.ChatBox",
                        false);

                    Type settingsType = hkmpAssembly.GetType(
                        "Hkmp.Game.Settings.ModSettings",
                        false);

                    if (chatBoxType == null || settingsType == null)
                    {
                        throw new TypeLoadException(
                            "HKMP ChatBox or ModSettings type was not found.");
                    }

                    MethodInfo target = chatBoxType.GetMethod(
                        "CheckKeyBinds",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic,
                        null,
                        new[] { settingsType },
                        null);

                    if (target == null ||
                        target.IsStatic ||
                        target.ReturnType != typeof(void))
                    {
                        throw new MissingMethodException(
                            "HKMP ChatBox.CheckKeyBinds(ModSettings) was not found.");
                    }

                    MethodInfo hideChatInput = chatBoxType.GetMethod(
                        "HideChatInput",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic,
                        null,
                        Type.EmptyTypes,
                        null);

                    if (hideChatInput == null ||
                        hideChatInput.IsStatic ||
                        hideChatInput.ReturnType != typeof(void))
                    {
                        throw new MissingMethodException(
                            "HKMP ChatBox.HideChatInput() was not found.");
                    }

                    MethodInfo suppressMethod =
                        typeof(ListChatKeybindHook).GetMethod(
                            nameof(ShouldSuppressChatInput),
                            BindingFlags.Static |
                            BindingFlags.NonPublic);

                    if (suppressMethod == null)
                    {
                        throw new MissingMethodException(
                            "ListChatKeybindHook.ShouldSuppressChatInput was not found.");
                    }

                    Type originalDelegateType =
                        Expression.GetDelegateType(
                            chatBoxType,
                            settingsType,
                            typeof(void));

                    Type detourDelegateType =
                        Expression.GetDelegateType(
                            originalDelegateType,
                            chatBoxType,
                            settingsType,
                            typeof(void));

                    DynamicMethod detourMethod = new DynamicMethod(
                        "HKMPListSuppressChatHotkeyWhileListVisible",
                        typeof(void),
                        new[]
                        {
                            originalDelegateType,
                            chatBoxType,
                            settingsType
                        },
                        typeof(ListChatKeybindHook).Module,
                        true);

                    ILGenerator il = detourMethod.GetILGenerator();
                    Label callOriginal = il.DefineLabel();

                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Call, suppressMethod);
                    il.Emit(OpCodes.Brfalse_S, callOriginal);
                    il.Emit(OpCodes.Ret);

                    il.MarkLabel(callOriginal);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(
                        OpCodes.Callvirt,
                        originalDelegateType.GetMethod("Invoke"));
                    il.Emit(OpCodes.Ret);

                    Delegate detour =
                        detourMethod.CreateDelegate(detourDelegateType);

                    _hideChatInput = hideChatInput;
                    _isOpenField = chatBoxType.GetField(
                        "_isOpen",
                        BindingFlags.Instance |
                        BindingFlags.NonPublic);

                    _hook = new Hook(target, detour);

                    if (_hook == null)
                    {
                        throw new InvalidOperationException(
                            "MonoMod did not create the HKMP chat keybind hook.");
                    }

                    return true;
                }
                catch (Exception exception)
                {
                    _hook = null;
                    _hideChatInput = null;
                    _isOpenField = null;

                    LogInstallFailure(exception);
                    return false;
                }
            }
        }

        public static void EnforceChatClosedWhileVisible()
        {
            bool visible;
            object settings;

            lock (Gate)
            {
                visible = _listVisible;
                settings = _lastObservedSettings;
            }

            visible = visible || ListCursorHook.IsListVisible;
            if (!visible)
            {
                RestoreChatOpenBinding();
                return;
            }

            if (settings != null)
            {
                DisableChatOpenBinding(settings);
            }

            object chatBox = GetChatBoxFromClientApi();
            if (chatBox != null)
            {
                EnsureChatReflection(chatBox);
                HideChatIfOpen(chatBox);
            }
        }

        private static object GetChatBoxFromClientApi()
        {
            try
            {
                HKMP.List.ListMod mod = HKMP.List.ListMod.Instance;
                if (mod == null ||
                    mod.ClientAddon == null ||
                    mod.ClientAddon.Api == null ||
                    mod.ClientAddon.Api.UiManager == null)
                {
                    return null;
                }

                return mod.ClientAddon.Api.UiManager.ChatBox;
            }
            catch
            {
                return null;
            }
        }

        private static void EnsureChatReflection(object chatBox)
        {
            if (chatBox == null)
            {
                return;
            }

            lock (Gate)
            {
                if (_hideChatInput != null && _isOpenField != null)
                {
                    return;
                }
            }

            Type chatBoxType = chatBox.GetType();
            MethodInfo hideMethod = chatBoxType.GetMethod(
                "HideChatInput",
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);

            FieldInfo isOpenField = chatBoxType.GetField(
                "_isOpen",
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic);

            lock (Gate)
            {
                if (_hideChatInput == null &&
                    hideMethod != null &&
                    !hideMethod.IsStatic &&
                    hideMethod.ReturnType == typeof(void))
                {
                    _hideChatInput = hideMethod;
                }

                if (_isOpenField == null &&
                    isOpenField != null &&
                    isOpenField.FieldType == typeof(bool))
                {
                    _isOpenField = isOpenField;
                }
            }
        }

        private static bool ShouldSuppressChatInput(
            object chatBox,
            object settings)
        {
            bool visible;

            lock (Gate)
            {
                _lastObservedSettings = settings;
                visible = _listVisible;
            }

            visible = visible || ListCursorHook.IsListVisible;
            if (!visible)
            {
                RestoreChatOpenBinding();
                return false;
            }

            DisableChatOpenBinding(settings);
            EnsureChatReflection(chatBox);
            HideChatIfOpen(chatBox);
            return true;
        }

        private static void DisableChatOpenBinding(object settings)
        {
            if (settings == null)
            {
                LogBindingFailure(
                    "HKMP ModSettings instance was unavailable while the list was open.");
                return;
            }

            object keybinds;
            if (!TryGetPropertyValue(settings, "Keybinds", out keybinds) ||
                keybinds == null)
            {
                LogBindingFailure(
                    "HKMP ModSettings.Keybinds could not be resolved.");
                return;
            }

            object chatOpenAction;
            if (!TryGetPropertyValue(keybinds, "OpenChat", out chatOpenAction) ||
                chatOpenAction == null)
            {
                LogBindingFailure(
                    "HKMP Keybinds.OpenChat could not be resolved.");
                return;
            }

            MemberInfo enabledMember =
                FindBooleanEnabledMember(chatOpenAction.GetType());

            if (enabledMember == null)
            {
                LogBindingFailure(
                    "HKMP Keybinds.OpenChat.Enabled could not be resolved.");
                return;
            }

            bool currentEnabled;
            if (!TryReadBooleanMember(
                    chatOpenAction,
                    enabledMember,
                    out currentEnabled))
            {
                LogBindingFailure(
                    "HKMP Keybinds.OpenChat.Enabled could not be read.");
                return;
            }

            object previousAction = null;
            MemberInfo previousEnabledMember = null;
            bool previousEnabled = false;
            bool restorePrevious = false;

            lock (Gate)
            {
                if (!_hasSavedChatOpenEnabled ||
                    !ReferenceEquals(_savedChatOpenAction, chatOpenAction) ||
                    !Equals(_savedChatOpenEnabledMember, enabledMember))
                {
                    restorePrevious = _hasSavedChatOpenEnabled;
                    previousAction = _savedChatOpenAction;
                    previousEnabledMember = _savedChatOpenEnabledMember;
                    previousEnabled = _savedChatOpenEnabled;

                    _savedChatOpenAction = chatOpenAction;
                    _savedChatOpenEnabledMember = enabledMember;
                    _savedChatOpenEnabled = currentEnabled;
                    _hasSavedChatOpenEnabled = true;
                }
            }

            if (restorePrevious &&
                previousAction != null &&
                previousEnabledMember != null)
            {
                TryWriteBooleanMember(
                    previousAction,
                    previousEnabledMember,
                    previousEnabled);
            }

            if (!TryWriteBooleanMember(
                    chatOpenAction,
                    enabledMember,
                    false))
            {
                LogBindingFailure(
                    "HKMP Keybinds.OpenChat.Enabled could not be disabled.");
            }
        }

        private static void RestoreChatOpenBinding()
        {
            object action;
            MemberInfo enabledMember;
            bool originalEnabled;

            lock (Gate)
            {
                if (!_hasSavedChatOpenEnabled)
                {
                    return;
                }

                action = _savedChatOpenAction;
                enabledMember = _savedChatOpenEnabledMember;
                originalEnabled = _savedChatOpenEnabled;

                _savedChatOpenAction = null;
                _savedChatOpenEnabledMember = null;
                _savedChatOpenEnabled = false;
                _hasSavedChatOpenEnabled = false;
            }

            if (action != null &&
                enabledMember != null &&
                !TryWriteBooleanMember(
                    action,
                    enabledMember,
                    originalEnabled))
            {
                LogBindingFailure(
                    "HKMP Keybinds.OpenChat.Enabled could not be restored.");
            }
        }

        private static bool TryGetPropertyValue(
            object target,
            string propertyName,
            out object value)
        {
            value = null;
            if (target == null)
            {
                return false;
            }

            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

                if (property == null ||
                    property.PropertyType == typeof(void))
                {
                    return false;
                }

                MethodInfo getter = property.GetGetMethod(true);
                if (getter == null || getter.IsStatic || getter.GetParameters().Length != 0)
                {
                    return false;
                }

                value = property.GetValue(target, null);
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private static MemberInfo FindBooleanEnabledMember(Type actionType)
        {
            if (actionType == null)
            {
                return null;
            }

            const BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            PropertyInfo property = actionType.GetProperty(
                "Enabled",
                flags);

            if (property != null && property.PropertyType == typeof(bool))
            {
                MethodInfo getter = property.GetGetMethod(true);
                MethodInfo setter = property.GetSetMethod(true);

                if (getter != null &&
                    setter != null &&
                    !getter.IsStatic &&
                    !setter.IsStatic &&
                    getter.GetParameters().Length == 0 &&
                    setter.GetParameters().Length == 1)
                {
                    return property;
                }
            }

            FieldInfo field = actionType.GetField(
                "Enabled",
                flags);

            if (field != null &&
                field.FieldType == typeof(bool) &&
                !field.IsStatic &&
                !field.IsInitOnly)
            {
                return field;
            }

            return null;
        }

        private static bool TryReadBooleanMember(
            object target,
            MemberInfo member,
            out bool value)
        {
            value = false;
            if (target == null || member == null)
            {
                return false;
            }

            try
            {
                PropertyInfo property = member as PropertyInfo;
                if (property != null)
                {
                    object result = property.GetValue(target, null);
                    if (!(result is bool))
                    {
                        return false;
                    }

                    value = (bool)result;
                    return true;
                }

                FieldInfo field = member as FieldInfo;
                if (field != null && field.FieldType == typeof(bool))
                {
                    object result = field.GetValue(target);
                    if (!(result is bool))
                    {
                        return false;
                    }

                    value = (bool)result;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryWriteBooleanMember(
            object target,
            MemberInfo member,
            bool value)
        {
            if (target == null || member == null)
            {
                return false;
            }

            try
            {
                PropertyInfo property = member as PropertyInfo;
                if (property != null && property.PropertyType == typeof(bool))
                {
                    property.SetValue(target, value, null);
                    return true;
                }

                FieldInfo field = member as FieldInfo;
                if (field != null &&
                    field.FieldType == typeof(bool) &&
                    !field.IsInitOnly)
                {
                    field.SetValue(target, value);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void LogBindingFailure(string message)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastBindingFailureLogUtc).TotalSeconds < 10d)
            {
                return;
            }

            _lastBindingFailureLogUtc = now;
            Debug.LogWarning("HKMP.List: " + message);
        }

        private static void HideChatIfOpen(object chatBox)
        {
            if (chatBox == null)
            {
                return;
            }

            EnsureChatReflection(chatBox);

            MethodInfo hideMethod;
            FieldInfo isOpenField;

            lock (Gate)
            {
                hideMethod = _hideChatInput;
                isOpenField = _isOpenField;
            }

            if (hideMethod == null)
            {
                return;
            }

            bool shouldHide = false;
            bool stateWasRead = false;

            if (isOpenField != null)
            {
                try
                {
                    object value = isOpenField.GetValue(chatBox);
                    if (value is bool)
                    {
                        stateWasRead = true;
                        shouldHide = (bool)value;
                    }
                }
                catch
                {
                    stateWasRead = false;
                }
            }

            if (!stateWasRead)
            {
                lock (Gate)
                {
                    if (!ReferenceEquals(_lastHiddenChatBox, chatBox))
                    {
                        _lastHiddenChatBox = chatBox;
                        shouldHide = true;
                    }
                }
            }

            if (!shouldHide)
            {
                return;
            }

            try
            {
                hideMethod.Invoke(chatBox, null);

                lock (Gate)
                {
                    _lastHiddenChatBox = chatBox;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "HKMP.List: could not close HKMP chat input while the list is open: " +
                    exception.GetType().Name +
                    ": " +
                    exception.Message);
            }
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
                "HKMP.List: could not install the HKMP chat hotkey guard: " +
                exception.GetType().Name +
                ": " +
                exception.Message);
        }
    }
}
