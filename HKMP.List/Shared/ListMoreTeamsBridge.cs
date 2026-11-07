using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hkmp.Game;
using UnityEngine;

namespace HKMP.List.Shared
{
    public static class ListMoreTeamsBridge
    {
        private const int FirstAdditionalTeamId = 5;
        private const int FirstCustomTeamId = 13;
        private const int LastCustomTeamId = 68;
        private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        public static bool IsAvailable
        {
            get { return FindMoreTeamsBridge() != null; }
        }

        public static bool HasListIntegration
        {
            get { return ReadBooleanProperty("HasListIntegration"); }
        }

        public static bool CanManageTeams
        {
            get { return ReadBooleanProperty("CanManageTeams"); }
        }

        public static bool AllowPlayerCreation
        {
            get { return ReadBooleanProperty("AllowPlayerCreation"); }
        }

        public static bool CreatorsBecomeLeaders
        {
            get { return ReadBooleanProperty("CreatorsBecomeLeaders"); }
        }

        public static bool IsPlayableTeam(int teamId)
        {
            if (teamId >= 1 && teamId <= 4)
            {
                return true;
            }

            if (teamId < FirstAdditionalTeamId || teamId > LastCustomTeamId)
            {
                return false;
            }

            MethodInfo method = FindMethod("IsPlayableTeam", typeof(int));
            if (method == null)
            {
                return false;
            }

            try
            {
                return Convert.ToBoolean(method.Invoke(null, new object[] { teamId }));
            }
            catch
            {
                return false;
            }
        }

        public static bool IsCustomTeam(int teamId)
        {
            return teamId >= FirstCustomTeamId &&
                   teamId <= LastCustomTeamId &&
                   IsPlayableTeam(teamId);
        }

        public static int[] GetPlayableTeamIds()
        {
            SortedSet<int> ids = new SortedSet<int> { 1, 2, 3, 4 };
            MethodInfo method = FindMethod("GetPlayableTeamIds", Type.EmptyTypes);
            if (method == null)
            {
                return ids.ToArray();
            }

            try
            {
                IEnumerable<int> additionalIds = method.Invoke(null, null) as IEnumerable<int>;
                if (additionalIds != null)
                {
                    foreach (int teamId in additionalIds)
                    {
                        if (teamId >= FirstAdditionalTeamId &&
                            teamId <= LastCustomTeamId &&
                            IsPlayableTeam(teamId))
                        {
                            ids.Add(teamId);
                        }
                    }
                }
            }
            catch
            {
            }

            return ids.ToArray();
        }

        public static Team[] GetPlayableTeams()
        {
            return GetPlayableTeamIds()
                .Select(teamId => (Team)teamId)
                .ToArray();
        }

        public static string GetTeamName(Team team)
        {
            int teamId = (int)team;
            MethodInfo method = FindMethod("GetTeamName", typeof(int));
            if (method != null)
            {
                try
                {
                    string result = method.Invoke(null, new object[] { teamId }) as string;
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        return result;
                    }
                }
                catch
                {
                }
            }

            switch (teamId)
            {
                case 1: return "MOSS";
                case 2: return "HIVE";
                case 3: return "GRIMM";
                case 4: return "LIFEBLOOD";
                default: return teamId.ToString();
            }
        }

        public static Color GetTeamColor(Team team, Color fallback)
        {
            MethodInfo method = FindMethod("GetTeamTextColor", typeof(int));
            if (method == null)
            {
                return fallback;
            }

            try
            {
                string value = method.Invoke(null, new object[] { (int)team }) as string;
                Color color;
                return !string.IsNullOrWhiteSpace(value) &&
                       ColorUtility.TryParseHtmlString(value, out color)
                    ? color
                    : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        public static void DeleteTeam(byte teamId)
        {
            InvokeVoid("DeleteTeam", new[] { typeof(byte) }, new object[] { teamId });
        }

        public static void SetPlayerCreationEnabled(bool enabled)
        {
            InvokeVoid("SetPlayerCreationEnabled", new[] { typeof(bool) }, new object[] { enabled });
        }

        public static void SetCreatorsBecomeLeaders(bool enabled)
        {
            InvokeVoid("SetCreatorsBecomeLeaders", new[] { typeof(bool) }, new object[] { enabled });
        }

        private static bool ReadBooleanProperty(string name)
        {
            Type bridge = FindMoreTeamsBridge();
            PropertyInfo property = bridge == null ? null : bridge.GetProperty(name, StaticFlags);
            if (property == null)
            {
                return false;
            }

            try
            {
                return Convert.ToBoolean(property.GetValue(null, null));
            }
            catch
            {
                return false;
            }
        }

        private static void InvokeVoid(string name, Type[] parameterTypes, object[] arguments)
        {
            MethodInfo method = FindMethod(name, parameterTypes);
            if (method == null)
            {
                return;
            }

            try
            {
                method.Invoke(null, arguments);
            }
            catch (Exception exception)
            {
                Modding.Logger.LogWarn("HKMP.List could not call HKMP.MoreTeams." + name + ": " + exception.GetBaseException());
            }
        }

        private static MethodInfo FindMethod(string name, params Type[] parameterTypes)
        {
            Type bridge = FindMoreTeamsBridge();
            return bridge == null
                ? null
                : bridge.GetMethod(name, StaticFlags, null, parameterTypes, null);
        }

        private static Type FindMoreTeamsBridge()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type bridge = assembly.GetType("HKMP.MoreTeams.MoreTeamsBridge", false);
                if (bridge != null)
                {
                    return bridge;
                }
            }

            return null;
        }
    }
}
