# BJORNHJARTA – ENSHROUDED TOOLBOX
## Complete Feature Overview & Product Vision

**Purpose of this document:** One overview of the intended finished application, followed by the full detailed feature specification. The overview is a **product vision**, not a declaration that all features are currently implemented. Individual modules have different development and verification statuses.

## At a Glance — Main Features

Bjornhjarta is an all-in-one Windows companion toolkit for **Enshrouded**, combining building design, mod administration, exploration, character planning, dedicated-server operations and community sharing in a single application.

| Main feature | What it delivers |
|---|---|
| **Main Dashboard** | One central control panel for tools, game/server status, updates, notifications and recent projects. |
| **Blueprint Forge** | Create and edit building blueprints in **2D and 3D**, place and transform pieces, import reference images, manage models/materials and share finished designs. |
| **EML Mod Manager** | Manage **Enshrouded Mod Loader (EML)** for both the game client and dedicated server, including mods, configurations, versions, profiles and backups. |
| **Creative Tools** | Advanced selection, area-based building operations and safety-first demolition workflows where supported by game/mod APIs. |
| **Item Vault** | Searchable reference library for items, resources, equipment, furniture, building materials and internal Type IDs. |
| **World Explorer** | Interactive map, points of interest, resources, custom markers, distances and route planning. |
| **Loadouts** | Save and compare equipment, skills, character builds and playstyle-specific presets. |
| **Dedicated Server Manager** | Configure, monitor and maintain dedicated servers, including restarts, logs, updates, backups and player administration where supported. |
| **Community Hub** | Discover, organize, rate and share blueprints, guides, collections, mods and community resources. |
| **Application-Wide Foundation** | Unified Norse-inspired user interface, data validation, import/export, diagnostics, backups and protection for game data. |

### Key Product Capabilities

- **Build:** Design structures with a top-down **2D Editor** and a texture-capable **3D Editor**, using grid snapping, direct manipulation, multi-selection and grouped operations.
- **Reconstruct:** Import a 2D plan or 3D reference image and generate an **editable suggested blueprint**, with explicit scale assumptions, a preview and user approval before insertion.
- **Place:** Browse building and object categories—foundations, walls, roofs, stairs, doors, storage, magic chests, furniture, lighting, crafting stations and decorations—and place supported pieces in a design.
- **Visualize:** Use local GLB/glTF assets and, where legally and technically available, the user's own Enshrouded model and material data for increasingly authentic 3D representation.
- **Mod:** Manage EML separately for **Client** and **Dedicated Server**, with manifest checks, configuration management, mod installation and compatibility workflows.
- **Explore:** Search game item information and world locations to assist building, crafting, traveling and planning.
- **Administer:** Perform routine server configuration, maintenance, monitoring and recovery from one desktop interface.
- **Share:** Organize and distribute blueprints, builds, collections and community knowledge.
- **Protect:** Use validation, dry runs/previews, explicit confirmation and backups before changing game or server files.

### Development and Accuracy Notes

- The detailed sections below describe the **intended finished product**. Where the original specification says *Planned* or *Future Development*, those features should not be treated as delivered.
- As of the latest conversation, Blueprint Forge **v0.5.0 was confirmed working by the user**. The project has since reached a **v0.12.0 development preview**, but that preview has not been confirmed to pass a Windows build and full functional testing.
- **Image-to-Blueprint** is currently experimental. A single image may not determine accurate depth, hidden geometry, material types or dimensions; these require user choices, verified game data and sometimes additional reference views.
- **Original Enshrouded graphics** should be loaded from user-owned local game assets or user-provided converted models; they are not bundled or redistributed with Bjornhjarta.
- In-game modifications, destructive actions and administrative operations depend on confirmed Enshrouded/EML capabilities and appropriate permissions.

---

**Inspiration credit:** Bjornhjarta Toolbox is inspired by *Embervale Toolkit*, created by @grannymaster69. 

**Target release date (as supplied):** 30 November 2026, 19:00 UTC+01:00

**Project:** Bjornhjarta – Enshrouded ToolBox  

**Platform:** Windows 10/11  

**Application Type:** Desktop Companion & Game Administration Toolkit  

**Target Game:** Enshrouded  

**Development Status:** Active Development  

**Last user-confirmed working milestone:** Blueprint Forge v0.5.0  
**Most recent development preview:** v0.12.0 (Windows build/runtime verification still required)

---

# 1. About Bjornhjarta – Enshrouded ToolBox

Bjornhjarta – Enshrouded ToolBox is an ambitious all-in-one desktop companion designed to enhance the Enshrouded experience for builders, explorers, server administrators, modders, and community members.

The goal is to create a comprehensive collection of powerful tools that make building, designing, managing, exploring, and administering Enshrouded easier and more efficient.

Rather than requiring multiple separate applications, Bjornhjarta aims to bring everything together under one unified interface.

The application will combine advanced blueprint creation, building utilities, item databases, world exploration, character loadout management, mod management, dedicated server administration, and community features.

The long-term vision is to provide a complete ecosystem for the Enshrouded community.

# 2. Main Dashboard

The Dashboard will serve as the central hub for the entire application.

**Main features:** Unified navigation · Status and notifications · Recent work · Tool/settings access · Updates.

### Planned Features
- Centralized navigation to all available modules.
- Overview of installed and available modules.
- Game installation detection.
- Game version detection.
- Mod framework and compatibility status.
- Dedicated server status overview.
- Quick access to recently opened blueprints.
- Recently used tools and features.
- Notifications for available updates.
- Application settings and configuration.
- Update management for Bjornhjarta.
- System and application status information.

# 3. Blueprint Forge

**Purpose:** An advanced blueprint creation and editing environment designed specifically for Enshrouded building projects.

Blueprint Forge will allow players to design, modify, organize, and manage complex building structures without having to construct everything manually inside the game.

**Main features:** 2D/3D editors · Blueprint library · Placeables catalog · Group/drag editing · Image-to-Blueprint · Authentic local model assets · Dimension and grid tools.

### 3.1 Blueprint Management
- Create new blueprints.
- Open existing blueprints.
- Save and save as.
- Import and export blueprint files.
- Validate blueprint structure and data.
- Edit blueprint names and descriptions.
- Manage blueprint metadata.
- Track blueprint dimensions.
- Organize blueprints into categories.
- Maintain a personal blueprint library.
- Duplicate existing blueprints.

### 3.2 Building Piece Management
- Add and remove building pieces.
- Edit building piece Type IDs.
- Modify individual element positions.
- Edit rotation and orientation.
- Change available building variants.
- View individual element properties.
- Search and filter building elements.
- Support different building piece categories.
- Maintain unique identifiers for individual elements.

### 3.3 Advanced Selection
- Select individual building pieces.
- Select multiple building pieces simultaneously.
- Rectangular mouse selection.
- Add or remove pieces from an existing selection.
- Select all building pieces.
- Clear the current selection.
- Visual highlighting of selected pieces.
- Multi-selection through the element list.
- Multi-selection directly through the blueprint grid.

### 3.4 Group Editing
- Move multiple selected pieces together.
- Preserve relative positions during group movement.
- Grid-snapped group movement.
- Copy and paste entire groups.
- Duplicate selected groups.
- Rotate entire groups.
- Preserve relative orientations and positions.
- Paste the same group multiple times.
- Undo and redo group operations.

### 3.5 Grid & Navigation
- Top-down blueprint editing.
- Coordinate-based placement.
- Configurable grid visibility.
- Grid snapping.
- Mouse-wheel zoom.
- Zoom centered around the mouse cursor.
- Mouse-based panning.
- Keyboard-assisted panning.
- Automatic blueprint centering.
- Fit-to-view functionality.
- Improved navigation for large structures.

### 3.6 Blueprint Dimensions
- Automatic total width calculation.
- Automatic total depth calculation.
- Automatic vertical coordinate-span calculation.
- Dimensions displayed in grid units.
- Estimated dimensions displayed in meters.
- Automatic dimension updates.
- Overall blueprint footprint information.
- Building area calculations.

### 3.7 History & Editing Safety
- Undo and redo functionality.
- Multi-operation history.
- Group operations treated as individual history steps.
- Validation before saving.
- Protection against invalid blueprint data.
- Recovery-friendly saving.

### 3.8 Advanced Building Tools – Future Development
- Verified Enshrouded building piece database.
- Accurate building piece dimensions.
- Verified coordinate and rotation conventions.
- Building piece collision detection.
- Overlap detection.
- Placement validation.
- Advanced snapping and alignment.
- 3D blueprint preview.
- 3D blueprint editing.
- Elevation-aware building tools.
- Advanced structure comparison.
- Blueprint compatibility testing.
- Blueprint sharing and community integration.

### 3.9 Dual 2D/3D Editing & Direct Manipulation (Current Direction)

- Use a dropdown to switch between **2D Editor** and **3D Editor** in the same Blueprint Forge workspace.
- Keep both editors synchronized to the same blueprint data.
- Use **right-click + drag** to pan in both editors.
- In 3D, **left-click + drag** a piece to reposition it on the grid; support selected-group movement, surface snapping and moving between floor/elevation levels.
- Preserve 4-meter construction-grid snapping where appropriate, while allowing the editor to accommodate 2-meter and 4-meter non-foundation elements once piece data is verified.
- Provide camera orbit, zoom and fit-to-scene for 3D.
- Show local GLB geometry and base-color textures, with a longer-term goal of verified Enshrouded materials and full PBR rendering.

### 3.10 Placeables Library (Current Direction)

- Separate **Placeables** (available items) from **Placed Elements** (already added to the blueprint).
- Search, filter and categorize foundations, walls, floors, roofs, doors, windows, pillars, stairs and beams.
- Include interactive/decorative placeables such as **magic chests**, furniture, storage containers, crafting stations, lighting, decorations and other eligible objects.
- Import user-provided Type ID catalogs and verify piece identities before representing them as official Enshrouded data.
- Associate building piece Type IDs with local 3D assets, using placeholders when an authentic asset is unavailable.

### 3.11 Image-to-Blueprint (Experimental and Planned)

- Load a **2D drawing, floor plan, or 3D building reference image**.
- Let the user restrict the generation to selected supported piece categories (e.g., foundations, walls and roofs).
- Detect available outlines, shapes and visible features rather than always producing a rectangular box.
- Estimate the required footprint and **auto-fit/resize the editor grid** to the proposed structure.
- Accept a known scale or measurement to convert image features into grid units/meters.
- Preview, edit, approve or reject generated pieces before insertion.
- Record generation uncertainty and identify dimensions/areas inferred rather than observed.
- Improve recognition of walls, doors, windows, roof forms and more complex 3D building geometry over time.
- Preserve all inserted elements as editable blueprint pieces with one undoable import operation.

# 4. Mod Manager

**Purpose:** A centralized environment for managing Enshrouded mods and supported mod frameworks.

**Main features:** EML integration · Client/server profiles · Mod discovery · Manifest validation · Installation/update tools · Configuration backups · Compatibility checks.

### Planned Features
- Detect the Enshrouded installation.
- Detect supported mod frameworks.
- Browse installed mods.
- Install and uninstall supported mods.
- Enable and disable mods.
- Update installed mods.
- Version compatibility checks.
- Mod dependency management.
- Mod conflict detection.
- Mod configuration management.
- Mod installation profiles.
- Backup and restore mod configurations.
- Manage different mod setups.
- Installation error reporting.
- Mod information and documentation.

### EML-Specific Integration (Current Direction)

- Use **Enshrouded Mod Loader (EML)** as the selected mod framework.
- Maintain separate game-client and dedicated-server installation profiles and configuration paths.
- Detect the EML loader proxy (`dbghelp.dll`), `mods` directory, `eml.json` and locally selected `emm.exe`.
- Inspect mod folders and validate `mod.json` fields such as identifier, name, version and capabilities.
- Back up configuration and existing loader files before deliberate changes.
- Provide safe EML validation and template-creation workflows through EMM CLI where supported.
- Future additions: ZIP mod installation, enable/disable, dependency checks, update management and mod profiles.

# 5. Creative Tools

**Purpose:** A collection of advanced in-game building and world-management utilities.

Creative Tools will focus on reducing repetitive building tasks and providing more control over large construction projects.

**Main features:** Area selection · Bulk building operations · Safe demolition previews · Filters and protected objects · Recovery workflows.

### 5.1 Building Utilities
- Advanced building selection.
- Area-based building operations.
- Bulk building piece management.
- Building piece filtering.
- Object categorization.
- Building area measurements.
- Coordinate-based selection.
- Building structure inspection.

### 5.2 Area Demolition
- Select a 3D region using two opposite corners.
- Define rectangular building removal areas.
- Preview all eligible objects before deletion.
- Highlight selected objects.
- Display the total number of selected objects.
- Filter objects by category.
- Exclude specific building pieces.
- Protect important structures and objects.
- Save a recovery snapshot where supported.
- Require explicit confirmation before removal.
- Support large-area operations through controlled batches.
- Report successful and unsuccessful removal operations.
- Verify multiplayer permissions before performing destructive operations.
- Optional Construction Hammer interaction, subject to supported game API capabilities.

### 5.3 Destructive Operation Safety
- Preview-only mode by default.
- Protected objects excluded by default.
- Explicit confirmation before deletion.
- Additional confirmation for large operations.
- Recovery backups where technically possible.
- Permission checks for multiplayer environments.
- No bypassing of server authority.
- Detailed operation reports.
- Leave objects untouched when they cannot be safely identified.

# 6. Item Vault

**Purpose:** A comprehensive searchable database of Enshrouded items, resources, building pieces, and other in-game objects.

**Main features:** Item and object catalog · Type ID search · Material/equipment lookups · Favorites and collections · Blueprint Forge integration.

### Planned Features
- Searchable item database.
- Search by item name.
- Search by internal Type ID.
- Item categorization.
- Building material database.
- Resource database.
- Equipment database.
- Weapon and armor information.
- Furniture and decoration catalog.
- Item variants.
- Item descriptions.
- Item images and icons.
- Favorite items.
- Custom item collections.
- Item information and metadata.
- Copy internal Type IDs.
- Use compatible building piece information directly in Blueprint Forge.
- Export and share custom item collections.

# 7. World Explorer

**Purpose:** An interactive exploration and reference environment for the world of Enshrouded.

**Main features:** Interactive map · Searchable locations · Map layers and markers · Resource/POI discovery · Distances and routes.

### Planned Features
- Interactive world map.
- Coordinate-based navigation.
- Points of interest.
- Location markers.
- Resource locations.
- Enemy and boss locations.
- Ancient Spires.
- Flame Altars.
- Elixir Wells.
- Vault locations.
- Quest-related locations.
- Custom player markers.
- Personal notes attached to locations.
- Categorized map layers.
- Searchable locations.
- Coordinate lookup.
- Distance measurements.
- Route planning.
- Import and export custom markers.
- Community-shared map information.

# 8. Loadouts

**Purpose:** A character build and equipment management system.

Loadouts will make it easier to create, save, organize, and compare different character setups.

**Main features:** Equipment presets · Skill build planning · Build comparison · Saved playstyle profiles · Sharing.

### Planned Features
- Save multiple character loadouts.
- Equipment configuration management.
- Weapon loadouts.
- Armor configurations.
- Ring configurations.
- Skill build documentation.
- Character build presets.
- Build descriptions.
- Equipment comparisons.
- Loadout categorization.
- Favorite builds.
- Export and import loadouts.
- Share character builds with the community.
- Maintain separate builds for different playstyles.

# 9. Dedicated Server Manager

**Purpose:** A centralized administration environment for Enshrouded dedicated servers.

This module will provide server administrators with tools to monitor, configure, maintain, and manage their dedicated servers.

**Main features:** Server controls · Configuration · Automated maintenance · Player administration · Logs · Backups and recovery.

### 9.1 Server Management
- Detect dedicated server installations.
- Start and stop the server.
- Restart the server.
- Monitor server process status.
- Display server uptime.
- Monitor memory and CPU usage.
- Display server configuration.
- Manage server launch parameters.
- Support multiple server profiles.
- Manage multiple dedicated server installations.

### 9.2 Server Configuration
- Graphical server configuration editor.
- Server name management.
- Password configuration.
- Maximum player configuration.
- Server visibility settings.
- Game difficulty configuration.
- Server settings validation.
- Configuration backups.
- Configuration restore.
- Configuration comparison.

### 9.3 Automatic Server Maintenance
- Scheduled server restarts.
- Restart countdown notifications.
- Automatic shutdown and startup.
- Backup scheduling.
- Automatic backup rotation.
- Server update checks.
- Controlled server update procedures.
- Server health monitoring.
- Error detection.
- Maintenance notifications.

### 9.4 Player Management
- Connected player overview, where supported.
- Player list management.
- Admin list management.
- Whitelist management.
- Ban list management.
- Player permission management.
- Player activity information, where available.
- Server announcements.
- Restart and maintenance notifications.

### 9.5 Logs & Diagnostics
- Live server log viewer.
- Server startup logs.
- Error and warning detection.
- Crash information.
- Server performance information.
- Diagnostic reports.
- Log filtering and searching.
- Export diagnostic information.

### 9.6 Backup & Recovery
- Automatic world backups.
- Manual backup creation.
- Backup history.
- Backup rotation.
- Restore selected backups.
- Backup integrity checks.
- Recovery before server updates.
- Backup storage management.

# 10. Community Hub

**Purpose:** A centralized community environment for sharing creations, discovering content, and connecting Enshrouded players.

**Main features:** Blueprint sharing · Community collections · Ratings and search · Guides/resources · Discord links.

### Planned Features
- Community blueprint library.
- Upload and download blueprints.
- Share custom building designs.
- Blueprint categories.
- Search and filtering.
- Tags and descriptions.
- Author information.
- Ratings and reviews.
- Favorite blueprints.
- Community collections.
- Blueprint version information.
- Shared building templates.
- Community resource database.
- Mod recommendations.
- Community guides and tutorials.
- Links to community servers.
- Discord community integration.

# 11. Application-Wide Features

The following functionality will be shared across the different modules.

**Main features:** Norse-inspired interface · Search and keyboard shortcuts · Local data management · Safe file operations · Diagnostics and recovery.

### User Experience
- Unified desktop interface.
- Consistent navigation.
- Search functionality.
- Configurable application settings.
- Keyboard shortcuts.
- Tooltips and contextual help.
- Responsive desktop layouts.
- Dark Viking-inspired visual design.

### Data Management
- Local configuration storage.
- JSON-based data management where appropriate.
- Import and export support.
- Data validation.
- Automatic backup options.
- Recovery mechanisms.
- Versioned data formats.

### Security & Reliability
- Safe handling of game-related files.
- Explicit confirmation for destructive operations.
- Permission-aware server operations.
- Input validation.
- Error handling.
- Diagnostic logging.
- Compatibility checks.
- Recovery options.
- No unsupported modification of game or server data.
- Integration only through verified and supported APIs or frameworks.

### Branding & Accessibility (Current Direction)

- Use the supplied **Bjornhjarta Berserker emblem** as the app logo and executable/window icon.
- Style the UI around black/charcoal, metallic details, luminous green, restrained red accents and readable light typography.
- Ensure high-contrast **dropdown text, selected items, popup backgrounds and hover states** across all windows.
- Use the custom Norse-themed symbols where appropriate without sacrificing readability or accessibility.
