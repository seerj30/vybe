# Decision 3: 3D art direction, licensing and asset production ownership

- Spec: [§9 Avatar 3D asset contract](../spec/VYBE_Technical_Specification_v1.3.md#9-avatar-3d-asset-contract), [§13 CI asset validation](../spec/VYBE_Technical_Specification_v1.3.md#13-deployment-and-operations), [§17 item 3](../spec/VYBE_Technical_Specification_v1.3.md#17-open-decisions-requiring-sign-off)
- Signs off: product owner and the art lead (or the person who will own art); counsel reviews contracts and licenses
- Needed before: any art production starts. Art has the longest lead time of all open items.

## Question

1. What is the visual style of avatars, items and the Street Hoops court?
2. Who makes the 3D assets: an in-house artist, a contractor or studio, licensed packs, or generated assets?
3. Who owns the rights, and who owns the pipeline (rig, manifests, QA) after delivery?

## Why it matters

- **Everything visual is a placeholder.** `assets/manifests/avatar.v1.json` says "Placeholder ids until the art pipeline publishes the real manifest (spec §17 item 3)". `catalog.v1.json` has `assetKey` values such as `tops/court_jersey`, but there are no `.glb` files. The API returns `previewUrl` on `https://cdn.vybe.invalid/…`, so the web app always shows the fallback (stream C hand-over, E10).
- **The 3D viewer does not exist yet.** `apps/web` has the 2D fallback only; the `AvatarViewport` of spec §10 waits for real assets.
- **The manifest contract is fixed, the content is not.** Spec §9 requires glTF 2.0 `.glb`, one shared humanoid rig, 2 body presets, slot rules, coverage masks, per-item fields (`rigVersion`, `bodyCompatibility`, `hideBodyRegions[]`, `triangles`, `textureBytes`, `downloadBytes`, `contentHash`) and budgets (≤25k triangles, ≤2 MB initial avatar download, ≤1024 px textures, ≤60 draw calls). The current `catalog.v1.json` does not have these fields yet.
- **The catalog size limits the economy.** [Decision 5](05-economy.md) shows that a casual player can buy the whole current catalog (7 priced items, 2,450 V-Coins) in about 9 days. The number of items that art can deliver decides how long the economy lasts.
- **Web distribution exposes the files.** Every `.glb` and texture on the CDN can be downloaded by any visitor. A license that forbids redistribution of the source files can be incompatible with a web game. Counsel must check each license for this point.

## Options

Effort figures are rough planning numbers for a stylized low-poly cartoon style. Get quotes before you commit a budget.

| | Option | Quality and control | Rights | Effort and cost (rough) |
|---|---|---|---|---|
| **A** | **Contractor or small studio, work-for-hire.** One style guide, one rig, 2 bodies, starter set, first 10–20 items, 3–5 emotes. | High consistency. The team controls the style. | Full assignment of rights in the contract. Lowest legal risk. | Rig and 2 bodies: about 10–20 artist-days. Each garment: about 1–3 days. Each emote: about 1–2 days. Court and props: about 5–10 days. |
| **B** | **In-house artist (hire)** | Highest control, slowest start (hiring time). | Employment contract assigns rights. | One full-time artist. 4–8 weeks before the first full set. |
| **C** | **Licensed asset packs, adapted to one rig** | Fast start. Style mix risk. Packs rarely share one rig, so retargeting is needed. | Each license must allow use in a commercial web game with downloadable files, and modification. Keep a license record per asset. | Low purchase cost. 5–15 days of technical-art work to adapt. |
| **D** | **Generated assets (AI tools) with artist clean-up** | Uneven topology and rigging. High clean-up effort. | Copyright status of generated output and tool terms differ by country and by tool. Counsel must review. | Unclear. Clean-up can cost as much as option A. |

## Recommendation

**Option A for the beta set, with one named in-house owner for the pipeline. Use option C only for neutral props (court, ball, background), and only with a checked license record.**

Reasons:

- The spec needs one authoritative rig and strict budgets. A single contractor delivers a consistent rig and style faster than a hire, and with less risk than mixed packs.
- Work-for-hire with full assignment gives clean rights for catalog items that are sold for V-Coins and shown on public profiles.
- The pipeline (manifest generation, budget checks, `contentHash`, CDN upload, visual QA of §9) must have one owner inside the team. A contractor delivers files; the team owns the manifest.
- Do not use option D for beta items until counsel gives a written view.

Proposed style brief for the product owner to accept or change: stylized cartoon proportions, flat or toon shading, no realistic skin, no gendered clothing (spec §9), a clear silhouette at small mobile sizes, and a color palette that keeps contrast for color-blind players (spec §10).

## After sign-off

| Change | File or area | Owner |
|---|---|---|
| Style guide and rig specification (bone names, bind pose, axis, scale) | new `docs/art/` (or the art owner's tool), referenced from spec §9 | art owner |
| Contracts with rights assignment; license record for every third-party asset (for example `assets/LICENSES.md` with source, license, date, allowed use) | `assets/` | product owner, counsel |
| Real preset ids | `assets/manifests/avatar.v1.json` → a new version (`avatar.v2.json`); the API validates presets from it (ADR 0001, ADR 0003 #6) | art owner, API stream |
| Item manifest fields of §9 (`assetVersion`, `rigVersion`, `contentHash`, budget fields) | `assets/manifests/catalog.v1.json` → new version; `ContentCatalog.cs` loader | art owner, API stream |
| CI manifest check (budgets, compatible bodies, hashes) | `.github/workflows/ci.yml`, spec §13 | stream D |
| Base URL for `previewUrl` and assets: done (#54). `previewUrl` is the manifest `thumbnailUrl` under `Content:AssetBaseUrl` (`https://<web origin>/assets`, written by `infra/oracle/stage-env.sh`) | API configuration, `infra/` | stream N |
| 3D `AvatarViewport` with mount/dispose and fallback | `apps/web` | stream C successor |
