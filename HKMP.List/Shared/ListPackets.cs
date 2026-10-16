using System;
using System.Collections.Generic;
using Hkmp.Game;
using Hkmp.Networking.Packet;
using System.Linq;
namespace HKMP.List.Shared
{
    public enum ListClientPacketId
    {
        NoteChanged = 0,
        AdminAction = 1,
        AdminServerSettingsChanged = 2,
        AdminIdentityNoteChanged = 3,
        AdminIdentityBundleAction = 4,
        AdminPermissionsChanged = 5,
        SettingsProposalAction = 6,
        TeamManagementAction = 7,
        SkinPreferenceChanged = 8
    }

    public enum ListServerPacketId
    {
        PlayerListBegin = 0,
        PlayerListEntry = 1,
        PlayerListEnd = 2,
        LocalPermissions = 3,
        AdminPlayer = 4,
        PlayerNote = 5,
        ServerSettings = 6,
        AdminActionResult = 7,
        AdminBanList = 8,
        AdminIdentityBundleFiles = 9,
        SettingsProposal = 10,
        TeamSettings = 11,
        AssignLocalTeam = 12
    }

    public enum ListAdminAction
    {
        Kick = 0,
        Ban = 1,
        Unban = 2,
        UnbanAll = 3,
        DemoteAdministrator = 4,
        RestoreAdministrator = 5
    }

    public enum AdminIdentityBundleAction
    {
        ClearNotes = 0,
        ExportBundle = 1,
        ListBundles = 2,
        ImportBundle = 3
    }

    public enum ServerSettingsProposalAction : byte
    {
        Reject = 0,
        Approve = 1
    }

    public static class ListProtocol
    {
        public const byte Version = 13;
        public const int MaxUsernameLength = 20;
        public const int MaxNoteLength = 4096;
        public const int MaxActionMessageLength = 255;
        public const int MaxAuthorizationFingerprintLength = 64;
        public const int MaxAuthorizationKeyLength = 256;
        public const int MaxIdentityBundleFileNameLength = 128;
        public const int MaxIdentityBundleFiles = 128;
        public const int MaxBanEntries = 2048;
        public const int MaxSettingsProposalIdLength = 64;
        public const int MaxSettingsProposals = 64;
        public const int SyncIntervalMilliseconds = 250;

        internal static void EnsureVersion(
            IPacket packet)
        {
            byte version =
                packet.ReadByte();

            if (version != Version)
            {
                throw new InvalidOperationException(
                    "Unsupported HKMP.List protocol version.");
            }
        }
    }

    public sealed class PlayerListBeginData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;

        public ulong SnapshotId { get; set; }
        public ushort PlayerCount { get; set; }

        public void WriteData(IPacket packet)
        {
            packet.Write(ListProtocol.Version);
            packet.Write(SnapshotId);
            packet.Write(PlayerCount);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);

            SnapshotId = packet.ReadULong();
            PlayerCount = packet.ReadUShort();
        }
    }

    public sealed class PlayerListEntryData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;

        public ulong SnapshotId { get; set; }
        public ushort PlayerId { get; set; }
        public string Username { get; set; }
        public string AuthorizationFingerprint { get; set; }
        public Team Team { get; set; }
        public byte SkinId { get; set; }
        public bool IsHost { get; set; }
        public bool IsAuthorized { get; set; }
        public bool IsTeamLeader { get; set; }
        public bool HasTeamSkin { get; set; }
        public byte TeamSkinId { get; set; }
        public bool IsTeamSkinLocked { get; set; }
        public bool HasListSkinOverride { get; set; }
        public byte ListSkinOverrideId { get; set; }
        public long ListSkinUpdatedAtUtcTicks { get; set; }
        public long TeamSkinUpdatedAtUtcTicks { get; set; }

        public void WriteData(IPacket packet)
        {
            string username = Username ?? string.Empty;
            string fingerprint = AuthorizationFingerprint ?? string.Empty;

            if (username.Length > ListProtocol.MaxUsernameLength)
            {
                throw new ArgumentException(
                    "Username is too long.",
                    nameof(Username));
            }

            if (fingerprint.Length > ListProtocol.MaxAuthorizationFingerprintLength)
            {
                throw new ArgumentException(
                    "Authorization fingerprint is too long.",
                    nameof(AuthorizationFingerprint));
            }

            packet.Write(ListProtocol.Version);
            packet.Write(SnapshotId);
            packet.Write(PlayerId);
            packet.Write(username);
            packet.Write(fingerprint);
            packet.Write((byte)Team);
            packet.Write(SkinId);
            packet.Write(IsHost);
            packet.Write(IsAuthorized);
            packet.Write(IsTeamLeader);
            packet.Write(HasTeamSkin);
            packet.Write(TeamSkinId);
            packet.Write(IsTeamSkinLocked);
            packet.Write(HasListSkinOverride);
            packet.Write(HasListSkinOverride ? ListSkinOverrideId : (byte)0);
            packet.Write(ListSkinUpdatedAtUtcTicks);
            packet.Write(TeamSkinUpdatedAtUtcTicks);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);
            SnapshotId = packet.ReadULong();
            PlayerId = packet.ReadUShort();
            Username =
                packet.ReadString(
                    ListProtocol.MaxUsernameLength);
            AuthorizationFingerprint = packet.ReadString(ListProtocol.MaxAuthorizationFingerprintLength);
            Team = (Team)packet.ReadByte();
            SkinId = packet.ReadByte();
            IsHost = packet.ReadBool();
            IsAuthorized = packet.ReadBool();
            IsTeamLeader = packet.ReadBool();
            HasTeamSkin = packet.ReadBool();
            TeamSkinId = packet.ReadByte();
            IsTeamSkinLocked = packet.ReadBool();
            HasListSkinOverride = packet.ReadBool();
            ListSkinOverrideId = packet.ReadByte();
            ListSkinUpdatedAtUtcTicks = packet.ReadLong();
            TeamSkinUpdatedAtUtcTicks = packet.ReadLong();
            if (!HasListSkinOverride)
            {
                ListSkinOverrideId = 0;
            }
        }
    }

    public sealed class SkinPreferenceChangedData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => true;

        public bool HasListSkinOverride { get; set; }
        public byte ListSkinOverrideId { get; set; }
        public long UpdatedAtUtcTicks { get; set; }

        public void WriteData(IPacket packet)
        {
            packet.Write(ListProtocol.Version);
            packet.Write(HasListSkinOverride);
            packet.Write(HasListSkinOverride ? ListSkinOverrideId : (byte)0);
            packet.Write(UpdatedAtUtcTicks);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);
            HasListSkinOverride = packet.ReadBool();
            ListSkinOverrideId = packet.ReadByte();
            UpdatedAtUtcTicks = packet.ReadLong();
            if (!HasListSkinOverride)
            {
                ListSkinOverrideId = 0;
            }
        }
    }

    public sealed class PlayerListEndData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;

        public ulong SnapshotId { get; set; }
        public ushort PlayerCount { get; set; }

        public void WriteData(IPacket packet)
        {
            packet.Write(ListProtocol.Version);
            packet.Write(SnapshotId);
            packet.Write(PlayerCount);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);

            SnapshotId = packet.ReadULong();
            PlayerCount = packet.ReadUShort();
        }
    }

    public sealed class LocalPermissionsData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => true;

        public ushort PlayerId { get; set; }
        public bool IsAuthorized { get; set; }
        public bool IsHost { get; set; }
        public AdminPermissions Permissions { get; set; } =
            AdminPermissions.FullAccess();

        public void WriteData(IPacket packet)
        {
            packet.Write(ListProtocol.Version);
            packet.Write(PlayerId);
            packet.Write(IsAuthorized);
            packet.Write(IsHost);
            (Permissions ?? AdminPermissions.NoAccess()).WriteTo(packet);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);

            PlayerId = packet.ReadUShort();
            IsAuthorized = packet.ReadBool();
            IsHost = packet.ReadBool();
            Permissions = AdminPermissions.ReadFrom(packet);
        }
    }

    public sealed class AdminPlayerData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;

        public ushort PlayerId { get; set; }
        public string Username { get; set; }
        public bool IsAuthorized { get; set; }
        public bool IsAdminEnabled { get; set; }
        public string AuthorizationFingerprint { get; set; }
        public string AuthorizationKey { get; set; }
        public string AuthorizationNote { get; set; }
        public bool IsHost { get; set; }
        public AdminPermissions Permissions { get; set; } =
            AdminPermissions.FullAccess();

        public void WriteData(IPacket packet)
        {
            string username = Username ?? string.Empty;
            string fingerprint = AuthorizationFingerprint ?? string.Empty;
            string authorizationKey = AuthorizationKey ?? string.Empty;
            string note = AuthorizationNote ?? string.Empty;

            if (username.Length > ListProtocol.MaxUsernameLength)
            {
                throw new ArgumentException(
                    "Username is too long.",
                    nameof(Username));
            }

            if (fingerprint.Length >
                ListProtocol.MaxAuthorizationFingerprintLength)
            {
                throw new ArgumentException(
                    "Authorization fingerprint is too long.",
                    nameof(AuthorizationFingerprint));
            }

            if (authorizationKey.Length >
                ListProtocol.MaxAuthorizationKeyLength)
            {
                throw new ArgumentException(
                    "Authorization key is too long.",
                    nameof(AuthorizationKey));
            }

            if (note.Length > ListProtocol.MaxNoteLength)
            {
                throw new ArgumentException(
                    "Authorization note is too long.",
                    nameof(AuthorizationNote));
            }

            packet.Write(ListProtocol.Version);
            packet.Write(PlayerId);
            packet.Write(username);
            packet.Write(IsAuthorized);
            packet.Write(IsAdminEnabled);
            packet.Write(fingerprint);
            packet.Write(authorizationKey);
            packet.Write(note);
            packet.Write(IsHost);
            (Permissions ?? AdminPermissions.NoAccess()).WriteTo(packet);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);

            PlayerId = packet.ReadUShort();

            Username =
                packet.ReadString(
                    ListProtocol.MaxUsernameLength);

            IsAuthorized = packet.ReadBool();
            IsAdminEnabled = packet.ReadBool();

            AuthorizationFingerprint =
                packet.ReadString(
                    ListProtocol.MaxAuthorizationFingerprintLength);

            AuthorizationKey =
                packet.ReadString(
                    ListProtocol.MaxAuthorizationKeyLength);

            AuthorizationNote =
                packet.ReadString(
                    ListProtocol.MaxNoteLength);
            IsHost = packet.ReadBool();
            Permissions = AdminPermissions.ReadFrom(packet);
        }
    }

    public sealed class AdminPermissionsChangedData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;

        public string AuthorizationFingerprint { get; set; }
        public AdminPermissions Permissions { get; set; } =
            AdminPermissions.NoAccess();

        public void WriteData(IPacket packet)
        {
            string fingerprint = AuthorizationFingerprint ?? string.Empty;
            if (fingerprint.Length > ListProtocol.MaxAuthorizationFingerprintLength)
            {
                throw new ArgumentException(
                    "Authorization fingerprint is too long.",
                    nameof(AuthorizationFingerprint));
            }

            packet.Write(ListProtocol.Version);
            packet.Write(fingerprint);
            (Permissions ?? AdminPermissions.NoAccess()).WriteTo(packet);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);
            AuthorizationFingerprint = packet.ReadString(
                ListProtocol.MaxAuthorizationFingerprintLength);
            Permissions = AdminPermissions.ReadFrom(packet);
        }
    }

    public sealed class PlayerNoteData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;

        public ushort PlayerId { get; set; }
        public string Note { get; set; }

        public void WriteData(IPacket packet)
        {
            string value = Note ?? string.Empty;

            if (value.Length > ListProtocol.MaxNoteLength)
            {
                throw new ArgumentException(
                    "Note is too long.",
                    nameof(Note));
            }

            packet.Write(ListProtocol.Version);
            packet.Write(PlayerId);
            packet.Write(value);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);

            PlayerId = packet.ReadUShort();
            Note =
                packet.ReadString(
                    ListProtocol.MaxNoteLength);
        }
    }

    public sealed class AdminActionData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;

        public ListAdminAction Action { get; set; }
        public ushort TargetPlayerId { get; set; }
        public string TargetAuthorizationFingerprint { get; set; }

        public void WriteData(IPacket packet)
        {
            string fingerprint =
                TargetAuthorizationFingerprint ?? string.Empty;

            if (fingerprint.Length >
                ListProtocol.MaxAuthorizationFingerprintLength)
            {
                throw new ArgumentException(
                    "Authorization fingerprint is too long.",
                    nameof(TargetAuthorizationFingerprint));
            }

            packet.Write(ListProtocol.Version);
            packet.Write((byte)Action);
            packet.Write(TargetPlayerId);
            packet.Write(fingerprint);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);

            Action =
                (ListAdminAction)packet.ReadByte();

            TargetPlayerId =
                packet.ReadUShort();

            TargetAuthorizationFingerprint =
                packet.ReadString(
                    ListProtocol.MaxAuthorizationFingerprintLength);
        }
    }

    public sealed class AdminIdentityNoteChangedData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;

        public string AuthorizationFingerprint { get; set; }
        public string Note { get; set; }

        public void WriteData(IPacket packet)
        {
            string fingerprint =
                AuthorizationFingerprint ?? string.Empty;

            string note =
                Note ?? string.Empty;

            if (fingerprint.Length >
                ListProtocol.MaxAuthorizationFingerprintLength)
            {
                throw new ArgumentException(
                    "Authorization fingerprint is too long.",
                    nameof(AuthorizationFingerprint));
            }

            if (note.Length > ListProtocol.MaxNoteLength)
            {
                throw new ArgumentException(
                    "Authorization note is too long.",
                    nameof(Note));
            }

            packet.Write(ListProtocol.Version);
            packet.Write(fingerprint);
            packet.Write(note);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);

            AuthorizationFingerprint =
                packet.ReadString(
                    ListProtocol.MaxAuthorizationFingerprintLength);

            Note =
                packet.ReadString(
                    ListProtocol.MaxNoteLength);
        }
    }

    public sealed class AdminIdentityBundleActionData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;

        public AdminIdentityBundleAction Action { get; set; }
        public string FileName { get; set; }

        public void WriteData(IPacket packet)
        {
            string fileName = FileName ?? string.Empty;

            if (fileName.Length > ListProtocol.MaxIdentityBundleFileNameLength ||
                !IsSafeFileName(fileName, allowEmpty: true))
            {
                throw new ArgumentException(
                    "Identity bundle file name is invalid.",
                    nameof(FileName));
            }

            if (!Enum.IsDefined(typeof(AdminIdentityBundleAction), Action))
            {
                throw new ArgumentOutOfRangeException(nameof(Action));
            }

            packet.Write(ListProtocol.Version);
            packet.Write((byte)Action);
            packet.Write(fileName);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);

            Action = (AdminIdentityBundleAction)packet.ReadByte();
            FileName = packet.ReadString(
                ListProtocol.MaxIdentityBundleFileNameLength);

            if (!Enum.IsDefined(typeof(AdminIdentityBundleAction), Action) ||
                !IsSafeFileName(FileName ?? string.Empty, allowEmpty: true))
            {
                throw new InvalidOperationException(
                    "Identity bundle request is invalid.");
            }
        }

        internal static bool IsSafeFileName(
            string fileName,
            bool allowEmpty)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return allowEmpty;
            }

            if (fileName == "." || fileName == ".." ||
                fileName.IndexOf('/') >= 0 ||
                fileName.IndexOf('\\') >= 0 ||
                fileName.IndexOf(':') >= 0 ||
                fileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            return string.Equals(
                System.IO.Path.GetFileName(fileName),
                fileName,
                StringComparison.Ordinal);
        }
    }

    public sealed class AdminIdentityBundleFilesData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => true;

        public bool Success { get; set; }
        public string Message { get; set; }
        public List<string> FileNames { get; set; } = new List<string>();

        public void WriteData(IPacket packet)
        {
            string message = Message ?? string.Empty;
            if (message.Length > ListProtocol.MaxActionMessageLength)
            {
                message = message.Substring(0, ListProtocol.MaxActionMessageLength);
            }

            List<string> names = (FileNames ?? new List<string>())
                .Where(name => AdminIdentityBundleActionData.IsSafeFileName(name, allowEmpty: false))
                .Where(name => name.Length <= ListProtocol.MaxIdentityBundleFileNameLength)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(ListProtocol.MaxIdentityBundleFiles)
                .ToList();

            packet.Write(ListProtocol.Version);
            packet.Write(Success);
            packet.Write(message);
            packet.Write((ushort)names.Count);

            foreach (string name in names)
            {
                packet.Write(name);
            }
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);
            Success = packet.ReadBool();
            Message = packet.ReadString(ListProtocol.MaxActionMessageLength);

            ushort count = packet.ReadUShort();
            if (count > ListProtocol.MaxIdentityBundleFiles)
            {
                throw new InvalidOperationException(
                    "Identity bundle file list is too large.");
            }

            List<string> names = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                string name = packet.ReadString(
                    ListProtocol.MaxIdentityBundleFileNameLength);

                if (!AdminIdentityBundleActionData.IsSafeFileName(name, allowEmpty: false))
                {
                    throw new InvalidOperationException(
                        "Identity bundle file list contains an invalid file name.");
                }

                names.Add(name);
            }

            FileNames = names;
        }
    }

    public sealed class AdminActionResultData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => true;

        public bool Success { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }

        public void WriteData(IPacket packet)
        {
            string code = Code ?? string.Empty;
            string message = Message ?? string.Empty;

            if (code.Length >
                    ListProtocol.MaxActionMessageLength ||
                message.Length >
                    ListProtocol.MaxActionMessageLength)
            {
                throw new ArgumentException(
                    "Admin action result text is too long.");
            }

            packet.Write(ListProtocol.Version);
            packet.Write(Success);
            packet.Write(code);
            packet.Write(message);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);

            Success = packet.ReadBool();

            Code =
                packet.ReadString(
                    ListProtocol.MaxActionMessageLength);

            Message =
                packet.ReadString(
                    ListProtocol.MaxActionMessageLength);
        }
    }

    public sealed class AdminBanListData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => true;

        public List<AdminBanEntryView> Entries { get; set; } =
            new List<AdminBanEntryView>();

        public void WriteData(IPacket packet)
        {
            List<AdminBanEntryView> entries =
                Entries ?? new List<AdminBanEntryView>();

            int count =
                Math.Min(
                    ListProtocol.MaxBanEntries,
                    entries.Count);

            packet.Write(ListProtocol.Version);
            packet.Write((ushort)count);

            for (int i = 0; i < count; i++)
            {
                AdminBanEntryView entry =
                    entries[i];

                if (entry == null)
                {
                    packet.Write(string.Empty);
                    packet.Write(string.Empty);
                    packet.Write(string.Empty);
                    continue;
                }

                string fingerprint =
                    entry.AuthorizationFingerprint ?? string.Empty;

                string username =
                    entry.Username ?? string.Empty;

                string reason =
                    entry.Reason ?? string.Empty;

                if (fingerprint.Length >
                    ListProtocol.MaxAuthorizationFingerprintLength)
                {
                    fingerprint =
                        fingerprint.Substring(
                            0,
                            ListProtocol.MaxAuthorizationFingerprintLength);
                }

                if (username.Length >
                    ListProtocol.MaxUsernameLength)
                {
                    username =
                        username.Substring(
                            0,
                            ListProtocol.MaxUsernameLength);
                }

                if (reason.Length >
                    ListProtocol.MaxActionMessageLength)
                {
                    reason =
                        reason.Substring(
                            0,
                            ListProtocol.MaxActionMessageLength);
                }

                packet.Write(fingerprint);
                packet.Write(username);
                packet.Write(reason);
            }
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);

            ushort count =
                packet.ReadUShort();

            if (count > ListProtocol.MaxBanEntries)
            {
                throw new InvalidOperationException(
                    "HKMP.List ban list is too large.");
            }

            Entries =
                new List<AdminBanEntryView>(
                    count);

            for (int i = 0; i < count; i++)
            {
                Entries.Add(
                    new AdminBanEntryView(
                        packet.ReadString(
                            ListProtocol.MaxAuthorizationFingerprintLength),
                        packet.ReadString(
                            ListProtocol.MaxUsernameLength),
                        packet.ReadString(
                            ListProtocol.MaxActionMessageLength)));
            }
        }
    }

    [Serializable]
    public sealed class ServerSettingsData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => true;

        public bool IsPvpEnabled { get; set; } = true;
        public bool IsBodyDamageEnabled { get; set; } = false;
        public bool AlwaysShowMapIcons { get; set; }
        public bool OnlyBroadcastMapIconWithWaywardCompass { get; set; } = true;
        public bool DisplayNames { get; set; } = true;
        public bool TeamsEnabled { get; set; } = true;
        public bool AllowSkins { get; set; } = true;
        public bool AllowParries { get; set; } = true;
        public byte NailDamage { get; set; } = 1;
        public byte GrubberflyElegyDamage { get; set; } = 1;
        public byte VengefulSpiritDamage { get; set; } = 1;
        public byte ShadeSoulDamage { get; set; } = 2;
        public byte DesolateDiveDamage { get; set; } = 1;
        public byte DescendingDarkDamage { get; set; } = 2;
        public byte HowlingWraithDamage { get; set; } = 1;
        public byte AbyssShriekDamage { get; set; } = 2;
        public byte GreatSlashDamage { get; set; } = 2;
        public byte DashSlashDamage { get; set; } = 2;
        public byte CycloneSlashDamage { get; set; } = 1;
        public byte SporeShroomDamage { get; set; } = 1;
        public byte SporeDungShroomDamage { get; set; } = 1;
        public byte ThornOfAgonyDamage { get; set; } = 1;
        public byte SharpShadowDamage { get; set; } = 1;

        public static ServerSettingsData CreateDefault()
        {
            return new ServerSettingsData();
        }

        public ServerSettingsData Clone()
        {
            return new ServerSettingsData
            {
                IsPvpEnabled = IsPvpEnabled,
                IsBodyDamageEnabled = IsBodyDamageEnabled,
                AlwaysShowMapIcons = AlwaysShowMapIcons,
                OnlyBroadcastMapIconWithWaywardCompass = OnlyBroadcastMapIconWithWaywardCompass,
                DisplayNames = DisplayNames,
                TeamsEnabled = TeamsEnabled,
                AllowSkins = AllowSkins,
                AllowParries = AllowParries,
                NailDamage = NailDamage,
                GrubberflyElegyDamage = GrubberflyElegyDamage,
                VengefulSpiritDamage = VengefulSpiritDamage,
                ShadeSoulDamage = ShadeSoulDamage,
                DesolateDiveDamage = DesolateDiveDamage,
                DescendingDarkDamage = DescendingDarkDamage,
                HowlingWraithDamage = HowlingWraithDamage,
                AbyssShriekDamage = AbyssShriekDamage,
                GreatSlashDamage = GreatSlashDamage,
                DashSlashDamage = DashSlashDamage,
                CycloneSlashDamage = CycloneSlashDamage,
                SporeShroomDamage = SporeShroomDamage,
                SporeDungShroomDamage = SporeDungShroomDamage,
                ThornOfAgonyDamage = ThornOfAgonyDamage,
                SharpShadowDamage = SharpShadowDamage
            };
        }

        public void WriteData(IPacket packet)
        {
            packet.Write(ListProtocol.Version);
            packet.Write(IsPvpEnabled);
            packet.Write(IsBodyDamageEnabled);
            packet.Write(AlwaysShowMapIcons);
            packet.Write(OnlyBroadcastMapIconWithWaywardCompass);
            packet.Write(DisplayNames);
            packet.Write(TeamsEnabled);
            packet.Write(AllowSkins);
            packet.Write(AllowParries);
            packet.Write(NailDamage);
            packet.Write(GrubberflyElegyDamage);
            packet.Write(VengefulSpiritDamage);
            packet.Write(ShadeSoulDamage);
            packet.Write(DesolateDiveDamage);
            packet.Write(DescendingDarkDamage);
            packet.Write(HowlingWraithDamage);
            packet.Write(AbyssShriekDamage);
            packet.Write(GreatSlashDamage);
            packet.Write(DashSlashDamage);
            packet.Write(CycloneSlashDamage);
            packet.Write(SporeShroomDamage);
            packet.Write(SporeDungShroomDamage);
            packet.Write(ThornOfAgonyDamage);
            packet.Write(SharpShadowDamage);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);

            IsPvpEnabled = packet.ReadBool();
            IsBodyDamageEnabled = packet.ReadBool();
            AlwaysShowMapIcons = packet.ReadBool();
            OnlyBroadcastMapIconWithWaywardCompass = packet.ReadBool();
            DisplayNames = packet.ReadBool();
            TeamsEnabled = packet.ReadBool();
            AllowSkins = packet.ReadBool();
            AllowParries = packet.ReadBool();
            NailDamage = packet.ReadByte();
            GrubberflyElegyDamage = packet.ReadByte();
            VengefulSpiritDamage = packet.ReadByte();
            ShadeSoulDamage = packet.ReadByte();
            DesolateDiveDamage = packet.ReadByte();
            DescendingDarkDamage = packet.ReadByte();
            HowlingWraithDamage = packet.ReadByte();
            AbyssShriekDamage = packet.ReadByte();
            GreatSlashDamage = packet.ReadByte();
            DashSlashDamage = packet.ReadByte();
            CycloneSlashDamage = packet.ReadByte();
            SporeShroomDamage = packet.ReadByte();
            SporeDungShroomDamage = packet.ReadByte();
            ThornOfAgonyDamage = packet.ReadByte();
            SharpShadowDamage = packet.ReadByte();
        }
    }

    public sealed class ServerSettingsProposalActionData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => true;

        public string ProposalId { get; set; }
        public ServerSettingsProposalAction Action { get; set; }

        public void WriteData(IPacket packet)
        {
            string id = ProposalId ?? string.Empty;
            if (id.Length == 0 || id.Length > ListProtocol.MaxSettingsProposalIdLength)
            {
                throw new InvalidOperationException("Settings proposal ID is invalid.");
            }

            if (!Enum.IsDefined(typeof(ServerSettingsProposalAction), Action))
            {
                throw new InvalidOperationException("Settings proposal action is invalid.");
            }

            packet.Write(ListProtocol.Version);
            packet.Write(id);
            packet.Write((byte)Action);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);
            ProposalId = packet.ReadString(ListProtocol.MaxSettingsProposalIdLength);
            Action = (ServerSettingsProposalAction)packet.ReadByte();

            if (string.IsNullOrWhiteSpace(ProposalId) ||
                !Enum.IsDefined(typeof(ServerSettingsProposalAction), Action))
            {
                throw new InvalidOperationException("Settings proposal action packet is invalid.");
            }
        }
    }

    public sealed class ServerSettingsProposalData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;

        public string ProposalId { get; set; }
        public string SubmitterUsername { get; set; }
        public string AuthorizationFingerprint { get; set; }
        public ulong SubmittedUtcTicks { get; set; }
        public bool IsRemoved { get; set; }
        public bool AdoptSettings { get; set; }
        public ServerSettingsData Settings { get; set; }

        public ServerSettingsProposalData Clone()
        {
            return new ServerSettingsProposalData
            {
                ProposalId = ProposalId,
                SubmitterUsername = SubmitterUsername,
                AuthorizationFingerprint = AuthorizationFingerprint,
                SubmittedUtcTicks = SubmittedUtcTicks,
                IsRemoved = IsRemoved,
                AdoptSettings = AdoptSettings,
                Settings = Settings == null ? null : Settings.Clone()
            };
        }

        public void WriteData(IPacket packet)
        {
            string id = ProposalId ?? string.Empty;
            string username = SubmitterUsername ?? string.Empty;
            string fingerprint = AuthorizationFingerprint ?? string.Empty;

            if (id.Length == 0 || id.Length > ListProtocol.MaxSettingsProposalIdLength)
            {
                throw new InvalidOperationException("Settings proposal ID is invalid.");
            }

            if (username.Length > ListProtocol.MaxUsernameLength)
            {
                username = username.Substring(0, ListProtocol.MaxUsernameLength);
            }

            if (fingerprint.Length > ListProtocol.MaxAuthorizationFingerprintLength)
            {
                fingerprint = fingerprint.Substring(0, ListProtocol.MaxAuthorizationFingerprintLength);
            }

            packet.Write(ListProtocol.Version);
            packet.Write(id);
            packet.Write(username);
            packet.Write(fingerprint);
            packet.Write(SubmittedUtcTicks);
            packet.Write(IsRemoved);
            packet.Write(AdoptSettings);

            if (!IsRemoved || AdoptSettings)
            {
                if (Settings == null)
                {
                    throw new InvalidOperationException("A settings proposal must contain server settings.");
                }

                Settings.WriteData(packet);
            }
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);

            ProposalId = packet.ReadString(ListProtocol.MaxSettingsProposalIdLength);
            SubmitterUsername = packet.ReadString(ListProtocol.MaxUsernameLength);
            AuthorizationFingerprint = packet.ReadString(ListProtocol.MaxAuthorizationFingerprintLength);
            SubmittedUtcTicks = packet.ReadULong();
            IsRemoved = packet.ReadBool();
            AdoptSettings = packet.ReadBool();

            if (string.IsNullOrWhiteSpace(ProposalId))
            {
                throw new InvalidOperationException("Settings proposal ID is invalid.");
            }

            if (IsRemoved && !AdoptSettings)
            {
                Settings = null;
                return;
            }

            Settings = new ServerSettingsData();
            Settings.ReadData(packet);
        }
    }

    public enum ListTeamAction : byte
    {
        AssignLeader = 0,
        RemoveLeader = 1,
        SetTeamSkin = 2,
        SetTeamSkinLock = 3,
        KickTeamMember = 4,
        AssignPlayerToTeam = 5,
        SetTeamChangesLock = 6
    }

    public sealed class TeamManagementActionData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;

        public ListTeamAction Action { get; set; }
        public Team Team { get; set; }
        public ushort TargetPlayerId { get; set; }
        public byte SkinId { get; set; }
        public bool Enabled { get; set; }

        public void WriteData(IPacket packet)
        {
            packet.Write(ListProtocol.Version);
            packet.Write((byte)Action);
            packet.Write((byte)Team);
            packet.Write(TargetPlayerId);
            packet.Write(SkinId);
            packet.Write(Enabled);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);
            Action = (ListTeamAction)packet.ReadByte();
            Team = (Team)packet.ReadByte();
            TargetPlayerId = packet.ReadUShort();
            SkinId = packet.ReadByte();
            Enabled = packet.ReadBool();

            if (!Enum.IsDefined(typeof(ListTeamAction), Action))
            {
                throw new InvalidOperationException("Unknown team-management action.");
            }
        }
    }

    public sealed class TeamSettingsEntryData
    {
        public Team Team { get; set; }
        public bool HasAssignedLeader { get; set; }
        public bool HasConnectedLeader { get; set; }
        public ushort LeaderPlayerId { get; set; }
        public string LeaderUsername { get; set; } = string.Empty;
        public bool HasTeamSkin { get; set; }
        public byte TeamSkinId { get; set; }
        public bool IsSkinLocked { get; set; }
        public long SkinUpdatedAtUtcTicks { get; set; }

        public TeamSettingsEntryData Clone()
        {
            return new TeamSettingsEntryData
            {
                Team = Team,
                HasAssignedLeader = HasAssignedLeader,
                HasConnectedLeader = HasConnectedLeader,
                LeaderPlayerId = LeaderPlayerId,
                LeaderUsername = LeaderUsername ?? string.Empty,
                HasTeamSkin = HasTeamSkin,
                TeamSkinId = TeamSkinId,
                IsSkinLocked = IsSkinLocked && HasTeamSkin,
                SkinUpdatedAtUtcTicks = SkinUpdatedAtUtcTicks
            };
        }
    }

    public sealed class TeamSettingsData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => true;

        public List<TeamSettingsEntryData> Entries { get; set; } =
            new List<TeamSettingsEntryData>();

        public bool TeamChangesLocked { get; set; }

        public void WriteData(IPacket packet)
        {
            List<TeamSettingsEntryData> entries = Entries ?? new List<TeamSettingsEntryData>();
            entries = entries
                .Where(entry => entry != null && IsPlayableTeam(entry.Team))
                .GroupBy(entry => entry.Team)
                .Select(group => group.Last())
                .OrderBy(entry => (byte)entry.Team)
                .Take(4)
                .ToList();

            packet.Write(ListProtocol.Version);
            packet.Write(TeamChangesLocked);
            packet.Write((byte)entries.Count);

            foreach (TeamSettingsEntryData entry in entries)
            {
                string username = entry.LeaderUsername ?? string.Empty;
                if (username.Length > ListProtocol.MaxUsernameLength)
                    username = username.Substring(0, ListProtocol.MaxUsernameLength);

                packet.Write((byte)entry.Team);
                packet.Write(entry.HasAssignedLeader);
                packet.Write(entry.HasConnectedLeader);
                packet.Write(entry.LeaderPlayerId);
                packet.Write(username);
                packet.Write(entry.HasTeamSkin);
                packet.Write(entry.TeamSkinId);
                packet.Write(entry.IsSkinLocked && entry.HasTeamSkin);
                packet.Write(entry.SkinUpdatedAtUtcTicks);
            }
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);
            TeamChangesLocked = packet.ReadBool();
            byte count = packet.ReadByte();
            if (count > 4)
                throw new InvalidOperationException("Team settings collection is too large.");

            List<TeamSettingsEntryData> entries = new List<TeamSettingsEntryData>(count);
            for (int i = 0; i < count; i++)
            {
                Team team = (Team)packet.ReadByte();
                if (!IsPlayableTeam(team))
                    throw new InvalidOperationException("Invalid team in team settings packet.");

                entries.Add(new TeamSettingsEntryData
                {
                    Team = team,
                    HasAssignedLeader = packet.ReadBool(),
                    HasConnectedLeader = packet.ReadBool(),
                    LeaderPlayerId = packet.ReadUShort(),
                    LeaderUsername = packet.ReadString(ListProtocol.MaxUsernameLength),
                    HasTeamSkin = packet.ReadBool(),
                    TeamSkinId = packet.ReadByte(),
                    IsSkinLocked = packet.ReadBool(),
                    SkinUpdatedAtUtcTicks = packet.ReadLong()
                });
            }

            Entries = entries
                .GroupBy(entry => entry.Team)
                .Select(group => group.Last())
                .OrderBy(entry => (byte)entry.Team)
                .ToList();
        }

        public static bool IsPlayableTeam(Team team)
        {
            return team == Team.Moss || team == Team.Hive ||
                   team == Team.Grimm || team == Team.Lifeblood;
        }
    }

    public sealed class ListPlayerView
    {
        public ListPlayerView(
            ushort id,
            string username,
            Team team,
            byte skinId,
            bool isHost = false,
            bool isAuthorized = false,
            bool isTeamLeader = false,
            bool hasTeamSkin = false,
            byte teamSkinId = 0,
            bool isTeamSkinLocked = false,
            bool hasListSkinOverride = false,
            byte listSkinOverrideId = 0,
            long teamSkinUpdatedAtUtcTicks = 0,
            long listSkinUpdatedAtUtcTicks = 0,
            string authorizationFingerprint = null)
        {
            Id = id;
            Username = username ?? string.Empty;
            Team = team;
            SkinId = skinId;
            IsHost = isHost;
            IsAuthorized = isAuthorized || isHost;
            IsTeamLeader = isTeamLeader;
            HasTeamSkin = hasTeamSkin;
            TeamSkinId = hasTeamSkin ? teamSkinId : (byte)0;
            IsTeamSkinLocked = isTeamSkinLocked && hasTeamSkin;
            HasListSkinOverride = hasListSkinOverride;
            ListSkinOverrideId = hasListSkinOverride ? listSkinOverrideId : (byte)0;
            TeamSkinUpdatedAtUtcTicks = teamSkinUpdatedAtUtcTicks;
            ListSkinUpdatedAtUtcTicks = listSkinUpdatedAtUtcTicks;
            AuthorizationFingerprint = authorizationFingerprint ?? string.Empty;
        }

        public ushort Id { get; }
        public string Username { get; }
        public Team Team { get; }
        public byte SkinId { get; }
        public bool IsHost { get; }
        public bool IsAuthorized { get; }
        public bool IsTeamLeader { get; }
        public bool HasTeamSkin { get; }
        public byte TeamSkinId { get; }
        public bool IsTeamSkinLocked { get; }
        public bool HasListSkinOverride { get; }
        public byte ListSkinOverrideId { get; }
        public long TeamSkinUpdatedAtUtcTicks { get; }
        public long ListSkinUpdatedAtUtcTicks { get; }
        public string AuthorizationFingerprint { get; }
    }

    public sealed class ListAdminPlayerView
    {
        public ListAdminPlayerView(
            ushort id,
            string username,
            bool isAuthorized,
            bool isAdminEnabled,
            string authorizationFingerprint,
            string authorizationKey,
            string authorizationNote,
            bool isHost,
            AdminPermissions permissions)
        {
            Id = id;
            Username = username ?? string.Empty;
            IsAuthorized = isAuthorized;
            IsAdminEnabled = isAdminEnabled;
            AuthorizationFingerprint =
                authorizationFingerprint ?? string.Empty;
            AuthorizationKey =
                authorizationKey ?? string.Empty;
            AuthorizationNote =
                authorizationNote ?? string.Empty;
            IsHost = isHost;
            Permissions = (permissions ?? AdminPermissions.NoAccess()).Clone();
        }

        public ushort Id { get; }
        public string Username { get; }
        public bool IsAuthorized { get; }
        public bool IsAdminEnabled { get; }
        public string AuthorizationFingerprint { get; }
        public string AuthorizationKey { get; }
        public string AuthorizationNote { get; }
        public bool IsHost { get; }
        public AdminPermissions Permissions { get; }
    }

    public sealed class AdminBanEntryView
    {
        public AdminBanEntryView(
            string authorizationFingerprint,
            string username,
            string reason)
        {
            AuthorizationFingerprint =
                authorizationFingerprint ?? string.Empty;
            Username =
                username ?? string.Empty;
            Reason =
                reason ?? string.Empty;
        }

        public string AuthorizationFingerprint { get; }
        public string Username { get; }
        public string Reason { get; }
    }

    public sealed class TeamAssignmentData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;

        public Team Team { get; set; } = Team.None;

        public void WriteData(IPacket packet)
        {
            if (Team != Team.None && !TeamSettingsData.IsPlayableTeam(Team))
            {
                throw new InvalidOperationException("Invalid team assignment.");
            }

            packet.Write(ListProtocol.Version);
            packet.Write((byte)Team);
        }

        public void ReadData(IPacket packet)
        {
            ListProtocol.EnsureVersion(packet);
            Team = (Team)packet.ReadByte();
            if (Team != Team.None && !TeamSettingsData.IsPlayableTeam(Team))
            {
                throw new InvalidOperationException("Invalid team assignment packet.");
            }
        }
    }

}
