# Changelog

## 1.1.0

- **Quick sell key**: hold Space (changeable in F12) and left click an item to sell it to the best trader, or right click to list it on the flea. If the flea can't take it (no free offer slots, banned, not found in raid...), you get the same question as from the menu and can sell it to the trader instead.
- New setting to hide Quick sell from the item menu, for if you only want to use the key.
- Several of the same item at the same price now go up as one stacked flea offer, using one offer slot, the way the game lists several at once.
- With Better Flea on Near instant, the flea is picked whenever it pays more than the best trader. The 10% margin is only used for slower listings or without Better Flea.
- Neater settings: the listing price slider only shows on Custom (or without Better Flea), the margin only when it's used, and settings are in a sensible order.

## 1.0.0

First release.

- **Quick sell** in the item menu, under Inspect, for items in your stash and on you (worn gear and what's loose in it). One click sells to the flea or the trader who pays the most, whichever leaves you with more.
- The menu shows where it's going and for how much: `Quick sell (flea ~8,851 ₽)`, `Quick sell (Therapist 4,662 ₽)`, or `Quick sell (13 items, ~1,965,457 ₽)` for a selection or a full bag. Prices are fetched ahead of time, so the label is right the first time the menu opens.
- Flea listings go at 85% of the item's flea value by default, the fastest a listing sells with Better Flea. With Better Flea installed, a **Sell speed** setting picks Near instant (85%), Fast (90%), Normal (100%) or Max profit (110%); without it, or on Custom, a % slider sets the price. The listing fee is worked out with the game's own formula and taken off when comparing.
- The flea only wins if it pays at least 10% more than the best trader (a setting), since flea money comes later by mail.
- Made to be used with Better Flea, works fine without it. With Better Flea on the server, the flea value is Better Flea's own price for that exact item, the same one Price Index shows. Without it, the flea value is the base game's flea average adjusted for condition, a gun from its parts.
- The best trader is priced the way their sell screen prices it (condition, loyalty level, currency). Fence can be left out.
- Works with UI Fixes multi-select: the whole selection sells, each item wherever it fetches the most.
- A bag or rig with things in it asks first, then sells what's inside (the deepest first) and the empty bag or rig after.
- When there aren't enough free flea offer slots or roubles for the fees, they go to the items that gain the most over the trader first.
- Anything the flea would pay most for but can't take (banned, not found in raid, no free slots...) is listed with the reason and how much less the traders pay, and you're asked before it's sold to them. A setting can make it always sell to the trader, or always keep it.
- Items no one will buy say so in the menu: `Quick sell (can't be sold)`.
- When the flea is locked (under flea level, or an item Better Flea won't take yet) the menu still shows what the flea would have paid: `Quick sell (Ragman 32,320 ₽, flea locked ~45,000 ₽)`, or for a selection `~X ₽ lost to flea locks`.
- Traders are sold to with the same request their sell screen sends, flea listings go through the same call as the Add offer window, so the server and Better Flea apply all their usual rules.
