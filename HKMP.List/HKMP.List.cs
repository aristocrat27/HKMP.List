using System;
using System.Collections.Generic;
using Modding;
using UnityEngine;
using HKMP.List.UI;
using Hkmp.Api.Server;
using HKMP.List.Client;
using HKMP.List.Server;
using HKMP.List.Shared;

namespace HKMP.List
{
    public enum ListSkinPreviewMode
    {
        Side,
        Bench
    }

    [Serializable]
    public sealed class ListGlobalSettings
    {
        private const int CurrentServerSettingsDefaultsVersion = 1;

        public int ServerSettingsDefaultsVersion = 0;

        public KeyCode ToggleKey = KeyCode.F7;

        public float WindowX = 0f;
        public float WindowY = 0f;
        public float WindowWidth = 760f;
        public float WindowHeight = 520f;

        public ListSkinPreviewMode SkinPreviewMode =
            ListSkinPreviewMode.Side;

        public byte PreferredSkinId = 0;

        public bool HasPreferredSkinId = false;

        public byte ListSkinOverrideId = 0;

        public bool HasListSkinOverride = false;

        public long SkinPreferenceUpdatedAtUtcTicks = 0;

        public Dictionary<string, long> PlayerSkinOverrideUpdatedAtUtcTicks =
            new Dictionary<string, long>();

        public ServerSettingsData SavedAdminSettings =
            ServerSettingsData.CreateDefault();

        public Dictionary<string, byte> PlayerSkinOverrides =
            new Dictionary<string, byte>();

        public void Normalize()
        {
            if (PreferredSkinId != 0)
            {
                HasPreferredSkinId = true;
            }

            if (ToggleKey == KeyCode.None)
            {
                ToggleKey = KeyCode.F7;
            }

            WindowWidth =
                Mathf.Clamp(
                    WindowWidth,
                    460f,
                    1400f);

            WindowHeight =
                Mathf.Clamp(
                    WindowHeight,
                    280f,
                    900f);

            if (SavedAdminSettings == null)
            {
                SavedAdminSettings =
                    ServerSettingsData.CreateDefault();
            }
            else if (ServerSettingsDefaultsVersion <
                         CurrentServerSettingsDefaultsVersion &&
                     IsLegacyDefaultServerSettings(
                         SavedAdminSettings))
            {
                SavedAdminSettings =
                    ServerSettingsData.CreateDefault();
            }
            else
            {
                SavedAdminSettings =
                    SavedAdminSettings.Clone();
            }

            ServerSettingsDefaultsVersion =
                CurrentServerSettingsDefaultsVersion;

            Dictionary<string, byte> normalizedOverrides =
                new Dictionary<string, byte>(
                    StringComparer.OrdinalIgnoreCase);

            if (PlayerSkinOverrides != null)
            {
                foreach (KeyValuePair<string, byte> pair
                         in PlayerSkinOverrides)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                    {
                        continue;
                    }

                    string key =
                        pair.Key.Trim().ToLowerInvariant();

                    if (key.Length > 256)
                    {
                        continue;
                    }

                    normalizedOverrides[key] = pair.Value;
                }
            }

            PlayerSkinOverrides =
                normalizedOverrides;

            Dictionary<string, long> normalizedOverrideTimes =
                new Dictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase);

            if (PlayerSkinOverrideUpdatedAtUtcTicks != null)
            {
                foreach (KeyValuePair<string, long> pair
                         in PlayerSkinOverrideUpdatedAtUtcTicks)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                    {
                        continue;
                    }

                    string key =
                        pair.Key.Trim().ToLowerInvariant();

                    if (key.Length <= 256 &&
                        normalizedOverrides.ContainsKey(key))
                    {
                        normalizedOverrideTimes[key] =
                            Math.Max(0, pair.Value);
                    }
                }
            }

            PlayerSkinOverrideUpdatedAtUtcTicks =
                normalizedOverrideTimes;

            SkinPreferenceUpdatedAtUtcTicks =
                Math.Max(
                    0,
                    SkinPreferenceUpdatedAtUtcTicks);
        }

        private static bool IsLegacyDefaultServerSettings(
            ServerSettingsData settings)
        {
            if (settings == null)
            {
                return false;
            }

            return
                !settings.IsPvpEnabled &&
                settings.IsBodyDamageEnabled &&
                !settings.AlwaysShowMapIcons &&
                settings.OnlyBroadcastMapIconWithWaywardCompass &&
                settings.DisplayNames &&
                !settings.TeamsEnabled &&
                settings.AllowSkins &&
                settings.AllowParries &&
                settings.NailDamage == 1 &&
                settings.GrubberflyElegyDamage == 1 &&
                settings.VengefulSpiritDamage == 1 &&
                settings.ShadeSoulDamage == 2 &&
                settings.DesolateDiveDamage == 1 &&
                settings.DescendingDarkDamage == 2 &&
                settings.HowlingWraithDamage == 1 &&
                settings.AbyssShriekDamage == 2 &&
                settings.GreatSlashDamage == 2 &&
                settings.DashSlashDamage == 2 &&
                settings.CycloneSlashDamage == 1 &&
                settings.SporeShroomDamage == 1 &&
                settings.SporeDungShroomDamage == 1 &&
                settings.ThornOfAgonyDamage == 1 &&
                settings.SharpShadowDamage == 1;
        }

        public void ResetWindow()
        {
            WindowX = 0f;
            WindowY = 0f;
            WindowWidth = 760f;
            WindowHeight = 520f;
        }
    }

    public sealed class ListMod :
        Mod,
        IGlobalSettings<ListGlobalSettings>
    {
        public static ListMod Instance { get; private set; }

        public static ListGlobalSettings GlobalSettings { get; set; }

        internal ListClientAddon ClientAddon { get; private set; }

        private GameObject _uiObject;

        public ListMod()
            : base("HKMP.List")
        {
            Instance = this;

            if (GlobalSettings == null)
            {
                GlobalSettings =
                    new ListGlobalSettings();
            }
        }

        public override string GetVersion()
        {
            return "1.0.0.0";
        }

        public void OnLoadGlobal(
            ListGlobalSettings settings)
        {
            GlobalSettings =
                settings ??
                new ListGlobalSettings();

            GlobalSettings.Normalize();
        }

        public ListGlobalSettings OnSaveGlobal()
        {
            if (GlobalSettings == null)
            {
                GlobalSettings =
                    new ListGlobalSettings();
            }

            GlobalSettings.Normalize();

            return GlobalSettings;
        }

        public override void Initialize(
            Dictionary<string, Dictionary<string, GameObject>> preloadedObjects)
        {
            ClientAddon =
                new ListClientAddon();

            Hkmp.Api.Client.ClientAddon.RegisterAddon(
                ClientAddon);

            ListServerAddon serverAddon =
                new ListServerAddon();

            ListSkinLockHook.Configure(
                serverAddon);

            ServerAddon.RegisterAddon(
                serverAddon);

            CreateUi();
            ListChatKeybindHook.Install();
        }

        private void CreateUi()
        {
            if (_uiObject != null)
            {
                return;
            }

            GameObject existing =
                GameObject.Find(
                    "HKMP.List.UI");

            if (existing != null)
            {
                _uiObject = existing;

                EnsureComponent<ListCursorHook>(
                    _uiObject);

                EnsureComponent<ListUiController>(
                    _uiObject);

                return;
            }

            _uiObject =
                new GameObject(
                    "HKMP.List.UI");

            UnityEngine.Object.DontDestroyOnLoad(
                _uiObject);

            EnsureComponent<ListCursorHook>(
                _uiObject);

            EnsureComponent<ListUiController>(
                _uiObject);
        }

        private static T EnsureComponent<T>(
            GameObject target)
            where T : Component
        {
            T component =
                target.GetComponent<T>();

            if (component == null)
            {
                component =
                    target.AddComponent<T>();
            }

            return component;
        }
    }
}