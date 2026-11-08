# Layal — chat-first glass interface

## Primary experience

- The main window opens directly into the conversation. The chat page is the actual **Home** destination, not a separate dashboard that happens to open first.
- The former dashboard remains available as **Overview**.
- The content host has no permanent navigation rail. All eight existing sections remain available through the floating bottom dock.
- The dock stays compact and visible at the bottom center. Pressing its menu control expands the labeled destinations; pressing it again collapses them.
- The dock follows Arabic/English direction and exposes localized accessible names. Keyboard shortcuts are Ctrl+1 for Chat/Home, Ctrl+2 for Overview, and Ctrl+3 through Ctrl+8 for the remaining sections.

## Conversation layout

- The conversation occupies the available width by default.
- Saved conversations live in a separate side panel that starts closed and can be opened or closed from the chat header.
- The list uses the existing encrypted chat-session service and selection handler; creating, switching, copying, clearing, and private-chat protections retain their existing business logic.
- The clock, date, weather, provider status, attachments, voice controls, and message composer remain part of the primary chat surface.

## Visual system

- Light and dark appearance remain switchable from the top bar and are persisted by the existing settings store.
- Glass surfaces use translucent fills, visible borders, soft shadows, and the app's ambient color wash.
- On supported Windows 11 builds, the main window additionally requests the native DWM transient system backdrop so the desktop surface can contribute behind translucent app panels.
- Unsupported Windows versions fall back to the WPF translucent wash and app-owned glass surfaces. A native backdrop failure must never prevent startup.
- Windows High Contrast overrides the native backdrop, translucent wash, and decorative effects with system colors. UI motion also respects Windows client-area animation preferences and the app performance mode.

## Scope and regression protection

- This is a shell and visual-layout update. It does not claim to complete missing AI providers, browser automation, camera streaming, or other unfinished features.
- Existing message sending, saved sessions, voice, weather, attachments, tasks, skills, memory, API integrations, and settings handlers remain attached to their existing controls.
- Windows visual smoke tests render all eight sections in both themes and languages, verify the home route and RTL panel placement, and exercise the dock and chat-history panel.
