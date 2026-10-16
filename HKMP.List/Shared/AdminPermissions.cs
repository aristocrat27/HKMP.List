using Hkmp.Networking.Packet;

namespace HKMP.List.Shared
{
    public sealed class AdminPermissions
    {
        public bool CanKickPlayers { get; set; } = true;
        public bool CanBanPlayers { get; set; } = true;
        public bool CanManageBans { get; set; } = true;
        public bool CanChangeServerSettings { get; set; } = true;
        public bool CanChangeServerSettingsDirectly { get; set; } = false;
        public bool CanManageIdentityData { get; set; } = true;
        public bool CanManageTeamLeaders { get; set; }
        public bool CanManageAdministrators { get; set; }

        public static AdminPermissions FullAccess()
        {
            return new AdminPermissions
            {
                CanKickPlayers = true,
                CanBanPlayers = true,
                CanManageBans = true,
                CanChangeServerSettings = true,
                CanChangeServerSettingsDirectly = false,
                CanManageIdentityData = true,
                CanManageTeamLeaders = true,
                CanManageAdministrators = false
            };
        }

        public static AdminPermissions NoAccess()
        {
            return new AdminPermissions
            {
                CanKickPlayers = false,
                CanBanPlayers = false,
                CanManageBans = false,
                CanChangeServerSettings = false,
                CanChangeServerSettingsDirectly = false,
                CanManageIdentityData = false,
                CanManageTeamLeaders = false,
                CanManageAdministrators = false
            };
        }

        public AdminPermissions Clone()
        {
            return new AdminPermissions
            {
                CanKickPlayers = CanKickPlayers,
                CanBanPlayers = CanBanPlayers,
                CanManageBans = CanManageBans,
                CanChangeServerSettings = CanChangeServerSettings,
                CanChangeServerSettingsDirectly = CanChangeServerSettingsDirectly,
                CanManageIdentityData = CanManageIdentityData,
                CanManageTeamLeaders = CanManageTeamLeaders,
                CanManageAdministrators = CanManageAdministrators
            };
        }

        internal void WriteTo(IPacket packet)
        {
            packet.Write(CanKickPlayers);
            packet.Write(CanBanPlayers);
            packet.Write(CanManageBans);
            packet.Write(CanChangeServerSettings);
            packet.Write(CanChangeServerSettingsDirectly);
            packet.Write(CanManageIdentityData);
            packet.Write(CanManageTeamLeaders);
            packet.Write(CanManageAdministrators);
        }

        internal static AdminPermissions ReadFrom(IPacket packet)
        {
            return new AdminPermissions
            {
                CanKickPlayers = packet.ReadBool(),
                CanBanPlayers = packet.ReadBool(),
                CanManageBans = packet.ReadBool(),
                CanChangeServerSettings = packet.ReadBool(),
                CanChangeServerSettingsDirectly = packet.ReadBool(),
                CanManageIdentityData = packet.ReadBool(),
                CanManageTeamLeaders = packet.ReadBool(),
                CanManageAdministrators = packet.ReadBool()
            };
        }
    }
}
