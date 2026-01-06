# Peak Signs (Signal_Signs)

## Overview
Peak Signs is a small BepInEx plugin for Unity games that lets players place and remove simple arrow signs in the world. Signs can be placed on the ground or attached parallel to walls and are synchronized in multiplayer using Photon events.

## Features
- Place arrow signs on floor or walls (panel aligned parallel to the surface).
- Remove signs by holding the configured mouse button.
- Multiplayer sync: spawn and delete events are sent via Photon and added to the room cache.
- Sign color is derived from the owner's customization (via reflection) and refreshed periodically.
- Progress UI (placing / removing) is cloned from the game's existing "UseItem" HUD element.

## Controls
- Default input: Middle mouse button (configurable via BepInEx config).
- Hold to place or delete:
  - Short hold to place a sign.
  - Hold on an existing sign to delete it.

## Configuration (BepInEx)
Key config entries (see the plugin config file):
- Input.MouseButton — which mouse button triggers placement/deletion (default: 2 = middle).
- Placement.MaxDistance — maximum raycast distance for placement.
- Delete.HoldSeconds — seconds to hold to delete.
- Placement.HoldSeconds — seconds to hold to place.

## Multiplayer / Networking
- Spawn event code: 101
- Delete event code: 202
- Events are raised with EventCaching.AddToRoomCache so late-joiners receive cached events. Note: cached deletes do not automatically remove prior spawn cache entries; the plugin processes events in arrival order.

## Implementation Notes (for developers)
- Main class: PeakSigns (handles input, local spawn/delete and Photon events).
- Marker component: PeakSignMarker stores SignId and OwnerActor on the sign GameObject.
- Visuals: CreateArrowMesh builds a simple arrow mesh; ApplyUnlit sets up an unlit material.
- Wall placement: panel is rotated to be parallel to the hit surface; arrow direction tries to follow the player camera projection on the surface.
- Owner color: uses reflection to access a Customization singleton and player skin data; this is inherently fragile across game updates.
- Recoloring runs periodically (default every 10s) to update colors if player customization changes.

## Build & Installation
- Compile the project as a BepInEx plugin (DLL).
- Copy the compiled DLL to the game's BepInEx/plugins folder.
- Configure options in the BepInEx config file produced on first run.

## Troubleshooting & Tips
- If the progress HUD doesn't appear, the plugin tries to find and clone a radial filled Image named "UseItem". If the game UI changes, adjust the search or hook point.
- Reflection lookups may fail if class names or members change; wrap updates with logging for easier debugging.
- To tweak visuals, modify CreateArrowMesh or the materials created in ApplyUnlit.

## License
- Project code is intended for personal/modding use. Respect the game's modding policy and redistribution terms.
