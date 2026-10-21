# Project Goal — PTCGL Auto Battle Log

## Mission
Automatically preserve the battle log of each Pokémon TCG Live match on the player's Windows computer, removing the need to manually export and organize every match while keeping the process transparent and user-controlled.

## User Value
After a match, the player should have a readable, correctly named log that can be searched, reviewed, backed up, or used for personal analysis. The app should run quietly in the background and avoid interrupting gameplay or requiring the user to click an export control for each match.

## Product Scope
Provide a Windows 10/11 utility that detects completed matches and obtains the same log text exposed by the game's own export feature. Organize output files consistently, identify matches using available match metadata, avoid duplicates, and provide clear controls and status for automatic capture. Any account identifiers used for indexing should be minimized and protected, such as through hashing where appropriate.

## Quality Principles
- Do not automate gameplay, alter match outcomes, or interfere with competitive decisions.
- Save logs atomically and avoid data loss when the game exits or the app restarts.
- Handle missing, partial, duplicate, or malformed logs safely.
- Minimize stored personal data and explain what metadata is retained.
- Keep background behavior lightweight, observable, and easy to disable.
- Clearly communicate unofficial status, terms-of-service uncertainty, and user responsibility.

## Success Criteria
- Completed matches are saved without manual export steps under normal supported conditions.
- Each saved log can be matched to the correct game and is not silently overwritten.
- Failures and unsupported game changes are reported clearly.
- The application starts, stops, and recovers reliably on supported Windows versions.
- Releases are reproducible, tests cover parsing and file handling, and the privacy and risk disclosures remain accurate.

## Boundaries
This tool captures information the game already exposes to the player; it is not a gameplay bot, a competitive advantage engine, or an official Pokémon product. Compatibility may change with game updates, and users must decide whether installing it is acceptable under the game's terms.