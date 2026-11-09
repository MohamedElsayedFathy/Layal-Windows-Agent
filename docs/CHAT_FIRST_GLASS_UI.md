# Layal — chat-first glass interface

> **Historical design note:** This document records the earlier chat-first glass pass. Its floating bottom dock was superseded by the persistent, collapsible left navigation rail in the 0.5.8 workspace redesign. Use `SOFT_AURORA_DESIGN_SYSTEM_AR.md` as the current visual source of truth.

## Primary experience

- The main window opens directly into the conversation. The chat page is the actual **Home** destination, not a separate dashboard that happens to open first.
- The former dashboard remains available as **Overview**.
- Current mainline uses a persistent left navigation rail. The old bottom dock described by the original design pass is no longer current.
- The current rail starts expanded, groups workspace and personal tools, and can collapse to a compact icon rail.
- The dock follows Arabic/English direction and exposes localized accessible names. Keyboard shortcuts are Ctrl+1 for Chat/Home, Ctrl+2 for Overview, and Ctrl+3 through Ctrl+8 for the remaining sections.

## Conversation layout

- The conversation occupies the available width by default.
- Saved conversations live in a separate side panel that starts closed and can be opened or closed from the chat header.
- The list uses the existing encrypted chat-session service and selection handler; creating, switching, copying, clearing, and private-chat protections retain their existing business logic.
- The clock, date, weather, provider status, attachments, voice controls, and message composer remain part of the primary chat surface.

## Visual system — Soft Aurora Workspace

The adopted visual direction is **Soft Aurora Workspace**, combining calm neutral light surfaces, restrained blue-indigo accents, and a clear workspace hierarchy. Current colors and measurements are defined in `SOFT_AURORA_DESIGN_SYSTEM_AR.md`.

- The default light palette uses neutral cool backgrounds, white/pearl surfaces, deep readable navy text, indigo primary actions, and restrained lavender/cyan support accents.
- The dark palette follows the same hierarchy with deep navy surfaces and light readable text; both modes remain switchable and persisted through the existing settings store.
- App-owned glass surfaces remain nearly opaque, with visible quiet borders and low-depth shadows. Ambient blue/violet/pink color is restrained to background decoration and never competes with content.
- Shared WPF buttons, text fields, password fields, combo boxes, tooltips, context menus, and popup surfaces use consistent corner radii and theme resources.
- Auxiliary windows use the shared palette and chrome. Character-specific colors in the floating Layal mascot remain intentionally distinct to preserve the character identity.
- The floating navigation dock remains the navigation model; its shape and surface are refined without changing its eight destinations, automation identifiers, or keyboard routes.
- On supported Windows 11 builds, the main window additionally requests the native DWM transient system backdrop. Unsupported Windows versions fall back to app-owned WPF surfaces.
- Windows High Contrast overrides the native backdrop, translucent wash, and decorative effects with system colors. UI motion also respects Windows client-area animation preferences and the app performance mode.
- See [Soft Aurora design system](SOFT_AURORA_DESIGN_SYSTEM_AR.md) for the shared visual rules and acceptance scope.

## Transient surfaces and auxiliary windows

- WPF context menus, menu items, tooltips, ComboBox drop-downs, and the DatePicker calendar use the same theme-aware glass palette, borders, hover states, and keyboard focus treatment as the main shell.
- Chat-message and floating-mascot context menus localize their direction and labels for RTL/LTR.
- App-owned consent and confirmation prompts use one shared glass dialog surface. This includes privacy, microphone, camera, screen capture, clipboard, UI inspection, Gmail, memory, export, delete, and workspace confirmation paths. Safe defaults remain Cancel/No for destructive or privacy-sensitive choices.
- Agent-action approval and saved-routine confirmation windows use the same glass window chrome. Auxiliary windows refresh their backdrop when the app theme or Windows accessibility palette changes.
- The notification-area context menu follows the selected language and light/dark/High Contrast palette.
- OS-owned permission prompts and Windows shell file/folder pickers remain native Windows UI by design; Layal styles its own consent prompt before invoking a Windows permission request rather than replacing the OS dialog.

## Scope and regression protection

- This is a shell and visual-layout update. It does not claim to complete missing AI providers, browser automation, camera streaming, or other unfinished features.
- Existing message sending, saved sessions, voice, weather, attachments, tasks, skills, memory, API integrations, and settings handlers remain attached to their existing controls.
- Windows visual smoke tests render all eight sections in both themes and languages, verify the home route and RTL panel placement, and exercise the dock and chat-history panel.
