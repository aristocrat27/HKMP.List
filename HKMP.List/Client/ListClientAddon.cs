using Hkmp.Api.Client;
using Hkmp.Api.Client.Networking;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using HKMP.List.Shared;
using System;
using System.Collections.Generic;

namespace HKMP.List.Client
{
    public sealed class ListClientAddon : ClientAddon
    {
        private IClientApi _api;
        private IClientAddonNetworkSender<ListClientPacketId> _sender;
        private ListClientState _state;
        private ListSkinPersistence _skinPersistence;
        private ListClientNotesStore _localNotesStore;
        private readonly object _settingsSubmissionGate = new object();
        private bool _submittedSavedSettingsThisConnection;

        public event Action<AdminActionResultData> AdminActionResultReceived;
        public event Action<AdminIdentityBundleFilesData> IdentityBundleFilesReceived;

        protected override string Name =>
            "HKMP.List";

        protected override string Version =>
            "1.0.0.0";

        public override bool NeedsNetwork =>
            true;

        public ListClientState State =>
            _state;

        public IClientApi Api =>
            _api;

        public bool IsConnected =>
            _api != null &&
            _api.NetClient != null &&
            _api.NetClient.IsConnected;

        public ServerSettingsData GetSavedServerSettings()
        {
            EnsureGlobalSettings();
            return ListMod.GlobalSettings.SavedAdminSettings.Clone();
        }

        public override void Initialize(
            IClientApi clientApi)
        {
            _api = clientApi;
            _state =
                new ListClientState();

            _localNotesStore =
                new ListClientNotesStore();
            _state.SetLocalNotesStore(_localNotesStore);

            _skinPersistence =
                new ListSkinPersistence(clientApi, _state);

            _sender =
                clientApi.NetClient.GetNetworkSender<ListClientPacketId>(
                    this);

            IClientAddonNetworkReceiver<ListServerPacketId> receiver =
                clientApi.NetClient.GetNetworkReceiver<ListServerPacketId>(
                    this,
                    InstantiatePacket);

            receiver.RegisterPacketHandler<PlayerListBeginData>(
                ListServerPacketId.PlayerListBegin,
                OnPlayerListBegin);

            receiver.RegisterPacketHandler<PlayerListEntryData>(
                ListServerPacketId.PlayerListEntry,
                OnPlayerListEntry);

            receiver.RegisterPacketHandler<PlayerListEndData>(
                ListServerPacketId.PlayerListEnd,
                OnPlayerListEnd);

            receiver.RegisterPacketHandler<LocalPermissionsData>(
                ListServerPacketId.LocalPermissions,
                OnLocalPermissions);

            receiver.RegisterPacketHandler<AdminPlayerData>(
                ListServerPacketId.AdminPlayer,
                OnAdminPlayer);

            receiver.RegisterPacketHandler<PlayerNoteData>(
                ListServerPacketId.PlayerNote,
                OnPlayerNote);

            receiver.RegisterPacketHandler<ServerSettingsData>(
                ListServerPacketId.ServerSettings,
                OnServerSettings);

            receiver.RegisterPacketHandler<AdminActionResultData>(
                ListServerPacketId.AdminActionResult,
                OnAdminActionResult);

            receiver.RegisterPacketHandler<AdminBanListData>(
                ListServerPacketId.AdminBanList,
                OnAdminBanList);

            receiver.RegisterPacketHandler<AdminIdentityBundleFilesData>(
                ListServerPacketId.AdminIdentityBundleFiles,
                OnIdentityBundleFiles);

            receiver.RegisterPacketHandler<ServerSettingsProposalData>(
                ListServerPacketId.SettingsProposal,
                OnSettingsProposal);

            receiver.RegisterPacketHandler<TeamSettingsData>(
                ListServerPacketId.TeamSettings,
                OnTeamSettings);

            receiver.RegisterPacketHandler<TeamAssignmentData>(
                ListServerPacketId.AssignLocalTeam,
                OnTeamAssignment);

            clientApi.ClientManager.ConnectEvent +=
                OnConnected;
            clientApi.ClientManager.DisconnectEvent +=
                OnDisconnected;

            _skinPersistence.SkinPreferenceChanged +=
                OnSkinPreferenceChanged;

            if (IsConnected)
            {
                _skinPersistence.PublishCurrentSkinPreference();
            }
        }

        private void OnConnected()
        {
            UpdateTeamSelectionAvailability();

            if (_skinPersistence != null)
            {
                _skinPersistence.PublishCurrentSkinPreference();
            }
        }

        private void OnSkinPreferenceChanged(
            bool hasListSkinOverride,
            byte listSkinOverrideId,
            long updatedAtUtcTicks)
        {
            if (!IsConnected || _sender == null)
            {
                return;
            }

            try
            {
                _sender.SendSingleData(
                    ListClientPacketId.SkinPreferenceChanged,
                    new SkinPreferenceChangedData
                    {
                        HasListSkinOverride = hasListSkinOverride,
                        ListSkinOverrideId = hasListSkinOverride
                            ? listSkinOverrideId
                            : (byte)0,
                        UpdatedAtUtcTicks = updatedAtUtcTicks
                    });
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(
                    "HKMP.List: could not synchronize the personal skin preference: " +
                    exception.Message);
            }
        }

        public void SetPreferredSkinId(byte skinId)
        {
            ListGlobalSettings settings = GetOrCreateSkinSettings();
            byte fallbackSkinId = settings.HasPreferredSkinId
                ? settings.PreferredSkinId
                : GetCurrentLocalSkinId(skinId);

            SetPreferredSkinOverride(
                skinId,
                fallbackSkinId);
        }

        public void SetPreferredSkinOverride(
            byte skinId,
            byte fallbackSkinId)
        {
            ListGlobalSettings settings = GetOrCreateSkinSettings();

            if (_skinPersistence != null)
            {
                _skinPersistence.SetPreferredSkinOverride(
                    skinId,
                    fallbackSkinId);
                return;
            }

            if (!settings.HasPreferredSkinId)
            {
                settings.PreferredSkinId = fallbackSkinId;
                settings.HasPreferredSkinId = true;
            }

            settings.ListSkinOverrideId = skinId;
            settings.HasListSkinOverride = true;
        }

        public void ClearPreferredSkinOverride(
            byte fallbackSkinId)
        {
            ListGlobalSettings settings = GetOrCreateSkinSettings();

            if (_skinPersistence != null)
            {
                _skinPersistence.ClearPreferredSkinOverride(
                    fallbackSkinId);
                return;
            }

            if (!settings.HasPreferredSkinId)
            {
                settings.PreferredSkinId = fallbackSkinId;
                settings.HasPreferredSkinId = true;
            }

            settings.HasListSkinOverride = false;
        }

        private static ListGlobalSettings GetOrCreateSkinSettings()
        {
            if (ListMod.GlobalSettings == null)
            {
                ListMod.GlobalSettings = new ListGlobalSettings();
            }

            return ListMod.GlobalSettings;
        }

        private byte GetCurrentLocalSkinId(byte fallbackSkinId)
        {
            try
            {
                if (_api == null || _api.ClientManager == null)
                {
                    return fallbackSkinId;
                }

                string username = _api.ClientManager.Username;
                if (string.IsNullOrWhiteSpace(username))
                {
                    return fallbackSkinId;
                }

                foreach (Hkmp.Api.Client.IClientPlayer player in _api.ClientManager.Players)
                {
                    if (player != null &&
                        string.Equals(
                            player.Username,
                            username,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return player.SkinId;
                    }
                }
            }
            catch
            {
            }

            return fallbackSkinId;
        }

        public void SetNote(
            ushort playerId,
            string note)
        {
            string value =
                note ?? string.Empty;

            if (value.Length >
                ListProtocol.MaxNoteLength)
            {
                throw new ArgumentException(
                    "Note is too long.",
                    nameof(note));
            }

            EnsureConnected();

            _sender.SendCollectionData(
                ListClientPacketId.NoteChanged,
                new PlayerNoteData
                {
                    PlayerId = playerId,
                    Note = value
                });
        }

        public void KickPlayer(
            ushort playerId)
        {
            SendAdminAction(
                ListAdminAction.Kick,
                playerId,
                null);
        }

        public void BanPlayer(
            ushort playerId)
        {
            SendAdminAction(
                ListAdminAction.Ban,
                playerId,
                null);
        }

        public void UnbanPlayer(
            string authorizationFingerprint)
        {
            if (string.IsNullOrWhiteSpace(
                    authorizationFingerprint))
            {
                throw new ArgumentException(
                    "Authorization fingerprint is required.",
                    nameof(authorizationFingerprint));
            }

            SendAdminAction(
                ListAdminAction.Unban,
                0,
                authorizationFingerprint);
        }

        public void DemoteAdministrator(
            string authorizationFingerprint)
        {
            if (string.IsNullOrWhiteSpace(authorizationFingerprint))
            {
                throw new ArgumentException(
                    "Authorization fingerprint is required.",
                    nameof(authorizationFingerprint));
            }

            SendAdminAction(
                ListAdminAction.DemoteAdministrator,
                0,
                authorizationFingerprint);
        }

        public void RestoreAdministrator(
            string authorizationFingerprint)
        {
            if (string.IsNullOrWhiteSpace(authorizationFingerprint))
            {
                throw new ArgumentException(
                    "Authorization fingerprint is required.",
                    nameof(authorizationFingerprint));
            }

            SendAdminAction(
                ListAdminAction.RestoreAdministrator,
                0,
                authorizationFingerprint);
        }

        public void UnbanAllPlayers()
        {
            SendAdminAction(
                ListAdminAction.UnbanAll,
                0,
                null);
        }

        public void SetAdminPermissions(
            string authorizationFingerprint,
            AdminPermissions permissions)
        {
            string fingerprint = authorizationFingerprint ?? string.Empty;
            if (string.IsNullOrWhiteSpace(fingerprint) ||
                fingerprint.Length > ListProtocol.MaxAuthorizationFingerprintLength)
            {
                throw new ArgumentException(
                    "A valid authorization fingerprint is required.",
                    nameof(authorizationFingerprint));
            }

            if (permissions == null)
            {
                throw new ArgumentNullException(nameof(permissions));
            }

            EnsureConnected();
            _sender.SendSingleData(
                ListClientPacketId.AdminPermissionsChanged,
                new AdminPermissionsChangedData
                {
                    AuthorizationFingerprint = fingerprint,
                    Permissions = permissions.Clone()
                });
        }

        public void SetAdminIdentityNote(
            string authorizationFingerprint,
            string note)
        {
            string fingerprint =
                authorizationFingerprint ?? string.Empty;

            string value =
                note ?? string.Empty;

            if (fingerprint.Length >
                ListProtocol.MaxAuthorizationFingerprintLength)
            {
                throw new ArgumentException(
                    "Authorization fingerprint is too long.",
                    nameof(authorizationFingerprint));
            }

            if (value.Length >
                ListProtocol.MaxNoteLength)
            {
                throw new ArgumentException(
                    "Authorization note is too long.",
                    nameof(note));
            }

            EnsureConnected();

            _sender.SendSingleData(
                ListClientPacketId.AdminIdentityNoteChanged,
                new AdminIdentityNoteChangedData
                {
                    AuthorizationFingerprint =
                        fingerprint,
                    Note =
                        value
                });
        }

        public void RequestIdentityBundleAction(
            AdminIdentityBundleAction action,
            string fileName = null)
        {
            string value = fileName ?? string.Empty;

            if (value.Length > ListProtocol.MaxIdentityBundleFileNameLength ||
                !AdminIdentityBundleActionData.IsSafeFileName(value, allowEmpty: true))
            {
                throw new ArgumentException(
                    "Identity bundle file name is invalid.",
                    nameof(fileName));
            }

            EnsureConnected();

            _sender.SendSingleData(
                ListClientPacketId.AdminIdentityBundleAction,
                new AdminIdentityBundleActionData
                {
                    Action = action,
                    FileName = value
                });
        }

        public void ApplyServerSettings(
            ServerSettingsData settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            EnsureGlobalSettings();
            ListMod.GlobalSettings.SavedAdminSettings = settings.Clone();

            if (!IsConnected || _sender == null)
            {
                return;
            }

            lock (_settingsSubmissionGate)
            {
                _submittedSavedSettingsThisConnection = true;
            }

            _sender.SendSingleData(
                ListClientPacketId.AdminServerSettingsChanged,
                settings.Clone());
        }

        public void RespondToSettingsProposal(
            string proposalId,
            bool approve)
        {
            if (string.IsNullOrWhiteSpace(proposalId) ||
                proposalId.Length > ListProtocol.MaxSettingsProposalIdLength)
            {
                throw new ArgumentException(
                    "A valid settings proposal ID is required.",
                    nameof(proposalId));
            }

            EnsureConnected();

            _sender.SendSingleData(
                ListClientPacketId.SettingsProposalAction,
                new ServerSettingsProposalActionData
                {
                    ProposalId = proposalId,
                    Action = approve
                        ? ServerSettingsProposalAction.Approve
                        : ServerSettingsProposalAction.Reject
                });
        }

        private static void EnsureGlobalSettings()
        {
            if (ListMod.GlobalSettings == null)
            {
                ListMod.GlobalSettings = new ListGlobalSettings();
            }

            if (ListMod.GlobalSettings.SavedAdminSettings == null)
            {
                ListMod.GlobalSettings.SavedAdminSettings =
                    ServerSettingsData.CreateDefault();
            }
        }

        private void SubmitSavedSettingsOnce()
        {
            if (!IsConnected ||
                _sender == null ||
                _state == null ||
                !_state.IsHost)
            {
                return;
            }

            lock (_settingsSubmissionGate)
            {
                if (_submittedSavedSettingsThisConnection)
                {
                    return;
                }

                _submittedSavedSettingsThisConnection = true;
            }

            try
            {
                EnsureGlobalSettings();
                _sender.SendSingleData(
                    ListClientPacketId.AdminServerSettingsChanged,
                    ListMod.GlobalSettings.SavedAdminSettings.Clone());
            }
            catch
            {
                lock (_settingsSubmissionGate)
                {
                    _submittedSavedSettingsThisConnection = false;
                }
            }
        }

        public void AssignTeamLeader(ushort targetPlayerId)
        {
            SendTeamManagementAction(new TeamManagementActionData
            {
                Action = ListTeamAction.AssignLeader,
                TargetPlayerId = targetPlayerId
            });
        }

        public void RemoveTeamLeader(ushort targetPlayerId)
        {
            SendTeamManagementAction(new TeamManagementActionData
            {
                Action = ListTeamAction.RemoveLeader,
                TargetPlayerId = targetPlayerId
            });
        }

        public void RemoveTeamLeader(Hkmp.Game.Team team)
        {
            SendTeamManagementAction(new TeamManagementActionData
            {
                Action = ListTeamAction.RemoveLeader,
                Team = team,
                TargetPlayerId = ushort.MaxValue
            });
        }

        public void SetTeamSkin(Hkmp.Game.Team team, byte skinId)
        {
            SendTeamManagementAction(new TeamManagementActionData
            {
                Action = ListTeamAction.SetTeamSkin,
                Team = team,
                SkinId = skinId
            });
        }

        public void SetTeamSkinLocked(Hkmp.Game.Team team, bool locked)
        {
            SendTeamManagementAction(new TeamManagementActionData
            {
                Action = ListTeamAction.SetTeamSkinLock,
                Team = team,
                Enabled = locked
            });
        }

        public void KickTeamMember(ushort targetPlayerId)
        {
            SendTeamManagementAction(new TeamManagementActionData
            {
                Action = ListTeamAction.KickTeamMember,
                TargetPlayerId = targetPlayerId
            });
        }

        public void AssignPlayerToTeam(ushort targetPlayerId, Hkmp.Game.Team team)
        {
            if (team != Hkmp.Game.Team.None && !TeamSettingsData.IsPlayableTeam(team))
            {
                throw new ArgumentOutOfRangeException(nameof(team));
            }

            SendTeamManagementAction(new TeamManagementActionData
            {
                Action = ListTeamAction.AssignPlayerToTeam,
                TargetPlayerId = targetPlayerId,
                Team = team
            });
        }

        public void SetTeamChangesLocked(bool locked)
        {
            SendTeamManagementAction(new TeamManagementActionData
            {
                Action = ListTeamAction.SetTeamChangesLock,
                Enabled = locked
            });
        }

        private void SendTeamManagementAction(TeamManagementActionData data)
        {
            EnsureConnected();
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            _sender.SendSingleData(
                ListClientPacketId.TeamManagementAction,
                data);
        }

        public void Disconnect()
        {
            if (_state != null)
            {
                _state.Clear();
            }
        }

        internal void PumpSkinPersistence()
        {
            if (_skinPersistence != null)
            {
                IReadOnlyList<ListPlayerView> players =
                    _state == null
                        ? null
                        : _state.GetPlayers();

                _skinPersistence.Pump(players);
            }
        }

        private void SendAdminAction(
            ListAdminAction action,
            ushort playerId,
            string authorizationFingerprint)
        {
            EnsureConnected();

            _sender.SendSingleData(
                ListClientPacketId.AdminAction,
                new AdminActionData
                {
                    Action =
                        action,

                    TargetPlayerId =
                        playerId,

                    TargetAuthorizationFingerprint =
                        authorizationFingerprint ??
                        string.Empty
                });
        }

        private void EnsureConnected()
        {
            if (_api == null ||
                _sender == null ||
                _api.NetClient == null ||
                !_api.NetClient.IsConnected)
            {
                throw new InvalidOperationException(
                    "HKMP.List client is not connected to an HKMP server.");
            }
        }

        private void OnPlayerListBegin(
            PlayerListBeginData data)
        {
            try
            {
                _state.BeginSnapshot(
                    data);
            }
            catch
            {
            }
        }

        private void OnPlayerListEntry(
            PlayerListEntryData data)
        {
            try
            {
                _state.ReceiveSnapshotEntry(
                    data);
            }
            catch
            {
            }
        }

        private void OnPlayerListEnd(
            PlayerListEndData data)
        {
            try
            {
                _state.EndSnapshot(
                    data);
            }
            catch
            {
            }
        }

        private void OnLocalPermissions(
            LocalPermissionsData data)
        {
            try
            {
                _state.SetAuthorization(
                    data);

                UpdateTeamSelectionAvailability();
                SubmitSavedSettingsOnce();
            }
            catch
            {
            }
        }

        private void OnAdminPlayer(
            AdminPlayerData data)
        {
            try
            {
                _state.SetAdminPlayer(
                    data);
            }
            catch
            {
            }
        }

        private void OnPlayerNote(
            PlayerNoteData data)
        {
            try
            {
                _state.SetNote(
                    data);
            }
            catch
            {
            }
        }

        private void OnServerSettings(
            ServerSettingsData data)
        {
            if (data == null)
            {
                return;
            }

            try
            {
                _state.SetServerSettings(data);
            }
            catch
            {
            }

            UpdateTeamSelectionAvailability();
            SubmitSavedSettingsOnce();
        }

        private void OnSettingsProposal(
            ServerSettingsProposalData data)
        {
            if (data == null || _state == null)
            {
                return;
            }

            try
            {
                if (data.IsRemoved)
                {
                    if (data.AdoptSettings &&
                        data.Settings != null &&
                        _state.IsHost)
                    {
                        EnsureGlobalSettings();
                        ListMod.GlobalSettings.SavedAdminSettings =
                            data.Settings.Clone();
                    }

                    _state.RemoveSettingsProposal(data.ProposalId);
                }
                else
                {
                    _state.SetSettingsProposal(data);
                }
            }
            catch
            {
            }
        }

        private void OnAdminActionResult(
            AdminActionResultData data)
        {
            try
            {
                Action<AdminActionResultData> handler =
                    AdminActionResultReceived;

                if (handler != null)
                {
                    handler(data);
                }
            }
            catch
            {
            }
        }

        private void OnIdentityBundleFiles(
            AdminIdentityBundleFilesData data)
        {
            try
            {
                Action<AdminIdentityBundleFilesData> handler =
                    IdentityBundleFilesReceived;

                if (handler != null)
                {
                    handler(data);
                }
            }
            catch
            {
            }
        }

        private void OnTeamSettings(
            TeamSettingsData data)
        {
            try
            {
                if (_state != null)
                {
                    _state.SetTeamSettings(data);
                }

                UpdateTeamSelectionAvailability();
            }
            catch
            {
            }
        }

        private void OnTeamAssignment(
            TeamAssignmentData data)
        {
            if (_api == null || _api.ClientManager == null || data == null)
            {
                return;
            }

            if (data.Team != Hkmp.Game.Team.None &&
                !TeamSettingsData.IsPlayableTeam(data.Team))
            {
                return;
            }

            try
            {
                _api.ClientManager.ChangeTeam(data.Team);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(
                    "HKMP.List: could not apply the server team assignment: " + exception.Message);
            }
        }

        private void UpdateTeamSelectionAvailability()
        {
            if (_api == null || _api.UiManager == null)
            {
                return;
            }

            if (!IsConnected)
            {
                try { _api.UiManager.EnableTeamSelection(); }
                catch { }
                return;
            }

            if (_state == null)
            {
                return;
            }

            ServerSettingsData settings = _state.GetServerSettings();
            if (settings == null)
            {
                return;
            }

            try
            {
                if (_state.TeamChangesLocked || !settings.TeamsEnabled)
                {
                    _api.UiManager.DisableTeamSelection();
                }
                else
                {
                    _api.UiManager.EnableTeamSelection();
                }
            }
            catch
            {
            }
        }

        private void OnAdminBanList(
            AdminBanListData data)
        {
            try
            {
                _state.SetBanList(
                    data);
            }
            catch
            {
            }
        }

        private void OnDisconnected()
        {
            lock (_settingsSubmissionGate)
            {
                _submittedSavedSettingsThisConnection = false;
            }

            if (_state != null)
            {
                _state.Clear();
            }

            UpdateTeamSelectionAvailability();
        }

        private static IPacketData InstantiatePacket(
            ListServerPacketId packetId)
        {
            switch (packetId)
            {
                case ListServerPacketId.PlayerListBegin:
                    return new PlayerListBeginData();

                case ListServerPacketId.PlayerListEntry:
                    return new PacketDataCollection<PlayerListEntryData>();

                case ListServerPacketId.PlayerListEnd:
                    return new PlayerListEndData();

                case ListServerPacketId.LocalPermissions:
                    return new LocalPermissionsData();

                case ListServerPacketId.AdminPlayer:
                    return new PacketDataCollection<AdminPlayerData>();

                case ListServerPacketId.PlayerNote:
                    return new PacketDataCollection<PlayerNoteData>();

                case ListServerPacketId.ServerSettings:
                    return new ServerSettingsData();

                case ListServerPacketId.AdminActionResult:
                    return new AdminActionResultData();

                case ListServerPacketId.AdminBanList:
                    return new AdminBanListData();

                case ListServerPacketId.AdminIdentityBundleFiles:
                    return new AdminIdentityBundleFilesData();

                case ListServerPacketId.SettingsProposal:
                    return new PacketDataCollection<ServerSettingsProposalData>();

                case ListServerPacketId.TeamSettings:
                    return new TeamSettingsData();

                case ListServerPacketId.AssignLocalTeam:
                    return new TeamAssignmentData();

                default:
                    return null;
            }
        }
    }
}
