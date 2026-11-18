# Test fixtures

Synthetic iCalendar files used by `tests/CalendarBell.Infrastructure.Tests` and by the mock feed server.
They only contain the fictitious calendars **Calendar A** and **Calendar B** (`*.example` domains,
made-up UIDs) and never real URLs, people or organizations.

| File | Purpose |
|---|---|
| `calendar-a-basic.ics` | Busy / Tentative / OOF / free / all-day / cancelled / invalid interval / no UID / past events |
| `calendar-a-recurring.ics` | Weekly series across a DST change with EXDATE, a moved instance (RECURRENCE-ID) and a cancelled instance |
| `calendar-b-outlook-tz.ics` | Outlook-style display-name and "Customized Time Zone" TZIDs, a Windows TZID, and a UID shared with Calendar A |
| `floating.ics` | Floating times (no TZID, no Z), interpreted in the local zone |
| `dst-berlin.ics` | Daily series across the October DST change in Europe/Berlin |
| `empty-valid.ics` | A valid calendar with no events |
| `invalid-html.ics` | An HTML sign-in page served with 200 OK |
| `truncated.ics` | Body cut before `END:VCALENDAR` |
| `many-instances.ics` | Unbounded MINUTELY rule, to exercise the expansion cap |
| `folded.ics` | Folded (continued) content lines and an event without UID |
