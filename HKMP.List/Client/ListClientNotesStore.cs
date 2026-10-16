using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HKMP.List.Shared;
using Newtonsoft.Json;
using UnityEngine;

namespace HKMP.List.Client
{
    internal sealed class ListClientNotesStore
    {
        private const int MaximumStoredNotes = 5000;
        private const int MaximumIdentityKeyLength = 320;

        private readonly object _gate = new object();
        private readonly string _filePath;
        private readonly Dictionary<string, string> _notes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ListClientNotesStore()
        {
            string root = Application.persistentDataPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                root = AppDomain.CurrentDomain.BaseDirectory;
            }

            string directory = Path.Combine(root, "HKMP.List");
            _filePath = Path.Combine(directory, "player-notes.json");

            try
            {
                Directory.CreateDirectory(directory);
                Load();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("HKMP.List: player notes could not be loaded: " + exception.Message);
            }
        }

        public static string GetIdentityKey(
            string authorizationFingerprint,
            string username)
        {
            if (IsValidFingerprint(authorizationFingerprint))
            {
                return "auth:" + authorizationFingerprint.Trim().ToLowerInvariant();
            }

            string normalizedName = NormalizeUsername(username);
            if (normalizedName.Length == 0)
            {
                return string.Empty;
            }

            return "name:" + normalizedName;
        }

        public static bool IsValidFingerprint(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 64)
            {
                return false;
            }

            for (int i = 0; i < fingerprint.Length; i++)
            {
                char value = fingerprint[i];
                if (!((value >= '0' && value <= '9') ||
                      (value >= 'a' && value <= 'f') ||
                      (value >= 'A' && value <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }

        public bool TryGet(
            string identityKey,
            out string note)
        {
            string key = NormalizeIdentityKey(identityKey);
            lock (_gate)
            {
                if (key != null && _notes.TryGetValue(key, out note))
                {
                    return true;
                }
            }

            note = string.Empty;
            return false;
        }

        public bool Set(
            string identityKey,
            string note)
        {
            string key = NormalizeIdentityKey(identityKey);
            string value = note ?? string.Empty;

            if (key == null || value.Length > ListProtocol.MaxNoteLength)
            {
                return false;
            }

            lock (_gate)
            {
                string previous;
                bool hadPrevious = _notes.TryGetValue(key, out previous);
                if (hadPrevious && string.Equals(previous, value, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!hadPrevious && _notes.Count >= MaximumStoredNotes)
                {
                    return false;
                }

                _notes[key] = value;
                if (SaveLocked())
                {
                    return true;
                }

                if (hadPrevious)
                {
                    _notes[key] = previous;
                }
                else
                {
                    _notes.Remove(key);
                }

                return false;
            }
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
                    FileInfo file = new FileInfo(_filePath);
                    if (file.Length <= 0 || file.Length > 32L * 1024L * 1024L)
                    {
                        return;
                    }

                    Dictionary<string, string> loaded =
                        JsonConvert.DeserializeObject<Dictionary<string, string>>(
                            File.ReadAllText(_filePath, Encoding.UTF8));

                    if (loaded == null)
                    {
                        return;
                    }

                    foreach (KeyValuePair<string, string> pair in loaded
                        .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                        .Take(MaximumStoredNotes))
                    {
                        string key = NormalizeIdentityKey(pair.Key);
                        string value = pair.Value;
                        if (key == null || value == null ||
                            value.Length > ListProtocol.MaxNoteLength)
                        {
                            continue;
                        }

                        _notes[key] = value;
                    }
                }
                catch (Exception exception)
                {
                    _notes.Clear();
                    Debug.LogWarning("HKMP.List: player notes file is invalid: " + exception.Message);
                }
            }
        }

        private bool SaveLocked()
        {
            string temporaryPath = _filePath + ".tmp";
            try
            {
                string directory = Path.GetDirectoryName(_filePath);
                Directory.CreateDirectory(directory);
                string json = JsonConvert.SerializeObject(_notes, Formatting.Indented);
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));

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
            catch (Exception exception)
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

                Debug.LogWarning("HKMP.List: player note could not be saved: " + exception.Message);
                return false;
            }
        }

        private static string NormalizeIdentityKey(string identityKey)
        {
            if (string.IsNullOrWhiteSpace(identityKey))
            {
                return null;
            }

            string key = identityKey.Trim();
            if (key.Length > MaximumIdentityKeyLength ||
                !(key.StartsWith("auth:", StringComparison.OrdinalIgnoreCase) ||
                  key.StartsWith("name:", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return key;
        }

        private static string NormalizeUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return string.Empty;
            }

            string normalized = string.Join(
                " ",
                username.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

            return normalized.ToLowerInvariant();
        }
    }
}
