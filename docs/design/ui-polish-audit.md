# UI polish audit (stream T)

Date: 2026-11-11. Scope: the web app in `apps/web`, at 375 px (phone) and 1280 px (desktop).
Test data: one guest with V-Coins, two runs, two purchases (an avatar item and a room item), one friend and
one placed room item. Screens: every page in the app shell, the landing page and the age gate.

The style stays the same: dark navy background, rounded cards, yellow accent, cartoon art. This pass changes
only look and layout. It does not change the API, the contracts, the database, the game rules or the art.

## Shared values

The shared values are CSS custom properties in `apps/web/src/styles.css`:

- Colors: `--color-*` (surfaces, text, accent, states, earned and spent V-Coins, achievement tiers).
- Spacing: `--space-1` to `--space-7` (4, 8, 12, 16, 24, 32, 40 px).
- Radii: `--radius-sm` to `--radius-xl` (8, 10, 12, 16 px) and `--radius-pill`.
- Font sizes: `--text-xs` to `--text-3xl` (13, 14, 16, 18, 20, 28, 32 px).
- Controls: `--control-height` (44 px), `--duration-fast`, `--ease-out`.

The component styles use these values. Do not add a new hard-coded color to a page. If you need a new value,
add a token first.

## Findings

| Page | Problem | Fix |
| --- | --- | --- |
| All pages with a dropdown (age gate month, shop filter, room walls and floor, admin filters) | Each dropdown has a different background and border. The OS arrow shows. The options can show light colors on Windows and Linux. | One shared style for `select`: dark background, strong border, light text, custom chevron, 44 px height, yellow focus ring, dark options. |
| All text fields | Four different backgrounds and borders for text fields and text areas. | One shared style for `input`, `select` and `textarea`. The page rules for fields are removed. |
| Wallet | A spent amount has the same color as the other text. Only an earned amount has a color. | Shared `vybe-coin-change` element: earned is green with "+", spent is red with "−". The sign and the text label stay, so the color is not the only signal. Contrast on the card: green 10.1:1, red 7.3:1. |
| Wallet (375 px) | The date and time wrap into two cramped lines. The table has no frame. | Shorter date ("Nov 11"), the time on its own line on a phone, shared table style in a frame. |
| Wallet | No loading state. The empty state is plain text. | "Loading your V-Coin history…" status and the shared empty state. |
| Hub (desktop) | The wallet card is as tall as the avatar card but holds only the balance and one row of links. A large area is empty. | The card shows the last three V-Coin changes (green or red), and the links are four 44 px buttons at the bottom of the card. |
| Hub (375 px) | The links "History · Shop · Wardrobe · My Room" are small underlined text. On a phone, these links are the only way to Wardrobe, My Room and Wallet. | The same four links as 44 px buttons in a 2 × 2 grid. |
| Admin wallet adjustment | The result shows "-1000" with a hyphen and no color. | The result uses `vybe-coin-change` too. |
| All pages | A disabled primary button is a dim, muddy yellow ("Needs level 10", "Save room", "Save"). It looks like an error. | Disabled buttons are neutral gray with readable text. |
| All pages | Buttons have no hover or press state. | Hover color and a 1 px press movement for primary, secondary and danger buttons. Reduced motion turns the transitions off. |
| Settings | The danger button is defined only in the account deletion panel. | Shared `.button.danger`. |
| Leaderboard, achievements | Two separate copies of the same chip style (game, scope and filter tabs). | Shared `.chip` style with hover, `.active` and `aria-pressed`. |
| Leaderboard | The table is a card with a broken top-left corner. Its header style is different from the wallet table. | Shared `.table-wrap` and `.table` style for the leaderboard, the wallet and the admin tables. |
| Shop (375 px) | One item per row: the page is very long (about 30 screens). | Two items per row on a phone; four on a desktop. |
| Arcade | The practice button reads "Practice(no rewards)": the space is lost. | The label is one text run, so the space shows. The accessible name does not change. |
| Challenges, friends, room, wardrobe, outfits, profile, quests, settings | Empty states use three different styles (plain muted text, a blue notice, nothing). | Shared `.empty` style: a dashed box with the next step. |
| Settings, arcade, admin | Card headings are 24 px; on the hub and other pages they are 18 px. | Card headings (`h2`) are 20 px on all pages. Item titles stay 18 px. |
| Settings | Fact labels ("PLAYER NAME") are small upper-case text. | Normal-case labels at 13 px. |
| Phone navigation | The links are about 40 px tall. | The links are 44 px tall. Five items, as before. |
| All cards | On a desktop, the 16 px card padding is tight next to the 32 px page title. | 24 px card padding on a desktop; 16 px on a phone. |

## Not changed

- The 3D avatar, the room viewer and the game screens (art and game rules).
- The phone navigation items (five: Hub, Arcade, Challenges, Shop, Settings).
- Accessible names and roles that the E2E tests use.
