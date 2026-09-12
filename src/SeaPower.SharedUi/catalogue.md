# Native control catalogue

Sea Power `0.8.2-23810+s366`. Inspected with CLI `inspect`, `details` and screenshots.
This catalogue separates reusable controls from game-owned views and behaviour.

| Screen | Reusable elements | Shared helper |
|---|---|---|
| Unit Status / Flight Deck | Capacity bars, dropdowns, action buttons, resizable table headers | `CapacityMeters`, `Choice`, `Button`, `Table` |
| Unit Status / custom tabs | Tab availability, content attachment, selection, teardown | `UnitStatusPage` |
| Unit Status / Engagement | Equal-width lanes, status cards, mount labels, scrolling lists | `Columns`, `Card`, `Text`, `Scroll` |
| Unit Status / Damage Control | Compartment lists, team allocation, automatic-control toggle, summaries | `List`, `Stepper`, `Toggle`, `Text` |
| Unit Status / Sensors | Sensor list, target selector, numeric target and range fields | `List`, `Choice`, `Number`, `Split` |
| Encyclopedia / Unit Reference | Search field, country filter, checkboxes, category tree, images, detail tabs, stat bars | `Entry`, `Choice`, `Check`, `Tree`, `Branch`, `Picture`, `Tabs`, native `ProgressBar` |
| Standing Orders | Global/unit tabs, expandable categories, boolean options, enum choices, summary panel | `Tabs`, `Section`, `Check`, `Choice`, `Card`, `Scroll` |
| Editor / Scenario | Name and description fields, language/weather/side/date/time choices, coordinate fields | `Entry`, `Choice`, `Number`, `FormRow` |
| Editor / Groups and units | Group selector, formation/ROE choices, category tree, unit list, paired labels and editors | `Choice`, `Tree`, `List`, `FormRow` |
| Editor / Mission Briefing | Multiline text, size/font choices, boolean options | `Entry(multiline: true)`, `Choice`, `Check` |
| Editor / Map Elements | Element-kind and language choices, map visibility flags | `Choice`, `Check` |
| Editor / Triggers | Expandable conditions/actions, paired source/destination lists, multilingual messages, file browse fields, increment/decrement buttons | `Section`, `CollectionEditor`, `Split`, `Entry`, `BrowseField`, `Stepper` |
| Editor / Background Data | Category lists, add/remove, native file picker with name/date columns | `CollectionEditor`, `Table`, `TextColumn`, `BrowseField` |
| Editor / AI Behavior | Allow morale to affect AI | `Check` |
| Editor / Air Operations | Operation list, platform/type/loadout selectors, timing/count fields, repeat/disabled/radar flags | `CollectionEditor`, `Choice`, `Number`, `Check` |

## Layout and styling

Controls inherit the game's implicit Noesis styles. Do not copy a live control's
bindings or steal its parent. `Card` uses the observed charcoal panel and border
colours. The default text size is 12 logical pixels; editor/encyclopedia inputs
use native padding. `Choice` has a minimum height of 30, not a fixed height.

Flight Deck's capacity grid has auto-sized labels, a 10-pixel label gap and a
10-pixel gap before Recover All. The top bar spans the action column. The action
spans the lower two rows. `CapacityMeters` reproduces that layout.

`Split` uses native GridSplitter behaviour. `Scroll` owns scrolling for long forms;
do not wrap a table in it. Put tables and split panes in star-sized grid rows.

## Game-owned views

The map canvas, drawing tools, formation gizmos, damage compartment geometry,
weapon engagement logic, sensor models, aircraft preparation and encyclopedia
catalogue loading remain native. Generic controls do not implement those systems.
Images accept an existing `ImageSource`; callers own asset loading and lifetimes.
Date/time fields in the editor are ordinary dropdowns, not a special date-picker.

## Survey coverage

Live survey completed on 2026-09-11 using the Tarawa smoke scenario, the editor
and the Tarawa encyclopedia entry. No editor changes or standing orders were saved.

- Unit Status: Engagement lanes and mount summaries, Flight Deck selectors and
  capacity controls, Well Deck, Damage Control compartment/team controls, Sensors
  with SPS-52 selected, and Supplies. Supplies displayed its placeholder through
  `UnitStatusPage` after moving between a mission and the encyclopedia.
- Editor: Scenario, Groups, Triggers, Background Data, AI Behavior, Air Operations,
  Mission Briefing and Map Elements. Every trigger section was expanded, a sample
  condition and air operation were added, and the background-data file picker was
  opened then cancelled. AI Behavior contains only the morale checkbox in this build.
- Encyclopedia: Overview, Armament, Sensors & EW, Air Wing, Stealth and Cost.
  The detail pages combine tables, labelled statistics and progress bars.
- Standing Orders: global categories expanded, unit tab inspected. Boolean and
  enum controls sit in expandable sections beside a summary panel.
- Gallery: Forms, Operations, Browser, Capacity and Lists opened in a mission.
  Dropdown selection, numeric edits and slider changes reached sample callbacks.
  Native table headers and rows rendered. Deep `details --depth 32` JSON decoded
  successfully. The gallery now refuses the main menu because its window styling
  differs from the mission host.

This is a control/layout survey, not verification of every gameplay-dependent
state. Active engagement tracks, damage events, seeker telemetry, map drawing
and every editor condition permutation were not exercised. Their game models and
specialized rendering remain native. New helper methods are compile-checked;
the gallery does not yet exercise every helper or mouse gesture.

Final compile checks passed for GameBridge, CLI, UNREP, Well Deck and Amphibs,
with zero warnings and errors. The final helper additions were compile-only;
the running build verified the earlier gallery and tab attachment implementation.
