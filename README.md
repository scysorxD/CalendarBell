# CalendarBell

Native Windows 11 meeting reminders for calendars published as **ICS / iCalendar links** — for example calendars
added to New Outlook "from the internet", which show meetings but do not remind you about them.

CalendarBell lives in the notification area (tray), polls your calendar links every 2 minutes and shows a
**native Windows notification with sound 15 minutes before each meeting**:

```
CalendarBell
Calendar A · Meeting in 15 min
Busy · 10:30–11:00
[10 min before ▾]          [Snooze] [Dismiss]
```

* **Snooze to an absolute time** — *10 min before*, *5 min before* or *At start* of the meeting (not "10 minutes
  from now"). Only targets that are still in the future are offered.
* **Dismiss** is permanent for that occurrence, across refreshes and restarts. Closing a notification with **X**
  never brings the same reminder back.
* **Several calendars** (Calendar A, Calendar B, …). Meetings due at the same time arrive as **one grouped
  notification** — never a pile of pop-ups.
* Works with **Outlook closed**. No account sign-in, no add-in, no cloud service, no telemetry.
* **Privacy mode** (on by default): notifications show only calendar name, availability and time — never titles,
  descriptions, attendees or links.

---

## Requirements

* Windows 11 x64 (Windows 10 19041+ also works). No administrator rights. No .NET installation needed — the app is
  self-contained.
* A published calendar link (`https://…` or `webcal://…`), e.g. from Outlook: *Settings → Calendar → Shared
  calendars → Publish a calendar*, copying the **ICS** link. Only use links you are allowed to use under the
  calendar owner's policy: **a published link works like a password**.

## Install

1. Download and extract `CalendarBell-<version>-win-x64.zip` (or build it, see below).
2. Run **`CalendarBell.exe`** from the extracted folder → **Install**.
   It installs for your Windows account only into `%LOCALAPPDATA%\Programs\CalendarBell`, adds a Start menu entry,
   registers itself in *Settings → Apps → Installed apps* and starts in the notification area. No console window
   is ever shown.
3. Settings opens once on first run. Click **Add calendar**, enter a **Calendar name** (e.g. `Calendar A`) and
   paste the **Calendar URL**, then **Test connection** → **Save**. Add more calendars the same way.

*Run without installing* starts the app from the extracted folder without installing it (no auto-start).
Unattended install: `CalendarBell.exe --install --quiet`.

If `dist\CalendarBell-Setup.exe` exists (built when Inno Setup 6 is available), you can use it instead; it installs
to the same per-user location.

> **SmartScreen:** the binaries are not code-signed. Windows may show "Windows protected your PC" the first time —
> choose *More info → Run anyway* only if you obtained the files from a source you trust.

## Everyday use

Right-click the bell in the notification area (it may be under the **^** "Show hidden icons" arrow — drag it to
the taskbar to keep it visible):

| Menu item | What it does |
|---|---|
| **Upcoming reminders** (also double-click) | Meetings of the next 24 h with individual Snooze/Dismiss, filters and status. |
| **Settings** | Calendars (add, edit, enable/disable, check now, remove) and general options. |
| **Check now** | Refreshes all enabled calendars immediately. |
| **Pause reminders** › 30 minutes / 1 hour / Until resumed | No notifications while paused (sync continues). Reminders that fall due during a pause are not replayed. |
| **Start with Windows** | Starts CalendarBell when you sign in (Task Scheduler, never elevated). |
| **Status** | e.g. `2 calendars · Synced`. |
| **About** | Version and diagnostics (copyable, without URLs). |
| **Exit** | Really stops CalendarBell. Closing Settings with X does **not** exit. |

Tray icon states: blue bell = OK · bell with amber **!** = sync or notification problem · grey bell with ⏸ = paused.
The tooltip says `CalendarBell · Synced`, `Sync warning`, `Paused`, …

Clicking the body of a notification opens *Upcoming reminders* filtered to those meetings.

## Windows notification settings

CalendarBell never bypasses your Windows preferences:

* *Settings → System → Notifications*: **Notifications** must be on and **CalendarBell** must be allowed. For the
  most visible reminders, open CalendarBell there and enable *Show notification banners*.
* **Do not disturb / Focus** holds banners: reminders still go to Notification Center (Win + N). To let them
  through, add CalendarBell to *Set priority notifications*.
* *Play notification sound* in CalendarBell's Settings controls the reminder sound; Windows' own sound and volume
  settings still apply.

Settings shows a banner when Windows has notifications for CalendarBell turned off, with a button to the right
Windows page.

## Privacy and security

* Calendar URLs are encrypted with **Windows DPAPI** for your user account and stored in
  `%LOCALAPPDATA%\CalendarBell\calendarbell.db`. They are never written in clear text to settings, logs, error
  messages or diagnostics; the URL field is masked unless you click *Show URL*.
* Only what is needed for reminders is cached (start, end, availability, and — only with Privacy mode off — the
  title). Descriptions, attendees and meeting links are never stored or shown.
* Only `https://` links are accepted (`webcal://` is converted to `https://`). Certificates are always validated.
* Logs (`%LOCALAPPDATA%\CalendarBell\logs`, max 5 × 2 MB, 14 days) contain calendar names/IDs, HTTP status codes
  and timings — URLs and query strings are redacted.
* No telemetry, no third-party services.

## Troubleshooting

| Symptom | What to check |
|---|---|
| No notification appeared | Win + N (Notification Center) — Do not disturb may have held the banner. *About → Send test notification*. Windows notification settings for CalendarBell. |
| `Network error` on a calendar | Connectivity; *Check now*. CalendarBell keeps using the last good data for up to 24 h and retries (2, 4, 8, 16, 30 min). |
| `Invalid calendar` | The link no longer returns a calendar (expired or unpublished link, HTML sign-in page). Re-publish and *Edit* the calendar. |
| `Access denied (HTTP 401/403)` | The published link was revoked or changed. |
| `Data out of date` | No successful sync for more than 24 h; reminders from that calendar are held until it syncs again. |
| Meeting times are wrong | Check the Windows time zone. CalendarBell converts every calendar time zone (including Outlook's) to your local time. |
| "Running as administrator" banner | Windows does not deliver notifications to elevated apps. Start CalendarBell normally. |
| "Storage error" banner | The local database is damaged. A copy is kept; *Reset storage…* starts a new one and restores your calendars from their encrypted backup. |

*About → Copy diagnostics* produces a report without URLs that you can share. *Open logs folder* opens the logs.

## Uninstall

*Settings → Apps → Installed apps → CalendarBell → Uninstall*. You can choose to also delete your settings,
encrypted calendar URLs and logs. Uninstall removes the program, the Start menu entry, the sign-in startup entry and
the notification registration. Silent: `"%LOCALAPPDATA%\Programs\CalendarBell\CalendarBell.exe" --uninstall --quiet`
(keeps user data).

## Limitations

* Reminders depend on the published feed: providers may publish changes late, and CalendarBell polls every 2
  minutes. A meeting added 5 minutes before it starts gets a late reminder ("Meeting in 4 min").
* Nothing can be shown while the PC is off or asleep; after waking up, reminders for meetings that have not started
  yet are shown once, older ones are skipped.
* Windows decides how notifications look and where they appear; Do not disturb, notification settings and volume
  are respected.
* Availability-only feeds usually do not say whether you accepted a meeting; CalendarBell shows `Busy`,
  `Tentative`, `Out of office` only when the feed states it (otherwise `Meeting`).
* Events without a UID get a stable fallback identity; if such an event is edited, it may look like a new event.
* The UI is in English; Spanish (es-AR) resources are included and can be enabled with `"Language": "es-AR"` in
  `%LOCALAPPDATA%\CalendarBell\settings.json`.

---

## Building from source

Requirements: Windows 10/11 x64, .NET 10 SDK (pinned by `global.json`), PowerShell 7.

```powershell
./scripts/build.ps1          # dotnet build -c Release
./scripts/test.ps1           # dotnet test -c Release (unit + integration)
./scripts/test.ps1 -Smoke    # + Windows smoke tests against dist (real notifications)
./scripts/publish.ps1        # dist/CalendarBell-win-x64, ZIP, and Setup.exe if Inno Setup 6 is installed
```

Development data and a local test feed:

```powershell
$env:CALENDARBELL_DATA_DIR = "$env:TEMP\cb-dev"        # isolated data folder (no install prompt)
$env:CALENDARBELL_ALLOW_HTTP_LOCALHOST = "1"            # allow http://localhost feeds (tests only)
dotnet run --project tools/CalendarBell.MockFeedServer -- --urls http://localhost:5199
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/TESTING.md](docs/TESTING.md) and [AGENTS.md](AGENTS.md).
