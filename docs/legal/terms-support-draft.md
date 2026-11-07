# VYBE terms of service and support process — DRAFT

> **DRAFT — COUNSEL MUST REVIEW. NOT FOR PUBLICATION.**
>
> This file is not legal advice and is not a legal text. Engineering wrote it from the design of VYBE
> (spec v1.4 §1, §2, §8, `docs/decisions/` 1, 2, 6 and 8). It lists the facts that the final terms and the
> support process must match. Counsel writes the final wording. Text in `[brackets]` is a placeholder.

## Part A: facts for the terms of service

### A1. Eligibility

- VYBE is for adults only (18 or older, or the local age of majority if it is higher: `[counsel per country]`).
- VYBE asks for the year of birth before it creates a player and keeps only the result (see the privacy notice draft §2).
- VYBE is available only in `[counsel-approved countries]`.

### A2. Accounts

- A player can start as a guest. A guest has no email and no password; the guest lives in one browser.
- The guest session ends 30 days after the last visit, and never later than 90 days after the guest was
  created. When it ends, the guest progress is lost, unless the guest linked an account before.
- A linked account (email or a sign-in provider) keeps the player, the coins and the items.
- One sign-in identity has one player. VYBE does not merge players.
- Each player is responsible for the security of the email account that is linked to the player.

### A3. V-Coins and items

- V-Coins have no money value. Players cannot buy V-Coins with real money, cash them out, sell them or
  give them to another player.
- Items are cosmetic. Players cannot trade or give items to other players.
- All purchases with V-Coins are final. Support corrects only errors of VYBE (for example a reward that
  the server did not pay), and every correction is written to an audit log.
- There are no entry fees, stakes or prizes in challenges. VYBE has no gambling.
- VYBE can change prices, items and earn rules. `[Counsel: wording for changes and for the end of the service.]`

### A4. Fair play and safety

- No cheating, no automation and no use of bugs to get rewards. The server checks every game run and can
  refuse rewards. Runs with unusual patterns are flagged for review; the flags alone do not cause an action.
- Player names and display names must not be offensive.
- Players can block and report other players. A block is private.
- Moderators can suspend a player, with a reason in the audit log. `[Counsel: appeal process — see Part B.]`

### A5. Profiles

- Signed-in players can see a player's profile (name, display name, level, avatar). A player can make the profile private.

### A6. Deletion

- A player can delete the account in Settings. VYBE anonymizes the player: see the privacy notice draft §7.
  The deletion cannot be undone. VYBE does not promise immediate deletion from backups.
- An inactive guest is anonymized 30 days after the guest session ended.

## Part B: support process (decision 8, option A)

| Item | Process |
|---|---|
| Channel | Email `[support address]` and a web form at `[URL]` |
| Response target | 3 business days for the first answer |
| Corrections of V-Coins or items | Only for errors of VYBE, through `POST /admin/wallet-adjustments` with a reason; the audit log keeps the reason and the staff member |
| Guest progress | Support never moves guest progress to another player. There is no proof of ownership. |
| Merge of players | Not possible. One identity has one player. |
| Lost access to the linked email | No manual transfer in the MVP. `[Counsel: a verification process, if any.]` |
| Data-rights requests | The signed-in player uses Settings (export, deletion). A request by email needs an identity check first `[counsel: method]`. |
| Suspension appeal | `[Counsel and support lead: channel, time limit, who decides]` |
| Security reports | `[security contact]` |

Support staff must never ask a player for a password, a sign-in link or a session token.

## Open points for counsel

1. Countries and the adult age per country.
2. Consumer law for virtual items, purchase finality and the right of withdrawal for digital content.
3. Wording for changes to prices and items and for the end of the service.
4. The appeal process for suspensions.
5. The identity check for data-rights requests by email.
