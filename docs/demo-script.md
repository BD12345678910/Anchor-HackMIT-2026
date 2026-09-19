# Anchor Hackathon Demo Script

This seven-minute demo shows a complete loop: configure gaze, convert an intention into trackable steps, detect drift from several signals, prevent a detour, restore context, and compare a with/without-Anchor recording.

## Before judges arrive

1. Build with `./scripts/build.ps1` and run `release/Anchor-win-x64/Anchor.exe`.
2. Load the release's `browser-extension` folder unpacked in Chrome or Edge, register its displayed ID with `register-browser-bridge.ps1`, and click the extension on the demo origin once.
3. Use public/non-sensitive material. Good choices are an article or problem on `usaco.guide`, `usaco.org`, `codeforces.com`, `luogu.com.cn`, or a local PDF. Avoid signing into school, College Board, or personal YouTube accounts during recording.
4. Keep `./scripts/run-demo.ps1 -Scenario all` ready as deterministic backup evidence.
5. Keep the notification-area icon visible and know the safety keys: `Esc`, `Ctrl+Shift+A`, and `Ctrl+Shift+F12`.

## 1. Prove gaze is real and adjustable — 60 seconds

Open **Test Gaze**, select **Find cameras**, choose the webcam, and select **Start**. Move your eyes between screen corners and point out the live normalized coordinates, confidence, and face-present behavior.

Demonstrate one harmless adjustment, such as mirror or horizontal offset, then select **Apply adjustments**. If camera placement is unusual, complete the nine points: look at the named location, hold still, and select **Capture point**. Explain that OpenCV captures locally, MediaPipe estimates face/iris landmarks, calibration maps the estimate to the screen, and low-confidence/missing-face samples become unknown rather than “distracted.”

## 2. Plan a goal and show dynamic progress — 60 seconds

Optionally show that DeepSeek is enabled without exposing the key. Enter:

> Solve three USACO practice problems and check each solution.

Select **Plan goal**. Show the numbered subtasks and plan source. Explain that the LLM returns a constrained structured plan; malformed or unavailable responses fall back locally. Start the session, show the Goal Beacon on the active monitor, complete one subtask, and show that both Settings and the beacon advance to the next step.

Select **Test attention shake**. The stationary beacon should pulse/shake and then settle; this is the same attention-grabbing behavior used only after sustained evidence, with reduced-motion support.

## 3. Show active prevention in desktop and browser — 90 seconds

Open the approved reading/problem page. In Settings, use the preview controls to show:

- gaze spotlight and peripheral dim;
- low-relevance window firewall;
- intention gate;
- optional pointer guard, followed immediately by `Esc` to prove fail-open release.

On the browser page show dynamic image blur, hide-future-text, and animation suppression. Scroll normally, make a large forward jump, and dwell on one phrase. Explain that the adapter reports coarse progress/skip/stuck evidence; it does not read browser history or modify protected inputs. Hover/reveal or disable the mechanism to show reversibility.

If a live distraction takes too long to accumulate, run:

```powershell
.\scripts\run-demo.ps1 -Scenario focused-to-distracted
```

The audited path is `Focused → Drifting → Distracted`, escalating from a beacon pulse to an intention gate only after sustained evidence.

## 4. Show passive and manual recovery — 60 seconds

Move to an unrelated page or window, then select **I'm distracted** or press `Ctrl+Shift+F12`. The context reminder should appear immediately even if automatic detection is uncertain.

Show that it uses the last safe anchor, not the distracting window: goal, current subtask, prior action, saved origin/document marker, and suggested next action. Demonstrate **Recap**, **Break down**, and **Resume/Reopen**. Breakdown uses DeepSeek when available and returns a labeled local smaller step otherwise.

Run the deterministic backup if needed:

```powershell
.\scripts\run-demo.ps1 -Scenario interrupted-and-returned
.\scripts\run-demo.ps1 -Scenario stuck-reading
```

## 5. Record and compare — 90 seconds

Use participant code `DEMO01` and a dedicated empty folder.

1. Start a planned session. Choose **Baseline · interventions off**, select **Start recording**, perform a short fixed reading task, and stop. Point out that sensing stays active while interventions are suppressed.
2. Repeat the same task and approximate duration with **Anchor enabled**.
3. Select **Compare recordings**, choose the baseline summary, then the Anchor-enabled summary.

Open the output folder and play one MP4. It should show Display 1 with the gaze marker and task state. Briefly show that MP4, event JSONL, sample CSV, summary, and manifest are paired. The comparison reports deltas for gaze coverage, gaze-away time, distracted/low-relevance time, interruptions, recovery time, and completed subtasks, followed by an observational-only disclaimer.

Say explicitly: camera frames and audio are not recorded; screen recording happens only between the two visible recording controls and stays in the selected folder.

## 6. Background behavior and close — 30 seconds

Close Settings. The window disappears but the stationary notification-area icon and active session remain. Reopen with `Ctrl+Shift+A` or the icon. Use **Exit Anchor** from the icon to demonstrate ordered shutdown.

## Judge answers

**Why is this more than an app blocker?** Anchor estimates continuity relative to a declared task, preserves the last safe context before intervention, guides return, and tracks progress through meaningful subtasks. Blocking is only one optional, reversible mechanism.

**What makes it technically complex?** It combines real-time computer vision, calibration, native Windows input/window sensing, semantic LLM calls, temporal multimodal fusion, desktop overlays, a DOM-aware extension, authenticated cross-process IPC, local persistence, synchronized study recording, and fail-open safety.

**How do you avoid a gaze false positive?** Gaze is one confidence-weighted source. The engine also considers task relevance, app switches, idle/input patterns, browser reading behavior, and time. Missing gaze becomes unknown, and stronger interventions require sustained combined evidence and cooldowns.

**What runs without the network?** Gaze, sensors, fusion, overlays, recovery storage, browser controls, recording, and deterministic fallback. DeepSeek planning/relevance degrades to a labeled local fallback.

**What is not implemented?** Signed installation, desktop-wide OCR/object segmentation, webcam-video/audio recording, clinical validation, and reliable modification of protected/DRM surfaces.

## Success checklist

- Packaged `Anchor.exe` launches without Python or .NET installed.
- Test Gaze discovers a camera or clearly reports no usable device.
- Goal planning produces subtasks; completing one advances the beacon.
- Beacon preview and attention shake are visible.
- Browser blur/mask and desktop overlays can be previewed and reversed.
- Manual distraction opens a visible context reminder immediately.
- `Esc` releases restrictive behavior.
- Baseline and enabled trials produce playable MP4 plus aligned metadata.
- **Compare recordings** rejects invalid pairs and reports valid deltas.
- Closing Settings leaves the tray host running; tray Exit shuts it down.
