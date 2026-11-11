# Asset license record

Decision 3 (amended 2026-11-10): beta art is code-built in this repository. Third-party packs are allowed
only for neutral props, only under CC0, and only with a row in this file. Generative-model (AI image or 3D)
assets are not allowed.

## Code-built assets

| Assets | Made from | Owner | License | Date |
|---|---|---|---|---|
| Base avatar (rig `vybe-rig-1`, 2 body presets, 4 faces, 4 preset hair styles, idle loop), 34 catalog meshes, 5 emotes, 43 thumbnails: everything in `assets/published/3d/` | Python scripts in `assets/source/` (geometry in plain Python, export and render with Blender 4.5 LTS) | The product owner of VYBE | All rights reserved (proprietary). Served to players as part of the game | 2026-11-10 |
| Catalog version 3: 5 avatar meshes (aurora puffer, neon joggers, bucket hat, headband, laser shades), 1 emote (jump), 13 thumbnails | The same scripts (`catalog.py`, `wardrobe.py`, `emotes.py`) | The product owner of VYBE | All rights reserved (proprietary) | 2026-11-11 |
| My Room: room shell, 7 finish meshes, 23 furniture and wall-decor meshes, 31 thumbnails (`assets/published/3d/room/`, `room.v1.json`) | Python scripts in `assets/source/vybe_art/room.py` (same pipeline) | The product owner of VYBE | All rights reserved (proprietary) | 2026-11-11 |

Blender (GPL) is a tool only. The files that Blender makes from your own scripts and data belong to you;
the GPL does not apply to them.

No generative-model tool made or changed any of these assets.

## Third-party assets

| Asset | Source URL | Author | License | Date | Allowed use |
|---|---|---|---|---|---|
| (none) | | | | | |

Add one row for each third-party file before you commit it. Only CC0 is accepted for neutral props
(court, ball, background) without a counsel review.
