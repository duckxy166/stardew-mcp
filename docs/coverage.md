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
| Move | WASD | `walk_to`, `travel_to` | pathfinder, warps, doors | ✅ `travel_to` also takes shop owners and villagers (#9) |
| Action button | right-click | `interact(x, y, holding?)` | `Game1.pressActionButton`: the held item's use action or eating, else `tryToCheckAt` → `GameLocation.checkAction` | ✅ `holding` picks the item; handlers that wait for a fresh press (`didPlayerJustRightClick`: chests, shipping bin, fish ponds) see it |
| Use button | left-click | `use_tool`, `plant`, `place` | `Farmer.BeginUsingTool`, `Utility.tryToPlaceItem` | 🟡 `place` puts items on free ground (smoke `place`, not yet run in game); `plant` only on tilled soil |
| Dialogue answer | click an option | `respond(option)` | `DialogueBox` → `answerDialogueAction` or the question's callback | ✅ |
| Menu | click, type | `menu`; `shop_list`, `buy`, `close_menu` | the menu's own click and handler methods | 🟡 reader, fallback, `take`/`put`, `choose`, `amount`, `type`, `ok`/`cancel`; `buy`/`craft` adapters to come; selling is the `sell` tool (#11) |
| Minigame | timing | `fish` (BobberBar only) | `BobberBar`, `IMinigame` | 🟡 fishing only (not yet run in game), other `IMinigame`s ❌ |

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
| Clear, till, water, chop, mine | tool on a tile | `use_tool`; `clear_area`, `till_area` for rectangles | ✅ single tiles; 🟡 `clear_area`/`till_area` untested (smoke `farm_batch`) | #15 |
| Plant seeds, fertilizer | item on soil | `plant` (tiles, or `area`/whole map) | ✅ tiles; 🟡 area mode untested (smoke `farm_batch`) | #15 |
| Harvest crops, forage | action on a tile | `harvest`; `harvest_all` for the whole map | ✅ tiles; 🟡 `harvest_all` untested | #15 |
| Whole-farm routine | look at every plot, water, collect | `farm_summary`, `water_all`, `harvest_all` | 🟡 untested (smoke `farm_batch` covers summary and water; refill and rain need manual play) | #15 |
| Place machines, chests, sprinklers, fences, furniture | use button with the item | `place` | 🟡 smoke `place`, `craft_place_chest` written, not yet run in game | #3 |
| Load a machine | action while holding the input | `interact(holding)` | ✅ smoke `furnace` | #3 |
| Collect from a machine | action | `interact` | ✅ smoke `furnace` | #3 |
| Chests, fridge | action → `ItemGrabMenu` | `interact`, then `menu take/put`; `chest` does it in one call (by name and quality, several items, "everything") | ✅ chests (smoke `chest`); 🟡 `chest` (smoke `chest_tool`) and fridge untested; owners and theft: #52 | #3 |
| Ship | shipping bin → `ItemGrabMenu` (`Farm.shipItem`) | `ship` | ✅ next morning's briefing lists what was sold per item (smoke `briefing`) | #29 |
| Eat | action holding food → "Eat?" | `eat` | ✅ | |
| Sleep | bed → `Sleep_Yes` → ready check `sleep` | `journal`, then `go_to_bed` (refuses until today's journal is written) | ✅ one player (smoke `journal`); 🟡 untested with 4 seats; an unknown menu open for 10 s is closed (#32) | #38, #21, #32 |
| Pass out (2:00, health 0, exhaustion) | automatic; the day ends without a bed | `pass_out` record in `/api/state` and the log (cause, place, running action, money, items lost); `POST /api/release_freeze` lets the clock run out; next morning the runner asks for `journal(day=N)` first | 🟡 smoke `pass_out` (2:00 AM) written, not run yet; health 0 in the mines untested | #22 |
| TV: weather, luck, recipes | action on the TV → question | `watch_tv(channel)` (macro over `interact` + `respond`: picks the channel, reads every page, reports the learned recipe); weather and luck also come free in `status`, dates and shop hours in `calendar` | 🟡 PR for #14, not yet run in game (smoke `watch_tv`, `calendar`) | #14, #11 |
| Backpack upgrade | `BuyBackpack` tile → `Backpack_Purchase` | `look` labels the tile ("Backpack upgrade to 24 slots (2000g…)"), then `interact` + `respond` | 🟡 smoke `backpack` added, not yet run | #2 |
| Totems, books | action button while holding the item (`performUseAction`) | `interact(holding)` without a tile | ✅ totems (smoke `totem`); 🟡 books untested | |
| Animal care | pet (action), milk/shear (tool), hay (hopper) | `animals` (pet / collect / feed: `FarmAnimal.pet`, the Milk Pail or Shears swing, the hopper tile); `interact` and `use_tool` also work | 🟡 untested in game (PR for #10) | #10 |
| Horse | stable, name, mount (action) | `interact`, `mount`, `dismount` (horse `checkAction`) | 🟡 riding untested; smoke `mount` self-skips without a horse | #10, #54 |

### Shops, crafting, upgrades

| System | How a human does it | Covered by | Status | Issue |
|---|---|---|---|---|
| Buy | shop counter → `ShopMenu` | `shop_list`, `buy` | ✅ (PR #45: `tryToPurchaseItem`, recipes, trade items) | #7 |
| Sell to a shop | click an inventory item in `ShopMenu` | `sell(item, quantity)`: clicks the inventory slot, the shop decides what it buys | 🟡 untested; smoke `sell_to_shop`; right-click selling one item is assumed | #11 |
| Tool upgrades | `Blacksmith` → `Blacksmith_Upgrade` → `ShopMenu` | `buy` (trade items, PR #45) | 🟡 smoke `tool_upgrade` added, not yet run; the two-day wait and pickup are untested | #2 |
| Geodes | `Blacksmith_Process` → `GeodeMenu` | `crack_geode` (macro: pays Clint 25g and draws `Utility.getTreasureFromGeode`; no `GeodeMenu` clicks, so the `menu` adapter is still missing) | 🟡 PR for #14, not yet run in game (smoke `crack_geode`) | #14 |
| Crafting | crafting tab or `Craft` tile → `CraftingPage` | `craft` (macro over `CraftingRecipe`: list, make N) | 🟡 smoke `craft`, `craft_place_chest` written, not yet run in game; the page itself has no `menu` adapter | #3 |
| Cooking | `kitchen` tile → `CraftingPage` (cooking) | `cook` (macro over `CraftingRecipe(key, true)`: pack plus fridge ingredients; the page itself has no `menu` adapter) | 🟡 PR for #14, not yet run in game (smoke `cook`) | #14 |
| Buildings, house upgrade, renovation | `Carpenter` → `carpenter_*` → `CarpenterMenu`, `RenovateMenu` | `build`, `upgrade_house`: `Farm.buildStructure` and the `upgrade_Yes` answer, costs from `Game1.buildingData` | 🟡 untested in game (PR for #10); upgrading a built coop or barn (Big Coop...) and renovation are missing | #10 |
| Buy animals | `AnimalShop` → `Marnie_Purchase` → `PurchaseAnimalsMenu` → `NamingMenu` | `buy_animal`: creates the `FarmAnimal`, `AnimalHouse.adoptAnimal`; prices from `Game1.farmAnimalData` | 🟡 untested in game (PR for #10); the random brown chicken or cow colour is not handled | #10 |
| Rings, boots, hats | equipment slots in `InventoryPage` | `equip` (`Farmer.Equip`, else the slot field plus `onEquip`/`onUnequip`); `status` and `inventory` show them | 🟡 untested | #27 |
| Bait, tackle | drop onto the rod in `InventoryPage` | `equip` (the rod's own `attach`); `inventory` shows what is on the rod | 🟡 untested | #27 |
| Clothes (hats, shirts, pants) | equipment slots in `InventoryPage` | `wear`, `wardrobe` | 🟡 untested; smoke `wear` | #54 |
| Tailoring, dye, forge | `Tailoring`, `DyePot`, `Forge` tiles | menu adapter | ❌ | |
| Trash cans | `Garbage` tile | `search_trash` (presses the tile; `trash_dive` event with `seen_by`) | 🟡 smoke `search_trash` | #14, #54 |
| Trash an item | inventory trash can → `Utility.trashItem` | `trash(item, quantity, confirm)` (never tools; valuables need `confirm`) | 🟡 PR for #14, not yet run in game (smoke `trash`) | #14 |

### People

| System | How a human does it | Covered by | Status | Issue |
|---|---|---|---|---|
| Know the villagers | social page | `villagers` (location, hearts, counters, birthdays, tastes found out) | 🟡 untested in game | #9 |
| Talk | action on an NPC | `talk_to` (finds the villager, travels across maps, reports points before/after) | 🟡 untested in game | #9 |
| Gifts | action holding the gift | `talk_to(gift_item)`: reaction and taste are learned; `interact(holding)` gifts are not recorded | 🟡 untested in game | #9 |
| Heart events | cutscenes with questions | `PlayHeartEvents` (off): story events play, stop at questions for `respond`; other events are skipped | 🟡 opt-in, untested; in split-screen the event only moves while no other AI is thinking | #9 |
| Dating, marriage | give a bouquet (8 hearts), later a Mermaid Pendant (10 hearts, upgraded house) | `bouquet`, `mermaid_pendant` (the game's gift path through `talk_to`; requirements shown in `villagers`); the wedding is the game's own, watched and logged | 🟡 untested in game | #53 |
| Children | night question (`DialogueBox`), then `NamingMenu` | `go_to_bed` hands the question back; `respond` answers it, `name` names the child | 🟡 untested in game; the AI is not forced to want a child | #53 |
| Naming pets, animals, horse | `NamingMenu` | `name(text)`; status shows "Naming: ..."; an unanswered box gets a random name at bedtime or at 1:00 AM | 🟡 untested in game | #53 |
| Mail | mailbox → `LetterViewerMenu` | `interact` + `close_menu` | ✅ attachments are claimed; the morning briefing and the footer (`📬 N letters`) say when mail is waiting (smoke `briefing`); 🟡 `close_menu` also accepts the letter's quest (`AcceptQuest`), untested | #29, #2 |
| Help Wanted, quest log | `Billboard` tile, `QuestLog` | `quests` (journal, board, who took it, Special Orders read-only, `share` to the plan board), `accept_quest` (macro: walks to the board, presses Accept); events `quest_accepted`/`quest_completed`/`quest_failed`/`quest_taken_by_other` go to the player log feed | 🟡 PR for #14, not yet run in game (smoke `quests`, `accept_quest`); turning in a quest: `talk_to` with the item | #14 |
| Special Orders | `SpecialOrders` tile → `SpecialOrdersBoard`, its Accept button | `special_orders` (`list`, `accept`, `active`); `ActiveSpecialOrdersText()` is for `quests` (#14) | 🟡 untested | #27 |
| Community Center | `JunimoNoteMenu` | `bundles` (reads `Bundles`/`BundleData`), `donate_bundle` (opens the room's `JunimoNoteMenu` and clicks the bundle, the inventory slot and an ingredient slot; success is read back from `Bundles`) | 🟡 untested, needs the CC open | #13 |
| Joja | Morris's membership question, `JojaCDMenu` | `joja`: `projects` lists the five projects and their state from the mail flags; `membership` shows a warning and buys nothing unless `confirm=true`, then answers Morris's yes/no dialogue (global, irreversible: closes the bundles for everyone, announced in chat, recorded on the log, the plan board and as the `joja_membership` event); `buy_project` opens the game's own `JojaCDMenu` and clicks the project's box, found by its price, so the game takes the gold and sets its own flags (`joja_project_bought` event) | 🟡 untested, JojaMart + dialogue and form members late-bound | #26 |
| Museum | Gunther → `MuseumMenu` | `museum`: `list` via `isItemSuitableForDonation`; `donate` writes `museumPieces` on the next free display tile (not the `MuseumMenu` UI, which needs pointer coordinates); `rewards` answers Gunther's "Collect" and takes the grab menu | 🟡 untested | #13 |

### Adventure

| System | How a human does it | Covered by | Status | Issue |
|---|---|---|---|---|
| Fishing | cast, then `BobberBar` | `fish(tries)`: shore spot, cast power, bite, BobberBar driver, treasure | 🟡 written, not yet run in game (smoke `fish`); `fish_info` and bait/tackle `attach` still open | #4 |
| Crab pots | place, then action holding bait | `place`, `interact(holding)` | ❌ | #4 |
| Mine ladders, shafts | action on a tile | `interact` | ✅ | |
| Mine elevator | `MineElevator` → `MineElevatorMenu` | `menu choose` | ✅ smoke `mine_elevator` | #2 |
| Combat | weapon swings, aim | `fight`: best weapon, walk to the nearest monster, swing with `Farmer.BeginUsingTool`; `eat_below` eats through the `eat` tool's path (`Farmer.eatHeldObject`) | 🟡 untested, no smoke case yet | #5, #6 |
| Mine survival | watch health, eat, climb out | runtime interrupt when a monster within 3 tiles hurts the farmer (every action except `fight`, `eat`, `leave_mine`); `fight` stops at 30% HP; `leave_mine` walks to the up ladder (tile 115) and uses it with `checkAction`, else `Game1.warpFarmer` to the Mine; the footer shows HP and warns from 11 PM | 🟡 untested, no smoke case yet | #6 |
| Festivals | arrive on time → ready check `festivalStart` → actions, shops, games | `calendar` (dates, hours), `travel_to`, `festival(start/leave)`, `talk_to` reaches festival villagers and booths, `interact(holding)` for the Luau soup | 🟡 untested; Egg Hunt eggs and the Fair and ice-fishing games are not covered | #11 |
| Night events: fairy, witch, meteorite, Qi plane | automatic (`Game1.farmEvent`) | `NightEvents` reports them as an `Overnight:` line and a `night_event` log entry | 🟡 random, no smoke case | #54 |
| Arcade, casino, darts, crane | `IMinigame`, `CalicoJack`, `Slots` | drivers (optional) | ❌ | |
| Desert, Skull Cavern, Ginger Island, movie theater, Qi | travel, tile actions, menus | primitives | ❌ not checked | |

### Knowledge

Reference tools read the game's own data (`Game1.cropData`, `Game1.objectData`, `Data/Shops`, the recipe files), so they are always right and cover modded content. They are instant observations: no game time.

| System | How a human does it | Covered by | Status | Issue |
|---|---|---|---|---|
| Crop planning: seasons, growth, prices | the wiki, the seed shop | `crops(season?)`: last day to plant, harvests that fit, seed and sell prices, gold per tile per day | 🟡 smoke `knowledge`, not yet run in game | #16 |
| Item facts: sell price, food, shops, recipes | the wiki, the collections tab | `item_info(name)` (objects only) | 🟡 smoke `knowledge`, not yet run in game | #16 |

### Multi-AI

Use the game's multiplayer features first. Add mod rules only where the game has nothing.

| System | How the game does it | Covered by | Status | Issue |
|---|---|---|---|---|
| Separate wallets | host reads the ledger in Lewis's house (`LedgerBook`); it switches overnight | `interact` + `respond` | 🟡 untested | |
| Send money | ledger → `SendMoney` → recipient → `DigitEntryMenu`; chat shows "X sent Y" | `interact` + `respond` + `menu amount` | ✅ smoke `send_money` (split-screen) | |
| Loans, debts | no mechanic | a promise on the plan board, paid with Send money | story | #39, #48 |
| Give an item to a player | action holding the item → `SendProposal(Gift)`: one item, the receiver may decline | `give` (PR #50 moves the stack directly, no decline) | ✅ different path | #39 |
| Marriage between players | `SendProposal(Marriage)` → `PendingProposalDialog` | `propose(to)` and `answer_proposal(accept)`; the other player is told in their next tool result | 🟡 untested in game | #53 |
| Dance, baby between players | `SendProposal(Dance/Baby)` → `PendingProposalDialog` | `interact(holding)` + `menu choose` | ❌ | |
| Owned plots and chests | no mechanic | mod rule (`Ownership`): chests and machines carry `Object.owner`, crops record their planter in `HoeDirt.modData`; taking from or harvesting another seat's claim logs a `theft` entry | ✅ smoke `ownership` | #52, #40 |
| Seeing what others do, being interrupted | no mechanic (the game draws the other farmers and nothing more) | mod rule (`Perception`): `look` and `status` list the other AI players in view with the tool they run, its reason and how long, next to their last `say` and plan board section. An @mention, whisper, gift, a player coming into view or entering your house, a theft (`Ownership.Theft`), rain starting and crows on the farm add an `Events:` block to the next tool result and stop a running `wait`, `walk_to`, `travel_to` or farm action (`AgentRuntime.Interrupt`). P4 neither sees nor is seen | 🟡 written, smoke `caught_slacking` not run yet; damage and monster interrupts wait for #6 | #40 |
| Sleep, festival start/end | `ReadyCheckDialog` waits for every online farmer, P4 included | AI seats wait inside their action (`festival(start)` waits in the ready check); P4 accepts automatically; `Bedtime` warns late seats | ✅ smoke `bedtime`, `sleep_together` | #52, #38, #11 |

### Context control (#30)

What the AI reads and how long a session lasts. These are not game systems; the rows say where the
text size comes from.

| System | How it works | Covered by | Status | Issue |
|---|---|---|---|---|
| Map size | `WorldText.Describe` scan of a square window | `look` default radius 8 (17x17), legend limited to the symbols in view | 🟡 untested in game | #30 |
| Object list filter | the "Things in view" list of the same scan | `look(kinds=[...])` | 🟡 untested in game | #30 |
| What changed | per-seat snapshot of the last scan on that map | `look(diff=true)`; falls back to the full view when most of the window is new | 🟡 untested in game | #30 |
| Result footer | `AgentTools.Footer()` (time, place, energy, money, menu, professions) | joined into one line in `AgentRuntime.Finish` | 🟡 untested in game | #30 |
| Session break | none (the game has no such thing); `SessionBreaks.cs` adds a `Session break:` line at set times or after N calls / M characters, then refuses actions | opt-in with `/mcp?breaks=1`; `think`, observations and `go_to_bed` still work; `run_days.py` starts the next part with a handoff | 🟡 untested in game | #30 |
| Token log | `claude -p` stream-json `result` event | `runner/usage_log.py` writes `runs/usage.csv`, one row per part | 🟡 tested on fake transcripts only | #30 |

## Choice policy

What `AgentRuntime.AutoHandleOverlays` does with screens that appear by themselves.

| Screen | Today | Target |
|---|---|---|
| `ShippingMenu`, `LevelUpMenu` with no choice | clicks OK | keep |
| `LevelUpMenu` profession | picks the left one | the AI chooses (#8, PR #47) |
| Letter with a quest | `close_menu` accepts the quest | the AI chooses |
| `ItemGrabMenu` that is not a chest (fishing treasure, gift, fish pond output) | `close_menu` first takes what fits and names what is left | keep; `menu take` picks single items |
| `NamingMenu` | the AI answers with `name(text)`; a box nobody answers gets a random name at bedtime, at 1:00 AM, with no AI on the seat, or after the thinking timeout plus a minute | keep |
| Question in a cutscene | skips the event, then answers option 0; with `PlayHeartEvents`, heart events wait for `respond` (option 0 after the think timeout) | the AI answers with `respond` for every event |
| `PendingProposalDialog` | the AI accepts or declines with `answer_proposal`; nothing answers for it | keep |
| `ReadyCheckDialog` | not handled | AI seats wait; P4 accepts automatically |
| Any other menu | "Menu open: X"; `menu` lists its labelled buttons (`click`, `ok`, `cancel`) | keep |
| Unknown menu during `go_to_bed`'s wait for the new day (not `ShippingMenu`, `LevelUpMenu`, `SaveGameMenu`, `ReadyCheckDialog`) | waited 5 min, then the call failed | after 10 s: log its type, `exitThisMenu` (#32) |

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
