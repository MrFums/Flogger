using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using Comfort.Common;
using EFT.InventoryLogic;
using EFT.Trading;
using EFT.UI.Ragfair;
using EFT;
using Newtonsoft.Json;
using SPT.Common.Http;
using SPT.Common.Utils;
using UnityEngine;

namespace Flogger
{
    internal struct TraderBid
    {
        public Trader Trader;
        public double Roubles;
    }

    internal static class Market
    {
        private const string FenceId = "579dc571d53a0658a154fbec";

        private static readonly HashSet<string> NoPrices = new HashSet<string>();
        private static Task _loading;

        public static IEftSession Session => Singleton<ClientApplication<IEftSession>>.Instance?.GetClientBackEndSession();

        public static bool InRaid => Singleton<GameWorld>.Instantiated && !(Singleton<GameWorld>.Instance is HideoutGameWorld);

        public static RagFair OpenFlea
        {
            get
            {
                RagFair ragFair = Session?.RagFair;
                return ragFair != null && ragFair.Available && RagFair.Settings != null ? ragFair : null;
            }
        }

        public static int FreeOffers(RagFair ragFair)
        {
            return ragFair != null ? ragFair.MaxOffersCount - ragFair.MyOffersCount : 0;
        }

        public static bool TraderPricesLoaded
        {
            get
            {
                var session = Session;
                return session?.Traders != null && Unlocked(session).All(t => t._supplyData != null || NoPrices.Contains(t.Id));
            }
        }

        public static Task LoadTraderPrices()
        {
            if (TraderPricesLoaded)
                return Task.CompletedTask;
            if (_loading == null || _loading.IsCompleted)
                _loading = LoadAll();
            return _loading;
        }

        private static IEnumerable<Trader> Unlocked(IEftSession session)
        {
            return session.Traders.Where(t => t?.Info != null && t.Info.Available);
        }

        private static async Task LoadAll()
        {
            var session = Session;
            if (session?.Traders == null)
                return;

            await Task.WhenAll(Unlocked(session)
                .Where(t => t._supplyData == null && !NoPrices.Contains(t.Id))
                .Select(t => LoadOne(session, t))
                .ToList());
        }

        private static async Task LoadOne(IEftSession session, Trader trader)
        {
            try
            {
                var result = await session.GetSupplyData(trader.Id);
                if (result.Succeed && trader._supplyData == null)
                    trader._supplyData = result.Value;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not load prices for trader {trader.Id}: {e.Message}");
            }

            if (trader._supplyData == null)
                NoPrices.Add(trader.Id);
        }

        public static bool BestTrader(Item item, out TraderBid best)
        {
            best = default;
            var session = Session;
            if (session?.Traders == null)
                return false;

            foreach (Trader trader in Unlocked(session))
            {
                if (trader._supplyData == null || trader.Id == FenceId && !Plugin.IncludeFence.Value)
                    continue;

                Trader.ItemPrice? price = trader.GetUserItemPrice(item);
                if (price?.CurrencyId == null || price.Value.Amount <= 0)
                    continue;

                double rate = trader.CurrencyCourses.TryGetValue(price.Value.CurrencyId.Value.ToString(), out double r) ? r : 1d;
                double roubles = price.Value.Amount * rate;
                if (roubles > best.Roubles)
                    best = new TraderBid { Trader = trader, Roubles = roubles };
            }

            return best.Trader != null;
        }
    }

    internal enum Priority
    {
        Hover,
        Sale,
        Warm
    }

    internal sealed class Ticket
    {
        public Priority Priority;
        public bool StartedAsWarm;
        public readonly TaskCompletionSource<bool> Turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Ticket(Priority priority)
        {
            Priority = priority;
        }
    }

    internal static class Limiter
    {
        private const int MaxInFlight = 4;
        private const int PerSecond = 20;
        private const int WarmInFlight = 1;
        private const int WarmPerSecond = 3;
        private const long WindowMs = 1000;
        private const int FailuresBeforePause = 3;
        private const long BasePauseMs = 10000;
        private const long MaxPauseMs = 60000;

        private static readonly object Gate = new object();
        private static readonly List<Ticket> Waiting = new List<Ticket>();
        private static readonly Queue<long> Started = new Queue<long>();
        private static readonly Queue<long> WarmStarted = new Queue<long>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly System.Threading.Timer Wake = new System.Threading.Timer(_ => Pump(), null, Timeout.Infinite, Timeout.Infinite);
        private static int _running;
        private static int _warmRunning;
        private static int _failures;
        private static long _pausedUntil;

        public static void Succeeded()
        {
            lock (Gate)
                _failures = 0;
        }

        public static void Failed()
        {
            lock (Gate)
            {
                _failures++;
                if (_failures < FailuresBeforePause)
                    return;

                long pause = Math.Min(MaxPauseMs, BasePauseMs << Math.Min(_failures - FailuresBeforePause, 6));
                _pausedUntil = Math.Max(_pausedUntil, Clock.ElapsedMilliseconds + pause);
                Plugin.Log.LogWarning($"{_failures} price requests failed in a row, pausing price requests for {pause / 1000} s.");
            }
        }

        public static void Promote(Ticket ticket, Priority priority)
        {
            if (ticket == null)
                return;

            bool changed = false;
            lock (Gate)
            {
                if (priority < ticket.Priority)
                {
                    ticket.Priority = priority;
                    changed = Waiting.Contains(ticket);
                }
            }
            if (changed)
                Pump();
        }

        public static async Task<T> Run<T>(Ticket ticket, Func<Task<T>> send)
        {
            lock (Gate)
                Waiting.Add(ticket);
            Pump();

            await ticket.Turn.Task;
            try
            {
                return await send();
            }
            finally
            {
                lock (Gate)
                {
                    _running--;
                    if (ticket.StartedAsWarm)
                        _warmRunning--;
                }
                Pump();
            }
        }

        private static void Pump()
        {
            lock (Gate)
            {
                long now = Clock.ElapsedMilliseconds;
                while (Started.Count > 0 && now - Started.Peek() >= WindowMs)
                    Started.Dequeue();
                while (WarmStarted.Count > 0 && now - WarmStarted.Peek() >= WindowMs)
                    WarmStarted.Dequeue();

                if (now < _pausedUntil)
                {
                    if (Waiting.Count > 0)
                        Wake.Change(Math.Max(1L, _pausedUntil - now), Timeout.Infinite);
                    return;
                }

                while (_running < MaxInFlight && Started.Count < PerSecond)
                {
                    bool warmAllowed = _warmRunning < WarmInFlight && WarmStarted.Count < WarmPerSecond;
                    Ticket next = null;
                    foreach (Ticket waiting in Waiting)
                    {
                        if (waiting.Priority == Priority.Warm && !warmAllowed)
                            continue;
                        if (next == null || waiting.Priority < next.Priority)
                            next = waiting;
                    }
                    if (next == null)
                        break;

                    Waiting.Remove(next);
                    _running++;
                    Started.Enqueue(now);
                    if (next.Priority == Priority.Warm)
                    {
                        next.StartedAsWarm = true;
                        _warmRunning++;
                        WarmStarted.Enqueue(now);
                    }
                    next.Turn.SetResult(true);
                }

                if (Waiting.Count == 0 || _running >= MaxInFlight)
                    return;

                long wait = long.MaxValue;
                if (Started.Count >= PerSecond)
                    wait = WindowMs - (now - Started.Peek());
                else if (_warmRunning < WarmInFlight && WarmStarted.Count >= WarmPerSecond)
                    wait = WindowMs - (now - WarmStarted.Peek());
                if (wait != long.MaxValue)
                    Wake.Change(Math.Max(1L, wait), Timeout.Infinite);
            }
        }
    }

    internal static class FleaValue
    {
        private const string FleaRoute = "/betterflea/item-price";
        private const int MostCached = 10000;
        private const float RetryAfter = 10f;
        private const int AverageTimeoutMs = 30000;
        public const float KeepFor = 120f;

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
        private static readonly Func<string, Ticket, Task<double?>> AskFlea = Post;
        private static readonly Func<string, Ticket, Task<double?>> AskAverage = MarketAverage;

        private static readonly Dictionary<string, Entry> Flea = new Dictionary<string, Entry>();
        private static readonly Dictionary<string, Entry> Average = new Dictionary<string, Entry>();
        private static Task<bool> _betterFlea;

        public static bool HasBetterFlea => _betterFlea != null && _betterFlea.IsCompleted && _betterFlea.Result;

        private class Entry
        {
            public double Price;
            public float FetchedAt = float.NegativeInfinity;
            public bool Failed;
            public Task<double> Pending;
            public Ticket Ticket;
        }

        public static async Task<(double Value, bool Exact)> Get(Item item, float maxAge, Priority priority = Priority.Sale)
        {
            if (await BetterFlea())
            {
                double price = await Ask(Flea, Serialize(item), AskFlea, maxAge, priority);
                if (price > 0d)
                    return (price, true);
            }

            return (await GetAverage(item, maxAge, priority), false);
        }

        public static bool TryGet(Item item, out double value)
        {
            value = 0d;
            Task<bool> check = BetterFlea();
            if (!check.IsCompleted)
                return false;

            if (check.Result)
            {
                Entry entry = Peek(Flea, Serialize(item), AskFlea);
                if (entry.Price > 0d)
                {
                    value = entry.Price;
                    return true;
                }
                if (entry.Pending != null)
                    return false;
            }

            return TryAverage(item, out value);
        }

        private static Task<bool> BetterFlea()
        {
            if (_betterFlea != null)
                return _betterFlea;
            if (Market.Session == null)
                return Task.FromResult(false);

            return _betterFlea = Check();

            async Task<bool> Check()
            {
                try
                {
                    await Post("[]", new Ticket(Priority.Hover));
                    Plugin.Log.LogInfo("Better Flea found on the server, flea prices are its own.");
                    return true;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogInfo($"No Better Flea prices on this server ({e.Message}). Using the flea's average prices.");
                    return false;
                }
            }
        }

        private static Task<double?> Post(string body, Ticket ticket)
        {
            return Limiter.Run(ticket, () => Send(body));
        }

        private static async Task<double?> Send(string body)
        {
            Client client = RequestHandler.HttpClient;
            using (HttpRequestMessage request = client.CreateNewHttpRequest(HttpMethod.Post, FleaRoute))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await client.HttpClient.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("the server answered " + (int)response.StatusCode);

                    byte[] data = await response.Content.ReadAsByteArrayAsync();
                    if (Zlib.IsCompressed(data))
                        data = Zlib.Decompress(data);

                    var answer = JsonConvert.DeserializeObject<FleaAnswer>(Encoding.UTF8.GetString(data))
                                 ?? throw new Exception("the server sent nothing back");
                    return answer.Price;
                }
            }
        }

        private static Task<double?> MarketAverage(string tpl, Ticket ticket)
        {
            return Limiter.Run(ticket, () => FetchAverage(tpl));
        }

        private static async Task<double?> FetchAverage(string tpl)
        {
            var session = Market.Session;
            if (session == null)
                return null;

            var done = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.GetMarketPrices(tpl, result => done.TrySetResult(result.Succeed && result.Value != null ? result.Value.avg : (double?)null));
            if (await Task.WhenAny(done.Task, Task.Delay(AverageTimeoutMs)) != done.Task)
                throw new TimeoutException("the market prices took too long");
            return await done.Task;
        }

        private static Task<double> Ask(Dictionary<string, Entry> cache, string key, Func<string, Ticket, Task<double?>> ask, float maxAge, Priority priority)
        {
            Entry entry = Find(cache, key);
            if (entry.Pending != null)
            {
                Limiter.Promote(entry.Ticket, priority);
                return entry.Pending;
            }
            if (Time.realtimeSinceStartup - entry.FetchedAt <= (entry.Failed ? Math.Min(maxAge, RetryAfter) : maxAge))
                return Task.FromResult(entry.Price);
            return Refresh(entry, key, ask, priority);
        }

        private static Entry Peek(Dictionary<string, Entry> cache, string key, Func<string, Ticket, Task<double?>> ask)
        {
            Entry entry = Find(cache, key);
            if (entry.Pending != null)
                Limiter.Promote(entry.Ticket, Priority.Hover);
            else if (Time.realtimeSinceStartup - entry.FetchedAt > (entry.Failed ? RetryAfter : KeepFor))
                Refresh(entry, key, ask, Priority.Hover);
            return entry;
        }

        private static Task<double> Refresh(Entry entry, string key, Func<string, Ticket, Task<double?>> ask, Priority priority)
        {
            var ticket = new Ticket(priority);
            Task<double> running = Run(entry, key, ask, ticket);
            if (!running.IsCompleted)
            {
                entry.Pending = running;
                entry.Ticket = ticket;
            }
            return running;
        }

        private static async Task<double> Run(Entry entry, string key, Func<string, Ticket, Task<double?>> ask, Ticket ticket)
        {
            bool failed = true;
            try
            {
                double? price = await ask(key, ticket);
                if (price != null)
                {
                    entry.Price = Math.Max(0d, price.Value);
                    failed = false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not get a flea price: {e.Message}");
            }
            finally
            {
                if (failed)
                    Limiter.Failed();
                else
                    Limiter.Succeeded();
                entry.Pending = null;
                entry.Ticket = null;
                entry.Failed = failed;
                entry.FetchedAt = Time.realtimeSinceStartup;
            }

            return entry.Price;
        }

        private static Entry Find(Dictionary<string, Entry> cache, string key)
        {
            if (cache.TryGetValue(key, out Entry entry))
                return entry;

            if (cache.Count >= MostCached)
            {
                float now = Time.realtimeSinceStartup;
                foreach (string old in cache.Where(p => p.Value.Pending == null && now - p.Value.FetchedAt > KeepFor).Select(p => p.Key).ToList())
                    cache.Remove(old);
                if (cache.Count >= MostCached * 3 / 4)
                    cache.Clear();
            }

            return cache[key] = new Entry();
        }

        private static List<Item> Parts(Item item)
        {
            return item is Weapon
                ? item.GetAllItems().Where(part => part == item || Attached(part, item)).ToList()
                : new List<Item> { item };
        }

        private static async Task<double> GetAverage(Item item, float maxAge, Priority priority)
        {
            List<Item> parts = Parts(item);
            await Task.WhenAll(parts.Select(part => Ask(Average, part.TemplateId.ToString(), AskAverage, maxAge, priority)));
            return Sum(item, parts);
        }

        private static bool TryAverage(Item item, out double price)
        {
            price = 0d;
            List<Item> parts = Parts(item);
            bool loading = false;
            foreach (Item part in parts)
            {
                Entry entry = Peek(Average, part.TemplateId.ToString(), AskAverage);
                if (entry.Price <= 0d && entry.Pending != null)
                    loading = true;
            }

            if (loading)
                return false;
            price = Sum(item, parts);
            return true;
        }

        // A gun is its parts added up. The item itself needs a price, a part without one counts as nothing.
        private static double Sum(Item item, List<Item> parts)
        {
            double price = 0d;
            foreach (Item part in parts)
            {
                double each = Average.TryGetValue(part.TemplateId.ToString(), out Entry entry) ? entry.Price : 0d;
                if (each <= 0d && part == item)
                    return 0d;

                int rounds = part != item && part.CurrentAddress is StackSlotItemAddress ? Math.Max(1, part.StackObjectsCount) : 1;
                price += each * rounds;
            }

            return Math.Max(0d, price * Quality(item));
        }

        private static double Quality(Item item)
        {
            if (item is Weapon)
                return Math.Round(PartQuality(item), 5);

            double sum = 0;
            int count = 0;
            foreach (Item part in item.GetAllItems())
            {
                if (part != item && !Attached(part, item))
                    continue;
                if (part.TryGetItemComponent(out ArmorComponent armor) && armor.Repairable.TemplateDurability <= 0)
                    continue;

                sum += PartQuality(part);
                count++;
            }

            return count == 0 ? 1 : Math.Min(Math.Round(sum / count, 5), 1);
        }

        private static double PartQuality(Item item)
        {
            double quality = 1;
            if (item.TryGetItemComponent(out MedKitComponent medKit) && medKit.MaxHpResource > 0)
                quality = medKit.HpResource / medKit.MaxHpResource;
            else if (item.TryGetItemComponent(out RepairableComponent repairable))
                quality = repairable.TemplateDurability > 0 ? Math.Sqrt(repairable.Durability / repairable.TemplateDurability) : 1;
            else if (item.TryGetItemComponent(out FoodDrinkComponent food) && food.MaxResource > 0)
                quality = food.HpPercent / food.MaxResource;
            else if (item.TryGetItemComponent(out KeyComponent key) && key.NumberOfUsages > 0 && key.Template.MaximumNumberOfUsage > 0)
                quality = (key.Template.MaximumNumberOfUsage - key.NumberOfUsages) / (double)key.Template.MaximumNumberOfUsage;
            else if (item.TryGetItemComponent(out ResourceComponent resource) && resource.MaxResource > 0 && resource.Value < resource.MaxResource)
                quality = resource.Value / resource.MaxResource;
            else if (item is RepairKit repairKit && repairKit.MaxRepairResource > 0)
                quality = repairKit.Resource / repairKit.MaxRepairResource;

            return quality <= 0 ? 0.01 : quality;
        }

        private class FleaAnswer
        {
            public double Price { get; set; }
        }

        // The item and its attached parts as the server stores them, without the item's own stack count,
        // so the answer is for one of it.
        private static string Serialize(Item root)
        {
            var ids = new Dictionary<Item, string>();
            foreach (Item item in root.GetAllItems())
            {
                if (item == root || Attached(item, root))
                    ids[item] = (ids.Count + 1).ToString("x24");
            }

            var parts = new List<object>();
            foreach (Item item in ids.Keys)
            {
                ItemAddress address = item.CurrentAddress;
                var upd = Upd(item, item != root);
                parts.Add(new Dictionary<string, object>
                {
                    ["_id"] = ids[item],
                    ["_tpl"] = item.TemplateId.ToString(),
                    ["parentId"] = item == root ? null : ids[address.Container.ParentItem],
                    ["slotId"] = item == root ? null : address.Container.ID,
                    ["upd"] = upd.Count > 0 ? upd : null
                });
            }

            return JsonConvert.SerializeObject(parts, Json);
        }

        private static bool Attached(Item item, Item root)
        {
            while (item != root)
            {
                ItemAddress address = item.CurrentAddress;
                if (address == null || address is GridItemAddress)
                    return false;
                item = address.Container.ParentItem;
                if (item == null)
                    return false;
            }

            return true;
        }

        private static Dictionary<string, object> Upd(Item item, bool withStack)
        {
            var upd = new Dictionary<string, object>();
            if (withStack && item.StackObjectsCount > 1)
                upd["StackObjectsCount"] = item.StackObjectsCount;

            if (item.TryGetItemComponent(out RepairableComponent repairable))
                upd["Repairable"] = new { Durability = Round(repairable.Durability), MaxDurability = Round(repairable.MaxDurability) };
            if (item.TryGetItemComponent(out MedKitComponent medKit))
                upd["MedKit"] = new { HpResource = Round(medKit.HpResource) };
            if (item.TryGetItemComponent(out FoodDrinkComponent food))
                upd["FoodDrink"] = new { HpPercent = Round(food.HpPercent) };
            if (item.TryGetItemComponent(out KeyComponent key) && key.NumberOfUsages > 0)
                upd["Key"] = new { key.NumberOfUsages };
            if (item.TryGetItemComponent(out ResourceComponent resource) && resource.Value < resource.MaxResource)
                upd["Resource"] = new { Value = Round(resource.Value), UnitsConsumed = Round(resource.MaxResource - resource.Value) };
            if (item.TryGetItemComponent(out RepairKitComponent repairKit))
                upd["RepairKit"] = new { Resource = Round(repairKit.Resource) };
            return upd;
        }

        private static double Round(float value)
        {
            return Math.Round(value, 2);
        }
    }
}
