using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using HKMP.List.Client;
using HKMP.List.Shared;

namespace HKMP.List.UI
{
    [DefaultExecutionOrder(32000)]
    public sealed class ListUiController :
        MonoBehaviour
    {
        private const float HeaderHeight = 52f;
        private const float MinimumWindowWidth = 460f;
        private const float MinimumWindowHeight = 280f;
        private const float MaximumWindowWidth = 1400f;
        private const float MaximumWindowHeight = 900f;
        private const float MinimumVisiblePixels = 40f;
        private const float RowHeight = 88f;
        private const float CompactExpandedRowHeight = 150f;
        private const float ExpandedRowHeight = 320f;
        private const float RowSpacing = 8f;
        private const float ResizeHandleSize = 12f;

        private static readonly Color WindowColor =
            new Color(
                0.055f,
                0.055f,
                0.070f,
                1f);

        private static readonly Color HeaderColor =
            new Color(
                0.085f,
                0.085f,
                0.105f,
                0.99f);

        private static readonly Color RowColor =
            new Color(
                0.075f,
                0.075f,
                0.090f,
                0.98f);

        private static readonly Color RowAlternateColor =
            new Color(
                0.065f,
                0.065f,
                0.080f,
                0.98f);

        private static readonly Color BorderColor =
            new Color(
                0.30f,
                0.30f,
                0.34f,
                1f);

        private static readonly Color MutedTextColor =
            new Color(
                0.62f,
                0.62f,
                0.66f,
                1f);

        private static readonly Color InputColor =
            new Color(
                0.035f,
                0.035f,
                0.045f,
                0.98f);

        private static readonly Color AccentColor =
            new Color(
                0.78f,
                0.78f,
                0.84f,
                1f);
        private static readonly Color TextColor = Color.white;
        private readonly object _pendingGate =
            new object();

        private readonly Dictionary<ushort, string> _pendingNotes =
            new Dictionary<ushort, string>();

        private readonly List<GameObject> _rowObjects =
            new List<GameObject>();

        private readonly Dictionary<ushort, InputField> _noteFields =
            new Dictionary<ushort, InputField>();

        private readonly Dictionary<ushort, InputField> _skinIdFields =
            new Dictionary<ushort, InputField>();

        private readonly Dictionary<string, InputField> _adminIdentityNoteFields =
            new Dictionary<string, InputField>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<ushort, Image> _skinImages =
            new Dictionary<ushort, Image>();

        private readonly HashSet<ushort> _expandedAdminRows =
            new HashSet<ushort>();

        private string _adminStructureSignature =
            string.Empty;

        private Canvas _canvas;
        private RectTransform _canvasRect;
        private RectTransform _backdropRect;
        private Image _backdropImage;
        private RectTransform _window;
        private RectTransform _header;
        private RectTransform _playersRoot;
        private ScrollRect _scrollRect;
        private Text _title;
        private Text _status;
        private Button _settingsButton;
        private Button _closeButton;

        private RectTransform _settingsRoot;
        private ScrollRect _settingsScrollRect;
        private RectTransform _settingsContent;
        private Button _generalSettingsTabButton;
        private Button _sidePreviewButton;
        private Button _benchPreviewButton;
        private Button _resetWindowButton;
        private Button _resetAllSettingsButton;
        private Button _reloadSkinsButton;
        private Button _adminSettingsButton;
        private Button _teamSettingsButton;
        private Button _roundsSettingsButton;
        private Button _teamSortButton;
        private ListTeamSettingsPanel _teamSettingsPanel;
        private ListRoundsSettingsPanel _roundsSettingsPanel;
        private bool _sortByTeams;
        private Button _keyButton;
        private Text _keyButtonText;
        private Text _settingsStatus;
        private Text _settingsHeading;
        private float _lastSettingsHeaderWidth = -1f;
        private bool _lastSettingsHeaderRoundsAvailable;
        private bool _settingsHeaderLayoutInitialized;
        private bool _settingsHeadingFits = true;

        private ListAdminSettingsPanel _adminSettingsPanel;

        private ListClientAddon _addon;
        private ListClientState _state;
        private bool _subscribedToState;
        private bool _subscribedToAddonEvents;

        private bool _playersDirty;
        private bool _settingsDirty;
        private bool _authorizationDirty;
        private bool _adminPlayersDirty;
        private bool _adminBansDirty;

        private bool _visible;
        private bool _settingsVisible;

        private bool _capturingKey;

        private GameObject _gameMenuSelection;
        private EventSystem _gameMenuEventSystem;

        private bool _ownsEventSystem;
        private EventSystem _eventSystem;

        private ListSkinPreviewMode _previewMode =
            ListSkinPreviewMode.Side;

        private bool _initialized;
        private bool _rebuildingRows;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);

            EnsureSettings();

            KeepCursorVisible();

            CreateCanvas();
            CreateWindow();
            ListRoundsIntegration.AvailabilityChanged += OnRoundsAvailabilityChanged;

            ApplyStoredWindowRect();

            _previewMode =
                ListMod.GlobalSettings.SkinPreviewMode;

            HideInternal(
                restoreOtherUiSelection: false);

            _initialized = true;
            KeepCursorVisible();
        }

        private void OnEnable()
        {
            KeepCursorVisible();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
            {
                KeepCursorVisible();
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (!pauseStatus)
            {
                KeepCursorVisible();
            }
        }

        private void OnDestroy()
        {
            _visible = false;
            ListCursorHook.SetListVisible(false);

            ListRoundsIntegration.AvailabilityChanged -= OnRoundsAvailabilityChanged;
            ListRoundsIntegration.Dispose();
            UnsubscribeFromState();

            if (_adminSettingsPanel != null)
                _adminSettingsPanel.Hide();
            if (_teamSettingsPanel != null)
                _teamSettingsPanel.Hide();
            if (_roundsSettingsPanel != null)
                _roundsSettingsPanel.Hide();

            RestoreOtherUiSelection();

            if (_ownsEventSystem &&
                _eventSystem != null)
            {
                Destroy(
                    _eventSystem.gameObject);
            }

            ListSkinPreviewProvider.ClearCache();
        }

        private void Update()
        {
            if (!_initialized)
            {
                return;
            }

            KeepCursorVisible();
            ListRoundsIntegration.Pump();
            UpdateSettingsTabLayout(false);
            ResolveAddon();

            if (_addon != null)
            {
                _addon.PumpSkinPersistence();
            }

            EnsureStateSubscription();

            HandleToggleKey();

            ApplyPendingStateChanges();
            ApplyViewVisibility();

            if (_adminSettingsPanel != null)
            {
                _adminSettingsPanel.PumpPendingNetworkResults();
            }

            if (_teamSettingsPanel != null)
            {
                _teamSettingsPanel.PumpPendingUpdates();
            }

            if (_visible)
            {
                KeepInputOnList();
            }

            if (_visible &&
                _settingsVisible &&
                _capturingKey)
            {
                TryCaptureKey();
            }
        }

        private void LateUpdate()
        {
            if (!_initialized)
            {
                return;
            }

            KeepCursorVisible();

            if (_visible)
            {
                KeepInputOnList();
            }
        }

        private void OnGUI()
        {
            if (!_initialized)
            {
                return;
            }

            KeepCursorVisible();

            if (_visible)
            {
                KeepInputOnList();
            }
        }

        private void ResolveAddon()
        {
            if (_addon != null)
            {
                return;
            }

            ListMod mod =
                ListMod.Instance;

            if (mod == null)
            {
                return;
            }

            _addon =
                mod.ClientAddon;
        }

        private void EnsureStateSubscription()
        {
            if (_subscribedToState ||
                _addon == null ||
                _addon.State == null)
            {
                return;
            }

            _state =
                _addon.State;

            _state.PlayersChanged +=
                OnPlayersChanged;

            _state.NoteChanged +=
                OnNoteChanged;

            _state.AuthorizationChanged +=
                OnAuthorizationChanged;

            _state.LocalPlayerIdentityChanged +=
                OnLocalPlayerIdentityChanged;

            _state.ServerSettingsChanged +=
                OnServerSettingsChanged;

            _state.AdminPlayersChanged +=
                OnAdminPlayersChanged;

            _state.AdminBansChanged +=
                OnAdminBansChanged;

            _state.TeamSettingsChanged +=
                OnTeamSettingsChanged;

            if (!_subscribedToAddonEvents &&
                _addon != null)
            {
                _addon.AdminActionResultReceived +=
                    OnAdminActionResult;

                _subscribedToAddonEvents = true;
            }

            _subscribedToState = true;

            lock (_pendingGate)
            {
                _playersDirty = true;
                _authorizationDirty = true;
                _adminPlayersDirty = true;
                _adminBansDirty = true;
                _adminStructureSignature =
                    BuildAdminStructureSignature(
                        _state.GetAdminPlayers());
            }

            RebuildPlayers();
            RefreshSettingsVisuals();
        }

        private void UnsubscribeFromState()
        {
            if (!_subscribedToState ||
                _state == null)
            {
                return;
            }

            _state.PlayersChanged -=
                OnPlayersChanged;

            _state.NoteChanged -=
                OnNoteChanged;

            _state.AuthorizationChanged -=
                OnAuthorizationChanged;

            _state.LocalPlayerIdentityChanged -=
                OnLocalPlayerIdentityChanged;

            _state.ServerSettingsChanged -=
                OnServerSettingsChanged;

            _state.AdminPlayersChanged -=
                OnAdminPlayersChanged;

            _state.AdminBansChanged -=
                OnAdminBansChanged;

            _state.TeamSettingsChanged -=
                OnTeamSettingsChanged;

            if (_subscribedToAddonEvents &&
                _addon != null)
            {
                _addon.AdminActionResultReceived -=
                    OnAdminActionResult;

                _subscribedToAddonEvents = false;
            }

            _subscribedToState = false;
            _state = null;
        }

        private void OnPlayersChanged(
            IReadOnlyList<ListPlayerView> players)
        {
            lock (_pendingGate)
            {
                _playersDirty = true;
                _authorizationDirty = true;
            }
        }

        private void OnNoteChanged(
            ushort playerId,
            string note)
        {
            lock (_pendingGate)
            {
                _pendingNotes[playerId] =
                    note ?? string.Empty;
            }
        }

        private void OnAuthorizationChanged(
            bool isAuthorized)
        {
            lock (_pendingGate)
            {
                _authorizationDirty = true;
                _playersDirty = true;
            }
        }

        private void OnLocalPlayerIdentityChanged()
        {
            lock (_pendingGate)
            {
                _playersDirty = true;
            }
        }

        private void OnServerSettingsChanged(
            ServerSettingsData settings)
        {
            lock (_pendingGate)
            {
                _settingsDirty = true;
            }
        }

        private void OnTeamSettingsChanged(
            IReadOnlyList<TeamSettingsEntryData> entries)
        {
            lock (_pendingGate)
            {
                _playersDirty = true;
                _settingsDirty = true;
            }
        }

        private void OnAdminPlayersChanged(
            IReadOnlyList<ListAdminPlayerView> players)
        {
            string signature =
                BuildAdminStructureSignature(
                    players);

            lock (_pendingGate)
            {
                _adminPlayersDirty = true;

                if (_visible &&
                    _state != null &&
                    (_state.IsAuthorized || _state.IsHost) &&
                    !string.Equals(
                        signature,
                        _adminStructureSignature,
                        StringComparison.Ordinal))
                {
                    _playersDirty = true;
                }

                _adminStructureSignature =
                    signature;
            }
        }

        private static string BuildAdminStructureSignature(
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
                    .Where(x => x != null)
                    .OrderBy(x => x.Id)
                    .Select(
                        x =>
                            x.Id + ":" +
                            (x.Username ?? string.Empty) + ":" +
                            (x.IsAuthorized ? "1" : "0") + ":" +
                            (x.IsAdminEnabled ? "1" : "0") + ":" +
                            (x.AuthorizationFingerprint ?? string.Empty) + ":" +
                            ((x.Permissions != null && x.Permissions.CanKickPlayers) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanBanPlayers) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanManageBans) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanChangeServerSettings) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanManageIdentityData) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanManageTeamLeaders) ? "1" : "0") +
                            ((x.Permissions != null && x.Permissions.CanManageAdministrators) ? "1" : "0")));
        }

        private void OnAdminBansChanged(
            IReadOnlyList<AdminBanEntryView> bans)
        {
            lock (_pendingGate)
            {
                _adminBansDirty = true;
            }
        }

        private void OnAdminActionResult(
            AdminActionResultData result)
        {
            if (result == null)
            {
                return;
            }

            string message =
                result.Success
                    ? result.Message
                    : "Admin action failed: " +
                      result.Message;

            SetSettingsStatus(
                message);

            if (_adminSettingsPanel != null &&
                _adminSettingsPanel.IsVisible)
            {
                _adminSettingsPanel.SetStatus(
                    message);
            }
        }

        private void ApplyPendingStateChanges()
        {
            bool playersDirty;
            bool authorizationDirty;
            bool settingsDirty;
            bool adminPlayersDirty;
            bool adminBansDirty;
            Dictionary<ushort, string> notes;

            lock (_pendingGate)
            {
                playersDirty =
                    _playersDirty;

                authorizationDirty =
                    _authorizationDirty;

                settingsDirty =
                    _settingsDirty;

                adminPlayersDirty =
                    _adminPlayersDirty;

                adminBansDirty =
                    _adminBansDirty;

                _playersDirty = false;
                _authorizationDirty = false;
                _settingsDirty = false;
                _adminPlayersDirty = false;
                _adminBansDirty = false;

                notes =
                    new Dictionary<ushort, string>(
                        _pendingNotes);

                _pendingNotes.Clear();
            }

            if (playersDirty)
            {
                if (HasFocusedNoteInput())
                {
                    lock (_pendingGate)
                    {
                        _playersDirty = true;
                    }
                }
                else
                {
                    RebuildPlayers();
                }
            }

            foreach (KeyValuePair<ushort, string> pair
                     in notes)
            {
                UpdateNoteField(
                    pair.Key,
                    pair.Value);
            }

            if (adminPlayersDirty)
            {
                RefreshAdminIdentityNoteFields();

                if (_adminSettingsPanel != null &&
                    _adminSettingsPanel.IsVisible)
                {
                    _adminSettingsPanel.RefreshAdminPlayersOnly();
                }
            }

            if (authorizationDirty ||
                settingsDirty ||
                adminBansDirty)
            {
                if (_state == null || (!_state.IsAuthorized && !_state.IsHost))
                {
                    _expandedAdminRows.Clear();
                    _adminStructureSignature = string.Empty;
                }

                RefreshSettingsVisuals();
            }
        }

        private void HandleToggleKey()
        {
            if (_capturingKey)
            {
                return;
            }

            KeyCode key =
                ListMod.GlobalSettings == null
                    ? KeyCode.F7
                    : ListMod.GlobalSettings.ToggleKey;

            if (key == KeyCode.None)
            {
                key =
                    KeyCode.F7;
            }

            if (!Input.GetKeyDown(key))
            {
                return;
            }

            if (_visible && IsTextInputFocused())
            {
                return;
            }

            ToggleVisibility();
        }

        private void TryCaptureKey()
        {
            Array keys =
                Enum.GetValues(
                    typeof(KeyCode));

            foreach (object value in keys)
            {
                KeyCode key =
                    (KeyCode)value;

                if (key == KeyCode.None)
                {
                    continue;
                }

                if (IsMouseKey(key))
                {
                    continue;
                }

                if (!Input.GetKeyDown(key))
                {
                    continue;
                }

                if (key == KeyCode.Escape)
                {
                    _capturingKey = false;
                    RefreshSettingsVisuals();
                    return;
                }

                ListMod.GlobalSettings.ToggleKey =
                    key;

                ListMod.GlobalSettings.Normalize();

                _capturingKey = false;

                UpdateKeyButton();
                RefreshSettingsVisuals();

                return;
            }
        }

        private static bool IsMouseKey(
            KeyCode key)
        {
            return
                key >= KeyCode.Mouse0 &&
                key <= KeyCode.Mouse6;
        }

        private bool IsTextInputFocused()
        {
            EventSystem eventSystem =
                GetCurrentEventSystem();

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

            if (!selected.activeInHierarchy)
            {
                eventSystem.SetSelectedGameObject(null);
                return false;
            }

            InputField field =
                selected.GetComponentInParent<InputField>();

            return field != null &&
                   field.isActiveAndEnabled &&
                   field.gameObject.activeInHierarchy;
        }

        private bool IsInputBeingEdited(
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
                GetCurrentEventSystem();

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

        private void ToggleVisibility()
        {
            if (_visible)
            {
                Hide();
                return;
            }

            Show();
        }

        public void Show()
        {
            if (_visible)
            {
                return;
            }

            TakeInputFromOtherUi();

            _visible = true;
            ListCursorHook.SetListVisible(true);
            _settingsVisible = false;
            _capturingKey = false;

            if (_backdropRect != null)
                _backdropRect.gameObject.SetActive(true);

            _window.gameObject.SetActive(true);
            _window.SetAsLastSibling();
            ApplyViewVisibility();

            RebuildPlayers();

            ShowCursor();

            UpdateTitle();
        }

        public void Hide()
        {
            if (!_visible)
            {
                return;
            }

            HideInternal(
                restoreOtherUiSelection: true);
        }

        private void HideInternal(
            bool restoreOtherUiSelection)
        {
            _visible = false;
            ListCursorHook.SetListVisible(false);
            _settingsVisible = false;
            _capturingKey = false;
            ApplyViewVisibility();

            if (_adminSettingsPanel != null)
            {
                _adminSettingsPanel.Hide();
            }

            if (_teamSettingsPanel != null)
            {
                _teamSettingsPanel.Hide();
            }
            if (_roundsSettingsPanel != null)
            {
                _roundsSettingsPanel.Hide();
            }

            ApplyViewVisibility();
            if (_window != null)
            {
                _window.gameObject.SetActive(false);
            }

            if (_backdropRect != null)
                _backdropRect.gameObject.SetActive(false);

            if (restoreOtherUiSelection)
            {
                RestoreOtherUiSelection();
            }
        }

        private void TakeInputFromOtherUi()
        {
            EventSystem eventSystem =
                GetCurrentEventSystem();

            if (eventSystem == null)
            {
                return;
            }

            GameObject selected =
                eventSystem.currentSelectedGameObject;

            if (selected != null &&
                !IsListUi(selected))
            {
                _gameMenuSelection = selected;
                _gameMenuEventSystem = eventSystem;
            }

            eventSystem.SetSelectedGameObject(null);
        }

        private void KeepInputOnList()
        {
            if (!_visible)
            {
                return;
            }

            Cursor.lockState =
                CursorLockMode.None;

            Cursor.visible =
                true;

            EventSystem eventSystem =
                GetCurrentEventSystem();

            if (eventSystem == null)
            {
                return;
            }

            GameObject selected =
                eventSystem.currentSelectedGameObject;

            if (selected != null &&
                !IsListUi(selected))
            {
                if (_gameMenuSelection == null)
                {
                    _gameMenuSelection = selected;
                    _gameMenuEventSystem = eventSystem;
                }

                eventSystem.SetSelectedGameObject(null);
            }
        }

        private void RestoreOtherUiSelection()
        {
            EventSystem eventSystem =
                _gameMenuEventSystem != null
                    ? _gameMenuEventSystem
                    : GetCurrentEventSystem();

            if (eventSystem != null &&
                _gameMenuSelection != null &&
                _gameMenuSelection.activeInHierarchy)
            {
                eventSystem.SetSelectedGameObject(
                    _gameMenuSelection);
            }

            _gameMenuSelection = null;
            _gameMenuEventSystem = null;
        }

        private EventSystem GetCurrentEventSystem()
        {
            EventSystem current =
                EventSystem.current;

            return current != null
                ? current
                : _eventSystem;
        }

        private bool IsListUi(
            GameObject candidate)
        {
            if (candidate == null ||
                _canvasRect == null)
            {
                return false;
            }

            Transform candidateTransform =
                candidate.transform;

            return
                candidateTransform == _canvasRect ||
                candidateTransform.IsChildOf(
                    _canvasRect);
        }

        private void ShowCursor()
        {
            KeepCursorVisible();
        }

        private void KeepCursorVisible()
        {
            if (!_visible)
            {
                return;
            }

            if (Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState =
                    CursorLockMode.None;
            }

            if (!Cursor.visible)
            {
                Cursor.visible =
                    true;
            }
        }

        private void CreateCanvas()
        {
            GameObject canvasObject =
                ListUiFactory.CreateObject(
                    transform,
                    "Canvas");

            _canvas =
                canvasObject.AddComponent<Canvas>();

            _canvas.renderMode =
                RenderMode.ScreenSpaceOverlay;

            _canvas.sortingOrder =
                32000;

            CanvasScaler scaler =
                canvasObject.AddComponent<
                    CanvasScaler>();

            scaler.uiScaleMode =
                CanvasScaler.ScaleMode.ConstantPixelSize;

            scaler.scaleFactor =
                1f;

            scaler.referencePixelsPerUnit =
                100f;

            _canvas.pixelPerfect = true;

            canvasObject.AddComponent<
                GraphicRaycaster>();

            _canvasRect =
                _canvas
                    .GetComponent<RectTransform>();

            _canvasRect.anchorMin =
                Vector2.zero;

            _canvasRect.anchorMax =
                Vector2.one;

            _canvasRect.offsetMin =
                Vector2.zero;

            _canvasRect.offsetMax =
                Vector2.zero;

            GameObject backdropObject = ListUiFactory.CreateObject(
                _canvasRect,
                "ModalBackdrop");
            _backdropRect = backdropObject.GetComponent<RectTransform>();
            _backdropRect.anchorMin = Vector2.zero;
            _backdropRect.anchorMax = Vector2.one;
            _backdropRect.offsetMin = Vector2.zero;
            _backdropRect.offsetMax = Vector2.zero;
            _backdropImage = backdropObject.AddComponent<Image>();
            _backdropImage.sprite = ListUiFactory.WhiteSprite;
            _backdropImage.color = new Color(0.015f, 0.015f, 0.022f, 0.90f);
            _backdropImage.raycastTarget = true;
            backdropObject.SetActive(false);

            EnsureEventSystem();
        }

        private void EnsureEventSystem()
        {
            EventSystem existing =
                EventSystem.current;

            if (existing != null)
            {
                _eventSystem =
                    existing;

                return;
            }

            GameObject gameObject =
                new GameObject(
                    "HKMP.List.EventSystem");

            DontDestroyOnLoad(
                gameObject);

            _eventSystem =
                gameObject.AddComponent<
                    EventSystem>();

            gameObject.AddComponent<
                StandaloneInputModule>();

            _ownsEventSystem =
                true;
        }

        private void CreateWindow()
        {
            GameObject windowObject =
                ListUiFactory.CreateObject(
                    _canvasRect,
                    "Window");

            _window =
                windowObject.GetComponent<
                    RectTransform>();

            ListUiFactory.SetCentered(
                _window,
                new Vector2(
                    760f,
                    520f),
                Vector2.zero);

            Image windowImage =
                windowObject.AddComponent<Image>();

            windowImage.sprite =
                ListUiFactory.WhiteSprite;

            windowImage.color =
                WindowColor;

            windowImage.raycastTarget =
                true;

            Outline outline =
                windowObject.AddComponent<
                    Outline>();

            outline.effectColor =
                BorderColor;

            outline.effectDistance =
                new Vector2(
                    1f,
                    -1f);

            CreateHeader();
            CreatePlayersArea();
            CreateSettingsArea();
            CreateDragAndResizeHandles();

            UpdateTitle();
        }

        private void CreateHeader()
        {
            GameObject headerObject =
                ListUiFactory.CreateObject(
                    _window,
                    "Header");

            _header =
                headerObject.GetComponent<
                    RectTransform>();

            _header.anchorMin =
                new Vector2(
                    0f,
                    1f);

            _header.anchorMax =
                new Vector2(
                    1f,
                    1f);

            _header.pivot =
                new Vector2(
                    0.5f,
                    1f);

            _header.sizeDelta =
                new Vector2(
                    0f,
                    HeaderHeight);

            _header.anchoredPosition =
                Vector2.zero;

            Image headerImage =
                headerObject.AddComponent<Image>();

            headerImage.sprite =
                ListUiFactory.WhiteSprite;

            headerImage.color =
                HeaderColor;

            headerImage.raycastTarget =
                false;

            GameObject titleObject =
                ListUiFactory.CreateObject(
                    _header,
                    "Title");

            RectTransform titleRect =
                titleObject.GetComponent<
                    RectTransform>();

            titleRect.anchorMin =
                new Vector2(
                    0f,
                    0f);

            titleRect.anchorMax =
                new Vector2(
                    1f,
                    1f);

            titleRect.offsetMin =
                new Vector2(
                    16f,
                    0f);

            titleRect.offsetMax =
                new Vector2(
                    -108f,
                    0f);

            _title =
                titleObject.AddComponent<Text>();

            _title.font =
                ListUiFactory.Font;

            _title.fontSize =
                23;

            _title.fontStyle =
                FontStyle.Bold;

            _title.color =
                Color.white;

            _title.alignment =
                TextAnchor.MiddleLeft;

            _title.raycastTarget =
                false;

            GameObject statusObject =
                ListUiFactory.CreateObject(
                    _header,
                    "Status");

            RectTransform statusRect =
                statusObject.GetComponent<
                    RectTransform>();

            statusRect.anchorMin =
                new Vector2(
                    0f,
                    0f);

            statusRect.anchorMax =
                new Vector2(
                    1f,
                    1f);

            statusRect.offsetMin =
                new Vector2(
                    160f,
                    0f);

            statusRect.offsetMax =
                new Vector2(
                    -108f,
                    0f);

            _status =
                statusObject.AddComponent<Text>();

            _status.font =
                ListUiFactory.Font;

            _status.fontSize =
                13;

            _status.color =
                MutedTextColor;

            _status.alignment =
                TextAnchor.MiddleRight;

            _status.raycastTarget =
                false;

            _settingsButton =
                ListUiFactory.CreateIconButton(
                    _header,
                    "Settings",
                    ListUiFactory.GearSprite,
                    delegate
                    {
                        ToggleSettings();
                    });

            RectTransform settingsRect =
                _settingsButton
                    .GetComponent<RectTransform>();

            settingsRect.anchorMin =
                new Vector2(
                    1f,
                    0.5f);

            settingsRect.anchorMax =
                new Vector2(
                    1f,
                    0.5f);

            settingsRect.pivot =
                new Vector2(
                    1f,
                    0.5f);

            settingsRect.sizeDelta =
                new Vector2(
                    44f,
                    44f);

            settingsRect.anchoredPosition =
                new Vector2(
                    -52f,
                    0f);

            _closeButton =
                ListUiFactory.CreateButton(
                    _header,
                    "Close",
                    "×",
                    delegate
                    {
                        Hide();
                    },
                    28);

            RectTransform closeRect =
                _closeButton
                    .GetComponent<RectTransform>();

            closeRect.anchorMin =
                new Vector2(
                    1f,
                    0.5f);

            closeRect.anchorMax =
                new Vector2(
                    1f,
                    0.5f);

            closeRect.pivot =
                new Vector2(
                    1f,
                    0.5f);

            closeRect.sizeDelta =
                new Vector2(
                    44f,
                    44f);

            closeRect.anchoredPosition =
                new Vector2(
                    -6f,
                    0f);
        }

        private void CreatePlayersArea()
        {
            GameObject scrollObject =
                ListUiFactory.CreateObject(
                    _window,
                    "Players");

            RectTransform scrollRectTransform =
                scrollObject.GetComponent<RectTransform>();

            scrollRectTransform.anchorMin =
                Vector2.zero;

            scrollRectTransform.anchorMax =
                Vector2.one;

            scrollRectTransform.offsetMin =
                new Vector2(10f, 10f);

            scrollRectTransform.offsetMax =
                new Vector2(-10f, -HeaderHeight - 42f);

            Image viewportImage =
                scrollObject.AddComponent<Image>();

            viewportImage.sprite =
                ListUiFactory.WhiteSprite;

            viewportImage.color =
                new Color(0f, 0f, 0f, 0f);

            viewportImage.raycastTarget =
                true;

            scrollObject.AddComponent<RectMask2D>();

            _scrollRect =
                scrollObject.AddComponent<ScrollRect>();

            _scrollRect.horizontal =
                false;

            _scrollRect.vertical =
                true;

            _scrollRect.movementType =
                ScrollRect.MovementType.Clamped;

            _scrollRect.scrollSensitivity =
                35f;

            GameObject contentObject =
                ListUiFactory.CreateObject(
                    scrollObject.transform,
                    "Content");

            _playersRoot =
                contentObject.GetComponent<RectTransform>();

            _playersRoot.anchorMin =
                new Vector2(0f, 1f);

            _playersRoot.anchorMax =
                new Vector2(1f, 1f);

            _playersRoot.pivot =
                new Vector2(0.5f, 1f);

            _playersRoot.anchoredPosition =
                Vector2.zero;

            _playersRoot.sizeDelta =
                new Vector2(0f, scrollRectTransform.rect.height);

            _scrollRect.viewport =
                scrollRectTransform;

            _scrollRect.content =
                _playersRoot;

            _teamSortButton =
                ListUiFactory.CreateButton(
                    _window,
                    "TeamSort",
                    "Sort: by ID",
                    ToggleTeamSort,
                    13);

            RectTransform sortRect =
                _teamSortButton.GetComponent<RectTransform>();
            sortRect.anchorMin = new Vector2(0f, 1f);
            sortRect.anchorMax = new Vector2(0f, 1f);
            sortRect.pivot = new Vector2(0f, 1f);
            sortRect.sizeDelta = new Vector2(164f, 26f);
            sortRect.anchoredPosition = new Vector2(10f, -HeaderHeight - 16f);
        }

        private void ToggleTeamSort()
        {
            _sortByTeams = !_sortByTeams;
            if (_teamSortButton != null)
            {
                Text label = _teamSortButton.GetComponentInChildren<Text>();
                if (label != null)
                    label.text = _sortByTeams ? "Sort: by team" : "Sort: by ID";
            }

            RebuildPlayers();
        }

        private static int GetTeamSortOrder(Hkmp.Game.Team team)
        {
            switch (team)
            {
                case Hkmp.Game.Team.Moss: return 0;
                case Hkmp.Game.Team.Hive: return 1;
                case Hkmp.Game.Team.Grimm: return 2;
                case Hkmp.Game.Team.Lifeblood: return 3;
                default: return 4;
            }
        }

        private void CreateSettingsArea()
        {
            GameObject settingsObject =
                ListUiFactory.CreateObject(
                    _window,
                    "Settings");

            _settingsRoot =
                settingsObject.GetComponent<
                    RectTransform>();

            _settingsRoot.anchorMin =
                Vector2.zero;

            _settingsRoot.anchorMax =
                Vector2.one;

            _settingsRoot.offsetMin =
                new Vector2(
                    16f,
                    16f);

            _settingsRoot.offsetMax =
                new Vector2(
                    -16f,
                    -HeaderHeight - 16f);

            Image background =
                settingsObject.AddComponent<Image>();

            background.sprite =
                ListUiFactory.WhiteSprite;

            background.color =
                new Color(
                    0.045f,
                    0.045f,
                    0.058f,
                    1f);

            background.raycastTarget =
                true;

            _settingsHeading =
                ListUiFactory.CreateText(
                    _settingsRoot,
                    "Heading",
                    "SETTINGS",
                    24,
                    Color.white);

            Text heading = _settingsHeading;

            RectTransform headingRect =
                heading.rectTransform;

            headingRect.anchorMin =
                new Vector2(
                    0f,
                    1f);

            headingRect.anchorMax =
                new Vector2(
                    0f,
                    1f);

            headingRect.pivot =
                new Vector2(
                    0f,
                    1f);

            headingRect.sizeDelta =
                new Vector2(
                    140f,
                    40f);

            headingRect.anchoredPosition =
                new Vector2(
                    10f,
                    0f);

            _generalSettingsTabButton =
                ListUiFactory.CreateButton(
                    _settingsRoot,
                    "GeneralSettings",
                    "General",
                    SelectGeneralSettingsTab,
                    15);

            ConfigureSettingsHeaderButton(
                _generalSettingsTabButton,
                116f,
                100f);

            _adminSettingsButton =
                ListUiFactory.CreateButton(
                    _settingsRoot,
                    "AdminSettings",
                    "Admin",
                    SelectAdminSettingsTab,
                    15);

            ConfigureSettingsHeaderButton(
                _adminSettingsButton,
                8f,
                100f);

            _teamSettingsButton =
                ListUiFactory.CreateButton(
                    _settingsRoot,
                    "TeamSettings",
                    "Teams",
                    SelectTeamSettingsTab,
                    15);

            ConfigureSettingsHeaderButton(
                _teamSettingsButton,
                224f,
                100f);

            _roundsSettingsButton =
                ListUiFactory.CreateButton(
                    _settingsRoot,
                    "RoundsSettings",
                    "Rounds",
                    SelectRoundsSettingsTab,
                    15);

            ConfigureSettingsHeaderButton(
                _roundsSettingsButton,
                332f,
                100f);
            _roundsSettingsButton.gameObject.SetActive(false);

            GameObject settingsScrollObject =
                ListUiFactory.CreateObject(
                    _settingsRoot,
                    "SettingsScroll");

            RectTransform settingsViewport =
                settingsScrollObject.GetComponent<RectTransform>();

            settingsViewport.anchorMin =
                Vector2.zero;

            settingsViewport.anchorMax =
                Vector2.one;

            settingsViewport.offsetMin =
                new Vector2(8f, 48f);

            settingsViewport.offsetMax =
                new Vector2(-8f, -50f);

            Image settingsViewportImage =
                settingsScrollObject.AddComponent<Image>();

            settingsViewportImage.sprite =
                ListUiFactory.WhiteSprite;

            settingsViewportImage.color =
                new Color(0f, 0f, 0f, 0f);

            settingsViewportImage.raycastTarget =
                true;

            settingsScrollObject.AddComponent<RectMask2D>();

            _settingsScrollRect =
                settingsScrollObject.AddComponent<ScrollRect>();

            _settingsScrollRect.horizontal =
                false;

            _settingsScrollRect.vertical =
                true;

            _settingsScrollRect.movementType =
                ScrollRect.MovementType.Clamped;

            _settingsScrollRect.scrollSensitivity =
                30f;

            _settingsScrollRect.viewport =
                settingsViewport;

            GameObject settingsContentObject =
                ListUiFactory.CreateObject(
                    settingsViewport,
                    "SettingsContent");

            _settingsContent =
                settingsContentObject.GetComponent<RectTransform>();

            _settingsContent.anchorMin =
                new Vector2(0f, 1f);

            _settingsContent.anchorMax =
                new Vector2(1f, 1f);

            _settingsContent.pivot =
                new Vector2(0.5f, 1f);

            _settingsContent.anchoredPosition =
                Vector2.zero;

            _settingsContent.sizeDelta =
                new Vector2(0f, 386f);

            _settingsScrollRect.content =
                _settingsContent;

            Text previewHeading =
                ListUiFactory.CreateText(
                    _settingsContent,
                    "PreviewHeading",
                    "Skin preview",
                    17,
                    AccentColor);

            RectTransform previewHeadingRect =
                previewHeading.rectTransform;

            previewHeadingRect.anchorMin =
                new Vector2(
                    0f,
                    1f);

            previewHeadingRect.anchorMax =
                new Vector2(
                    0f,
                    1f);

            previewHeadingRect.pivot =
                new Vector2(
                    0f,
                    1f);

            previewHeadingRect.sizeDelta =
                new Vector2(
                    240f,
                    32f);

            previewHeadingRect.anchoredPosition =
                new Vector2(
                    10f,
                    -4f);

            _sidePreviewButton =
                ListUiFactory.CreateButton(
                    _settingsContent,
                    "Side",
                    "Side",
                    delegate
                    {
                        SetPreviewMode(
                            ListSkinPreviewMode.Side);
                    },
                    17);

            ConfigureSettingsButton(
                _sidePreviewButton,
                10f,
                -42f,
                150f);

            _benchPreviewButton =
                ListUiFactory.CreateButton(
                    _settingsContent,
                    "Bench",
                    "Bench",
                    delegate
                    {
                        SetPreviewMode(
                            ListSkinPreviewMode.Bench);
                    },
                    17);

            ConfigureSettingsButton(
                _benchPreviewButton,
                170f,
                -42f,
                150f);

            Text windowInfo =
                ListUiFactory.CreateText(
                    _settingsContent,
                    "WindowInfo",
                    "Move: drag the header.  Resize: drag any window edge or corner.",
                    15,
                    MutedTextColor);

            RectTransform windowInfoRect =
                windowInfo.rectTransform;

            windowInfoRect.anchorMin =
                new Vector2(
                    0f,
                    1f);

            windowInfoRect.anchorMax =
                new Vector2(
                    1f,
                    1f);

            windowInfoRect.pivot =
                new Vector2(
                    0.5f,
                    1f);

            windowInfoRect.sizeDelta =
                new Vector2(
                    0f,
                    50f);

            windowInfoRect.anchoredPosition =
                new Vector2(
                    0f,
                    -92f);

            windowInfo.alignment =
                TextAnchor.UpperLeft;

            _resetWindowButton =
                ListUiFactory.CreateButton(
                    _settingsContent,
                    "ResetWindow",
                    "Reset window position and size",
                    ResetWindow,
                    16);

            ConfigureResponsiveSettingsButton(
                _resetWindowButton,
                0f,
                0.68f,
                10f,
                8f,
                -154f);

            ConfigureButtonTextToFit(
                _resetWindowButton,
                9,
                16);

            _reloadSkinsButton =
                ListUiFactory.CreateButton(
                    _settingsContent,
                    "ReloadSkins",
                    "Reload skins",
                    ReloadSkins,
                    16);

            ConfigureResponsiveSettingsButton(
                _reloadSkinsButton,
                0.70f,
                1f,
                6f,
                10f,
                -154f);

            ConfigureButtonTextToFit(
                _reloadSkinsButton,
                9,
                16);

            _resetAllSettingsButton =
                ListUiFactory.CreateButton(
                    _settingsContent,
                    "ResetAllSettings",
                    "Reset List settings to defaults",
                    ResetAllSettings,
                    16);

            ConfigureResponsiveSettingsButton(
                _resetAllSettingsButton,
                0f,
                1f,
                10f,
                10f,
                -198f);

            ConfigureButtonTextToFit(
                _resetAllSettingsButton,
                9,
                16);

            Text keyHeading =
                ListUiFactory.CreateText(
                    _settingsContent,
                    "KeyHeading",
                    "Open / close key",
                    17,
                    AccentColor);

            RectTransform keyHeadingRect =
                keyHeading.rectTransform;

            keyHeadingRect.anchorMin =
                new Vector2(
                    0f,
                    1f);

            keyHeadingRect.anchorMax =
                new Vector2(
                    0f,
                    1f);

            keyHeadingRect.pivot =
                new Vector2(
                    0f,
                    1f);

            keyHeadingRect.sizeDelta =
                new Vector2(
                    240f,
                    32f);

            keyHeadingRect.anchoredPosition =
                new Vector2(
                    10f,
                    -260f);

            _keyButton =
                ListUiFactory.CreateButton(
                    _settingsContent,
                    "Key",
                    "",
                    BeginKeyCapture,
                    16);

            ConfigureSettingsButton(
                _keyButton,
                10f,
                -298f,
                310f);

            Text keyHelp =
                ListUiFactory.CreateText(
                    _settingsContent,
                    "KeyHelp",
                    "Press Escape while choosing a key to cancel.",
                    14,
                    MutedTextColor);

            RectTransform keyHelpRect =
                keyHelp.rectTransform;

            keyHelpRect.anchorMin =
                new Vector2(
                    0f,
                    1f);

            keyHelpRect.anchorMax =
                new Vector2(
                    1f,
                    1f);

            keyHelpRect.pivot =
                new Vector2(
                    0.5f,
                    1f);

            keyHelpRect.offsetMin =
                new Vector2(
                    10f,
                    -374f);

            keyHelpRect.offsetMax =
                new Vector2(
                    -10f,
                    -346f);

            _settingsStatus =
                ListUiFactory.CreateText(
                    _settingsRoot,
                    "Status",
                    "",
                    14,
                    MutedTextColor);

            RectTransform statusRect =
                _settingsStatus.rectTransform;

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
                    42f);

            statusRect.anchoredPosition =
                new Vector2(
                    0f,
                    10f);

            UpdateKeyButton();
            UpdateSettingsTabLayout(true);
            RefreshSettingsVisuals();

            _settingsRoot.gameObject.SetActive(
                false);
            ApplyViewVisibility();
        }

        private static void ConfigureButtonTextToFit(
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

            label.resizeTextForBestFit =
                false;

            label.fontSize =
                Mathf.Max(
                    minimumFontSize,
                    maximumFontSize);

            label.horizontalOverflow =
                HorizontalWrapMode.Wrap;

            label.verticalOverflow =
                VerticalWrapMode.Truncate;

            label.alignment =
                TextAnchor.MiddleCenter;
        }

        private static void ConfigureResponsiveSettingsButton(
            Button button,
            float anchorMinX,
            float anchorMaxX,
            float left,
            float right,
            float y)
        {
            if (button == null)
            {
                return;
            }

            RectTransform rect =
                button.GetComponent<
                    RectTransform>();

            rect.anchorMin =
                new Vector2(
                    anchorMinX,
                    1f);

            rect.anchorMax =
                new Vector2(
                    anchorMaxX,
                    1f);

            rect.pivot =
                new Vector2(
                    0f,
                    1f);

            rect.offsetMin =
                new Vector2(
                    left,
                    y - 42f);

            rect.offsetMax =
                new Vector2(
                    -right,
                    y);
        }

        private void UpdateSettingsTabLayout(bool force)
        {
            if (_settingsRoot == null)
                return;

            float width = _settingsRoot.rect.width;
            if (width <= 1f)
                return;

            bool roundsAvailable = ListRoundsIntegration.IsAvailable;
            if (!force &&
                _settingsHeaderLayoutInitialized &&
                Mathf.Abs(width - _lastSettingsHeaderWidth) < 1f &&
                roundsAvailable == _lastSettingsHeaderRoundsAvailable)
            {
                return;
            }

            bool compact = width < 600f;
            float buttonWidth = compact ? 80f : 100f;
            float gap = compact ? 4f : 8f;

            float adminRight = 8f;
            float generalRight = adminRight + buttonWidth + gap;
            float teamRight = generalRight + buttonWidth + gap;
            float roundsRight = teamRight + buttonWidth + gap;

            ConfigureSettingsHeaderButton(
                _adminSettingsButton,
                adminRight,
                buttonWidth);

            ConfigureSettingsHeaderButton(
                _generalSettingsTabButton,
                generalRight,
                buttonWidth);

            ConfigureSettingsHeaderButton(
                _teamSettingsButton,
                teamRight,
                buttonWidth);

            ConfigureSettingsHeaderButton(
                _roundsSettingsButton,
                roundsRight,
                buttonWidth);

            float leftmostRightOffset = roundsAvailable
                ? roundsRight
                : teamRight;

            float leftmostTabLeft = width -
                leftmostRightOffset -
                buttonWidth;

            _settingsHeadingFits = leftmostTabLeft >= 148f;
            UpdateSettingsHeadingVisibility();

            _lastSettingsHeaderWidth = width;
            _lastSettingsHeaderRoundsAvailable = roundsAvailable;
            _settingsHeaderLayoutInitialized = true;
        }

        private void UpdateSettingsHeadingVisibility()
        {
            if (_settingsHeading == null)
                return;

            bool adminSelected =
                _adminSettingsPanel != null &&
                _adminSettingsPanel.IsVisible;

            bool teamSelected =
                _teamSettingsPanel != null &&
                _teamSettingsPanel.IsVisible;

            bool roundsSelected =
                _roundsSettingsPanel != null &&
                _roundsSettingsPanel.IsVisible;

            _settingsHeading.gameObject.SetActive(
                _settingsVisible &&
                !adminSelected &&
                !teamSelected &&
                !roundsSelected &&
                _settingsHeadingFits);
        }

        private static void ConfigureSettingsHeaderButton(
            Button button,
            float rightOffset,
            float width)
        {
            if (button == null)
            {
                return;
            }

            RectTransform rect =
                button.GetComponent<RectTransform>();

            rect.anchorMin =
                new Vector2(1f, 1f);

            rect.anchorMax =
                new Vector2(1f, 1f);

            rect.pivot =
                new Vector2(1f, 1f);

            rect.sizeDelta =
                new Vector2(width, 42f);

            rect.anchoredPosition =
                new Vector2(-rightOffset, -1f);
        }

        private static void ConfigureSettingsButton(
            Button button,
            float x,
            float y,
            float width)
        {
            RectTransform rect =
                button.GetComponent<
                    RectTransform>();

            rect.anchorMin =
                new Vector2(
                    0f,
                    1f);

            rect.anchorMax =
                new Vector2(
                    0f,
                    1f);

            rect.pivot =
                new Vector2(
                    0f,
                    1f);

            rect.sizeDelta =
                new Vector2(
                    width,
                    42f);

            rect.anchoredPosition =
                new Vector2(
                    x,
                    y);
        }

        private void CreateDragAndResizeHandles()
        {
            GameObject dragObject =
                ListUiFactory.CreateObject(
                    _header,
                    "DragHandle");

            RectTransform dragRect =
                dragObject.GetComponent<
                    RectTransform>();

            dragRect.anchorMin =
                new Vector2(
                    0f,
                    0f);

            dragRect.anchorMax =
                new Vector2(
                    1f,
                    1f);

            dragRect.offsetMin =
                new Vector2(
                    0f,
                    0f);

            dragRect.offsetMax =
                new Vector2(
                    -100f,
                    0f);

            Image dragImage =
                dragObject.AddComponent<Image>();

            dragImage.sprite =
                ListUiFactory.WhiteSprite;

            dragImage.color =
                Color.clear;

            dragImage.raycastTarget =
                true;

            ListDragHandle drag =
                dragObject.AddComponent<
                    ListDragHandle>();

            drag.Initialize(
                this,
                _window);

            CreateResizeHandle(
                ListResizeHandle.Direction.Left);

            CreateResizeHandle(
                ListResizeHandle.Direction.Right);

            CreateResizeHandle(
                ListResizeHandle.Direction.Top);

            CreateResizeHandle(
                ListResizeHandle.Direction.Bottom);

            CreateResizeHandle(
                ListResizeHandle.Direction.TopLeft);

            CreateResizeHandle(
                ListResizeHandle.Direction.TopRight);

            CreateResizeHandle(
                ListResizeHandle.Direction.BottomLeft);

            CreateResizeHandle(
                ListResizeHandle.Direction.BottomRight);
        }

        private void CreateResizeHandle(
            ListResizeHandle.Direction direction)
        {
            GameObject handle =
                ListUiFactory.CreateObject(
                    _window,
                    "Resize_" +
                    direction);

            RectTransform rect =
                handle.GetComponent<
                    RectTransform>();

            ConfigureResizeRect(
                rect,
                direction);

            Image image =
                handle.AddComponent<Image>();

            image.sprite =
                ListUiFactory.WhiteSprite;

            image.color =
                Color.clear;

            image.raycastTarget =
                true;

            ListResizeHandle component =
                handle.AddComponent<
                    ListResizeHandle>();

            component.Initialize(
                this,
                _window,
                _canvasRect,
                direction);
        }

        private static void ConfigureResizeRect(
            RectTransform rect,
            ListResizeHandle.Direction direction)
        {
            const float edge = 7f;

            switch (direction)
            {
                case ListResizeHandle.Direction.Left:
                    rect.anchorMin =
                        new Vector2(
                            0f,
                            0f);

                    rect.anchorMax =
                        new Vector2(
                            0f,
                            1f);

                    rect.pivot =
                        new Vector2(
                            0.5f,
                            0.5f);

                    rect.sizeDelta =
                        new Vector2(
                            ResizeHandleSize,
                            0f);

                    rect.anchoredPosition =
                        Vector2.zero;
                    break;

                case ListResizeHandle.Direction.Right:
                    rect.anchorMin =
                        new Vector2(
                            1f,
                            0f);

                    rect.anchorMax =
                        new Vector2(
                            1f,
                            1f);

                    rect.pivot =
                        new Vector2(
                            0.5f,
                            0.5f);

                    rect.sizeDelta =
                        new Vector2(
                            ResizeHandleSize,
                            0f);

                    rect.anchoredPosition =
                        Vector2.zero;
                    break;

                case ListResizeHandle.Direction.Top:
                    rect.anchorMin =
                        new Vector2(
                            0f,
                            1f);

                    rect.anchorMax =
                        new Vector2(
                            1f,
                            1f);

                    rect.pivot =
                        new Vector2(
                            0.5f,
                            0.5f);

                    rect.sizeDelta =
                        new Vector2(
                            0f,
                            ResizeHandleSize);

                    rect.anchoredPosition =
                        Vector2.zero;
                    break;

                case ListResizeHandle.Direction.Bottom:
                    rect.anchorMin =
                        new Vector2(
                            0f,
                            0f);

                    rect.anchorMax =
                        new Vector2(
                            1f,
                            0f);

                    rect.pivot =
                        new Vector2(
                            0.5f,
                            0.5f);

                    rect.sizeDelta =
                        new Vector2(
                            0f,
                            ResizeHandleSize);

                    rect.anchoredPosition =
                        Vector2.zero;
                    break;

                case ListResizeHandle.Direction.TopLeft:
                    rect.anchorMin =
                        new Vector2(
                            0f,
                            1f);

                    rect.anchorMax =
                        new Vector2(
                            0f,
                            1f);

                    rect.pivot =
                        new Vector2(
                            0.5f,
                            0.5f);

                    rect.sizeDelta =
                        new Vector2(
                            ResizeHandleSize,
                            ResizeHandleSize);

                    rect.anchoredPosition =
                        new Vector2(
                            edge,
                            -edge);
                    break;

                case ListResizeHandle.Direction.TopRight:
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
                            0.5f,
                            0.5f);

                    rect.sizeDelta =
                        new Vector2(
                            ResizeHandleSize,
                            ResizeHandleSize);

                    rect.anchoredPosition =
                        new Vector2(
                            -edge,
                            -edge);
                    break;

                case ListResizeHandle.Direction.BottomLeft:
                    rect.anchorMin =
                        new Vector2(
                            0f,
                            0f);

                    rect.anchorMax =
                        new Vector2(
                            0f,
                            0f);

                    rect.pivot =
                        new Vector2(
                            0.5f,
                            0.5f);

                    rect.sizeDelta =
                        new Vector2(
                            ResizeHandleSize,
                            ResizeHandleSize);

                    rect.anchoredPosition =
                        new Vector2(
                            edge,
                            edge);
                    break;

                case ListResizeHandle.Direction.BottomRight:
                    rect.anchorMin =
                        new Vector2(
                            1f,
                            0f);

                    rect.anchorMax =
                        new Vector2(
                            1f,
                            0f);

                    rect.pivot =
                        new Vector2(
                            0.5f,
                            0.5f);

                    rect.sizeDelta =
                        new Vector2(
                            ResizeHandleSize,
                            ResizeHandleSize);

                    rect.anchoredPosition =
                        new Vector2(
                            -edge,
                            edge);
                    break;
            }
        }

        private void RebuildPlayers()
        {
            if (_rebuildingRows ||
                _playersRoot == null)
            {
                return;
            }

            _rebuildingRows = true;

            try
            {
                ClearRows();

                if (_state == null)
                {
                    CreateEmptyStateRow(
                        "Waiting for HKMP.List...");

                    SetPlayersContentHeight(80f);
                    return;
                }

                IReadOnlyList<ListPlayerView> playerSnapshot =
                    _state.GetPlayers();

                List<ListPlayerView> players =
                    playerSnapshot == null
                        ? new List<ListPlayerView>()
                        : playerSnapshot.Where(player => player != null).ToList();

                if (_sortByTeams)
                {
                    players = players
                        .OrderBy(player => GetTeamSortOrder(player.Team))
                        .ThenByDescending(player => player.IsTeamLeader)
                        .ThenBy(player => player.Username, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(player => player.Id)
                        .ToList();
                }

                UpdateTitle();

                if (players.Count == 0)
                {
                    string message;

                    try
                    {
                        message =
                            _addon != null &&
                            _addon.Api != null &&
                            _addon.Api.NetClient != null &&
                            _addon.Api.NetClient.IsConnected
                                ? "No players connected."
                                : "Connect to an HKMP server to see players.";
                    }
                    catch
                    {
                        message =
                            "Connect to an HKMP server to see players.";
                    }

                    CreateEmptyStateRow(
                        message);

                    SetPlayersContentHeight(80f);
                    return;
                }

                float offset = 0f;

                for (int index = 0;
                     index < players.Count;
                     index++)
                {
                    float height =
                        CreatePlayerRow(
                            players[index],
                            index,
                            offset);

                    offset +=
                        height +
                        RowSpacing;
                }

                float contentHeight =
                    Math.Max(
                        0f,
                        offset - RowSpacing);

                SetPlayersContentHeight(
                    contentHeight);

                Canvas.ForceUpdateCanvases();

                LayoutRebuilder.ForceRebuildLayoutImmediate(
                    _playersRoot);

                _scrollRect.verticalNormalizedPosition =
                    1f;
            }
            finally
            {
                _rebuildingRows = false;
            }
        }

        private void SetPlayersContentHeight(
            float height)
        {
            if (_playersRoot == null)
            {
                return;
            }

            float viewportHeight =
                _scrollRect == null ||
                _scrollRect.viewport == null
                    ? 0f
                    : _scrollRect.viewport.rect.height;

            float finalHeight =
                Mathf.Max(
                    height,
                    viewportHeight);

            _playersRoot.SetSizeWithCurrentAnchors(
                RectTransform.Axis.Vertical,
                finalHeight);

        }

        private void ClearRows()
        {
            foreach (GameObject row in _rowObjects)
            {
                if (row != null)
                {
                    Destroy(
                        row);
                }
            }

            _rowObjects.Clear();
            _noteFields.Clear();
            _skinIdFields.Clear();
            _adminIdentityNoteFields.Clear();
            _skinImages.Clear();
        }

        private void CreateEmptyStateRow(
            string message)
        {
            GameObject row =
                ListUiFactory.CreateObject(
                    _playersRoot,
                    "Empty");

            RectTransform rowRect =
                row.GetComponent<RectTransform>();

            ConfigureRowRect(
                rowRect,
                0f,
                80f);

            Text text =
                ListUiFactory.CreateText(
                    row.transform,
                    "Text",
                    message,
                    18,
                    MutedTextColor,
                    TextAnchor.MiddleCenter);

            ListUiFactory.SetFullStretch(
                text.rectTransform,
                8f,
                8f,
                8f,
                8f);

            _rowObjects.Add(
                row);
        }

        private static void ConfigureRowRect(
            RectTransform rowRect,
            float topOffset,
            float height)
        {
            rowRect.anchorMin =
                new Vector2(0f, 1f);

            rowRect.anchorMax =
                new Vector2(1f, 1f);

            rowRect.pivot =
                new Vector2(0.5f, 1f);

            rowRect.anchoredPosition =
                new Vector2(0f, -topOffset);

            rowRect.sizeDelta =
                new Vector2(0f, height);
        }

        private float CreatePlayerRow(
            ListPlayerView player,
            int index,
            float topOffset)
        {
            if (player == null)
            {
                return 0f;
            }

            bool isAdminViewer =
                _state != null &&
                (_state.IsAuthorized || _state.IsHost);

            bool isLocalPlayer =
                IsLocalPlayerRow(player);

            bool canModeratePlayer =
                isAdminViewer &&
                !player.IsHost &&
                !isLocalPlayer;

            bool canManageAllTeams =
                _state != null &&
                (_state.IsHost ||
                 (_state.IsAuthorized &&
                  _state.Permissions != null &&
                  _state.Permissions.CanManageTeamLeaders));

            bool canKickOwnTeamMember =
                _state != null &&
                _state.IsLocalTeamLeader &&
                player.Team == _state.LocalTeam &&
                !player.IsTeamLeader &&
                !player.IsAuthorized &&
                !player.IsHost &&
                !isLocalPlayer;

            bool canKickFromTeam =
                !player.IsHost &&
                !isLocalPlayer &&
                IsPlayableTeam(player.Team) &&
                (canManageAllTeams || canKickOwnTeamMember);

            bool canKickPlayer =
                canModeratePlayer &&
                (_state.IsHost ||
                 (_state.Permissions != null &&
                  _state.Permissions.CanKickPlayers));

            bool canBanPlayer =
                canModeratePlayer &&
                (_state.IsHost ||
                 (_state.Permissions != null &&
                  _state.Permissions.CanBanPlayers));

            bool canExpandPlayer =
                isAdminViewer ||
                (_state != null &&
                 (_state.IsLocalTeamLeader || canManageAllTeams));

            bool expanded =
                canExpandPlayer &&
                _expandedAdminRows.Contains(player.Id);

            float rowHeight =
                expanded
                    ? isAdminViewer
                        ? ExpandedRowHeight
                        : CompactExpandedRowHeight
                    : RowHeight;

            GameObject row =
                ListUiFactory.CreateObject(
                    _playersRoot,
                    "Player_" + player.Id);

            RectTransform rowRect =
                row.GetComponent<RectTransform>();

            ConfigureRowRect(
                rowRect,
                topOffset,
                rowHeight);

            Image rowImage =
                row.AddComponent<Image>();

            rowImage.sprite =
                ListUiFactory.WhiteSprite;

            rowImage.color =
                index % 2 == 0
                    ? RowColor
                    : RowAlternateColor;

            rowImage.raycastTarget =
                false;

            GameObject skinObject =
                ListUiFactory.CreateObject(
                    row.transform,
                    "Skin");

            RectTransform skinRect =
                skinObject.GetComponent<RectTransform>();

            skinRect.anchorMin =
                expanded
                    ? new Vector2(0f, 1f)
                    : new Vector2(0f, 0.5f);

            skinRect.anchorMax =
                expanded
                    ? new Vector2(0f, 1f)
                    : new Vector2(0f, 0.5f);

            skinRect.pivot =
                expanded
                    ? new Vector2(0.5f, 1f)
                    : new Vector2(0.5f, 0.5f);

            skinRect.sizeDelta =
                expanded
                    ? new Vector2(64f, 64f)
                    : new Vector2(52f, 52f);

            skinRect.anchoredPosition =
                expanded
                    ? new Vector2(43f, -8f)
                    : new Vector2(43f, 0f);

            Image skinImage =
                skinObject.AddComponent<Image>();

            skinImage.raycastTarget =
                false;

            skinImage.preserveAspect =
                true;

            byte displayedSkinId =
                GetDisplayedSkinId(player);

            Sprite preview =
                ListSkinPreviewProvider.GetPreview(
                    _addon == null ? null : _addon.Api,
                    displayedSkinId,
                    _previewMode);

            skinImage.sprite = preview;

            _skinImages[player.Id] =
                skinImage;

            if (player.IsTeamLeader)
            {
                Text crown = ListUiFactory.CreateText(
                    row.transform,
                    "TeamLeaderCrown",
                    "♛",
                    15,
                    new Color(1f, 0.82f, 0.22f, 1f),
                    TextAnchor.MiddleCenter);
                RectTransform crownRect = crown.rectTransform;
                crownRect.anchorMin = new Vector2(0f, 1f);
                crownRect.anchorMax = new Vector2(0f, 1f);
                crownRect.pivot = new Vector2(0.5f, 1f);
                crownRect.sizeDelta = new Vector2(24f, 16f);
                crownRect.anchoredPosition = new Vector2(43f, expanded ? 8f : -1f);
                crown.transform.SetAsLastSibling();
            }

            InputField skinIdField =
                ListUiFactory.CreateInputField(
                    row.transform,
                    "SkinId",
                    GetSkinIdInputValue(player),
                    "ID",
                    10,
                    value =>
                        OnPlayerSkinIdChanged(
                            player,
                            value,
                            false));

            skinIdField.characterLimit = 3;
            skinIdField.lineType = InputField.LineType.SingleLine;
            skinIdField.contentType = InputField.ContentType.IntegerNumber;
            skinIdField.interactable = !(
                player.HasTeamSkin && player.IsTeamSkinLocked);

            RectTransform skinIdRect =
                skinIdField.GetComponent<RectTransform>();

            skinIdRect.anchorMin =
                expanded
                    ? new Vector2(0f, 1f)
                    : new Vector2(0f, 0f);

            skinIdRect.anchorMax =
                expanded
                    ? new Vector2(0f, 1f)
                    : new Vector2(0f, 0f);

            skinIdRect.pivot =
                expanded
                    ? new Vector2(0.5f, 1f)
                    : new Vector2(0.5f, 0f);

            skinIdRect.sizeDelta =
                new Vector2(48f, 19f);

            skinIdRect.anchoredPosition =
                expanded
                    ? new Vector2(43f, -76f)
                    : new Vector2(43f, 0f);

            skinIdField.onEndEdit.AddListener(
                value =>
                    OnPlayerSkinIdChanged(
                        player,
                        value,
                        true));

            _skinIdFields[player.Id] =
                skinIdField;

            float actionWidth = 10f;
            if (isAdminViewer)
            {
                if (canExpandPlayer)
                    actionWidth = Math.Max(actionWidth, 72f);
                if (canBanPlayer)
                    actionWidth = Math.Max(actionWidth, 106f);
                if (canKickPlayer)
                    actionWidth = Math.Max(actionWidth, 140f);
                if (canKickFromTeam)
                    actionWidth = Math.Max(actionWidth, 174f);
            }
            else
            {
                if (canExpandPlayer)
                    actionWidth = Math.Max(actionWidth, 82f);
                if (canKickFromTeam)
                    actionWidth = Math.Max(
                        actionWidth,
                        canExpandPlayer ? 116f : 48f);
            }

            Text name =
                ListUiFactory.CreateText(
                    row.transform,
                    "Name",
                    player.Username,
                    20,
                    GetTeamColor(player.Team),
                    TextAnchor.MiddleLeft);

            RectTransform nameRect =
                name.rectTransform;

            nameRect.anchorMin =
                expanded
                    ? new Vector2(0f, 1f)
                    : new Vector2(0f, 0f);

            nameRect.anchorMax =
                expanded
                    ? new Vector2(0f, 1f)
                    : new Vector2(0f, 1f);

            nameRect.pivot =
                expanded
                    ? new Vector2(0f, 1f)
                    : new Vector2(0f, 0.5f);

            nameRect.sizeDelta =
                expanded
                    ? new Vector2(isAdminViewer ? 174f : 190f, 40f)
                    : new Vector2(isAdminViewer ? 174f : 190f, -16f);

            nameRect.anchoredPosition =
                expanded
                    ? new Vector2(86f, -8f)
                    : new Vector2(86f, 0f);

            Text id =
                ListUiFactory.CreateText(
                    row.transform,
                    "Id",
                    "ID " + player.Id,
                    12,
                    MutedTextColor,
                    TextAnchor.LowerLeft);

            RectTransform idRect =
                id.rectTransform;

            idRect.anchorMin =
                expanded
                    ? new Vector2(0f, 1f)
                    : new Vector2(0f, 0f);

            idRect.anchorMax =
                expanded
                    ? new Vector2(0f, 1f)
                    : new Vector2(0f, 0f);

            idRect.pivot =
                expanded
                    ? new Vector2(0f, 1f)
                    : new Vector2(0f, 0f);

            idRect.sizeDelta =
                new Vector2(160f, 18f);

            idRect.anchoredPosition =
                expanded
                    ? new Vector2(86f, -51f)
                    : new Vector2(86f, 8f);

            float noteRight = actionWidth;

            InputField note =
                ListUiFactory.CreateInputField(
                    row.transform,
                    "Note",
                    _state.GetNote(player.Id),
                    "Note about this player...",
                    15,
                    value =>
                        OnLocalNoteChanged(
                            player.Id,
                            value));

            RectTransform noteRect =
                note.GetComponent<RectTransform>();

            noteRect.anchorMin =
                expanded
                    ? new Vector2(0f, 1f)
                    : new Vector2(0f, 0.5f);

            noteRect.anchorMax =
                expanded
                    ? new Vector2(1f, 1f)
                    : new Vector2(1f, 0.5f);

            noteRect.offsetMin =
                expanded
                    ? new Vector2(288f, -78f)
                    : new Vector2(288f, -35f);

            noteRect.offsetMax =
                expanded
                    ? new Vector2(-noteRight, -8f)
                    : new Vector2(-noteRight, 35f);

            _noteFields[player.Id] =
                note;

            if (isAdminViewer)
            {
                if (canKickFromTeam)
                {
                    CreateAdminRowActionButton(
                        row.transform,
                        "TeamKick",
                        "T",
                        -136f,
                        () => SendTeamKick(player.Id));
                }

                if (canKickPlayer)
                {
                    CreateAdminRowActionButton(
                        row.transform, "Kick", "K", -102f,
                        () => SendKick(player.Id));
                }

                if (canBanPlayer)
                {
                    CreateAdminRowActionButton(
                        row.transform, "Ban", "B", -68f,
                        () => SendBan(player.Id));
                }

                CreateAdminRowActionButton(
                    row.transform,
                    "Expand",
                    expanded ? "▲" : "▼",
                    -34f,
                    () => ToggleAdminRow(player.Id));

                if (expanded)
                {
                    CreateAdminIdentityDetails(
                        row.transform,
                        player);
                    CreateTeamLeaderManagementControl(
                        row.transform,
                        player,
                        isAdminViewer ? -248f : -108f);
                    CreatePlayerTeamAssignmentControl(
                        row.transform,
                        player,
                        isAdminViewer ? -278f : -108f);
                }
            }
            else
            {
                if (canKickFromTeam)
                {
                    CreateAdminRowActionButton(
                        row.transform,
                        "TeamKick",
                        "T",
                        canExpandPlayer ? -78f : -10f,
                        () => SendTeamKick(player.Id));
                }

                if (canExpandPlayer)
                {
                    CreateAdminRowActionButton(
                        row.transform,
                        "Expand",
                        expanded ? "▲" : "▼",
                        -44f,
                        () => ToggleAdminRow(player.Id));
                }

                if (expanded)
                {
                    CreateTeamLeaderManagementControl(
                        row.transform,
                        player,
                        isAdminViewer ? -248f : -108f);
                    CreatePlayerTeamAssignmentControl(
                        row.transform,
                        player,
                        isAdminViewer ? -278f : -108f);
                }
            }

            _rowObjects.Add(
                row);

            return rowHeight;
        }

        private void CreateTeamLeaderManagementControl(
            Transform parent,
            ListPlayerView player,
            float topOffset)
        {
            if (_state == null ||
                player == null ||
                player.IsHost ||
                IsLocalPlayerRow(player) ||
                !IsPlayableTeam(player.Team))
            {
                return;
            }

            bool canManageAllTeams =
                _state.IsHost ||
                (_state.IsAuthorized &&
                 _state.Permissions != null &&
                 _state.Permissions.CanManageTeamLeaders);
            bool canManageOwnTeam =
                _state.IsLocalTeamLeader &&
                _state.LocalTeam == player.Team;

            if (!canManageAllTeams && !canManageOwnTeam)
            {
                return;
            }

            if (!canManageAllTeams && (player.IsAuthorized || player.IsTeamLeader))
            {
                return;
            }

            bool isLeader = player.IsTeamLeader;
            Button button = ListUiFactory.CreateButton(
                parent,
                "TeamLeaderManagement",
                isLeader ? "Remove team leader" : "Appoint as " + player.Team + " leader",
                delegate
                {
                    if (_addon == null)
                    {
                        return;
                    }

                    try
                    {
                        if (player.IsTeamLeader)
                        {
                            _addon.RemoveTeamLeader(player.Id);
                        }
                        else
                        {
                            _addon.AssignTeamLeader(player.Id);
                        }

                        SetSettingsStatus(isLeader
                            ? "Team leader removal requested."
                            : "Team leader appointment requested.");
                    }
                    catch
                    {
                        SetSettingsStatus("Team leader action could not be sent.");
                    }
                },
                12);

            RectTransform rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(276f, 25f);
            rect.anchoredPosition = new Vector2(8f, topOffset);
        }

        private void CreatePlayerTeamAssignmentControl(
            Transform parent,
            ListPlayerView player,
            float topOffset)
        {
            if (_state == null ||
                player == null ||
                player.IsHost ||
                IsLocalPlayerRow(player) ||
                !IsPlayableTeam(_state.LocalTeam) ||
                player.Team == _state.LocalTeam)
            {
                return;
            }

            bool canManageAllTeams =
                _state.IsHost ||
                (_state.IsAuthorized &&
                 _state.Permissions != null &&
                 _state.Permissions.CanManageTeamLeaders);
            bool canManageOwnTeam = _state.IsLocalTeamLeader;
            if (!canManageAllTeams && !canManageOwnTeam)
            {
                return;
            }

            Hkmp.Game.Team targetTeam = _state.LocalTeam;
            Button button = ListUiFactory.CreateButton(
                parent,
                "AssignPlayerToMyTeam",
                "Add to " + targetTeam + " team",
                delegate
                {
                    if (_addon == null)
                    {
                        return;
                    }

                    try
                    {
                        _addon.AssignPlayerToTeam(player.Id, targetTeam);
                        SetSettingsStatus("Team assignment requested.");
                    }
                    catch (Exception exception)
                    {
                        SetSettingsStatus("Team assignment could not be sent: " + exception.Message);
                    }
                },
                12);

            RectTransform rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(276f, 25f);
            rect.anchoredPosition = new Vector2(8f, topOffset);
        }

        private static bool IsPlayableTeam(Hkmp.Game.Team team)
        {
            return team == Hkmp.Game.Team.Moss ||
                   team == Hkmp.Game.Team.Hive ||
                   team == Hkmp.Game.Team.Grimm ||
                   team == Hkmp.Game.Team.Lifeblood;
        }

        private void SendTeamKick(ushort playerId)
        {
            if (_addon == null)
                return;

            try
            {
                _addon.KickTeamMember(playerId);
                SetSettingsStatus("Team kick request sent.");
            }
            catch
            {
                SetSettingsStatus("Team kick request could not be sent.");
            }
        }

        private void CreateAdminRowActionButton(
            Transform parent,
            string name,
            string caption,
            float x,
            Action action)
        {
            Button button =
                ListUiFactory.CreateButton(
                    parent,
                    name,
                    caption,
                    delegate
                    {
                        if (action != null)
                        {
                            action();
                        }
                    },
                    13);

            RectTransform rect =
                button.GetComponent<RectTransform>();

            rect.anchorMin =
                new Vector2(1f, 0f);

            rect.anchorMax =
                new Vector2(1f, 0f);

            rect.pivot =
                new Vector2(1f, 0f);

            rect.sizeDelta =
                new Vector2(30f, 30f);

            rect.anchoredPosition =
                new Vector2(x, 9f);
        }

        private void SendKick(
            ushort playerId)
        {
            if (_addon == null)
            {
                return;
            }

            try
            {
                _addon.KickPlayer(
                    playerId);
            }
            catch
            {
                SetSettingsStatus(
                    "Kick request could not be sent.");
            }
        }

        private void SendBan(
            ushort playerId)
        {
            if (_addon == null)
            {
                return;
            }

            try
            {
                _addon.BanPlayer(
                    playerId);
            }
            catch
            {
                SetSettingsStatus(
                    "Ban request could not be sent.");
            }
        }

        private void ToggleAdminRow(
            ushort playerId)
        {
            if (_state == null ||
                (!_state.IsAuthorized && !_state.IsHost && !_state.IsLocalTeamLeader))
            {
                return;
            }

            if (_expandedAdminRows.Contains(
                    playerId))
            {
                _expandedAdminRows.Remove(
                    playerId);
            }
            else
            {
                _expandedAdminRows.Add(
                    playerId);
            }

            RebuildPlayers();
        }

        private void CreateAdminIdentityDetails(
            Transform parent,
            ListPlayerView player)
        {
            ListAdminPlayerView adminPlayer;

            if (_state == null ||
                !_state.TryGetAdminPlayer(
                    player.Id,
                    out adminPlayer) ||
                adminPlayer == null)
            {
                CreateAdminDetailText(
                    parent,
                    "Identity",
                    "Player identity data is unavailable.",
                    11,
                    MutedTextColor,
                    8f,
                    -106f,
                    330f,
                    24f);

                return;
            }

            string identityCaption =
                adminPlayer.IsAuthorized
                    ? "Copy authorized.json key"
                    : "Copy auth identity key";

            CreateAdminIdentityCopyButton(
                parent,
                identityCaption,
                adminPlayer.AuthorizationKey);

            CreateAdminDetailText(
                parent,
                "Fingerprint",
                string.IsNullOrWhiteSpace(
                        adminPlayer.AuthorizationFingerprint)
                    ? "Fingerprint unavailable"
                    : adminPlayer.AuthorizationFingerprint,
                11,
                AccentColor,
                8f,
                -132f,
                340f,
                20f);

            if (!adminPlayer.IsAuthorized)
            {
                CreateAdminDetailText(
                    parent,
                    "IdentityStatus",
                    "This identity is not an HKMP administrator identity.",
                    10,
                    MutedTextColor,
                    8f,
                    -156f,
                    340f,
                    18f);
            }

            CreateAdminDetailText(
                parent,
                "AuthorizationNoteLabel",
                "NOTE FOR THIS PLAYER IDENTITY",
                9,
                MutedTextColor,
                8f,
                -176f,
                340f,
                15f);

            bool canEditIdentityNote =
                _state != null &&
                (_state.IsAuthorized || _state.IsHost) &&
                _state.Permissions != null &&
                _state.Permissions.CanManageIdentityData &&
                !string.IsNullOrWhiteSpace(
                    adminPlayer.AuthorizationFingerprint);

            if (canEditIdentityNote)
            {
                string fingerprint =
                    adminPlayer.AuthorizationFingerprint;

                InputField identityNote =
                    ListUiFactory.CreateInputField(
                        parent,
                        "AuthorizationNote",
                        adminPlayer.AuthorizationNote ?? string.Empty,
                        "Note about this player identity...",
                        10,
                        value =>
                            OnAdminIdentityNoteChanged(
                                fingerprint,
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
                    new Vector2(340f, 44f);
                identityNoteRect.anchoredPosition =
                    new Vector2(8f, -193f);

                _adminIdentityNoteFields[fingerprint] =
                    identityNote;
            }
            else
            {
                CreateAdminDetailText(
                    parent,
                    "AuthorizationNote",
                    string.IsNullOrWhiteSpace(adminPlayer.AuthorizationNote)
                        ? "No note has been saved for this player identity."
                        : adminPlayer.AuthorizationNote,
                    10,
                    TextColor,
                    8f,
                    -193f,
                    340f,
                    44f);
            }

            bool canManageAdminStatus =
                _state != null &&
                _state.IsHost &&
                !adminPlayer.IsHost &&
                adminPlayer.IsAuthorized &&
                ListClientNotesStore.IsValidFingerprint(
                    adminPlayer.AuthorizationFingerprint);

            if (canManageAdminStatus)
            {
                string fingerprint = adminPlayer.AuthorizationFingerprint;
                Button roleButton = ListUiFactory.CreateButton(
                    parent,
                    "AdministratorAccess",
                    adminPlayer.IsAdminEnabled
                        ? "Demote administrator"
                        : "Restore administrator access",
                    delegate
                    {
                        if (_addon == null || !_state.IsHost)
                        {
                            return;
                        }

                        try
                        {
                            if (adminPlayer.IsAdminEnabled)
                            {
                                _addon.DemoteAdministrator(fingerprint);
                            }
                            else
                            {
                                _addon.RestoreAdministrator(fingerprint);
                            }
                        }
                        catch (Exception exception)
                        {
                            SetSettingsStatus(
                                "Administrator role could not be changed: " + exception.Message);
                        }
                    },
                    11);

                RectTransform roleRect = roleButton.GetComponent<RectTransform>();
                roleRect.anchorMin = new Vector2(0f, 1f);
                roleRect.anchorMax = new Vector2(0f, 1f);
                roleRect.pivot = new Vector2(0f, 1f);
                roleRect.sizeDelta = new Vector2(340f, 23f);
                roleRect.anchoredPosition = new Vector2(8f, -242f);
            }
        }

        private void CreateAdminPermissionToggle(
            Transform parent,
            string name,
            string caption,
            string fingerprint,
            AdminPermissions current,
            Func<AdminPermissions, bool> getter,
            Action<AdminPermissions, bool> setter,
            float y,
            bool canEdit)
        {
            AdminPermissions snapshot = (current ?? AdminPermissions.NoAccess()).Clone();
            bool enabled = getter(snapshot);
            Button button = ListUiFactory.CreateButton(
                parent, name, caption + ": " + (enabled ? "ON" : "OFF"),
                delegate
                {
                    if (!canEdit || _addon == null) return;
                    AdminPermissions updated = snapshot.Clone();
                    setter(updated, !enabled);
                    try
                    {
                        _addon.SetAdminPermissions(fingerprint, updated);
                        SetSettingsStatus(caption + " permission updated.");
                    }
                    catch
                    {
                        SetSettingsStatus("Administrator permission could not be changed.");
                    }
                }, 10);

            RectTransform rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(235f, 22f);
            rect.anchoredPosition = new Vector2(88f, y);
            button.interactable = canEdit;
        }

        private void CreateAdminDetailText(
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
            Text text =
                ListUiFactory.CreateText(
                    parent,
                    name,
                    value,
                    fontSize,
                    color,
                    TextAnchor.UpperLeft);

            RectTransform rect =
                text.rectTransform;

            rect.anchorMin =
                new Vector2(0f, 1f);

            rect.anchorMax =
                new Vector2(0f, 1f);

            rect.pivot =
                new Vector2(0f, 1f);

            rect.sizeDelta =
                new Vector2(width, height);

            rect.anchoredPosition =
                new Vector2(x, y);
        }

        private void CreateAdminIdentityCopyButton(
            Transform parent,
            string caption,
            string authorizationKey)
        {
            Button button = null;

            button =
                ListUiFactory.CreateButton(
                    parent,
                    "CopyAuthorizationKey",
                    caption,
                    delegate
                    {
                        if (string.IsNullOrWhiteSpace(authorizationKey))
                        {
                            SetSettingsStatus(
                                "This player did not provide an authorization key.");
                            return;
                        }

                        try
                        {
                            GUIUtility.systemCopyBuffer = authorizationKey;
                            SetSettingsStatus(
                                string.Equals(
                                    GUIUtility.systemCopyBuffer,
                                    authorizationKey,
                                    StringComparison.Ordinal)
                                    ? "authorized.json authorization key copied to clipboard."
                                    : "The clipboard did not accept the authorization key.");
                        }
                        catch (Exception ex)
                        {
                            SetSettingsStatus(
                                "Could not copy the authorization key: " + ex.Message);
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
                new Vector2(330f, 22f);

            rect.anchoredPosition =
                new Vector2(8f, -106f);

            Image buttonBackground =
                button.GetComponent<Image>();

            Image rowBackground =
                parent == null
                    ? null
                    : parent.GetComponent<Image>();

            Color normalButtonColor =
                rowBackground != null
                    ? rowBackground.color
                    : RowColor;
            if (buttonBackground != null)
            {
                buttonBackground.color = Color.white;
            }

            ColorBlock buttonColors =
                button.colors;

            buttonColors.normalColor =
                normalButtonColor;

            buttonColors.highlightedColor =
                new Color(
                    Mathf.Clamp01(normalButtonColor.r + 0.045f),
                    Mathf.Clamp01(normalButtonColor.g + 0.045f),
                    Mathf.Clamp01(normalButtonColor.b + 0.045f),
                    normalButtonColor.a);

            buttonColors.pressedColor =
                new Color(
                    Mathf.Max(0f, normalButtonColor.r - 0.018f),
                    Mathf.Max(0f, normalButtonColor.g - 0.018f),
                    Mathf.Max(0f, normalButtonColor.b - 0.018f),
                    normalButtonColor.a);

            buttonColors.disabledColor =
                normalButtonColor;

            buttonColors.colorMultiplier = 1f;

            button.colors =
                buttonColors;

            Text buttonLabel =
                ButtonText(button);

            if (buttonLabel != null)
            {
                buttonLabel.alignment =
                    TextAnchor.MiddleLeft;

                buttonLabel.fontSize = 11;
                buttonLabel.color = Color.white;
                buttonLabel.resizeTextForBestFit = false;
                buttonLabel.horizontalOverflow =
                    HorizontalWrapMode.Overflow;

                buttonLabel.verticalOverflow =
                    VerticalWrapMode.Truncate;

                buttonLabel.supportRichText = false;
            }
        }


        private static Text ButtonText(
            Button button)
        {
            return button == null
                ? null
                : button.GetComponentInChildren<Text>();
        }

        private bool IsLocalPlayerRow(
            ListPlayerView player)
        {
            if (player == null)
            {
                return false;
            }

            ushort localPlayerId;

            if (_state != null &&
                _state.TryGetLocalPlayerId(
                    out localPlayerId))
            {
                return player.Id == localPlayerId;
            }

            if (_addon == null ||
                _addon.Api == null ||
                _addon.Api.ClientManager == null)
            {
                return false;
            }

            try
            {
                string localUsername =
                    _addon.Api.ClientManager.Username;

                return
                    !string.IsNullOrWhiteSpace(localUsername) &&
                    string.Equals(
                        player.Username == null ? string.Empty : player.Username.Trim(),
                        localUsername.Trim(),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void OnPlayerSkinIdChanged(
            ListPlayerView player,
            string value,
            bool commit)
        {
            if (_rebuildingRows ||
                player == null ||
                ListMod.GlobalSettings == null)
            {
                return;
            }

            bool teamSkinLockedForPlayer =
                player.HasTeamSkin &&
                player.IsTeamSkinLocked;
            if (teamSkinLockedForPlayer)
            {
                if (commit)
                {
                    UpdateSkinIdFieldText(
                        player.Id,
                        player.TeamSkinId.ToString(CultureInfo.InvariantCulture));
                    UpdateSkinPreview(player.Id, player.TeamSkinId);
                }
                return;
            }

            ListGlobalSettings settings =
                ListMod.GlobalSettings;

            string text = value ?? string.Empty;
            byte skinId;

            if (IsLocalPlayerRow(player))
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (!commit)
                    {
                        return;
                    }

                    if (_addon != null)
                    {
                        _addon.ClearPreferredSkinOverride(
                            player.SkinId);
                    }
                    else
                    {
                        if (!settings.HasPreferredSkinId)
                        {
                            settings.PreferredSkinId = player.SkinId;
                            settings.HasPreferredSkinId = true;
                        }

                        settings.HasListSkinOverride = false;
                        settings.SkinPreferenceUpdatedAtUtcTicks = DateTime.UtcNow.Ticks;
                    }

                    byte fallbackSkinId =
                        settings.HasPreferredSkinId
                            ? settings.PreferredSkinId
                            : player.SkinId;

                    UpdateSkinPreview(
                        player.Id,
                        fallbackSkinId);

                    UpdateSkinIdFieldText(
                        player.Id,
                        fallbackSkinId.ToString(
                            CultureInfo.InvariantCulture));

                    return;
                }

                if (!byte.TryParse(
                        text.Trim(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out skinId))
                {
                    if (commit)
                    {
                        UpdateSkinIdFieldText(
                            player.Id,
                            GetSkinIdInputValue(player));
                    }

                    return;
                }

                UpdateSkinPreview(
                    player.Id,
                    skinId);

                if (commit)
                {
                    if (_addon != null)
                    {
                        _addon.SetPreferredSkinOverride(
                            skinId,
                            player.SkinId);
                    }
                    else
                    {
                        if (!settings.HasPreferredSkinId)
                        {
                            settings.PreferredSkinId = player.SkinId;
                            settings.HasPreferredSkinId = true;
                        }

                        settings.ListSkinOverrideId = skinId;
                        settings.HasListSkinOverride = true;
                        settings.SkinPreferenceUpdatedAtUtcTicks = DateTime.UtcNow.Ticks;
                    }

                    UpdateSkinIdFieldText(
                        player.Id,
                        skinId.ToString(
                            CultureInfo.InvariantCulture));
                }

                return;
            }

            string key =
                GetPlayerSkinOverrideKey(player);

            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                settings.PlayerSkinOverrides.Remove(key);
                if (settings.PlayerSkinOverrideUpdatedAtUtcTicks != null)
                    settings.PlayerSkinOverrideUpdatedAtUtcTicks.Remove(key);
                UpdateSkinPreview(
                    player.Id,
                    GetDisplayedSkinId(player));
                UpdateSkinIdFieldText(player.Id, GetSkinIdInputValue(player));
                return;
            }

            if (!byte.TryParse(
                    text.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out skinId))
            {
                if (commit)
                {
                    UpdateSkinIdFieldText(
                        player.Id,
                        GetSkinIdInputValue(player));
                }

                return;
            }

            settings.PlayerSkinOverrides[key] = skinId;
            if (settings.PlayerSkinOverrideUpdatedAtUtcTicks == null)
                settings.PlayerSkinOverrideUpdatedAtUtcTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long overrideTicks = DateTime.UtcNow.Ticks;
            long previousTicks;
            if (settings.PlayerSkinOverrideUpdatedAtUtcTicks.TryGetValue(key, out previousTicks) && previousTicks >= overrideTicks)
                overrideTicks = previousTicks + 1;
            settings.PlayerSkinOverrideUpdatedAtUtcTicks[key] = overrideTicks;
            UpdateSkinPreview(
                player.Id,
                skinId);

            if (commit)
            {
                UpdateSkinIdFieldText(
                    player.Id,
                    skinId.ToString(
                        CultureInfo.InvariantCulture));
            }
        }

        private byte GetDisplayedSkinId(
            ListPlayerView player)
        {
            if (player == null)
                return 0;

            ListGlobalSettings settings = ListMod.GlobalSettings;
            if (settings == null)
                return SkinPolicyResolver.Resolve(
                    player.SkinId,
                    player.HasTeamSkin,
                    player.TeamSkinId,
                    player.IsTeamSkinLocked,
                    player.TeamSkinUpdatedAtUtcTicks,
                    player.HasListSkinOverride || player.ListSkinUpdatedAtUtcTicks > 0,
                    player.HasListSkinOverride ? player.ListSkinOverrideId : player.SkinId,
                    player.ListSkinUpdatedAtUtcTicks,
                    false,
                    0,
                    0,
                    player.IsTeamLeader);

            bool isLocal = IsLocalPlayerRow(player);
            bool hasViewerOverride = false;
            byte viewerSkinId = 0;
            long viewerUpdatedAtUtcTicks = 0;
            string key = GetPlayerSkinOverrideKey(player);

            if (!isLocal && !string.IsNullOrWhiteSpace(key) &&
                settings.PlayerSkinOverrides != null)
            {
                hasViewerOverride = settings.PlayerSkinOverrides.TryGetValue(key, out viewerSkinId);
                if (hasViewerOverride && settings.PlayerSkinOverrideUpdatedAtUtcTicks != null)
                    settings.PlayerSkinOverrideUpdatedAtUtcTicks.TryGetValue(key, out viewerUpdatedAtUtcTicks);
            }

            bool hasPersonalPreference = isLocal
                ? settings.HasListSkinOverride || settings.HasPreferredSkinId
                : player.HasListSkinOverride || player.ListSkinUpdatedAtUtcTicks > 0;
            byte personalSkinId = isLocal
                ? (settings.HasListSkinOverride ? settings.ListSkinOverrideId :
                    settings.HasPreferredSkinId ? settings.PreferredSkinId : player.SkinId)
                : (player.HasListSkinOverride ? player.ListSkinOverrideId : player.SkinId);
            long personalUpdatedAtUtcTicks = isLocal
                ? settings.SkinPreferenceUpdatedAtUtcTicks
                : player.ListSkinUpdatedAtUtcTicks;

            return SkinPolicyResolver.Resolve(
                player.SkinId,
                player.HasTeamSkin,
                player.TeamSkinId,
                player.IsTeamSkinLocked,
                player.TeamSkinUpdatedAtUtcTicks,
                hasPersonalPreference,
                personalSkinId,
                personalUpdatedAtUtcTicks,
                hasViewerOverride,
                viewerSkinId,
                viewerUpdatedAtUtcTicks,
                player.IsTeamLeader);
        }

        private string GetSkinIdInputValue(
            ListPlayerView player)
        {
            return player == null
                ? string.Empty
                : GetDisplayedSkinId(player).ToString(CultureInfo.InvariantCulture);
        }

        private static string GetPlayerSkinOverrideKey(
            ListPlayerView player)
        {
            if (player == null ||
                string.IsNullOrWhiteSpace(player.Username))
            {
                return string.Empty;
            }

            return "name:" +
                   player.Username.Trim().ToLowerInvariant();
        }

        private void UpdateSkinIdFieldText(
            ushort playerId,
            string value)
        {
            InputField field;
            if (_skinIdFields.TryGetValue(playerId, out field) &&
                field != null &&
                !string.Equals(
                    field.text,
                    value ?? string.Empty,
                    StringComparison.Ordinal))
            {
                SetInputFieldTextSilently(
                    field,
                    value ?? string.Empty);
            }
        }

        private void UpdateSkinPreview(
            ushort playerId,
            byte skinId)
        {
            Image image;
            if (!_skinImages.TryGetValue(playerId, out image) ||
                image == null)
            {
                return;
            }

            image.sprite =
                ListSkinPreviewProvider.GetPreview(
                    _addon == null ? null : _addon.Api,
                    skinId,
                    _previewMode);
        }

        private void OnAdminIdentityNoteChanged(
            string fingerprint,
            string value)
        {
            if (_rebuildingRows ||
                _addon == null ||
                _state == null ||
                (!_state.IsAuthorized && !_state.IsHost) ||
                _state.Permissions == null ||
                !_state.Permissions.CanManageIdentityData ||
                string.IsNullOrWhiteSpace(fingerprint))
            {
                return;
            }

            try
            {
                _addon.SetAdminIdentityNote(
                    fingerprint,
                    value ?? string.Empty);
            }
            catch (Exception exception)
            {
                SetSettingsStatus(
                    "Could not save player identity note: " +
                    exception.Message);
            }
        }

        private void RefreshAdminIdentityNoteFields()
        {
            if (_state == null ||
                (!_state.IsAuthorized && !_state.IsHost) ||
                _adminIdentityNoteFields.Count == 0)
            {
                return;
            }

            IReadOnlyList<ListAdminPlayerView> players =
                _state.GetAdminPlayers();

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
                if (!_adminIdentityNoteFields.TryGetValue(
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
                    SetInputFieldTextSilently(
                        field,
                        note);
                }
            }
        }

        private void OnLocalNoteChanged(
            ushort playerId,
            string value)
        {
            if (_rebuildingRows)
            {
                return;
            }

            if (_addon == null)
            {
                return;
            }

            string note = value ?? string.Empty;
            if (_state != null)
            {
                _state.SetLocalNote(playerId, note);
            }

            try
            {
                _addon.SetNote(
                    playerId,
                    note);
            }
            catch
            {
                UpdateNoteField(
                    playerId,
                    _state == null
                        ? string.Empty
                        : _state.GetNote(
                            playerId));
            }
        }

        private bool HasFocusedNoteInput()
        {
            if (!_visible)
            {
                return false;
            }

            foreach (InputField field in _noteFields.Values)
            {
                if (field != null && IsInputBeingEdited(field))
                {
                    return true;
                }
            }

            foreach (InputField field in _adminIdentityNoteFields.Values)
            {
                if (field != null && IsInputBeingEdited(field))
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdateNoteField(
            ushort playerId,
            string note)
        {
            InputField field;

            if (!_noteFields.TryGetValue(
                    playerId,
                    out field) ||
                field == null ||
                IsInputBeingEdited(field))
            {
                return;
            }

            string value =
                note ?? string.Empty;

            if (field.text ==
                value)
            {
                return;
            }

            SetInputFieldTextSilently(
                field,
                value);
        }

        private void SetInputFieldTextSilently(
            InputField field,
            string value)
        {
            if (field == null)
            {
                return;
            }

            string normalized =
                value ?? string.Empty;

            if (string.Equals(
                    field.text,
                    normalized,
                    StringComparison.Ordinal))
            {
                return;
            }

            bool wasRebuildingRows =
                _rebuildingRows;

            _rebuildingRows = true;
            try
            {
                field.text = normalized;
            }
            finally
            {
                _rebuildingRows = wasRebuildingRows;
            }
        }

        private void UpdateTitle()
        {
            if (_title == null)
            {
                return;
            }

            int count =
                _state == null
                    ? 0
                    : _state.GetPlayers().Count;

            _title.text =
                "HKMP.List";

            if (_status != null)
            {
                _status.text =
                    count == 0
                        ? ""
                        : count +
                          (
                              count == 1
                                  ? " player"
                                  : " players");
            }
        }

        private void ApplyViewVisibility()
        {
            bool showWindow = _visible && _window != null;
            bool showSettings = showWindow && _settingsVisible;
            bool showPlayers = showWindow && !_settingsVisible;

            if (_settingsRoot != null)
            {
                _settingsRoot.gameObject.SetActive(showSettings);
                if (showSettings && _settingsRoot.parent != null &&
                    _settingsRoot.GetSiblingIndex() != _settingsRoot.parent.childCount - 1)
                {
                    _settingsRoot.SetAsLastSibling();
                }
            }

            if (_teamSortButton != null)
            {
                _teamSortButton.gameObject.SetActive(showPlayers);
            }

            if (_scrollRect != null)
            {
                _scrollRect.gameObject.SetActive(showPlayers);
            }
        }

        private void ToggleSettings()
        {
            _settingsVisible =
                !_settingsVisible;

            ApplyViewVisibility();

            _capturingKey = false;

            if (!_settingsVisible &&
                _adminSettingsPanel != null)
            {
                _adminSettingsPanel.Hide();
            }

            if (!_settingsVisible && _teamSettingsPanel != null)
            {
                _teamSettingsPanel.Hide();
            }
            if (!_settingsVisible && _roundsSettingsPanel != null)
            {
                _roundsSettingsPanel.Hide();
            }

            if (_settingsScrollRect != null)
            {
                bool showGeneralSettings =
                    _settingsVisible &&
                    (_adminSettingsPanel == null || !_adminSettingsPanel.IsVisible) &&
                    (_teamSettingsPanel == null || !_teamSettingsPanel.IsVisible) &&
                    (_roundsSettingsPanel == null || !_roundsSettingsPanel.IsVisible);

                _settingsScrollRect.gameObject.SetActive(
                    showGeneralSettings);
            }

            if (_settingsVisible)
            {
                if (_settingsScrollRect != null)
                {
                    _settingsScrollRect.verticalNormalizedPosition =
                        1f;
                }

                RefreshSettingsVisuals();
            }

            UpdateTitle();
        }

        private void SelectGeneralSettingsTab()
        {
            if (_adminSettingsPanel != null)
            {
                _adminSettingsPanel.Hide();
            }

            if (_teamSettingsPanel != null)
            {
                _teamSettingsPanel.Hide();
            }
            if (_roundsSettingsPanel != null)
            {
                _roundsSettingsPanel.Hide();
            }

            if (_settingsScrollRect != null)
            {
                _settingsScrollRect.gameObject.SetActive(
                    _settingsVisible);
            }

            RefreshSettingsVisuals();
        }

        private void SelectAdminSettingsTab()
        {
            bool connected =
                _addon != null &&
                _addon.IsConnected;

            if (connected &&
                (_state == null || (!_state.IsAuthorized && !_state.IsHost)))
            {
                return;
            }

            if (_adminSettingsPanel == null)
            {
                _adminSettingsPanel =
                    new ListAdminSettingsPanel(
                        _addon,
                        delegate
                        {
                            RefreshSettingsVisuals();
                        });
            }

            if (_teamSettingsPanel != null)
            {
                _teamSettingsPanel.Hide();
            }
            if (_roundsSettingsPanel != null)
            {
                _roundsSettingsPanel.Hide();
            }

            if (_settingsScrollRect != null)
            {
                _settingsScrollRect.gameObject.SetActive(false);
            }

            _adminSettingsPanel.Show(
                _settingsRoot);

            RefreshSettingsVisuals();
        }

        private void SelectTeamSettingsTab()
        {
            bool connected = _addon != null && _addon.IsConnected;
            if (connected && _state == null)
                return;

            if (_adminSettingsPanel != null)
                _adminSettingsPanel.Hide();
            if (_roundsSettingsPanel != null)
                _roundsSettingsPanel.Hide();

            if (_settingsScrollRect != null)
                _settingsScrollRect.gameObject.SetActive(false);

            if (_teamSettingsPanel == null)
            {
                _teamSettingsPanel = new ListTeamSettingsPanel(_addon);
            }

            _teamSettingsPanel.Show(_settingsRoot);
            RefreshSettingsVisuals();
        }

        private void SelectRoundsSettingsTab()
        {
            if (!ListRoundsIntegration.IsAvailable)
                return;

            if (_adminSettingsPanel != null)
                _adminSettingsPanel.Hide();
            if (_teamSettingsPanel != null)
                _teamSettingsPanel.Hide();
            if (_settingsScrollRect != null)
                _settingsScrollRect.gameObject.SetActive(false);

            if (_roundsSettingsPanel == null)
                _roundsSettingsPanel = new ListRoundsSettingsPanel(ListRoundsIntegration.Entries);
            else
                _roundsSettingsPanel.SetEntries(ListRoundsIntegration.Entries);
            _roundsSettingsPanel.Show(_settingsRoot);
            RefreshSettingsVisuals();
        }

        private void OnRoundsAvailabilityChanged()
        {
            lock (_pendingGate)
            {
                _settingsDirty = true;
            }

            if (_roundsSettingsPanel != null)
            {
                if (ListRoundsIntegration.IsAvailable)
                {
                    _roundsSettingsPanel.SetEntries(
                        ListRoundsIntegration.Entries);
                }
                else if (_roundsSettingsPanel.IsVisible)
                {
                    _roundsSettingsPanel.Hide();
                    if (_settingsScrollRect != null)
                    {
                        _settingsScrollRect.gameObject.SetActive(
                            _settingsVisible);
                    }
                }
            }

            UpdateSettingsTabLayout(true);
            RefreshSettingsVisuals();
        }

        private void ToggleAdminSettings()
        {
            SelectAdminSettingsTab();
        }

        private void SetPreviewMode(
            ListSkinPreviewMode mode)
        {
            _previewMode =
                mode;

            ListMod.GlobalSettings.SkinPreviewMode =
                mode;

            ListSkinPreviewProvider.ClearCache();

            RefreshSettingsVisuals();

            RebuildPlayers();
        }

        private void ResetWindow()
        {
            ListMod.GlobalSettings.ResetWindow();

            ApplyStoredWindowRect();

            SetSettingsStatus(
                "Window position and size reset.");
        }

        private void ResetAllSettings()
        {
            ListGlobalSettings settings =
                ListMod.GlobalSettings ?? new ListGlobalSettings();

            byte fallbackSkinId = settings.HasPreferredSkinId
                ? settings.PreferredSkinId
                : (settings.HasListSkinOverride ? settings.ListSkinOverrideId : (byte)0);

            if (_addon != null)
            {
                _addon.ClearPreferredSkinOverride(fallbackSkinId);
            }
            else
            {
                settings.ListSkinOverrideId = 0;
                settings.HasListSkinOverride = false;
            }

            settings.ToggleKey = KeyCode.F7;
            settings.ResetWindow();
            settings.SkinPreviewMode = ListSkinPreviewMode.Side;
            settings.PlayerSkinOverrides =
                new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            settings.PlayerSkinOverrideUpdatedAtUtcTicks =
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            settings.Normalize();

            ListMod.GlobalSettings = settings;
            _previewMode = ListSkinPreviewMode.Side;
            _capturingKey = false;

            ListSkinPreviewProvider.ClearCache();
            ApplyStoredWindowRect();
            UpdateKeyButton();
            RefreshSettingsVisuals();
            RebuildPlayers();

            SetSettingsStatus(
                "List interface preferences and viewer skin overrides reset. Host-owned notes, bans, administrator permissions, and team settings were preserved.");
        }

        private void ReloadSkins()
        {
            ListSkinPreviewProvider.ClearCache();

            RebuildPlayers();

            SetSettingsStatus(
                "Skin cache reloaded.");
        }

        private void BeginKeyCapture()
        {
            _capturingKey = true;

            if (_keyButtonText != null)
            {
                _keyButtonText.text =
                    "Press a key...";
            }

            SetSettingsStatus(
                "Waiting for a keyboard key.");
        }

        private void UpdateKeyButton()
        {
            if (_keyButton == null)
            {
                return;
            }

            Text text =
                _keyButton.GetComponentInChildren<
                    Text>();

            _keyButtonText =
                text;

            if (_keyButtonText != null)
            {
                _keyButtonText.text =
                    _capturingKey
                        ? "Press a key..."
                        : "Open / close: " +
                          ListUiFactory.GetButtonKeyText(
                              ListMod.GlobalSettings.ToggleKey);
            }
        }

        private void RefreshSettingsVisuals()
        {
            if (_settingsRoot == null)
            {
                return;
            }

            if (ListMod.GlobalSettings == null)
            {
                ListMod.GlobalSettings = new ListGlobalSettings();
            }

            _previewMode = ListMod.GlobalSettings.SkinPreviewMode;
            UpdateKeyButton();

            if (_sidePreviewButton != null)
            {
                SetButtonSelected(
                    _sidePreviewButton,
                    _previewMode == ListSkinPreviewMode.Side);
            }

            if (_benchPreviewButton != null)
            {
                SetButtonSelected(
                    _benchPreviewButton,
                    _previewMode == ListSkinPreviewMode.Bench);
            }

            bool connected = _addon != null && _addon.IsConnected;
            bool authorized = _state != null && (_state.IsAuthorized || _state.IsHost);
            bool localTeamLeader = _state != null && _state.IsLocalTeamLeader;

            bool adminTabAvailable = !connected || authorized;
            bool teamTabAvailable = !connected || _state != null;

            if (!adminTabAvailable && _adminSettingsPanel != null)
                _adminSettingsPanel.Hide();

            if (!teamTabAvailable && _teamSettingsPanel != null)
                _teamSettingsPanel.Hide();

            bool adminSelected =
                _adminSettingsPanel != null && _adminSettingsPanel.IsVisible;
            bool teamSelected =
                _teamSettingsPanel != null && _teamSettingsPanel.IsVisible;
            bool roundsSelected =
                _roundsSettingsPanel != null && _roundsSettingsPanel.IsVisible;

            if (_generalSettingsTabButton != null)
            {
                _generalSettingsTabButton.gameObject.SetActive(_settingsVisible);
                SetButtonSelected(_generalSettingsTabButton, !adminSelected && !teamSelected && !roundsSelected);
            }

            if (_adminSettingsButton != null)
            {
                _adminSettingsButton.gameObject.SetActive(
                    adminTabAvailable && _settingsVisible);
                SetButtonSelected(_adminSettingsButton, authorized && adminSelected);
            }

            if (_teamSettingsButton != null)
            {
                _teamSettingsButton.gameObject.SetActive(
                    teamTabAvailable && _settingsVisible);
                SetButtonSelected(_teamSettingsButton, teamSelected);
            }

            if (_roundsSettingsButton != null)
            {
                bool roundsAvailable = ListRoundsIntegration.IsAvailable;
                _roundsSettingsButton.gameObject.SetActive(roundsAvailable && _settingsVisible);
                SetButtonSelected(_roundsSettingsButton, roundsSelected);
            }

            if (adminTabAvailable && adminSelected && _adminSettingsPanel != null)
                _adminSettingsPanel.Refresh();

            bool showGeneralSettings =
                _settingsVisible &&
                !adminSelected &&
                !teamSelected &&
                !roundsSelected;

            if (_settingsHeading != null)
                _settingsHeading.gameObject.SetActive(
                    showGeneralSettings && _settingsHeadingFits);

            if (_settingsScrollRect != null)
                _settingsScrollRect.gameObject.SetActive(showGeneralSettings);

            if (_settingsStatus != null)
                _settingsStatus.gameObject.SetActive(showGeneralSettings);

            if (_settingsStatus != null &&
                string.IsNullOrEmpty(_settingsStatus.text))
            {
                _settingsStatus.text = authorized
                    ? "Settings are saved automatically. Admin controls are available."
                    : "Settings are saved automatically.";
            }
        }

        private void SetSettingsStatus(
            string message)
        {
            if (_settingsStatus != null)
            {
                _settingsStatus.text =
                    message;
            }
        }

        private static void SetButtonSelected(
            Button button,
            bool selected)
        {
            if (button == null)
            {
                return;
            }

            Color normal =
                selected
                    ? new Color(
                        0.24f,
                        0.24f,
                        0.28f,
                        1f)
                    : new Color(
                        0.12f,
                        0.12f,
                        0.15f,
                        0.96f);

            Color highlighted =
                selected
                    ? new Color(
                        0.30f,
                        0.30f,
                        0.35f,
                        1f)
                    : new Color(
                        0.20f,
                        0.20f,
                        0.24f,
                        0.98f);

            Color pressed =
                selected
                    ? new Color(
                        0.18f,
                        0.18f,
                        0.22f,
                        1f)
                    : new Color(
                        0.08f,
                        0.08f,
                        0.10f,
                        1f);

            ColorBlock colors =
                button.colors;

            colors.normalColor =
                normal;

            colors.highlightedColor =
                highlighted;

            colors.pressedColor =
                pressed;

            colors.selectedColor =
                highlighted;

            button.colors =
                colors;

            Image background =
                button.GetComponent<Image>();

            if (background != null)
            {
                background.color =
                    normal;
            }
        }

        private void EnsureSettings()
        {
            if (ListMod.GlobalSettings == null)
            {
                ListMod.GlobalSettings =
                    new ListGlobalSettings();
            }

            ListMod.GlobalSettings.Normalize();
        }

        private void ApplyStoredWindowRect()
        {
            if (_window == null)
            {
                return;
            }

            ListGlobalSettings settings =
                ListMod.GlobalSettings ??
                new ListGlobalSettings();

            settings.Normalize();

            Vector2 size =
                new Vector2(
                    settings.WindowWidth,
                    settings.WindowHeight);

            Vector2 position =
                new Vector2(
                    settings.WindowX,
                    settings.WindowY);

            ClampWindow(
                ref position,
                ref size);

            _window.sizeDelta =
                size;

            _window.anchoredPosition =
                position;

            settings.WindowWidth =
                size.x;

            settings.WindowHeight =
                size.y;

            settings.WindowX =
                position.x;

            settings.WindowY =
                position.y;

            ListMod.GlobalSettings =
                settings;
        }

        internal void ApplyWindowRect(
            Vector2 position,
            Vector2 size)
        {
            ClampWindow(
                ref position,
                ref size);

            _window.sizeDelta =
                size;

            _window.anchoredPosition =
                position;
        }

        internal void SaveWindowRect()
        {
            if (_window == null ||
                ListMod.GlobalSettings == null)
            {
                return;
            }

            Vector2 position =
                _window.anchoredPosition;

            Vector2 size =
                _window.sizeDelta;

            ClampWindow(
                ref position,
                ref size);

            ListMod.GlobalSettings.WindowX =
                position.x;

            ListMod.GlobalSettings.WindowY =
                position.y;

            ListMod.GlobalSettings.WindowWidth =
                size.x;

            ListMod.GlobalSettings.WindowHeight =
                size.y;

            ListMod.GlobalSettings.Normalize();
        }

        private void ClampWindow(
            ref Vector2 position,
            ref Vector2 size)
        {
            float screenWidth =
                Mathf.Max(
                    Screen.width,
                    1f);

            float screenHeight =
                Mathf.Max(
                    Screen.height,
                    1f);

            float maximumWidth =
                Mathf.Max(
                    MinimumWindowWidth,
                    Mathf.Min(
                        MaximumWindowWidth,
                        screenWidth -
                        MinimumVisiblePixels * 2f));

            float maximumHeight =
                Mathf.Max(
                    MinimumWindowHeight,
                    Mathf.Min(
                        MaximumWindowHeight,
                        screenHeight -
                        MinimumVisiblePixels * 2f));

            size.x =
                Mathf.Clamp(
                    size.x,
                    MinimumWindowWidth,
                    maximumWidth);

            size.y =
                Mathf.Clamp(
                    size.y,
                    MinimumWindowHeight,
                    maximumHeight);

            float halfWidth =
                screenWidth / 2f;

            float halfHeight =
                screenHeight / 2f;

            float horizontalLimit =
                Mathf.Max(
                    0f,
                    halfWidth -
                    MinimumVisiblePixels +
                    size.x / 2f);

            float verticalLimit =
                Mathf.Max(
                    0f,
                    halfHeight -
                    MinimumVisiblePixels +
                    size.y / 2f);

            position.x =
                Mathf.Clamp(
                    position.x,
                    -horizontalLimit,
                    horizontalLimit);

            position.y =
                Mathf.Clamp(
                    position.y,
                    -verticalLimit,
                    verticalLimit);
        }

        private static Color GetTeamColor(
            Hkmp.Game.Team team)
        {
            switch (team)
            {
                case Hkmp.Game.Team.Moss:
                    return new Color(
                        0f,
                        150f / 255f,
                        0f);

                case Hkmp.Game.Team.Hive:
                    return new Color(
                        200f / 255f,
                        150f / 255f,
                        0f);

                case Hkmp.Game.Team.Grimm:
                    return new Color(
                        250f / 255f,
                        50f / 255f,
                        50f / 255f);

                case Hkmp.Game.Team.Lifeblood:
                    return new Color(
                        50f / 255f,
                        150f / 255f,
                        200f / 255f);

                default:
                    return Color.white;
            }
        }
        private sealed class ListDragHandle :
            MonoBehaviour,
            IBeginDragHandler,
            IDragHandler,
            IEndDragHandler
        {
            private ListUiController _controller;
            private RectTransform _window;
            private Vector2 _offset;

            public void Initialize(
                ListUiController controller,
                RectTransform window)
            {
                _controller =
                    controller;

                _window =
                    window;
            }

            public void OnBeginDrag(
                PointerEventData eventData)
            {
                if (_controller == null ||
                    _window == null)
                {
                    return;
                }

                RectTransform canvas =
                    _controller._canvasRect;

                Vector2 mouse;

                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        canvas,
                        eventData.position,
                        null,
                        out mouse))
                {
                    return;
                }

                _offset =
                    mouse -
                    _window.anchoredPosition;
            }

            public void OnDrag(
                PointerEventData eventData)
            {
                if (_controller == null ||
                    _window == null)
                {
                    return;
                }

                Vector2 mouse;

                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        _controller._canvasRect,
                        eventData.position,
                        null,
                        out mouse))
                {
                    return;
                }

                Vector2 position =
                    mouse -
                    _offset;

                Vector2 size =
                    _window.sizeDelta;

                _controller.ApplyWindowRect(
                    position,
                    size);
            }

            public void OnEndDrag(
                PointerEventData eventData)
            {
                if (_controller != null)
                {
                    _controller.SaveWindowRect();
                }
            }
        }

        private sealed class ListResizeHandle :
            MonoBehaviour,
            IBeginDragHandler,
            IDragHandler,
            IEndDragHandler
        {
            public enum Direction
            {
                Left,
                Right,
                Top,
                Bottom,
                TopLeft,
                TopRight,
                BottomLeft,
                BottomRight
            }

            private ListUiController _controller;
            private RectTransform _window;
            private RectTransform _canvas;
            private Direction _direction;

            private Vector2 _startMouse;
            private Vector2 _startPosition;
            private Vector2 _startSize;

            public void Initialize(
                ListUiController controller,
                RectTransform window,
                RectTransform canvas,
                Direction direction)
            {
                _controller =
                    controller;

                _window =
                    window;

                _canvas =
                    canvas;

                _direction =
                    direction;
            }

            public void OnBeginDrag(
                PointerEventData eventData)
            {
                if (_controller == null ||
                    _window == null ||
                    _canvas == null)
                {
                    return;
                }

                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        _canvas,
                        eventData.position,
                        null,
                        out _startMouse))
                {
                    return;
                }

                _startPosition =
                    _window.anchoredPosition;

                _startSize =
                    _window.sizeDelta;
            }

            public void OnDrag(
                PointerEventData eventData)
            {
                if (_controller == null ||
                    _window == null ||
                    _canvas == null)
                {
                    return;
                }

                Vector2 mouse;

                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        _canvas,
                        eventData.position,
                        null,
                        out mouse))
                {
                    return;
                }

                Vector2 delta =
                    mouse -
                    _startMouse;

                Vector2 position =
                    _startPosition;

                Vector2 size =
                    _startSize;

                bool left =
                    _direction ==
                        Direction.Left ||
                    _direction ==
                        Direction.TopLeft ||
                    _direction ==
                        Direction.BottomLeft;

                bool right =
                    _direction ==
                        Direction.Right ||
                    _direction ==
                        Direction.TopRight ||
                    _direction ==
                        Direction.BottomRight;

                bool top =
                    _direction ==
                        Direction.Top ||
                    _direction ==
                        Direction.TopLeft ||
                    _direction ==
                        Direction.TopRight;

                bool bottom =
                    _direction ==
                        Direction.Bottom ||
                    _direction ==
                        Direction.BottomLeft ||
                    _direction ==
                        Direction.BottomRight;

                if (left)
                {
                    size.x =
                        _startSize.x -
                        delta.x;

                    position.x =
                        _startPosition.x +
                        delta.x / 2f;
                }

                if (right)
                {
                    size.x =
                        _startSize.x +
                        delta.x;

                    position.x =
                        _startPosition.x +
                        delta.x / 2f;
                }

                if (bottom)
                {
                    size.y =
                        _startSize.y -
                        delta.y;

                    position.y =
                        _startPosition.y +
                        delta.y / 2f;
                }

                if (top)
                {
                    size.y =
                        _startSize.y +
                        delta.y;

                    position.y =
                        _startPosition.y +
                        delta.y / 2f;
                }

                _controller.ApplyWindowRect(
                    position,
                    size);
            }

            public void OnEndDrag(
                PointerEventData eventData)
            {
                if (_controller != null)
                {
                    _controller.SaveWindowRect();
                }
            }
        }
    }
}
