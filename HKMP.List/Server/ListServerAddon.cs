using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hkmp.Api.Server;
using Hkmp.Api.Eventing.ServerEvents;
using Hkmp.Api.Server.Networking;
using Hkmp.Game.Settings;
using UnityEngine;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using HKMP.List.Shared;
using HKMP.List;

namespace HKMP.List.Server
{
    public sealed class ListServerAddon : ServerAddon
    {
        private IServerApi _api;
        private IServerAddonNetworkSender<ListServerPacketId> _sender;
        private ListHostNotesStore _notes;
        private ListHostAdminStore _adminStore;
        private ListHostTeamStore _teamStore;
        private ServerUpdateRunner _updateRunner;

        private readonly object _gate = new object();
        private readonly object _teamLockGate = new object();
        private readonly Dictionary<string, Hkmp.Game.Team> _lockedTeamAssignments =
            new Dictionary<string, Hkmp.Game.Team>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ushort, ForcedTeamChange> _lastForcedTeamByPlayer =
            new Dictionary<ushort, ForcedTeamChange>();
        private readonly Dictionary<ushort, PlayerState> _lastPlayers =
            new Dictionary<ushort, PlayerState>();
        private readonly Dictionary<ushort, SkinPreferenceState> _skinPreferences =
            new Dictionary<ushort, SkinPreferenceState>();

        private readonly object _settingsProposalGate = new object();
        private readonly Dictionary<string, ServerSettingsProposalData> _settingsProposals =
            new Dictionary<string, ServerSettingsProposalData>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _processingSettingsProposals =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private ServerSettingsData _lastSettings;
        private string _lastTeamSettingsSignature = string.Empty;
        private ulong _snapshotId;
        private bool _syncing;

        protected override string Name =>
            "HKMP.List";

        protected override string Version =>
            "1.0.0.0";

        public override bool NeedsNetwork =>
            true;

        public override void Initialize(
            IServerApi serverApi)
        {
            _api = serverApi;
            _sender =
                serverApi.NetServer.GetNetworkSender<ListServerPacketId>(
                    this);

            string baseDirectory =
                Path.GetDirectoryName(
                    typeof(ListServerAddon)
                        .Assembly
                        .Location);

            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                baseDirectory =
                    AppDomain.CurrentDomain.BaseDirectory;
            }

            _notes =
                new ListHostNotesStore(baseDirectory);

            _adminStore =
                new ListHostAdminStore(baseDirectory);

            _teamStore =
                new ListHostTeamStore(baseDirectory);

            IServerAddonNetworkReceiver<ListClientPacketId> receiver =
                serverApi.NetServer.GetNetworkReceiver<ListClientPacketId>(
                    this,
                    InstantiatePacket);

            receiver.RegisterPacketHandler<PlayerNoteData>(
                ListClientPacketId.NoteChanged,
                OnNoteChanged);

            receiver.RegisterPacketHandler<AdminActionData>(
                ListClientPacketId.AdminAction,
                OnAdminAction);

            receiver.RegisterPacketHandler<ServerSettingsData>(
                ListClientPacketId.AdminServerSettingsChanged,
                OnAdminServerSettingsChanged);

            receiver.RegisterPacketHandler<AdminIdentityNoteChangedData>(
                ListClientPacketId.AdminIdentityNoteChanged,
                OnAdminIdentityNoteChanged);

            receiver.RegisterPacketHandler<AdminIdentityBundleActionData>(
                ListClientPacketId.AdminIdentityBundleAction,
                OnAdminIdentityBundleAction);

            receiver.RegisterPacketHandler<AdminPermissionsChangedData>(
                ListClientPacketId.AdminPermissionsChanged,
                OnAdminPermissionsChanged);

            receiver.RegisterPacketHandler<ServerSettingsProposalActionData>(
                ListClientPacketId.SettingsProposalAction,
                OnSettingsProposalAction);

            receiver.RegisterPacketHandler<TeamManagementActionData>(
                ListClientPacketId.TeamManagementAction,
                OnTeamManagementAction);

            receiver.RegisterPacketHandler<SkinPreferenceChangedData>(
                ListClientPacketId.SkinPreferenceChanged,
                OnSkinPreferenceChanged);

            serverApi.ServerManager.PlayerConnectEvent +=
                OnPlayerConnect;

            serverApi.ServerManager.PlayerDisconnectEvent +=
                OnPlayerDisconnect;

            serverApi.ServerManager.PlayerEnterSceneEvent +=
                OnPlayerEnterScene;

            serverApi.ServerManager.PlayerChatEvent +=
                OnPlayerChatEvent;

            _updateRunner =
                CreateUpdateRunner();

            Synchronize();
        }

        private void OnPlayerChatEvent(IPlayerChatEvent chatEvent)
        {
            if (chatEvent == null || chatEvent.Player == null ||
                string.IsNullOrWhiteSpace(chatEvent.Message))
            {
                return;
            }

            string message = chatEvent.Message.Trim();
            if (!message.StartsWith("/", StringComparison.Ordinal))
            {
                return;
            }

            IServerPlayer sender = chatEvent.Player;
            if (IsLocalHostPlayer(sender))
            {
                return;
            }

            string fingerprint = ListHostNotesStore.GetAuthorizationFingerprint(sender);
            bool wasAdministrator = sender.IsAuthorized ||
                (_adminStore != null && _adminStore.IsKnownAdministratorFingerprint(fingerprint));
            if (!wasAdministrator)
            {
                return;
            }

            string commandLine = message.Substring(1).TrimStart();
            if (commandLine.Length == 0)
            {
                return;
            }

            string[] parts = commandLine.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return;
            }

            string command = parts[0].Trim().ToLowerInvariant();
            Func<AdminPermissions, bool> permission = null;
            string permissionName = null;
            bool hostOnly = false;

            switch (command)
            {
                case "kick":
                    permission = p => p.CanKickPlayers;
                    permissionName = "kick players";
                    break;
                case "ban":
                case "banip":
                    permission = p => p.CanBanPlayers;
                    permissionName = "ban players";
                    break;
                case "unban":
                case "unbanip":
                    permission = p => p.CanManageBans;
                    permissionName = "manage bans";
                    break;
                case "set":
                    permission = p =>
                        p.CanChangeServerSettings &&
                        p.CanChangeServerSettingsDirectly;
                    permissionName = "change host server settings directly";
                    break;
                case "auth":
                case "deauth":
                case "whitelist":
                case "announce":
                case "team":
                case "skin":
                case "copysave":
                case "host":
                    hostOnly = true;
                    permissionName = "use this host-only HKMP command";
                    break;
                default:
                    return;
            }

            AdminPermissions currentPermissions = GetPermissionsForPlayer(sender);
            bool allowed = IsAdminViewer(sender) && !hostOnly &&
                permission != null && permission(currentPermissions);
            if (allowed)
            {
                return;
            }

            if (chatEvent != null)
            {
                var property = chatEvent.GetType().GetProperty("Cancelled",
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);

                if (property != null && property.CanWrite)
                {
                    property.SetValue(chatEvent, true);
                }
            }

        

            try
            {
                _api.ServerManager.SendMessage(
                    sender.Id,
                    "HKMP.List: your administrator role does not permit /" +
                    command + " (" + permissionName + ").");
            }
            catch
            {
            }
        }

        private void OnPlayerConnect(
            IServerPlayer player)
        {
            if (player == null)
            {
                return;
            }

            if (IsBanned(player))
            {
                try
                {
                    _api.ServerManager.SendMessage(
                        player.Id,
                        "HKMP.List: you are banned from this host.");
                }
                catch
                {
                }

                try
                {
                    _api.ServerManager.DisconnectPlayer(
                        player.Id,
                        DisconnectReason.Kicked);
                }
                catch
                {
                }

                return;
            }

            Synchronize();

            SendSnapshotTo(
                player.Id);

            SendPrivateState(
                player.Id);

            if (IsLocalHostPlayer(player))
            {
                SendPendingSettingsProposalsToHost(player.Id);
            }
        }

        private void OnPlayerDisconnect(
            IServerPlayer player)
        {
            if (player != null)
            {
                lock (_gate)
                {
                    _skinPreferences.Remove(player.Id);
                }

                lock (_teamLockGate)
                {
                    _lastForcedTeamByPlayer.Remove(player.Id);
                }
            }

            Synchronize();
        }

        private void OnPlayerEnterScene(
            IServerPlayer player)
        {
            Synchronize();
        }

        private ServerUpdateRunner CreateUpdateRunner()
        {
            GameObject runnerObject =
                new GameObject(
                    "HKMP.List.ServerRunner");

            UnityEngine.Object.DontDestroyOnLoad(
                runnerObject);

            ServerUpdateRunner runner =
                runnerObject.AddComponent<
                    ServerUpdateRunner>();

            runner.Initialize(
                this,
                ListProtocol.SyncIntervalMilliseconds);

            return runner;
        }

        private bool IsBanned(
            IServerPlayer player)
        {
            if (player == null ||
                _adminStore == null)
            {
                return false;
            }

            string fingerprint =
                ListHostNotesStore.GetAuthorizationFingerprint(
                    player);

            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                return false;
            }

            return _adminStore.IsBanned(
                fingerprint);
        }

        private void Synchronize()
        {
            lock (_gate)
            {
                if (_syncing ||
                    _api == null ||
                    _sender == null)
                {
                    return;
                }

                _syncing = true;
            }

            try
            {
                List<IServerPlayer> players =
                    _api.ServerManager.Players == null
                        ? new List<IServerPlayer>()
                        : _api.ServerManager.Players.ToList();

                EnforceTeamChangesLock(players);

                if (_adminStore != null)
                {
                    foreach (IServerPlayer player in players)
                    {
                        if (player != null)
                        {
                            _adminStore.RememberAuthorizationIdentity(
                                player.AuthKey,
                                player.Username,
                                player.IsAuthorized);
                        }
                    }
                }

                Dictionary<ushort, PlayerState> current =
                    players
                        .Where(x => x != null)
                        .ToDictionary(
                            x => x.Id,
                            CreatePlayerState);

                ServerSettingsData settings =
                    CreateServerSettings(
                        _api.ServerManager.ServerSettings);

                TeamSettingsData teamSettings =
                    CreateTeamSettingsData(players);
                string teamSettingsSignature =
                    CreateTeamSettingsSignature(teamSettings);

                bool playerStateChanged;
                bool settingsChanged;
                bool teamSettingsChanged;

                lock (_gate)
                {
                    playerStateChanged =
                        !PlayerStateMapsEqual(
                            _lastPlayers,
                            current);

                    settingsChanged =
                        !ServerSettingsEqual(
                            _lastSettings,
                            settings);

                    teamSettingsChanged =
                        !string.Equals(
                            _lastTeamSettingsSignature,
                            teamSettingsSignature,
                            StringComparison.Ordinal);

                    _lastPlayers.Clear();
                    foreach (KeyValuePair<ushort, PlayerState> pair in current)
                    {
                        _lastPlayers[pair.Key] = pair.Value;
                    }

                    _lastSettings = settings;
                    _lastTeamSettingsSignature = teamSettingsSignature;
                }

                if (playerStateChanged)
                {
                    BroadcastSnapshot(
                        players);
                    RefreshAuthorizationData(
                        players);
                }

                if (settingsChanged)
                {
                    _sender.BroadcastSingleData(
                        ListServerPacketId.ServerSettings,
                        settings);
                }

                if (teamSettingsChanged)
                {
                    _sender.BroadcastSingleData(
                        ListServerPacketId.TeamSettings,
                        teamSettings);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Error(
                        "HKMP.List synchronization failed: " +
                        ex);
                }
                catch
                {
                }
            }
            finally
            {
                lock (_gate)
                {
                    _syncing = false;
                }
            }
        }

        private void BroadcastSnapshot(
            IReadOnlyList<IServerPlayer> players)
        {
            List<IServerPlayer> current =
                players == null
                    ? new List<IServerPlayer>()
                    : players
                        .Where(x => x != null)
                        .OrderBy(x => x.Id)
                        .ToList();

            ulong snapshotId;

            lock (_gate)
            {
                _snapshotId++;
                if (_snapshotId == 0)
                {
                    _snapshotId++;
                }

                snapshotId = _snapshotId;
            }

            ushort count =
                (ushort)Math.Min(
                    ushort.MaxValue,
                    current.Count);

            _sender.BroadcastSingleData(
                ListServerPacketId.PlayerListBegin,
                new PlayerListBeginData
                {
                    SnapshotId = snapshotId,
                    PlayerCount = count
                });

            foreach (IServerPlayer player in current.Take(count))
            {
                _sender.BroadcastCollectionData(
                    ListServerPacketId.PlayerListEntry,
                    CreatePlayerEntry(
                        snapshotId,
                        player));
            }

            _sender.BroadcastSingleData(
                ListServerPacketId.PlayerListEnd,
                new PlayerListEndData
                {
                    SnapshotId = snapshotId,
                    PlayerCount = count
                });
        }

        private void SendSnapshotTo(
            ushort playerId)
        {
            if (_api == null ||
                _sender == null)
            {
                return;
            }

            List<IServerPlayer> players =
                _api.ServerManager.Players == null
                    ? new List<IServerPlayer>()
                    : _api.ServerManager.Players.ToList();

            List<IServerPlayer> current =
                players == null
                    ? new List<IServerPlayer>()
                    : players
                        .Where(x => x != null)
                        .OrderBy(x => x.Id)
                        .ToList();

            ulong snapshotId;

            lock (_gate)
            {
                _snapshotId++;
                if (_snapshotId == 0)
                {
                    _snapshotId++;
                }

                snapshotId = _snapshotId;
            }

            ushort count =
                (ushort)Math.Min(
                    ushort.MaxValue,
                    current.Count);

            _sender.SendSingleData(
                ListServerPacketId.PlayerListBegin,
                new PlayerListBeginData
                {
                    SnapshotId = snapshotId,
                    PlayerCount = count
                },
                playerId);

            foreach (IServerPlayer player in current.Take(count))
            {
                _sender.SendCollectionData(
                    ListServerPacketId.PlayerListEntry,
                    CreatePlayerEntry(
                        snapshotId,
                        player),
                    playerId);
            }

            _sender.SendSingleData(
                ListServerPacketId.PlayerListEnd,
                new PlayerListEndData
                {
                    SnapshotId = snapshotId,
                    PlayerCount = count
                },
                playerId);
        }

        private void RefreshAuthorizationData(
            IReadOnlyList<IServerPlayer> players)
        {
            if (players == null)
            {
                return;
            }

            List<IServerPlayer> current =
                players
                    .Where(x => x != null)
                    .OrderBy(x => x.Id)
                    .ToList();

            foreach (IServerPlayer viewer in current)
            {
                _sender.SendSingleData(
                    ListServerPacketId.LocalPermissions,
                    CreateLocalPermissionsData(viewer),
                    viewer.Id);

                if (!IsAdminViewer(viewer))
                {
                    continue;
                }

                foreach (IServerPlayer target in current)
                {
                    _sender.SendCollectionData(
                        ListServerPacketId.AdminPlayer,
                        CreateAdminPlayerData(target, viewer),
                        viewer.Id);
                }

                SendAdminBanList(
                    viewer.Id);
            }
        }

        private LocalPermissionsData CreateLocalPermissionsData(
            IServerPlayer player)
        {
            bool isHost = player != null && IsLocalHostPlayer(player);
            bool isAuthorized = player != null && IsAdminViewer(player);
            string fingerprint = player == null ? null :
                ListHostNotesStore.GetAuthorizationFingerprint(player);

            return new LocalPermissionsData
            {
                PlayerId = player == null ? (ushort)0 : player.Id,
                IsAuthorized = isAuthorized,
                IsHost = isHost,
                Permissions = !isAuthorized
                    ? AdminPermissions.NoAccess()
                    : isHost
                        ? AdminPermissions.FullAccess()
                        : _adminStore.GetPermissions(fingerprint)
            };
        }

        private AdminPermissions GetPermissionsForPlayer(
            IServerPlayer player)
        {
            if (player == null)
            {
                return AdminPermissions.NoAccess();
            }

            if (IsLocalHostPlayer(player))
            {
                return AdminPermissions.FullAccess();
            }

            if (!IsAdminViewer(player) || _adminStore == null)
            {
                return AdminPermissions.NoAccess();
            }

            return _adminStore.GetPermissions(
                ListHostNotesStore.GetAuthorizationFingerprint(player));
        }

        private bool RequirePermission(
            IServerPlayer player,
            Func<AdminPermissions, bool> check,
            string permissionName)
        {
            if (player == null || !IsAdminViewer(player))
                return false;
            AdminPermissions permissions = GetPermissionsForPlayer(player);
            if (check != null && check(permissions)) return true;
            SendAdminResult(
                player.Id, false, "PERMISSION_DENIED",
                "Your administrator role does not permit: " + permissionName + ".");
            return false;
        }

        private void SendAdminBanList(
            ushort playerId)
        {
            if (_adminStore == null ||
                _sender == null)
            {
                return;
            }

            _sender.SendSingleData(
                ListServerPacketId.AdminBanList,
                new AdminBanListData
                {
                    Entries =
                        _adminStore.GetBans()
                            .ToList()
                },
                playerId);
        }

        private void RefreshAdminStateForAuthorizedViewers(
            IReadOnlyList<IServerPlayer> players)
        {
            if (players == null ||
                _adminStore == null)
            {
                return;
            }

            foreach (IServerPlayer viewer in players)
            {
                if (viewer == null || !IsAdminViewer(viewer))
                {
                    continue;
                }

                foreach (IServerPlayer target in players)
                {
                    if (target == null)
                    {
                        continue;
                    }

                    _sender.SendCollectionData(
                        ListServerPacketId.AdminPlayer,
                        CreateAdminPlayerData(target, viewer),
                        viewer.Id);
                }

                SendAdminBanList(
                    viewer.Id);
            }
        }

        private void RefreshAdminIdentityForAuthorizedViewers(
            IReadOnlyList<IServerPlayer> players,
            string authorizationFingerprint)
        {
            if (players == null ||
                string.IsNullOrWhiteSpace(authorizationFingerprint) ||
                _sender == null)
            {
                return;
            }

            IServerPlayer[] matchingTargets =
                players
                    .Where(
                        target =>
                            target != null &&
                            string.Equals(
                                ListHostNotesStore.GetAuthorizationFingerprint(target),
                                authorizationFingerprint,
                                StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            if (matchingTargets.Length == 0)
            {
                return;
            }

            foreach (IServerPlayer viewer in players)
            {
                if (viewer == null ||
                    !IsAdminViewer(viewer))
                {
                    continue;
                }

                foreach (IServerPlayer target in matchingTargets)
                {
                    _sender.SendCollectionData(
                        ListServerPacketId.AdminPlayer,
                        CreateAdminPlayerData(
                            target,
                            viewer),
                        viewer.Id);
                }
            }
        }

        private void SendPrivateState(
            ushort playerId)
        {
            if (_api == null ||
                _sender == null)
            {
                return;
            }

            IServerPlayer owner =
                _api.ServerManager.GetPlayer(
                    playerId);

            if (owner == null)
            {
                return;
            }

            _sender.SendSingleData(
                ListServerPacketId.LocalPermissions,
                CreateLocalPermissionsData(owner),
                owner.Id);

            ServerSettingsData settings =
                CreateServerSettings(
                    _api.ServerManager.ServerSettings);

            _sender.SendSingleData(
                ListServerPacketId.ServerSettings,
                settings,
                owner.Id);

            _sender.SendSingleData(
                ListServerPacketId.TeamSettings,
                CreateTeamSettingsData(
                    _api.ServerManager.Players == null
                        ? new List<IServerPlayer>()
                        : _api.ServerManager.Players.ToList()),
                owner.Id);

            List<IServerPlayer> players =
                _api.ServerManager.Players == null
                    ? new List<IServerPlayer>()
                    : _api.ServerManager.Players.ToList();

            if (IsAdminViewer(owner) &&
                players != null)
            {
                foreach (IServerPlayer target in players.Where(x => x != null))
                {
                    _sender.SendCollectionData(
                        ListServerPacketId.AdminPlayer,
                        CreateAdminPlayerData(target, owner),
                        owner.Id);
                }

                SendAdminBanList(
                    owner.Id);
            }

            if (players == null)
            {
                return;
            }

            foreach (IServerPlayer target in players.Where(x => x != null))
            {
                string note =
                    _notes.GetNote(
                        owner,
                        target);

                if (string.IsNullOrEmpty(note))
                {
                    continue;
                }

                _sender.SendCollectionData(
                    ListServerPacketId.PlayerNote,
                    new PlayerNoteData
                    {
                        PlayerId = target.Id,
                        Note = note
                    },
                    owner.Id);
            }
        }

        private void OnSkinPreferenceChanged(
            ushort senderId,
            SkinPreferenceChangedData data)
        {
            if (_api == null || data == null)
            {
                return;
            }

            IServerPlayer player = _api.ServerManager.GetPlayer(senderId);
            if (player == null)
            {
                return;
            }

            SkinPreferenceState state = new SkinPreferenceState
            {
                HasListSkinOverride = data.HasListSkinOverride,
                ListSkinOverrideId = data.HasListSkinOverride
                    ? data.ListSkinOverrideId
                    : (byte)0,
                UpdatedAtUtcTicks = Math.Max(0, data.UpdatedAtUtcTicks)
            };

            bool changed;
            lock (_gate)
            {
                SkinPreferenceState previous;
                changed = !_skinPreferences.TryGetValue(senderId, out previous) ||
                    previous == null ||
                    previous.HasListSkinOverride != state.HasListSkinOverride ||
                    previous.ListSkinOverrideId != state.ListSkinOverrideId ||
                    previous.UpdatedAtUtcTicks != state.UpdatedAtUtcTicks;

                _skinPreferences[senderId] = state;
            }

            if (changed)
            {
                Synchronize();
            }
        }

        private void OnNoteChanged(
            ushort senderId,
            PlayerNoteData data)
        {
            if (_api == null ||
                _notes == null ||
                data == null)
            {
                return;
            }

            IServerPlayer owner =
                _api.ServerManager.GetPlayer(
                    senderId);

            IServerPlayer target =
                _api.ServerManager.GetPlayer(
                    data.PlayerId);

            if (owner == null ||
                target == null)
            {
                return;
            }

            string note = data.Note ?? string.Empty;

            if (note.Length > ListProtocol.MaxNoteLength)
            {
                return;
            }

            bool saved =
                _notes.SetNote(
                    owner,
                    target,
                    note);

            if (!saved)
            {
                try
                {
                    _api.ServerManager.SendMessage(
                        owner.Id,
                        "HKMP.List: note could not be saved.");
                }
                catch
                {
                }

                return;
            }

            _sender.SendCollectionData(
                ListServerPacketId.PlayerNote,
                new PlayerNoteData
                {
                    PlayerId = target.Id,
                    Note = note
                },
                owner.Id);
        }

        private void OnAdminAction(
            ushort senderId,
            AdminActionData data)
        {
            if (_api == null ||
                data == null ||
                _adminStore == null)
            {
                return;
            }

            IServerPlayer sender =
                _api.ServerManager.GetPlayer(
                    senderId);

            if (sender == null)
            {
                return;
            }

            if (!IsAdminViewer(sender))
            {
                SendAdminResult(
                    senderId,
                    false,
                    "NOT_AUTHORIZED",
                    "You are not authorized to use HKMP.List admin actions.");

                return;
            }

            bool permitted;
            switch (data.Action)
            {
                case ListAdminAction.Kick:
                    permitted = RequirePermission(sender, p => p.CanKickPlayers, "kick players");
                    break;
                case ListAdminAction.Ban:
                    permitted = RequirePermission(sender, p => p.CanBanPlayers, "ban players");
                    break;
                case ListAdminAction.Unban:
                case ListAdminAction.UnbanAll:
                    permitted = RequirePermission(sender, p => p.CanManageBans, "manage bans");
                    break;
                case ListAdminAction.DemoteAdministrator:
                case ListAdminAction.RestoreAdministrator:
                    if (!IsLocalHostPlayer(sender))
                    {
                        SendAdminResult(senderId, false, "HOST_ONLY",
                            "Only the host can demote or restore administrator access.");
                        return;
                    }
                    permitted = true;
                    break;
                default:
                    SendAdminResult(
                        senderId, false, "UNKNOWN_ACTION", "Unknown admin action.");
                    return;
            }

            if (!permitted) return;

            switch (data.Action)
            {
                case ListAdminAction.Kick:
                    HandleKick(
                        sender,
                        data.TargetPlayerId);
                    return;

                case ListAdminAction.Ban:
                    HandleBan(
                        sender,
                        data.TargetPlayerId);
                    return;

                case ListAdminAction.Unban:
                    HandleUnban(
                        sender,
                        data.TargetAuthorizationFingerprint);
                    return;

                case ListAdminAction.UnbanAll:
                    HandleUnbanAll(
                        sender);
                    return;

                case ListAdminAction.DemoteAdministrator:
                    HandleAdministratorAccessChange(
                        sender, data.TargetAuthorizationFingerprint, true);
                    return;

                case ListAdminAction.RestoreAdministrator:
                    HandleAdministratorAccessChange(
                        sender, data.TargetAuthorizationFingerprint, false);
                    return;

                default:
                    SendAdminResult(
                        senderId,
                        false,
                        "UNKNOWN_ACTION",
                        "Unknown admin action.");
                    return;
            }
        }

        private void HandleAdministratorAccessChange(
            IServerPlayer sender,
            string authorizationFingerprint,
            bool revoke)
        {
            if (sender == null || !IsLocalHostPlayer(sender) || _adminStore == null)
            {
                if (sender != null)
                {
                    SendAdminResult(sender.Id, false, "HOST_ONLY",
                        "Only the host can change administrator access.");
                }
                return;
            }

            if (!IsValidFingerprint(authorizationFingerprint) ||
                !_adminStore.IsKnownAdministratorFingerprint(authorizationFingerprint))
            {
                SendAdminResult(sender.Id, false, "IDENTITY_INVALID",
                    "The selected identity is not a known administrator identity.");
                return;
            }

            bool isHostIdentity = GetCurrentPlayers().Any(player =>
                player != null && IsLocalHostPlayer(player) &&
                string.Equals(
                    ListHostNotesStore.GetAuthorizationFingerprint(player),
                    authorizationFingerprint, StringComparison.OrdinalIgnoreCase));
            if (isHostIdentity)
            {
                SendAdminResult(sender.Id, false, "HOST_PROTECTED",
                    "Host administrator access cannot be changed.");
                return;
            }

            if (!_adminStore.SetAdminAccessRevoked(authorizationFingerprint, revoke))
            {
                SendAdminResult(sender.Id, false, "ADMIN_ACCESS_SAVE_FAILED",
                    "Administrator access state could not be saved.");
                return;
            }

            SendAdminResult(sender.Id, true,
                revoke ? "ADMIN_DEMOTED" : "ADMIN_RESTORED",
                revoke
                    ? "The selected identity's HKMP.List administrator access was revoked."
                    : "The selected identity's HKMP.List administrator access was restored.");

            Synchronize();
            RefreshAuthorizationData(GetCurrentPlayers());
        }

        private bool RejectProtectedModerationTarget(
            IServerPlayer sender,
            IServerPlayer target)
        {
            if (sender == null || target == null)
            {
                return true;
            }

            string hostUsername;

            if (!TryGetLocalHostUsername(
                    out hostUsername))
            {
                SendAdminResult(
                    sender.Id,
                    false,
                    "HOST_IDENTITY_UNAVAILABLE",
                    "Moderation was blocked because the host identity could not be verified.");

                return true;
            }

            bool targetIsHost =
                string.Equals(
                    target.Username,
                    hostUsername,
                    StringComparison.OrdinalIgnoreCase);

            bool targetIsSender =
                sender.Id == target.Id;

            if (!targetIsHost && !targetIsSender)
            {
                return false;
            }

            string code;
            string message;

            if (targetIsHost)
            {
                code = "HOST_PROTECTED";
                message = "The host cannot be kicked or banned.";
            }
            else
            {
                code = "SELF_ACTION_DENIED";
                message = "You cannot kick or ban yourself.";
            }

            SendAdminResult(
                sender.Id,
                false,
                code,
                message);

            return true;
        }

        private bool IsAdminViewer(IServerPlayer player)
        {
            if (player == null)
            {
                return false;
            }

            if (IsLocalHostPlayer(player))
            {
                return true;
            }

            if (!player.IsAuthorized || _adminStore == null)
            {
                return false;
            }

            string fingerprint = ListHostNotesStore.GetAuthorizationFingerprint(player);
            return !string.IsNullOrWhiteSpace(fingerprint) &&
                   !_adminStore.IsAdminAccessRevoked(fingerprint);
        }

        private static bool IsLocalHostPlayer(
            IServerPlayer player)
        {
            if (player == null)
            {
                return false;
            }

            string hostUsername;

            return
                TryGetLocalHostUsername(
                    out hostUsername) &&
                string.Equals(
                    player.Username,
                    hostUsername,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetLocalHostUsername(
            out string hostUsername)
        {
            hostUsername = null;

            try
            {
                HKMP.List.ListMod mod =
                    HKMP.List.ListMod.Instance;

                if (mod == null ||
                    mod.ClientAddon == null)
                {
                    return false;
                }

                Hkmp.Api.Client.IClientApi clientApi =
                    mod.ClientAddon.Api;

                if (clientApi == null ||
                    clientApi.ClientManager == null)
                {
                    return false;
                }

                string username =
                    clientApi.ClientManager.Username;

                if (string.IsNullOrWhiteSpace(username))
                {
                    return false;
                }

                hostUsername = username.Trim();
                return true;
            }
            catch
            {
                hostUsername = null;
                return false;
            }
        }

        private void HandleKick(
            IServerPlayer sender,
            ushort targetPlayerId)
        {
            IServerPlayer target =
                _api.ServerManager.GetPlayer(
                    targetPlayerId);

            if (target == null)
            {
                SendAdminResult(
                    sender.Id,
                    false,
                    "PLAYER_NOT_FOUND",
                    "Target player is not connected.");

                return;
            }

            if (RejectProtectedModerationTarget(
                    sender,
                    target))
            {
                return;
            }

            try
            {
                _api.ServerManager.DisconnectPlayer(
                    target.Id,
                    DisconnectReason.Kicked);

                SendAdminResult(
                    sender.Id,
                    true,
                    "KICKED",
                    "Player was kicked.");

                Synchronize();
            }
            catch (Exception ex)
            {
                SendAdminResult(
                    sender.Id,
                    false,
                    "KICK_FAILED",
                    ex.Message);
            }
        }

        private void HandleBan(
            IServerPlayer sender,
            ushort targetPlayerId)
        {
            IServerPlayer target =
                _api.ServerManager.GetPlayer(
                    targetPlayerId);

            if (target == null)
            {
                SendAdminResult(
                    sender.Id,
                    false,
                    "PLAYER_NOT_FOUND",
                    "Target player is not connected.");

                return;
            }

            if (RejectProtectedModerationTarget(
                    sender,
                    target))
            {
                return;
            }

            string fingerprint =
                ListHostNotesStore.GetAuthorizationFingerprint(
                    target);

            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                SendAdminResult(
                    sender.Id,
                    false,
                    "IDENTITY_UNAVAILABLE",
                    "Target has no stable authorization identity.");

                return;
            }

            string reason =
                "Banned by " +
                (sender.Username ?? "administrator");

            if (!_adminStore.Ban(
                    target,
                    reason))
            {
                SendAdminResult(
                    sender.Id,
                    false,
                    "BAN_SAVE_FAILED",
                    "The ban could not be stored.");

                return;
            }

            try
            {
                _api.ServerManager.SendMessage(
                    target.Id,
                    "HKMP.List: you have been banned by an administrator.");
            }
            catch
            {
            }

            try
            {
                _api.ServerManager.DisconnectPlayer(
                    target.Id,
                    DisconnectReason.Kicked);
            }
            catch
            {
            }

            SendAdminResult(
                sender.Id,
                true,
                "BANNED",
                "Player was banned.");

            Synchronize();

            IReadOnlyList<IServerPlayer> players =
                _api.ServerManager.Players == null
                    ? new List<IServerPlayer>()
                    : _api.ServerManager.Players
                        .Where(x => x != null)
                        .ToList();

            RefreshAdminStateForAuthorizedViewers(
                players);
        }

        private void HandleUnban(
            IServerPlayer sender,
            string authorizationFingerprint)
        {
            if (string.IsNullOrWhiteSpace(
                    authorizationFingerprint))
            {
                SendAdminResult(
                    sender.Id,
                    false,
                    "IDENTITY_UNAVAILABLE",
                    "Authorization fingerprint is required.");

                return;
            }

            if (!_adminStore.Unban(
                    authorizationFingerprint))
            {
                SendAdminResult(
                    sender.Id,
                    false,
                    "BAN_NOT_FOUND",
                    "No matching ban was found.");

                return;
            }

            SendAdminResult(
                sender.Id,
                true,
                "UNBANNED",
                "Player was unbanned.");

            SendAdminBanList(
                sender.Id);
        }

        private void HandleUnbanAll(
            IServerPlayer sender)
        {
            if (sender == null ||
                _adminStore == null)
            {
                return;
            }

            if (!_adminStore.ClearBans())
            {
                SendAdminResult(
                    sender.Id,
                    false,
                    "BAN_SAVE_FAILED",
                    "The ban list could not be cleared.");

                return;
            }

            SendAdminResult(
                sender.Id,
                true,
                "UNBANNED_ALL",
                "All players were unbanned.");

            IReadOnlyList<IServerPlayer> players =
                _api.ServerManager.Players == null
                    ? new List<IServerPlayer>()
                    : _api.ServerManager.Players
                        .Where(x => x != null)
                        .ToList();

            RefreshAdminStateForAuthorizedViewers(
                players);
        }

        private void OnTeamManagementAction(
            ushort senderId,
            TeamManagementActionData data)
        {
            if (_api == null || _teamStore == null || data == null)
                return;

            IServerPlayer sender = _api.ServerManager.GetPlayer(senderId);
            if (sender == null)
                return;

            switch (data.Action)
            {
                case ListTeamAction.AssignLeader:
                case ListTeamAction.RemoveLeader:
                    {
                        IServerPlayer target = null;
                        Hkmp.Game.Team requestedTeam = data.Team;

                        if (data.Action == ListTeamAction.AssignLeader)
                        {
                            target = _api.ServerManager.GetPlayer(data.TargetPlayerId);
                            if (target == null)
                            {
                                SendAdminResult(sender.Id, false, "PLAYER_NOT_FOUND", "The selected player is no longer connected.");
                                return;
                            }

                            requestedTeam = target.Team;
                            if (!TeamSettingsData.IsPlayableTeam(requestedTeam))
                            {
                                SendAdminResult(sender.Id, false, "PLAYER_HAS_NO_TEAM", "A team leader must belong to one of the four teams. Select a team for the host in HKMP before appointing the host.");
                                return;
                            }
                        }
                        else if (!TeamSettingsData.IsPlayableTeam(requestedTeam))
                        {
                            target = _api.ServerManager.GetPlayer(data.TargetPlayerId);
                            if (target == null || !TeamSettingsData.IsPlayableTeam(target.Team))
                            {
                                SendAdminResult(sender.Id, false, "TEAM_INVALID", "The selected player does not belong to a team.");
                                return;
                            }

                            requestedTeam = target.Team;
                        }

                        AdminPermissions senderPermissions = GetPermissionsForPlayer(sender);
                        bool isHost = IsLocalHostPlayer(sender);
                        bool isTeamLeaderForRequestedTeam =
                            sender.Team == requestedTeam && _teamStore.IsTeamLeader(sender);
                        bool canManageAllTeams = isHost ||
                            (sender.IsAuthorized && senderPermissions != null &&
                             senderPermissions.CanManageTeamLeaders);

                        if (!canManageAllTeams && !isTeamLeaderForRequestedTeam)
                        {
                            SendAdminResult(sender.Id, false, "TEAM_PERMISSION_DENIED",
                                "Only the host, an administrator with team-management permission, or the leader of this team can change its leader.");
                            return;
                        }

                        if (!canManageAllTeams && data.Action == ListTeamAction.AssignLeader &&
                            (target == null || target.Team != sender.Team))
                        {
                            SendAdminResult(sender.Id, false, "TEAM_PERMISSION_DENIED",
                                "A team leader can appoint only a player from their own team.");
                            return;
                        }

                        if (data.Action == ListTeamAction.AssignLeader)
                        {
                            string fingerprint = ListHostNotesStore.GetAuthorizationFingerprint(target);
                            if (string.IsNullOrWhiteSpace(fingerprint) ||
                                !_teamStore.TrySetLeader(requestedTeam, fingerprint, target.Username))
                            {
                                SendAdminResult(sender.Id, false, "TEAM_LEADER_SAVE_FAILED", "Could not save the team leader assignment.");
                                return;
                            }

                            SendAdminResult(sender.Id, true, "TEAM_LEADER_ASSIGNED",
                                target.Username + " is now leader of " + requestedTeam + ".");
                            Synchronize();
                            return;
                        }

                        string expectedFingerprint = null;
                        if (!canManageAllTeams)
                        {
                            expectedFingerprint = ListHostNotesStore.GetAuthorizationFingerprint(sender);
                        }
                        else if (target != null)
                        {
                            expectedFingerprint = ListHostNotesStore.GetAuthorizationFingerprint(target);
                        }

                        if (!_teamStore.TryRemoveLeader(requestedTeam, expectedFingerprint))
                        {
                            SendAdminResult(sender.Id, false, "TEAM_LEADER_REMOVE_FAILED", "Could not remove the current team leader.");
                            return;
                        }

                        SendAdminResult(sender.Id, true, "TEAM_LEADER_REMOVED", "Team leader assignment removed.");
                        Synchronize();
                        return;
                    }

                case ListTeamAction.SetTeamSkin:
                case ListTeamAction.SetTeamSkinLock:
                    {
                        if (!TeamSettingsData.IsPlayableTeam(data.Team))
                        {
                            SendAdminResult(sender.Id, false, "TEAM_INVALID", "A valid team must be selected.");
                            return;
                        }

                        AdminPermissions appearancePermissions = GetPermissionsForPlayer(sender);
                        bool mayManage = IsLocalHostPlayer(sender) ||
                            (sender.IsAuthorized && appearancePermissions != null &&
                             appearancePermissions.CanManageTeamLeaders) ||
                            (_teamStore.IsTeamLeader(sender) && sender.Team == data.Team);
                        if (!mayManage)
                        {
                            SendAdminResult(sender.Id, false, "TEAM_PERMISSION_DENIED",
                                "Only the host or this team's leader can change its skin settings.");
                            return;
                        }

                        if (_api.ServerManager.ServerSettings == null ||
                            !_api.ServerManager.ServerSettings.TeamsEnabled)
                        {
                            SendAdminResult(sender.Id, false, "TEAMS_DISABLED", "Teams must be enabled on the host before team skin settings can be changed.");
                            return;
                        }

                        if (!_api.ServerManager.ServerSettings.AllowSkins)
                        {
                            SendAdminResult(sender.Id, false, "SKINS_DISABLED", "Skin changes are disabled by the host.");
                            return;
                        }

                        bool success = data.Action == ListTeamAction.SetTeamSkin
                            ? _teamStore.TrySetTeamSkin(data.Team, data.SkinId)
                            : _teamStore.TrySetTeamSkinLocked(data.Team, data.Enabled);

                        if (!success)
                        {
                            SendAdminResult(sender.Id, false, "TEAM_SETTINGS_SAVE_FAILED",
                                data.Action == ListTeamAction.SetTeamSkinLock && data.Enabled
                                    ? "Apply a team skin before locking it."
                                    : "Team skin settings could not be saved.");
                            return;
                        }

                        SendAdminResult(sender.Id, true,
                            data.Action == ListTeamAction.SetTeamSkin ? "TEAM_SKIN_UPDATED" : "TEAM_SKIN_LOCK_UPDATED",
                            data.Action == ListTeamAction.SetTeamSkin
                                ? "Team skin updated."
                                : (data.Enabled ? "Team skin locked." : "Team skin unlocked."));
                        Synchronize();
                        return;
                    }

                case ListTeamAction.KickTeamMember:
                    HandlePlayerTeamAssignment(
                        sender,
                        data.TargetPlayerId,
                        Hkmp.Game.Team.None,
                        true);
                    return;

                case ListTeamAction.AssignPlayerToTeam:
                    HandlePlayerTeamAssignment(
                        sender,
                        data.TargetPlayerId,
                        data.Team,
                        false);
                    return;

                case ListTeamAction.SetTeamChangesLock:
                    HandleTeamChangesLock(sender, data.Enabled);
                    return;

                default:
                    SendAdminResult(sender.Id, false, "TEAM_ACTION_UNKNOWN", "Unknown team-management action.");
                    return;
            }
        }

        private void HandlePlayerTeamAssignment(
            IServerPlayer sender,
            ushort targetPlayerId,
            Hkmp.Game.Team requestedTeam,
            bool isTeamKick)
        {
            if (_api == null || _teamStore == null || sender == null)
            {
                return;
            }

            IServerPlayer target = _api.ServerManager.GetPlayer(targetPlayerId);
            if (target == null)
            {
                SendAdminResult(sender.Id, false, "PLAYER_NOT_FOUND", "The selected player is no longer connected.");
                return;
            }

            if (target.Id == sender.Id || IsLocalHostPlayer(target))
            {
                SendAdminResult(sender.Id, false, "TEAM_ASSIGNMENT_DENIED", "You cannot move yourself or the host through this action.");
                return;
            }

            if (requestedTeam != Hkmp.Game.Team.None &&
                !TeamSettingsData.IsPlayableTeam(requestedTeam))
            {
                SendAdminResult(sender.Id, false, "TEAM_INVALID", "A valid team or No Team must be selected.");
                return;
            }

            if (isTeamKick && !TeamSettingsData.IsPlayableTeam(target.Team))
            {
                SendAdminResult(sender.Id, false, "PLAYER_HAS_NO_TEAM", "The selected player is not on a team.");
                return;
            }

            AdminPermissions permissions = GetPermissionsForPlayer(sender);
            bool canManageAllTeams = IsLocalHostPlayer(sender) ||
                (IsAdminViewer(sender) && permissions != null && permissions.CanManageTeamLeaders);
            bool isTeamLeader = _teamStore.IsTeamLeader(sender) &&
                TeamSettingsData.IsPlayableTeam(sender.Team);
            bool canManageOwnTeam = isTeamLeader &&
                (requestedTeam == sender.Team ||
                 (requestedTeam == Hkmp.Game.Team.None && target.Team == sender.Team));

            if (!canManageAllTeams && !canManageOwnTeam)
            {
                SendAdminResult(sender.Id, false, "TEAM_PERMISSION_DENIED",
                    "Only the host, an administrator with team-management permission, or the target's team leader can change this team assignment.");
                return;
            }

            if (!canManageAllTeams && (target.IsAuthorized || _teamStore.IsTeamLeader(target)))
            {
                SendAdminResult(sender.Id, false, "TEAM_ASSIGNMENT_DENIED",
                    "A team leader cannot move an administrator or another team leader.");
                return;
            }

            if (requestedTeam == target.Team)
            {
                SendAdminResult(sender.Id, true, "TEAM_UNCHANGED", "The player is already on that team.");
                return;
            }

            string targetFingerprint = ListHostNotesStore.GetAuthorizationFingerprint(target);
            if (string.IsNullOrWhiteSpace(targetFingerprint))
            {
                SendAdminResult(sender.Id, false, "PLAYER_IDENTITY_UNAVAILABLE", "The player's stable authorization identity is unavailable.");
                return;
            }

            if (_teamStore.IsTeamLeader(target))
            {
                if (!canManageAllTeams ||
                    !_teamStore.TryRemoveLeader(target.Team, targetFingerprint))
                {
                    SendAdminResult(sender.Id, false, "TEAM_ASSIGNMENT_DENIED",
                        "Remove the current team-leader assignment before moving this player.");
                    return;
                }
            }

            if (_teamStore.TeamChangesLocked)
            {
                lock (_teamLockGate)
                {
                    _lockedTeamAssignments[targetFingerprint] = requestedTeam;
                    _lastForcedTeamByPlayer[target.Id] = new ForcedTeamChange
                    {
                        Team = requestedTeam,
                        SentAtUtc = DateTime.UtcNow
                    };
                }
            }

            try
            {
                _sender.SendSingleData(
                    ListServerPacketId.AssignLocalTeam,
                    new TeamAssignmentData { Team = requestedTeam },
                    target.Id);
            }
            catch (Exception exception)
            {
                if (_teamStore.TeamChangesLocked)
                {
                    lock (_teamLockGate)
                    {
                        _lastForcedTeamByPlayer.Remove(target.Id);
                    }
                }

                SendAdminResult(sender.Id, false, "TEAM_ASSIGNMENT_FAILED", exception.Message);
                return;
            }

            SendAdminResult(
                sender.Id,
                true,
                isTeamKick ? "TEAM_MEMBER_REMOVED" : "TEAM_ASSIGNMENT_SENT",
                isTeamKick
                    ? target.Username + " was removed from the team and remains connected."
                    : "Team assignment sent to " + target.Username + ".");
            Synchronize();
        }

        private void HandleTeamChangesLock(
            IServerPlayer sender,
            bool enabled)
        {
            if (sender == null || _teamStore == null)
            {
                return;
            }

            AdminPermissions permissions = GetPermissionsForPlayer(sender);
            bool permitted = IsLocalHostPlayer(sender) ||
                (IsAdminViewer(sender) && permissions != null && permissions.CanManageTeamLeaders);

            if (!permitted)
            {
                SendAdminResult(sender.Id, false, "TEAM_PERMISSION_DENIED",
                    "Only the host or an administrator with team-management permission can change the team lock.");
                return;
            }

            bool wasLocked = _teamStore.TeamChangesLocked;
            if (!_teamStore.TrySetTeamChangesLocked(enabled))
            {
                SendAdminResult(sender.Id, false, "TEAM_SETTINGS_SAVE_FAILED", "Team lock could not be saved.");
                return;
            }

            if (enabled && !wasLocked)
            {
                CaptureLockedTeamAssignments(_api.ServerManager.Players);
            }
            else if (!enabled)
            {
                ClearTeamLockState();
            }

            SendAdminResult(
                sender.Id,
                true,
                "TEAM_CHANGES_LOCK_UPDATED",
                enabled
                    ? "Players can no longer change or leave their teams. Team managers can still assign players."
                    : "Players can change teams again.");

            Synchronize();
        }

        private void CaptureLockedTeamAssignments(
            IEnumerable<IServerPlayer> players)
        {
            lock (_teamLockGate)
            {
                _lockedTeamAssignments.Clear();
                _lastForcedTeamByPlayer.Clear();

                if (players == null)
                {
                    return;
                }

                foreach (IServerPlayer player in players)
                {
                    if (player == null)
                    {
                        continue;
                    }

                    string fingerprint = ListHostNotesStore.GetAuthorizationFingerprint(player);
                    if (!string.IsNullOrWhiteSpace(fingerprint))
                    {
                        _lockedTeamAssignments[fingerprint] = player.Team;
                    }
                }
            }
        }

        private void ClearTeamLockState()
        {
            lock (_teamLockGate)
            {
                _lockedTeamAssignments.Clear();
                _lastForcedTeamByPlayer.Clear();
            }
        }

        private void EnforceTeamChangesLock(
            IEnumerable<IServerPlayer> players)
        {
            if (_teamStore == null || !_teamStore.TeamChangesLocked)
            {
                ClearTeamLockState();
                return;
            }

            if (players == null)
            {
                return;
            }

            foreach (IServerPlayer player in players)
            {
                if (player == null)
                {
                    continue;
                }

                string fingerprint = ListHostNotesStore.GetAuthorizationFingerprint(player);
                if (string.IsNullOrWhiteSpace(fingerprint))
                {
                    continue;
                }

                Hkmp.Game.Team lockedTeam;
                bool shouldSend = false;
                DateTime now = DateTime.UtcNow;

                lock (_teamLockGate)
                {
                    if (!_lockedTeamAssignments.TryGetValue(fingerprint, out lockedTeam))
                    {
                        _lockedTeamAssignments[fingerprint] = player.Team;
                        _lastForcedTeamByPlayer.Remove(player.Id);
                        continue;
                    }

                    if (player.Team == lockedTeam)
                    {
                        _lastForcedTeamByPlayer.Remove(player.Id);
                        continue;
                    }

                    ForcedTeamChange previous;
                    if (!_lastForcedTeamByPlayer.TryGetValue(player.Id, out previous) ||
                        previous.Team != lockedTeam ||
                        now - previous.SentAtUtc >= TimeSpan.FromSeconds(1))
                    {
                        _lastForcedTeamByPlayer[player.Id] = new ForcedTeamChange
                        {
                            Team = lockedTeam,
                            SentAtUtc = now
                        };
                        shouldSend = true;
                    }
                }

                if (shouldSend)
                {
                    try
                    {
                        _sender.SendSingleData(
                            ListServerPacketId.AssignLocalTeam,
                            new TeamAssignmentData { Team = lockedTeam },
                            player.Id);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private sealed class ForcedTeamChange
        {
            public Hkmp.Game.Team Team { get; set; }
            public DateTime SentAtUtc { get; set; }
        }

        private void OnAdminPermissionsChanged(
            ushort senderId,
            AdminPermissionsChangedData data)
        {
            if (_api == null || _adminStore == null || data == null || data.Permissions == null)
            {
                return;
            }

            IServerPlayer sender = _api.ServerManager.GetPlayer(senderId);
            if (sender == null || !IsAdminViewer(sender))
            {
                SendAdminResult(senderId, false, "NOT_AUTHORIZED",
                    "Only an active administrator can manage administrator permissions.");
                return;
            }

            bool senderIsHost = IsLocalHostPlayer(sender);
            if (!senderIsHost && !RequirePermission(
                    sender, p => p.CanManageAdministrators, "manage administrator permissions"))
            {
                return;
            }

            string fingerprint = data.AuthorizationFingerprint;
            if (!IsValidFingerprint(fingerprint))
            {
                SendAdminResult(senderId, false, "IDENTITY_INVALID",
                    "The selected administrator identity is not valid.");
                return;
            }

            string senderFingerprint = ListHostNotesStore.GetAuthorizationFingerprint(sender);
            if (!senderIsHost && string.Equals(
                    senderFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                SendAdminResult(senderId, false, "SELF_PERMISSION_CHANGE_DENIED",
                    "Administrators cannot change their own permissions.");
                return;
            }

            bool isHostIdentity = GetCurrentPlayers().Any(player =>
                player != null && IsLocalHostPlayer(player) &&
                string.Equals(
                    ListHostNotesStore.GetAuthorizationFingerprint(player),
                    fingerprint, StringComparison.OrdinalIgnoreCase));
            if (isHostIdentity)
            {
                SendAdminResult(senderId, false, "HOST_PERMISSIONS_LOCKED",
                    "The host's permissions cannot be changed.");
                return;
            }

            if (!_adminStore.IsKnownAdministratorFingerprint(fingerprint))
            {
                SendAdminResult(senderId, false, "IDENTITY_INVALID",
                    "The selected identity has not been an HKMP administrator.");
                return;
            }

            AdminPermissions currentPermissions = _adminStore.GetStoredPermissions(fingerprint);
            if (!senderIsHost &&
                currentPermissions != null &&
                currentPermissions.CanManageAdministrators != data.Permissions.CanManageAdministrators)
            {
                SendAdminResult(senderId, false, "DELEGATION_HOST_ONLY",
                    "Only the host can grant or revoke administrator-permission management.");
                return;
            }

            if (!_adminStore.SetPermissions(fingerprint, data.Permissions))
            {
                SendAdminResult(senderId, false, "PERMISSIONS_SAVE_FAILED",
                    "Administrator permissions could not be saved.");
                return;
            }

            SendAdminResult(senderId, true, "PERMISSIONS_UPDATED",
                "Administrator permissions saved.");
            RefreshAuthorizationData(GetCurrentPlayers());
        }

        private void OnAdminIdentityNoteChanged(
            ushort senderId,
            AdminIdentityNoteChangedData data)
        {
            if (_api == null ||
                _adminStore == null ||
                data == null)
            {
                return;
            }

            IServerPlayer sender =
                _api.ServerManager.GetPlayer(
                    senderId);

            if (sender == null || !IsAdminViewer(sender))
            {
                if (sender != null)
                {
                    SendAdminResult(
                        senderId,
                        false,
                        "NOT_AUTHORIZED",
                        "You are not permitted to edit player identity notes.");
                }

                return;
            }

            if (!RequirePermission(
                    sender, p => p.CanManageIdentityData, "manage identity data"))
            {
                return;
            }

            if (!IsValidFingerprint(
                    data.AuthorizationFingerprint))
            {
                SendAdminResult(
                    senderId,
                    false,
                    "IDENTITY_INVALID",
                    "The player identity fingerprint is invalid.");

                return;
            }

            if (!_adminStore.IsKnownIdentityFingerprint(
                    data.AuthorizationFingerprint))
            {
                SendAdminResult(
                    senderId,
                    false,
                    "IDENTITY_UNKNOWN",
                    "The selected player identity is not known to this host.");

                return;
            }

            if (!_adminStore.SetIdentityNote(
                    data.AuthorizationFingerprint,
                    data.Note))
            {
                SendAdminResult(
                    senderId,
                    false,
                    "NOTE_SAVE_FAILED",
                    "The player identity note could not be saved.");

                return;
            }

            IReadOnlyList<IServerPlayer> players =
                _api.ServerManager.Players == null
                    ? new List<IServerPlayer>()
                    : _api.ServerManager.Players
                        .Where(x => x != null)
                        .ToList();

            RefreshAdminIdentityForAuthorizedViewers(
                players,
                data.AuthorizationFingerprint);
        }

        private void OnAdminIdentityBundleAction(
            ushort senderId,
            AdminIdentityBundleActionData data)
        {
            if (_api == null || _adminStore == null || data == null)
            {
                return;
            }

            IServerPlayer sender =
                _api.ServerManager.GetPlayer(senderId);

            if (sender == null || !IsAdminViewer(sender))
            {
                if (sender != null)
                {
                    SendAdminResult(
                        senderId,
                        false,
                        "NOT_AUTHORIZED",
                        "You are not authorized to manage identity bundles.");
                }

                return;
            }

            if (!RequirePermission(
                    sender, p => p.CanManageIdentityData, "manage identity data and bundles"))
            {
                return;
            }

            switch (data.Action)
            {
                case AdminIdentityBundleAction.ClearNotes:
                    {
                        if (!_adminStore.ClearIdentityNotes())
                        {
                            SendAdminResult(
                                senderId,
                                false,
                                "IDENTITY_NOTES_CLEAR_FAILED",
                                "All identity notes could not be saved as cleared.");
                            return;
                        }

                        SendAdminResult(
                            senderId,
                            true,
                            "IDENTITY_NOTES_CLEARED",
                            "All administrator identity notes were cleared.");

                        RefreshAdminStateForAuthorizedViewers(
                            GetCurrentPlayers());
                        return;
                    }

                case AdminIdentityBundleAction.ExportBundle:
                    {
                        string fileName;
                        string message;
                        bool success = _adminStore.ExportIdentityBundle(
                            out fileName,
                            out message);

                        SendAdminResult(
                            senderId,
                            success,
                            success ? "IDENTITY_BUNDLE_EXPORTED" : "IDENTITY_BUNDLE_EXPORT_FAILED",
                            message);
                        return;
                    }

                case AdminIdentityBundleAction.ListBundles:
                    {
                        IReadOnlyList<string> files =
                            _adminStore.GetIdentityBundleFileNames();

                        _sender.SendSingleData(
                            ListServerPacketId.AdminIdentityBundleFiles,
                            new AdminIdentityBundleFilesData
                            {
                                Success = true,
                                Message = files.Count == 0
                                    ? "No identity bundles were found in HKMP.List/IdentityBundles."
                                    : "Select an identity bundle to import.",
                                FileNames = files.ToList()
                            },
                            senderId);
                        return;
                    }

                case AdminIdentityBundleAction.ImportBundle:
                    {
                        string message;
                        bool success = _adminStore.ImportIdentityBundle(
                            data.FileName,
                            out message);

                        SendAdminResult(
                            senderId,
                            success,
                            success ? "IDENTITY_BUNDLE_IMPORTED" : "IDENTITY_BUNDLE_IMPORT_FAILED",
                            message);

                        if (success)
                        {
                            RefreshAdminStateForAuthorizedViewers(
                                GetCurrentPlayers());
                        }

                        return;
                    }

                default:
                    SendAdminResult(
                        senderId,
                        false,
                        "IDENTITY_BUNDLE_ACTION_INVALID",
                        "Unknown identity bundle action.");
                    return;
            }
        }

        private IReadOnlyList<IServerPlayer> GetCurrentPlayers()
        {
            return _api == null || _api.ServerManager.Players == null
                ? (IReadOnlyList<IServerPlayer>)new List<IServerPlayer>()
                : _api.ServerManager.Players
                    .Where(player => player != null)
                    .ToList();
        }

        private static bool IsValidFingerprint(
            string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length !=
                ListProtocol.MaxAuthorizationFingerprintLength)
            {
                return false;
            }

            for (int i = 0;
                 i < value.Length;
                 i++)
            {
                char c =
                    value[i];

                bool valid =
                    (c >= '0' && c <= '9') ||
                    (c >= 'a' && c <= 'f') ||
                    (c >= 'A' && c <= 'F');

                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        private void OnAdminServerSettingsChanged(
            ushort senderId,
            ServerSettingsData data)
        {
            if (_api == null || data == null)
            {
                return;
            }

            IServerPlayer sender =
                _api.ServerManager.GetPlayer(senderId);

            if (sender == null)
            {
                return;
            }

            bool isLocalHost = IsLocalHostPlayer(sender);

            if (!isLocalHost && !IsAdminViewer(sender))
            {
                return;
            }

            AdminPermissions permissions =
                GetPermissionsForPlayer(sender);

            if (!isLocalHost &&
                (permissions == null || !permissions.CanChangeServerSettings))
            {
                return;
            }

            ServerSettingsData snapshot = data.Clone();

            if (!isLocalHost &&
                (permissions == null || !permissions.CanChangeServerSettingsDirectly))
            {
                StoreSettingsProposal(
                    sender,
                    snapshot);
                return;
            }

            try
            {
                ApplyServerSettings(snapshot);
                Synchronize();
                SendAdminResult(
                    senderId,
                    true,
                    "SETTINGS_APPLIED",
                    "Your saved HKMP server settings were applied.");
            }
            catch (Exception ex)
            {
                SendAdminResult(
                    senderId,
                    false,
                    "SETTINGS_FAILED",
                    ex.Message);
            }
        }

        private void StoreSettingsProposal(
            IServerPlayer sender,
            ServerSettingsData settings)
        {
            if (sender == null || settings == null)
            {
                return;
            }

            string fingerprint =
                ListHostNotesStore.GetAuthorizationFingerprint(sender) ?? string.Empty;

            ServerSettingsProposalData proposal =
                new ServerSettingsProposalData
                {
                    ProposalId = Guid.NewGuid().ToString("N"),
                    SubmitterUsername = TruncateUsername(sender.Username),
                    AuthorizationFingerprint = fingerprint,
                    SubmittedUtcTicks = (ulong)DateTime.UtcNow.Ticks,
                    Settings = settings.Clone(),
                    IsRemoved = false
                };

            lock (_settingsProposalGate)
            {
                ServerSettingsProposalData existing =
                    _settingsProposals.Values.FirstOrDefault(item =>
                        item != null &&
                        (!string.IsNullOrWhiteSpace(fingerprint)
                            ? string.Equals(item.AuthorizationFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
                            : string.Equals(item.SubmitterUsername, proposal.SubmitterUsername, StringComparison.OrdinalIgnoreCase)));

                if (existing != null)
                {
                    proposal.ProposalId = existing.ProposalId;
                }

                _settingsProposals[proposal.ProposalId] = proposal.Clone();

                while (_settingsProposals.Count > ListProtocol.MaxSettingsProposals)
                {
                    string oldestId = null;
                    ulong oldestTicks = ulong.MaxValue;

                    foreach (KeyValuePair<string, ServerSettingsProposalData> pair in _settingsProposals)
                    {
                        if (pair.Value != null && pair.Value.SubmittedUtcTicks < oldestTicks)
                        {
                            oldestTicks = pair.Value.SubmittedUtcTicks;
                            oldestId = pair.Key;
                        }
                    }

                    if (oldestId == null)
                    {
                        break;
                    }

                    _settingsProposals.Remove(oldestId);
                }
            }

            SendSettingsProposalToHost(proposal);
        }

        private void SendSettingsProposalToHost(
            ServerSettingsProposalData proposal)
        {
            if (_api == null || _sender == null || proposal == null)
            {
                return;
            }

            List<IServerPlayer> players =
                _api.ServerManager.Players == null
                    ? new List<IServerPlayer>()
                    : _api.ServerManager.Players.ToList();

            foreach (IServerPlayer player in players)
            {
                if (player == null || !IsLocalHostPlayer(player))
                {
                    continue;
                }

                try
                {
                    _sender.SendCollectionData(
                        ListServerPacketId.SettingsProposal,
                        proposal.Clone(),
                        player.Id);
                }
                catch
                {
                }

                return;
            }
        }

        private void SendPendingSettingsProposalsToHost(
            ushort hostPlayerId)
        {
            List<ServerSettingsProposalData> proposals;

            lock (_settingsProposalGate)
            {
                proposals = _settingsProposals.Values
                    .Where(x => x != null)
                    .OrderBy(x => x.SubmittedUtcTicks)
                    .Select(x => x.Clone())
                    .ToList();
            }

            foreach (ServerSettingsProposalData proposal in proposals)
            {
                try
                {
                    _sender.SendCollectionData(
                        ListServerPacketId.SettingsProposal,
                        proposal,
                        hostPlayerId);
                }
                catch
                {
                }
            }
        }

        private void OnSettingsProposalAction(
            ushort senderId,
            ServerSettingsProposalActionData data)
        {
            if (_api == null || data == null ||
                string.IsNullOrWhiteSpace(data.ProposalId))
            {
                return;
            }

            IServerPlayer sender = _api.ServerManager.GetPlayer(senderId);
            if (sender == null || !IsLocalHostPlayer(sender))
            {
                return;
            }

            ServerSettingsProposalData proposal;
            lock (_settingsProposalGate)
            {
                if (!_settingsProposals.TryGetValue(data.ProposalId, out proposal) ||
                    proposal == null ||
                    !_processingSettingsProposals.Add(data.ProposalId))
                {
                    return;
                }
            }

            bool completed = false;
            try
            {
                if (data.Action == ServerSettingsProposalAction.Approve)
                {
                    ApplyServerSettings(proposal.Settings);
                    Synchronize();
                }
                else if (data.Action != ServerSettingsProposalAction.Reject)
                {
                    return;
                }

                lock (_settingsProposalGate)
                {
                    _settingsProposals.Remove(data.ProposalId);
                }

                completed = true;
                bool adoptSettings =
                    data.Action == ServerSettingsProposalAction.Approve;

                SendRemovedSettingsProposal(
                    sender.Id,
                    data.ProposalId,
                    adoptSettings,
                    adoptSettings ? proposal.Settings : null);

                SendAdminResult(
                    sender.Id,
                    true,
                    data.Action == ServerSettingsProposalAction.Approve
                        ? "SETTINGS_PROPOSAL_APPROVED"
                        : "SETTINGS_PROPOSAL_REJECTED",
                    data.Action == ServerSettingsProposalAction.Approve
                        ? "Administrator settings suggestion approved and applied."
                        : "Administrator settings suggestion rejected.");
            }
            catch (Exception ex)
            {
                SendAdminResult(
                    sender.Id,
                    false,
                    "SETTINGS_PROPOSAL_FAILED",
                    ex.Message);
            }
            finally
            {
                lock (_settingsProposalGate)
                {
                    _processingSettingsProposals.Remove(data.ProposalId);
                    if (!completed && proposal != null &&
                        !_settingsProposals.ContainsKey(data.ProposalId))
                    {
                        _settingsProposals[data.ProposalId] = proposal;
                    }
                }
            }
        }

        private void SendRemovedSettingsProposal(
            ushort hostPlayerId,
            string proposalId,
            bool adoptSettings,
            ServerSettingsData adoptedSettings)
        {
            try
            {
                _sender.SendCollectionData(
                    ListServerPacketId.SettingsProposal,
                    new ServerSettingsProposalData
                    {
                        ProposalId = proposalId,
                        SubmitterUsername = string.Empty,
                        AuthorizationFingerprint = string.Empty,
                        SubmittedUtcTicks = 0,
                        IsRemoved = true,
                        AdoptSettings = adoptSettings,
                        Settings = adoptedSettings == null
                            ? null
                            : adoptedSettings.Clone()
                    },
                    hostPlayerId);
            }
            catch
            {
            }
        }

        private static string TruncateUsername(string username)
        {
            string value = username ?? string.Empty;
            return value.Length <= ListProtocol.MaxUsernameLength
                ? value
                : value.Substring(0, ListProtocol.MaxUsernameLength);
        }

        private void ApplyServerSettings(
            ServerSettingsData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            ServerSettings settings = new ServerSettings
            {
                IsPvpEnabled = data.IsPvpEnabled,
                IsBodyDamageEnabled = data.IsBodyDamageEnabled,
                AlwaysShowMapIcons = data.AlwaysShowMapIcons,
                OnlyBroadcastMapIconWithWaywardCompass = data.OnlyBroadcastMapIconWithWaywardCompass,
                DisplayNames = data.DisplayNames,
                TeamsEnabled = data.TeamsEnabled,
                AllowSkins = data.AllowSkins,
                AllowParries = data.AllowParries,
                NailDamage = data.NailDamage,
                GrubberflyElegyDamage = data.GrubberflyElegyDamage,
                VengefulSpiritDamage = data.VengefulSpiritDamage,
                ShadeSoulDamage = data.ShadeSoulDamage,
                DesolateDiveDamage = data.DesolateDiveDamage,
                DescendingDarkDamage = data.DescendingDarkDamage,
                HowlingWraithDamage = data.HowlingWraithDamage,
                AbyssShriekDamage = data.AbyssShriekDamage,
                GreatSlashDamage = data.GreatSlashDamage,
                DashSlashDamage = data.DashSlashDamage,
                CycloneSlashDamage = data.CycloneSlashDamage,
                SporeShroomDamage = data.SporeShroomDamage,
                SporeDungShroomDamage = data.SporeDungShroomDamage,
                ThornOfAgonyDamage = data.ThornOfAgonyDamage,
                SharpShadowDamage = data.SharpShadowDamage
            };

            _api.ServerManager.ApplyServerSettings(settings);
        }

        private void SendAdminResult(
            ushort playerId,
            bool success,
            string code,
            string message)
        {
            try
            {
                _sender.SendSingleData(
                    ListServerPacketId.AdminActionResult,
                    new AdminActionResultData
                    {
                        Success = success,
                        Code = Truncate(code),
                        Message = Truncate(message)
                    },
                    playerId);
            }
            catch
            {
            }
        }

        private PlayerState CreatePlayerState(
            IServerPlayer player)
        {
            if (player == null)
                return null;

            bool hasTeamSkin;
            byte teamSkinId;
            bool teamSkinLocked;
            long teamSkinUpdatedAtUtcTicks;
            bool isTeamLeader = _teamStore != null && _teamStore.IsTeamLeader(player);

            if (_teamStore == null || !_teamStore.TryGetTeamAppearance(
                player.Team, out hasTeamSkin, out teamSkinId, out teamSkinLocked,
                out teamSkinUpdatedAtUtcTicks))
            {
                hasTeamSkin = false;
                teamSkinId = 0;
                teamSkinLocked = false;
                teamSkinUpdatedAtUtcTicks = 0;
            }

            bool hasListSkinOverride = false;
            byte listSkinOverrideId = 0;
            long listSkinUpdatedAtUtcTicks = 0;
            lock (_gate)
            {
                SkinPreferenceState preference;
                if (_skinPreferences.TryGetValue(player.Id, out preference) && preference != null)
                {
                    hasListSkinOverride = preference.HasListSkinOverride;
                    listSkinOverrideId = hasListSkinOverride ? preference.ListSkinOverrideId : (byte)0;
                    listSkinUpdatedAtUtcTicks = preference.UpdatedAtUtcTicks;
                }
            }

            bool isHost = IsLocalHostPlayer(player);
            return new PlayerState
            {
                Id = player.Id,
                Username = player.Username ?? string.Empty,
                Team = player.Team,
                SkinId = player.SkinId,
                IsAuthorized = IsAdminViewer(player),
                IsHost = isHost,
                IsTeamLeader = isTeamLeader,
                HasTeamSkin = hasTeamSkin,
                TeamSkinId = teamSkinId,
                IsTeamSkinLocked = teamSkinLocked,
                HasListSkinOverride = hasListSkinOverride,
                ListSkinOverrideId = listSkinOverrideId,
                TeamSkinUpdatedAtUtcTicks = teamSkinUpdatedAtUtcTicks,
                ListSkinUpdatedAtUtcTicks = listSkinUpdatedAtUtcTicks
            };
        }

        private PlayerListEntryData CreatePlayerEntry(
            ulong snapshotId,
            IServerPlayer player)
        {
            PlayerState state = CreatePlayerState(player);
            return new PlayerListEntryData
            {
                SnapshotId = snapshotId,
                PlayerId = player.Id,
                Username = player.Username ?? string.Empty,
                AuthorizationFingerprint = ListHostNotesStore.GetAuthorizationFingerprint(player),
                Team = player.Team,
                SkinId = player.SkinId,
                IsHost = state.IsHost,
                IsAuthorized = state.IsAuthorized,
                IsTeamLeader = state.IsTeamLeader,
                HasTeamSkin = state.HasTeamSkin,
                TeamSkinId = state.TeamSkinId,
                IsTeamSkinLocked = state.IsTeamSkinLocked,
                HasListSkinOverride = state.HasListSkinOverride,
                ListSkinOverrideId = state.ListSkinOverrideId,
                TeamSkinUpdatedAtUtcTicks = state.TeamSkinUpdatedAtUtcTicks,
                ListSkinUpdatedAtUtcTicks = state.ListSkinUpdatedAtUtcTicks
            };
        }

        private TeamSettingsData CreateTeamSettingsData(
            IEnumerable<IServerPlayer> players)
        {
            return new TeamSettingsData
            {
                TeamChangesLocked = _teamStore != null && _teamStore.TeamChangesLocked,
                Entries = _teamStore == null
                    ? new List<TeamSettingsEntryData>()
                    : _teamStore.CreateSnapshot(players).Select(entry => entry.Clone()).ToList()
            };
        }

        private static string CreateTeamSettingsSignature(
            TeamSettingsData data)
        {
            if (data == null || data.Entries == null)
                return string.Empty;

            return (data.TeamChangesLocked ? "LOCKED|" : "OPEN|") + string.Join("|", data.Entries
                .Where(entry => entry != null)
                .OrderBy(entry => (byte)entry.Team)
                .Select(entry =>
                    ((byte)entry.Team).ToString() + ":" +
                    (entry.HasAssignedLeader ? "1" : "0") + ":" +
                    (entry.HasConnectedLeader ? "1" : "0") + ":" +
                    entry.LeaderPlayerId.ToString() + ":" +
                    (entry.LeaderUsername ?? string.Empty) + ":" +
                    (entry.HasTeamSkin ? "1" : "0") + ":" +
                    entry.TeamSkinId.ToString() + ":" +
                    (entry.IsSkinLocked ? "1" : "0") + ":" +
                    entry.SkinUpdatedAtUtcTicks.ToString()));
        }

        private AdminPlayerData CreateAdminPlayerData(
            IServerPlayer player)
        {
            return new AdminPlayerData
            {
                PlayerId = player.Id,
                Username = player.Username ?? string.Empty,
                IsAuthorized = player.IsAuthorized || IsLocalHostPlayer(player),
                IsAdminEnabled = IsAdminViewer(player),
                IsHost = IsLocalHostPlayer(player),
                AuthorizationFingerprint =
                    ListHostNotesStore.GetAuthorizationFingerprint(
                        player),

                AuthorizationKey =
                    string.IsNullOrWhiteSpace(player.AuthKey) ||
                    player.AuthKey.Length > ListProtocol.MaxAuthorizationKeyLength
                        ? string.Empty
                        : player.AuthKey,

                AuthorizationNote =
                    _adminStore == null
                        ? string.Empty
                        : _adminStore.GetIdentityNote(
                            ListHostNotesStore.GetAuthorizationFingerprint(
                                player)),

                Permissions = IsLocalHostPlayer(player)
                    ? AdminPermissions.FullAccess()
                    : _adminStore == null
                        ? AdminPermissions.NoAccess()
                        : _adminStore.GetStoredPermissions(
                            ListHostNotesStore.GetAuthorizationFingerprint(player))
            };
        }

        private AdminPlayerData CreateAdminPlayerData(
            IServerPlayer target,
            IServerPlayer viewer)
        {
            AdminPlayerData data = CreateAdminPlayerData(target);
            if (data == null || viewer == null || !IsAdminViewer(viewer) ||
                IsLocalHostPlayer(viewer))
            {
                return data;
            }

            AdminPermissions viewerPermissions = GetPermissionsForPlayer(viewer);
            if (viewerPermissions != null && viewerPermissions.CanManageIdentityData)
            {
                return data;
            }
            data.AuthorizationKey = string.Empty;
            data.AuthorizationNote = string.Empty;
            if (viewerPermissions == null || !viewerPermissions.CanManageAdministrators)
            {
                data.AuthorizationFingerprint = string.Empty;
            }

            return data;
        }

        private static ServerSettingsData CreateServerSettings(
            IServerSettings settings)
        {
            if (settings == null)
            {
                return new ServerSettingsData();
            }

            return new ServerSettingsData
            {
                IsPvpEnabled = settings.IsPvpEnabled,
                IsBodyDamageEnabled = settings.IsBodyDamageEnabled,
                AlwaysShowMapIcons = settings.AlwaysShowMapIcons,
                OnlyBroadcastMapIconWithWaywardCompass = settings.OnlyBroadcastMapIconWithWaywardCompass,
                DisplayNames = settings.DisplayNames,
                TeamsEnabled = settings.TeamsEnabled,
                AllowSkins = settings.AllowSkins,
                AllowParries = settings.AllowParries,
                NailDamage = settings.NailDamage,
                GrubberflyElegyDamage = settings.GrubberflyElegyDamage,
                VengefulSpiritDamage = settings.VengefulSpiritDamage,
                ShadeSoulDamage = settings.ShadeSoulDamage,
                DesolateDiveDamage = settings.DesolateDiveDamage,
                DescendingDarkDamage = settings.DescendingDarkDamage,
                HowlingWraithDamage = settings.HowlingWraithDamage,
                AbyssShriekDamage = settings.AbyssShriekDamage,
                GreatSlashDamage = settings.GreatSlashDamage,
                DashSlashDamage = settings.DashSlashDamage,
                CycloneSlashDamage = settings.CycloneSlashDamage,
                SporeShroomDamage = settings.SporeShroomDamage,
                SporeDungShroomDamage = settings.SporeDungShroomDamage,
                ThornOfAgonyDamage = settings.ThornOfAgonyDamage,
                SharpShadowDamage = settings.SharpShadowDamage
            };
        }

        private static bool PlayerStateMapsEqual(
            IDictionary<ushort, PlayerState> first,
            IDictionary<ushort, PlayerState> second)
        {
            if (first.Count != second.Count)
            {
                return false;
            }

            foreach (KeyValuePair<ushort, PlayerState> pair in first)
            {
                PlayerState other;
                if (!second.TryGetValue(pair.Key, out other))
                {
                    return false;
                }

                if (!pair.Value.Equals(other))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ServerSettingsEqual(
            ServerSettingsData first,
            ServerSettingsData second)
        {
            if (first == null || second == null)
            {
                return first == null && second == null;
            }

            return
                first.IsPvpEnabled == second.IsPvpEnabled &&
                first.IsBodyDamageEnabled == second.IsBodyDamageEnabled &&
                first.AlwaysShowMapIcons == second.AlwaysShowMapIcons &&
                first.OnlyBroadcastMapIconWithWaywardCompass == second.OnlyBroadcastMapIconWithWaywardCompass &&
                first.DisplayNames == second.DisplayNames &&
                first.TeamsEnabled == second.TeamsEnabled &&
                first.AllowSkins == second.AllowSkins &&
                first.AllowParries == second.AllowParries &&
                first.NailDamage == second.NailDamage &&
                first.GrubberflyElegyDamage == second.GrubberflyElegyDamage &&
                first.VengefulSpiritDamage == second.VengefulSpiritDamage &&
                first.ShadeSoulDamage == second.ShadeSoulDamage &&
                first.DesolateDiveDamage == second.DesolateDiveDamage &&
                first.DescendingDarkDamage == second.DescendingDarkDamage &&
                first.HowlingWraithDamage == second.HowlingWraithDamage &&
                first.AbyssShriekDamage == second.AbyssShriekDamage &&
                first.GreatSlashDamage == second.GreatSlashDamage &&
                first.DashSlashDamage == second.DashSlashDamage &&
                first.CycloneSlashDamage == second.CycloneSlashDamage &&
                first.SporeShroomDamage == second.SporeShroomDamage &&
                first.SporeDungShroomDamage == second.SporeDungShroomDamage &&
                first.ThornOfAgonyDamage == second.ThornOfAgonyDamage &&
                first.SharpShadowDamage == second.SharpShadowDamage;
        }

        private static string Truncate(
            string value)
        {
            string result = value ?? string.Empty;

            return result.Length <= ListProtocol.MaxActionMessageLength
                ? result
                : result.Substring(
                    0,
                    ListProtocol.MaxActionMessageLength);
        }

        private static IPacketData InstantiatePacket(
            ListClientPacketId packetId)
        {
            switch (packetId)
            {
                case ListClientPacketId.NoteChanged:
                    return new PacketDataCollection<PlayerNoteData>();

                case ListClientPacketId.AdminAction:
                    return new AdminActionData();

                case ListClientPacketId.AdminServerSettingsChanged:
                    return new ServerSettingsData();

                case ListClientPacketId.AdminIdentityNoteChanged:
                    return new AdminIdentityNoteChangedData();

                case ListClientPacketId.AdminIdentityBundleAction:
                    return new AdminIdentityBundleActionData();

                case ListClientPacketId.AdminPermissionsChanged:
                    return new AdminPermissionsChangedData();

                case ListClientPacketId.SettingsProposalAction:
                    return new ServerSettingsProposalActionData();

                case ListClientPacketId.TeamManagementAction:
                    return new TeamManagementActionData();

                case ListClientPacketId.SkinPreferenceChanged:
                    return new SkinPreferenceChangedData();

                default:
                    return null;
            }
        }

        private sealed class SkinPreferenceState
        {
            public bool HasListSkinOverride { get; set; }
            public byte ListSkinOverrideId { get; set; }
            public long UpdatedAtUtcTicks { get; set; }
        }

        private sealed class ServerUpdateRunner :
            MonoBehaviour
        {
            private ListServerAddon _owner;
            private float _intervalSeconds;
            private float _nextSyncTime;

            public void Initialize(
                ListServerAddon owner,
                int intervalMilliseconds)
            {
                _owner =
                    owner;

                _intervalSeconds =
                    Math.Max(
                        50,
                        intervalMilliseconds) /
                    1000f;

                _nextSyncTime =
                    0f;

                enabled =
                    true;
            }

            private void Update()
            {
                if (_owner == null)
                {
                    enabled = false;
                    return;
                }

                if (Time.unscaledTime <
                    _nextSyncTime)
                {
                    return;
                }

                _nextSyncTime =
                    Time.unscaledTime +
                    _intervalSeconds;

                _owner.Synchronize();
            }
        }

        private sealed class PlayerState
        {
            public ushort Id { get; set; }
            public string Username { get; set; }
            public Hkmp.Game.Team Team { get; set; }
            public byte SkinId { get; set; }
            public bool IsAuthorized { get; set; }
            public bool IsHost { get; set; }
            public bool IsTeamLeader { get; set; }
            public bool HasTeamSkin { get; set; }
            public byte TeamSkinId { get; set; }
            public bool IsTeamSkinLocked { get; set; }
            public bool HasListSkinOverride { get; set; }
            public byte ListSkinOverrideId { get; set; }
            public long TeamSkinUpdatedAtUtcTicks { get; set; }
            public long ListSkinUpdatedAtUtcTicks { get; set; }

            public override bool Equals(object obj)
            {
                PlayerState other = obj as PlayerState;

                if (other == null)
                {
                    return false;
                }

                return
                    Id == other.Id &&
                    string.Equals(
                        Username,
                        other.Username,
                        StringComparison.Ordinal) &&
                    Team == other.Team &&
                    SkinId == other.SkinId &&
                    IsAuthorized == other.IsAuthorized &&
                    IsHost == other.IsHost &&
                    IsTeamLeader == other.IsTeamLeader &&
                    HasTeamSkin == other.HasTeamSkin &&
                    TeamSkinId == other.TeamSkinId &&
                    IsTeamSkinLocked == other.IsTeamSkinLocked &&
                    HasListSkinOverride == other.HasListSkinOverride &&
                    ListSkinOverrideId == other.ListSkinOverrideId &&
                    TeamSkinUpdatedAtUtcTicks == other.TeamSkinUpdatedAtUtcTicks &&
                    ListSkinUpdatedAtUtcTicks == other.ListSkinUpdatedAtUtcTicks;
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Id.GetHashCode();
                    hash = (hash * 397) ^ (Username ?? string.Empty).GetHashCode();
                    hash = (hash * 397) ^ Team.GetHashCode();
                    hash = (hash * 397) ^ SkinId.GetHashCode();
                    hash = (hash * 397) ^ IsAuthorized.GetHashCode();
                    hash = (hash * 397) ^ IsHost.GetHashCode();
                    hash = (hash * 397) ^ IsTeamLeader.GetHashCode();
                    hash = (hash * 397) ^ HasTeamSkin.GetHashCode();
                    hash = (hash * 397) ^ TeamSkinId.GetHashCode();
                    hash = (hash * 397) ^ IsTeamSkinLocked.GetHashCode();
                    hash = (hash * 397) ^ HasListSkinOverride.GetHashCode();
                    hash = (hash * 397) ^ ListSkinOverrideId.GetHashCode();
                    hash = (hash * 397) ^ TeamSkinUpdatedAtUtcTicks.GetHashCode();
                    hash = (hash * 397) ^ ListSkinUpdatedAtUtcTicks.GetHashCode();
                    return hash;
                }
            }
        }
    }
}
