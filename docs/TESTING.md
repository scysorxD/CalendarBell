# Testing CalendarBell

## Commands

```powershell
dotnet build -c Release                 # or ./scripts/build.ps1
dotnet test -c Release                  # unit + integration (smoke E2E is skipped)
./scripts/publish.ps1                   # dist/CalendarBell-win-x64 + ZIP (+ Setup.exe with Inno Setup)
$env:CALENDARBELL_SMOKE = "1"; dotnet test tests/CalendarBell.Windows.SmokeTests -c Release   # or ./scripts/test.ps1 -Smoke
```

`global.json` pins the .NET 10 SDK and opts `dotnet test` into Microsoft.Testing.Platform (xUnit v3).

## Results observed (18 Nov 2026, Windows 11 Pro 26200, .NET SDK 10.0.404)

| Suite | Tests | Result |
|---|---|---|
| `CalendarBell.Core.Tests` | 125 | 125 passed |
| `CalendarBell.Infrastructure.Tests` | 140 | 140 passed |
| `CalendarBell.Windows.SmokeTests` | 2 | 1 passed + 1 opt-in skipped in a normal run; **2 passed** with `CALENDARBELL_SMOKE=1` against the published build |
| **Total** (`dotnet test -c Release`) | **267** | **266 passed, 1 skipped, 0 failed** |

Stability: the full suite was run 10 times (and 3 more times after the review fixes) in a row without failures after two timing issues were root-caused and
fixed (see the git history: corrupt-file name collision within one millisecond; FakeTimeProvider multi-minute jumps
racing a background timer re-arm).

`dotnet build -c Release`: 0 warnings, 0 errors (warnings are errors, .NET analyzers on).

## Spec test matrix (§10.1)

| ID | Covered by |
|---|---|
| T01 | `ReminderStateMachineTests.T01_*`, `ReminderSchedulerTests.T01_*` |
| T02 | `ReminderSchedulerTests.T02_polling_at_1014_and_1016_…` |
| T03–T06 | `ReminderStateMachineTests.T03…T06`, `ReminderSchedulerTests.T03_T05_T06_*`, `T04_*`, `Available_targets_are_strictly_future_with_two_second_margin` |
| T07 | `ReminderSchedulerTests.T07_*`, `StorageTests.T07_*` (SQLite reopen), `SchedulerPersistenceIntegrationTests` |
| T08 | `ReminderSchedulerTests.T08_*` |
| T09–T11 | `ReminderSchedulerTests.T09/T10_T11`, `NotificationContentTests.T09_*`, `NotificationOrchestratorTests.T10/T11_*` |
| T12 | `ReminderSchedulerTests.T12_*`, `NotificationContentTests.T12_*` (one toast, < 5 KB, `+98 more`) |
| T13 | `ReminderSchedulerTests.T13_*`, `IcsCalendarParserTests.T13_*` |
| T14 | `ReminderSchedulerTests.T14_*`, `CalendarSyncServiceTests.T14_*` (HTTP 304) |
| T15, T16 | `ReminderSchedulerTests.T15_*`, `T16_*` (toast retraction) |
| T17, T18 | `ReminderSchedulerTests.T17_*`, `T18_*`, `Recalculate_after_clock_jump_fires_late_or_expires` |
| T19 | `IcsCalendarParserTests.T19_*` (US and EU DST transitions) |
| T20–T22 | `CalendarSyncServiceTests.T20_*`, `T21_*`, `T22_*`; `IcsCalendarParserTests.T21_*` |
| T23–T25 | `IcsCalendarParserTests.T23_*`, `T24_*`, `T25_*` |
| T26 | `UrlAndSecurityTests.T26_*` (DPAPI), `StorageTests.T26_*` (no URL in DB/WAL/backup/settings) |
| T27 | `UrlAndSecurityTests.T27_*`, `CalendarSyncServiceTests.T27_*`, `WindowsIntegrationTests.File_logger_writes_redacted_lines` |
| T28 | `NotificationContentTests.T28_*` (`<audio silent="true"/>`) |
| T29–T31 | `ReminderSchedulerTests.T29_*`, `T30_*`, `T31_*`; `CalendarSyncServiceTests.T31_*` |
| T32 | `WindowsIntegrationTests.T32_*`; smoke step 4 (real processes) |
| T33 | `NotificationContentTests.T33_*` |
| T34 | `ReminderSchedulerTests.T34_*` (fires within ±2 s with a fake clock) |
| T35 | `PublishedAppSmokeTests` step 6 (app closed → COM activation relaunches it, action processed) |
| T36 | `ReminderSchedulerTests.T36_*`, `CalendarSyncServiceTests.T36_*` |

Extra edge cases from the plan's review focus: stale snooze choice from an old toast falls back to the next future
target; clock jump re-evaluation; HTML sign-in page with 200 OK; Outlook display-name and "Customized Time Zone"
TZIDs; renaming a calendar with reminders pending.

## Windows smoke test (`tests/CalendarBell.Windows.SmokeTests`)

`PublishedAppSmokeTests` copies `dist/CalendarBell-win-x64` to a temp folder, writes a `CalendarBell.dev.json`
(isolated data folder, http://localhost allowed), starts the mock feed server, seeds *Calendar A* and *Calendar B*
with DPAPI-encrypted URLs containing a fake token, starts the app and then:

1. waits for the real group toast at T−15 (A and B at the same minute);
2. clicks **Snooze / 5 min before** through the registered COM activator (`INotificationActivationCallback`, the same
   path the shell uses) and verifies both members are snoozed to *their own* start − 5 min in SQLite;
3. waits for the next single toast and clicks **Dismiss**; a third toast (Calendar B) is deliberately left unanswered;
4. launches a second instance (`--upcoming`): it exits and only one resident process remains;
5. asks the app to exit through the single-instance pipe;
6. with the app **closed**, clicks **Dismiss** on the earlier group toast: Windows relaunches CalendarBell, the click
   is processed (`Started by a notification click`), both occurrences become dismissed, and the relaunch **keeps**
   the unanswered toast (`kept 1 pending toast(s)`);
7. restarts the app and verifies dismissed reminders do not reappear;
8. verifies no file in the data folder contains the URL or the token.

Set `CALENDARBELL_SMOKE_KEEP=1` to keep the temp copy and logs for diagnosis. The test removes the COM/AUMID
registration of its temp copy afterwards.

## Manual verification performed on Windows 11 (this machine)

Done with the release build, an isolated data folder and the mock feed server (fictitious Calendar A / B):

* First start: no console; tray icon appears; Settings opens once (no calendars).
* *Add calendar* (UI Automation): URL masked by default, *Test connection* → `Connected · 1 upcoming event`;
  saving shows `Synced · 12:50 PM` with a green dot for both calendars.
* Real toasts in Notification Center: `Calendar B · Meeting in 14 min` / `Tentative · 1:04 PM–1:34 PM` (Windows
  12-hour regional format) with the CalendarBell icon. The machine had **Do not disturb on**, so Windows kept banners
  in Notification Center (expected; the app does not bypass it).
* Late reminder (calendar added after T−15) shown immediately; the second one was delayed by the 8 s rate limit.
* Upcoming: *5 min before* on a 1:04 PM meeting → `Snoozed to 12:59 PM`; *Dismiss* hides the item (shown again
  with *Show dismissed*).
* About & diagnostics: registration `Registered`, Windows setting `Enabled`, no URLs.
* Second launch with `--upcoming`: exits in 0.45 s, resident instance opens Upcoming, one process.
* Tray menu exactly as specified (dark theme following Windows); *Pause → 30 minutes*: grey paused icon, tooltip
  `CalendarBell · Paused`, Settings banner with *Resume*; *Exit* stops the process cleanly.
* Every file in the data folder (DB, WAL, backup, settings, logs) searched: no token and no URL.
* Install dialog from the extracted folder; quiet per-user install: files in `%LOCALAPPDATA%\Programs\CalendarBell`,
  Start menu shortcut, *Installed apps* entry, non-elevated resident process, logon task registered **without
  admin** (`InteractiveToken`, least privilege, `ExecutionTimeLimit PT0S`, `"…\CalendarBell.exe" --background`),
  no duplicate Run key. Quiet uninstall: process stopped, folder, shortcut, entry, task and notification
  registration removed; user data kept (quiet mode). All test artifacts were removed afterwards.

## Not verified here (honest limitations)

* Behaviour across a real sign-out/sign-in and reboot (the logon task was verified as registered, not observed
  firing at a real logon).
* Banner appearance and sound with Do not disturb **off** (the test machine had it on); verified only that
  Windows received the toasts with `scenario="reminder"` and the reminder sound event.
* Light theme, 150 %/200 % DPI and multi-monitor placement were not exercised visually (the code validates saved
  positions against connected monitors and uses per-monitor DPI awareness).
* 24-hour endurance / memory trend.
* Real published Outlook / Google feeds (the parser is tested with Outlook-style synthetic fixtures).
* `CalendarBell-Setup.exe`: Inno Setup is not installed on the build machine; the `.iss` source is included and
  `publish.ps1` builds it when `ISCC.exe` exists.

## Manual test checklist (for a fresh machine)

1. Extract the ZIP, run `CalendarBell.exe` → *Install*. No console window. Tray bell appears; Settings opens.
2. Add *Calendar A* and *Calendar B* (published ICS links), *Test connection*, *Save*; both show `Synced`.
3. Close Settings with X → the bell stays. Outlook can be closed.
4. With a meeting 16 minutes away: a reminder with sound at T−15; choose *10 min before* → *Snooze* → it returns
   at T−10; then *5 min before*, then *At start*; *Dismiss* ends it.
5. Two calendars with a meeting at the same time → one grouped notification.
6. *Pause reminders → 1 hour*, then *Resume reminders*.
7. Sign out and in (or reboot): the bell comes back without a console; snoozes and dismissals are remembered.
8. *Settings → Apps → Installed apps → CalendarBell → Uninstall*.

## Final code review

An independent whole-branch review (read-only) found no critical issues and 4 important ones; 9 minor findings were
re-graded by their effect on a user and fixed in the same pass (each with a test that failed first, except two
App-layer fixes verified by build and the smoke test). See `ReviewFixTests` in both test projects. Fixed:
pending toasts wiped on restart, 304 responses freezing the 30-day expansion window, in-flight refresh undoing
disable/rename/remove, storage failure suppressing a toast, unexpected parser exceptions causing a retry loop, the
8 s spacing using the wall clock, group toasts retracted by another calendar, titles kept after enabling Privacy
mode, removed URLs left in a backup copy, partial wiring after a storage reset, pipe listener spinning, offset-only
time-zone fallbacks shadowing another feed's DST rules, and setup failing when another user runs CalendarBell.
Deferred (minor): a lock around the pause value; a test with real timers across sleep.
