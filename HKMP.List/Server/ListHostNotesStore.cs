using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Hkmp.Api.Server;
using HKMP.List.Shared;

namespace HKMP.List.Server
{
    internal sealed class ListHostNotesStore
    {
        private readonly object _gate = new object();
        private readonly string _filePath;
        private readonly Dictionary<string, Dictionary<string, string>> _notes =
            new Dictionary<string, Dictionary<string, string>>(
                StringComparer.Ordinal);

        public ListHostNotesStore(string baseDirectory)
        {
            string directory =
                Path.Combine(
                    baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory,
                    "HKMP.List");

            Directory.CreateDirectory(directory);

            _filePath =
                Path.Combine(
                    directory,
                    "notes.json");

            Load();
        }

        public string GetNote(
            IServerPlayer owner,
            IServerPlayer target)
        {
            if (owner == null || target == null)
            {
                return string.Empty;
            }

            string ownerKey =
                GetIdentityKey(owner);

            string targetKey =
                GetIdentityKey(target);

            lock (_gate)
            {
                Dictionary<string, string> ownerNotes;
                if (!_notes.TryGetValue(
                        ownerKey,
                        out ownerNotes))
                {
                    return string.Empty;
                }

                string note;
                return ownerNotes.TryGetValue(
                    targetKey,
                    out note)
                    ? note
                    : string.Empty;
            }
        }

        public bool SetNote(
            IServerPlayer owner,
            IServerPlayer target,
            string note)
        {
            if (owner == null || target == null)
            {
                return false;
            }

            string value = note ?? string.Empty;
            if (value.Length > ListProtocol.MaxNoteLength)
            {
                return false;
            }

            string ownerKey =
                GetIdentityKey(owner);

            string targetKey =
                GetIdentityKey(target);

            lock (_gate)
            {
                Dictionary<string, string> ownerNotes;
                bool hadOwner =
                    _notes.TryGetValue(
                        ownerKey,
                        out ownerNotes);

                if (!hadOwner)
                {
                    ownerNotes =
                        new Dictionary<string, string>(
                            StringComparer.Ordinal);
                }

                string previousValue = null;
                bool hadPreviousValue =
                    hadOwner &&
                    ownerNotes.TryGetValue(
                        targetKey,
                        out previousValue);

                if (hadPreviousValue &&
                    string.Equals(
                        previousValue,
                        value,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                if (value.Length == 0 && !hadPreviousValue)
                {
                    return true;
                }

                if (value.Length == 0)
                {
                    ownerNotes.Remove(targetKey);

                    if (ownerNotes.Count == 0)
                    {
                        _notes.Remove(ownerKey);
                    }
                }
                else
                {
                    ownerNotes[targetKey] = value;
                    _notes[ownerKey] = ownerNotes;
                }

                if (SaveLocked())
                {
                    return true;
                }

                if (hadOwner)
                {
                    if (hadPreviousValue)
                    {
                        ownerNotes[targetKey] = previousValue;
                    }
                    else
                    {
                        ownerNotes.Remove(targetKey);
                    }

                    _notes[ownerKey] = ownerNotes;
                }
                else
                {
                    _notes.Remove(ownerKey);
                }

                return false;
            }
        }

        public static string GetIdentityKey(
            IServerPlayer player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            string authKey =
                player.AuthKey ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(authKey))
            {
                return "auth:" +
                       Sha256(
                           authKey.Trim());
            }

            return "id:" +
                   player.Id.ToString();
        }

        public static string GetAuthorizationFingerprint(
            IServerPlayer player)
        {
            if (player == null ||
                string.IsNullOrWhiteSpace(player.AuthKey))
            {
                return string.Empty;
            }

            return Sha256(
                player.AuthKey.Trim());
        }

        private void Load()
        {
            lock (_gate)
            {
                _notes.Clear();

                if (!File.Exists(_filePath))
                {
                    return;
                }

                try
                {
                    string json =
                        File.ReadAllText(
                            _filePath,
                            Encoding.UTF8);

                    Dictionary<string, Dictionary<string, string>> loaded =
                        JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(
                            json);

                    if (loaded == null)
                    {
                        return;
                    }

                    foreach (KeyValuePair<string, Dictionary<string, string>> ownerPair in loaded)
                    {
                        if (string.IsNullOrWhiteSpace(ownerPair.Key) ||
                            ownerPair.Value == null)
                        {
                            continue;
                        }

                        Dictionary<string, string> targetNotes =
                            new Dictionary<string, string>(
                                StringComparer.Ordinal);

                        foreach (KeyValuePair<string, string> targetPair in ownerPair.Value)
                        {
                            if (string.IsNullOrWhiteSpace(targetPair.Key) ||
                                targetPair.Value == null ||
                                targetPair.Value.Length > ListProtocol.MaxNoteLength)
                            {
                                continue;
                            }

                            targetNotes[targetPair.Key] = targetPair.Value;
                        }

                        if (targetNotes.Count > 0)
                        {
                            _notes[ownerPair.Key] = targetNotes;
                        }
                    }
                }
                catch
                {
                    _notes.Clear();
                }
            }
        }

        private bool SaveLocked()
        {
            string directory =
                Path.GetDirectoryName(_filePath);

            string temporaryPath =
                _filePath + ".tmp";

            try
            {
                string json =
                    JsonConvert.SerializeObject(
                        _notes,
                        Formatting.Indented);

                File.WriteAllText(
                    temporaryPath,
                    json,
                    Encoding.UTF8);

                if (File.Exists(_filePath))
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
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                }

                return false;
            }
        }

        private static string Sha256(
            string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes =
                    Encoding.UTF8.GetBytes(
                        value ?? string.Empty);

                byte[] hash =
                    sha.ComputeHash(bytes);

                StringBuilder builder =
                    new StringBuilder(
                        hash.Length * 2);

                foreach (byte item in hash)
                {
                    builder.Append(
                        item.ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}
