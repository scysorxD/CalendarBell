# CalendarBell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build CalendarBell, a per-user Windows 11 tray app (WPF, .NET 10) that polls ICS feeds and raises native Windows App SDK reminders 15 min before each meeting, with absolute-time Snooze (T−10/T−5/T), Dismiss, grouping, persistence, auto-start and a per-user installer.

**Architecture:** Three layers. `CalendarBell.Core` (pure .NET, no Windows APIs) owns models, the reminder state machine, scheduler, batching/rate limiting and toast XML generation, all driven by `TimeProvider`. `CalendarBell.Infrastructure` (net10.0-windows) owns ICS parsing (Ical.Net), HTTP sync, SQLite storage, DPAPI, logging, startup registration, single-instance IPC and the install/uninstall logic. `CalendarBell.App` (WinExe, WPF + WinForms NotifyIcon) owns the tray, windows and the Windows App SDK notification adapter.

**Tech Stack:** .NET 10 SDK (pinned by global.json), C# 13+, WPF (Fluent `ThemeMode=System`), Microsoft.WindowsAppSDK.Foundation 2.7.0 (self-contained, unpackaged), Ical.Net 5.2.4, Microsoft.Data.Sqlite 10.0.x, System.Security.Cryptography.ProtectedData 10.0.x, Microsoft.Extensions.* 10.0.x, xUnit v3 + Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `CalendarBell_SPEC_GENERICA.md` (repo root). The spec is the contract; this plan records the decisions it leaves open.

## Global Constraints

- Windows 11 x64, per-user, never elevated, never a Windows Service, no visible console in any path (`OutputType=WinExe`).
- Initial reminder fixed at 15 min before start; snooze targets are absolute: `T−10`, `T−5`, `T` (0). Only targets strictly in the future (2 s margin) are offered.
- Polling every 2 min per enabled calendar; max 3 concurrent downloads; 15 s timeout; 10 MiB max body; max 3 redirects, HTTPS only; backoff 2/4/8/16/30 min with jitter; honour `Retry-After` on 429.
- Expansion window `[now − 1 day, now + 30 days]`, max 100,000 instances.
- Coalescing window ~1 s; at most one new toast every 8 s; one sound per toast; toast XML < 5 KB; ≤ 2 detail lines plus `+N more`.
- Late policy: due-but-unsent before start → immediate late toast with real remaining minutes; start ≤ now < start+60 s → one "starting now" toast; ≥ start+60 s → Expired, no toast.
- Calendar name: trimmed, 1–40 chars, unique case-insensitively. URL: required, ≤ 4096 chars, HTTPS (http://localhost only in DEBUG builds or when `CALENDARBELL_ALLOW_HTTP_LOCALHOST=1`).
- URLs encrypted with DPAPI CurrentUser; never logged, never shown in errors, never in fixtures. Logs: 5 files × 2 MiB, 14-day retention, URL-redacted.
- Data in `%LOCALAPPDATA%\CalendarBell\` (override `CALENDARBELL_DATA_DIR` for tests); install in `%LOCALAPPDATA%\Programs\CalendarBell\`.
- Privacy mode on by default: toasts show calendar name, availability and times only; SUMMARY not persisted when on.
- Only fictitious names (`Calendar A`, `Calendar B`) in fixtures, docs, tests, commits.

## Decisions taken where the spec left a choice (Rulings)

1. **TFMs:** Core `net10.0`; Infrastructure `net10.0-windows`; App `net10.0-windows10.0.19041.0` (Windows App SDK needs a versioned Windows TFM). Spec says `net10.0-windows`; this is the closest valid value.
2. **Windows App SDK package:** reference `Microsoft.WindowsAppSDK.Foundation` 2.7.0 only (contains AppNotifications); the metapackage drags WinUI/AI/ML. Self-contained + `WindowsPackageType=None`.
3. **Toast adapter lives in the App project** (`src/CalendarBell.App/Notifications/WindowsToastService.cs`), not Infrastructure, so test hosts never load the Windows App SDK bootstrap. Toast XML generation is pure and lives in Core where it is unit-tested.
4. **`TimeProvider` replaces `IClock`** (the spec names both; TimeProvider is the framework abstraction and FakeTimeProvider drives tests).
5. **Stale snooze choice from a toast:** if the chosen target is no longer future when clicked, the next later future target is used (10→5→0); if none remains, nothing is scheduled. The scheduler API itself rejects past targets (T04).
6. **Pause:** reminders that fall due while paused are consumed silently (marked sent) so resuming never replays them.
7. **Feeds stale > 24 h:** reminders from that calendar are held (not fired, not consumed) and the tray shows `Sync warning`; they fire late if sync recovers before start.
8. **Batching:** everything that is queued when an emission slot opens is merged into one toast; same start (±60 s) → group toast with group Snooze/Dismiss; otherwise summary toast (`View reminders` / `Dismiss all`).
9. **Toast identity:** tag = batch id (12 hex), group = `reminders` (`test` for test toasts). Batch membership persisted in SQLite so actions work after restart/relaunch. Arguments carry only the batch id.
10. **At-most-once emission:** state is persisted as sent before `Show`; on start-up active toasts are reconciled (`GetAllAsync`) and stale ones removed.
11. **Installer:** Inno Setup is not available on the build machine. The app has a built-in per-user installer (run from the extracted ZIP → "Install CalendarBell" dialog; `--uninstall` from Apps & Features). `installer/CalendarBell.iss` is shipped as source and `scripts/publish.ps1` compiles it when `ISCC.exe` exists.
12. **Startup:** Task Scheduler logon task (LeastPrivilege, InteractiveToken, no time limit) via the `Schedule.Service` COM API; if registration is refused, fall back to `HKCU\...\Run`. Only one method active at a time.
13. **Availability mapping order:** `X-MICROSOFT-CDO-BUSYSTATUS` → `STATUS:TENTATIVE` → SUMMARY equal to Busy/Tentative/Out of Office → explicit `TRANSP:OPAQUE` (Busy) → Unknown ("Meeting"). Free (TRANSP:TRANSPARENT, BUSYSTATUS FREE, SUMMARY "Free") is ignored.
14. **OccurrenceKey:** SHA-256 of `calendarId | UID | RECURRENCE-ID(UTC or "")`, 32 hex chars; UID is never stored or shown.
15. **Localisation:** `Strings.resx` (en) + `Strings.es-AR.resx`; UI language comes from `settings.json` `language` (default `en-US`), so the initial UI is English regardless of OS language.
16. **Test framework:** xUnit v3.

## Review Focus

1. **Toast clicked long after it appeared** (e.g. group toast left on screen past T−10): chosen target already past → fallback rule 5; never an immediate re-alert. Test in Task 3 (`Snooze_from_toast_with_stale_target_uses_next_future_target`).
2. **Clock jumps / sleep while a timer is armed:** timer due computed from relative delay must be re-armed on resume/time change and by a 60 s drift check. Test in Task 3 (`Recalculate_after_clock_jump_fires_late_or_expires`).
3. **Feed returns 200 with HTML login page** (expired published link): must be `Invalid calendar`, cache kept. Test in Task 6 (`Html_body_is_invalid_and_keeps_cache`).
4. **Outlook custom TZIDs** like `(UTC+01:00) Amsterdam, Berlin...` with an embedded VTIMEZONE: must resolve via Windows display names, not fall back to UTC. Test in Task 5 (`Outlook_display_name_tzid_resolves`).
5. **Calendar renamed while a toast is visible**: same CalendarId, no re-alert, new name used on next toast. Test in Task 3 (T30).

---

### Task 1: Solution scaffold, build settings, icons

**Files:** `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `CalendarBell.sln`, `src/*/*.csproj`, `tests/*/*.csproj`, `tools/CalendarBell.MockFeedServer`, `scripts/generate-icons.ps1`, `src/CalendarBell.App/Resources/Icons/*.ico|png`, `.gitignore`, `.editorconfig`.

- [ ] global.json pins SDK `10.0.404` (`rollForward: latestFeature`).
- [ ] Central package management with exact versions; `RestorePackagesWithLockFile=true`.
- [ ] App csproj: `WinExe`, `UseWPF`, `UseWindowsForms` (remove global usings for `System.Windows.Forms`/`System.Drawing`), `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, `SelfContained=true`, `RuntimeIdentifier=win-x64`, `ApplicationManifest=app.manifest` (asInvoker, PerMonitorV2, Win11 supportedOS), custom `Program.Main` (App.xaml as Page).
- [ ] Icons: bell glyph drawn with GDI+ in `scripts/generate-icons.ps1` → `calendarbell.ico`, `calendarbell-warning.ico`, `calendarbell-paused.ico` (16–256 px) + `calendarbell.png` (256) for notification registration.
- [ ] Verify: `dotnet build -c Release` → 0 errors. Commit.

### Task 2: Core models, state machine (pure functions)

**Files:** `src/CalendarBell.Core/Models/*.cs`, `Services/ReminderStateMachine.cs`, `Services/SnoozeTargets.cs`; tests `tests/CalendarBell.Core.Tests/ReminderStateMachineTests.cs`.

**Produces:**
- `record CalendarSource(Guid CalendarId, string Name, string EncryptedUrl, bool Enabled)`
- `record EventOccurrence(Guid CalendarId, string Uid, string OccurrenceKey, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Availability, bool IsAllDay, bool IsCancelled, string? PrivacySafeTitle, string RevisionFingerprint)`
- `record ReminderRecord(...)` exactly as spec §6.2 plus `int? SnoozeMinutesBefore`.
- `enum ReminderPhase { Initial, Snoozed, Late, StartingNow }`, `enum ReminderStatus { Pending, Notified, Snoozed, Dismissed, Cancelled, Expired }`
- `static ReminderStateMachine`: `DueUtc(ReminderRecord, now) → DateTimeOffset?`, `Status(ReminderRecord, now)`, `IsExpired`, `ApplySnooze`, `ApplyDismiss`, `ApplyFired`, `ApplyStartChanged`, `ApplyCancelled`.
- `static SnoozeTargets.Available(DateTimeOffset start, DateTimeOffset now) → IReadOnlyList<int>` (subset of 10,5,0 with `start − m > now + 2 s`).

- [ ] Tests T01, T03–T06 (target arithmetic), T18 (expired at T+10), late at T−12, starting-now at T+30 s, none at T+60 s.
- [ ] Implement; run `dotnet test tests/CalendarBell.Core.Tests`; commit.

### Task 3: ReminderScheduler + store contract

**Files:** `Core/Interfaces/IReminderStore.cs`, `Core/Interfaces/IReminderScheduler.cs`, `Core/Services/ReminderScheduler.cs`, test fake `tests/CalendarBell.Core.Tests/Fakes/InMemoryReminderStore.cs`, tests `ReminderSchedulerTests.cs`.

**Produces:** `IReminderScheduler` (spec §7.3 plus `StartAsync`, `RemoveCalendarAsync`, `UpdateCalendarState(Guid id, string name, bool enabled, DateTimeOffset? lastSuccessUtc)`, `SetPausedUntil(DateTimeOffset?)`, `RecalculateAsync`, `GetUpcoming(from,to)`, `RecordToastAsync(keys, tag, group)`, `event Changed`). `IReminderStore` loads/saves records + occurrences + batches transactionally. Due items are delivered to `IReminderSink.EnqueueAsync(IReadOnlyList<DueReminder>)`.

- [ ] Tests (FakeTimeProvider): T01, T02 (sync at 10:14 and 10:16 does not move the 10:15 alert), T03–T08, T13, T14 (same occurrences twice → no change), T15, T16, T17, T18, T19 (DST: Europe/Berlin and America/New_York transitions), T29, T30, T31, T34 (fires within 2 s of due), restart after dismiss/snooze (new scheduler over same store), Review Focus 1, 2, 5, stale calendar held.
- [ ] Implement single armed `ITimer` to earliest due; coalesce `due ≤ now+1 s`; persist before sink; 60 s drift timer.
- [ ] Run tests; commit.

### Task 4: Batching, orchestrator, toast XML

**Files:** `Core/Notifications/NotificationBatcher.cs`, `NotificationBatch.cs`, `NotificationOrchestrator.cs`, `ToastXmlBuilder.cs`, `ToastArguments.cs`, `DisplayText.cs`, `Core/Resources/Strings.resx` (+ `es-AR`); tests `NotificationBatcherTests.cs`, `NotificationOrchestratorTests.cs`, `ToastXmlBuilderTests.cs`.

**Produces:** `INotificationPresenter { ShowAsync(NotificationBatch, ct); RemoveAsync(tag, group, ct); }`, `NotificationOrchestrator.HandleActionAsync(ToastAction action, CancellationToken)`, `event OpenUpcomingRequested(IReadOnlyList<string> keys)`.

- [ ] Tests: single toast copy (`Calendar A · Meeting in 15 min`, `Busy · 10:30–11:00`), snoozed copy (`in 5 min`, combo shows only `At start`), starting-now copy (Dismiss only), T09, T10, T11, T12 (100 items → one summary, XML < 5 KB, two lines + `+98 more`), T28 (`<audio silent="true"/>`), T33 (`<`, `&`, quotes escaped, XML parses), rate limit 8 s, queued item dismissed before slot is dropped, toast-action fallback (Review Focus 1).
- [ ] Implement; run tests; commit.

### Task 5: ICS parser

**Files:** `Infrastructure/Ics/IcsCalendarParser.cs`, `Ics/TimeZoneResolution.cs`, `Ics/AvailabilityMapper.cs`, `fixtures/*.ics`; tests `tests/CalendarBell.Infrastructure.Tests/IcsCalendarParserTests.cs`.

- [ ] Fixtures (Calendar A/B, synthetic UIDs): simple, recurring weekly with EXDATE (T23), RECURRENCE-ID override moving one instance (T24), cancelled instance, all-day/transparent/cancelled (T25), TZID + VTIMEZONE Outlook style (Review Focus 4), floating time, no-UID event, same UID in two calendars (T13), invalid/HTML body (T21), truncated body (no END:VCALENDAR), Busy/Tentative/OOF BUSYSTATUS.
- [ ] Parser throws `IcsFormatException` for invalid/truncated content; enforces window + 100,000 cap.
- [ ] Run tests; commit.

### Task 6: HTTP feed client + sync service

**Files:** `Infrastructure/Sync/CalendarFeedClient.cs`, `UrlPolicy.cs`, `CalendarSyncService.cs`, `BackoffPolicy.cs`, `Security/LogSanitizer.cs`, `Security/DpapiUrlProtector.cs`; tests with a fake `HttpMessageHandler`.

- [ ] Tests: 200/304 with ETag+Last-Modified round-trip (T14), 401/403/404/429(Retry-After)/500/timeout statuses, 10 MiB cap, redirect to http rejected, >3 redirects rejected, invalid body keeps cache (T21, Review Focus 3), URL down keeps cache and other calendar still syncs (T20), 3 concurrent refreshes → one request (T22), stale >24 h flagged (T36), exception text never contains the URL (T27), backoff sequence 2/4/8/16/30.
- [ ] Run tests; commit.

### Task 7: SQLite storage + settings

**Files:** `Infrastructure/Storage/CalendarBellDatabase.cs` (migrations, WAL), `SqliteReminderStore.cs`, `SqliteCalendarRepository.cs`, `SqliteSyncStateStore.cs`, `SettingsStore.cs`, `DataPaths.cs`; tests.

- [ ] Tests: schema v1 created; records/occurrences/batches round-trip; restart keeps snooze/dismiss (T07); DB file and settings.json contain no plain URL (T26); privacy mode stores no title; corrupt DB → `StorageCorruptedException`, original preserved as `.corrupt-<ts>` copy; settings atomic write.
- [ ] Run tests; commit.

### Task 8: Windows integration services

**Files:** `Infrastructure/Windows/StartupRegistration.cs`, `SingleInstance.cs` (mutex `Global\CalendarBell-<SID>` + ACL'd named pipe), `SystemEventsMonitor.cs`, `NotificationQuietState.cs`, `Install/PerUserInstaller.cs`, `Logging/FileLoggerProvider.cs`.

- [ ] Tests: two `SingleInstance` objects in one test → second is secondary and its message reaches the first (T32); logger rotation + redaction; startup task XML has `LeastPrivilege`/`InteractiveToken`/`PT0S`.
- [ ] Run tests; commit.

### Task 9: App shell — tray, notifications, windows

**Files:** `src/CalendarBell.App/**` (Program, App, AppHost, TrayIconHost, WindowsToastService, Views + ViewModels for Settings, CalendarEditor, Upcoming, About/Diagnostics, Install/Uninstall dialogs, Styles).

- [ ] Tray menu exactly as spec §2.4; tooltip states; icon states.
- [ ] Settings layout §3.2, editor validation §3.2, Test connection (cancelable), Remove confirmation, banners (notifications disabled, elevated, storage error).
- [ ] Upcoming §3.3 with filters, highlight, per-item actions.
- [ ] Manual + scripted verification on this Windows 11 machine (screenshots); commit.

### Task 10: Release, installer, docs, final verification

**Files:** `scripts/build.ps1`, `test.ps1`, `publish.ps1`, `install-dev.ps1`, `installer/CalendarBell.iss`, `tests/CalendarBell.Windows.SmokeTests`, `README.md`, `AGENTS.md`, `docs/ARCHITECTURE.md`, `docs/TESTING.md`.

- [ ] Publish `dist/CalendarBell-win-x64/` + zip; smoke tests (GUI subsystem, single instance, sync against mock server, no URL in logs, real toast shown and actioned via UI Automation).
- [ ] Code review (superpowers:requesting-code-review), fix, `dotnet build -c Release` + `dotnet test -c Release` with real counts in TESTING.md; commit.
