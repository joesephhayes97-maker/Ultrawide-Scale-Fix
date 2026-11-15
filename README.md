# HayesUltrawiderescale

Client-side BepInEx plugin for SPT 4.1. Forces every root UI canvas to
`ScaleWithScreenSize` (reference 3440x1440, match 0.5 by default) and lets you scale or
nudge individual UI parts.

## Build
1. Install the .NET SDK (6.0 or newer).
2. Run `SetSptPath.bat` and give it your SPT folder (the one containing `EscapeFromTarkov.exe`).
3. Run `Build.bat`. The DLL is copied to `BepInEx\plugins\HayesUltrawiderescale\`.

## Settings (F12, or BepInEx\config\com.hayes.ultrawiderescale.cfg)
- **1 General**: enable, reference resolution, match value. Unticking Enabled puts the
  game's original canvas and element values back.
- **2 Canvas Groups**: scale multiplier for HUD, menu screens and everything else, chosen
  by canvas-name fragments.
- **3 Element Overrides**: `Name=scale` or `Name=scale,x,y`, separated by `;`. Use
  `Parent/Child` to tell same-named objects apart. Only canvases that have a CanvasScaler
  are searched, a little per frame, so new windows can take about a second to pick up a rule.
- **4 Debug**: tick "Dump Canvases" to write canvas and child names to the BepInEx log.
  The log also records each canvas's original scaler settings the first time it is touched.

## Performance notes
Scalers are picked up through a hook on `CanvasScaler.OnEnable` (plus one startup pass), so the
plugin never searches the whole project while you play. Nothing is allocated per frame, and the
element scan is capped at about 0.4 ms per frame. If the game keeps resetting a canvas, the log
shows a warning naming it.
