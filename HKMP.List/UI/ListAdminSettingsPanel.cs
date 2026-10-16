using HKMP.List.Client;
using HKMP.List.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HKMP.List.UI
{
    internal sealed class ListAdminSettingsPanel
    {
        private static readonly Color PanelColor =
            new Color(
                0.045f,
                0.045f,
                0.058f,
                1f);

        private static readonly Color TextColor =
            Color.white;

        private static readonly Color MutedColor =
            new Color(
                0.62f,
                0.62f,
                0.66f,
                1f);

        private static readonly Color AccentColor =
            new Color(
                0.78f,
                0.78f,
                0.84f,
                1f);

        private readonly ListClientAddon _addon;
        private readonly Action _onClose;

        private GameObject _rootObject;
        private RectTransform _root;
        private ScrollRect _scroll;
        private RectTransform _content;
        private Text _status;

        private bool _created;
        private bool _settingIdentityNoteProgrammatically;

        private readonly HashSet<ushort> _expandedPlayers =
            new HashSet<ushort>();

        private string _connectedPlayersSignature =
            string.Empty;

        private readonly object _bundleResponseGate = new object();
        private readonly object _settingsProposalGate = new object();
        private bool _settingsProposalsRefreshPending;
        private bool _showSettingsProposals;
        private ListClientState _observedState;
        private AdminIdentityBundleFilesData _pendingBundleResponse;
        private List<string> _identityBundleFiles = new List<string>();
        private string _identityBundleFilesMessage = string.Empty;

        private readonly Dictionary<string, InputField> _identityNoteFields =
            new Dictionary<string, InputField>(StringComparer.OrdinalIgnoreCase);

        private readonly List<Action> _refreshActions =
            new List<Action>();

        private sealed class DamageSetting
        {
            public string Name;
            public Func<ServerSettingsData, byte> Get;
            public Action<ServerSettingsData, byte> Set;
        }

        public ListAdminSettingsPanel(
            ListClientAddon addon,
            Action onClose)
        {
            _addon = addon;
            _onClose = onClose;

            if (_addon != null)
            {
                _addon.IdentityBundleFilesReceived +=
                    OnIdentityBundleFilesReceived;

            }
        }

        public bool IsVisible
        {
            get
            {
                return _rootObject != null &&
                       _rootObject.activeSelf;
            }
        }

        public void Show(
            Transform parent)
        {
            EnsureCreated(
                parent);

            if (_rootObject != null)
            {
                _rootObject.SetActive(
                    true);

                _rootObject.transform.SetAsLastSibling();
            }

            Canvas.ForceUpdateCanvases();

            Refresh();

            Canvas.ForceUpdateCanvases();

            if (_content != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(
                    _content);
            }
        }

        public void Hide()
        {

            if (_rootObject != null)
            {
                _rootObject.SetActive(
                    false);
            }
        }

        internal void PumpPendingNetworkResults()
        {
            EnsureSettingsProposalSubscription();
            AdminIdentityBundleFilesData pendingBundle;

            lock (_bundleResponseGate)
            {
                pendingBundle = _pendingBundleResponse;
                _pendingBundleResponse = null;
            }

            if (pendingBundle != null)
            {
                _identityBundleFiles =
                    (pendingBundle.FileNames ?? new List<string>())
                        .Where(name => AdminIdentityBundleActionData.IsSafeFileName(
                            name ?? string.Empty,
                            allowEmpty: false))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(ListProtocol.MaxIdentityBundleFiles)
                        .ToList();

                _identityBundleFilesMessage = pendingBundle.Message ?? string.Empty;
                UpdateStatus(_identityBundleFilesMessage);
            }

            bool refreshProposals;
            lock (_settingsProposalGate)
            {
                refreshProposals = _settingsProposalsRefreshPending;
                _settingsProposalsRefreshPending = false;
            }

            if (_created && IsVisible &&
                (pendingBundle != null || refreshProposals))
            {
                Refresh();
            }
        }

        private void OnSettingsProposalsChanged(
            IReadOnlyList<ServerSettingsProposalData> proposals)
        {
            lock (_settingsProposalGate)
            {
                _settingsProposalsRefreshPending = true;
            }
        }

        private void EnsureSettingsProposalSubscription()
        {
            ListClientState current = _addon == null ? null : _addon.State;
            if (ReferenceEquals(current, _observedState))
            {
                return;
            }

            if (_observedState != null)
            {
                _observedState.SettingsProposalsChanged -= OnSettingsProposalsChanged;
            }

            _observedState = current;
            if (_observedState != null)
            {
                _observedState.SettingsProposalsChanged += OnSettingsProposalsChanged;
            }
        }

        private void OnIdentityBundleFilesReceived(
            AdminIdentityBundleFilesData data)
        {
            if (data == null)
            {
                return;
            }

            AdminIdentityBundleFilesData copy =
                new AdminIdentityBundleFilesData
                {
                    Success = data.Success,
                    Message = data.Message,
                    FileNames = (data.FileNames ?? new List<string>()).ToList()
                };

            lock (_bundleResponseGate)
            {
                _pendingBundleResponse = copy;
            }
        }

        public void Refresh()
        {
            EnsureSettingsProposalSubscription();

            if (!_created || _content == null)
            {
                return;
            }

            ClearContent();

            bool connected = IsConnected();
            bool authorized =
                _addon != null &&
                _addon.State != null &&
                (_addon.State.IsAuthorized || _addon.State.IsHost);

            if (connected && !authorized)
            {
                CreateTextBlock(
                    "Admin settings are hidden while connected to a server where you are not an administrator.",
                    16,
                    TextAnchor.MiddleCenter,
                    MutedColor,
                    0f,
                    0f,
                    0f);

                UpdateStatus(string.Empty);
                return;
            }

            CreateHeader();

            if (connected && authorized)
            {
                CreateAdministratorsSection();
                CreateIdentityBundleSection();

                if (_addon.State.IsHost)
                {
                    CreateSettingsProposalsSection();
                }
            }
            else
            {
                CreateTextBlock(
                    "These settings are saved locally and will be applied when you host. While connected to another host, only an authorized admin with permission can submit them as a suggestion.",
                    13,
                    TextAnchor.UpperLeft,
                    MutedColor,
                    0f,
                    0f,
                    0f);
            }

            CreateSectionHeader("SERVER SETTINGS");

            if (!connected || (_addon != null && _addon.State != null && _addon.State.IsHost))
            {
                Button resetServerSettings =
                    ListUiFactory.CreateButton(
                        _content,
                        "ResetServerSettings",
                        "Reset server settings to defaults",
                        delegate
                        {
                            if (!CanChangeSettings())
                                return;

                            SubmitSettings(ServerSettingsData.CreateDefault());
                            Refresh();
                        },
                        13);
                LayoutElement resetLayout =
                    resetServerSettings.gameObject.AddComponent<LayoutElement>();
                resetLayout.preferredHeight = 36f;
                resetLayout.minHeight = 36f;
                resetServerSettings.interactable = CanChangeSettings();
            }

            CreateToggle(
                "PvP",
                settings => settings.IsPvpEnabled,
                (settings, value) => settings.IsPvpEnabled = value);

            CreateToggle(
                "Body damage",
                settings => settings.IsBodyDamageEnabled,
                (settings, value) => settings.IsBodyDamageEnabled = value);

            CreateToggle(
                "Always show map icons",
                settings => settings.AlwaysShowMapIcons,
                (settings, value) => settings.AlwaysShowMapIcons = value);

            CreateToggle(
                "Map icon with Wayward Compass only",
                settings => settings.OnlyBroadcastMapIconWithWaywardCompass,
                (settings, value) => settings.OnlyBroadcastMapIconWithWaywardCompass = value);

            CreateToggle(
                "Display names",
                settings => settings.DisplayNames,
                (settings, value) => settings.DisplayNames = value);

            CreateToggle(
                "Teams",
                settings => settings.TeamsEnabled,
                (settings, value) => settings.TeamsEnabled = value);

            CreateToggle(
                "Skins",
                settings => settings.AllowSkins,
                (settings, value) => settings.AllowSkins = value);

            CreateToggle(
                "Parries",
                settings => settings.AllowParries,
                (settings, value) => settings.AllowParries = value);

            CreateSectionHeader(
                "DAMAGE");

            foreach (DamageSetting setting in GetDamageSettings())
            {
                CreateDamageRow(
                    setting);
            }

            if (!connected || (_addon != null && _addon.State != null && _addon.State.IsHost))
            {
                Button resetDamage =
                    ListUiFactory.CreateButton(
                        _content,
                        "ResetDamageSettings",
                        "Reset damage values to defaults",
                        ResetDamageSettings,
                        13);

                LayoutElement resetDamageLayout =
                    resetDamage.gameObject.AddComponent<LayoutElement>();
                resetDamageLayout.preferredHeight = 36f;
                resetDamageLayout.minHeight = 36f;
                resetDamage.interactable = CanChangeSettings();
            }

            if (connected && authorized)
            {
                CreateSectionHeader("BANNED IDENTITIES");

                IReadOnlyList<AdminBanEntryView> bans =
                    _addon.State.GetAdminBans();

                Button unbanAll =
                    ListUiFactory.CreateButton(
                        _content,
                        "UnbanAll",
                        "Unban all",
                        delegate
                        {
                            try
                            {
                                _addon.UnbanAllPlayers();
                            }
                            catch (Exception ex)
                            {
                                UpdateStatus(
                                    "Could not send unban-all request: " + ex.Message);
                            }
                        },
                        14);

                LayoutElement unbanAllLayout =
                    unbanAll.gameObject.AddComponent<LayoutElement>();
                unbanAllLayout.preferredWidth = 140f;
                unbanAllLayout.minWidth = 120f;
                unbanAll.interactable =
                    bans != null && bans.Count > 0 &&
                    _addon.State.Permissions.CanManageBans;

                if (bans == null || bans.Count == 0)
                {
                    CreateTextBlock(
                        "No banned identities.",
                        15,
                        TextAnchor.MiddleLeft,
                        MutedColor,
                        0f,
                        0f,
                        0f);
                }
                else
                {
                    foreach (AdminBanEntryView ban in bans)
                    {
                        if (ban != null)
                        {
                            CreateBanRow(ban);
                        }
                    }
                }

                CreateTextBlock(
                    "Bans are bound to the authorization fingerprint, not the current username.",
                    13,
                    TextAnchor.UpperLeft,
                    MutedColor,
                    0f,
                    0f,
                    0f);
            }

            if (!connected)
            {
                UpdateStatus("Saved locally; changes will be applied when you host.");
            }
            else if (_addon.State.IsHost)
            {
                UpdateStatus("Host settings are applied immediately.");
            }
            else
            {
                UpdateStatus("Settings are sent to the host for approval.");
            }
        }

        private void EnsureCreated(
            Transform parent)
        {
            if (_created)
            {
                return;
            }

            if (parent == null)
            {
                return;
            }

            _rootObject =
                ListUiFactory.CreateObject(
                    parent,
                    "AdminSettings");

            _root =
                _rootObject.GetComponent<
                    RectTransform>();

            _root.anchorMin =
                Vector2.zero;

            _root.anchorMax =
                Vector2.one;

            _root.offsetMin =
                new Vector2(
                    0f,
                    0f);

            _root.offsetMax =
                new Vector2(
                    0f,
                    -48f);

            Image background =
                _rootObject.AddComponent<Image>();

            background.sprite =
                ListUiFactory.WhiteSprite;

            background.color =
                PanelColor;

            background.raycastTarget =
                true;

            GameObject scrollObject =
                ListUiFactory.CreateObject(
                    _root,
                    "Scroll");

            RectTransform scrollRect =
                scrollObject.GetComponent<
                    RectTransform>();

            scrollRect.anchorMin =
                new Vector2(
                    0f,
                    0f);

            scrollRect.anchorMax =
                new Vector2(
                    1f,
                    1f);

            scrollRect.offsetMin =
                new Vector2(
                    10f,
                    44f);

            scrollRect.offsetMax =
                new Vector2(
                    -10f,
                    -8f);

            Image viewportImage =
                scrollObject.AddComponent<Image>();

            viewportImage.sprite =
                ListUiFactory.WhiteSprite;

            viewportImage.color =
                Color.clear;

            viewportImage.raycastTarget =
                true;

            RectMask2D mask =
                scrollObject.AddComponent<RectMask2D>();

            _scroll =
                scrollObject.AddComponent<
                    ScrollRect>();

            _scroll.horizontal =
                false;

            _scroll.vertical =
                true;

            _scroll.movementType =
                ScrollRect.MovementType.Clamped;

            _scroll.scrollSensitivity =
                35f;

            GameObject contentObject =
                ListUiFactory.CreateObject(
                    scrollObject.transform,
                    "Content");

            _content =
                contentObject.GetComponent<
                    RectTransform>();

            _content.anchorMin =
                new Vector2(
                    0f,
                    1f);

            _content.anchorMax =
                new Vector2(
                    1f,
                    1f);

            _content.pivot =
                new Vector2(
                    0.5f,
                    1f);

            _content.sizeDelta =
                new Vector2(
                    0f,
                    0f);

            VerticalLayoutGroup layout =
                contentObject.AddComponent<
                    VerticalLayoutGroup>();

            layout.childAlignment =
                TextAnchor.UpperCenter;

            layout.childControlWidth =
                true;

            layout.childControlHeight =
                true;

            layout.childForceExpandWidth =
                true;

            layout.childForceExpandHeight =
                false;

            layout.spacing =
                3f;

            layout.padding =
                new RectOffset(
                    2,
                    2,
                    2,
                    2);

            ContentSizeFitter fitter =
                contentObject.AddComponent<
                    ContentSizeFitter>();

            fitter.horizontalFit =
                ContentSizeFitter.FitMode.Unconstrained;

            fitter.verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            _scroll.viewport =
                scrollRect;

            _scroll.content =
                _content;

            _status =
                ListUiFactory.CreateText(
                    _root,
                    "Status",
                    "",
                    13,
                    MutedColor,
                    TextAnchor.MiddleLeft);

            RectTransform statusRect =
                _status.rectTransform;

            statusRect.anchorMin =
                new Vector2(
                    0f,
                    0f);

            statusRect.anchorMax =
                new Vector2(
                    1f,
                    0f);

            statusRect.pivot =
                new Vector2(
                    0.5f,
                    0f);

            statusRect.sizeDelta =
                new Vector2(
                    0f,
                    26f);

            statusRect.anchoredPosition =
                new Vector2(
                    0f,
                    6f);

            _created = true;
        }

        private void ClearContent()
        {
            _identityNoteFields.Clear();

            if (_content == null)
            {
                return;
            }

            List<GameObject> children =
                new List<GameObject>();

            for (int i = 0;
                 i < _content.childCount;
                 i++)
            {
                children.Add(
                    _content.GetChild(i)
                        .gameObject);
            }

            foreach (GameObject child in children)
            {
                if (child != null)
                {
                    UnityEngine.Object.Destroy(
                        child);
                }
            }

        }

        internal void RefreshAdminPlayersOnly()
        {
            if (!_created ||
                !_rootObject.activeSelf ||
                _addon == null ||
                _addon.State == null ||
                (!_addon.State.IsAuthorized && !_addon.State.IsHost))
            {
                return;
            }

            IReadOnlyList<ListAdminPlayerView> players =
                _addon.State.GetAdminPlayers();

            string signature =
                BuildConnectedPlayersSignature(
                    players);

            if (!string.Equals(
                    signature,
                    _connectedPlayersSignature,
                    StringComparison.Ordinal))
            {
                Refresh();
                return;
            }

            if (players == null)
            {
                return;
            }
            foreach (ListAdminPlayerView player in players)
            {
                if (player == null ||
                    string.IsNullOrWhiteSpace(player.AuthorizationFingerprint))
                {
                    continue;
                }

                InputField field;
                if (!_identityNoteFields.TryGetValue(
                        player.AuthorizationFingerprint,
                        out field) ||
                    field == null ||
                    IsInputBeingEdited(field))
                {
                    continue;
                }

                string note = player.AuthorizationNote ?? string.Empty;
                if (!string.Equals(
                        field.text,
                        note,
                        StringComparison.Ordinal))
                {
                    _settingIdentityNoteProgrammatically = true;
                    try
                    {
                        field.text = note;
                    }
                    finally
                    {
                        _settingIdentityNoteProgrammatically = false;
                    }
                }
            }
        }

        private static bool IsInputBeingEdited(
            InputField field)
        {
            if (field == null)
            {
                return false;
            }

            if (field.isFocused)
            {
                return true;
            }

            EventSystem eventSystem =
                EventSystem.current;

            if (eventSystem == null)
            {
                return false;
            }

            GameObject selected =
                eventSystem.currentSelectedGameObject;

            if (selected == null)
            {
                return false;
            }

            Transform selectedTransform =
                selected.transform;

            Transform fieldTransform =
                field.transform;

            return
                selectedTransform == fieldTransform ||
                selectedTransform.IsChildOf(fieldTransform);
        }

        internal void SetStatus(
            string value)
        {
            UpdateStatus(
                value ?? string.Empty);
        }

        private void CreateSettingsProposalsSection()
        {
            if (_addon == null || _addon.State == null || !_addon.State.IsHost)
            {
                return;
            }

            IReadOnlyList<ServerSettingsProposalData> proposals =
                _addon.State.GetSettingsProposals();

            int count = proposals == null ? 0 : proposals.Count;
            GameObject headerRow = ListUiFactory.CreateObject(_content, "SettingsProposalsHeader");
            ListUiFactory.AddLayoutElement(headerRow, 0f, 38f);

            Button header = ListUiFactory.CreateButton(
                headerRow.transform,
                "Toggle",
                "ADMIN SETTINGS SUGGESTIONS (" + count + ") " + (_showSettingsProposals ? "−" : "+"),
                delegate
                {
                    _showSettingsProposals = !_showSettingsProposals;
                    Refresh();
                },
                14);
            ListUiFactory.SetFullStretch(header.GetComponent<RectTransform>(), 0f, 0f, 0f, 0f);

            if (!_showSettingsProposals)
            {
                return;
            }

            if (count == 0)
            {
                CreateTextBlock(
                    "No pending administrator suggestions.",
                    13,
                    TextAnchor.MiddleLeft,
                    MutedColor,
                    0f,
                    0f,
                    0f);
                return;
            }

            foreach (ServerSettingsProposalData proposal in proposals)
            {
                if (proposal == null || proposal.Settings == null)
                {
                    continue;
                }

                CreateSettingsProposalRow(proposal);
            }
        }

        private void CreateSettingsProposalRow(
            ServerSettingsProposalData proposal)
        {
            GameObject row = ListUiFactory.CreateObject(
                _content,
                "SettingsProposal_" + SanitizeId(proposal.ProposalId));
            Image background = row.AddComponent<Image>();
            background.sprite = ListUiFactory.WhiteSprite;
            background.color = new Color(0.075f, 0.075f, 0.09f, 1f);
            background.raycastTarget = false;
            ListUiFactory.AddLayoutElement(row, 0f, 82f);

            string submitted = "";
            try
            {
                if (proposal.SubmittedUtcTicks > 0 && proposal.SubmittedUtcTicks <= (ulong)DateTime.MaxValue.Ticks)
                {
                    submitted = new DateTime((long)proposal.SubmittedUtcTicks, DateTimeKind.Utc)
                        .ToLocalTime().ToString("HH:mm:ss");
                }
            }
            catch
            {
            }

            ServerSettingsData settings = proposal.Settings;
            string summary =
                "PvP " + OnOff(settings.IsPvpEnabled) +
                " · Body " + OnOff(settings.IsBodyDamageEnabled) +
                " · Skins " + OnOff(settings.AllowSkins) +
                " · Teams " + OnOff(settings.TeamsEnabled) +
                " · Names " + OnOff(settings.DisplayNames) +
                "\n" +
                "Nail " + settings.NailDamage +
                " · VS " + settings.VengefulSpiritDamage +
                " · Shade Soul " + settings.ShadeSoulDamage +
                " · Descending Dark " + settings.DescendingDarkDamage;

            Text details = ListUiFactory.CreateText(
                row.transform,
                "Details",
                "From " + (string.IsNullOrWhiteSpace(proposal.SubmitterUsername) ? "administrator" : proposal.SubmitterUsername) +
                (string.IsNullOrEmpty(submitted) ? "" : " · " + submitted) + "\n" + summary,
                12,
                TextColor,
                TextAnchor.MiddleLeft);

            RectTransform detailsRect = details.rectTransform;
            detailsRect.anchorMin = new Vector2(0f, 0f);
            detailsRect.anchorMax = new Vector2(1f, 1f);
            detailsRect.offsetMin = new Vector2(8f, 4f);
            detailsRect.offsetMax = new Vector2(-84f, -4f);

            Button approve = ListUiFactory.CreateButton(
                row.transform,
                "Approve",
                "✓",
                delegate
                {
                    try
                    {
                        _addon.RespondToSettingsProposal(proposal.ProposalId, true);
                        UpdateStatus("Approval sent to server.");
                    }
                    catch (Exception ex)
                    {
                        UpdateStatus("Could not approve suggestion: " + ex.Message);
                    }
                },
                18);

            ConfigureProposalActionButton(approve, 36f, 44f);
            Image approveImage = approve.GetComponent<Image>();
            if (approveImage != null) approveImage.color = new Color(0.12f, 0.42f, 0.20f, 1f);
            ColorBlock approveColors = approve.colors;
            approveColors.normalColor = new Color(0.12f, 0.42f, 0.20f, 1f);
            approveColors.highlightedColor = new Color(0.18f, 0.58f, 0.27f, 1f);
            approve.colors = approveColors;

            Button reject = ListUiFactory.CreateButton(
                row.transform,
                "Reject",
                "×",
                delegate
                {
                    try
                    {
                        _addon.RespondToSettingsProposal(proposal.ProposalId, false);
                        UpdateStatus("Rejection sent to server.");
                    }
                    catch (Exception ex)
                    {
                        UpdateStatus("Could not reject suggestion: " + ex.Message);
                    }
                },
                18);

            ConfigureProposalActionButton(reject, 36f, 4f);
            Image rejectImage = reject.GetComponent<Image>();
            if (rejectImage != null) rejectImage.color = new Color(0.46f, 0.12f, 0.14f, 1f);
            ColorBlock rejectColors = reject.colors;
            rejectColors.normalColor = new Color(0.46f, 0.12f, 0.14f, 1f);
            rejectColors.highlightedColor = new Color(0.64f, 0.18f, 0.20f, 1f);
            reject.colors = rejectColors;
        }

        private static void ConfigureProposalActionButton(
            Button button,
            float size,
            float rightOffset)
        {
            RectTransform rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(size, size);
            rect.anchoredPosition = new Vector2(-rightOffset, 0f);

            Text label = button.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.fontSize =
                    Mathf.RoundToInt(
                        Mathf.Clamp(size * 0.55f, 10f, 20f));
                label.alignment = TextAnchor.MiddleCenter;
                label.resizeTextForBestFit = false;
            }
        }

        private bool IsConnected()
        {
            return _addon != null && _addon.IsConnected;
        }

        private bool CanChangeSettings()
        {
            if (!IsConnected())
            {
                return true;
            }

            return _addon.State != null &&
                   (_addon.State.IsAuthorized || _addon.State.IsHost) &&
                   (_addon.State.IsHost ||
                    (_addon.State.Permissions != null &&
                     _addon.State.Permissions.CanChangeServerSettings));
        }

        private ServerSettingsData GetEditableSettings()
        {
            if (_addon != null && _addon.IsConnected && _addon.State != null)
            {
                ServerSettingsData currentServerSettings =
                    _addon.State.GetServerSettings();

                if (currentServerSettings != null)
                {
                    return currentServerSettings.Clone();
                }
            }

            if (_addon != null)
            {
                return _addon.GetSavedServerSettings();
            }

            if (HKMP.List.ListMod.GlobalSettings == null)
            {
                HKMP.List.ListMod.GlobalSettings = new HKMP.List.ListGlobalSettings();
            }

            if (HKMP.List.ListMod.GlobalSettings.SavedAdminSettings == null)
            {
                HKMP.List.ListMod.GlobalSettings.SavedAdminSettings = ServerSettingsData.CreateDefault();
            }

            return HKMP.List.ListMod.GlobalSettings.SavedAdminSettings.Clone();
        }

        private void SubmitSettings(ServerSettingsData settings)
        {
            if (settings == null)
            {
                return;
            }

            if (_addon != null)
            {
                _addon.ApplyServerSettings(settings);
            }
            else
            {
                if (HKMP.List.ListMod.GlobalSettings == null)
                {
                    HKMP.List.ListMod.GlobalSettings = new HKMP.List.ListGlobalSettings();
                }

                HKMP.List.ListMod.GlobalSettings.SavedAdminSettings = settings.Clone();
            }

            if (!IsConnected())
            {
                UpdateStatus("Saved locally; changes will be applied when you host.");
            }
            else if (_addon.State != null && _addon.State.IsHost)
            {
                UpdateStatus("Settings sent to your hosted server.");
            }
            else
            {
                UpdateStatus("Settings suggestion sent to the host for approval.");
            }
        }

        private static string OnOff(bool enabled)
        {
            return enabled ? "ON" : "OFF";
        }

        private void CreateHeader()
        {
            GameObject row =
                ListUiFactory.CreateObject(
                    _content,
                    "Header");

            ListUiFactory.AddLayoutElement(
                row,
                0f,
                29f);

            Text title =
                ListUiFactory.CreateText(
                    row.transform,
                    "Title",
                    IsConnected() ? "ADMIN CONTROLS" : "SAVED HOST SETTINGS",
                    20,
                    TextColor,
                    TextAnchor.MiddleLeft);

            title.fontStyle =
                FontStyle.Bold;

            ListUiFactory.SetFullStretch(
                title.rectTransform,
                0f,
                0f,
                0f,
                0f);
        }

        private void CreateSectionHeader(
            string title)
        {
            GameObject row =
                ListUiFactory.CreateObject(
                    _content,
                    "Section_" +
                    title);

            ListUiFactory.AddLayoutElement(
                row,
                0f,
                24f);

            Text label =
                ListUiFactory.CreateText(
                    row.transform,
                    "Text",
                    title,
                    16,
                    AccentColor,
                    TextAnchor.MiddleLeft);

            ListUiFactory.SetFullStretch(
                label.rectTransform,
                4f,
                0f,
                4f,
                0f);
        }

        private void CreateAdministratorsSection()
        {
            CreateSectionHeader(
                "ADMINISTRATORS");

            IReadOnlyList<ListAdminPlayerView> allPlayers =
                _addon.State.GetAdminPlayers();

            List<ListAdminPlayerView> administrators =
                (allPlayers ?? new List<ListAdminPlayerView>())
                    .Where(player => player != null && player.IsAuthorized)
                    .OrderBy(player => player.Username, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(player => player.Id)
                    .ToList();

            if (administrators.Count == 0)
            {
                CreateTextBlock(
                    "No connected administrators.",
                    15,
                    TextAnchor.MiddleLeft,
                    MutedColor,
                    0f,
                    0f,
                    0f);
            }
            else
            {
                foreach (ListAdminPlayerView player in administrators)
                {
                    CreateAdministratorRow(player);
                }
            }

            _connectedPlayersSignature =
                BuildConnectedPlayersSignature(administrators);
        }

        private void CreateAdministratorRow(
            ListAdminPlayerView player)
        {
            bool expanded =
                _expandedPlayers.Contains(player.Id);

            float height =
                expanded
                    ? (player.IsHost ? 245f : 415f)
                    : 48f;

            GameObject row =
                ListUiFactory.CreateObject(
                    _content,
                    "Administrator_" + player.Id);

            ListUiFactory.AddLayoutElement(
                row,
                0f,
                height);

            Image background =
                row.AddComponent<Image>();

            background.sprite =
                ListUiFactory.WhiteSprite;

            background.color =
                new Color(0.075f, 0.075f, 0.090f, 1f);

            background.raycastTarget =
                false;

            string username =
                string.IsNullOrWhiteSpace(player.Username)
                    ? "Unknown administrator"
                    : player.Username;

            Text identity =
                ListUiFactory.CreateText(
                    row.transform,
                    "Identity",
                    username + "  ·  ID " + player.Id + "  ·  ADMIN",
                    15,
                    TextColor,
                    TextAnchor.MiddleLeft);

            RectTransform identityRect =
                identity.rectTransform;

            identityRect.anchorMin =
                new Vector2(0f, 1f);

            identityRect.anchorMax =
                new Vector2(1f, 1f);

            identityRect.pivot =
                new Vector2(0f, 1f);

            identityRect.offsetMin =
                new Vector2(8f, -42f);

            identityRect.offsetMax =
                new Vector2(-48f, -6f);

            CreateSmallActionButton(
                row.transform,
                "Expand",
                expanded ? "▲" : "▼",
                -16f,
                delegate
                {
                    ToggleExpandedPlayer(player.Id);
                });

            if (!expanded)
            {
                return;
            }

            CreateDetailText(
                row.transform,
                "Permission",
                player.IsAdminEnabled
                    ? "Permission: HKMP authorized · HKMP.List administrator enabled"
                    : "Permission: HKMP authorized · HKMP.List administrator access revoked",
                12,
                AccentColor,
                8f,
                -44f,
                360f,
                18f);

            CreateDetailText(
                row.transform,
                "FingerprintLabel",
                "authorized.json identity fingerprint",
                11,
                MutedColor,
                8f,
                -63f,
                360f,
                16f);

            CreateDetailText(
                row.transform,
                "FingerprintValue",
                string.IsNullOrWhiteSpace(player.AuthorizationFingerprint)
                    ? "Unavailable"
                    : player.AuthorizationFingerprint,
                11,
                AccentColor,
                8f,
                -79f,
                360f,
                18f);

            Button copyKey =
                ListUiFactory.CreateButton(
                    row.transform,
                    "CopyAuthorizationKey",
                    "Copy authorized.json key",
                    delegate
                    {
                        if (string.IsNullOrWhiteSpace(player.AuthorizationKey))
                        {
                            UpdateStatus(
                                "This administrator has no authorization key available to copy.");
                            return;
                        }

                        try
                        {
                            GUIUtility.systemCopyBuffer = player.AuthorizationKey;
                            UpdateStatus(
                                string.Equals(
                                    GUIUtility.systemCopyBuffer,
                                    player.AuthorizationKey,
                                    StringComparison.Ordinal)
                                    ? "authorized.json authorization key copied to clipboard."
                                    : "The clipboard did not accept the authorization key.");
                        }
                        catch (Exception ex)
                        {
                            UpdateStatus(
                                "Could not copy the authorization key: " + ex.Message);
                        }
                    },
                    11);

            RectTransform copyKeyRect =
                copyKey.GetComponent<RectTransform>();

            copyKeyRect.anchorMin =
                new Vector2(0f, 1f);

            copyKeyRect.anchorMax =
                new Vector2(0f, 1f);

            copyKeyRect.pivot =
                new Vector2(0f, 1f);

            copyKeyRect.sizeDelta =
                new Vector2(GetResponsiveControlButtonWidth(), 22f);

            copyKeyRect.anchoredPosition =
                new Vector2(8f, -99f);

            ConfigureSingleLineButtonText(
                copyKey,
                8,
                11);

            copyKey.interactable =
                !string.IsNullOrWhiteSpace(player.AuthorizationKey);

            bool canEditIdentityNotes =
                _addon.State.Permissions.CanManageIdentityData &&
                !string.IsNullOrWhiteSpace(player.AuthorizationFingerprint);

            CreateDetailText(
                row.transform,
                "IdentityNoteLabel",
                "ADMIN NOTE FOR THIS authorized.json IDENTITY",
                10,
                MutedColor,
                8f,
                -124f,
                GetResponsiveControlButtonWidth(),
                16f);

            InputField identityNote =
                ListUiFactory.CreateInputField(
                    row.transform,
                    "IdentityNote",
                    player.AuthorizationNote ?? string.Empty,
                    "Note about this administrator identity...",
                    12,
                    value =>
                        OnIdentityNoteChanged(
                            player.AuthorizationFingerprint,
                            value));

            RectTransform identityNoteRect =
                identityNote.GetComponent<RectTransform>();

            identityNoteRect.anchorMin =
                new Vector2(0f, 1f);

            identityNoteRect.anchorMax =
                new Vector2(0f, 1f);

            identityNoteRect.pivot =
                new Vector2(0f, 1f);

            identityNoteRect.sizeDelta =
                new Vector2(
                    GetResponsiveControlButtonWidth(),
                    34f);

            identityNoteRect.anchoredPosition =
                new Vector2(8f, -143f);

            identityNote.interactable =
                canEditIdentityNotes;

            if (!string.IsNullOrWhiteSpace(player.AuthorizationFingerprint))
            {
                _identityNoteFields[player.AuthorizationFingerprint] =
                    identityNote;
            }

            ushort localPlayerId;
            bool hasLocalPlayerId = _addon.State.TryGetLocalPlayerId(out localPlayerId);
            bool editingSelf = hasLocalPlayerId && localPlayerId == player.Id;
            AdminPermissions localPermissions = _addon.State.Permissions;
            bool canManagePermissions = _addon.State.IsHost ||
                (localPermissions != null && localPermissions.CanManageAdministrators);
            bool canEditPermissions =
                canManagePermissions &&
                !editingSelf &&
                !player.IsHost &&
                !string.IsNullOrWhiteSpace(player.AuthorizationFingerprint);

            if (player.IsHost)
            {
                CreateDetailText(
                    row.transform,
                    "RoleHeader",
                    "HOST ROLE — LOCKED",
                    10,
                    AccentColor,
                    8f,
                    -181f,
                    GetResponsiveControlButtonWidth(),
                    16f);

                CreateDetailText(
                    row.transform,
                    "HostRoleExplanation",
                    "Host access is fixed and cannot be revoked.",
                    11,
                    MutedColor,
                    8f,
                    -201f,
                    GetResponsiveControlButtonWidth(),
                    30f);

                return;
            }

            CreateDetailText(
                row.transform,
                "RoleHeader",
                canEditPermissions
                    ? "ADMIN PERMISSIONS"
                    : "ADMIN PERMISSIONS — READ ONLY",
                10,
                AccentColor,
                8f,
                -181f,
                GetResponsiveControlButtonWidth(),
                16f);

            CreateAdministratorPermissionToggle(
                row.transform,
                player,
                "CanKickPlayers",
                "Kick players",
                p => p.CanKickPlayers,
                (p, value) => p.CanKickPlayers = value,
                -200f,
                canEditPermissions);

            CreateAdministratorPermissionToggle(
                row.transform,
                player,
                "CanBanPlayers",
                "Ban players",
                p => p.CanBanPlayers,
                (p, value) => p.CanBanPlayers = value,
                -225f,
                canEditPermissions);

            CreateAdministratorPermissionToggle(
                row.transform,
                player,
                "CanManageBans",
                "Manage bans",
                p => p.CanManageBans,
                (p, value) => p.CanManageBans = value,
                -250f,
                canEditPermissions);

            CreateAdministratorPermissionToggle(
                row.transform,
                player,
                "CanChangeServerSettings",
                "Change server settings",
                p => p.CanChangeServerSettings,
                (p, value) =>
                {
                    p.CanChangeServerSettings = value;
                    if (!value)
                    {
                        p.CanChangeServerSettingsDirectly = false;
                    }
                },
                -275f,
                canEditPermissions);

            CreateAdministratorPermissionToggle(
                row.transform,
                player,
                "CanChangeServerSettingsDirectly",
                "Apply host settings directly (no request)",
                p => p.CanChangeServerSettingsDirectly,
                (p, value) =>
                {
                    p.CanChangeServerSettingsDirectly = value;
                    if (value)
                    {
                        p.CanChangeServerSettings = true;
                    }
                },
                -300f,
                canEditPermissions);

            CreateAdministratorPermissionToggle(
                row.transform,
                player,
                "CanManageIdentityData",
                "Manage identity data",
                p => p.CanManageIdentityData,
                (p, value) => p.CanManageIdentityData = value,
                -325f,
                canEditPermissions);

            CreateAdministratorPermissionToggle(
                row.transform,
                player,
                "CanManageTeamLeaders",
                "Manage teams",
                p => p.CanManageTeamLeaders,
                (p, value) => p.CanManageTeamLeaders = value,
                -350f,
                canEditPermissions);

            CreateAdministratorPermissionToggle(
                row.transform,
                player,
                "CanManageAdministrators",
                "Manage admin permissions",
                p => p.CanManageAdministrators,
                (p, value) => p.CanManageAdministrators = value,
                -375f,
                canEditPermissions && _addon.State.IsHost);
        }

        private void CreateAdministratorPermissionToggle(
            Transform parent,
            ListAdminPlayerView player,
            string name,
            string caption,
            Func<AdminPermissions, bool> getter,
            Action<AdminPermissions, bool> setter,
            float y,
            bool canEdit)
        {
            AdminPermissions snapshot =
                (player.Permissions ?? AdminPermissions.NoAccess()).Clone();

            bool enabled =
                getter(snapshot);

            Button button =
                ListUiFactory.CreateButton(
                    parent,
                    "Role_" + name,
                    caption + ": " + (enabled ? "ON" : "OFF"),
                    delegate
                    {
                        if (!canEdit || _addon == null)
                        {
                            return;
                        }

                        AdminPermissions updated =
                            snapshot.Clone();

                        setter(
                            updated,
                            !enabled);

                        try
                        {
                            _addon.SetAdminPermissions(
                                player.AuthorizationFingerprint,
                                updated);

                            UpdateStatus(
                                caption + " permission change requested.");
                        }
                        catch (Exception ex)
                        {
                            UpdateStatus(
                                "Permission update failed: " + ex.Message);
                        }
                    },
                    11);

            RectTransform rect =
                button.GetComponent<RectTransform>();

            rect.anchorMin =
                new Vector2(0f, 1f);

            rect.anchorMax =
                new Vector2(0f, 1f);

            rect.pivot =
                new Vector2(0f, 1f);

            rect.sizeDelta =
                new Vector2(
                    GetResponsiveControlButtonWidth(),
                    22f);

            rect.anchoredPosition =
                new Vector2(8f, y);

            ConfigureSingleLineButtonText(
                button,
                9,
                11);

            button.interactable =
                canEdit;
        }

        private void CreateIdentityBundleSection()
        {
            if (_addon == null || _addon.State == null ||
                !_addon.State.Permissions.CanManageIdentityData)
            {
                return;
            }

            CreateSectionHeader("IDENTITY DATA TRANSFER");

            CreateTextBlock(
                "Bundles contain authorization identity data. Keep exported files private. Import merges records by fingerprint and does not change HKMP permissions.",
                12,
                TextAnchor.MiddleLeft,
                MutedColor,
                0f,
                0f,
                0f);

            CreateResponsiveActionButton(
                "ExportIdentityBundle",
                "Export authorization identities",
                delegate
                {
                    try
                    {
                        _addon.RequestIdentityBundleAction(
                            AdminIdentityBundleAction.ExportBundle);
                        UpdateStatus("Identity bundle export requested.");
                    }
                    catch (Exception ex)
                    {
                        UpdateStatus("Could not request identity bundle export: " + ex.Message);
                    }
                },
                38f,
                13);

            CreateResponsiveActionButton(
                "ListIdentityBundles",
                "List importable identity bundles",
                delegate
                {
                    _identityBundleFiles.Clear();
                    _identityBundleFilesMessage = "Searching for identity bundles...";
                    try
                    {
                        _addon.RequestIdentityBundleAction(
                            AdminIdentityBundleAction.ListBundles);
                        UpdateStatus("Searching HKMP.List/IdentityBundles for importable files.");
                    }
                    catch (Exception ex)
                    {
                        UpdateStatus("Could not list identity bundles: " + ex.Message);
                    }

                    Refresh();
                },
                38f,
                13);

            if (!string.IsNullOrWhiteSpace(_identityBundleFilesMessage))
            {
                CreateTextBlock(
                    _identityBundleFilesMessage,
                    12,
                    TextAnchor.MiddleLeft,
                    MutedColor,
                    0f,
                    0f,
                    0f);
            }

            foreach (string fileName in _identityBundleFiles)
            {
                string selectedFile = fileName;

                CreateResponsiveActionButton(
                    "Import_" + SanitizeId(selectedFile),
                    "Import: " + selectedFile,
                    delegate
                    {
                        try
                        {
                            _addon.RequestIdentityBundleAction(
                                AdminIdentityBundleAction.ImportBundle,
                                selectedFile);
                            UpdateStatus("Import requested for " + selectedFile + ".");
                        }
                        catch (Exception ex)
                        {
                            UpdateStatus("Could not import identity bundle: " + ex.Message);
                        }
                    },
                    34f,
                    12);
            }
        }

        private void CreateDetailText(
            Transform parent,
            string name,
            string value,
            int fontSize,
            Color color,
            float x,
            float y,
            float width,
            float height)
        {
            Text text = ListUiFactory.CreateText(
                parent,
                name,
                value,
                fontSize,
                color,
                TextAnchor.UpperLeft);

            RectTransform rect = text.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(x, y);
        }

        private void CreateSmallActionButton(
            Transform parent,
            string name,
            string caption,
            float x,
            UnityEngine.Events.UnityAction action)
        {
            Button button =
                ListUiFactory.CreateButton(
                    parent,
                    name,
                    caption,
                    action,
                    13);

            RectTransform rect =
                button.GetComponent<RectTransform>();

            rect.anchorMin =
                new Vector2(
                    1f,
                    1f);

            rect.anchorMax =
                new Vector2(
                    1f,
                    1f);

            rect.pivot =
                new Vector2(
                    1f,
                    1f);

            rect.sizeDelta =
                new Vector2(
                    30f,
                    30f);

            rect.anchoredPosition =
                new Vector2(
                    x,
                    -9f);
        }

        private void ToggleExpandedPlayer(
            ushort playerId)
        {
            if (_expandedPlayers.Contains(
                    playerId))
            {
                _expandedPlayers.Remove(
                    playerId);
            }
            else
            {
                _expandedPlayers.Add(
                    playerId);
            }

            Refresh();
        }

        private static string BuildConnectedPlayersSignature(
            IReadOnlyList<ListAdminPlayerView> players)
        {
            if (players == null ||
                players.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                "|",
                players
                    .Where(x => x != null && x.IsAuthorized)
                    .OrderBy(x => x.Id)
                    .Select(
                        x =>
                            x.Id + ":" +
                            (x.Username ?? string.Empty) + ":" +
                            (x.IsAuthorized ? "1" : "0") + ":" +
                            (x.IsAdminEnabled ? "1" : "0") + ":" +
                            (x.IsHost ? "1" : "0") + ":" +
                            (x.AuthorizationFingerprint ?? string.Empty) + ":" +
                            ((x.Permissions != null && x.Permissions.CanKickPlayers) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanBanPlayers) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanManageBans) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanChangeServerSettings) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanChangeServerSettingsDirectly) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanManageIdentityData) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanManageTeamLeaders) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanManageAdministrators) ? "1" : "0")));
        }

        private void CreateToggle(
            string caption,
            Func<ServerSettingsData, bool> getter,
            Action<ServerSettingsData, bool> setter)
        {
            GameObject row =
                ListUiFactory.CreateObject(
                    _content,
                    "Toggle_" + caption);

            ListUiFactory.AddLayoutElement(
                row,
                0f,
                40f);

            ServerSettingsData settings = GetEditableSettings();

            bool enabled =
                settings != null && getter(settings);

            Button button =
                ListUiFactory.CreateButton(
                    row.transform,
                    "Button",
                    caption + ": " + (enabled ? "ON" : "OFF"),
                    delegate
                    {
                        if (!CanChangeSettings())
                        {
                            return;
                        }

                        ServerSettingsData current = GetEditableSettings();
                        if (current == null)
                        {
                            return;
                        }

                        setter(current, !getter(current));
                        SubmitSettings(current);
                        Refresh();
                    },
                    13);

            RectTransform rect =
                button.GetComponent<RectTransform>();

            rect.anchorMin =
                new Vector2(0f, 0f);

            rect.anchorMax =
                new Vector2(0f, 1f);

            rect.pivot =
                new Vector2(0f, 0.5f);

            rect.sizeDelta =
                new Vector2(
                    GetResponsiveControlButtonWidth(),
                    0f);

            rect.anchoredPosition =
                new Vector2(4f, 0f);

            ConfigureSingleLineButtonText(
                button,
                9,
                13);

            button.interactable = CanChangeSettings();
        }

        private Button CreateResponsiveActionButton(
            string name,
            string caption,
            UnityEngine.Events.UnityAction action,
            float rowHeight,
            int fontSize)
        {
            GameObject row =
                ListUiFactory.CreateObject(
                    _content,
                    name + "_Row");

            ListUiFactory.AddLayoutElement(
                row,
                0f,
                rowHeight);

            Button button =
                ListUiFactory.CreateButton(
                    row.transform,
                    name,
                    caption,
                    action,
                    fontSize);

            RectTransform rect =
                button.GetComponent<RectTransform>();

            rect.anchorMin =
                new Vector2(0f, 0f);

            rect.anchorMax =
                new Vector2(0f, 1f);

            rect.pivot =
                new Vector2(0f, 0.5f);

            rect.sizeDelta =
                new Vector2(
                    GetResponsiveControlButtonWidth(),
                    0f);

            rect.anchoredPosition =
                new Vector2(4f, 0f);

            ConfigureSingleLineButtonText(
                button,
                8,
                fontSize);

            return button;
        }

        private float GetResponsiveControlButtonWidth()
        {
            float panelWidth =
                _root == null
                    ? 600f
                    : _root.rect.width;

            if (panelWidth <= 1f)
            {
                panelWidth = 600f;
            }

            return Mathf.Clamp(
                panelWidth * 0.52f,
                220f,
                360f);
        }

        private static void ConfigureSingleLineButtonText(
          Button button,
          int minimumFontSize,
          int maximumFontSize)
        {
            if (button == null)
            {
                return;
            }

            Text label =
                button.GetComponentInChildren<Text>();

            if (label == null)
            {
                return;
            }

            label.resizeTextForBestFit = false;
            label.fontSize =
                Mathf.Max(minimumFontSize, maximumFontSize);
            label.horizontalOverflow =
                HorizontalWrapMode.Overflow;

            label.verticalOverflow =
                VerticalWrapMode.Truncate;
            label.alignment =
                TextAnchor.MiddleLeft;
        }


        private void OnIdentityNoteChanged(
            string authorizationFingerprint,
            string value)
        {
            if (_settingIdentityNoteProgrammatically ||
                _addon == null ||
                string.IsNullOrWhiteSpace(authorizationFingerprint))
            {
                return;
            }

            try
            {
                _addon.SetAdminIdentityNote(
                    authorizationFingerprint,
                    value ?? string.Empty);
            }
            catch (Exception ex)
            {
                UpdateStatus(
                    "Could not save administrator identity note: " + ex.Message);
            }
        }

        private void ResetDamageSettings()
        {
            if (!CanChangeSettings())
            {
                return;
            }

            ServerSettingsData current = GetEditableSettings();
            ServerSettingsData defaults = ServerSettingsData.CreateDefault();
            if (current == null || defaults == null)
            {
                UpdateStatus("Damage settings could not be reset.");
                return;
            }

            current.NailDamage = defaults.NailDamage;
            current.GrubberflyElegyDamage = defaults.GrubberflyElegyDamage;
            current.VengefulSpiritDamage = defaults.VengefulSpiritDamage;
            current.ShadeSoulDamage = defaults.ShadeSoulDamage;
            current.DesolateDiveDamage = defaults.DesolateDiveDamage;
            current.DescendingDarkDamage = defaults.DescendingDarkDamage;
            current.HowlingWraithDamage = defaults.HowlingWraithDamage;
            current.AbyssShriekDamage = defaults.AbyssShriekDamage;
            current.GreatSlashDamage = defaults.GreatSlashDamage;
            current.DashSlashDamage = defaults.DashSlashDamage;
            current.CycloneSlashDamage = defaults.CycloneSlashDamage;
            current.SporeShroomDamage = defaults.SporeShroomDamage;
            current.SporeDungShroomDamage = defaults.SporeDungShroomDamage;
            current.ThornOfAgonyDamage = defaults.ThornOfAgonyDamage;
            current.SharpShadowDamage = defaults.SharpShadowDamage;

            SubmitSettings(current);
            Refresh();
            UpdateStatus("Damage values restored to their defaults.");
        }

        private void CreateDamageRow(
            DamageSetting setting)
        {
            GameObject row =
                ListUiFactory.CreateObject(
                    _content,
                    "Damage_" +
                    setting.Name);

            ListUiFactory.AddLayoutElement(
                row,
                0f,
                38f);

            HorizontalLayoutGroup layout =
                row.AddComponent<
                    HorizontalLayoutGroup>();

            layout.childAlignment =
                TextAnchor.MiddleLeft;

            layout.childControlWidth =
                false;

            layout.childControlHeight =
                true;

            layout.childForceExpandWidth =
                false;

            layout.childForceExpandHeight =
                true;

            layout.spacing =
                4f;

            Text label =
                ListUiFactory.CreateText(
                    row.transform,
                    "Label",
                    setting.Name,
                    13,
                    TextColor,
                    TextAnchor.MiddleLeft);

            LayoutElement labelLayout =
                label.gameObject.AddComponent<
                    LayoutElement>();

            labelLayout.preferredWidth =
                230f;

            labelLayout.minWidth =
                160f;

            Button minus =
                ListUiFactory.CreateButton(
                    row.transform,
                    "Minus",
                    "−",
                    delegate
                    {
                        ChangeDamage(
                            setting,
                            -1);
                    },
                    16);

            LayoutElement minusLayout =
                minus.gameObject.AddComponent<
                    LayoutElement>();

            minusLayout.preferredWidth =
                34f;

            minusLayout.minWidth =
                34f;

            Text valueText =
                ListUiFactory.CreateText(
                    row.transform,
                    "Value",
                    GetDamageText(setting),
                    15,
                    AccentColor,
                    TextAnchor.MiddleCenter);

            LayoutElement valueLayout =
                valueText.gameObject.AddComponent<
                    LayoutElement>();

            valueLayout.preferredWidth =
                54f;

            valueLayout.minWidth =
                54f;

            Button plus =
                ListUiFactory.CreateButton(
                    row.transform,
                    "Plus",
                    "+",
                    delegate
                    {
                        ChangeDamage(
                            setting,
                            1);
                    },
                    16);

            LayoutElement plusLayout =
                plus.gameObject.AddComponent<
                    LayoutElement>();

            plusLayout.preferredWidth =
                34f;

            plusLayout.minWidth =
                34f;

            bool canChangeSettings = CanChangeSettings();
            minus.interactable = canChangeSettings;
            plus.interactable = canChangeSettings;
        }

        private string GetDamageText(
            DamageSetting setting)
        {
            ServerSettingsData settings = GetEditableSettings();

            if (settings == null)
            {
                return "0";
            }

            return setting.Get(settings)
                .ToString();
        }

        private void ChangeDamage(
            DamageSetting setting,
            int delta)
        {
            if (!CanChangeSettings())
            {
                return;
            }

            ServerSettingsData settings = GetEditableSettings();
            if (settings == null)
            {
                return;
            }

            int value =
                setting.Get(settings) +
                delta;

            value =
                Mathf.Clamp(
                    value,
                    0,
                    byte.MaxValue);

            setting.Set(
                settings,
                (byte)value);

            SubmitSettings(settings);
            Refresh();
        }

        private void CreateBanRow(
            AdminBanEntryView ban)
        {
            GameObject row =
                ListUiFactory.CreateObject(
                    _content,
                    "Ban_" +
                    SanitizeId(
                        ban.AuthorizationFingerprint));

            ListUiFactory.AddLayoutElement(
                row,
                0f,
                62f);

            HorizontalLayoutGroup layout =
                row.AddComponent<
                    HorizontalLayoutGroup>();

            layout.childAlignment =
                TextAnchor.MiddleCenter;

            layout.childControlWidth =
                false;

            layout.childControlHeight =
                true;

            layout.childForceExpandWidth =
                false;

            layout.childForceExpandHeight =
                true;

            layout.spacing =
                5f;

            Text text =
                ListUiFactory.CreateText(
                    row.transform,
                    "Info",
                    (string.IsNullOrWhiteSpace(
                        ban.Username)
                        ? "Unknown player"
                        : ban.Username) +
                    "\n" +
                    ban.AuthorizationFingerprint,
                    11,
                    TextColor,
                    TextAnchor.MiddleLeft);

            LayoutElement textLayout =
                text.gameObject.AddComponent<
                    LayoutElement>();

            textLayout.preferredWidth =
                0f;

            textLayout.flexibleWidth =
                1f;

            textLayout.minWidth =
                100f;

            Button unban =
                ListUiFactory.CreateButton(
                    row.transform,
                    "Unban",
                    "Unban",
                    delegate
                    {
                        try
                        {
                            _addon.UnbanPlayer(
                                ban.AuthorizationFingerprint);
                        }
                        catch
                        {
                            UpdateStatus(
                                "Could not send unban request.");
                        }
                    },
                    14);

            LayoutElement buttonLayout =
                unban.gameObject.AddComponent<
                    LayoutElement>();

            buttonLayout.preferredWidth =
                85f;

            buttonLayout.minWidth =
                85f;
            unban.interactable = _addon != null && _addon.State != null &&
                _addon.State.Permissions.CanManageBans;
        }

        private void CreateTextBlock(
            string value,
            int fontSize,
            TextAnchor alignment,
            Color color,
            float left,
            float bottom,
            float right)
        {
            GameObject row =
                ListUiFactory.CreateObject(
                    _content,
                    "TextBlock");

            ListUiFactory.AddLayoutElement(
                row,
                0f,
                44f);

            Text text =
                ListUiFactory.CreateText(
                    row.transform,
                    "Text",
                    value,
                    fontSize,
                    color,
                    alignment);

            ListUiFactory.SetFullStretch(
                text.rectTransform,
                left + 4f,
                bottom,
                right + 4f,
                0f);
        }

        private void UpdateStatus(
            string value)
        {
            if (_status != null)
            {
                _status.text =
                    value ?? string.Empty;
            }
        }

        private static string SanitizeId(
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                return "Unknown";
            }

            char[] chars =
                value.ToCharArray();

            for (int i = 0;
                 i < chars.Length;
                 i++)
            {
                if (!char.IsLetterOrDigit(
                        chars[i]))
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private static IList<DamageSetting> GetDamageSettings()
        {
            return new List<DamageSetting>
            {
                new DamageSetting
                {
                    Name = "Nail",
                    Get = s => s.NailDamage,
                    Set = (s, v) => s.NailDamage = v
                },
                new DamageSetting
                {
                    Name = "Grubberfly's Elegy",
                    Get = s => s.GrubberflyElegyDamage,
                    Set = (s, v) => s.GrubberflyElegyDamage = v
                },
                new DamageSetting
                {
                    Name = "Vengeful Spirit",
                    Get = s => s.VengefulSpiritDamage,
                    Set = (s, v) => s.VengefulSpiritDamage = v
                },
                new DamageSetting
                {
                    Name = "Shade Soul",
                    Get = s => s.ShadeSoulDamage,
                    Set = (s, v) => s.ShadeSoulDamage = v
                },
                new DamageSetting
                {
                    Name = "Desolate Dive",
                    Get = s => s.DesolateDiveDamage,
                    Set = (s, v) => s.DesolateDiveDamage = v
                },
                new DamageSetting
                {
                    Name = "Descending Dark",
                    Get = s => s.DescendingDarkDamage,
                    Set = (s, v) => s.DescendingDarkDamage = v
                },
                new DamageSetting
                {
                    Name = "Howling Wraiths",
                    Get = s => s.HowlingWraithDamage,
                    Set = (s, v) => s.HowlingWraithDamage = v
                },
                new DamageSetting
                {
                    Name = "Abyss Shriek",
                    Get = s => s.AbyssShriekDamage,
                    Set = (s, v) => s.AbyssShriekDamage = v
                },
                new DamageSetting
                {
                    Name = "Great Slash",
                    Get = s => s.GreatSlashDamage,
                    Set = (s, v) => s.GreatSlashDamage = v
                },
                new DamageSetting
                {
                    Name = "Dash Slash",
                    Get = s => s.DashSlashDamage,
                    Set = (s, v) => s.DashSlashDamage = v
                },
                new DamageSetting
                {
                    Name = "Cyclone Slash",
                    Get = s => s.CycloneSlashDamage,
                    Set = (s, v) => s.CycloneSlashDamage = v
                },
                new DamageSetting
                {
                    Name = "Spore Shroom",
                    Get = s => s.SporeShroomDamage,
                    Set = (s, v) => s.SporeShroomDamage = v
                },
                new DamageSetting
                {
                    Name = "Spore Dung Shroom",
                    Get = s => s.SporeDungShroomDamage,
                    Set = (s, v) => s.SporeDungShroomDamage = v
                },
                new DamageSetting
                {
                    Name = "Thorns of Agony",
                    Get = s => s.ThornOfAgonyDamage,
                    Set = (s, v) => s.ThornOfAgonyDamage = v
                },
                new DamageSetting
                {
                    Name = "Sharp Shadow",
                    Get = s => s.SharpShadowDamage,
                    Set = (s, v) => s.SharpShadowDamage = v
                }
            };
        }
    }
}
