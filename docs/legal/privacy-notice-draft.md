# VYBE privacy notice — DRAFT

> **DRAFT — COUNSEL MUST REVIEW. NOT FOR PUBLICATION.**
>
> This file is not legal advice and is not a legal text. Engineering wrote it from the design of VYBE
> (spec v1.4, `docs/decisions/` 1, 2, 6 and 8, ADR 0002, 0005, 0006, 0007). It lists the facts that the
> final notice must match. Counsel and the data protection officer write the final wording, the lawful
> bases, the retention periods and the country-specific parts. Text in `[brackets]` is a placeholder.
> Every retention period below is a proposal (decision 8 retention draft), not an approved period.

## 1. Who we are

- Controller: `[company name, address, registration number]`.
- Contact for privacy questions and data-rights requests: `[privacy email address]` and the web form at `[URL]`.
- Data protection officer (if the company has one): `[name or role, contact]`.
- Countries where VYBE is available: `[counsel-approved country list]` (decision 1 is still open on this point).

## 2. Who can use VYBE

- VYBE is for adults only (18 or older, or the local age of majority if it is higher: `[counsel to confirm per country]`).
- Before VYBE creates a player, it asks for the month and year of birth. It does not show which dates it accepts.
- VYBE does not keep the month or the year of birth. It keeps only the result: the time of the
  confirmation, the version of the age policy, and the band "adult".
- If the answer shows that the person is under the adult age, VYBE creates no player and sets no cookie.
  It does not keep the answer.
- A month and year cannot show the exact age. The rule is conservative: VYBE accepts a person only when
  the month of the 18th birthday is before the current month. In the month of the 18th birthday itself,
  VYBE refuses, because the person can still be 17. `[Counsel to confirm this rule.]`
- When a person signs up for an account without a guest, the age question comes before VYBE creates the
  sign-in account, so an under-age answer creates no email record. If an existing sign-in account without
  a player gives an under-age answer, VYBE deletes that sign-in account.
- The gate is a self-declaration. `[Counsel to state whether this is sufficient in each country.]`

## 3. What data VYBE uses, why, and for how long

| Data | Where it is | Why | Kept for (proposal) | Lawful basis `[counsel]` |
|---|---|---|---|---|
| Player name (handle), display name, avatar choices, level | VYBE database | To run the game and show your profile | Until you delete your account. Guests: until 30 days after the guest session ends (see §5) | `[contract]` |
| Result of the age gate (time, policy version, band) | VYBE database | To show that the age rule was applied | Until the account is deleted or anonymized | `[legal obligation / legitimate interest]` |
| Email address and sign-in identity (registered players only) | Supabase Auth | To sign in and to keep the player across devices | Until you delete your account. VYBE deletes the sign-in identity after the anonymization | `[contract]` |
| Guest session token (only a hash; the token is in a cookie) | VYBE database | To keep you signed in as a guest | Until the session ends (see §5) | `[contract; security]` |
| V-Coin ledger (amounts, reasons, items, a pseudonymous player id) | VYBE database | Economy integrity, fraud checks, support corrections | No time limit, pseudonymized after deletion `[only if counsel accepts; otherwise a fixed period]` | `[legitimate interest / legal obligation]` |
| Inventory, saved outfits, quests, daily counters | VYBE database | To run the game | Deleted or anonymized when the account is deleted | `[contract]` |
| Game sessions (scores, rewards, review flags) | VYBE database | Fair play, rewards | Kept pseudonymized with the ledger | `[legitimate interest]` |
| Raw game input traces | VYBE database | Anti-cheat checks | 30 days | `[legitimate interest]` |
| Challenges | VYBE database | Results for both players | Kept pseudonymized (the other player's history needs them) | `[legitimate interest]` |
| Blocks | VYBE database | Safety | Deleted when either player is deleted | `[legitimate interest]` |
| Reports (reason, free text) | VYBE database | Safety and moderation | Free text deleted on account deletion; resolved reports `[1 year]` | `[legitimate interest]` |
| Moderation audit log | VYBE database | Accountability for staff actions | 90 days `[counsel may require longer]` | `[legitimate interest; legal claims]` |
| Data export files | Private storage bucket | Your right of access | File: 24 hours; request record: 90 days | `[legal obligation]` |
| Deletion requests (id, status, dates; no profile data) | VYBE database and a deletion log | Proof that a deletion was done; repeat after a backup restore | `[3 years — counsel to set]` | `[legal obligation (accountability)]` |
| Operational logs and traces (pseudonymous player id, no tokens, no email) | Log and trace platform | Security and operations | 30 days | `[legitimate interest]` |
| Crash reports | Sentry (if turned on) | To fix errors | `[30–90 days, vendor setting]` | `[legitimate interest; consent if counsel requires it]` |
| Product analytics | — | Not collected now. Only with consent in the future | — | `[consent]` |

VYBE does not ask guests for an email address. VYBE has no free-text chat and no real-money purchases.
V-Coins have no money value.

## 4. Profiles

- A profile shows the player name, display name, level and avatar.
- Only signed-in players (guests or registered players) can see a profile. A visitor who is not signed in
  gets the same answer as for a player that does not exist.
- A player can make the profile private in Settings. Then only the player can see it, and nobody can send
  that player a challenge from the profile. After a challenge, the other participant still sees the player name.
- Search engines are told not to index profiles (`noindex`), and profile answers are not stored by shared caches.
- A blocked player gets the same profile answer as everybody else, so a block stays private.

## 5. Guest players

- A guest player has no email address and no password. The only key is a cookie in the browser.
- The guest session ends 30 days after the last visit. Each visit extends it (at most once per day), but
  never to more than 90 days after the guest player was created.
- When the guest session ends or the cookie is lost, the guest progress cannot be recovered. Support cannot
  move guest progress to another player, because nobody can prove who owned it.
- 30 days after the last guest session ends, VYBE anonymizes the guest player in the same way as a deletion (§7).
- To keep the progress, the guest can link an account (email or a sign-in provider). The player keeps its id,
  coins and items.

## 6. Cookies

| Cookie | Purpose | Lifetime | Type |
|---|---|---|---|
| `__Host-vybe_guest` | Keeps a guest signed in | Up to 30 days after the last visit, at most 90 days after the guest was created | Necessary (authentication) |
| Supabase sign-in session | Keeps a registered player signed in | In memory of the browser tab only (no storage) | Necessary (authentication) |

`[Counsel: cookie notice text and whether a banner is necessary when there are only necessary cookies.]`

## 7. Your rights

- **Access and portability:** in Settings, ask for a data export. The download link is short-lived and
  works only for you.
- **Deletion:** in Settings, delete your account. Registered players must sign in again first.
- **What deletion does:** VYBE **anonymizes** the player. The player name is replaced with a random
  pseudonym. The display name, the age-gate result, the avatar, saved outfits, blocks and the free text of
  reports are removed. The sign-in identity (email) in Supabase Auth is deleted. The pseudonymous player
  id stays in the V-Coin ledger, game sessions and challenges, because these records cannot be deleted
  without breaking the records of other players and the economy audit.
- **Status:** the deletion has a status page that works without signing in. It shows no personal data.
- **Backups:** deleted data can stay in database backups until the backup expires: `[N days — the
  backup retention of the chosen plan; see docs/runbooks/backup-restore.md]`. If VYBE must restore a
  backup, it anonymizes again every player who asked for deletion after the backup time. VYBE does not
  promise immediate deletion from backups.
- `[Counsel: other rights (rectification, restriction, objection), the right to complain to a supervisory
  authority, and how VYBE checks identity for a request that does not come from the signed-in player.]`

## 8. Who processes data for VYBE

| Processor | What | Region |
|---|---|---|
| Supabase (database, Auth, storage) | All game data, sign-in identities, export files | `[region of the project]` |
| Fly.io (container host, decision 7) | Runs the VYBE API | `[same region as Supabase — still to choose]` |
| `[CDN]` | Delivers the web app and forwards API calls | `[global / region]` |
| Sentry (optional) | Crash reports | `[region]` |

`[Counsel: data processing agreements, international transfers and transfer safeguards.]`

## 9. Security

- Guest tokens and invite tokens are stored only as hashes.
- Logs and traces contain no tokens, no email addresses and no invite tokens.
- Staff actions on players are written to an audit log.

## 10. Changes to this notice

`[Counsel: how players learn about changes. Each version has an id, so that the age-gate record can name
the policy version that the player confirmed.]`

## Open points for counsel

1. The countries list and the adult age per country (decision 1).
2. The month-and-year rule for the age gate (§2) and whether a self-declaration is sufficient.
3. The lawful basis for each row of §3, and the ledger retention (option A: no time limit; option B: fixed period).
4. The retention of resolved reports, the audit log and deletion requests.
5. The backup retention period to state in §7.
6. Whether "anonymize" (§7) meets the erasure duty for the pseudonymous ledger id.
7. The cookie notice (§6).
