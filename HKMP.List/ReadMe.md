# HKMP.List

An HKMP addon for displaying the player list and managing host settings.

## Features

### Player List
The `List` window synchronizes with the host in real time and displays currently connected players.
- **Player Info:** Each row displays the player's name, HKMP Player ID, team details, and current skin.
- **Sorting:** Sort the list by Player ID or by teams.
- **Personal Notes:** Linked to a permanent authorization identity, persisting across sessions regardless of temporary ID changes.
- **Moderation:** Authorized users can execute `Kick` and `Ban` directly from a player's row.
- **Management:** Team leaders and admins with group management permissions can kick players from both the host and the team.

### Skins
Configure skin display and player customization choices via the general settings tab.
- **Preview Modes:** Choose between `Side` or `Bench` view variants in the UI.
- **In-Game Skin Changes:** Change your own skin directly through the UI. Settings save automatically and reapply when rejoining the server or restarting the game.
- **Overriding Skins:** Force-assign a personal skin to any player by their ID. Removing the ID restores the player's original skin.

### Teams
Distribute players and control rosters via the dedicated `Teams` tab.
- **Leader Roles:** Assign a team leader or remove them from their role.
- **Roster Control:** Add a player to a team or remove them from their current team.
- **Team Skins:** Configure a skin ID for each individual team and lock it.
- **Lock Team Changes:** Restricts players from freely switching teams or leaving their current faction.

### Admin Panel
The `Admin` tab contains advanced host utilities and is completely hidden from regular players. 
- **Server Settings:** Toggle `PvP`, `Body Damage`, display names, teams, skins, and parries.
- **Map Icons:** Choose between `Always show map icons` or `Map icon with Wayward Compass only`.
- **Damage Adjustments:** Modify incoming damage values separately for all in-game damage sources.
- **Reset Configuration:** Reset all host configurations and damage modifications back to their default values.
- **Offline Editing:** Edit the server configuration while offline; parameters save for the next host session.
- **Admin Proposals:** Changes made by remote admins without full execution rights are sent to the host as a proposal for manual approval or rejection.
- **Ban Management:** View the ban list, execute `Unban` on a specific player, or clear the entire list using `Unban All`.
- **Host Protection:** Safeguards block any attempts to kick or ban the server host, as well as preventing admins from kicking themselves.

### Admin Permissions
The host can view saved admin identities, copy their authorization keys, add notes, and configure specific permission flags for each admin:
- Kick players
- Ban players
- Manage bans
- Modify server settings
- Apply host settings directly without sending proposals
- Manage identity data
- Manage teams and team leaders
- Manage permissions for other administrators

### Identity Bundles
Export and import saved identity records and associated notes directly to the following directory:
`...\Hollow Knight\hollow_knight_Data\Managed\Mods\HKMP.List\HKMP.List\IdentityBundles`

---

## Window Controls

- **Open/Close Keybind:** Default menu toggle is bound to `F7`. Can be reassigned in the `General` settings tab.
- **Window Layout:** Drag the header to freely move the window across the screen.
- **Resizing:** Drag any edge or corner of the interface to adjust the window size.
- **Layout Reset:** Reset the window's position and size back to defaults via the configuration option.
- **Reset List Settings to Defaults:** Erases only local UI configurations and visible skin overrides. Host-side data (notes, bans, admin records, team states) remains unaffected.

---

## Integrations

### HKMP.Rounds
Displays parameters for the `HKMP.Rounds` mod inside the dedicated `Rounds` tab if the mod is installed on the client.

`HKMP.List` is a proud part of the official RU **Hollow Knight PvP-Events Community**.  
Announcements, tournaments, custom PvP modes, and additional information can be found here:
* **[Events HK on Telegram]([https://t.me](https://t.me/Events_HK))**

> *A portion of this project's code was optimized and generated using AI. The final codebase is fully maintained, integrated, and thoroughly tested by the project author.*