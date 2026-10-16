using System;

namespace HKMP.List.Shared
{
    public static class SkinPolicyResolver
    {
        public static byte Resolve(
            byte fallbackSkinId,
            bool hasTeamSkin,
            byte teamSkinId,
            bool isTeamSkinLocked,
            long teamSkinUpdatedAtUtcTicks,
            bool hasPersonalPreference,
            byte personalSkinId,
            long personalUpdatedAtUtcTicks,
            bool hasViewerOverride,
            byte viewerSkinId,
            long viewerUpdatedAtUtcTicks,
            bool isTeamLeader)
        {
            if (hasTeamSkin && isTeamSkinLocked)
                return teamSkinId;

            bool teamApplies = hasTeamSkin && !isTeamLeader;

            long newestTicks = long.MinValue;
            byte result = fallbackSkinId;
            int newestPriority = -1;

            if (teamApplies)
            {
                newestTicks = Math.Max(0, teamSkinUpdatedAtUtcTicks);
                result = teamSkinId;
                newestPriority = 0;
            }

            if (hasPersonalPreference)
            {
                long ticks = Math.Max(0, personalUpdatedAtUtcTicks);
                if (ticks > newestTicks || (ticks == newestTicks && newestPriority <= 1))
                {
                    newestTicks = ticks;
                    result = personalSkinId;
                    newestPriority = 1;
                }
            }

            if (hasViewerOverride)
            {
                long ticks = Math.Max(0, viewerUpdatedAtUtcTicks);
                if (ticks > newestTicks || (ticks == newestTicks && newestPriority <= 2))
                {
                    result = viewerSkinId;
                    newestPriority = 2;
                }
            }

            return result;
        }
    }
}
