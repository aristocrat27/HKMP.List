using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Modding;
using Modding.Menu;
using Modding.Menu.Config;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
namespace HKMP.List.UI
{
    internal static class ListRoundsIntegration
    {
        private static Hook _settingsDataHook;
        private static Hook _nativeSettingsButtonHook;
        private static IMenuMod _roundsMenuMod;
        private static List<IMenuMod.MenuEntry> _entries =
            new List<IMenuMod.MenuEntry>();

        private static string _nativeSettingsButtonName;
        private static float _nextProbeTime;

        public static event Action AvailabilityChanged;

        public static bool IsAvailable
        {
            get
            {
                return _roundsMenuMod != null;
            }
        }

        public static IReadOnlyList<IMenuMod.MenuEntry> Entries
        {
            get { return _entries.AsReadOnly(); }
        }

        private delegate ContentArea AddMenuButtonOriginal(
            ContentArea content,
            string name,
            MenuButtonConfig config);

        private delegate ContentArea AddMenuButtonDetour(
            AddMenuButtonOriginal original,
            ContentArea content,
            string name,
            MenuButtonConfig config);



        public static void Pump()
        {
            if (Time.unscaledTime < _nextProbeTime)
            {
                return;
            }

            _nextProbeTime = Time.unscaledTime + (IsAvailable ? 1.5f : 2f);

            IMenuMod rounds;
            try
            {
                rounds = FindRoundsMenuMod();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "HKMP.List: could not locate HKMP.Rounds: " + exception.Message);
                return;
            }

            if (rounds == null)
            {
                if (_roundsMenuMod != null)
                {
                    ResetIntegration();
                    RaiseAvailabilityChanged();
                }
                return;
            }

            if (_roundsMenuMod != null && !ReferenceEquals(rounds, _roundsMenuMod))
            {
                ResetIntegration();
            }

            if (!ReferenceEquals(rounds, _roundsMenuMod) ||
                _entries == null || _entries.Count == 0)
            {
                TryAttach(rounds);
                return;
            }

            InstallMissingHooks(rounds);

            if (_nativeSettingsButtonHook == null)
            {
                HideExistingNativeSettingsButton();
            }
        }

        private static void TryAttach(IMenuMod rounds)
        {
            if (rounds == null)
            {
                return;
            }

            List<IMenuMod.MenuEntry> entries;
            try
            {
                entries = rounds.GetMenuData(null) ?? new List<IMenuMod.MenuEntry>();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "HKMP.List: GetMenuData failed for HKMP.Rounds: " + exception.Message);
                _nextProbeTime = Time.unscaledTime + 3f;
                return;
            }

            bool wasAvailable = IsAvailable;
            bool hadEntries = _entries != null && _entries.Count > 0;
            _roundsMenuMod = rounds;
            _entries = new List<IMenuMod.MenuEntry>(entries);
            _nativeSettingsButtonName = rounds.GetName() + "_Settings";

            InstallMissingHooks(rounds);
            HideExistingNativeSettingsButton();

            if (!wasAvailable || (!hadEntries && _entries.Count > 0))
            {
                RaiseAvailabilityChanged();
            }

            _nextProbeTime = Time.unscaledTime + (_entries.Count > 0 ? 1.5f : 2f);
        }

        private static void InstallMissingHooks(IMenuMod rounds)
        {
            if (rounds == null)
            {
                return;
            }

            if (_settingsDataHook == null && _entries != null && _entries.Count > 0)
            {
                try
                {
                    MethodInfo dataMethod = FindSettingsDataMethod(rounds.GetType());
                    if (dataMethod != null)
                    {
                        _settingsDataHook = CreateSettingsMenuHook(dataMethod);
                    }
                    else
                    {
                        Debug.LogWarning(
                            "HKMP.List: could not hook HKMP.Rounds.GetMenuData; native settings may remain visible, but the List tab is available.");
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "HKMP.List: could not suppress HKMP.Rounds menu data: " + exception.Message);
                }
            }

            if (_nativeSettingsButtonHook == null)
            {
                try
                {
                    _nativeSettingsButtonHook = CreateNativeSettingsButtonHook();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "HKMP.List: native HKMP.Rounds button hook unavailable; the existing row will be hidden when found: " + exception.Message);
                }
            }
        }

        private static void ResetIntegration()
        {
            Hook dataHook = _settingsDataHook;
            Hook buttonHook = _nativeSettingsButtonHook;

            _settingsDataHook = null;
            _nativeSettingsButtonHook = null;
            _roundsMenuMod = null;
            _entries = new List<IMenuMod.MenuEntry>();
            _nativeSettingsButtonName = null;
            _nextProbeTime = Time.unscaledTime + 2f;

            DisposeHook(dataHook);
            DisposeHook(buttonHook);
        }

        private static IMenuMod FindRoundsMenuMod()
        {
            const string expectedAssemblyName = "HKMP.Rounds";

            foreach (IMod mod in ModHooks.GetAllMods(true))
            {
                if (mod == null)
                    continue;

                try
                {
                    Assembly assembly = mod.GetType().Assembly;
                    AssemblyName assemblyName = assembly.GetName();

                    if (!string.Equals(
                            assemblyName.Name,
                            expectedAssemblyName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    IMenuMod menuMod = mod as IMenuMod;
                    if (menuMod != null)
                        return menuMod;

                    Debug.LogWarning(
                        "HKMP.List: found the HKMP.Rounds assembly, but its main mod type does not implement IMenuMod.");
                    return null;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "HKMP.List: failed to inspect a loaded mod while locating HKMP.Rounds: " +
                        exception.Message);
                }
            }

            return null;
        }

        private static MethodInfo FindSettingsDataMethod(Type type)
        {
            if (type == null || !typeof(IMenuMod).IsAssignableFrom(type))
            {
                return null;
            }

            Type entryType = typeof(IMenuMod.MenuEntry?);
            MethodInfo publicMethod = type.GetMethod(
                "GetMenuData",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { entryType },
                null);

            if (IsSupportedSettingsMethod(publicMethod, entryType))
            {
                return publicMethod;
            }

            try
            {
                InterfaceMapping map = type.GetInterfaceMap(typeof(IMenuMod));
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    MethodInfo interfaceMethod = map.InterfaceMethods[i];
                    if (interfaceMethod != null &&
                        string.Equals(interfaceMethod.Name, "GetMenuData", StringComparison.Ordinal))
                    {
                        MethodInfo target = map.TargetMethods[i];
                        if (IsSupportedSettingsMethod(target, entryType))
                        {
                            return target;
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool IsSupportedSettingsMethod(
            MethodInfo method,
            Type entryType)
        {
            if (method == null || method.IsStatic ||
                method.ReturnType != typeof(List<IMenuMod.MenuEntry>))
            {
                return false;
            }

            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 1 &&
                parameters[0].ParameterType == entryType;
        }

        private static Hook CreateSettingsMenuHook(MethodInfo method)
        {
            Type targetType = method.DeclaringType;
            Type entryType = method.GetParameters()[0].ParameterType;
            Type returnType = method.ReturnType;

            Type originalDelegateType = typeof(Func<,,>).MakeGenericType(
                targetType,
                entryType,
                returnType);

            Type detourDelegateType = typeof(Func<,,,>).MakeGenericType(
                originalDelegateType,
                targetType,
                entryType,
                returnType);

            DynamicMethod detourMethod = new DynamicMethod(
                "HKMPListSuppressRoundsNativeSettingsData",
                returnType,
                new[]
                {
                    originalDelegateType,
                    targetType,
                    entryType
                },
                typeof(ListRoundsIntegration).Module,
                true);

            ILGenerator il = detourMethod.GetILGenerator();
            ConstructorInfo listConstructor =
                returnType.GetConstructor(Type.EmptyTypes);

            if (listConstructor == null)
            {
                throw new InvalidOperationException(
                    "HKMP.Rounds menu list has no parameterless constructor.");
            }

            il.Emit(OpCodes.Newobj, listConstructor);
            il.Emit(OpCodes.Ret);

            Delegate detour =
                detourMethod.CreateDelegate(detourDelegateType);

            return new Hook(method, detour);
        }

        private static Hook CreateNativeSettingsButtonHook()
        {
            Type buttonContentType = FindMenuButtonContentType();
            if (buttonContentType == null)
            {
                throw new MissingMemberException(
                    "Modding.Menu.MenuButtonContent was not found in the loaded Modding API assemblies.");
            }

            MethodInfo target = null;
            MethodInfo[] methods = buttonContentType.GetMethods(
                BindingFlags.Public | BindingFlags.Static);

            foreach (MethodInfo method in methods)
            {
                if (!string.Equals(
                        method.Name,
                        "AddMenuButton",
                        StringComparison.Ordinal) ||
                    method.ReturnType != typeof(ContentArea))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 3 ||
                    parameters[0].ParameterType != typeof(ContentArea) ||
                    parameters[1].ParameterType != typeof(string) ||
                    parameters[2].ParameterType != typeof(MenuButtonConfig))
                {
                    continue;
                }

                target = method;
                break;
            }

            if (target == null)
            {
                throw new MissingMethodException(
                    "Modding.Menu.MenuButtonContent.AddMenuButton(ContentArea, string, MenuButtonConfig) was not found.");
            }

            AddMenuButtonDetour detour = SuppressNativeSettingsButton;
            return new Hook(target, detour);
        }

        private static Type FindMenuButtonContentType()
        {
            Type type = typeof(ContentArea).Assembly.GetType(
                "Modding.Menu.MenuButtonContent",
                false);

            if (type != null)
                return type;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly assembly in assemblies)
            {
                if (assembly == null)
                    continue;

                try
                {
                    type = assembly.GetType(
                        "Modding.Menu.MenuButtonContent",
                        false);

                    if (type != null)
                        return type;
                }
                catch
                {
                }
            }

            return null;
        }

        private static ContentArea SuppressNativeSettingsButton(
            AddMenuButtonOriginal original,
            ContentArea content,
            string name,
            MenuButtonConfig config)
        {
            if (IsNativeSettingsButtonName(name))
            {
                RectTransform contentRect =
                    content == null || content.ContentObject == null
                        ? null
                        : content.ContentObject.GetComponent<RectTransform>();

                if (contentRect != null)
                {
                    Vector2 size = contentRect.sizeDelta;
                    size.y = Mathf.Max(0f, size.y - 105f);
                    contentRect.sizeDelta = size;
                }

                return content;
            }

            return original(
                content,
                name,
                config);
        }


        private static void HideExistingNativeSettingsButton()
        {
            if (string.IsNullOrEmpty(_nativeSettingsButtonName))
                return;

            try
            {
                Transform[] transforms =
                    Resources.FindObjectsOfTypeAll<Transform>();

                foreach (Transform item in transforms)
                {
                    if (item == null ||
                        !IsNativeSettingsButtonName(item.name))
                    {
                        continue;
                    }

                    Transform menuRoot = FindModListRoot(item);
                    if (menuRoot == null || !item.gameObject.activeSelf)
                        continue;

                    Selectable selectable =
                        item.GetComponent<Selectable>();

                    Selectable replacement =
                        GetNavigationReplacement(item, selectable);

                    if (replacement == null)
                    {
                        replacement =
                            FindSelectableOutside(menuRoot, selectable);
                    }

                    RewireNavigation(selectable, replacement);
                    ReplaceAutoSelectorStart(
                        menuRoot,
                        selectable,
                        replacement);

                    Transform contentTransform = item.parent;
                    CompactNativeMenuLayout(item, 105f);

                    RectTransform contentRect =
                        contentTransform == null
                            ? null
                            : contentTransform.GetComponent<RectTransform>();

                    if (contentRect != null)
                    {
                        Vector2 size = contentRect.sizeDelta;
                        size.y = Mathf.Max(0f, size.y - 105f);
                        contentRect.sizeDelta = size;
                    }

                    item.gameObject.SetActive(false);

                    EventSystem eventSystem = EventSystem.current;
                    if (eventSystem != null &&
                        eventSystem.currentSelectedGameObject == item.gameObject)
                    {
                        eventSystem.SetSelectedGameObject(
                            replacement == null
                                ? null
                                : replacement.gameObject);
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "HKMP.List: could not hide an already-created HKMP.Rounds settings button: " +
                    exception.Message);
            }
        }
        private static bool IsNativeSettingsButtonName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (string.Equals(
                    name,
                    _nativeSettingsButtonName,
                    StringComparison.Ordinal))
            {
                return true;
            }

            IMenuMod rounds = _roundsMenuMod;
            if (rounds == null)
                return false;

            if (string.Equals(
                    name,
                    rounds.GetType().Name + "_Settings",
                    StringComparison.Ordinal))
            {
                return true;
            }

            string assemblyName = rounds.GetType().Assembly.GetName().Name;
            return !string.IsNullOrEmpty(assemblyName) &&
                string.Equals(
                    name,
                    assemblyName + "_Settings",
                    StringComparison.Ordinal);
        }
        private static void CompactNativeMenuLayout(
            Transform removedItem,
            float rowHeight)
        {
            if (removedItem == null || removedItem.parent == null)
                return;

            Transform parent = removedItem.parent;
            int removedIndex = removedItem.GetSiblingIndex();

            for (int i = removedIndex + 1; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child == null || !child.gameObject.activeSelf)
                    continue;

                RectTransform rect = child as RectTransform;
                if (rect == null)
                    rect = child.GetComponent<RectTransform>();

                if (rect == null)
                    continue;

                Vector2 position = rect.anchoredPosition;
                position.y += rowHeight;
                rect.anchoredPosition = position;
            }
        }

        private static Transform FindModListRoot(Transform item)
        {
            Transform current = item;

            while (current != null)
            {
                if (string.Equals(
                        current.name,
                        "ModListMenu",
                        StringComparison.Ordinal))
                {
                    return current;
                }

                current = current.parent;
            }

            return null;
        }

        private static Selectable GetNavigationReplacement(
            Transform item,
            Selectable selectable)
        {
            if (selectable == null)
                return null;

            Navigation navigation = selectable.navigation;

            if (navigation.selectOnDown != null &&
                navigation.selectOnDown != selectable &&
                navigation.selectOnDown.gameObject.activeInHierarchy)
            {
                return navigation.selectOnDown;
            }

            if (navigation.selectOnUp != null &&
                navigation.selectOnUp != selectable &&
                navigation.selectOnUp.gameObject.activeInHierarchy)
            {
                return navigation.selectOnUp;
            }

            Transform parent = item.parent;
            if (parent == null)
                return null;

            int index = item.GetSiblingIndex();

            for (int offset = 1; offset < parent.childCount; offset++)
            {
                int nextIndex = index + offset;
                if (nextIndex < parent.childCount)
                {
                    Selectable next =
                        parent.GetChild(nextIndex).GetComponent<Selectable>();

                    if (next != null && next.gameObject.activeInHierarchy)
                        return next;
                }
            }

            for (int offset = 1; offset < parent.childCount; offset++)
            {
                int previousIndex = index - offset;
                if (previousIndex >= 0)
                {
                    Selectable previous =
                        parent.GetChild(previousIndex).GetComponent<Selectable>();

                    if (previous != null && previous.gameObject.activeInHierarchy)
                        return previous;
                }
            }

            return null;
        }

        private static Selectable FindSelectableOutside(
            Transform menuRoot,
            Selectable excluded)
        {
            if (menuRoot == null)
                return null;

            Selectable[] selectables =
                menuRoot.GetComponentsInChildren<Selectable>(true);

            foreach (Selectable selectable in selectables)
            {
                if (selectable == null ||
                    selectable == excluded ||
                    !selectable.gameObject.activeSelf)
                {
                    continue;
                }

                return selectable;
            }

            return null;
        }

        private static void RewireNavigation(
            Selectable removed,
            Selectable replacement)
        {
            if (removed == null || replacement == null)
                return;

            Navigation removedNavigation = removed.navigation;
            Selectable up = removedNavigation.selectOnUp;
            Selectable down = removedNavigation.selectOnDown;

            if (up == removed)
                up = null;

            if (down == removed)
                down = null;

            if (up != null)
            {
                Navigation upNavigation = up.navigation;

                if (upNavigation.selectOnDown == removed)
                {
                    upNavigation.selectOnDown =
                        down != null
                            ? down
                            : replacement;
                    up.navigation = upNavigation;
                }
            }

            if (down != null)
            {
                Navigation downNavigation = down.navigation;

                if (downNavigation.selectOnUp == removed)
                {
                    downNavigation.selectOnUp =
                        up != null
                            ? up
                            : replacement;
                    down.navigation = downNavigation;
                }
            }
        }

        private static void ReplaceAutoSelectorStart(
            Transform menuRoot,
            Selectable removed,
            Selectable replacement)
        {
            if (menuRoot == null ||
                removed == null ||
                replacement == null)
            {
                return;
            }

            Component[] components =
                menuRoot.GetComponents<Component>();

            foreach (Component component in components)
            {
                if (component == null ||
                    !string.Equals(
                        component.GetType().Name,
                        "AutoSelector",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                Type componentType = component.GetType();

                FieldInfo startField = componentType.GetField(
                    "Start",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

                if (startField != null &&
                    typeof(Selectable).IsAssignableFrom(startField.FieldType) &&
                    ReferenceEquals(
                        startField.GetValue(component),
                        removed))
                {
                    startField.SetValue(
                        component,
                        replacement);
                    continue;
                }

                PropertyInfo startProperty = componentType.GetProperty(
                    "Start",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

                if (startProperty != null &&
                    startProperty.CanRead &&
                    startProperty.CanWrite &&
                    typeof(Selectable).IsAssignableFrom(startProperty.PropertyType) &&
                    ReferenceEquals(
                        startProperty.GetValue(component, null),
                        removed))
                {
                    startProperty.SetValue(
                        component,
                        replacement,
                        null);
                }
            }
        }

        private static void DisposeHook(Hook hook)
        {
            if (hook == null)
                return;

            try
            {
                hook.Dispose();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "HKMP.List: failed to remove a HKMP.Rounds integration hook: " +
                    exception.Message);
            }
        }

        public static void Dispose()
        {
            bool wasAvailable = IsAvailable;
            ResetIntegration();
            _nextProbeTime = 0f;

            if (wasAvailable)
            {
                RaiseAvailabilityChanged();
            }
        }

        private static void RaiseAvailabilityChanged()
        {
            Action changed = AvailabilityChanged;
            if (changed == null)
                return;

            try
            {
                changed();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "HKMP.List: a listener failed while updating HKMP.Rounds integration UI: " +
                    exception.Message);
            }
        }
    }
}
