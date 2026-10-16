using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hkmp.Api.Server;
using Hkmp.Game;
using HKMP.List.Shared;
using Newtonsoft.Json;

namespace HKMP.List.Server
{
    internal sealed class ListHostTeamStore
    {
        private const int FormatVersion = 1;

        private readonly object _gate = new object();
        private readonly string _filePath;
        private readonly Dictionary<Team, TeamRecord> _records =
            new Dictionary<Team, TeamRecord>();
        private bool _teamChangesLocked;

        public bool TeamChangesLocked
        {
            get { lock (_gate) { return _teamChangesLocked; } }
        }

        public ListHostTeamStore(string baseDirectory)
        {
            string directory = Path.Combine(
                string.IsNullOrWhiteSpace(baseDirectory)
                    ? AppDomain.CurrentDomain.BaseDirectory
                    : baseDirectory,
                "HKMP.List");

            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory, "team-state.json");

            EnsureTeamRecords();
            Load();
        }

        public bool IsTeamLeader(IServerPlayer player)
        {
            if (player == null || !TeamSettingsData.IsPlayableTeam(player.Team))
                return false;

            string fingerprint = ListHostNotesStore.GetAuthorizationFingerprint(player);
            if (string.IsNullOrWhiteSpace(fingerprint))
                return false;

            lock (_gate)
            {
                TeamRecord record;
                return _records.TryGetValue(player.Team, out record) &&
                       record != null &&
                       string.Equals(record.LeaderFingerprint, fingerprint,
                           StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool TryGetTeamAppearance(
            Team team,
            out bool hasSkin,
            out byte skinId,
            out bool isLocked,
            out long skinUpdatedAtUtcTicks)
        {
            hasSkin = false;
            skinId = 0;
            isLocked = false;
            skinUpdatedAtUtcTicks = 0;

            if (!TeamSettingsData.IsPlayableTeam(team))
                return false;

            lock (_gate)
            {
                TeamRecord record;
                if (!_records.TryGetValue(team, out record) || record == null)
                    return false;

                hasSkin = record.HasTeamSkin;
                skinId = record.TeamSkinId;
                isLocked = record.HasTeamSkin && record.IsSkinLocked;
                skinUpdatedAtUtcTicks = record.SkinUpdatedAtUtcTicks;
                return true;
            }
        }

        public IReadOnlyList<TeamSettingsEntryData> CreateSnapshot(
            IEnumerable<IServerPlayer> connectedPlayers)
        {
            List<TeamRecord> records;
            lock (_gate)
            {
                records = _records.Values
                    .Where(record => record != null)
                    .Select(record => record.Clone())
                    .ToList();
            }

            List<IServerPlayer> players = connectedPlayers == null
                ? new List<IServerPlayer>()
                : connectedPlayers.Where(player => player != null).ToList();

            List<TeamSettingsEntryData> result = new List<TeamSettingsEntryData>(4);
            foreach (Team team in GetPlayableTeams())
            {
                TeamRecord record = records.FirstOrDefault(item => item.Team == (int)team);
                if (record == null)
                    record = new TeamRecord { Team = (int)team };

                IServerPlayer connectedLeader = null;
                if (!string.IsNullOrWhiteSpace(record.LeaderFingerprint))
                {
                    connectedLeader = players.FirstOrDefault(player =>
                        player.Team == team &&
                        string.Equals(
                            ListHostNotesStore.GetAuthorizationFingerprint(player),
                            record.LeaderFingerprint,
                            StringComparison.OrdinalIgnoreCase));
                }

                result.Add(new TeamSettingsEntryData
                {
                    Team = team,
                    HasAssignedLeader = !string.IsNullOrWhiteSpace(record.LeaderFingerprint),
                    HasConnectedLeader = connectedLeader != null,
                    LeaderPlayerId = connectedLeader == null ? (ushort)0 : connectedLeader.Id,
                    LeaderUsername = connectedLeader == null
                        ? (record.LeaderUsername ?? string.Empty)
                        : (connectedLeader.Username ?? string.Empty),
                    HasTeamSkin = record.HasTeamSkin,
                    TeamSkinId = record.TeamSkinId,
                    IsSkinLocked = record.HasTeamSkin && record.IsSkinLocked,
                    SkinUpdatedAtUtcTicks = record.SkinUpdatedAtUtcTicks
                });
            }

            return result.AsReadOnly();
        }

        public bool TrySetLeader(Team team, string fingerprint, string username)
        {
            string normalized = NormalizeFingerprint(fingerprint);
            if (!TeamSettingsData.IsPlayableTeam(team) || normalized == null)
                return false;

            lock (_gate)
            {
                Dictionary<Team, TeamRecord> previous = CloneRecordsLocked();

                foreach (TeamRecord record in _records.Values)
                {
                    if (record != null && string.Equals(
                        record.LeaderFingerprint, normalized,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        record.LeaderFingerprint = string.Empty;
                        record.LeaderUsername = string.Empty;
                    }
                }

                TeamRecord selected;
                if (!_records.TryGetValue(team, out selected) || selected == null)
                {
                    selected = new TeamRecord { Team = (int)team };
                    _records[team] = selected;
                }

                selected.LeaderFingerprint = normalized;
                selected.LeaderUsername = TruncateUsername(username);

                if (SaveLocked())
                    return true;

                RestoreRecordsLocked(previous);
                return false;
            }
        }

        public bool TryRemoveLeader(Team team, string expectedFingerprint = null)
        {
            if (!TeamSettingsData.IsPlayableTeam(team))
                return false;

            string expected = string.IsNullOrWhiteSpace(expectedFingerprint)
                ? null
                : NormalizeFingerprint(expectedFingerprint);

            lock (_gate)
            {
                TeamRecord record;
                if (!_records.TryGetValue(team, out record) || record == null)
                    return false;

                if (expected != null && !string.Equals(
                    record.LeaderFingerprint, expected,
                    StringComparison.OrdinalIgnoreCase))
                    return false;

                Dictionary<Team, TeamRecord> previous = CloneRecordsLocked();
                record.LeaderFingerprint = string.Empty;
                record.LeaderUsername = string.Empty;

                if (SaveLocked())
                    return true;

                RestoreRecordsLocked(previous);
                return false;
            }
        }

        public bool TrySetTeamSkin(Team team, byte skinId)
        {
            if (!TeamSettingsData.IsPlayableTeam(team))
                return false;

            lock (_gate)
            {
                TeamRecord record = GetOrCreateLocked(team);
                Dictionary<Team, TeamRecord> previous = CloneRecordsLocked();
                record.HasTeamSkin = true;
                record.TeamSkinId = skinId;
                record.SkinUpdatedAtUtcTicks = NextTimestamp(record.SkinUpdatedAtUtcTicks);

                if (SaveLocked())
                    return true;

                RestoreRecordsLocked(previous);
                return false;
            }
        }

        public bool TrySetTeamSkinLocked(Team team, bool locked)
        {
            if (!TeamSettingsData.IsPlayableTeam(team))
                return false;

            lock (_gate)
            {
                TeamRecord record = GetOrCreateLocked(team);
                if (locked && !record.HasTeamSkin)
                    return false;

                Dictionary<Team, TeamRecord> previous = CloneRecordsLocked();
                if (record.IsSkinLocked != locked)
                    record.SkinUpdatedAtUtcTicks = NextTimestamp(record.SkinUpdatedAtUtcTicks);
                record.IsSkinLocked = locked;

                if (SaveLocked())
                    return true;

                RestoreRecordsLocked(previous);
                return false;
            }
        }

        public bool TrySetTeamChangesLocked(bool locked)
        {
            lock (_gate)
            {
                bool previous = _teamChangesLocked;
                if (previous == locked)
                {
                    return true;
                }

                _teamChangesLocked = locked;
                if (SaveLocked())
                {
                    return true;
                }

                _teamChangesLocked = previous;
                return false;
            }
        }

        private static long NextTimestamp(long previousTicks)
        {
            long now = DateTime.UtcNow.Ticks;
            if (now > previousTicks)
                return now;

            return previousTicks == long.MaxValue
                ? long.MaxValue
                : previousTicks + 1;
        }

        private void EnsureTeamRecords()
        {
            foreach (Team team in GetPlayableTeams())
            {
                if (!_records.ContainsKey(team))
                    _records[team] = new TeamRecord { Team = (int)team };
            }
        }

        private TeamRecord GetOrCreateLocked(Team team)
        {
            TeamRecord record;
            if (!_records.TryGetValue(team, out record) || record == null)
            {
                record = new TeamRecord { Team = (int)team };
                _records[team] = record;
            }

            return record;
        }

        private Dictionary<Team, TeamRecord> CloneRecordsLocked()
        {
            return _records.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        }

        private void RestoreRecordsLocked(Dictionary<Team, TeamRecord> previous)
        {
            _records.Clear();
            foreach (KeyValuePair<Team, TeamRecord> pair in previous)
                _records[pair.Key] = pair.Value.Clone();
            EnsureTeamRecords();
        }

        private void Load()
        {
            if (!File.Exists(_filePath))
                return;

            try
            {
                FileInfo info = new FileInfo(_filePath);
                if (info.Length <= 0 || info.Length > 1024 * 1024)
                    return;

                TeamStoreFile loaded = JsonConvert.DeserializeObject<TeamStoreFile>(
                    File.ReadAllText(_filePath));
                if (loaded == null || loaded.FormatVersion != FormatVersion || loaded.Teams == null)
                    return;

                lock (_gate)
                {
                    _teamChangesLocked = loaded.TeamChangesLocked;
                    bool upgradedLegacySkinTimestamps = false;
                    foreach (TeamRecord item in loaded.Teams)
                    {
                        if (item == null || !Enum.IsDefined(typeof(Team), item.Team))
                            continue;

                        Team team = (Team)item.Team;
                        if (!TeamSettingsData.IsPlayableTeam(team))
                            continue;

                        TeamRecord normalized = item.Clone();
                        normalized.Team = (int)team;
                        normalized.LeaderFingerprint = NormalizeFingerprint(normalized.LeaderFingerprint) ?? string.Empty;
                        normalized.LeaderUsername = TruncateUsername(normalized.LeaderUsername);
                        normalized.IsSkinLocked = normalized.HasTeamSkin && normalized.IsSkinLocked;
                        if (normalized.HasTeamSkin && normalized.SkinUpdatedAtUtcTicks <= 0)
                        {
                            normalized.SkinUpdatedAtUtcTicks = DateTime.UtcNow.Ticks;
                            upgradedLegacySkinTimestamps = true;
                        }
                        _records[team] = normalized;
                    }

                    EnsureTeamRecords();
                    if (upgradedLegacySkinTimestamps)
                        SaveLocked();
                }
            }
            catch
            {
                lock (_gate)
                    EnsureTeamRecords();
            }
        }

        private bool SaveLocked()
        {
            string temporaryPath = _filePath + ".tmp";
            try
            {
                TeamStoreFile data = new TeamStoreFile
                {
                    FormatVersion = FormatVersion,
                    TeamChangesLocked = _teamChangesLocked,
                    Teams = _records.Values
                        .Where(record => record != null)
                        .OrderBy(record => record.Team)
                        .Select(record => record.Clone())
                        .ToList()
                };

                File.WriteAllText(temporaryPath,
                    JsonConvert.SerializeObject(data, Formatting.Indented),
                    new System.Text.UTF8Encoding(false));

                if (File.Exists(_filePath))
                {
                    try
                    {
                        File.Replace(temporaryPath, _filePath, null);
                    }
                    catch
                    {
                        File.Copy(temporaryPath, _filePath, true);
                        File.Delete(temporaryPath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, _filePath);
                }

                return true;
            }
            catch
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
                return false;
            }
        }

        private static IEnumerable<Team> GetPlayableTeams()
        {
            return new[] { Team.Moss, Team.Hive, Team.Grimm, Team.Lifeblood };
        }

        private static string NormalizeFingerprint(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string result = value.Trim();
            if (result.Length != ListProtocol.MaxAuthorizationFingerprintLength)
                return null;

            for (int i = 0; i < result.Length; i++)
            {
                char c = result[i];
                if (!((c >= '0' && c <= '9') ||
                      (c >= 'a' && c <= 'f') ||
                      (c >= 'A' && c <= 'F')))
                    return null;
            }

            return result;
        }

        private static string TruncateUsername(string username)
        {
            string value = username ?? string.Empty;
            return value.Length <= ListProtocol.MaxUsernameLength
                ? value
                : value.Substring(0, ListProtocol.MaxUsernameLength);
        }

        private sealed class TeamStoreFile
        {
            public int FormatVersion { get; set; }
            public bool TeamChangesLocked { get; set; }
            public List<TeamRecord> Teams { get; set; } = new List<TeamRecord>();
        }

        private sealed class TeamRecord
        {
            public int Team { get; set; }
            public string LeaderFingerprint { get; set; } = string.Empty;
            public string LeaderUsername { get; set; } = string.Empty;
            public bool HasTeamSkin { get; set; }
            public byte TeamSkinId { get; set; }
            public bool IsSkinLocked { get; set; }
            public long SkinUpdatedAtUtcTicks { get; set; }

            public TeamRecord Clone()
            {
                return new TeamRecord
                {
                    Team = Team,
                    LeaderFingerprint = LeaderFingerprint ?? string.Empty,
                    LeaderUsername = LeaderUsername ?? string.Empty,
                    HasTeamSkin = HasTeamSkin,
                    TeamSkinId = TeamSkinId,
                    IsSkinLocked = IsSkinLocked && HasTeamSkin,
                    SkinUpdatedAtUtcTicks = SkinUpdatedAtUtcTicks
                };
            }
        }
    }
}
