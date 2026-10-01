# Agent Quota Tracker mark

The mark is an open, square cycle: the long ink segment is used quota and the
short sage segment is the remaining allowance. Its gap makes the state feel
finite without borrowing the usual speedometer, robot, sparkle, or lettermark
language.

## Files

- `aq-tracker-mark.svg` — primary transparent mark for light surfaces.
- `aq-tracker-mark-dark.svg` — light mark for dark surfaces.
- `aq-tracker-mark-mono.svg` — single-ink fallback for constrained contexts.
- `aq-tracker-app-icon.svg` — porcelain-tile application/installer source.
- `png/` and `aq-tracker.ico` — generated derivatives; regenerate with
  `scripts/export-brand-assets.ps1` when the source changes. The exporter uses
  Windows `System.Drawing` and mirrors the source SVG's geometry.

The 64-unit master uses a 10-unit stroke so its narrowest visible feature is
2.5 px at 16 px. The shape is intentionally free of fine detail and remains
recognizable at tray sizes.

## Authorship and license

This geometry was created specifically for Agent Quota Tracker on 2026-08-12. It is
original work, contains no third-party logo or icon source, and is distributed
under the repository's MIT license.
