# UI and player workflows

The UI is built with Noesis and MVVM-style view models over Unity. This matters
for automation: accessibility exposes little of the rendered game UI, so tests
usually need screenshots and stable visual landmarks. Coordinates must be
derived from the current captured window, not assumed across resolutions.

## Main menu

Observed entries in version `0.8.2-23444+s358`:

- Scenarios
- Mission Editor
- Campaigns
- Continue (disabled when no suitable state exists)
- Encyclopedia
- Tutorials
- Manual
- Options
- Mod Manager
- Credits
- Exit

The bottom-right version string is a useful screenshot artifact for test runs.

## Scenario workflow

The scenario browser presents a grouped tree and a detail pane. Observed groups
included user missions, campaign scenarios, Intro, JMSDF, NATO, Other, PLAN,
Warsaw Pact, and a custom mission entry.

Selecting `Intro > 01 Red Sea Breakout - NATO (1988)` showed date, weather,
difficulty, narrative, briefing material, and an annotated map. The flow was:

1. Select the scenario and press **Play**.
2. Wait through loading.
3. Review a full-screen mission briefing.
4. Press the briefing's **Play** button to enter tactical play.

The second Play is important for automated smoke tests; reaching the briefing is
not yet proof the tactical scene initialized.

## Tactical screen

The observed tactical screen contains:

- heading ruler at the top;
- mission date/time and time controls at top right;
- detachable Formation Manager at top left;
- detachable Tactical Display/map at bottom left;
- selected-unit name, speed, course, keel/depth, and status along the bottom;
- ammunition and weapon summaries;
- doctrine/communications states such as `Radio Routine` and `Weapons Tight`;
- buttons for Flight Deck, Formation Manager, Event Log, Tactical Display, and
  camera-related functions.

Clicking the central time control changed `x1` to `x0`, proving the mission could
be paused without opening the Escape menu.

### Formation Manager

The intro scenario listed a cruiser and an E-2C formation. The manager provides
a task-force hierarchy and selection surface. The distinction between an
individual unit and its formation matters because several commands and keyboard
shortcuts have both unit and formation variants.

### Tactical Display

The map shows friendly/contact symbols, routes, objective areas, and a toolbar
for map drawing/measurement. It is the player-relative plotting picture, not a
ground-truth debug map.

### Flight Deck

The observed panel showed:

- capacity, deck utilization, and crew utilization;
- recover-all;
- aircraft type, squadron, assigned count, loadout, callsign, and ready/launch;
- a flight-operations queue with abort and launch actions.

This is the tactical endpoint for campaign air readiness and assignments.

### Event Log

Tabs were **Event Log**, **Intelligence**, **Objectives**, and **Environment**.
The log received visual/radar/ESM/passive reports and assigned track numbers.
For tests, this is better evidence of the player's information state than a
Unity object listing.

## Pause and mission end

Escape opened:

- Resume
- Mission Briefing
- Weapon Report
- Options
- Save As
- Load
- Restart Mission
- Send Debug Report
- Exit

Restart is a supported and useful test action. The repository guidance does not
forbid restarting Sea Power or a mission.

Exiting the mission produced an After Action Report with outcome, objective
statuses, enemy losses, allied units/damage, neutral penalty, survivors rescued,
score, and efficiency. It offered Back, Restart Mission, and Exit, with a
confirmation before leaving.

## Campaign browser

Observed installed campaigns:

- Calculus of Conflict (NATO/USSR)
- Pacific Strike '85 (NATO - Task Force Mode)
- Strike Group Molniya (USSR)

A standard campaign selection showed narrative plus Play/Load. Task Force Mode
also showed difficulty, unrestricted/date-limit choices, points, loadout,
air-group, repair, refund, and crew-skill rules. This is a useful visual
regression surface for persistent-force features.

## Mission editor

Opening Mission Editor immediately presents a world map and authoring surface.
No file was saved during this research pass.

### Scenario tab

Controls include filename/load/save, language, mission name/difficulty/
description, weather, wind, sea state, precipitation/fog, date/time, center
latitude/longitude, player side, background-data toggle, and Intel Updates.

The toolbar provides unit-type placement (surface, submarine, fixed wing,
helicopter, land, weapon-like/special objects), load/open, measurements, and
other editing tools. Map filters toggle units by side, drawings, waypoints,
selected group, and trigger areas.

### Groups tab

Groups can be selected and removed; details populate after units/groups exist.
Group-centric authoring is important because formations, orders, and AI behavior
are not solely per-unit concerns.

### Triggers tab

The surface contains mission objectives and an add/remove trigger workflow.
The decompiled model supports a much larger trigger/action graph than is visible
in an empty scenario.

### Mission Briefing tab

The editor selects an assets folder and previews the actual briefing layout,
including Menu, Formation Editor, and Play regions. Generated campaigns should
either supply valid briefing assets or deliberately use a minimal supported
layout.

### Map Elements tab

The editor supports localized map symbols/labels, polygon zones, and mission
points. These are useful for campaign-generated objectives, uncertainty areas,
front lines, and operational boundaries.

Leaving the editor opens a confirmation even when the empty/default state was
not saved. UI automation must handle that dialog.

## Mod Manager

The observed list contained User Data, Anchor Chain (Workshop), ACConfigs
(local), the legacy pre-rename CrimeanTom bridge directory (local), and Base
Game. It offers Sync, dependency Sort,
Create Mod, Accept, and Back.

Anchor Chain and the legacy CrimeanTom bridge directory appeared unchecked and
their description text was missing in this local data-mod list, even though the
in-game runtime bridge assembly had a
fresh successful runtime marker. Use the manager to test content ordering and
metadata, not DLL load health.

## Options and controls

Options are split into Video, Audio, Gameplay, Controls, and General. Gameplay
includes difficulty, flight-deck timing, hit-chance display, 3D identification,
Weapons-Free auto attack, sea-state air-op restrictions, search-sensor
requirements, and jammed-range display.

Controls have Gameplay, General, and Camera sub-tabs. Visible defaults included:

- `X`: cease fire
- `LeftShift+E/A/S/D/T/G/W`: unit EMCON, radar, decoy/sonar, active sonar, ROE
- `LeftControl+LeftShift+...`: formation equivalents
- `Z`: optical view
- `W/S/A/D`: speed and rudder
- `L`: launch aircraft
- `P`: periscope depth

Do not encode all shortcuts into automation. Invoke visible controls where
possible and use only stable, documented keys such as Escape and the developer
installation's F7 UnityExplorer toggle.
