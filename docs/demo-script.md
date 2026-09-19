# Anchor Hackathon Demo Script

This demonstration shows one closed loop: declare an intention, detect sustained drift, prevent a detour, restore context, and measure the return. It takes about four minutes and remains reproducible if the inference worker or browser extension is unavailable.

## Preparation

1. Run `./scripts/build.ps1` and confirm all suites pass.
2. Optionally load `browser/anchor-extension` as an unpacked extension.
3. Start Anchor through the development command in the README or from the published output.
4. Keep `./scripts/run-demo.ps1 -Scenario all` ready as deterministic evidence.
5. Use only the included demo text; avoid personal accounts or sensitive documents.

## Story 1 Declare the task

Enter **Review the attention research paper** and start the session.

Point out the compact Goal Beacon and the capability label. Explain that the worker can disappear without ending the session because deterministic inference remains in the host. The dashboard reports estimates and reasons, not a medical score.

## Story 2 Active prevention

Run:

```powershell
./scripts/run-demo.ps1 -Scenario focused-to-distracted
```

The replay shows `Focused → Drifting → Distracted`. The proportional intervention path is `None → BeaconPulse → IntentionGate`. Explain that a single noisy sample cannot reach `Distracted`; the state requires sustained evidence. The gate offers Return, Continue, Park for later, and Disable gates.

In the live app, enable **Pointer guard** only if there is time to demonstrate the fail-open path. Press `Esc` and show that overlays and confinement clear immediately.

## Story 3 Manual and passive recovery

Press **I'm distracted** or `Ctrl+Shift+F12`. The recovery card should appear without waiting for detector confidence.

Show the saved task title, last anchor, and suggested next action. Use **Recap** and **Break down**, then select **Resume**. Emphasize that context preservation runs even when active prevention is disabled.

Run:

```powershell
./scripts/run-demo.ps1 -Scenario interrupted-and-returned
```

The expected state path ends in `Focused`, and the replay reports an interruption and recovery.

## Story 4 Reading adapter

On a safe article page, activate the Anchor extension. Show dynamic image softening and temporarily reveal an image by hovering. Enable future-text masking in the extension options if desired.

Run:

```powershell
./scripts/run-demo.ps1 -Scenario stuck-reading
```

The repeated scroll loop enters `Stuck` and opens a Recovery Card. The extension also detects large forward skips and repeated phrase dwell, which can supply richer evidence than the desktop layer alone.

## Story 5 Privacy and degraded operation

State the retained features: categories and counts, not raw keys or coordinates. Open the dashboard privacy section and show **Delete local history**. Stop the session before deletion.

If asked about the worker, stop it and explain that the next prediction uses deterministic fallback with a `worker_unavailable` reason rather than ending the session. Password, payment, permission, and secure windows suppress interventions.

## Judge questions

**How is this different from an app blocker?** Anchor models task relevance and attention continuity, preserves context before intervening, and supports recovery rather than only denying access.

**Why is it technically difficult?** It combines native Windows event hooks, bounded event flow, temporal sensor fusion, an explicit state machine, authenticated process isolation, DOM-level browser adaptation, local persistence, and fail-open safety.

**What is actually working?** The repository contains the Windows host, sensor aggregation, deterministic and Python inference paths, prevention/recovery overlays, browser adapter, local history, automated tests, and deterministic end-to-end replays.

**What remains future work?** Opt-in gaze/head-pose, audio transients, OCR/UI Automation saliency, application-specific adapters, learned personalization, signed packaging, and formal user evaluation.

## Success checklist

- Goal Beacon displays the declared task
- Manual report opens recovery immediately
- Drift replay reaches the intention gate
- Interruption replay returns to focused
- Stuck-reading replay reaches recovery
- `Esc` clears restrictive behavior
- Worker absence leaves the session usable
- Local history deletion empties the event store
