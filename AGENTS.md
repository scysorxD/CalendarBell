# AGENTS.md — rules for AI agents and contributors

`CalendarBell_SPEC_GENERICA.md` is the product contract. `docs/ARCHITECTURE.md` explains how it is implemented;
`docs/superpowers/plans/2026-11-18-calendarbell.md` lists the decisions taken where the spec left a choice.

## Non-negotiables

* **No real calendar data.** Fixtures, tests, docs, screenshots and commits use only fictitious calendars
  (`Calendar A`, `Calendar B`, `*.example` domains). Never commit a real ICS URL, UID, person or organization name.
* **Calendar URLs are secrets.** Never log, print, show or persist a URL in clear text. Store only DPAPI ciphertext
  (`IUrlProtector`). Error messages shown to users or written to logs go through `LogSanitizer.Redact` /
  `LogSanitizer.Describe`; never pass raw `HttpRequestException` messages to the UI.
* **Logs**: calendar name/ID, error type, HTTP status and duration only. Never titles/SUMMARY, descriptions,
  attendees, UIDs or query strings. The file logger redacts URLs as a second line of defense — not as the first.
* **Never run elevated, never a Windows Service, never a console.** Startup goes through `StartupRegistration`
  (least privilege). The app is `WinExe`.
* **Absolute snooze semantics.** `minutesBeforeStart` is resolved against each occurrence's own start. Do not use
  Windows' system snooze. Never offer or schedule a target in the past.
* **No new dependencies** for telemetry, crash reporting or UI frameworks. Package versions are pinned in
  `Directory.Packages.props` with lock files.

## Code conventions

* C# latest, nullable enabled, warnings as errors, .NET analyzers on, file-scoped namespaces.
* Time: always `DateTimeOffset` in UTC internally and an injected `TimeProvider`; convert to local only for display
  (`DisplayFormatter`). Never call `DateTime.Now` in Core/Infrastructure logic.
* Core stays free of WPF, WinForms, Windows App SDK and Win32. Windows-specific code lives in Infrastructure; the
  Windows App SDK only in the App project.
* Pure rules go to `ReminderStateMachine` / `SnoozeTargets`; side effects to the scheduler/orchestrator.
* Treat ICS text as untrusted: build toast XML only with `XElement` (`ToastXmlBuilder`), sanitize with
  `NotificationText.Sanitize`, keep toast arguments to the whitelisted `action=…&b=<12 hex>` format.
* UI text comes from `scripts/strings.json` → `scripts/resx.ps1` → `Strings.resx` / `Strings.es-AR.resx`
  (`{l:Loc Key}` in XAML, `Strings.Get/Format` in code). Use only Fluent theme resources (`DynamicResource`).

## Process

* **Tests first** (xUnit v3): write the failing test, watch it fail for the right reason, then implement. Bugs get a
  reproducing test before the fix.
* Never mutate global state (e.g. `Strings.Culture`) in tests: test classes run in parallel.
* Time-based tests use `FakeTimeProvider` and advance in small steps (`FakeClock.StepToAsync`) rather than big
  jumps.
* Before claiming completion: `dotnet build -c Release` (0 warnings) and `dotnet test -c Release` (all green); for
  notification/activation/installer changes also run the Windows smoke tests (`./scripts/test.ps1 -Smoke`) after
  `./scripts/publish.ps1`, and report the real numbers.
* Changes to notifications, storage schema, security or startup registration need a code review before merge.
  Schema changes add a new migration step in `CalendarBellDatabase` (never edit an applied one).
