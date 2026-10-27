# Layal 0.5.5 — chat-first glass interface

## What changed

- The app opens directly into the conversation instead of the legacy dashboard.
- The live clock, date, current temperature, weather summary, location, update status, and refresh action appear in the main chat surface. The values continue to come from the existing clock and Open-Meteo/location services; no fake weather values are added.
- The home dashboard, settings, tasks, skills, memory, camera, and API integration areas remain available from the glass navigation rail.
- The clock/weather card uses the translucent glass palette so the backdrop and panel borders remain visually consistent with the rest of the shell.
- A Windows visual smoke assertion protects the default Chat tab from being accidentally replaced by the home dashboard in a future build.

## Installation note

This source update must be built by the Windows CI workflow before the new installer/distribution can be downloaded. An already-installed copy will not change its interface in place; download and install the artifact built from this update.
