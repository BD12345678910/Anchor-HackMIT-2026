# Anchor Privacy and Safety Model

Anchor infers whether a person may have lost task context. That is sensitive and uncertain, so the release minimizes collection, labels cloud use, keeps study capture opt-in, and makes every restrictive mechanism fail open.

## Data flows

| Source | What Anchor uses | Stored by normal use |
|---|---|---|
| Camera | Face landmarks, normalized gaze point, confidence, face-present flag | Calibration/settings and derived events; no webcam frames |
| Foreground desktop | Process name and redacted window title | Bounded derived events |
| Keyboard | Counts by key category | Aggregate counts; never raw keys or typed text |
| Mouse/scroll | Movement distance, idle duration, scroll reversals | Aggregate values; not raw pointer history |
| Browser adapter | Approved origin, title, reading progress, skip/stuck events, optional short phrase | Derived event; no browsing-history permission |
| Task context | Goal, subtask, sanitized document/origin anchor, prior and next action | Local Context Capsule |
| DeepSeek, when enabled | Goal and bounded/redacted task context needed for planning or relevance | Subject to the user's DeepSeek account and API policy |
| Study recording, when explicitly started | Display 1 frames, composited gaze point/task state, event timeline, derived samples | MP4/JSONL/CSV/JSON in the folder chosen by the user |

Webcam video and audio recording are unavailable in this release. The live camera stream is processed locally by the bundled gaze worker and is not written to the study output. Ordinary use does not retain screenshots. Screen capture starts only after the user selects **Start recording** and ends on **Stop recording**, session stop, worker failure, or app shutdown.

## Local and cloud boundaries

Gaze, recording, sensor fusion, context storage, and interventions run locally. The desktop host, browser bridge, and worker communicate through authenticated local channels with random per-launch credentials and bounded messages.

DeepSeek is optional and off until the user enables it and stores an API key. The key is encrypted for the current Windows account. When enabled, Anchor sends the goal and bounded task context for task decomposition, relevance classification, or smaller-step recovery. It does not send screen video, webcam frames, raw keys, or raw pointer traces. If DeepSeek is disabled, unreachable, malformed, or slow, Anchor reports the degradation and uses deterministic local fallback.

## Study files

Each recording creates:

- an MP4 screen recording with gaze/task overlay;
- an event JSONL file;
- a gaze/attention sample CSV;
- a summary JSON;
- a manifest JSON describing mode and file relationships.

Use a pseudonymous participant code, not a name, email, school ID, or medical record number. The chosen output folder may be synchronized by third-party software such as OneDrive; Anchor cannot control that folder's backup policy. Delete study files manually when they are no longer needed.

Baseline mode keeps sensing active for measurement but suppresses interventions. The comparison report rejects missing, malformed, incompatible, or same-mode trials rather than converting absent values to zero.

## Sensitive contexts and suppression

Password, payment, permission, secure-desktop, browser-internal, editable, dialog, protected-media, and user-denied contexts suppress relevant browser or desktop interventions. Redaction removes likely email addresses, access tokens, secrets, and password-like values before bounded context is stored.

Because full-screen study recording captures what is visible on Display 1, the participant must avoid opening private messages, passwords, personal accounts, or other sensitive material during an active trial. Stop recording before switching to sensitive content.

## Local storage and deletion

Normal event history is stored in `%LOCALAPPDATA%\Anchor\anchor.db` using SQLite. It contains session identifiers, timestamps, source/type labels, and bounded redacted feature maps. **Delete local history** removes stored event history after the current session stops.

Study recordings are separate files in the user-selected folder and are not removed by **Delete local history**. Browser permission and native-host registration are also separate; use the extension controls and `unregister-browser-bridge.ps1` to remove them.

## Safety controls

Anchor releases overlays, hooks, and pointer confinement when the user presses `Esc`, chooses emergency release, leaves the protected window, enters a secure context, the watchdog expires, the session stops, or the app exits. Pointer guard is off by default. Closing Settings hides the window; the notification-area **Exit Anchor** action performs full shutdown.

Manual recovery (`Ctrl+Shift+F12` or **I'm distracted**) is always available during a session because internal distraction cannot always be inferred from external behavior. Low-confidence or missing gaze is treated as unknown evidence, never as proof of distraction.

## Responsible use

Anchor does not measure a clinical attention span and has not undergone clinical, accessibility, security, or privacy certification. Do not use it for diagnosis, treatment decisions, employee surveillance, exam proctoring, or safety-critical control. Obtain informed consent before recording another person, test with non-sensitive material first, and describe study comparisons as observational prototype results.
