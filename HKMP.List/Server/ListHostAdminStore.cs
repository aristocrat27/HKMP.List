using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Hkmp.Api.Server;
using HKMP.List.Shared;
using Newtonsoft.Json;

namespace HKMP.List.Server
{
    internal sealed class ListHostAdminStore
    {
        private readonly object _gate =
            new object();

        private readonly string _filePath;
        private readonly string _identityBundleDirectory;

        private const int IdentityBundleFormatVersion = 1;
        private const long MaximumIdentityBundleBytes = 4L * 1024L * 1024L;
        private const int MaximumIdentityBundleEntries = 4096;

        private readonly Dictionary<string, AuthorizationIdentityRecord> _identityKeys =
            new Dictionary<string, AuthorizationIdentityRecord>(
                StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, BanRecord> _bans =
            new Dictionary<string, BanRecord>(
                StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _identityNotes =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, AdminPermissions> _adminPermissions =
            new Dictionary<string, AdminPermissions>(
                StringComparer.OrdinalIgnoreCase);

        public ListHostAdminStore(
            string baseDirectory)
        {
            string directory =
                Path.Combine(
                    baseDirectory ??
                    AppDomain.CurrentDomain.BaseDirectory,
                    "HKMP.List");

            Directory.CreateDirectory(
                directory);

            _filePath =
                Path.Combine(
                    directory,
                    "admin.json");

            _identityBundleDirectory =
                Path.Combine(
                    directory,
                    "IdentityBundles");

            Directory.CreateDirectory(
                _identityBundleDirectory);

            Load();
        }

        public string GetIdentityNote(
            string authorizationFingerprint)
        {
            string key =
                NormalizeFingerprint(
                    authorizationFingerprint);

            if (key == null)
            {
                return string.Empty;
            }

            lock (_gate)
            {
                string note;

                return _identityNotes.TryGetValue(
                        key,
                        out note)
                    ? note
                    : string.Empty;
            }
        }

        public bool IsKnownIdentityFingerprint(
            string authorizationFingerprint)
        {
            string key = NormalizeFingerprint(authorizationFingerprint);
            if (key == null)
            {
                return false;
            }

            lock (_gate)
            {
                AuthorizationIdentityRecord identity;
                return _identityKeys.TryGetValue(key, out identity) &&
                       identity != null;
            }
        }

        public bool IsKnownAdministratorFingerprint(
            string authorizationFingerprint)
        {
            string key = NormalizeFingerprint(authorizationFingerprint);
            if (key == null)
            {
                return false;
            }

            lock (_gate)
            {
                AuthorizationIdentityRecord identity;
                return _identityKeys.TryGetValue(key, out identity) &&
                       identity != null &&
                       identity.WasAuthorized;
            }
        }

        public bool IsAdminAccessRevoked(
            string authorizationFingerprint)
        {
            string key = NormalizeFingerprint(authorizationFingerprint);
            if (key == null)
            {
                return false;
            }

            lock (_gate)
            {
                AuthorizationIdentityRecord identity;
                return _identityKeys.TryGetValue(key, out identity) &&
                       identity != null && identity.AdminAccessRevoked;
            }
        }

        public bool SetAdminAccessRevoked(
            string authorizationFingerprint,
            bool revoked)
        {
            string key = NormalizeFingerprint(authorizationFingerprint);
            if (key == null)
            {
                return false;
            }

            lock (_gate)
            {
                AuthorizationIdentityRecord identity;
                if (!_identityKeys.TryGetValue(key, out identity) ||
                    identity == null || !identity.WasAuthorized)
                {
                    return false;
                }

                bool previous = identity.AdminAccessRevoked;
                if (previous == revoked)
                {
                    return true;
                }

                identity.AdminAccessRevoked = revoked;
                if (SaveLocked())
                {
                    return true;
                }

                identity.AdminAccessRevoked = previous;
                return false;
            }
        }

        public AdminPermissions GetPermissions(
            string authorizationFingerprint)
        {
            string key = NormalizeFingerprint(authorizationFingerprint);
            if (key == null) return AdminPermissions.NoAccess();

            lock (_gate)
            {
                AuthorizationIdentityRecord identity;
                if (!_identityKeys.TryGetValue(key, out identity) ||
                    identity == null || !identity.WasAuthorized || identity.AdminAccessRevoked)
                {
                    return AdminPermissions.NoAccess();
                }

                AdminPermissions permissions;
                return _adminPermissions.TryGetValue(key, out permissions) && permissions != null
                    ? permissions.Clone()
                    : AdminPermissions.FullAccess();
            }
        }

        public AdminPermissions GetStoredPermissions(
            string authorizationFingerprint)
        {
            string key = NormalizeFingerprint(authorizationFingerprint);
            if (key == null)
            {
                return AdminPermissions.NoAccess();
            }

            lock (_gate)
            {
                AuthorizationIdentityRecord identity;
                if (!_identityKeys.TryGetValue(key, out identity) ||
                    identity == null || !identity.WasAuthorized)
                {
                    return AdminPermissions.NoAccess();
                }

                AdminPermissions permissions;
                return _adminPermissions.TryGetValue(key, out permissions) && permissions != null
                    ? permissions.Clone()
                    : AdminPermissions.FullAccess();
            }
        }

        public bool SetPermissions(
            string authorizationFingerprint,
            AdminPermissions permissions)
        {
            string key = NormalizeFingerprint(authorizationFingerprint);
            if (key == null || permissions == null) return false;

            lock (_gate)
            {
                AuthorizationIdentityRecord identity;
                if (!_identityKeys.TryGetValue(key, out identity) ||
                    identity == null || !identity.WasAuthorized)
                {
                    return false;
                }

                AdminPermissions previous;
                bool hadPrevious = _adminPermissions.TryGetValue(key, out previous);
                _adminPermissions[key] = permissions.Clone();
                if (SaveLocked()) return true;

                if (hadPrevious && previous != null) _adminPermissions[key] = previous;
                else _adminPermissions.Remove(key);
                return false;
            }
        }

        public bool SetIdentityNote(
            string authorizationFingerprint,
            string note)
        {
            string key =
                NormalizeFingerprint(
                    authorizationFingerprint);

            if (key == null)
            {
                return false;
            }

            string value =
                note ?? string.Empty;

            if (value.Length >
                ListProtocol.MaxNoteLength)
            {
                return false;
            }

            lock (_gate)
            {
                AuthorizationIdentityRecord identity;
                if (!_identityKeys.TryGetValue(key, out identity) ||
                    identity == null)
                {
                    return false;
                }

                string previousValue;
                bool hadPreviousValue =
                    _identityNotes.TryGetValue(key, out previousValue);

                if (string.IsNullOrEmpty(value))
                {
                    _identityNotes.Remove(key);
                }
                else
                {
                    _identityNotes[key] = value;
                }

                if (SaveLocked())
                {
                    return true;
                }

                if (hadPreviousValue)
                {
                    _identityNotes[key] = previousValue;
                }
                else
                {
                    _identityNotes.Remove(key);
                }

                return false;
            }
        }

        public bool RememberAuthorizationIdentity(
            string authorizationKey,
            string username,
            bool isAuthorized)
        {
            if (string.IsNullOrWhiteSpace(authorizationKey))
            {
                return false;
            }

            string key = authorizationKey.Trim();
            if (key.Length > ListProtocol.MaxAuthorizationKeyLength)
            {
                return false;
            }

            string fingerprint =
                ComputeAuthorizationFingerprint(key);

            if (fingerprint == null)
            {
                return false;
            }

            AuthorizationIdentityRecord updated =
                new AuthorizationIdentityRecord
                {
                    AuthorizationFingerprint = fingerprint,
                    AuthorizationKey = key,
                    Username = Truncate(
                        username,
                        ListProtocol.MaxUsernameLength),
                    WasAuthorized = isAuthorized
                };

            lock (_gate)
            {
                AuthorizationIdentityRecord existing;
                bool hadExisting =
                    _identityKeys.TryGetValue(fingerprint, out existing) &&
                    existing != null;

                if (hadExisting)
                {
                    if (existing.WasAuthorized)
                    {
                        updated.WasAuthorized = true;
                    }

                    updated.AdminAccessRevoked = existing.AdminAccessRevoked;
                }

                if (hadExisting &&
                    string.Equals(existing.AuthorizationKey, updated.AuthorizationKey, StringComparison.Ordinal) &&
                    string.Equals(existing.Username, updated.Username, StringComparison.Ordinal) &&
                    existing.WasAuthorized == updated.WasAuthorized &&
                    existing.AdminAccessRevoked == updated.AdminAccessRevoked)
                {
                    return true;
                }

                _identityKeys[fingerprint] = updated;
                if (SaveLocked())
                {
                    return true;
                }

                if (hadExisting)
                {
                    _identityKeys[fingerprint] = existing;
                }
                else
                {
                    _identityKeys.Remove(fingerprint);
                }

                return false;
            }
        }

        public bool ClearIdentityNotes()
        {
            lock (_gate)
            {
                if (_identityNotes.Count == 0)
                {
                    return true;
                }

                Dictionary<string, string> previous =
                    new Dictionary<string, string>(
                        _identityNotes,
                        StringComparer.OrdinalIgnoreCase);

                _identityNotes.Clear();
                if (SaveLocked())
                {
                    return true;
                }

                foreach (KeyValuePair<string, string> pair in previous)
                {
                    _identityNotes[pair.Key] = pair.Value;
                }

                return false;
            }
        }

        public IReadOnlyList<string> GetIdentityBundleFileNames()
        {
            try
            {
                if (!Directory.Exists(_identityBundleDirectory))
                {
                    return new List<string>().AsReadOnly();
                }

                return Directory.GetFiles(
                        _identityBundleDirectory,
                        "identity-bundle-*.json",
                        SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(name => AdminIdentityBundleActionData.IsSafeFileName(name, allowEmpty: false))
                    .OrderByDescending(
                        name => File.GetLastWriteTimeUtc(
                            Path.Combine(_identityBundleDirectory, name)))
                    .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Take(ListProtocol.MaxIdentityBundleFiles)
                    .ToList()
                    .AsReadOnly();
            }
            catch
            {
                return new List<string>().AsReadOnly();
            }
        }

        public bool ExportIdentityBundle(
            out string fileName,
            out string message)
        {
            fileName = string.Empty;
            message = string.Empty;
            string temporaryPath = null;

            try
            {
                IdentityBundleFile bundle;

                lock (_gate)
                {
                    List<IdentityBundleEntry> entries =
                        _identityKeys.Values
                            .Where(record => record != null && record.WasAuthorized)
                            .OrderBy(
                                record => record.Username,
                                StringComparer.OrdinalIgnoreCase)
                            .ThenBy(
                                record => record.AuthorizationFingerprint,
                                StringComparer.OrdinalIgnoreCase)
                            .Select(
                                record =>
                                {
                                    string note;
                                    _identityNotes.TryGetValue(
                                        record.AuthorizationFingerprint,
                                        out note);

                                    return new IdentityBundleEntry
                                    {
                                        AuthorizationFingerprint = record.AuthorizationFingerprint,
                                        AuthorizationKey = record.AuthorizationKey,
                                        Username = record.Username,
                                        WasAuthorized = record.WasAuthorized,
                                        Note = note ?? string.Empty
                                    };
                                })
                            .ToList();

                    if (entries.Count > MaximumIdentityBundleEntries)
                    {
                        message = "Too many stored identities to export in one bundle.";
                        return false;
                    }

                    bundle = new IdentityBundleFile
                    {
                        FormatVersion = IdentityBundleFormatVersion,
                        ExportedAtUtc = DateTime.UtcNow,
                        Entries = entries
                    };
                }

                string json = JsonConvert.SerializeObject(
                    bundle,
                    Formatting.Indented);

                if (Encoding.UTF8.GetByteCount(json) > MaximumIdentityBundleBytes)
                {
                    message = "The identity bundle exceeds the 4 MiB size limit.";
                    return false;
                }

                fileName =
                    "identity-bundle-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") +
                    "-" +
                    Guid.NewGuid().ToString("N").Substring(0, 6) +
                    ".json";

                string targetPath = Path.Combine(
                    _identityBundleDirectory,
                    fileName);

                temporaryPath = targetPath + ".tmp";

                File.WriteAllText(
                    temporaryPath,
                    json,
                    new UTF8Encoding(false));

                File.Move(
                    temporaryPath,
                    targetPath);

                message =
                    "Identity bundle exported to HKMP.List/IdentityBundles/" +
                    fileName + ".";

                return true;
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(temporaryPath))
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch
                    {
                    }
                }

                fileName = string.Empty;
                message = "Identity bundle export failed: " + ex.Message;
                return false;
            }
        }

        public bool ImportIdentityBundle(
            string requestedFileName,
            out string message)
        {
            message = string.Empty;

            if (!AdminIdentityBundleActionData.IsSafeFileName(
                    requestedFileName ?? string.Empty,
                    allowEmpty: false) ||
                !requestedFileName.StartsWith(
                    "identity-bundle-",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetExtension(requestedFileName),
                    ".json",
                    StringComparison.OrdinalIgnoreCase))
            {
                message = "The selected identity bundle file name is invalid.";
                return false;
            }

            string path = Path.Combine(
                _identityBundleDirectory,
                requestedFileName);

            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists)
                {
                    message = "The selected identity bundle no longer exists.";
                    return false;
                }

                if (info.Length <= 0 || info.Length > MaximumIdentityBundleBytes)
                {
                    message = "The selected identity bundle is empty or exceeds 4 MiB.";
                    return false;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                IdentityBundleFile bundle =
                    JsonConvert.DeserializeObject<IdentityBundleFile>(json);

                if (bundle == null ||
                    bundle.FormatVersion != IdentityBundleFormatVersion ||
                    bundle.Entries == null)
                {
                    message = "The selected file is not a supported HKMP.List identity bundle.";
                    return false;
                }

                if (bundle.Entries.Count > MaximumIdentityBundleEntries)
                {
                    message = "The identity bundle contains too many records.";
                    return false;
                }

                List<IdentityBundleEntry> validated =
                    new List<IdentityBundleEntry>(bundle.Entries.Count);

                foreach (IdentityBundleEntry entry in bundle.Entries)
                {
                    if (entry == null ||
                        string.IsNullOrWhiteSpace(entry.AuthorizationKey) ||
                        entry.AuthorizationKey.Length > ListProtocol.MaxAuthorizationKeyLength ||
                        entry.Note == null ||
                        entry.Note.Length > ListProtocol.MaxNoteLength ||
                        (entry.Username ?? string.Empty).Length > ListProtocol.MaxUsernameLength ||
                        !entry.WasAuthorized)
                    {
                        message = "The identity bundle contains an invalid or oversized record.";
                        return false;
                    }

                    string authorizationKey = entry.AuthorizationKey.Trim();
                    if (authorizationKey.Length == 0)
                    {
                        message = "The identity bundle contains an empty authorization key.";
                        return false;
                    }

                    string computedFingerprint =
                        ComputeAuthorizationFingerprint(authorizationKey);

                    string suppliedFingerprint =
                        NormalizeFingerprint(entry.AuthorizationFingerprint);

                    if (computedFingerprint == null ||
                        suppliedFingerprint == null ||
                        !string.Equals(
                            computedFingerprint,
                            suppliedFingerprint,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        message = "The identity bundle contains a fingerprint that does not match its authorization key.";
                        return false;
                    }

                    validated.Add(
                        new IdentityBundleEntry
                        {
                            AuthorizationFingerprint = computedFingerprint,
                            AuthorizationKey = authorizationKey,
                            Username = Truncate(
                                entry.Username,
                                ListProtocol.MaxUsernameLength),
                            WasAuthorized = entry.WasAuthorized,
                            Note = entry.Note
                        });
                }

                lock (_gate)
                {
                    Dictionary<string, AuthorizationIdentityRecord> previousKeys =
                        new Dictionary<string, AuthorizationIdentityRecord>(
                            _identityKeys,
                            StringComparer.OrdinalIgnoreCase);

                    Dictionary<string, string> previousNotes =
                        new Dictionary<string, string>(
                            _identityNotes,
                            StringComparer.OrdinalIgnoreCase);

                    foreach (IdentityBundleEntry entry in validated)
                    {
                        _identityKeys[entry.AuthorizationFingerprint] =
                            new AuthorizationIdentityRecord
                            {
                                AuthorizationFingerprint = entry.AuthorizationFingerprint,
                                AuthorizationKey = entry.AuthorizationKey,
                                Username = entry.Username ?? string.Empty,
                                WasAuthorized = entry.WasAuthorized
                            };

                        if (string.IsNullOrEmpty(entry.Note))
                        {
                            _identityNotes.Remove(entry.AuthorizationFingerprint);
                        }
                        else
                        {
                            _identityNotes[entry.AuthorizationFingerprint] = entry.Note;
                        }
                    }

                    if (!SaveLocked())
                    {
                        _identityKeys.Clear();
                        foreach (KeyValuePair<string, AuthorizationIdentityRecord> pair in previousKeys)
                        {
                            _identityKeys[pair.Key] = pair.Value;
                        }

                        _identityNotes.Clear();
                        foreach (KeyValuePair<string, string> pair in previousNotes)
                        {
                            _identityNotes[pair.Key] = pair.Value;
                        }

                        message = "Imported identity data could not be saved to admin.json.";
                        return false;
                    }
                }

                message =
                    "Imported " + validated.Count +
                    " identity records. HKMP permission settings were not changed.";
                return true;
            }
            catch (Exception ex)
            {
                message = "Identity bundle import failed: " + ex.Message;
                return false;
            }
        }

        public bool Ban(
            IServerPlayer player,
            string reason)
        {
            if (player == null)
            {
                return false;
            }

            string fingerprint =
                ListHostNotesStore.GetAuthorizationFingerprint(
                    player);

            if (string.IsNullOrWhiteSpace(
                    fingerprint))
            {
                return false;
            }

            string normalized =
                NormalizeFingerprint(
                    fingerprint);

            if (normalized == null)
            {
                return false;
            }

            string username =
                Truncate(
                    player.Username,
                    ListProtocol.MaxUsernameLength);

            string banReason =
                Truncate(
                    reason,
                    ListProtocol.MaxActionMessageLength);

            lock (_gate)
            {
                BanRecord previous;
                bool hadPrevious = _bans.TryGetValue(normalized, out previous);

                _bans[normalized] =
                    new BanRecord
                    {
                        AuthorizationFingerprint = normalized,
                        Username = username,
                        Reason = banReason,
                        CreatedUtc = DateTime.UtcNow
                    };

                if (SaveLocked())
                {
                    return true;
                }

                if (hadPrevious)
                {
                    _bans[normalized] = previous;
                }
                else
                {
                    _bans.Remove(normalized);
                }

                return false;
            }
        }

        public bool IsBanned(
            string authorizationFingerprint)
        {
            string key =
                NormalizeFingerprint(
                    authorizationFingerprint);

            if (key == null)
            {
                return false;
            }

            lock (_gate)
            {
                return _bans.ContainsKey(
                    key);
            }
        }

        public bool Unban(
            string authorizationFingerprint)
        {
            string key =
                NormalizeFingerprint(
                    authorizationFingerprint);

            if (key == null)
            {
                return false;
            }

            lock (_gate)
            {
                BanRecord previous;
                if (!_bans.TryGetValue(key, out previous))
                {
                    return false;
                }

                _bans.Remove(key);
                if (SaveLocked())
                {
                    return true;
                }

                _bans[key] = previous;
                return false;
            }
        }

        public bool ClearBans()
        {
            lock (_gate)
            {
                if (_bans.Count == 0)
                {
                    return true;
                }

                Dictionary<string, BanRecord> previous =
                    new Dictionary<string, BanRecord>(
                        _bans,
                        StringComparer.OrdinalIgnoreCase);

                _bans.Clear();
                if (SaveLocked())
                {
                    return true;
                }

                foreach (KeyValuePair<string, BanRecord> pair in previous)
                {
                    _bans[pair.Key] = pair.Value;
                }

                return false;
            }
        }

        public IReadOnlyList<AdminBanEntryView> GetBans()
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
                    .Select(
                        x =>
                            new AdminBanEntryView(
                                x.AuthorizationFingerprint,
                                x.Username,
                                x.Reason))
                    .ToList()
                    .AsReadOnly();
            }
        }

        private void Load()
        {
            lock (_gate)
            {
                _bans.Clear();
                _identityNotes.Clear();
                _identityKeys.Clear();
                _adminPermissions.Clear();

                if (!File.Exists(
                        _filePath))
                {
                    return;
                }

                try
                {
                    string json =
                        File.ReadAllText(
                            _filePath,
                            Encoding.UTF8);

                    AdminStoreData loaded =
                        JsonConvert.DeserializeObject<AdminStoreData>(
                            json);

                    if (loaded == null)
                    {
                        return;
                    }

                    if (loaded.AuthorizationIdentities != null)
                    {
                        foreach (AuthorizationIdentityRecord record in loaded.AuthorizationIdentities)
                        {
                            if (record == null ||
                                string.IsNullOrWhiteSpace(record.AuthorizationKey) ||
                                record.AuthorizationKey.Length > ListProtocol.MaxAuthorizationKeyLength)
                            {
                                continue;
                            }

                            string authorizationKey = record.AuthorizationKey.Trim();
                            string fingerprint = ComputeAuthorizationFingerprint(authorizationKey);
                            string suppliedFingerprint = NormalizeFingerprint(record.AuthorizationFingerprint);

                            if (fingerprint == null || suppliedFingerprint == null ||
                                !string.Equals(fingerprint, suppliedFingerprint, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            _identityKeys[fingerprint] = new AuthorizationIdentityRecord
                            {
                                AuthorizationFingerprint = fingerprint,
                                AuthorizationKey = authorizationKey,
                                Username = Truncate(record.Username, ListProtocol.MaxUsernameLength),
                                WasAuthorized = record.WasAuthorized,
                                AdminAccessRevoked = record.AdminAccessRevoked
                            };
                        }
                    }

                    if (loaded.Bans != null)
                    {
                        foreach (BanRecord record
                                 in loaded.Bans)
                        {
                            if (record == null)
                            {
                                continue;
                            }

                            string fingerprint =
                                NormalizeFingerprint(
                                    record.AuthorizationFingerprint);

                            if (fingerprint == null)
                            {
                                continue;
                            }

                            _bans[fingerprint] =
                                new BanRecord
                                {
                                    AuthorizationFingerprint =
                                        fingerprint,

                                    Username =
                                        Truncate(
                                            record.Username,
                                            ListProtocol.MaxUsernameLength),

                                    Reason =
                                        Truncate(
                                            record.Reason,
                                            ListProtocol.MaxActionMessageLength),

                                    CreatedUtc =
                                        record.CreatedUtc
                                };
                        }
                    }

                    if (loaded.IdentityNotes != null)
                    {
                        foreach (
                            KeyValuePair<string, string> pair
                            in loaded.IdentityNotes)
                        {
                            string fingerprint =
                                NormalizeFingerprint(
                                    pair.Key);

                            if (fingerprint == null ||
                                pair.Value == null ||
                                pair.Value.Length >
                                ListProtocol.MaxNoteLength)
                            {
                                continue;
                            }

                            if (pair.Value.Length == 0)
                            {
                                continue;
                            }

                            _identityNotes[fingerprint] =
                                pair.Value;
                        }
                    }

                    if (loaded.AdminPermissions != null)
                    {
                        foreach (KeyValuePair<string, AdminPermissions> pair in loaded.AdminPermissions)
                        {
                            string fingerprint = NormalizeFingerprint(pair.Key);
                            AuthorizationIdentityRecord identity;
                            if (fingerprint == null || pair.Value == null ||
                                !_identityKeys.TryGetValue(fingerprint, out identity) ||
                                identity == null || !identity.WasAuthorized)
                            {
                                continue;
                            }

                            _adminPermissions[fingerprint] = pair.Value.Clone();
                        }
                    }
                }
                catch
                {
                    _bans.Clear();
                    _identityNotes.Clear();
                    _identityKeys.Clear();
                    _adminPermissions.Clear();
                }
            }
        }

        private bool SaveLocked()
        {
            string directory =
                Path.GetDirectoryName(
                    _filePath);

            string temporaryPath =
                _filePath + ".tmp";

            try
            {
                AdminStoreData data =
                    new AdminStoreData
                    {
                        Bans =
                            _bans.Values
                                .ToList(),

                        IdentityNotes =
                            new Dictionary<string, string>(
                                _identityNotes,
                                StringComparer.OrdinalIgnoreCase),

                        AuthorizationIdentities =
                            _identityKeys.Values
                                .Where(record => record != null)
                                .ToList(),

                        AdminPermissions =
                            _adminPermissions.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value == null
                                    ? AdminPermissions.NoAccess()
                                    : pair.Value.Clone(),
                                StringComparer.OrdinalIgnoreCase)
                    };

                string json =
                    JsonConvert.SerializeObject(
                        data,
                        Formatting.Indented);

                File.WriteAllText(
                    temporaryPath,
                    json,
                    Encoding.UTF8);

                if (File.Exists(
                        _filePath))
                {
                    try
                    {
                        File.Replace(
                            temporaryPath,
                            _filePath,
                            null);
                    }
                    catch
                    {
                        File.Copy(
                            temporaryPath,
                            _filePath,
                            true);

                        try
                        {
                            File.Delete(
                                temporaryPath);
                        }
                        catch
                        {
                        }
                    }
                }
                else
                {
                    File.Move(
                        temporaryPath,
                        _filePath);
                }

                return true;
            }
            catch
            {
                try
                {
                    if (File.Exists(
                            temporaryPath))
                    {
                        File.Delete(
                            temporaryPath);
                    }
                }
                catch
                {
                }

                return false;
            }
        }

        private static string ComputeAuthorizationFingerprint(
            string authorizationKey)
        {
            if (string.IsNullOrWhiteSpace(authorizationKey))
            {
                return null;
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(
                    authorizationKey.Trim());

                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder builder = new StringBuilder(hash.Length * 2);

                foreach (byte value in hash)
                {
                    builder.Append(value.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static string NormalizeFingerprint(
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                return null;
            }

            string fingerprint =
                value.Trim();

            if (fingerprint.Length !=
                ListProtocol.MaxAuthorizationFingerprintLength)
            {
                return null;
            }

            for (int i = 0;
                 i < fingerprint.Length;
                 i++)
            {
                char c =
                    fingerprint[i];

                bool valid =
                    (c >= '0' && c <= '9') ||
                    (c >= 'a' && c <= 'f') ||
                    (c >= 'A' && c <= 'F');

                if (!valid)
                {
                    return null;
                }
            }

            return fingerprint.ToLowerInvariant();
        }

        private static string Truncate(
            string value,
            int maximumLength)
        {
            string result =
                value ?? string.Empty;

            if (result.Length <=
                maximumLength)
            {
                return result;
            }

            return result.Substring(
                0,
                maximumLength);
        }

        private sealed class AdminStoreData
        {
            public List<BanRecord> Bans { get; set; } =
                new List<BanRecord>();

            public Dictionary<string, string> IdentityNotes { get; set; } =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            public List<AuthorizationIdentityRecord> AuthorizationIdentities { get; set; } =
                new List<AuthorizationIdentityRecord>();

            public Dictionary<string, AdminPermissions> AdminPermissions { get; set; } =
                new Dictionary<string, AdminPermissions>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class AuthorizationIdentityRecord
        {
            public string AuthorizationFingerprint { get; set; }
            public string AuthorizationKey { get; set; }
            public string Username { get; set; }
            public bool WasAuthorized { get; set; }
            public bool AdminAccessRevoked { get; set; }
        }

        private sealed class IdentityBundleFile
        {
            public int FormatVersion { get; set; }
            public DateTime ExportedAtUtc { get; set; }
            public List<IdentityBundleEntry> Entries { get; set; } =
                new List<IdentityBundleEntry>();
        }

        private sealed class IdentityBundleEntry
        {
            public string AuthorizationFingerprint { get; set; }
            public string AuthorizationKey { get; set; }
            public string Username { get; set; }
            public bool WasAuthorized { get; set; }
            public string Note { get; set; }
        }

        private sealed class BanRecord
        {
            public string AuthorizationFingerprint { get; set; }
            public string Username { get; set; }
            public string Reason { get; set; }
            public DateTime CreatedUtc { get; set; }
        }
    }
}
