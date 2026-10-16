# Game coverage

How the AI farmers reach every part of Stardew Valley, and where the gaps are.
Status is for `main`, with open PRs noted. Update a row when a PR changes its coverage.

✅ works · 🟡 reachable but untested or partial · ❌ missing

## Principle

A human plays the whole game with six inputs. The AI gets each input in a form it can use
(a tile, a name or a label instead of pixels), and every input calls the game's own handler.
Every system behind that handler is then covered without new code.

| Input | Human | AI tool | Game entry point | Status |
|---|---|---|---|---|
| Move | WASD | `walk_to`, `travel_to` | pathfinder, warps, doors | ✅ |
| Action button | right-click | `interact(x, y, holding?)` | `Game1.pressActionButton`: the held item's use action or eating, else `tryToCheckAt` → `GameLocation.checkAction` | ✅ `holding` picks the item; handlers that wait for a fresh press (`didPlayerJustRightClick`: chests, shipping bin, fish ponds) see it |
| Use button | left-click | `use_tool`, `plant`, `place` | `Farmer.BeginUsingTool`, `Utility.tryToPlaceItem` | 🟡 `place` puts items on free ground (smoke `place`, not yet run in game); `plant` only on tilled soil |
| Dialogue answer | click an option | `respond(option)` | `DialogueBox` → `answerDialogueAction` or the question's callback | ✅ |
| Menu | click, type | `menu`; `shop_list`, `buy`, `close_menu` | the menu's own click and handler methods | 🟡 reader, fallback, `take`/`put`, `choose`, `amount`, `type`, `ok`/`cancel`; `buy`/`sell`/`craft` adapters to come |
| Minigame | timing | none | `BobberBar`, `IMinigame` | ❌ |

Rules:

- **Call the game's entry point; never re-implement it.** The old `buy` (#7) did the purchase
  itself and skipped recipe learning, chat messages and stock sync.
- **Bespoke tools are macros.** They batch primitives (`use_tool` on 40 tiles) or play things that
  need timing (fishing, combat). Nothing may be reachable only through a macro.
- **The mod does the mechanics; the AI makes every choice.** A screen that asks something goes to
  the AI. Only screens with no choice are clicked automatically.
- **Check the decompiled game before planning a system.** Find its tile action, dialogue answer or
  menu class and put it in the row. #2 planned the backpack as a shop item; it is the `BuyBackpack`
  tile action.

### Target tools

- `interact(x, y, holding?)`: hold an item, then press the action button. `checkAction` then covers
  loading machines (every machine in `Data/Machines`), gifts and quest items for NPCs, gifts and
  proposals to other farmers, fish ponds and festival pots. Without a tile it uses the held item
  in place (`performUseAction`): totems, books.
- `place(item, tiles)` (#3): `plant` without the tilled-soil check, same `Utility.tryToPlaceItem`. It covers machines,
  chests, sprinklers, fences, paths, furniture, bombs, mine staircases and tappers.
- `craft(recipe, quantity)` and `chest(x, y, action, item, quality)` (#3) are macros over the game's own code:
  `CraftingRecipe` (the recipe the crafting page uses) and `interact` + `menu take/put` (the chest's `ItemGrabMenu`).
  `menu take/put` also takes `quality`, so the primitive stays complete.
- `menu()` reads the open menu: its type, title, items with counts or prices, and the actions it
  allows. `menu(action, ...)` acts through a small adapter per menu family:
  - `take` / `put`: chests, fridge, fishing treasure, Lost and Found, shipping bin
  - `choose`: list menus, mine elevator, professions, proposals
  - `amount`: number entry (send money)
  - `type`: naming
  - `buy` / `sell`, `craft`, `ok` / `cancel`
  - A menu without an adapter falls back to listing its `allClickableComponents` by label and
    clicking one, so unknown menus stay usable.

  `shop_list`, `buy`, `close_menu` and `choose_profession` (PR #47) can become adapters later.
  Renaming them is not needed now.

## Coverage matrix

### Farm and home

| System | How a human does it | Covered by | Status | Issue |
|---|---|---|---|---|
| Clear, till, water, chop, mine | tool on a tile | `use_tool` | ✅ | #15 (batches) |
| Plant seeds, fertilizer | item on soil | `plant` | ✅ | |
| Harvest crops, forage | action on a tile | `harvest` | ✅ | |
| Place machines, chests, sprinklers, fences, furniture | use button with the item | `place` | 🟡 smoke `place`, `craft_place_chest` written, not yet run in game | #3 |
| Load a machine | action while holding the input | `interact(holding)` | ✅ smoke `furnace` | #3 |
| Collect from a machine | action | `interact` | ✅ smoke `furnace` | #3 |
| Chests, fridge | action → `ItemGrabMenu` | `interact`, then `menu take/put`; `chest` does it in one call (by name and quality, several items, "everything") | ✅ chests (smoke `chest`); 🟡 `chest` (smoke `chest_tool`) and fridge untested; owners and theft: #52 | #3 |
| Ship | shipping bin → `ItemGrabMenu` (`Farm.shipItem`) | `ship` | ✅ | |
| Eat | action holding food → "Eat?" | `eat` | ✅ | |
| Sleep | bed → `Sleep_Yes` → ready check `sleep` | `go_to_bed` | ✅ one player; 🟡 untested with 4 seats | #38 |
| Pass out (2:00, no energy) | automatic | | 🟡 | #22 |
| TV: weather, luck, recipes | action on the TV → question | `watch_tv(channel)` (macro over `interact` + `respond`: picks the channel, reads every page, reports the learned recipe); weather and luck overlap the calendar outlook (#11) | 🟡 PR for #14, not yet run in game (smoke `watch_tv`) | #14 |
| Backpack upgrade | `BuyBackpack` tile → `Backpack_Purchase` | `interact` + `respond` | 🟡 untested | #2 (plan is wrong) |
| Totems, books | action button while holding the item (`performUseAction`) | `interact(holding)` without a tile | ✅ totems (smoke `totem`); 🟡 books untested | |
| Animal care | pet (action), milk/shear (tool), hay (hopper) | `interact`, `use_tool` | 🟡 | #10 |
| Horse | stable, name, mount (action) | `interact` | 🟡 riding untested | #10 |

### Shops, crafting, upgrades

| System | How a human does it | Covered by | Status | Issue |
|---|---|---|---|---|
| Buy | shop counter → `ShopMenu` | `shop_list`, `buy` | ✅ (PR #45: `tryToPurchaseItem`, recipes, trade items) | #7 |
| Sell to a shop | click an inventory item in `ShopMenu` | `menu sell` | ❌ | #11 |
| Tool upgrades | `Blacksmith` → `Blacksmith_Upgrade` → `ShopMenu` | `buy` | 🟡 needs PR #45 (trade items) | #2 |
| Geodes | `Blacksmith_Process` → `GeodeMenu` | `crack_geode` (macro: pays Clint 25g and draws `Utility.getTreasureFromGeode`; no `GeodeMenu` clicks, so the `menu` adapter is still missing) | 🟡 PR for #14, not yet run in game (smoke `crack_geode`) | #14 |
| Crafting | crafting tab or `Craft` tile → `CraftingPage` | `craft` (macro over `CraftingRecipe`: list, make N) | 🟡 smoke `craft`, `craft_place_chest` written, not yet run in game; the page itself has no `menu` adapter | #3 |
| Cooking | `kitchen` tile → `CraftingPage` (cooking) | `cook` (macro over `CraftingRecipe(key, true)`: pack plus fridge ingredients; the page itself has no `menu` adapter) | 🟡 PR for #14, not yet run in game (smoke `cook`) | #14 |
| Buildings, house upgrade, renovation | `Carpenter` → `carpenter_*` → `CarpenterMenu`, `RenovateMenu` | menu adapter | ❌ | #10 |
| Buy animals | `AnimalShop` → `Marnie_Purchase` → `PurchaseAnimalsMenu` → `NamingMenu` | menu adapter | ❌ | #10 |
| Rings, boots, hats, clothes | equipment slots in `InventoryPage` | menu adapter | ❌ | #27 |
| Tailoring, dye, forge | `Tailoring`, `DyePot`, `Forge` tiles | menu adapter | ❌ | |
| Trash cans | `Garbage` tile | `interact` (searching the can in town) | 🟡 untested | #14 |
| Trash an item | inventory trash can → `Utility.trashItem` | `trash(item, quantity, confirm)` (never tools; valuables need `confirm`) | 🟡 PR for #14, not yet run in game (smoke `trash`) | #14 |

### People

| System | How a human does it | Covered by | Status | Issue |
|---|---|---|---|---|
| Talk | action on an NPC | `talk_to` | ✅ | #9 |
| Gifts | action holding the gift | `talk_to(gift_item)` | ✅ | #9 |
| Heart events | cutscenes with questions | skipped, then option 0 | ❌ the AI makes no choice | #9 |
| Dating, marriage | give a bouquet, later a Mermaid Pendant | `talk_to(gift_item)` | 🟡 untested | |
| Children | night question, then `NamingMenu` | choice policy | ❌ name is random | |
| Naming pets, animals, horse | `NamingMenu` | `menu type` | 🟡 `menu type` works, but the choice policy names first at random | |
| Mail | mailbox → `LetterViewerMenu` | `interact` + `close_menu` | ✅ attachments are claimed | |
| Help Wanted, quest log | `Billboard` tile, `QuestLog` | `quests` (journal, board, who took it, Special Orders read-only, `share` to the plan board), `accept_quest` (macro: walks to the board, presses Accept); events `quest_accepted`/`quest_completed`/`quest_failed`/`quest_taken_by_other` go to the player log feed | 🟡 PR for #14, not yet run in game (smoke `quests`, `accept_quest`); turning in a quest: `talk_to` with the item | #14 |
| Special Orders | `SpecialOrders` tile → `SpecialOrdersBoard` | menu adapter; `quests` lists the open ones (read-only) | ❌ board, 🟡 listing | #27 |
| Community Center | `JunimoNoteMenu` | menu adapter | ❌ | #13 |
| Joja | `JojaCDMenu` | menu adapter | ❌ | #26 |
| Museum | Gunther → `MuseumMenu` | menu adapter | ❌ | #13 |

### Adventure

| System | How a human does it | Covered by | Status | Issue |
|---|---|---|---|---|
| Fishing | cast, then `BobberBar` | fishing driver | ❌ | #4 |
| Crab pots | place, then action holding bait | `place`, `interact(holding)` | ❌ | #4 |
| Mine ladders, shafts | action on a tile | `interact` | ✅ | |
| Mine elevator | `MineElevator` → `MineElevatorMenu` | `menu choose` | ✅ smoke `mine_elevator` | #2 |
| Combat | weapon swings, aim | fight driver | ❌ | #5, #6 |
| Festivals | arrive on time → ready check `festivalStart` → actions, shops, games | travel + primitives + game drivers | ❌ | #11 |
| Night events: fairy, witch, meteorite, Qi plane | automatic | | 🟡 they play by themselves | |
| Arcade, casino, darts, crane | `IMinigame`, `CalicoJack`, `Slots` | drivers (optional) | ❌ | |
| Desert, Skull Cavern, Ginger Island, movie theater, Qi | travel, tile actions, menus | primitives | ❌ not checked | |

### Multi-AI

Use the game's multiplayer features first. Add mod rules only where the game has nothing.

| System | How the game does it | Covered by | Status | Issue |
|---|---|---|---|---|
| Separate wallets | host reads the ledger in Lewis's house (`LedgerBook`); it switches overnight | `interact` + `respond` | 🟡 untested | |
| Send money | ledger → `SendMoney` → recipient → `DigitEntryMenu`; chat shows "X sent Y" | `interact` + `respond` + `menu amount` | ✅ smoke `send_money` (split-screen) | |
| Loans, debts | no mechanic | a promise on the plan board, paid with Send money | story | #39, #48 |
| Give an item to a player | action holding the item → `SendProposal(Gift)`: one item, the receiver may decline | `give` (PR #50 moves the stack directly, no decline) | ✅ different path | #39 |
| Marriage, dance, baby between players | `SendProposal(Marriage/Dance/Baby)` → `PendingProposalDialog` | `interact(holding)` + `menu choose` | ❌ | |
| Owned plots and chests | no mechanic | mod rule: a cabin and its chests belong to its owner; plots on the plan board | ❌ | #40 |
| Sleep, festival start/end | `ReadyCheckDialog` waits for every online farmer, P4 included | AI seats wait inside their action; P4 must accept automatically | ❌ untested | #38 |

## Choice policy

What `AgentRuntime.AutoHandleOverlays` does with screens that appear by themselves.

| Screen | Today | Target |
|---|---|---|
| `ShippingMenu`, `LevelUpMenu` with no choice | clicks OK | keep |
| `LevelUpMenu` profession | picks the left one | the AI chooses (#8, PR #47) |
| `NamingMenu` | random name | the AI types a name |
| Question in a cutscene | skips the event, then answers option 0 | the AI answers with `respond` |
| `PendingProposalDialog` | not handled | the AI accepts or declines |
| `ReadyCheckDialog` | not handled | AI seats wait; P4 accepts automatically |
| Any other menu | "Menu open: X"; `menu` lists its labelled buttons (`click`, `ok`, `cancel`) | keep |

## Build order

1. ✅ `interact(holding)`; `menu` with the reader, the fallback, `take`/`put`, `choose`, `amount` and
   `type`; fix the chest label. This unlocks machines, chests, the fridge, simple menus and Send money.
2. `place`: `plant` for every placeable item. (#3 adds `place`, `craft` and `chest`; waiting for a local test.)
3. Choice policy: naming, cutscene questions, proposals, P4 ready checks.
4. Menu adapters, most useful for the videos first: craft/cook, sell, quests and Special Orders,
   animals and buildings, bundles and museum, geodes, equipment.
5. `look` labels: readable names for tile actions, plus animals and other farmers.
6. Drivers: fishing, combat, festival games, casino.
7. Late game: check each area; most should need no new tool.

## Keeping it complete

- A PR that adds coverage updates its row and adds a `runner/smoke.py` case. A 🟡 row needs a
  smoke case before it becomes ✅.
- Where to look for gaps in the decompiled game:
  - `GameLocation.performAction`: about 120 tile actions. The `performAction` overrides in
    `StardewValley.Locations` add a few dozen more (for example `LedgerBook`, `Concessions`).
  - `GameLocation.answerDialogueAction`: about 90 answers, plus overrides.
  - `StardewValley.Menus`: about 100 classes, about 30 used during play. `StardewValley.Minigames`:
    about 20.
  - `ProposalType` (Gift, Marriage, Dance, Baby); night events in `Utility.pickFarmEvent` and
    `pickPersonalFarmEvent`.
