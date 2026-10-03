<!-- Imported from the project's private planning notes on 2026-10-02. Machine paths, hostnames,
     and personal identifiers were removed; nothing else was changed. See ../how-this-was-built.md. -->

# UI design notes — Phase 5

_Written 2026-09-07 before implementing the Components RCL. The reference is the owner's ASA
wallpaper: pure black void, a thin cyan planet-rim arc, a chrome triangle with amber circuit
traces, a red left arm tip and a green right arm tip._

## Subject

An operator's console for a handful of ARK: Survival Ascended dedicated servers on one Windows
box, used by one person from a browser on the LAN. Its job: know at a glance which instances are
up, start/stop/restart them safely, read their console, and change configuration without
breaking a running world. It is a tool, not a landing page: dense, left-anchored, quiet.

## Tokens

### Color (dark only, taken from the reference)

| Token | Hex | Role |
| --- | --- | --- |
| `--ark-void` | `#05080b` | page ground (the wallpaper's black, one notch up with a cold cast) |
| `--ark-basalt` | `#0d141a` | panels, inputs, rail hover |
| `--ark-basalt-2` | `#131c24` | raised inputs, row hover |
| `--ark-hairline` | `#1e2b35` | borders; the only structural line |
| `--ark-steel` | `#c6d1d9` | body text |
| `--ark-steel-2` | `#7c8b97` | secondary text |
| `--ark-steel-3` | `#4a5864` | disabled / placeholder |
| `--ark-rim` | `#38c6ff` | the planet rim: primary action, links, focus, selection |
| `--ark-trace` | `#f0a53a` | the amber circuit trace: in-transition states, warnings |
| `--ark-arm-green` | `#8ccc4d` | the right arm tip: Running |
| `--ark-arm-red` | `#d9452f` | the left arm tip: Unreachable / failed / destructive |

State → color is one-to-one and never decorative:
Running = green, Starting/Stopping/Updating/StartingUnconfirmed = amber, Stopped = steel-3 hollow,
Unreachable/Unknown/IdentityUnpersisted = red, Ready(readiness) = rim.

### Type

- **Saira** (variable 300–700, self-hosted latin subset) for all UI text. Wide, squarish
  forms echo the ASA wordmark without imitating it. Headings 500 with `-0.01em` tracking,
  body 400, instance names 600. Tabular numerals for ports, timers, counts.
- **JetBrains Mono** only where the content is a terminal or a file: console panel, INI
  editors, command lines. Never for labels.
- Scale (×1.2): 12 / 13 / 14 body / 16 / 20 / 26 / 34. Body line-height 1.5, mono 1.45.
- Line length under 80ch for prose (help text, empty states).

### Layout

```
┌────────┬──────────────────────────────────────────────────────┐
│ ▲ ARK  │ ▬▬▬▬▬▬▬▬▬▬ horizon rule (rim → transparent) ▬▬▬▬▬▬▬▬ │
│ admin  │ Instances                          [+ New instance]   │
│        │                                                       │
│ Instan │ Survivors cluster ─────────────── 2 of 3 up  ▶ all ■ all│
│ Cluste │ ▲ Island        The Island   7777 · 27020  Running  ▶ ■ ↻ ⧉│
│ Mods   │ ▲ Scorched      Scorched E.  7779 · 27021  Stopped  ▶ ■ ↻ ⧉│
│ Player │                                                       │
│ Maps   │ Standalone ────────────────────────────────────────── │
│ Settin │ ▲ Sandbox       Aberration   7781 · 27022  Starting …  │
│        │                                                       │
│────────│                                                       │
│ system │                                                       │
│ ● Ready│                                                       │
│ Update │                                                       │
└────────┴──────────────────────────────────────────────────────┘
```

- Left rail, 224 px, on `--ark-void`; the content area is `--ark-basalt`-tinted only where a
  panel needs it, the page itself stays on void. The rail has no border: the mark and the
  luminance of the hover states make the edge.
- No global header bar. Page titles live in the content; the rail carries identity and, at its
  foot, the system block (readiness, maintenance phase, Update game).
- **The horizon rule** is the one memorable device: a 1 px gradient line across the top of the
  content, rim-cyan fading to transparent. It is also functional: it turns amber and sweeps
  while a maintenance phase is active, red when readiness has failed. Nothing else animates
  without user action except the Starting lamp's slow pulse (both honor reduced motion).
- **The lamp**: a 10 px triangle (the logo silhouette) in the state color, glowing when Running,
  hollow when Stopped. Used in the instance list, the detail header, and the rail's system block.
- Panels (bordered surfaces on basalt) only for real containers: console, editors, forms,
  dialogs. Lists are rows separated by hairlines, grouped under sentence-case section heads.
- Corner radius 2 px everywhere; no drop shadows; elevation is luminance.
- Everything left-aligned; numbers right-aligned in tables. Content max-width 1320 px, anchored
  left. Collapses to a top rail under 900 px.

### Copy

Sentence case, plain verbs that stay the same through the flow: Start / Stop / Restart /
Back up now / Save Game.ini / Delete instance. Errors say what happened and what to do. Empty
states invite the next action ("No instances yet. Create one to start a server.").

## Radzen

Radzen stays for inputs, dropdowns, tabs, dialogs, notifications, the sortable mod list, and the
data grid on the backups/players pages. `standard-dark` is the base; `ark.css` overrides the
`--rz-*` root tokens (primary, base scale, fonts, radius, body background) so Radzen widgets
inherit the palette instead of being restyled one by one. Layout, the instance list, the lamp,
the console panel, and the rail are plain HTML/CSS.

## Self-critique against the generic defaults

- Black + one acid accent: no — four semantic colors, each traced to a feature of the logo.
- SaaS card kit: no cards except real containers; rows and hairlines carry the grouping.
- Eyebrow labels / all caps / middle-dot meta strings / mono data labels: none.
- Header + sidebar clone: no header; identity and system status live in the rail.
- The hero is the instance list with its lamps, not a stat-tile row.

## Implemented 2026-09-08

Landed as planned. Two things moved during the build: the tri-state "Default" position uses the
steel fill and only "Inherit" (clustered instances) uses the rim, because nine cyan blocks on a
standalone page were louder than the horizon; and the console hides its own clock on lines that
already carry the game's bracketed timestamp. Screenshots of every page were reviewed at 1440×900.
