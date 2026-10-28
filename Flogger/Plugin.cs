using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using EFT.UI.Ragfair;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Flogger
{
    public enum SellTo
    {
        WhoeverPaysMost,
        TradersOnly,
        FleaOnly
    }

    public enum FleaRefused
    {
        Ask,
        SellToTrader,
        KeepIt
    }

    public enum SellSpeed
    {
        NearInstant,
        Fast,
        Normal,
        MaxProfit,
        Custom
    }

    // Read by Configuration Manager by name.
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
        public int? Order;
    }

    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.SPT.core", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.fums.flogger";
        public const string Name = "Fums-Flogger";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<SellTo> Where;
        internal static ConfigEntry<int> FleaMargin;
        internal static ConfigEntry<int> ListingShare;
        internal static ConfigEntry<SellSpeed> Speed;
        internal static ConfigEntry<FleaRefused> IfFleaRefuses;
        internal static ConfigEntry<bool> IncludeFence;
        internal static ConfigEntry<bool> PriceInMenu;
        internal static ConfigEntry<bool> InMenu;
        internal static ConfigEntry<KeyboardShortcut> SellKey;

        private static readonly ConfigurationManagerAttributes SpeedShown = Shown(80, false);
        private static readonly ConfigurationManagerAttributes ShareShown = Shown(70);
        private static readonly ConfigurationManagerAttributes MarginShown = Shown(60);
        private static MethodInfo _rebuild;
        private static object _configManager;
        private bool _patched;

        internal static bool BetterFlea => FleaValue.HasBetterFlea || BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fums.betterflea.client");

        internal static int ListingPercent
        {
            get
            {
                if (!BetterFlea)
                    return ListingShare.Value;
                switch (Speed.Value)
                {
                    case SellSpeed.NearInstant:
                        return 85;
                    case SellSpeed.Fast:
                        return 90;
                    case SellSpeed.Normal:
                        return 100;
                    case SellSpeed.MaxProfit:
                        return 110;
                    default:
                        return ListingShare.Value;
                }
            }
        }

        // Better Flea sells a listing at 85% or less within seconds, so there's no wait to make up for.
        internal static int MarginPercent => BetterFlea && ListingPercent <= 85 ? 0 : FleaMargin.Value;

        private static ConfigurationManagerAttributes Shown(int order, bool browsable = true)
        {
            return new ConfigurationManagerAttributes { Order = order, Browsable = browsable };
        }

        // Only show the settings that do something: the slider on Custom (or without Better Flea), the margin when
        // it's used. Configuration Manager is told to redraw when that changes.
        internal static void ShowSettings()
        {
            bool betterFlea = BetterFlea;
            bool share = !betterFlea || Speed.Value == SellSpeed.Custom;
            bool margin = !betterFlea || ListingPercent > 85;
            if (SpeedShown.Browsable == betterFlea && ShareShown.Browsable == share && MarginShown.Browsable == margin)
                return;

            SpeedShown.Browsable = betterFlea;
            ShareShown.Browsable = share;
            MarginShown.Browsable = margin;
            try
            {
                if (_configManager == null
                    && BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.bepis.bepinex.configurationmanager", out PluginInfo info) && info.Instance != null)
                {
                    _configManager = info.Instance;
                    _rebuild = AccessTools.Method(info.Instance.GetType(), "BuildSettingList");
                }
                _rebuild?.Invoke(_configManager, null);
            }
            catch (Exception e)
            {
                Log.LogWarning($"Couldn't refresh the settings window: {e.Message}");
            }
        }

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("1. General", "Enabled", true,
                new ConfigDescription("Turn Quick sell on or off, both the menu entry and the key.",
                    null, Shown(100)));
            Where = Config.Bind("1. General", "Sell to", SellTo.WhoeverPaysMost,
                new ConfigDescription("WhoeverPaysMost: the flea or the best trader, whichever gives more money. "
                    + "TradersOnly / FleaOnly: never use the other one.",
                    null, Shown(90)));
            Speed = Config.Bind("1. General", "Sell speed (Better Flea)", SellSpeed.NearInstant,
                new ConfigDescription("How fast Better Flea sells a Quick sell listing, and so what it asks. "
                    + "NearInstant: 85% of the flea value, sells in seconds. Fast: 90%, within about a minute. "
                    + "Normal: 100%, a few minutes. MaxProfit: 110%, can take hours and may not sell. "
                    + "Custom: set the listing price yourself. Only shown when Better Flea is installed.",
                    null, SpeedShown));
            ListingShare = Config.Bind("1. General", "Flea listing price (% of flea value)", 85,
                new ConfigDescription("Flea items are listed at this share of what they're worth on the flea "
                    + "in their current condition: Better Flea's own price when it's installed, otherwise the "
                    + "average flea price adjusted for condition. With Better Flea, 85% or less sells fastest, "
                    + "up to 100% sells slower, above it rarely sells and above 115% never does. "
                    + "With Better Flea this is only shown when Sell speed is Custom.",
                    new AcceptableValueRange<int>(30, 115), ShareShown));
            FleaMargin = Config.Bind("1. General", "Flea must pay this much more (%)", 10,
                new ConfigDescription("The flea only wins if, after its fee, it pays at least this much more "
                    + "than the best trader. Flea money comes later by mail and depends on the item selling, "
                    + "a trader pays on the spot. Hidden and not used with Better Flea when the listing price is "
                    + "85% or less, since those listings sell within seconds.",
                    new AcceptableValueRange<int>(0, 200), MarginShown));
            IfFleaRefuses = Config.Bind("1. General", "If the flea cannot take an item", FleaRefused.Ask,
                new ConfigDescription("When the flea would pay the most for an item but can't take it (banned, not found in raid, "
                    + "bought from a trader, no free offer slots, not enough roubles for the fee, or the listing is "
                    + "turned down). Ask: show why and ask before selling it to the best trader. "
                    + "SellToTrader: sell it to the best trader without asking. KeepIt: keep it.",
                    null, Shown(50)));
            IncludeFence = Config.Bind("1. General", "Include Fence", true,
                new ConfigDescription("Let Fence buy things when he pays the most.", null, Shown(40)));
            SellKey = Config.Bind("1. General", "Quick sell key", new KeyboardShortcut(KeyCode.Space),
                new ConfigDescription("Hold this and left click an item to sell it to the best trader, or right click "
                    + "to list it on the flea. If the flea can't take it, it's handled the way 'If the flea cannot "
                    + "take an item' says.",
                    null, Shown(30)));
            InMenu = Config.Bind("1. General", "Show Quick sell in the item menu", true,
                new ConfigDescription("Add Quick sell to the right click menu. The Quick sell key works either way.",
                    null, Shown(20)));
            PriceInMenu = Config.Bind("1. General", "Show where it goes in the menu", true,
                new ConfigDescription("Show the buyer and price next to Quick sell.", null, Shown(10)));

            var harmony = new Harmony(Guid);
            try
            {
                Menu.Apply(harmony);
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();
                Logger.LogError($"{Name} {Version} could not patch the item menu, Quick sell is off: {e}");
                return;
            }

            _patched = true;
            ShowSettings();
            Logger.LogInfo($"{Name} {Version} loaded.");
        }

        private void Update()
        {
            if (_patched)
                Menu.Update();
        }
    }

    // UI Fixes' multi-select, looked up by name so UI Fixes isn't needed to load.
    internal static class Selection
    {
        private static bool _looked;
        private static MethodInfo _getCount;
        private static MethodInfo _getItems;

        public static List<Item> For(Item clicked)
        {
            var single = new List<Item> { clicked };
            if (!Find())
                return single;

            try
            {
                if ((int)_getCount.Invoke(null, null) < 2)
                    return single;

                var selected = ((IEnumerable<Item>)_getItems.Invoke(null, null)).ToList();
                return selected.Contains(clicked) ? selected : single;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't read the UI Fixes selection, selling just the one item: {e.Message}");
                return single;
            }
        }

        private static bool Find()
        {
            if (!_looked)
            {
                _looked = true;
                Type controller = AccessTools.TypeByName("UIFixes.MultiSelectController");
                if (controller != null)
                {
                    _getCount = AccessTools.Method(controller, "GetCount");
                    _getItems = AccessTools.Method(controller, "GetItems");
                }
                if (_getCount != null && _getItems != null)
                    Plugin.Log.LogInfo("UI Fixes found, Quick sell works on multi-select.");
            }
            return _getCount != null && _getItems != null;
        }
    }

    // The label has to be final when the menu opens (that's when it fits itself on screen), so prices are
    // fetched ahead: on hover, and for the whole stash in the background.
    internal static class Menu
    {
        private const string EntryId = "flogger.quicksell";
        private const float WarmEvery = 600f;

        private static readonly HashSet<string> Explained = new HashSet<string>();
        private static Transform _inspect;
        private static Item _hovered;
        private static float _nextUpdate;
        private static float _warmedAt = float.NegativeInfinity;
        private static bool _warming;

        private static Sprite Icon => EFTHardSettings.Instance?.StaticIcons?.GetBigCurrencySign(CurrencyUtil.ROUBLE_ID);

        public static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.GetItemContextInteractions),
                             new[] { typeof(ItemContext), typeof(Action) })
                         ?? throw new MissingMethodException(nameof(ItemUiContext), nameof(ItemUiContext.GetItemContextInteractions));
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(Menu), nameof(Postfix)));

            try
            {
                var button = AccessTools.Method(typeof(InteractionButtonsContainer), nameof(InteractionButtonsContainer.CreateContextButton),
                    new[]
                    {
                        typeof(string), typeof(string), typeof(SimpleContextMenuButton), typeof(RectTransform), typeof(Sprite),
                        typeof(Action), typeof(Action), typeof(bool), typeof(bool)
                    });
                var dynamic = AccessTools.Method(typeof(InteractionButtonsContainer), nameof(InteractionButtonsContainer.CreateDynamicContextButton));
                harmony.Patch(button, postfix: new HarmonyMethod(typeof(Menu), nameof(ButtonPostfix)));
                harmony.Patch(dynamic, postfix: new HarmonyMethod(typeof(Menu), nameof(DynamicPostfix)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't move Quick sell to the top of the menu, it stays at the bottom: {e.Message}");
            }

            try
            {
                harmony.Patch(AccessTools.Method(typeof(ItemView), nameof(ItemView.OnPointerEnter)),
                    postfix: new HarmonyMethod(typeof(Menu), nameof(HoverPostfix)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't ask for prices on hover: {e.Message}");
            }

            try
            {
                MethodInfo click = typeof(ItemView).GetInterfaceMap(typeof(IPointerClickHandler)).TargetMethods[0];
                harmony.Patch(click, prefix: new HarmonyMethod(typeof(Menu), nameof(ClickPrefix)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't hook item clicks, the Quick sell key is off: {e.Message}");
            }
        }

        // Key + left click sells to a trader, key + right click lists on the flea.
        private static bool ClickPrefix(ItemView __instance, PointerEventData eventData)
        {
            bool left = eventData.button == PointerEventData.InputButton.Left;
            if (!left && eventData.button != PointerEventData.InputButton.Right)
                return true;
            if (!Plugin.Enabled.Value || Market.InRaid || !Plugin.SellKey.Value.IsPressed())
                return true;

            Item item = __instance.Item;
            if (item == null || __instance.ItemContext?.ViewType != EItemViewType.Inventory)
                return true;

            try
            {
                string why = QuickSeller.Unsellable(item, QuickSeller.PlayerStash, QuickSeller.PlayerController, true);
                if (why != null)
                    NotificationManager.DisplayWarningNotification($"Can't Quick sell {QuickSeller.Name(item)}: {why}");
                else
                    QuickSeller.Sell(Selection.For(item), left ? SellTo.TradersOnly : SellTo.WhoeverPaysMost, !left);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Quick sell key failed: {e}");
            }
            return false;
        }

        private static void Postfix(ItemUiContext __instance, ItemContext itemContext, Action closeAction,
            ContextInteractions<EItemInfoButton> __result)
        {
            try
            {
                Item item = itemContext?.Item;
                if (!Plugin.Enabled.Value || !Plugin.InMenu.Value || __result == null || item == null || Market.InRaid)
                    return;
                if (itemContext.ViewType != EItemViewType.Inventory)
                {
                    Hidden(item, $"not offered in the {itemContext.ViewType} view");
                    return;
                }

                string why = QuickSeller.Unsellable(item, QuickSeller.PlayerStash, QuickSeller.PlayerController, true);
                if (why != null)
                {
                    Hidden(item, why);
                    return;
                }

                foreach (var old in __result.DynamicInteractions.Where(d => d.Id == EntryId).ToList())
                    __result.RemoveDynamicInteraction(old.Key);

                Action close = closeAction ?? __instance.ContextMenu.Close;
                __result.method_2(EntryId, Caption(item), () =>
                {
                    var items = Selection.For(item);
                    close();
                    QuickSeller.Sell(items, Plugin.Where.Value);
                }, Icon);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Couldn't add Quick sell to the menu: {e}");
            }
        }

        private static void Hidden(Item item, string why)
        {
            if (Explained.Add(item.TemplateId + why.Split('(')[0]))
                Plugin.Log.LogInfo($"No Quick sell for {QuickSeller.Name(item)}: {why}");
        }

        private static void ButtonPostfix(string key, SimpleContextMenuButton __result)
        {
            if (key == nameof(EItemInfoButton.Inspect) || key == nameof(EItemInfoButton.Examine))
                _inspect = __result.Transform;
        }

        // Dynamic entries are added last; move Quick sell up under Inspect. The inspect window's fixed-width
        // button grid has no room for the price, so it's left out there.
        private static void DynamicPostfix(InteractionButtonsContainer __instance, DynamicContextInteraction interaction)
        {
            RectTransform container = __instance._buttonsContainer;
            if (interaction?.Id != EntryId || container == null || container.childCount == 0)
                return;

            Transform entry = container.GetChild(container.childCount - 1);
            if (__instance.GetComponentInParent<ItemSpecificationPanel>() != null)
            {
                entry.gameObject.SetActive(false);
                return;
            }

            foreach (UnityEngine.UI.Image image in entry.GetComponentsInChildren<UnityEngine.UI.Image>())
            {
                if (image.sprite != null && image.sprite == interaction.Icon)
                    image.preserveAspect = true;
            }

            if (_inspect != null && _inspect.parent == container && entry != _inspect)
                entry.SetSiblingIndex(_inspect.GetSiblingIndex() + 1);
        }

        private static void HoverPostfix(ItemView __instance)
        {
            Item item = __instance.Item;
            if (item == null || item == _hovered || !Plugin.Enabled.Value || !Plugin.PriceInMenu.Value
                || Plugin.Where.Value == SellTo.TradersOnly || Market.InRaid)
                return;

            _hovered = item;
            try
            {
                FleaValue.TryGet(item, out _);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't ask for a price on hover: {e.Message}");
            }
        }

        public static void Update()
        {
            if (Time.realtimeSinceStartup < _nextUpdate)
                return;
            _nextUpdate = Time.realtimeSinceStartup + 1f;
            Plugin.ShowSettings();

            if (Market.InRaid)
            {
                _warmedAt = float.NegativeInfinity;
                return;
            }
            if (!Plugin.Enabled.Value || !Plugin.PriceInMenu.Value || Market.Session?.Profile == null)
                return;

            try
            {
                _ = Market.LoadTraderPrices();
                Warm();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't ask for prices ahead of the menu: {e.Message}");
            }
        }

        // One request at a time so a hovered item never waits behind the stash, and a few items a frame.
        private static async void Warm()
        {
            if (_warming || Time.realtimeSinceStartup - _warmedAt < WarmEvery || Plugin.Where.Value == SellTo.TradersOnly)
                return;

            _warming = true;
            try
            {
                Item equipment = QuickSeller.PlayerEquipment;
                var items = new List<Item>();
                if (QuickSeller.PlayerStash is Stash stash)
                    items.AddRange(stash.GetAllItems());
                if (equipment != null)
                    items.AddRange(equipment.GetAllItems());

                int priced = 0;
                foreach (Item item in items)
                {
                    bool worn = item.CurrentAddress?.Container?.ParentItem == equipment;
                    if (!(worn || item.CurrentAddress is GridItemAddress) || item is Money || item.QuestItem)
                        continue;

                    await FleaValue.Get(item, WarmEvery);
                    if (++priced % 25 == 0)
                        await Task.Yield();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't price the stash ahead of time: {e.Message}");
            }
            finally
            {
                _warming = false;
                _warmedAt = Time.realtimeSinceStartup;
            }
        }

        private static string Caption(Item item)
        {
            ItemController controller = QuickSeller.PlayerController;
            Stash stash = QuickSeller.PlayerStash;
            List<Item> picked = Selection.For(item);
            List<Item> items = QuickSeller.WithContents(picked.Count == 1 ? picked : QuickSeller.Sellable(picked, stash, controller, null));
            string plain = items.Count > 1 ? $"Quick sell ({items.Count} items)" : "Quick sell";
            if (!Plugin.PriceInMenu.Value)
                return plain;

            try
            {
                _ = Market.LoadTraderPrices();
                bool ready = Market.TraderPricesLoaded;
                RagFair ragFair = Market.OpenFlea;
                bool flea = Plugin.Where.Value != SellTo.TradersOnly;

                var sales = new List<Sale>();
                foreach (Item each in items)
                {
                    double value = 0d;
                    if (flea && !FleaValue.TryGet(each, out value))
                        ready = false;
                    else if (ready)
                        sales.Add(Sale.For(each, value, ragFair, controller, Plugin.Where.Value));
                }
                if (!ready)
                    return plain;

                QuickSeller.Plan(sales, Market.FreeOffers(ragFair), sales.Any(s => s.FleaPossible) ? QuickSeller.Roubles(stash) : 0d,
                    Plugin.Where.Value);

                double total = 0d;
                double lost = 0d;
                Sale last = null;
                foreach (Sale sale in sales)
                {
                    if (sale.ToFlea)
                        total += sale.FleaNet;
                    else if (sale.HasTrader)
                        total += sale.Trader.Roubles;
                    if (sale.FleaLocked)
                        lost += sale.FleaNet - sale.Trader.Roubles;
                    last = sale;
                }

                if (items.Count > 1)
                {
                    if (total <= 0d)
                        return plain;
                    return lost >= 1d
                        ? $"Quick sell ({items.Count} items, ~{QuickSeller.Money(total)}, ~{QuickSeller.Money(lost)} lost to flea locks)"
                        : $"Quick sell ({items.Count} items, ~{QuickSeller.Money(total)})";
                }

                if (last == null)
                    return plain;
                string locked = last.FleaLocked ? $", flea locked ~{QuickSeller.Money(last.FleaNet)}" : "";
                if (total <= 0d)
                    return !last.HasTrader && !last.FleaPossible ? $"Quick sell (can't be sold{locked})" : plain;
                return last.ToFlea
                    ? $"Quick sell (flea ~{QuickSeller.Money(total)})"
                    : $"Quick sell ({last.Trader.Trader.LocalizedName} {QuickSeller.Money(total)}{locked})";
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't price {item.TemplateId} for the menu: {e.Message}");
                return plain;
            }
        }
    }
}
