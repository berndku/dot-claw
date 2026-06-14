<!-- Heartbeat tasks. Lines that are not comments/headings are read on every tick, so keep this short. -->
<!-- To pause heartbeat API calls, reduce this file to comments and headings only. -->

# Heartbeat tasks

## Memory safety-net (every tick — keep it cheap)

Review the recent conversation for durable facts about the user — name, where they live, nationality,
timezone, pronouns, preferences, important people or projects — or decisions worth keeping, that are NOT
yet recorded in USER.md / MEMORY.md.

- If you find something new and unsaved: read the target file (USER.md for profile facts, MEMORY.md for
  everything else), merge it in with write_file, then reply with exactly HEARTBEAT_OK.
- If nothing is new and unsaved: reply with exactly HEARTBEAT_OK and do nothing else.

Never message the user from this check — persist silently and acknowledge with HEARTBEAT_OK.
