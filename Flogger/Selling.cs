using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;
using Comfort.Common;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.Trading;
using EFT.UI.Ragfair;
using EFT.UI;
using EFT;
using HarmonyLib;

namespace Flogger
{
    internal sealed class Sale
    {
        public Item Item;
        public string Name;
        public int Count;

        public TraderBid Trader;
        public bool HasTrader => Trader.Trader != null;

        public int ListPrice;
        public int Fee;
        public double FleaNet;
        public bool FleaPossible;
        public bool FleaBanned;
        public bool FleaClosed;
        public bool FleaWouldWin;

        public bool FleaLocked => !ToFlea && FleaWouldWin && (FleaClosed || FleaBanned);
        public string FleaRefusal;

        public bool ToFlea;

        public static Sale For(Item item, double fleaValue, RagFair ragFair, ItemController controller)
        {
            var sale = new Sale { Item = item, Name = QuickSeller.Name(item), Count = Math.Max(1, item.StackObjectsCount) };
            Market.BestTrader(item, out sale.Trader);
            if (Plugin.Where.Value == SellTo.TradersOnly)
                return sale;

            if (fleaValue > 0d)
            {
                sale.ListPrice = QuickSeller.ListPrice(fleaValue);
                try
                {
                    sale.Fee = (int)Math.Ceiling(PriceCalculator.CalculateTaxPrice(item, sale.Count, sale.ListPrice, false));
                }
                catch (Exception)
                {
                    // the fee needs the flea's settings, which a player under flea level may not have
                }
                sale.FleaNet = (double)sale.ListPrice * sale.Count - sale.Fee;
            }

            sale.FleaWouldWin = sale.FleaNet > 0d
                                && (!sale.HasTrader || sale.FleaNet >= sale.Trader.Roubles * (1d + Plugin.FleaMargin.Value / 100d));

            if (ragFair == null)
            {
                sale.FleaRefusal = "the flea market isn't open to you";
                sale.FleaClosed = true;
                return sale;
            }

            // The game's own check (examined, banned, found in raid, empty...). Better Flea adds its bans and
            // purchase lock to it.
            if (!RagFair.CanBeSelectedAtRagfair(item, controller, out string error))
            {
                sale.FleaRefusal = string.IsNullOrEmpty(error) ? "can't be listed on the flea" : error;
                sale.FleaBanned = true;
            }
            else if (fleaValue <= 0d)
                sale.FleaRefusal = "no flea price for it";
            else if (sale.FleaNet <= 0d)
                sale.FleaRefusal = "the flea fee is more than it would sell for";
            else
                sale.FleaPossible = true;

            return sale;
        }
    }

    internal static class QuickSeller
    {
        private const string StillFull = "it still has things in it";
        private const float SaleMaxAge = 15f;

        private static readonly AccessTools.FieldRef<ItemUiContext, ItemController> ItemControllerOf =
            AccessTools.FieldRefAccess<ItemUiContext, ItemController>("_itemController");

        private static bool _busy;

        public static ItemController PlayerController => ItemUiContext.Instance != null ? ItemControllerOf(ItemUiContext.Instance) : null;

        public static Stash PlayerStash =>
            (PlayerController as InventoryController)?.Inventory?.Stash ?? Market.Session?.Profile?.Inventory?.Stash;

        public static Item PlayerEquipment =>
            (PlayerController as InventoryController)?.Inventory?.Equipment ?? Market.Session?.Profile?.Inventory?.Equipment;

        public static int ListPrice(double fleaValue)
        {
            return Math.Max(1, (int)Math.Floor(fleaValue * Plugin.ListingPercent / 100d));
        }

        public static void Plan(List<Sale> sales, int freeOffers, double roubles)
        {
            foreach (Sale sale in sales.Where(s => s.FleaPossible && (Plugin.Where.Value == SellTo.FleaOnly || s.FleaWouldWin))
                         .OrderByDescending(s => s.FleaNet - s.Trader.Roubles))
            {
                if (freeOffers <= 0)
                    sale.FleaRefusal = "no free flea offer slots";
                else if (roubles < sale.Fee)
                    sale.FleaRefusal = "not enough roubles in your stash for the flea fee";
                else
                {
                    sale.ToFlea = true;
                    freeOffers--;
                    roubles -= sale.Fee;
                }
            }
        }

        public static async void Sell(List<Item> picked)
        {
            if (_busy)
            {
                NotificationManager.DisplayWarningNotification("Quick sell is still busy with the last sale.");
                return;
            }

            _busy = true;
            try
            {
                await SellAll(picked);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Quick sell failed: {e}");
                NotificationManager.DisplayWarningNotification("Quick sell failed, see BepInEx\\LogOutput.log.");
            }
            finally
            {
                _busy = false;
            }
        }

        public static string Unsellable(Item item, Stash stash, ItemController controller, bool full = false)
        {
            if (item == null || stash == null || controller == null)
                return "not available";
            if (item is Money)
                return "it's money";
            if (item.QuestItem)
                return "quest item";
            if (!InInventory(item, stash))
                return "only items in your stash or on you can be sold";
            if (item.PinLockState != EItemPinLockState.Free)
                return "it's pinned or locked";
            if (!full && HasContents(item))
                return StillFull;

            var remove = ItemManipulator.Remove(item, controller, simulate: true);
            return remove.Failed ? remove.Error?.ToString() ?? "it can't be moved right now" : null;
        }

        public static bool HasContents(Item item)
        {
            return item is CompoundItem compound && compound.Grids.Any(grid => grid.Items.Any());
        }

        public static List<Item> Sellable(List<Item> picked, Stash stash, ItemController controller, List<string> problems)
        {
            var chosen = new HashSet<Item>(picked.Where(i => i != null));
            var items = new List<Item>();
            foreach (Item item in chosen)
            {
                if (item.GetAllParentItems().Any(chosen.Contains))
                    continue;

                string why = Unsellable(item, stash, controller, true);
                if (why != null)
                    problems?.Add($"{Name(item)}: {why}");
                else
                    items.Add(item);
            }
            return items;
        }

        public static List<Item> WithContents(List<Item> items)
        {
            var all = new List<Item>(items);
            foreach (Item item in items.Where(HasContents))
                all.AddRange(Contents(item));
            return all;
        }

        private static IEnumerable<Item> Contents(Item container)
        {
            return container.GetAllItems().Where(i => i != container && i.CurrentAddress is GridItemAddress);
        }

        private static async Task SellAll(List<Item> picked)
        {
            ItemController controller = PlayerController;
            Stash stash = PlayerStash;
            var problems = new List<string>();
            var done = new List<string>();

            List<Item> items = Sellable(picked, stash, controller, problems);
            List<Item> full = items.Where(HasContents).ToList();
            if (full.Count > 0 && !await ConfirmContents(full))
            {
                foreach (Item item in full)
                {
                    items.Remove(item);
                    problems.Add($"{Name(item)}: {StillFull}");
                }
            }

            // what's inside first, the deepest first, so each bag or rig is empty by the time it's sold
            var waves = items
                .SelectMany(item => Contents(item).Select(inside => (Depth: Depth(inside, item), Item: inside)).Prepend((Depth: 0, Item: item)))
                .GroupBy(entry => entry.Depth, entry => entry.Item)
                .OrderByDescending(wave => wave.Key);
            foreach (var wave in waves)
                await SellWave(wave.ToList(), stash, controller, done, problems);

            Report(done, problems);
        }

        private static int Depth(Item item, Item root)
        {
            int depth = 0;
            foreach (Item parent in item.GetAllParentItems())
            {
                depth++;
                if (parent == root)
                    break;
            }
            return depth;
        }

        private static async Task<bool> ConfirmContents(List<Item> full)
        {
            ItemUiContext context = ItemUiContext.Instance;
            if (context == null)
                return false;

            int inside = full.Sum(item => Contents(item).Count());
            bool one = full.Count == 1;
            string text = $"{(one ? Name(full[0]) : $"{full.Count} of these")} still {(one ? "has" : "have")} {inside} item{(inside == 1 ? "" : "s")} inside.\n\n"
                          + "Sell everything inside too? Each item goes wherever it fetches the most, then the empty "
                          + (one ? "container is" : "containers are") + " sold.";
            return await context.ShowMessageWindow(out _, text, "Quick sell");
        }

        private static async Task SellWave(List<Item> wave, Stash stash, ItemController controller, List<string> done, List<string> problems)
        {
            var items = new List<Item>();
            foreach (Item item in wave)
            {
                await WaitWhile(() => Unsellable(item, stash, controller) == StillFull);
                string why = Unsellable(item, stash, controller);
                if (why != null)
                    problems.Add($"{Name(item)}: {why}");
                else
                    items.Add(item);
            }
            if (items.Count == 0)
                return;

            await Market.LoadTraderPrices();
            IEftSession session = Market.Session;
            RagFair ragFair = Market.OpenFlea;
            bool flea = ragFair != null && Plugin.Where.Value != SellTo.TradersOnly;

            var values = items.Select(item => flea ? FleaValue.Get(item, SaleMaxAge) : Task.FromResult((0d, false))).ToList();
            await Task.WhenAll(values);

            var all = new List<Sale>();
            for (int i = 0; i < items.Count; i++)
            {
                (double value, bool exact) = values[i].Result;
                Sale sale = Sale.For(items[i], value, ragFair, controller);
                if (value > 0d)
                    Plugin.Log.LogInfo($"{sale.Name}: flea value {Money(value)} ({(exact ? "Better Flea" : "base game")}), "
                                       + $"listing price {Money(sale.ListPrice)}, fee {Money(sale.Fee)}");
                all.Add(sale);
            }
            Plan(all, Market.FreeOffers(ragFair), Roubles(stash));

            var sales = new List<Sale>();
            var refused = new List<Sale>();
            foreach (Sale sale in all)
            {
                if (!sale.ToFlea && !sale.HasTrader)
                    problems.Add(sale.FleaBanned && Plugin.Where.Value == SellTo.WhoeverPaysMost
                        ? $"{sale.Name}: can't be sold anywhere, the flea won't take it ({sale.FleaRefusal}) and no trader buys it"
                        : $"{sale.Name}: {sale.FleaRefusal ?? "no trader buys this"}");
                else if (!sale.ToFlea && sale.FleaWouldWin)
                    refused.Add(sale);
                else
                    sales.Add(sale);
            }

            foreach (var group in sales.Where(s => !s.ToFlea).GroupBy(s => s.Trader.Trader))
                await SellToTrader(session, group.Key, group.ToList(), done, problems);

            foreach (Sale sale in sales.Where(s => s.ToFlea))
            {
                if (await ListOnFlea(ragFair, sale, stash))
                {
                    done.Add($"{sale.Name} listed on the flea for {Money(sale.ListPrice)}{(sale.Count > 1 ? " each" : "")} (fee {Money(sale.Fee)})");
                    continue;
                }

                sale.ToFlea = false;
                sale.FleaRefusal = "the flea didn't take it";
                if (Market.BestTrader(sale.Item, out sale.Trader))
                    refused.Add(sale);
                else
                    problems.Add($"{sale.Name}: {sale.FleaRefusal}");
            }

            if (refused.Count > 0 && await SellRefusedToTraders(refused))
            {
                foreach (var group in refused.GroupBy(s => s.Trader.Trader))
                    await SellToTrader(session, group.Key, group.ToList(), done, problems);
            }
            else
            {
                foreach (Sale sale in refused)
                    problems.Add($"{sale.Name}: {sale.FleaRefusal}");
            }
        }

        private static async Task<bool> SellRefusedToTraders(List<Sale> refused)
        {
            if (Plugin.IfFleaRefuses.Value != FleaRefused.Ask)
                return Plugin.IfFleaRefuses.Value == FleaRefused.SellToTrader;

            ItemUiContext context = ItemUiContext.Instance;
            if (context == null)
                return false;

            double total = refused.Sum(s => s.Trader.Roubles);
            var text = new StringBuilder(refused.Count == 1
                ? $"Sell it to {refused[0].Trader.Trader.LocalizedName} for {Money(total)} instead? The flea can't take it."
                : $"Sell {refused.Count} items to traders for {Money(total)} instead? The flea can't take them.");

            double lost = refused.Sum(s => Math.Max(0d, s.FleaNet - s.Trader.Roubles));
            if (lost >= 1d)
                text.Append($"\nYou'd get about {Money(lost)} less than on the flea.");

            foreach (var reason in refused.GroupBy(s => s.FleaRefusal))
            {
                text.Append("\n\n").Append(char.ToUpper(reason.Key[0])).Append(reason.Key.Substring(1)).Append(':');
                foreach (var same in reason.GroupBy(s => (s.Name, s.Trader.Trader)))
                {
                    int count = same.Count();
                    text.Append('\n').Append(same.Key.Name).Append(count > 1 ? $" x{count}" : "")
                        .Append(" - ").Append(same.Key.Trader.LocalizedName).Append(' ').Append(Money(same.Sum(s => s.Trader.Roubles)));
                }
            }

            return await context.ShowScrolledMessageWindow(out _, text.ToString(), "Quick sell");
        }

        private static async Task SellToTrader(IEftSession session, Trader trader, List<Sale> sales, List<string> done, List<string> problems)
        {
            var selling = new List<Sale>();
            int total = 0;
            foreach (Sale sale in sales)
            {
                Trader.ItemPrice? price = trader.GetUserItemPrice(sale.Item);
                if (price == null || price.Value.Amount <= 0)
                {
                    problems.Add($"{sale.Name}: {trader.LocalizedName} won't buy it any more");
                    continue;
                }
                total += price.Value.Amount;
                selling.Add(sale);
            }
            if (selling.Count == 0)
                return;

            TradingItemReference[] references = selling
                .Select(s => new TradingItemReference { Item = s.Item, Count = s.Count, SchemeId = 0 })
                .ToArray();

            var finished = new TaskCompletionSource<IResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.ConfirmSell(trader.Id, references, total, result => finished.TrySetResult(result));
            IResult outcome = await finished.Task;

            if (outcome == null || !outcome.Succeed)
            {
                string error = outcome?.Error;
                problems.Add($"{trader.LocalizedName} refused the sale{(string.IsNullOrEmpty(error) ? "" : ": " + error)}");
                return;
            }

            foreach (Sale sale in selling)
                trader.Info.SetSoldToTraderItem(sale.Item.TemplateId.ToString(), sale.Count);

            Singleton<GUISounds>.Instance?.PlayUISound(EUISoundType.TradeOperationComplete);
            done.Add($"{(selling.Count == 1 ? selling[0].Name : $"{selling.Count} items")} sold to {trader.LocalizedName} for {Money(total, trader)}");
        }

        private static async Task<bool> ListOnFlea(RagFair ragFair, Sale sale, Stash stash)
        {
            var requirement = new BarterTemplate { _tpl = CurrencyUtil.ROUBLE_ID.ToString(), count = sale.ListPrice };
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ragFair.AddOffer(false, new[] { sale.Item.Id.ToString() }, new[] { requirement }, () => finished.TrySetResult(true));
            await finished.Task;

            // AddOffer only says it's done, not whether it worked: the item leaving the inventory is the answer
            await WaitWhile(() => StillOwned(sale.Item, stash));
            return !StillOwned(sale.Item, stash);
        }

        private static async Task WaitWhile(Func<bool> condition)
        {
            for (int i = 0; i < 20 && condition(); i++)
                await Task.Delay(50);
        }

        private static bool StillOwned(Item item, Stash stash)
        {
            return item.CurrentAddress != null && InInventory(item, stash);
        }

        private static bool InInventory(Item item, Stash stash)
        {
            // GetRootItem() stops at the grid an item sits in, so walk every parent. By id, in case the screen
            // holds its own copy.
            if (item.Id != stash.Id && item.GetAllParentItems().Any(parent => parent.Id == stash.Id))
                return true;

            Item equipment = PlayerEquipment;
            if (equipment == null)
                return false;
            if (item.CurrentAddress?.Container?.ParentItem?.Id == equipment.Id)
                return true;
            return item.CurrentAddress is GridItemAddress && item.GetAllParentItems().Any(parent => parent.Id == equipment.Id);
        }

        public static double Roubles(Stash stash)
        {
            return stash?.GetAllItems().Where(i => i.TemplateId == CurrencyUtil.ROUBLE_ID).Sum(i => (double)i.StackObjectsCount) ?? 0d;
        }

        private static void Report(List<string> done, List<string> problems)
        {
            foreach (string line in done)
                NotificationManager.DisplayMessageNotification(line);

            foreach (string line in problems.Take(3))
                NotificationManager.DisplayWarningNotification("Not sold - " + line);
            if (problems.Count > 3)
                NotificationManager.DisplayWarningNotification($"...and {problems.Count - 3} more not sold, see BepInEx\\LogOutput.log.");

            foreach (string line in problems)
                Plugin.Log.LogInfo("Not sold - " + line);
        }

        public static string Name(Item item)
        {
            return item.ShortName.Localized();
        }

        private static string Number(double amount)
        {
            return Math.Round(amount).ToString("N0", CultureInfo.InvariantCulture);
        }

        public static string Money(double roubles)
        {
            return Number(roubles) + " ₽";
        }

        private static string Money(int amount, Trader trader)
        {
            switch (trader.Settings.Currency)
            {
                case ECurrencyType.USD:
                    return "$" + Number(amount);
                case ECurrencyType.EUR:
                    return "€" + Number(amount);
                default:
                    return Money(amount);
            }
        }
    }
}
