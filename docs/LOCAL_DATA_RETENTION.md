# Local data retention policy

Layal stores chat sessions, saved memory notes, and task history locally. Chat and memory state are encrypted with Windows DPAPI for the current Windows user. Retention limits keep the encrypted state files from growing without bound.

## Chat history

- At most **50 saved conversations** are retained. When the limit is exceeded, the oldest non-current conversations are removed first; the active conversation is preserved.
- Each conversation retains at most **200 messages**, **16,000 characters per message**, and **100,000 characters across all retained messages**.
- When message or character limits are reached, older messages are removed first. A message that exceeds the per-message cap is truncated in the persisted copy and ends with `…[truncated]` when the limit permits. The in-memory message object passed to the save operation is not mutated by this truncation.
- Conversation titles are limited to 120 characters.

## Saved memory

- At most **100 memory notes** are retained.
- Each note title is limited to 120 characters and each note body to 12,000 characters.
- When older existing memory files exceed the limit, the oldest notes are removed on access and the bounded list is saved back.

## Task history

Task history already retains at most 100 task records. This policy does not change task record or event-size limits.

## What to expect

These are automatic retention limits, not an archive. Data beyond the limits can be removed from the saved Layal history; export important memory notes before large-scale use. The limits are applied during normal saves and when older saved state is first loaded. Clearing chat history continues to delete all saved conversations and create a blank current conversation.

The limits described here are local application behavior; they do not promise secure deletion of historical copies from backups, filesystem snapshots, or exported files.
