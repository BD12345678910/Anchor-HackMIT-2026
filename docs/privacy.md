# Anchor Privacy and Safety Model

Anchor is designed for a sensitive use case: inferring when a person may have lost task context. The prototype therefore minimizes collection, processes locally, explains its conclusions, and fails open.

## Data boundary

The MVP does not send task, attention, browsing, camera, audio, or interaction data to a cloud service. The C# host and Python worker communicate only over `127.0.0.1` using a random token created for that worker launch. Every request has a deadline, and the host falls back to deterministic rules if the worker is missing or malformed.

## What is observed

| Signal | Representation used by Anchor | Retained |
|---|---|---|
| Foreground application | Process name and redacted window title | Derived event only |
| Keyboard activity | Counts by letter, digit, navigation, editing, modifier, function, or other category | Aggregate counts only |
| Pointer activity | Total movement distance | Aggregate distance only |
| Scrolling | Reversal count | Aggregate count only |
| Idle time | Seconds since last input | Derived duration |
| Browser reading | Origin, title, progress ratio, skip/stuck event, optional short phrase | Derived event; no history permission |
| Task context | Task title, sanitized document identity, location, last action, next action | Local Context Capsule |
| Camera and audio | Not enabled in the MVP | Nothing |

Anchor does not persist raw key values, typed content, pointer coordinates, clipboard history, raw audio, webcam frames, or a continuous screenshot stream.

## Sensitive contexts

Secure, password, permission, payment, browser-internal, and user-denied contexts suppress classification and immediately clear active interventions. Selected text marked sensitive is removed before a Context Capsule is stored. The feature redactor removes likely email addresses, access tokens, secrets, and password-like values.

## Local storage

Events are stored in `%LOCALAPPDATA%\Anchor\anchor.db` using SQLite WAL mode. The database contains session identifiers, timestamps, source/type labels, and bounded redacted feature maps. The dashboard's **Delete local history** action removes every stored event after the current session is stopped.

Deleting the database file while Anchor is stopped also removes history. The project does not create a cloud backup.

## Safety controls

Every restrictive feature is fail-open. Anchor releases overlays, native hooks, and pointer confinement when any of these events occurs:

- The user presses `Esc` or the emergency control
- The restricted window loses focus
- A secure window becomes active
- The 30-second watchdog expires
- The process or session stops
- The application shuts down

The global recovery shortcut is `Ctrl+Shift+F12`; `Ctrl+Shift+A` restores the hidden dashboard. Strong pointer confinement is off by default.

## Human factors

Anchor does not claim to measure a clinical attention span. It shows reason codes and confidence, uses sustained evidence, applies cooldowns, and increases its threshold after dismissals. Movement alone is not treated as failure. Manual recovery is always available because internal distraction cannot always be inferred from computer activity.

## Permissions

The desktop application needs ordinary interactive Windows access for foreground-window events, Raw Input aggregation, overlays, and global hotkeys. The browser extension requests only `activeTab`, `scripting`, `storage`, and `nativeMessaging`; it does not request browsing history. Extension processing begins only after the user activates Anchor for a tab.

## Prototype warning

This hackathon build has not undergone a clinical, accessibility, security, or privacy certification. Test it with non-sensitive demo material first. Do not use it as a medical diagnosis, treatment recommendation, workplace surveillance system, exam proctor, or safety-critical control.
