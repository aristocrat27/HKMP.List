using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Hkmp.Api.Client;
using HKMP.List.Shared;
using HKMP.List.UI;
using UnityEngine;

namespace HKMP.List.Client
{
    internal sealed class ListSkinPersistence : IDisposable
    {
        private const string SkinChangedEventName = "SkinChangedEvent";
        private const string ServerSettingsChangedEventName = "ServerSettingsChangedEvent";
        private const string NetClientFieldName = "_netClient";
        private const string UpdateManagerPropertyName = "UpdateManager";
        private const string AddPlayerSettingUpdateMethodName = "AddPlayerSettingUpdate";
        private const string PlayerManagerFieldName = "_playerManager";
        private const string PlayerDataFieldName = "_playerData";
        private const string SkinManagerFieldName = "_skinManager";
        private const string UpdatePlayerSkinMethodName = "UpdatePlayerSkin";
        private const string UpdateLocalPlayerSkinMethodName = "UpdateLocalPlayerSkin";
        private const float SkinUpdateRetryIntervalSeconds = 0.75f;

        private readonly object _gate = new object();
        private readonly IClientApi _api;
        private readonly ListClientState _state;
        private readonly object _clientManager;
        private readonly FieldInfo _playerManagerField;
        private readonly FieldInfo _playerDataField;
        private readonly FieldInfo _skinManagerField;
        private readonly MethodInfo _updatePlayerSkinMethod;
        private readonly MethodInfo _updateLocalPlayerSkinMethod;

        private EventInfo _skinChangedEvent;
        private Delegate _skinChangedHandler;
        private EventInfo _serverSettingsChangedEvent;
        private Delegate _serverSettingsChangedHandler;


        private bool _connected;
        private bool _allowSkins;
        private bool _disposed;
        private bool _reportedMissingSkinEvent;
        private bool _reportedMissingSettingsEvent;
        private bool _reportedNetworkBridgeFailure;
        private bool _hasReceivedSkinUpdate;
        private bool _forceRemoteSkinRefresh;
        private byte? _pendingSkinId;
        private byte? _lastObservedSkinId;
        private byte? _cancelledListSkinId;
        private byte? _enforcedLocalTeamSkinId;
        private bool _restoringBaseSkin;
        private byte? _lastAppliedLocalVisualSkinId;
        private GameObject _lastAppliedLocalVisualPlayer;
        private float _lastSkinSubmitTime = float.NegativeInfinity;

        internal event Action<bool, byte, long> SkinPreferenceChanged;
        private int _connectionGeneration;
        private readonly Dictionary<ushort, byte> _appliedRemoteSkinOverrides =
            new Dictionary<ushort, byte>();
        private readonly Dictionary<ushort, object> _appliedRemotePlayerContainers =
            new Dictionary<ushort, object>();
        private readonly Dictionary<ushort, float> _lastRemoteSkinApplyTimes =
            new Dictionary<ushort, float>();

        public ListSkinPersistence(
            IClientApi api,
            ListClientState state)
        {
            if (api == null)
            {
                throw new ArgumentNullException(nameof(api));
            }

            if (api.ClientManager == null)
            {
                throw new ArgumentException(
                    "HKMP client manager is unavailable.",
                    nameof(api));
            }

            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            _api = api;
            _state = state;
            _clientManager = api.ClientManager;
            _playerManagerField = FindField(
                _clientManager.GetType(),
                PlayerManagerFieldName);
            _playerDataField = FindField(
                _clientManager.GetType(),
                PlayerDataFieldName);

            InitializePreferredSkinPreference();

            object playerManager = _playerManagerField == null
                ? null
                : _playerManagerField.GetValue(_clientManager);
            Type playerManagerType = playerManager == null ? null : playerManager.GetType();
            _skinManagerField = FindField(playerManagerType, SkinManagerFieldName);
            object skinManager = _skinManagerField == null || playerManager == null
                ? null
                : _skinManagerField.GetValue(playerManager);
            Type skinManagerType = _skinManagerField != null
                ? _skinManagerField.FieldType
                : skinManager == null ? null : skinManager.GetType();
            _updatePlayerSkinMethod = FindMethodByParameterTypes(
                skinManagerType, UpdatePlayerSkinMethodName, typeof(GameObject), typeof(byte));
            _updateLocalPlayerSkinMethod = FindMethodByParameterTypes(
                skinManagerType, UpdateLocalPlayerSkinMethodName, typeof(byte));

            ListSkinPreviewProvider.CaptureDefaultTexture(api);

            _api.ClientManager.ConnectEvent += OnConnected;
            _api.ClientManager.DisconnectEvent += OnDisconnected;

            AttachSkinChangedEvent();
            AttachServerSettingsChangedEvent();

            _connected =
                _api.NetClient != null &&
                _api.NetClient.IsConnected;

            bool allowSkins;
            _allowSkins =
                TryReadAllowSkins(null, out allowSkins) &&
                allowSkins;

            if (_connected)
            {
                TryApplyPreferredSkin();
            }

        }

        private void InitializePreferredSkinPreference()
        {
            ListGlobalSettings settings = EnsureGlobalSettings();

            if (settings.PreferredSkinId != 0)
            {
                settings.HasPreferredSkinId = true;
            }
        }

        private static ListGlobalSettings EnsureGlobalSettings()
        {
            if (ListMod.GlobalSettings == null)
            {
                ListMod.GlobalSettings = new ListGlobalSettings();
            }

            return ListMod.GlobalSettings;
        }

        public void SetPreferredSkinId(byte skinId)
        {
            ListGlobalSettings settings = EnsureGlobalSettings();
            byte fallbackSkinId = settings.HasPreferredSkinId
                ? settings.PreferredSkinId
                : (_lastObservedSkinId.HasValue
                    ? _lastObservedSkinId.Value
                    : skinId);

            SetPreferredSkinOverride(
                skinId,
                fallbackSkinId);
        }

        public void SetPreferredSkinOverride(
            byte skinId,
            byte fallbackSkinId)
        {
            ListGlobalSettings settings = EnsureGlobalSettings();

            if (!settings.HasPreferredSkinId)
            {
                settings.PreferredSkinId = fallbackSkinId;
                settings.HasPreferredSkinId = true;
            }

            settings.ListSkinOverrideId = skinId;
            settings.HasListSkinOverride = true;
            long updatedAtUtcTicks = StampSkinPreference(settings);
            RaiseSkinPreferenceChanged(true, skinId, updatedAtUtcTicks);

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _restoringBaseSkin = false;
                _cancelledListSkinId = null;
                _lastObservedSkinId = null;
                _lastAppliedLocalVisualSkinId = null;
                _lastAppliedLocalVisualPlayer = null;
                _pendingSkinId = skinId;
                _lastSkinSubmitTime = float.NegativeInfinity;
            }

            TryApplyPreferredSkin();
        }

        public void ClearPreferredSkinOverride(
            byte fallbackSkinId)
        {
            ListGlobalSettings settings = EnsureGlobalSettings();

            bool hadListOverride = settings.HasListSkinOverride;
            byte previousListSkinId = settings.ListSkinOverrideId;

            if (!settings.HasPreferredSkinId)
            {
                settings.PreferredSkinId = fallbackSkinId;
                settings.HasPreferredSkinId = true;
            }

            settings.HasListSkinOverride = false;
            long updatedAtUtcTicks = StampSkinPreference(settings);
            RaiseSkinPreferenceChanged(false, 0, updatedAtUtcTicks);

            byte desiredSkinId = settings.PreferredSkinId;

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _restoringBaseSkin =
                    hadListOverride &&
                    previousListSkinId != desiredSkinId;
                _cancelledListSkinId = _restoringBaseSkin
                    ? (byte?)previousListSkinId
                    : null;
                _lastObservedSkinId = null;
                _lastAppliedLocalVisualSkinId = null;
                _lastAppliedLocalVisualPlayer = null;
                _pendingSkinId = desiredSkinId;
                _lastSkinSubmitTime = float.NegativeInfinity;
            }

            TryApplyPreferredSkin();
        }

        internal void PublishCurrentSkinPreference()
        {
            ListGlobalSettings settings = EnsureGlobalSettings();
            RaiseSkinPreferenceChanged(
                settings.HasListSkinOverride,
                settings.HasListSkinOverride ? settings.ListSkinOverrideId : (byte)0,
                settings.SkinPreferenceUpdatedAtUtcTicks);
        }

        private static long StampSkinPreference(ListGlobalSettings settings)
        {
            long next = DateTime.UtcNow.Ticks;
            if (settings.SkinPreferenceUpdatedAtUtcTicks >= next)
                next = settings.SkinPreferenceUpdatedAtUtcTicks + 1;
            settings.SkinPreferenceUpdatedAtUtcTicks = next;
            return next;
        }

        private void RaiseSkinPreferenceChanged(
            bool hasListSkinOverride,
            byte skinId,
            long updatedAtUtcTicks)
        {
            Action<bool, byte, long> handler = SkinPreferenceChanged;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(
                    hasListSkinOverride,
                    hasListSkinOverride ? skinId : (byte)0,
                    updatedAtUtcTicks);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "HKMP.List: skin preference notification failed: " +
                    exception.Message);
            }
        }

        public void Pump(IReadOnlyList<ListPlayerView> players)
        {
            ListSkinPreviewProvider.CaptureDefaultTexture(_api);
            ObserveLocalSkinFromSnapshot(players);
            UpdateLocalTeamSkinPolicy(players);
            PumpRemoteSkinOverrides(players);
            TryApplyPreferredSkin();
        }

        private void UpdateLocalTeamSkinPolicy(
            IReadOnlyList<ListPlayerView> players)
        {
            ListPlayerView localPlayer = null;
            ushort localPlayerId;
            if (players != null && _state.TryGetLocalPlayerId(out localPlayerId))
            {
                localPlayer = players.FirstOrDefault(player =>
                    player != null && player.Id == localPlayerId);
            }

            ListGlobalSettings settings = EnsureGlobalSettings();
            byte? desired = null;
            bool hasPersonalPreference =
                settings.HasListSkinOverride || settings.HasPreferredSkinId;
            long personalTicks = Math.Max(0, settings.SkinPreferenceUpdatedAtUtcTicks);
            if (localPlayer != null &&
                localPlayer.HasTeamSkin &&
                (localPlayer.IsTeamSkinLocked ||
                 (!localPlayer.IsTeamLeader &&
                  (!hasPersonalPreference ||
                   localPlayer.TeamSkinUpdatedAtUtcTicks > personalTicks))))
            {
                desired = localPlayer.TeamSkinId;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_enforcedLocalTeamSkinId == desired)
                {
                    return;
                }

                _enforcedLocalTeamSkinId = desired;
                _pendingSkinId = null;
                _lastSkinSubmitTime = float.NegativeInfinity;
                _lastAppliedLocalVisualSkinId = null;
                _lastAppliedLocalVisualPlayer = null;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _connected = false;
                _pendingSkinId = null;
                _lastObservedSkinId = null;
                _enforcedLocalTeamSkinId = null;
                _lastAppliedLocalVisualSkinId = null;
                _lastAppliedLocalVisualPlayer = null;
                _hasReceivedSkinUpdate = false;
                _appliedRemoteSkinOverrides.Clear();
                _appliedRemotePlayerContainers.Clear();
                _lastRemoteSkinApplyTimes.Clear();
                _connectionGeneration++;
            }

            try
            {
                _api.ClientManager.ConnectEvent -= OnConnected;
                _api.ClientManager.DisconnectEvent -= OnDisconnected;
            }
            catch
            {
            }

            DetachReflectedEvent(
                _skinChangedEvent,
                _skinChangedHandler);

            DetachReflectedEvent(
                _serverSettingsChangedEvent,
                _serverSettingsChangedHandler);
        }

        private void OnConnected()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _connected = true;
                _connectionGeneration++;
                _pendingSkinId = null;
                _lastObservedSkinId = null;
                _enforcedLocalTeamSkinId = null;
                _lastAppliedLocalVisualSkinId = null;
                _lastAppliedLocalVisualPlayer = null;
                _cancelledListSkinId = null;
                _restoringBaseSkin = false;
                _hasReceivedSkinUpdate = false;
                _lastSkinSubmitTime = float.NegativeInfinity;
                _forceRemoteSkinRefresh = true;
                _appliedRemoteSkinOverrides.Clear();
                _appliedRemotePlayerContainers.Clear();
                _lastRemoteSkinApplyTimes.Clear();
            }

            bool allowSkins;
            bool readable =
                TryReadAllowSkins(null, out allowSkins);

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _allowSkins = readable && allowSkins;
            }

            TryApplyPreferredSkin();
        }

        private void OnDisconnected()
        {
            byte? canonicalSkinId = GetLocalCanonicalSkinId();

            lock (_gate)
            {
                _connected = false;
                _allowSkins = false;
                _enforcedLocalTeamSkinId = null;
                _lastAppliedLocalVisualSkinId = null;
                _lastAppliedLocalVisualPlayer = null;
                _pendingSkinId = null;
                _lastObservedSkinId = null;
                _cancelledListSkinId = null;
                _restoringBaseSkin = false;
                _hasReceivedSkinUpdate = false;
                _lastSkinSubmitTime = float.NegativeInfinity;
                _forceRemoteSkinRefresh = false;
                _appliedRemoteSkinOverrides.Clear();
                _appliedRemotePlayerContainers.Clear();
                _lastRemoteSkinApplyTimes.Clear();
                _connectionGeneration++;
            }

            if (canonicalSkinId.HasValue && TryApplyLocalPlayerSkinVisual(canonicalSkinId.Value))
            {
                GameObject playerObject = GetLocalPlayerObject();
                lock (_gate)
                {
                    _lastAppliedLocalVisualSkinId = canonicalSkinId.Value;
                    _lastAppliedLocalVisualPlayer = playerObject;
                }
            }
        }

        private byte? GetLocalCanonicalSkinId()
        {
            ushort localPlayerId;
            ListPlayerView player;
            if (_state.TryGetLocalPlayerId(out localPlayerId) &&
                _state.TryGetPlayer(localPlayerId, out player) &&
                player != null)
            {
                return player.SkinId;
            }

            lock (_gate)
            {
                return _lastObservedSkinId;
            }
        }

        private void OnSkinChanged(byte skinId)
        {
            bool retrySavedPreference = false;
            bool persistNativeMenuSelection = false;
            bool notifyPreferenceCleared = false;

            lock (_gate)
            {
                if (_disposed ||
                    !_connected ||
                    !_allowSkins)
                {
                    return;
                }

                bool isInitialUpdate =
                    !_hasReceivedSkinUpdate;

                _hasReceivedSkinUpdate = true;
                _lastObservedSkinId = skinId;

                if (_pendingSkinId.HasValue &&
                    _pendingSkinId.Value == skinId)
                {
                    _pendingSkinId = null;
                    _lastSkinSubmitTime = Time.unscaledTime;
                    _restoringBaseSkin = false;
                    _cancelledListSkinId = null;
                    return;
                }

                if (_restoringBaseSkin &&
                    _cancelledListSkinId.HasValue &&
                    _cancelledListSkinId.Value == skinId)
                {
                    retrySavedPreference = true;
                    _lastSkinSubmitTime = float.NegativeInfinity;
                }
                else if (isInitialUpdate)
                {
                    retrySavedPreference = true;
                }
                else
                {
                    persistNativeMenuSelection = true;
                    notifyPreferenceCleared = true;
                }
            }

            if (persistNativeMenuSelection)
            {
                ListGlobalSettings settings = EnsureGlobalSettings();
                settings.PreferredSkinId = skinId;
                settings.HasPreferredSkinId = true;
                settings.HasListSkinOverride = false;
                long updatedAtUtcTicks = StampSkinPreference(settings);

                if (notifyPreferenceCleared)
                    RaiseSkinPreferenceChanged(false, 0, updatedAtUtcTicks);

                lock (_gate)
                {
                    _pendingSkinId = null;
                    _restoringBaseSkin = false;
                    _cancelledListSkinId = null;
                }
                TryApplyLocalPlayerSkinVisual(skinId);
                return;
            }

            if (retrySavedPreference)
            {
                TryApplyPreferredSkin();
            }
        }

        private void OnServerSettingsChanged(object settings)
        {
            bool allowSkins;
            bool readable;

            if (settings == null)
            {
                allowSkins = false;
                readable = true;
            }
            else
            {
                readable = TryReadAllowSkins(settings, out allowSkins);

                if (!readable)
                {
                    readable = TryReadAllowSkins(null, out allowSkins);
                }
            }

            bool wasAllowed;
            byte? resetLocalSkin = null;

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                wasAllowed = _allowSkins;
                _allowSkins = readable && allowSkins;

                if (!_allowSkins)
                {
                    if (wasAllowed)
                    {
                        ushort localPlayerId;
                        ListPlayerView localPlayer;
                        if (_state.TryGetLocalPlayerId(out localPlayerId) &&
                            _state.TryGetPlayer(localPlayerId, out localPlayer) &&
                            localPlayer != null)
                        {
                            resetLocalSkin = localPlayer.SkinId;
                        }
                        else if (_lastObservedSkinId.HasValue)
                        {
                            resetLocalSkin = _lastObservedSkinId.Value;
                        }
                    }

                    _pendingSkinId = null;
                    _lastObservedSkinId = null;
                    _lastAppliedLocalVisualSkinId = null;
                    _lastAppliedLocalVisualPlayer = null;
                    _cancelledListSkinId = null;
                    _restoringBaseSkin = false;
                    _hasReceivedSkinUpdate = false;
                    _lastSkinSubmitTime = float.NegativeInfinity;
                    _forceRemoteSkinRefresh = false;
                }
                else if (!wasAllowed)
                {
                    _pendingSkinId = null;
                    _lastObservedSkinId = null;
                    _lastAppliedLocalVisualSkinId = null;
                    _lastAppliedLocalVisualPlayer = null;
                    _cancelledListSkinId = null;
                    _restoringBaseSkin = false;
                    _hasReceivedSkinUpdate = false;
                    _lastSkinSubmitTime = float.NegativeInfinity;
                    _forceRemoteSkinRefresh = true;
                }
            }

            if (resetLocalSkin.HasValue && TryApplyLocalPlayerSkinVisual(resetLocalSkin.Value))
            {
                GameObject playerObject = GetLocalPlayerObject();
                lock (_gate)
                {
                    _lastAppliedLocalVisualSkinId = resetLocalSkin.Value;
                    _lastAppliedLocalVisualPlayer = playerObject;
                }
            }

            TryApplyPreferredSkin();
        }

        private void TryApplyPreferredSkin()
        {
            byte desiredSkinId;
            int generation;
            float submittedAt;
            bool teamSkinOnly;
            bool shouldSubmit;

            lock (_gate)
            {
                if (_disposed ||
                    !_connected ||
                    !_allowSkins ||
                    _api.NetClient == null ||
                    !_api.NetClient.IsConnected)
                {
                    return;
                }

                ListGlobalSettings settings = EnsureGlobalSettings();
                bool hasDesiredSkin =
                    _enforcedLocalTeamSkinId.HasValue ||
                    settings.HasListSkinOverride ||
                    settings.HasPreferredSkinId ||
                    settings.PreferredSkinId != 0;

                if (!hasDesiredSkin)
                {
                    return;
                }

                teamSkinOnly = _enforcedLocalTeamSkinId.HasValue;
                desiredSkinId = teamSkinOnly
                    ? _enforcedLocalTeamSkinId.Value
                    : settings.HasListSkinOverride
                        ? settings.ListSkinOverrideId
                        : settings.PreferredSkinId;

                generation = _connectionGeneration;
                submittedAt = Time.unscaledTime;
                shouldSubmit = false;

                if (teamSkinOnly)
                {
                    _pendingSkinId = null;
                    _lastSkinSubmitTime = float.NegativeInfinity;
                }
                else if (!_restoringBaseSkin &&
                         _lastObservedSkinId.HasValue &&
                         _lastObservedSkinId.Value == desiredSkinId)
                {
                    _pendingSkinId = null;
                    _lastSkinSubmitTime = submittedAt;
                }
                else if (!_pendingSkinId.HasValue ||
                         _pendingSkinId.Value != desiredSkinId ||
                         submittedAt - _lastSkinSubmitTime >= SkinUpdateRetryIntervalSeconds)
                {
                    _pendingSkinId = desiredSkinId;
                    _lastSkinSubmitTime = submittedAt;
                    shouldSubmit = true;
                }
            }

            EnsureLocalPlayerSkinVisual(desiredSkinId);

            if (teamSkinOnly || !shouldSubmit)
            {
                return;
            }

            if (!TrySubmitSkinUpdate(desiredSkinId))
            {
                lock (_gate)
                {
                    if (!_disposed &&
                        generation == _connectionGeneration &&
                        _pendingSkinId.HasValue &&
                        _pendingSkinId.Value == desiredSkinId)
                    {
                        _lastSkinSubmitTime = submittedAt;
                    }
                }
            }
        }

        private bool EnsureLocalPlayerSkinVisual(byte skinId)
        {
            GameObject playerObject = GetLocalPlayerObject();

            lock (_gate)
            {
                if (!_disposed &&
                    playerObject != null &&
                    _lastAppliedLocalVisualSkinId.HasValue &&
                    _lastAppliedLocalVisualSkinId.Value == skinId &&
                    ReferenceEquals(_lastAppliedLocalVisualPlayer, playerObject))
                {
                    return true;
                }
            }

            if (!TryApplyLocalPlayerSkinVisual(skinId))
            {
                return false;
            }

            GameObject appliedPlayerObject = GetLocalPlayerObject();
            if (appliedPlayerObject != null)
            {
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _lastAppliedLocalVisualSkinId = skinId;
                        _lastAppliedLocalVisualPlayer = appliedPlayerObject;
                    }
                }
            }

            return true;
        }

        private static GameObject GetLocalPlayerObject()
        {
            try
            {
                return HeroController.instance == null
                    ? null
                    : HeroController.instance.gameObject;
            }
            catch
            {
                return null;
            }
        }

        private void ObserveLocalSkinFromSnapshot(
            IReadOnlyList<ListPlayerView> players)
        {
            if (players == null || players.Count == 0)
            {
                return;
            }

            ushort localPlayerId;
            if (!_state.TryGetLocalPlayerId(out localPlayerId))
            {
                return;
            }

            ListPlayerView localPlayer =
                players.FirstOrDefault(player =>
                    player != null && player.Id == localPlayerId);

            if (localPlayer == null)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed || !_connected)
                {
                    return;
                }

                _lastObservedSkinId = localPlayer.SkinId;

                if (!_restoringBaseSkin &&
                    _pendingSkinId.HasValue &&
                    _pendingSkinId.Value == localPlayer.SkinId)
                {
                    _pendingSkinId = null;
                }

                ListGlobalSettings settings = EnsureGlobalSettings();
                if (!settings.HasListSkinOverride &&
                    !settings.HasPreferredSkinId)
                {
                    settings.PreferredSkinId = localPlayer.SkinId;
                    settings.HasPreferredSkinId = true;
                }
            }

        }

        private void PumpRemoteSkinOverrides(
            IReadOnlyList<ListPlayerView> players)
        {
            if (players == null)
            {
                return;
            }

            ListGlobalSettings settings = ListMod.GlobalSettings;
            if (settings == null)
            {
                return;
            }

            bool connected;
            bool allowSkins;
            bool forceRefresh;

            lock (_gate)
            {
                connected = _connected && !_disposed;
                allowSkins = _allowSkins;
                forceRefresh = _forceRemoteSkinRefresh;
            }

            if (!connected)
            {
                return;
            }

            if (!allowSkins)
            {
                foreach (ListPlayerView player in players)
                {
                    if (player == null || IsLocalPlayer(player))
                    {
                        continue;
                    }

                    bool wasApplied;
                    lock (_gate)
                    {
                        wasApplied = _appliedRemoteSkinOverrides.Remove(player.Id);
                        _appliedRemotePlayerContainers.Remove(player.Id);
                        _lastRemoteSkinApplyTimes.Remove(player.Id);
                    }

                    if (wasApplied)
                    {
                        TryApplyRemotePlayerSkin(player.Id, player.SkinId);
                    }
                }

                lock (_gate)
                {
                    _appliedRemoteSkinOverrides.Clear();
                    _appliedRemotePlayerContainers.Clear();
                    _lastRemoteSkinApplyTimes.Clear();
                    _forceRemoteSkinRefresh = false;
                }

                return;
            }

            if (settings.PlayerSkinOverrides == null)
            {
                settings.PlayerSkinOverrides =
                    new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            }

            HashSet<ushort> seenPlayerIds = new HashSet<ushort>();

            foreach (ListPlayerView player in players)
            {
                if (player == null)
                {
                    continue;
                }

                seenPlayerIds.Add(player.Id);
                if (IsLocalPlayer(player))
                {
                    continue;
                }

                string key = GetPlayerSkinOverrideKey(player);
                byte viewerOverrideSkinId = 0;
                bool hasViewerOverride =
                    !string.IsNullOrWhiteSpace(key) &&
                    settings.PlayerSkinOverrides.TryGetValue(key, out viewerOverrideSkinId);
                long viewerUpdatedAtUtcTicks = 0;
                if (hasViewerOverride && settings.PlayerSkinOverrideUpdatedAtUtcTicks != null)
                    settings.PlayerSkinOverrideUpdatedAtUtcTicks.TryGetValue(key, out viewerUpdatedAtUtcTicks);

                bool hasTeamSkin = player.HasTeamSkin;
                bool hasPersonalState = player.HasListSkinOverride || player.ListSkinUpdatedAtUtcTicks > 0;
                byte personalSkinId = player.HasListSkinOverride
                    ? player.ListSkinOverrideId
                    : player.SkinId;

                byte effectiveSkinId = SkinPolicyResolver.Resolve(
                    player.SkinId,
                    hasTeamSkin,
                    player.TeamSkinId,
                    player.IsTeamSkinLocked,
                    player.TeamSkinUpdatedAtUtcTicks,
                    hasPersonalState,
                    personalSkinId,
                    player.ListSkinUpdatedAtUtcTicks,
                    hasViewerOverride,
                    viewerOverrideSkinId,
                    viewerUpdatedAtUtcTicks,
                    player.IsTeamLeader);

                bool hasEffectiveOverride =
                    (hasTeamSkin && player.IsTeamSkinLocked) ||
                    (hasTeamSkin && effectiveSkinId == player.TeamSkinId) ||
                    (hasPersonalState && effectiveSkinId == personalSkinId) ||
                    (hasViewerOverride && effectiveSkinId == viewerOverrideSkinId);

                if (!hasEffectiveOverride)
                {
                    bool wasRemoteSkinRemoved;
                    lock (_gate)
                    {
                        wasRemoteSkinRemoved = _appliedRemoteSkinOverrides.Remove(player.Id);
                        _appliedRemotePlayerContainers.Remove(player.Id);
                        _lastRemoteSkinApplyTimes.Remove(player.Id);
                    }
                    if (wasRemoteSkinRemoved)
                        TryApplyRemotePlayerSkin(player.Id, player.SkinId);
                    continue;
                }

                object playerData;
                if (!TryGetHkmpPlayerData(player.Id, out playerData))
                {
                    continue;
                }

                object currentPlayerContainer =
                    ReadMemberValue(playerData, "PlayerObject") ??
                    ReadMemberValue(playerData, "PlayerContainer");

                byte previouslyApplied = 0;
                bool wasApplied;
                object previousContainer;
                float lastAttemptTime = float.NegativeInfinity;
                bool hasLastAttempt;

                lock (_gate)
                {
                    wasApplied = _appliedRemoteSkinOverrides.TryGetValue(player.Id, out previouslyApplied);
                    _appliedRemotePlayerContainers.TryGetValue(player.Id, out previousContainer);
                    hasLastAttempt = _lastRemoteSkinApplyTimes.TryGetValue(player.Id, out lastAttemptTime);
                }

                bool containerChanged =
                    wasApplied &&
                    !ReferenceEquals(previousContainer, currentPlayerContainer);
                bool skinChanged = wasApplied && previouslyApplied != effectiveSkinId;
                bool mustApplyImmediately = forceRefresh || containerChanged || skinChanged;
                bool retryFailedAttempt =
                    !wasApplied &&
                    (!hasLastAttempt ||
                     Time.unscaledTime - lastAttemptTime >= SkinUpdateRetryIntervalSeconds);
                bool needsApplication = mustApplyImmediately || retryFailedAttempt;

                if (!needsApplication)
                {
                    continue;
                }

                lock (_gate)
                {
                    _lastRemoteSkinApplyTimes[player.Id] = Time.unscaledTime;
                }

                if (TryApplyRemotePlayerSkin(player.Id, effectiveSkinId))
                {
                    lock (_gate)
                    {
                        _appliedRemoteSkinOverrides[player.Id] = effectiveSkinId;
                        _appliedRemotePlayerContainers[player.Id] = currentPlayerContainer;
                    }
                }
                else
                {
                    lock (_gate)
                    {
                        _appliedRemoteSkinOverrides.Remove(player.Id);
                        _appliedRemotePlayerContainers.Remove(player.Id);
                    }
                }
            }

            lock (_gate)
            {
                ushort[] staleIds = _appliedRemoteSkinOverrides.Keys
                    .Where(id => !seenPlayerIds.Contains(id))
                    .ToArray();

                foreach (ushort staleId in staleIds)
                {
                    _appliedRemoteSkinOverrides.Remove(staleId);
                    _appliedRemotePlayerContainers.Remove(staleId);
                    _lastRemoteSkinApplyTimes.Remove(staleId);
                }

                _forceRemoteSkinRefresh = false;
            }
        }

        private bool IsLocalPlayer(
            ListPlayerView player)
        {
            if (player == null)
            {
                return false;
            }

            ushort localPlayerId;
            if (_state.TryGetLocalPlayerId(out localPlayerId))
            {
                return player.Id == localPlayerId;
            }

            string localUsername = GetLocalUsername();
            return
                !string.IsNullOrWhiteSpace(localUsername) &&
                string.Equals(
                    player.Username,
                    localUsername,
                    StringComparison.OrdinalIgnoreCase);
        }

        private string GetLocalUsername()
        {
            try
            {
                return _api.ClientManager.Username;
            }
            catch
            {
                return string.Empty;
            }
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

        private bool TryGetHkmpPlayerData(
            ushort playerId,
            out object playerData)
        {
            playerData = null;

            if (_playerDataField == null)
            {
                return false;
            }

            try
            {
                object value =
                    _playerDataField.GetValue(
                        _clientManager);

                IDictionary dictionary =
                    value as IDictionary;

                if (dictionary == null)
                {
                    return false;
                }

                playerData =
                    dictionary[playerId];

                return playerData != null;
            }
            catch
            {
                playerData = null;
                return false;
            }
        }

        private bool TryApplyRemotePlayerSkin(
            ushort playerId,
            byte skinId)
        {
            object playerData;
            if (!TryGetHkmpPlayerData(playerId, out playerData))
                return false;

            try
            {
                object playerManager = _playerManagerField == null
                    ? null
                    : _playerManagerField.GetValue(_clientManager);
                object skinManager = playerManager == null || _skinManagerField == null
                    ? null
                    : _skinManagerField.GetValue(playerManager);
                object playerObject = ReadMemberValue(playerData, "PlayerObject");
                if (!(playerObject is GameObject))
                {
                    return false;
                }

                GameObject remotePlayer = (GameObject)playerObject;
                if (remotePlayer.GetComponent<tk2dSpriteAnimator>() == null)
                {
                    return false;
                }

                if (skinManager != null && _updatePlayerSkinMethod != null)
                {
                    _updatePlayerSkinMethod.Invoke(
                        skinManager,
                        new object[] { remotePlayer, (byte)0 });

                    if (skinId != 0)
                    {
                        _updatePlayerSkinMethod.Invoke(
                            skinManager,
                            new object[] { remotePlayer, skinId });
                    }

                    return true;
                }

                return false;
            }
            catch (Exception exception)
            {
                ReportNetworkBridgeFailure(
                    "Could not apply the remote visual skin override.",
                    exception);
                return false;
            }
        }

        private bool TryApplyLocalPlayerSkinVisual(byte skinId)
        {
            try
            {
                GameObject localPlayer = GetLocalPlayerObject();
                if (localPlayer == null ||
                    localPlayer.GetComponent<tk2dSpriteAnimator>() == null)
                {
                    return false;
                }

                object playerManager = _playerManagerField == null
                    ? null
                    : _playerManagerField.GetValue(_clientManager);
                object skinManager = playerManager == null || _skinManagerField == null
                    ? null
                    : _skinManagerField.GetValue(playerManager);

                if (skinManager != null && _updateLocalPlayerSkinMethod != null)
                {
                    _updateLocalPlayerSkinMethod.Invoke(
                        skinManager,
                        new object[] { (byte)0 });

                    if (skinId != 0)
                    {
                        _updateLocalPlayerSkinMethod.Invoke(
                            skinManager,
                            new object[] { skinId });
                    }

                    return true;
                }

                return false;
            }
            catch (Exception exception)
            {
                ReportNetworkBridgeFailure(
                    "Could not apply the selected skin to the local player immediately.",
                    exception);
                return false;
            }
        }

        private static MethodInfo FindMethodByParameterTypes(
            Type targetType,
            string methodName,
            params Type[] parameterTypes)
        {
            if (targetType == null)
                return null;

            for (Type current = targetType; current != null; current = current.BaseType)
            {
                MethodInfo method = current.GetMethods(
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(candidate =>
                    {
                        if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal))
                            return false;
                        ParameterInfo[] parameters = candidate.GetParameters();
                        if (parameters.Length != parameterTypes.Length)
                            return false;
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            if (parameters[i].ParameterType != parameterTypes[i])
                                return false;
                        }
                        return true;
                    });
                if (method != null)
                    return method;
            }
            return null;
        }

        private bool TrySubmitSkinUpdate(byte skinId)
        {
            try
            {
                object manager = _clientManager;

                FieldInfo netClientField =
                    FindField(
                        manager.GetType(),
                        NetClientFieldName);

                if (netClientField == null)
                {
                    throw new MissingFieldException(
                        manager.GetType().FullName,
                        NetClientFieldName);
                }

                object netClient =
                    netClientField.GetValue(manager);

                if (netClient == null)
                {
                    throw new InvalidOperationException(
                        "HKMP's internal NetClient is unavailable.");
                }

                PropertyInfo updateManagerProperty =
                    FindProperty(
                        netClient.GetType(),
                        UpdateManagerPropertyName);

                if (updateManagerProperty == null)
                {
                    throw new MissingMemberException(
                        netClient.GetType().FullName,
                        UpdateManagerPropertyName);
                }

                object updateManager =
                    updateManagerProperty.GetValue(
                        netClient,
                        null);

                if (updateManager == null)
                {
                    throw new InvalidOperationException(
                        "HKMP's client update manager is unavailable.");
                }

                MethodInfo updateMethod =
                    updateManager.GetType()
                        .GetMethods(
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic)
                        .FirstOrDefault(
                            method =>
                            {
                                if (!string.Equals(
                                        method.Name,
                                        AddPlayerSettingUpdateMethodName,
                                        StringComparison.Ordinal))
                                {
                                    return false;
                                }

                                ParameterInfo[] parameters =
                                    method.GetParameters();

                                return
                                    parameters.Length == 2 &&
                                    IsNullableType(parameters[0].ParameterType) &&
                                    parameters[1].ParameterType == typeof(byte?);
                            });

                if (updateMethod == null)
                {
                    throw new MissingMethodException(
                        updateManager.GetType().FullName,
                        AddPlayerSettingUpdateMethodName +
                        "(Nullable<Team>, Nullable<Byte>)");
                }

                updateMethod.Invoke(
                    updateManager,
                    new object[]
                    {
                        null,
                        (byte?)skinId
                    });

                return true;
            }
            catch (Exception exception)
            {
                ReportNetworkBridgeFailure(
                    "Could not submit the saved skin ID through HKMP's existing client update path.",
                    exception);

                return false;
            }
        }

        private void AttachSkinChangedEvent()
        {
            if (TryAttachReflectedEvent(
                    SkinChangedEventName,
                    nameof(OnSkinChanged),
                    out _skinChangedEvent,
                    out _skinChangedHandler))
            {
                return;
            }

            lock (_gate)
            {
                if (_reportedMissingSkinEvent)
                {
                    return;
                }

                _reportedMissingSkinEvent = true;
            }

            Debug.LogError(
                "HKMP.List: HKMP's SkinChangedEvent could not be found. " +
                "Skin selections will be reapplied, but changes may not be persisted.");
        }

        private void AttachServerSettingsChangedEvent()
        {
            if (TryAttachReflectedEvent(
                    ServerSettingsChangedEventName,
                    nameof(OnServerSettingsChanged),
                    out _serverSettingsChangedEvent,
                    out _serverSettingsChangedHandler))
            {
                return;
            }

            lock (_gate)
            {
                if (_reportedMissingSettingsEvent)
                {
                    return;
                }

                _reportedMissingSettingsEvent = true;
            }

            Debug.LogWarning(
                "HKMP.List: HKMP's ServerSettingsChangedEvent could not be found. " +
                "Skin preference will be applied when connecting, but may not be reapplied " +
                "if skin selection is enabled later during the same connection.");
        }

        private bool TryAttachReflectedEvent(
            string eventName,
            string methodName,
            out EventInfo eventInfo,
            out Delegate handler)
        {
            eventInfo = null;
            handler = null;

            try
            {
                eventInfo =
                    _clientManager.GetType().GetEvent(
                        eventName,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (eventInfo == null ||
                    eventInfo.EventHandlerType == null)
                {
                    return false;
                }

                MethodInfo invokeMethod =
                    eventInfo.EventHandlerType.GetMethod("Invoke");

                MethodInfo targetMethod =
                    GetType().GetMethod(
                        methodName,
                        BindingFlags.Instance |
                        BindingFlags.NonPublic);

                if (invokeMethod == null || targetMethod == null)
                {
                    return false;
                }

                ParameterInfo[] delegateParameters =
                    invokeMethod.GetParameters();

                ParameterInfo[] targetParameters =
                    targetMethod.GetParameters();

                if (delegateParameters.Length != targetParameters.Length)
                {
                    return false;
                }

                ParameterExpression[] parameters =
                    delegateParameters
                        .Select(
                            parameter =>
                                Expression.Parameter(
                                    parameter.ParameterType,
                                    parameter.Name))
                        .ToArray();

                Expression[] arguments =
                    new Expression[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    Type targetType =
                        targetParameters[i].ParameterType;

                    arguments[i] =
                        parameters[i].Type == targetType
                            ? (Expression)parameters[i]
                            : Expression.Convert(
                                parameters[i],
                                targetType);
                }

                MethodCallExpression call =
                    Expression.Call(
                        Expression.Constant(this),
                        targetMethod,
                        arguments);

                handler =
                    Expression.Lambda(
                        eventInfo.EventHandlerType,
                        call,
                        parameters)
                    .Compile();

                eventInfo.AddEventHandler(
                    _clientManager,
                    handler);

                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "HKMP.List: Failed to attach HKMP event '" +
                    eventName +
                    "': " +
                    exception.Message);

                eventInfo = null;
                handler = null;
                return false;
            }
        }

        private void DetachReflectedEvent(
            EventInfo eventInfo,
            Delegate handler)
        {
            if (eventInfo == null || handler == null)
            {
                return;
            }

            try
            {
                eventInfo.RemoveEventHandler(
                    _clientManager,
                    handler);
            }
            catch
            {
            }
        }

        private bool TryReadAllowSkins(
            object settingsObject,
            out bool allowSkins)
        {
            allowSkins = false;

            if (TryReadAllowSkinsProperty(
                    settingsObject,
                    out allowSkins))
            {
                return true;
            }

            try
            {
                FieldInfo settingsField =
                    FindField(
                        _clientManager.GetType(),
                        "_serverSettings");

                if (settingsField == null)
                {
                    return false;
                }

                object currentSettings =
                    settingsField.GetValue(
                        _clientManager);

                return TryReadAllowSkinsProperty(
                    currentSettings,
                    out allowSkins);
            }
            catch
            {
                allowSkins = false;
                return false;
            }
        }

        private static bool TryReadAllowSkinsProperty(
            object settingsObject,
            out bool allowSkins)
        {
            allowSkins = false;

            if (settingsObject == null)
            {
                return false;
            }

            try
            {
                PropertyInfo property =
                    FindProperty(
                        settingsObject.GetType(),
                        "AllowSkins");

                if (property == null ||
                    property.PropertyType != typeof(bool))
                {
                    return false;
                }

                object value =
                    property.GetValue(
                        settingsObject,
                        null);

                if (!(value is bool))
                {
                    return false;
                }

                allowSkins = (bool)value;
                return true;
            }
            catch
            {
                allowSkins = false;
                return false;
            }
        }

        private void ReportNetworkBridgeFailure(
            string message,
            Exception exception)
        {
            lock (_gate)
            {
                if (_reportedNetworkBridgeFailure)
                {
                    return;
                }

                _reportedNetworkBridgeFailure = true;
            }

            Exception cause = exception;
            TargetInvocationException invocationException =
                exception as TargetInvocationException;

            if (invocationException != null &&
                invocationException.InnerException != null)
            {
                cause = invocationException.InnerException;
            }

            Debug.LogError(
                "HKMP.List: " +
                message +
                " " +
                cause.GetType().Name +
                ": " +
                cause.Message);
        }

        private static bool IsNullableType(Type type)
        {
            return
                type != null &&
                type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(Nullable<>);
        }

        private static FieldInfo FindField(
            Type type,
            string name)
        {
            for (Type current = type;
                 current != null;
                 current = current.BaseType)
            {
                FieldInfo field =
                    current.GetField(
                        name,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                if (field != null)
                {
                    return field;
                }
            }

            return null;
        }

        private static PropertyInfo FindProperty(
        Type type,
        string name)
        {
            for (Type current = type;
                 current != null;
                 current = current.BaseType)
            {
                PropertyInfo property =
                    current.GetProperty(
                        name,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                if (property != null)
                {
                    return property;
                }
            }

            return null;
        }

        private object ReadMemberValue(object obj, string memberName)
        {
            if (obj == null) return null;

            var type = obj.GetType();

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                return field.GetValue(obj);
            }

            var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null && property.CanRead)
            {
                return property.GetValue(obj, null);
            }

            return null;
        }

    }
}
