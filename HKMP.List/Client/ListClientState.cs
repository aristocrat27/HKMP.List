using System;
using System.Collections.Generic;
using System.Linq;
using HKMP.List.Shared;

namespace HKMP.List.Client
{
    public sealed class ListClientState
    {
        private readonly object _gate =
            new object();

        private readonly Dictionary<ushort, ListPlayerView> _players =
            new Dictionary<ushort, ListPlayerView>();

        private readonly Dictionary<ushort, string> _notes =
            new Dictionary<ushort, string>();

        private readonly Dictionary<ushort, string> _noteIdentityKeysByPlayerId =
            new Dictionary<ushort, string>();

        private ListClientNotesStore _localNotesStore;

        private readonly Dictionary<ushort, ListAdminPlayerView> _adminPlayers =
            new Dictionary<ushort, ListAdminPlayerView>();

        private readonly Dictionary<string, AdminBanEntryView> _bans =
            new Dictionary<string, AdminBanEntryView>(
                StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, ServerSettingsProposalData> _settingsProposals =
            new Dictionary<string, ServerSettingsProposalData>(
                StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<Hkmp.Game.Team, TeamSettingsEntryData> _teamSettings =
            new Dictionary<Hkmp.Game.Team, TeamSettingsEntryData>();

        private const int MaximumPendingSnapshots = 8;
        private const int MaximumBufferedSnapshotPlayers = 1024;

        private readonly Dictionary<ulong, PendingSnapshot> _pendingSnapshots =
            new Dictionary<ulong, PendingSnapshot>();

        private ulong _latestCommittedSnapshotId;
        private bool _hasServerSnapshot;

        private ServerSettingsData _serverSettings;
        private bool _teamChangesLocked;
        private bool _isAuthorized;
        private bool _isHost;
        private AdminPermissions _permissions = AdminPermissions.NoAccess();
        private ushort _localPlayerId;
        private bool _hasLocalPlayerId;

        public event Action<IReadOnlyList<ListPlayerView>> PlayersChanged;
        public event Action<ushort, string> NoteChanged;
        public event Action<bool> AuthorizationChanged;
        public event Action LocalPlayerIdentityChanged;
        public event Action<IReadOnlyList<ListAdminPlayerView>> AdminPlayersChanged;
        public event Action<ServerSettingsData> ServerSettingsChanged;
        public event Action<IReadOnlyList<AdminBanEntryView>> AdminBansChanged;
        public event Action<IReadOnlyList<ServerSettingsProposalData>> SettingsProposalsChanged;
        public event Action<IReadOnlyList<TeamSettingsEntryData>> TeamSettingsChanged;

        public bool IsAuthorized
        {
            get
            {
                lock (_gate)
                {
                    return _isAuthorized;
                }
            }
        }

        public bool IsHost
        {
            get { lock (_gate) { return _isHost; } }
        }

        public bool TryGetLocalPlayerId(
            out ushort playerId)
        {
            lock (_gate)
            {
                playerId = _localPlayerId;
                return _hasLocalPlayerId;
            }
        }

        public AdminPermissions Permissions
        {
            get { lock (_gate) { return _permissions.Clone(); } }
        }

        public IReadOnlyList<ListPlayerView> GetPlayers()
        {
            lock (_gate)
            {
                return _players.Values
                    .OrderBy(x => x.Id)
                    .ToList()
                    .AsReadOnly();
            }
        }

        public bool TeamChangesLocked
        {
            get { lock (_gate) { return _teamChangesLocked; } }
        }

        public IReadOnlyList<TeamSettingsEntryData> GetTeamSettings()
        {
            lock (_gate)
            {
                return _teamSettings.Values
                    .Where(entry => entry != null)
                    .OrderBy(entry => (byte)entry.Team)
                    .Select(entry => entry.Clone())
                    .ToList()
                    .AsReadOnly();
            }
        }

        public bool TryGetTeamSettings(
            Hkmp.Game.Team team,
            out TeamSettingsEntryData settings)
        {
            lock (_gate)
            {
                TeamSettingsEntryData stored;
                if (_teamSettings.TryGetValue(team, out stored) && stored != null)
                {
                    settings = stored.Clone();
                    return true;
                }
            }

            settings = null;
            return false;
        }

        public bool IsLocalTeamLeader
        {
            get
            {
                lock (_gate)
                {
                    ListPlayerView player;
                    return _hasLocalPlayerId &&
                           _players.TryGetValue(_localPlayerId, out player) &&
                           player != null && player.IsTeamLeader;
                }
            }
        }

        public Hkmp.Game.Team LocalTeam
        {
            get
            {
                lock (_gate)
                {
                    ListPlayerView player;
                    return _hasLocalPlayerId &&
                           _players.TryGetValue(_localPlayerId, out player) &&
                           player != null
                        ? player.Team
                        : Hkmp.Game.Team.None;
                }
            }
        }

        public bool TryGetPlayer(
            ushort id,
            out ListPlayerView player)
        {
            lock (_gate)
            {
                return _players.TryGetValue(
                    id,
                    out player);
            }
        }

        internal void SetLocalNotesStore(
            ListClientNotesStore store)
        {
            lock (_gate)
            {
                _localNotesStore = store;
            }
        }

        public bool SetLocalNote(
            ushort playerId,
            string note)
        {
            string value = note ?? string.Empty;
            if (value.Length > ListProtocol.MaxNoteLength)
            {
                return false;
            }

            string identityKey = string.Empty;
            ListClientNotesStore store;
            lock (_gate)
            {
                ListPlayerView player;
                if (_players.TryGetValue(playerId, out player) && player != null)
                {
                    identityKey = ListClientNotesStore.GetIdentityKey(
                        player.AuthorizationFingerprint, player.Username);
                }

                store = _localNotesStore;
                _notes[playerId] = value;
                if (!string.IsNullOrEmpty(identityKey))
                {
                    _noteIdentityKeysByPlayerId[playerId] = identityKey;
                }
            }

            bool saved = true;
            if (store != null && !string.IsNullOrEmpty(identityKey))
            {
                saved = store.Set(identityKey, value);
            }

            Action<ushort, string> handler = NoteChanged;
            if (handler != null)
            {
                handler(playerId, value);
            }

            return saved;
        }

        public string GetNote(
            ushort playerId)
        {
            lock (_gate)
            {
                string note;

                return _notes.TryGetValue(
                        playerId,
                        out note)
                    ? note
                    : string.Empty;
            }
        }

        public IReadOnlyList<ListAdminPlayerView> GetAdminPlayers()
        {
            lock (_gate)
            {
                return _adminPlayers.Values
                    .OrderBy(x => x.Id)
                    .ToList()
                    .AsReadOnly();
            }
        }

        public bool TryGetAdminPlayer(
            ushort id,
            out ListAdminPlayerView player)
        {
            lock (_gate)
            {
                return _adminPlayers.TryGetValue(
                    id,
                    out player);
            }
        }

        public IReadOnlyList<AdminBanEntryView> GetAdminBans()
        {
            lock (_gate)
            {
                return _bans.Values
                    .OrderBy(
                        x => x.Username,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(
                        x => x.AuthorizationFingerprint,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    .AsReadOnly();
            }
        }

        public IReadOnlyList<ServerSettingsProposalData> GetSettingsProposals()
        {
            lock (_gate)
            {
                return _settingsProposals.Values
                    .Where(x => x != null && !x.IsRemoved)
                    .OrderBy(x => x.SubmittedUtcTicks)
                    .Select(x => x.Clone())
                    .ToList()
                    .AsReadOnly();
            }
        }

        internal void SetSettingsProposal(
            ServerSettingsProposalData proposal)
        {
            if (proposal == null ||
                proposal.IsRemoved ||
                string.IsNullOrWhiteSpace(proposal.ProposalId) ||
                proposal.Settings == null)
            {
                return;
            }

            IReadOnlyList<ServerSettingsProposalData> snapshot;

            lock (_gate)
            {
                _settingsProposals[proposal.ProposalId] =
                    proposal.Clone();

                snapshot =
                    _settingsProposals.Values
                        .Where(x => x != null && !x.IsRemoved)
                        .OrderBy(x => x.SubmittedUtcTicks)
                        .Select(x => x.Clone())
                        .ToList()
                        .AsReadOnly();
            }

            RaiseSettingsProposalsChanged(snapshot);
        }

        internal void RemoveSettingsProposal(
            string proposalId)
        {
            if (string.IsNullOrWhiteSpace(proposalId))
            {
                return;
            }

            IReadOnlyList<ServerSettingsProposalData> snapshot;

            lock (_gate)
            {
                _settingsProposals.Remove(proposalId);

                snapshot =
                    _settingsProposals.Values
                        .Where(x => x != null && !x.IsRemoved)
                        .OrderBy(x => x.SubmittedUtcTicks)
                        .Select(x => x.Clone())
                        .ToList()
                        .AsReadOnly();
            }

            RaiseSettingsProposalsChanged(snapshot);
        }

        public ServerSettingsData GetServerSettings()
        {
            lock (_gate)
            {
                return Clone(
                    _serverSettings);
            }
        }

        internal bool BeginSnapshot(
            PlayerListBeginData begin)
        {
            if (begin == null)
            {
                return false;
            }

            IReadOnlyList<ListPlayerView> changedPlayers = null;

            lock (_gate)
            {
                if (_hasServerSnapshot &&
                    begin.SnapshotId <= _latestCommittedSnapshotId)
                {
                    return false;
                }

                PendingSnapshot snapshot;

                if (!_pendingSnapshots.TryGetValue(
                        begin.SnapshotId,
                        out snapshot))
                {
                    snapshot =
                        new PendingSnapshot();

                    _pendingSnapshots[begin.SnapshotId] =
                        snapshot;
                }

                if (snapshot.HasExpectedCount &&
                    snapshot.ExpectedCount != begin.PlayerCount)
                {
                    _pendingSnapshots.Remove(begin.SnapshotId);
                    return false;
                }

                snapshot.HasExpectedCount = true;
                snapshot.ExpectedCount = begin.PlayerCount;

                TrimPendingSnapshotsLocked(
                    begin.SnapshotId);

                changedPlayers =
                    TryCommitSnapshotLocked(
                        begin.SnapshotId,
                        snapshot);
            }

            RaisePlayersChanged(
                changedPlayers);

            return true;
        }

        internal void ReceiveSnapshotEntry(
            PlayerListEntryData entry)
        {
            if (entry == null)
            {
                return;
            }

            IReadOnlyList<ListPlayerView> changedPlayers = null;

            lock (_gate)
            {
                if (_hasServerSnapshot &&
                    entry.SnapshotId <= _latestCommittedSnapshotId)
                {
                    return;
                }

                PendingSnapshot snapshot;

                if (!_pendingSnapshots.TryGetValue(
                        entry.SnapshotId,
                        out snapshot))
                {
                    snapshot =
                        new PendingSnapshot();

                    _pendingSnapshots[entry.SnapshotId] =
                        snapshot;
                }

                if (!snapshot.Players.ContainsKey(entry.PlayerId) &&
                    snapshot.Players.Count >= MaximumBufferedSnapshotPlayers)
                {
                    _pendingSnapshots.Remove(entry.SnapshotId);
                    return;
                }

                snapshot.Players[entry.PlayerId] =
                    new ListPlayerView(
                        entry.PlayerId,
                        entry.Username,
                        entry.Team,
                        entry.SkinId,
                        entry.IsHost,
                        entry.IsAuthorized,
                        entry.IsTeamLeader,
                        entry.HasTeamSkin,
                        entry.TeamSkinId,
                        entry.IsTeamSkinLocked,
                        entry.HasListSkinOverride,
                        entry.ListSkinOverrideId,
                        entry.TeamSkinUpdatedAtUtcTicks,
                        entry.ListSkinUpdatedAtUtcTicks,
                        entry.AuthorizationFingerprint);

                TrimPendingSnapshotsLocked(
                    entry.SnapshotId);

                changedPlayers =
                    TryCommitSnapshotLocked(
                        entry.SnapshotId,
                        snapshot);
            }

            RaisePlayersChanged(
                changedPlayers);
        }

        internal void EndSnapshot(
            PlayerListEndData end)
        {
            if (end == null)
            {
                return;
            }

            IReadOnlyList<ListPlayerView> changedPlayers = null;

            lock (_gate)
            {
                if (_hasServerSnapshot &&
                    end.SnapshotId <= _latestCommittedSnapshotId)
                {
                    return;
                }

                PendingSnapshot snapshot;

                if (!_pendingSnapshots.TryGetValue(
                        end.SnapshotId,
                        out snapshot))
                {
                    snapshot =
                        new PendingSnapshot();

                    _pendingSnapshots[end.SnapshotId] =
                        snapshot;
                }

                if (snapshot.HasExpectedCount &&
                    snapshot.ExpectedCount != end.PlayerCount)
                {
                    _pendingSnapshots.Remove(end.SnapshotId);
                    return;
                }

                snapshot.HasExpectedCount = true;
                snapshot.ExpectedCount = end.PlayerCount;
                snapshot.EndReceived = true;
                snapshot.EndPlayerCount = end.PlayerCount;

                TrimPendingSnapshotsLocked(
                    end.SnapshotId);

                changedPlayers =
                    TryCommitSnapshotLocked(
                        end.SnapshotId,
                        snapshot);
            }

            RaisePlayersChanged(
                changedPlayers);
        }

        internal void SetAuthorization(
            LocalPermissionsData data)
        {
            if (data == null)
            {
                return;
            }

            bool changed;
            bool identityChanged;
            bool newIsHost = data.IsAuthorized && data.IsHost;
            AdminPermissions newPermissions = data.IsAuthorized
                ? (data.Permissions ?? AdminPermissions.FullAccess()).Clone()
                : AdminPermissions.NoAccess();

            IReadOnlyList<ListAdminPlayerView> admins =
                null;

            IReadOnlyList<AdminBanEntryView> bans =
                null;

            lock (_gate)
            {
                identityChanged =
                    !_hasLocalPlayerId ||
                    _localPlayerId != data.PlayerId;

                _localPlayerId = data.PlayerId;
                _hasLocalPlayerId = true;

                changed =
                    _isAuthorized != data.IsAuthorized ||
                    _isHost != newIsHost ||
                    !PermissionsEqual(_permissions, newPermissions);

                _isAuthorized = data.IsAuthorized;
                _isHost = newIsHost;
                _permissions = newPermissions.Clone();

                if (!data.IsAuthorized)
                {
                    _adminPlayers.Clear();
                    _bans.Clear();

                    admins =
                        new List<ListAdminPlayerView>()
                            .AsReadOnly();

                    bans =
                        new List<AdminBanEntryView>()
                            .AsReadOnly();
                }
            }

            if (identityChanged)
            {
                Action handler =
                    LocalPlayerIdentityChanged;

                if (handler != null)
                {
                    handler();
                }
            }

            if (changed)
            {
                Action<bool> handler =
                    AuthorizationChanged;

                if (handler != null)
                {
                    handler(
                        data.IsAuthorized);
                }
            }

            if (!data.IsAuthorized)
            {
                RaiseAdminPlayersChanged(
                    admins);

                RaiseAdminBansChanged(
                    bans);
            }
        }

        internal void SetAdminPlayer(
     AdminPlayerData data)
        {
            if (data == null)
            {
                return;
            }

            IReadOnlyList<ListAdminPlayerView> snapshot;

            lock (_gate)
            {
                if (!_isAuthorized && !_isHost)
                {
                    return;
                }

                _adminPlayers[data.PlayerId] =
                    new ListAdminPlayerView(
                        data.PlayerId,
                        data.Username,
                        data.IsAuthorized,
                        data.IsAdminEnabled,
                        data.AuthorizationFingerprint,
                        data.AuthorizationKey,
                        data.AuthorizationNote,
                        data.IsHost,
                        data.Permissions);

                snapshot =
                    _adminPlayers.Values
                        .OrderBy(x => x.Id)
                        .ToList()
                        .AsReadOnly();
            }

            RaiseAdminPlayersChanged(
                snapshot);
        }


        internal void SetBanList(
            AdminBanListData data)
        {
            if (data == null)
            {
                return;
            }

            IReadOnlyList<AdminBanEntryView> snapshot;

            lock (_gate)
            {
                if (!_isAuthorized && !_isHost)
                {
                    return;
                }

                _bans.Clear();

                IEnumerable<AdminBanEntryView> entries =
                    data.Entries ??
                    new List<AdminBanEntryView>();

                foreach (AdminBanEntryView entry
                         in entries)
                {
                    if (entry == null ||
                        string.IsNullOrWhiteSpace(
                            entry.AuthorizationFingerprint))
                    {
                        continue;
                    }

                    _bans[
                        entry.AuthorizationFingerprint] =
                        entry;
                }

                snapshot =
                    _bans.Values
                        .OrderBy(
                            x => x.Username,
                            StringComparer.OrdinalIgnoreCase)
                        .ThenBy(
                            x => x.AuthorizationFingerprint,
                            StringComparer.OrdinalIgnoreCase)
                        .ToList()
                        .AsReadOnly();
            }

            RaiseAdminBansChanged(
                snapshot);
        }

        internal void SetTeamSettings(
            TeamSettingsData data)
        {
            if (data == null)
            {
                return;
            }

            IEnumerable<TeamSettingsEntryData> entries =
                data.Entries ?? new List<TeamSettingsEntryData>();

            IReadOnlyList<TeamSettingsEntryData> snapshot;
            lock (_gate)
            {
                _teamChangesLocked = data.TeamChangesLocked;
                _teamSettings.Clear();
                foreach (TeamSettingsEntryData entry in entries)
                {
                    if (entry == null || !TeamSettingsData.IsPlayableTeam(entry.Team))
                        continue;

                    _teamSettings[entry.Team] = entry.Clone();
                }

                snapshot = _teamSettings.Values
                    .OrderBy(entry => (byte)entry.Team)
                    .Select(entry => entry.Clone())
                    .ToList()
                    .AsReadOnly();
            }

            Action<IReadOnlyList<TeamSettingsEntryData>> handler = TeamSettingsChanged;
            if (handler != null)
            {
                handler(snapshot);
            }
        }

        internal void SetNote(
            PlayerNoteData data)
        {
            if (data == null)
            {
                return;
            }

            string note = data.Note ?? string.Empty;
            if (note.Length > ListProtocol.MaxNoteLength)
            {
                return;
            }

            string identityKey = string.Empty;
            ListClientNotesStore store;
            lock (_gate)
            {
                ListPlayerView player;
                if (_players.TryGetValue(data.PlayerId, out player) && player != null)
                {
                    identityKey = ListClientNotesStore.GetIdentityKey(
                        player.AuthorizationFingerprint, player.Username);
                }

                store = _localNotesStore;
            }

            if (store != null && !string.IsNullOrEmpty(identityKey))
            {
                string localNote;
                if (store.TryGet(identityKey, out localNote))
                {
                    note = localNote;
                }
                else if (note.Length > 0)
                {
                    store.Set(identityKey, note);
                }
            }

            lock (_gate)
            {
                _notes[data.PlayerId] = note;
                if (!string.IsNullOrEmpty(identityKey))
                {
                    _noteIdentityKeysByPlayerId[data.PlayerId] = identityKey;
                }
            }

            Action<ushort, string> handler = NoteChanged;
            if (handler != null)
            {
                handler(data.PlayerId, note);
            }
        }

        internal void SetServerSettings(
            ServerSettingsData data)
        {
            if (data == null)
            {
                return;
            }

            ServerSettingsData snapshot;

            lock (_gate)
            {
                _serverSettings =
                    Clone(data);

                snapshot =
                    Clone(_serverSettings);
            }

            Action<ServerSettingsData> handler =
                ServerSettingsChanged;

            if (handler != null)
            {
                handler(snapshot);
            }
        }

        internal void Clear()
        {
            IReadOnlyList<ListPlayerView> players =
                new List<ListPlayerView>()
                    .AsReadOnly();

            IReadOnlyList<ListAdminPlayerView> admins =
                new List<ListAdminPlayerView>()
                    .AsReadOnly();

            IReadOnlyList<AdminBanEntryView> bans =
                new List<AdminBanEntryView>()
                    .AsReadOnly();

            IReadOnlyList<ServerSettingsProposalData> proposals =
                new List<ServerSettingsProposalData>()
                    .AsReadOnly();

            lock (_gate)
            {
                _players.Clear();
                _notes.Clear();
                _noteIdentityKeysByPlayerId.Clear();
                _adminPlayers.Clear();
                _bans.Clear();
                _settingsProposals.Clear();
                _teamSettings.Clear();
                _teamChangesLocked = false;
                _pendingSnapshots.Clear();
                _latestCommittedSnapshotId = 0;
                _hasServerSnapshot = false;
                _serverSettings = null;
                _isAuthorized = false;
                _isHost = false;
                _permissions = AdminPermissions.NoAccess();
                _localPlayerId = 0;
                _hasLocalPlayerId = false;
            }

            Action identityHandler =
                LocalPlayerIdentityChanged;

            if (identityHandler != null)
            {
                identityHandler();
            }

            RaisePlayersChanged(players);

            Action<bool> authHandler =
                AuthorizationChanged;

            if (authHandler != null)
            {
                authHandler(false);
            }

            RaiseAdminPlayersChanged(admins);
            RaiseAdminBansChanged(bans);
            RaiseSettingsProposalsChanged(proposals);

            Action<ServerSettingsData> settingsHandler = ServerSettingsChanged;
            if (settingsHandler != null)
            {
                settingsHandler(null);
            }

            Action<IReadOnlyList<TeamSettingsEntryData>> teamHandler = TeamSettingsChanged;
            if (teamHandler != null)
            {
                teamHandler(new List<TeamSettingsEntryData>().AsReadOnly());
            }
        }

        private IReadOnlyList<ListPlayerView> TryCommitSnapshotLocked(
            ulong snapshotId,
            PendingSnapshot snapshot)
        {
            if (snapshot == null ||
                !snapshot.HasExpectedCount ||
                !snapshot.EndReceived ||
                snapshot.ExpectedCount != snapshot.EndPlayerCount ||
                snapshot.Players.Count != snapshot.ExpectedCount)
            {
                return null;
            }

            _pendingSnapshots.Remove(snapshotId);
            _latestCommittedSnapshotId = snapshotId;
            _hasServerSnapshot = true;

            bool playersChanged = !PlayerMapsEqual(_players, snapshot.Players);
            foreach (KeyValuePair<ushort, ListPlayerView> pair in snapshot.Players)
            {
                ListPlayerView previousPlayer;
                if (!_players.TryGetValue(pair.Key, out previousPlayer) ||
                    previousPlayer == null || pair.Value == null)
                {
                    continue;
                }

                string previousKey = ListClientNotesStore.GetIdentityKey(
                    previousPlayer.AuthorizationFingerprint, previousPlayer.Username);
                string currentKey = ListClientNotesStore.GetIdentityKey(
                    pair.Value.AuthorizationFingerprint, pair.Value.Username);

                if (!string.Equals(previousKey, currentKey, StringComparison.OrdinalIgnoreCase))
                {
                    _notes.Remove(pair.Key);
                    _noteIdentityKeysByPlayerId.Remove(pair.Key);
                }
            }

            if (playersChanged)
            {
                _players.Clear();
                foreach (KeyValuePair<ushort, ListPlayerView> pair in snapshot.Players)
                {
                    _players[pair.Key] = pair.Value;
                }
            }

            HashSet<ushort> currentPlayerIds = new HashSet<ushort>(snapshot.Players.Keys);
            foreach (ushort id in _notes.Keys.Where(id => !currentPlayerIds.Contains(id)).ToList())
            {
                _notes.Remove(id);
            }
            foreach (ushort id in _noteIdentityKeysByPlayerId.Keys
                .Where(id => !currentPlayerIds.Contains(id)).ToList())
            {
                _noteIdentityKeysByPlayerId.Remove(id);
            }
            foreach (ushort id in _adminPlayers.Keys.Where(id => !currentPlayerIds.Contains(id)).ToList())
            {
                _adminPlayers.Remove(id);
            }

            foreach (KeyValuePair<ushort, ListPlayerView> pair in snapshot.Players)
            {
                ListPlayerView player = pair.Value;
                if (player == null)
                {
                    continue;
                }

                string identityKey = ListClientNotesStore.GetIdentityKey(
                    player.AuthorizationFingerprint, player.Username);
                if (string.IsNullOrEmpty(identityKey))
                {
                    continue;
                }

                string persistentNote;
                if (_localNotesStore != null &&
                    _localNotesStore.TryGet(identityKey, out persistentNote))
                {
                    _notes[pair.Key] = persistentNote;
                }
                else
                {
                    string pendingOrExistingNote;
                    if (_notes.TryGetValue(pair.Key, out pendingOrExistingNote))
                    {
                        if (_localNotesStore != null &&
                            pendingOrExistingNote.Length > 0)
                        {
                            _localNotesStore.Set(identityKey, pendingOrExistingNote);
                        }
                    }
                }

                _noteIdentityKeysByPlayerId[pair.Key] = identityKey;
            }

            if (!playersChanged)
            {
                return null;
            }

            return _players.Values
                .OrderBy(x => x.Id)
                .ToList()
                .AsReadOnly();
        }

        private void TrimPendingSnapshotsLocked(
            ulong keepSnapshotId)
        {
            while (_pendingSnapshots.Count >
                   MaximumPendingSnapshots)
            {
                ulong oldestId =
                    ulong.MaxValue;

                foreach (ulong id in _pendingSnapshots.Keys)
                {
                    if (id == keepSnapshotId)
                    {
                        continue;
                    }

                    if (id < oldestId)
                    {
                        oldestId = id;
                    }
                }

                if (oldestId == ulong.MaxValue)
                {
                    break;
                }

                _pendingSnapshots.Remove(
                    oldestId);
            }
        }

        private void RaisePlayersChanged(
            IReadOnlyList<ListPlayerView> players)
        {
            if (players == null)
            {
                return;
            }

            Action<IReadOnlyList<ListPlayerView>> handler =
                PlayersChanged;

            if (handler != null)
            {
                handler(players);
            }
        }

        private void RaiseAdminPlayersChanged(
            IReadOnlyList<ListAdminPlayerView> admins)
        {
            if (admins == null)
            {
                return;
            }

            Action<IReadOnlyList<ListAdminPlayerView>> handler =
                AdminPlayersChanged;

            if (handler != null)
            {
                handler(admins);
            }
        }

        private void RaiseAdminBansChanged(
            IReadOnlyList<AdminBanEntryView> bans)
        {
            if (bans == null)
            {
                return;
            }

            Action<IReadOnlyList<AdminBanEntryView>> handler =
                AdminBansChanged;

            if (handler != null)
            {
                handler(bans);
            }
        }

        private void RaiseSettingsProposalsChanged(
            IReadOnlyList<ServerSettingsProposalData> proposals)
        {
            Action<IReadOnlyList<ServerSettingsProposalData>> handler =
                SettingsProposalsChanged;

            if (handler != null)
            {
                handler(proposals ?? new List<ServerSettingsProposalData>().AsReadOnly());
            }
        }

        private static bool PermissionsEqual(
            AdminPermissions first,
            AdminPermissions second)
        {
            if (ReferenceEquals(first, second))
            {
                return true;
            }

            if (first == null || second == null)
            {
                return false;
            }

            return
                first.CanKickPlayers == second.CanKickPlayers &&
                first.CanBanPlayers == second.CanBanPlayers &&
                first.CanManageBans == second.CanManageBans &&
                first.CanChangeServerSettings == second.CanChangeServerSettings &&
                first.CanChangeServerSettingsDirectly == second.CanChangeServerSettingsDirectly &&
                first.CanManageIdentityData == second.CanManageIdentityData &&
                first.CanManageTeamLeaders == second.CanManageTeamLeaders &&
                first.CanManageAdministrators == second.CanManageAdministrators;
        }

        private static bool PlayerMapsEqual(
            IDictionary<ushort, ListPlayerView> first,
            IDictionary<ushort, ListPlayerView> second)
        {
            if (first == null ||
                second == null)
            {
                return first == null &&
                       second == null;
            }

            if (first.Count !=
                second.Count)
            {
                return false;
            }

            foreach (KeyValuePair<ushort, ListPlayerView> pair
                     in first)
            {
                ListPlayerView other;

                if (!second.TryGetValue(
                        pair.Key,
                        out other))
                {
                    return false;
                }

                if (other == null ||
                    pair.Value == null)
                {
                    if (other != pair.Value)
                    {
                        return false;
                    }

                    continue;
                }

                if (!string.Equals(
                        pair.Value.Username,
                        other.Username,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        pair.Value.AuthorizationFingerprint,
                        other.AuthorizationFingerprint,
                        StringComparison.OrdinalIgnoreCase) ||
                    pair.Value.Team != other.Team ||
                    pair.Value.SkinId != other.SkinId ||
                    pair.Value.IsHost != other.IsHost ||
                    pair.Value.IsAuthorized != other.IsAuthorized ||
                    pair.Value.IsTeamLeader != other.IsTeamLeader ||
                    pair.Value.HasTeamSkin != other.HasTeamSkin ||
                    pair.Value.TeamSkinId != other.TeamSkinId ||
                    pair.Value.IsTeamSkinLocked != other.IsTeamSkinLocked ||
                    pair.Value.HasListSkinOverride != other.HasListSkinOverride ||
                    pair.Value.ListSkinOverrideId != other.ListSkinOverrideId ||
                    pair.Value.TeamSkinUpdatedAtUtcTicks != other.TeamSkinUpdatedAtUtcTicks ||
                    pair.Value.ListSkinUpdatedAtUtcTicks != other.ListSkinUpdatedAtUtcTicks)
                {
                    return false;
                }
            }

            return true;
        }

        private static ServerSettingsData Clone(
            ServerSettingsData value)
        {
            if (value == null)
            {
                return null;
            }

            return new ServerSettingsData
            {
                IsPvpEnabled =
                    value.IsPvpEnabled,

                IsBodyDamageEnabled =
                    value.IsBodyDamageEnabled,

                AlwaysShowMapIcons =
                    value.AlwaysShowMapIcons,

                OnlyBroadcastMapIconWithWaywardCompass =
                    value.OnlyBroadcastMapIconWithWaywardCompass,

                DisplayNames =
                    value.DisplayNames,

                TeamsEnabled =
                    value.TeamsEnabled,

                AllowSkins =
                    value.AllowSkins,

                AllowParries =
                    value.AllowParries,

                NailDamage =
                    value.NailDamage,

                GrubberflyElegyDamage =
                    value.GrubberflyElegyDamage,

                VengefulSpiritDamage =
                    value.VengefulSpiritDamage,

                ShadeSoulDamage =
                    value.ShadeSoulDamage,

                DesolateDiveDamage =
                    value.DesolateDiveDamage,

                DescendingDarkDamage =
                    value.DescendingDarkDamage,

                HowlingWraithDamage =
                    value.HowlingWraithDamage,

                AbyssShriekDamage =
                    value.AbyssShriekDamage,

                GreatSlashDamage =
                    value.GreatSlashDamage,

                DashSlashDamage =
                    value.DashSlashDamage,

                CycloneSlashDamage =
                    value.CycloneSlashDamage,

                SporeShroomDamage =
                    value.SporeShroomDamage,

                SporeDungShroomDamage =
                    value.SporeDungShroomDamage,

                ThornOfAgonyDamage =
                    value.ThornOfAgonyDamage,

                SharpShadowDamage =
                    value.SharpShadowDamage
            };
        }

        private sealed class PendingSnapshot
        {
            public bool HasExpectedCount { get; set; }

            public ushort ExpectedCount { get; set; }

            public ushort EndPlayerCount { get; set; }

            public bool EndReceived { get; set; }

            public Dictionary<ushort, ListPlayerView> Players { get; } =
                new Dictionary<ushort, ListPlayerView>();
        }
    }
}
