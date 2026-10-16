# The filming save and the shared farm rules

Three AIs share one farm, so the save has to be a co-op farm with separate wallets and room for three more
farmers. This page says how to make it, what the mod checks at startup, and the rules it adds on top of the
game (ownership, paying each other, going to bed). Related: issue #52.

## Making the save

1. New game, **Co-op** (host). Set **Starting Cabins** to 3. The three cabins become the homes of P2, P3 and P4
   (`SplitScreen: true` in `config.json` makes the mod join them).
2. Set the **money style to separate** on the same screen. Each farmer then has their own wallet.
3. If the save already exists with shared wallets, the host can switch at Lewis's house: the ledger book
   offers separate wallets, and the change takes effect overnight. (`runner/smoke.py` uses the debug op
   `separate_wallets`, which makes the same call at once.)

The exact wording of these options depends on the game version; the three requirements are co-op, separate
wallets and at least 3 cabins.

## Startup check

A few seconds after a save loads, the host checks the setup and logs it once per result:

- `Setup check: wallets are shared ...` (SMAPI console, WARN): the save does not use separate wallets, so every
  farmer spends the same money and `pay` has nothing to move.
- `Setup check: the farm has N cabin(s) but the three AI farmhands need 3 ...` (WARN).
- `Setup check: separate wallets on, N cabins ...` (INFO) when all is well.

Warnings are also written to the run log as `system` entries. The latest result is `setup` in
`/api/state` (`separateWallets`, `cabins`, `cabinsWithFarmhand`, `problems`). Splitting the screens itself
(joining P2-P4) reports its own problems in the same way.

`status`, `buy` and `ship` read and spend the money of the farmer who calls them; nothing in the mod shares a wallet.

## Who owns what

Nothing in the game records whose chest or plot is whose, so the mod keeps a small map (`Ownership`):

- `claim(x, y)` claims the chest or machine on that tile; `claim(x, y, width, height)` claims an area of
  ground (a plot). `unclaim(x, y)` gives it up. Claims cannot overlap another player's.
- Planting a crop on unowned ground claims that tile, and putting something into an unowned chest claims the
  chest. Ground inside another player's area stays theirs.
- `look` shows the owner: `Chest (owner: p2, 3 items ...)`, `Furnace (owner: p1)`, `Parsnip READY (owner: p1)`.
  `status` says who you are and how much you own. `/api/state` has `ownership` (claims per owner and the
  latest thefts).
- Claims are kept per save in `Mods/StardewMCP/data/ownership_<save folder>.json`. A claim on a chest or
  machine stops counting when that object is gone from its tile.

Taking from or harvesting something another seat owns is allowed, as in the game, but is logged as a `theft`
entry in the run log and the `/api/feed`: `kind: "theft"`, `player` = the taker, and `args`
`{taker, owner, what, thing, location, x, y}`. It covers `menu take` from a chest, collecting a machine's
finished output with `interact`, and `harvest` of a ripe crop (one entry per owner per call). Code can
subscribe to `Ownership.Theft` (a C# event) to react, for example to tell the owner (#40). Nothing tells the
owner yet; that is perception's job.

Suggested prompt line for the runner: "At the start of day 1, claim your chest, your machines and a plot area
with `claim`."

## Paying each other

`pay(to, amount, reason)` sends gold from your wallet to another AI player. It is a macro over the game's own
transfer: it walks to the ledger book in Lewis's house, chooses Send money, the recipient and the amount, and
the game moves the money. So it needs separate wallets and costs game time (the walk), and it works from any
map `travel_to` can leave. The result and the `pay` log entry show both wallets before and after;
the recipient gets a message with your reason. Debts and loans stay on the plan board.

## Everyone sleeps

The game ends the day when every farmer is in bed, and passes each farmer out at 02:00 on their own, so one
AI that never sleeps cannot hold the day forever. The mod adds a reminder, nothing that holds anyone:

- From `BedtimeWarnTime` (default 2400, `0` = off) an AI that is connected and not in bed gets one message a
  day, in its next tool result. From then on `status` also lists who is in bed (`/api/state` `sleep`).
- The world is frozen only while a connected AI is thinking, and `ThinkTimeoutSeconds` stops even that for a
  stuck AI. An AI that is busy acting, or has gone quiet, never freezes the others.

Not done yet: P4 (the camera farmer) is not in bed by itself, so the day still waits for it until 02:00;
accepting the sleep ready check for P4 is open under #38.

## Festivals

Not handled per seat. Festival days need the festival driver from #11 first.
