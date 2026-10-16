using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game;
using HKMP.List.Client;
using HKMP.List.Shared;
using UnityEngine;
using UnityEngine.UI;

namespace HKMP.List.UI
{
    internal sealed class ListTeamSettingsPanel
    {
        private static readonly Team[] Teams =
        {
            Team.Moss,
            Team.Hive,
            Team.Grimm,
            Team.Lifeblood
        };

        private readonly ListClientAddon _addon;
        private readonly object _gate = new object();
        private readonly HashSet<Team> _expandedLeaderTeams = new HashSet<Team>();

        private RectTransform _root;
        private RectTransform _content;
        private ScrollRect _scroll;
        private bool _refreshPending;
        private bool _subscribed;

        public ListTeamSettingsPanel(ListClientAddon addon)
        {
            _addon = addon;
        }

        public bool IsVisible
        {
            get { return _root != null && _root.gameObject.activeInHierarchy; }
        }

        public void Show(Transform parent)
        {
            EnsureCreated(parent);
            if (_root == null)
            {
                return;
            }

            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            Subscribe();
            Refresh();
        }

        public void Hide()
        {
            if (_root != null)
            {
                _root.gameObject.SetActive(false);
            }

            Unsubscribe();
        }

        public void PumpPendingUpdates()
        {
            bool refresh;
            lock (_gate)
            {
                refresh = _refreshPending;
                _refreshPending = false;
            }

            if (refresh && IsVisible)
            {
                if (HasFocusedInput())
                {
                    lock (_gate)
                    {
                        _refreshPending = true;
                    }
                    return;
                }

                Refresh();
            }
        }

        public void Refresh()
        {
            if (_content == null || _addon == null || _addon.State == null)
            {
                return;
            }

            ClearContent();

            ListClientState state = _addon.State;
            bool connected = _addon.IsConnected;
            ServerSettingsData serverSettings = state.GetServerSettings();
            bool teamsEnabled = serverSettings != null && serverSettings.TeamsEnabled;
            bool skinsEnabled = teamsEnabled && serverSettings.AllowSkins;
            AdminPermissions permissions = state.Permissions;
            bool canManageAllLeaders = state.IsHost ||
                (state.IsAuthorized && permissions != null && permissions.CanManageTeamLeaders);
            IReadOnlyList<ListPlayerView> players = state.GetPlayers();
            IReadOnlyList<TeamSettingsEntryData> snapshots = state.GetTeamSettings();

            AddInfoRow(
                !connected
                    ? "Connect to an HKMP host to see live team data."
                    : !teamsEnabled
                        ? "Teams are disabled by the host. Current team and skin status remain visible."
                        : "Team membership can be locked by the host or an administrator with team-management permission.",
                42f);

            CreateTeamChangesLockControl(state, connected, canManageAllLeaders);

            foreach (Team team in Teams)
            {
                TeamSettingsEntryData entry = snapshots.FirstOrDefault(
                    item => item != null && item.Team == team);

                if (entry == null)
                {
                    entry = new TeamSettingsEntryData { Team = team };
                }

                List<ListPlayerView> teamPlayers = (players ?? new List<ListPlayerView>())
                    .Where(player => player != null && player.Team == team)
                    .OrderBy(player => player.Username, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(player => player.Id)
                    .ToList();

                bool localLeaderCanManageTeam = state.IsLocalTeamLeader && state.LocalTeam == team;
                bool canManageLeaders = connected && teamsEnabled &&
                    (canManageAllLeaders || localLeaderCanManageTeam);
                bool canManageAppearance = connected && skinsEnabled &&
                    (state.IsHost ||
                     (state.IsAuthorized && permissions != null && permissions.CanManageTeamLeaders) ||
                     localLeaderCanManageTeam);

                CreateTeamCard(
                    entry,
                    teamPlayers,
                    canManageAppearance,
                    canManageLeaders);
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            if (_scroll != null)
            {
                _scroll.verticalNormalizedPosition = 1f;
            }
        }

        private void EnsureCreated(Transform parent)
        {
            if (_root != null || parent == null)
            {
                return;
            }

            GameObject rootObject = ListUiFactory.CreateObject(parent, "TeamSettingsPanel");
            _root = rootObject.GetComponent<RectTransform>();
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            _root.offsetMin = new Vector2(8f, 8f);
            _root.offsetMax = new Vector2(-8f, -48f);

            Image background = rootObject.AddComponent<Image>();
            background.sprite = ListUiFactory.WhiteSprite;
            background.color = new Color(0.045f, 0.045f, 0.058f, 1f);
            background.raycastTarget = true;

            Text heading = ListUiFactory.CreateText(
                _root, "TeamsHeading", "TEAMS", 21,
                new Color(0.83f, 0.83f, 0.90f, 1f));
            RectTransform headingRect = heading.rectTransform;
            headingRect.anchorMin = new Vector2(0f, 1f);
            headingRect.anchorMax = new Vector2(0f, 1f);
            headingRect.pivot = new Vector2(0f, 1f);
            headingRect.sizeDelta = new Vector2(170f, 32f);
            headingRect.anchoredPosition = new Vector2(10f, -4f);

            GameObject scrollObject = ListUiFactory.CreateObject(_root, "TeamsScroll");
            RectTransform viewport = scrollObject.GetComponent<RectTransform>();
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(2f, 4f);
            viewport.offsetMax = new Vector2(-2f, -38f);

            Image viewportImage = scrollObject.AddComponent<Image>();
            viewportImage.sprite = ListUiFactory.WhiteSprite;
            viewportImage.color = new Color(0f, 0f, 0f, 0f);
            viewportImage.raycastTarget = true;
            scrollObject.AddComponent<RectMask2D>();

            _scroll = scrollObject.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 28f;
            _scroll.viewport = viewport;

            GameObject contentObject = ListUiFactory.CreateObject(viewport, "TeamsContent");
            _content = contentObject.GetComponent<RectTransform>();
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = Vector2.zero;

            VerticalLayoutGroup layout = contentObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.spacing = 7f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            ContentSizeFitter fitter = contentObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scroll.content = _content;
            _root.gameObject.SetActive(false);
        }

        private void CreateTeamChangesLockControl(
            ListClientState state,
            bool connected,
            bool canManageAllTeams)
        {
            bool locked = state.TeamChangesLocked;
            bool canToggle = connected && canManageAllTeams &&
                _addon != null && _addon.IsConnected;

            GameObject row = ListUiFactory.CreateObject(_content, "TeamChangesLockRow");
            ListUiFactory.AddLayoutElement(row, 0f, 34f);

            HorizontalLayoutGroup rowLayout = row.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 8f;
            rowLayout.padding = new RectOffset(5, 5, 2, 2);
            rowLayout.childAlignment = TextAnchor.MiddleLeft;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = true;

            Text label = ListUiFactory.CreateText(
                row.transform,
                "TeamChangesLockLabel",
                "Lock team changes",
                13,
                locked
                    ? new Color(1f, 0.78f, 0.38f, 1f)
                    : Color.white);
            LayoutElement labelLayout = label.gameObject.AddComponent<LayoutElement>();
            labelLayout.flexibleWidth = 1f;
            labelLayout.minWidth = 190f;

            GameObject stateObject = ListUiFactory.CreateObject(
                row.transform,
                "TeamChangesLockState");
            ListUiFactory.AddLayoutElement(stateObject, 70f, 26f);

            Image background = ListUiFactory.CreateImage(
                stateObject.transform,
                "Background",
                locked
                    ? new Color(0.20f, 0.56f, 0.30f, 1f)
                    : new Color(0.16f, 0.16f, 0.19f, 1f),
                canToggle);
            RectTransform backgroundRect = background.rectTransform;
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;

            Text stateText = ListUiFactory.CreateText(
                stateObject.transform,
                "State",
                locked ? "ON" : "OFF",
                12,
                Color.white,
                TextAnchor.MiddleCenter);
            RectTransform stateTextRect = stateText.rectTransform;
            stateTextRect.anchorMin = Vector2.zero;
            stateTextRect.anchorMax = Vector2.one;
            stateTextRect.offsetMin = Vector2.zero;
            stateTextRect.offsetMax = Vector2.zero;

            if (!canToggle)
            {
                return;
            }

            Button button = stateObject.AddComponent<Button>();
            button.targetGraphic = background;

            ColorBlock colors = button.colors;
            colors.normalColor = background.color;
            colors.highlightedColor = new Color(0.24f, 0.62f, 0.34f, 1f);
            colors.pressedColor = new Color(0.12f, 0.40f, 0.22f, 1f);
            colors.selectedColor = colors.normalColor;
            colors.disabledColor = new Color(0.10f, 0.10f, 0.12f, 0.65f);
            colors.colorMultiplier = 1f;
            button.colors = colors;

            button.onClick.AddListener(
                () =>
                {
                    if (_addon != null && _addon.IsConnected)
                    {
                        _addon.SetTeamChangesLocked(!locked);
                    }
                });
        }

        private void CreateTeamCard(
            TeamSettingsEntryData entry,
            List<ListPlayerView> teamPlayers,
            bool canManageAppearance,
            bool canManageLeaders)
        {
            Team team = entry.Team;
            if (teamPlayers.Count == 0)
            {
                _expandedLeaderTeams.Remove(team);
            }

            GameObject card = ListUiFactory.CreateObject(_content, "Team_" + team);
            Image image = card.AddComponent<Image>();
            image.sprite = ListUiFactory.WhiteSprite;
            image.color = new Color(0.075f, 0.075f, 0.09f, 0.98f);
            image.raycastTarget = false;

            VerticalLayoutGroup layout = card.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(9, 9, 7, 7);
            layout.spacing = 3f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            float cardHeight = canManageLeaders ? 95f : 85f;
            if (canManageLeaders &&
                _expandedLeaderTeams.Contains(team) &&
                teamPlayers.Count > 0)
            {
                cardHeight += teamPlayers.Count * 31f + 4f;
            }
            if (canManageAppearance)
            {
                cardHeight += 32f;
            }
            ListUiFactory.AddLayoutElement(card, 0f, cardHeight);

            GameObject headingRow = ListUiFactory.CreateObject(card.transform, "TeamHeadingRow");
            ListUiFactory.AddLayoutElement(headingRow, 0f, 25f);
            HorizontalLayoutGroup headingLayout = headingRow.AddComponent<HorizontalLayoutGroup>();
            headingLayout.spacing = 8f;
            headingLayout.childAlignment = TextAnchor.MiddleLeft;
            headingLayout.childControlWidth = true;
            headingLayout.childControlHeight = true;
            headingLayout.childForceExpandWidth = false;
            headingLayout.childForceExpandHeight = true;

            Text teamTitle = ListUiFactory.CreateText(
                headingRow.transform, "TeamTitle", TeamName(team) + " TEAM", 16, TeamColor(team));
            LayoutElement titleLayout = teamTitle.gameObject.AddComponent<LayoutElement>();
            titleLayout.flexibleWidth = 1f;
            titleLayout.preferredHeight = 24f;

            Text count = ListUiFactory.CreateText(
                headingRow.transform,
                "PlayerCount",
                teamPlayers.Count + (teamPlayers.Count == 1 ? " player" : " players"),
                12,
                new Color(0.85f, 0.85f, 0.89f, 1f),
                TextAnchor.MiddleRight);
            ListUiFactory.AddLayoutElement(count.gameObject, 0f, 22f);

            string leader = !entry.HasAssignedLeader
                ? "Leader: not assigned"
                : entry.HasConnectedLeader
                    ? "Leader: " + SafeName(entry.LeaderUsername) + "  (ID " + entry.LeaderPlayerId + ")"
                    : "Leader: " + SafeName(entry.LeaderUsername) + "  (offline)";

            if (canManageLeaders)
            {
                CreateLeaderControls(card.transform, entry, teamPlayers, leader);
            }
            else
            {
                AddCardText(card.transform, leader, 12, Color.white, 19f);
            }

            string skinStatus = entry.HasTeamSkin
                ? "Team skin: " + entry.TeamSkinId + (entry.IsSkinLocked ? "  ·  LOCKED" : "  ·  unlocked")
                : "Team skin: not configured";
            AddCardText(
                card.transform,
                skinStatus,
                12,
                entry.IsSkinLocked
                    ? new Color(1f, 0.78f, 0.38f, 1f)
                    : new Color(0.75f, 0.75f, 0.80f, 1f),
                18f);

            if (canManageAppearance)
            {
                CreateAppearanceControls(card.transform, entry);
            }
            else if (canManageLeaders && teamPlayers.Count == 0)
            {
                AddCardText(card.transform, "No connected players are on this team.", 11,
                    new Color(0.62f, 0.62f, 0.67f, 1f), 17f);
            }
        }

        private void CreateLeaderControls(
            Transform parent,
            TeamSettingsEntryData entry,
            List<ListPlayerView> candidates,
            string leaderText)
        {
            GameObject row = ListUiFactory.CreateObject(parent, "LeaderControlRow");
            ListUiFactory.AddLayoutElement(row, 0f, 29f);
            HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 5f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = true;
            layout.childForceExpandWidth = false;

            Text label = ListUiFactory.CreateText(
                row.transform,
                "LeaderSummary",
                leaderText,
                12,
                Color.white,
                TextAnchor.MiddleLeft);
            LayoutElement labelLayout = label.gameObject.AddComponent<LayoutElement>();
            labelLayout.flexibleWidth = 1f;
            labelLayout.minWidth = 120f;

            bool expanded = _expandedLeaderTeams.Contains(entry.Team);
            if (candidates.Count > 0)
            {
                Button choose = ListUiFactory.CreateButton(
                    row.transform,
                    "ToggleLeaderCandidates",
                    expanded ? "▲" : "▼",
                    delegate
                    {
                        if (!_expandedLeaderTeams.Add(entry.Team))
                        {
                            _expandedLeaderTeams.Remove(entry.Team);
                        }

                        RequestRefresh();
                    },
                    12);
                ListUiFactory.AddLayoutElement(choose.gameObject, 32f, 27f);
                LayoutElement chooseLayout = choose.GetComponent<LayoutElement>();
                if (chooseLayout != null)
                {
                    chooseLayout.minWidth = 32f;
                    chooseLayout.preferredWidth = 32f;
                }
            }

            if (entry.HasAssignedLeader)
            {
                Button remove = ListUiFactory.CreateButton(
                    row.transform,
                    "RemoveLeader",
                    "Remove",
                    delegate
                    {
                        _addon.RemoveTeamLeader(entry.Team);
                        _expandedLeaderTeams.Remove(entry.Team);
                        RequestRefresh();
                    },
                    10);
                ListUiFactory.AddLayoutElement(remove.gameObject, 72f, 27f);
                LayoutElement removeLayout = remove.GetComponent<LayoutElement>();
                if (removeLayout != null)
                {
                    removeLayout.minWidth = 72f;
                    removeLayout.preferredWidth = 72f;
                }
            }

            if (!expanded)
            {
                return;
            }

            if (candidates.Count == 0)
            {
                AddCardText(parent, "No connected players can be appointed.", 11,
                    new Color(0.70f, 0.70f, 0.74f, 1f), 18f);
                return;
            }

            GameObject list = ListUiFactory.CreateObject(parent, "LeaderCandidates");
            ListUiFactory.AddLayoutElement(list, 0f, candidates.Count * 31f + 4f);
            VerticalLayoutGroup candidatesLayout = list.AddComponent<VerticalLayoutGroup>();
            candidatesLayout.spacing = 3f;
            candidatesLayout.padding = new RectOffset(4, 4, 2, 2);
            candidatesLayout.childControlHeight = true;
            candidatesLayout.childControlWidth = true;
            candidatesLayout.childForceExpandHeight = false;
            candidatesLayout.childForceExpandWidth = true;

            foreach (ListPlayerView candidate in candidates)
            {
                ListPlayerView captured = candidate;
                bool current = entry.HasConnectedLeader && entry.LeaderPlayerId == captured.Id;
                Button candidateButton = ListUiFactory.CreateButton(
                    list.transform,
                    "LeaderCandidate_" + captured.Id,
                    (current ? "✓  " : "    ") + SafeName(captured.Username) + "  ·  ID " + captured.Id,
                    delegate
                    {
                        _addon.AssignTeamLeader(captured.Id);
                        _expandedLeaderTeams.Remove(entry.Team);
                        RequestRefresh();
                    },
                    11);
                ListUiFactory.AddLayoutElement(candidateButton.gameObject, 0f, 28f);
                candidateButton.interactable = !current;
            }
        }

        private void CreateAppearanceControls(
            Transform parent,
            TeamSettingsEntryData entry)
        {
            GameObject controls = ListUiFactory.CreateObject(parent, "AppearanceControls");
            ListUiFactory.AddLayoutElement(controls, 0f, 31f);
            HorizontalLayoutGroup layout = controls.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = true;
            layout.childForceExpandWidth = false;

            InputField skinInput = ListUiFactory.CreateInputField(
                controls.transform,
                "TeamSkinId",
                entry.HasTeamSkin ? entry.TeamSkinId.ToString() : string.Empty,
                "Skin ID",
                13,
                null);
            skinInput.characterLimit = 3;
            skinInput.contentType = InputField.ContentType.IntegerNumber;
            ListUiFactory.AddLayoutElement(skinInput.gameObject, 62f, 29f);

            Button apply = ListUiFactory.CreateButton(
                controls.transform,
                "ApplyTeamSkin",
                "Apply skin",
                delegate
                {
                    byte skinId;
                    if (byte.TryParse(skinInput.text == null ? string.Empty : skinInput.text.Trim(), out skinId))
                    {
                        _addon.SetTeamSkin(entry.Team, skinId);
                    }
                    else
                    {
                        Debug.LogWarning("HKMP.List: enter a skin ID from 0 to 255.");
                    }
                },
                12);
            ListUiFactory.AddLayoutElement(apply.gameObject, 112f, 29f);

            Button lockButton = ListUiFactory.CreateButton(
                controls.transform,
                "ToggleTeamSkinLock",
                entry.IsSkinLocked ? "Unlock skin" : "Lock skin",
                delegate
                {
                    _addon.SetTeamSkinLocked(entry.Team, !entry.IsSkinLocked);
                },
                12);
            ListUiFactory.AddLayoutElement(lockButton.gameObject, 100f, 29f);
            lockButton.interactable = entry.HasTeamSkin || entry.IsSkinLocked;
        }

        private void AddInfoRow(string value, float height)
        {
            GameObject box = ListUiFactory.CreateObject(_content, "TeamInfo");
            ListUiFactory.AddLayoutElement(box, 0f, height);
            Image image = box.AddComponent<Image>();
            image.sprite = ListUiFactory.WhiteSprite;
            image.color = new Color(0.08f, 0.08f, 0.10f, 0.96f);

            Text text = ListUiFactory.CreateText(
                box.transform,
                "Info",
                value,
                12,
                new Color(0.76f, 0.76f, 0.82f, 1f));
            RectTransform rect = text.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(8f, 5f);
            rect.offsetMax = new Vector2(-8f, -5f);
        }

        private static void AddCardText(
            Transform parent,
            string value,
            int fontSize,
            Color color,
            float height)
        {
            GameObject row = ListUiFactory.CreateObject(parent, "TextRow");
            ListUiFactory.AddLayoutElement(row, 0f, height);
            Text text = ListUiFactory.CreateText(
                row.transform,
                "Label",
                value,
                fontSize,
                color,
                TextAnchor.MiddleLeft);
            RectTransform rect = text.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private bool HasFocusedInput()
        {
            if (_content == null)
            {
                return false;
            }

            InputField[] fields = _content.GetComponentsInChildren<InputField>(true);
            foreach (InputField field in fields)
            {
                if (field != null && field.isFocused)
                {
                    return true;
                }
            }

            return false;
        }

        private void ClearContent()
        {
            if (_content == null)
            {
                return;
            }

            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                Transform child = _content.GetChild(i);
                if (child != null)
                {
                    child.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
        }

        private void Subscribe()
        {
            if (_subscribed || _addon == null || _addon.State == null)
            {
                return;
            }

            _addon.State.TeamSettingsChanged += OnTeamSettingsChanged;
            _addon.State.PlayersChanged += OnPlayersChanged;
            _addon.State.ServerSettingsChanged += OnServerSettingsChanged;
            _addon.State.AuthorizationChanged += OnAuthorizationChanged;
            _addon.State.LocalPlayerIdentityChanged += OnLocalPlayerIdentityChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed || _addon == null || _addon.State == null)
            {
                return;
            }

            _addon.State.TeamSettingsChanged -= OnTeamSettingsChanged;
            _addon.State.PlayersChanged -= OnPlayersChanged;
            _addon.State.ServerSettingsChanged -= OnServerSettingsChanged;
            _addon.State.AuthorizationChanged -= OnAuthorizationChanged;
            _addon.State.LocalPlayerIdentityChanged -= OnLocalPlayerIdentityChanged;
            _subscribed = false;
        }

        private void OnTeamSettingsChanged(IReadOnlyList<TeamSettingsEntryData> entries) { RequestRefresh(); }
        private void OnPlayersChanged(IReadOnlyList<ListPlayerView> players) { RequestRefresh(); }
        private void OnServerSettingsChanged(ServerSettingsData settings) { RequestRefresh(); }
        private void OnAuthorizationChanged(bool isAuthorized) { RequestRefresh(); }
        private void OnLocalPlayerIdentityChanged() { RequestRefresh(); }

        private void RequestRefresh()
        {
            lock (_gate)
            {
                _refreshPending = true;
            }
        }

        private static string SafeName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
        }

        private static string TeamName(Team team)
        {
            switch (team)
            {
                case Team.Moss: return "MOSS";
                case Team.Hive: return "HIVE";
                case Team.Grimm: return "GRIMM";
                case Team.Lifeblood: return "LIFEBLOOD";
                default: return "NO TEAM";
            }
        }

        private static Color TeamColor(Team team)
        {
            switch (team)
            {
                case Team.Moss: return new Color(0.48f, 0.82f, 0.50f, 1f);
                case Team.Hive: return new Color(1f, 0.62f, 0.27f, 1f);
                case Team.Grimm: return new Color(0.94f, 0.38f, 0.42f, 1f);
                case Team.Lifeblood: return new Color(0.45f, 0.72f, 1f, 1f);
                default: return Color.white;
            }
        }
    }
}
