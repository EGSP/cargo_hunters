using System.Text.Json;
using System.Text.Encodings.Web;
using BepInEx;
using Game.ItemsSystem;
using Game.ItemsSystem.ItemComponents;
using Game.WeaponSystem.ItemComponents;
using Game.UI.Controllers;
using Game.UI.Views;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UObject = UnityEngine.Object;

namespace CargoHunters.InventoryMod;

/// <summary>Снимает дерево реальных предметов из доступных игровых контейнеров.</summary>
internal static class InventoryCommands
{
    private const int MaxItems = 20000;
    private const int MaxDepth = 32;
    private static IItemsManager? itemManager;

    public static object GetSettings() => new { outputPath = InventorySettings.OutputPath, settingsFile = Path.Combine(Paths.ConfigPath, "cargo-inventory-settings.json") };

    public static object SetSettings(JsonElement request)
    {
        if (!request.TryGetProperty("outputPath", out var path) || path.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new ArgumentException("outputPath is required and must be a string or null");
        return InventorySettings.Set(path.ValueKind == JsonValueKind.Null ? null : path.GetString());
    }

    public static object Export(JsonElement request)
    {
        string? requestedPath = null;
        if (request.TryGetProperty("outputPath", out var path))
        {
            if (path.ValueKind != JsonValueKind.String) throw new ArgumentException("outputPath must be an absolute path string");
            requestedPath = path.GetString();
        }
        var outputPath = string.IsNullOrWhiteSpace(requestedPath) ? InventorySettings.OutputPath : requestedPath.Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("No output path was provided. Pass outputPath or configure it with POST /inventory/settings.");
        if (!Path.IsPathRooted(outputPath)) throw new ArgumentException("outputPath must be absolute");
        outputPath = Path.GetFullPath(outputPath);

        var controller = FindCharacterController();
        if (controller == null || controller._account == null)
            throw new ArgumentException("The character inventory screen is not loaded; open the inventory and retry.");
        itemManager = controller._itemsManager;

        var groups = new List<InventoryGroup>();
        var playerRoots = new List<(IItemsContainer? Container, string Label)>();
        var playerInventory = controller._account.GetInventory();
        if (playerInventory != null) playerRoots.Add((playerInventory, "inventory"));
        var equipment = controller._account.GetEquipment();
        if (equipment != null)
        {
            var equipped = new List<IItem>();
            for (var slot = 0; slot < equipment.SlotsCount; slot++)
            {
                var item = equipment.GetItemFromSlot(slot);
                if (item != null) equipped.Add(item);
            }
            groups.Add(BuildEquipmentGroup(equipped));
        }
        groups.Add(BuildGroup("player", "Инвентарь игрока", playerRoots));

        var pageController = controller._itemsContainerController ?? FindPageController(controller.gameObject);
        var warehouse = pageController?.ItemsContainer;
        groups.Add(BuildGroup("warehouse", "Склад", warehouse == null ? Array.Empty<(IItemsContainer?, string)>() : new[] { ((IItemsContainer?)warehouse, "currentPage") }));

        var inspected = controller.TargetItemsContainer;
        var inspectedGroup = BuildGroup("inspected", "Осматриваемый объект", inspected == null ? Array.Empty<(IItemsContainer?, string)>() : new[] { ((IItemsContainer?)inspected, "target") });
        var knownPointers = groups.SelectMany(x => x.RootPointers).ToHashSet();
        if (inspectedGroup.RootPointers.Any(knownPointers.Contains))
            inspectedGroup = new InventoryGroup("inspected", "Осматриваемый объект", "sameContainerAsAnotherGroup", Array.Empty<object>(), 0, Array.Empty<long>());
        groups.Add(inspectedGroup);

        var snapshot = new
        {
            schemaVersion = 1,
            capturedAtUtc = DateTimeOffset.UtcNow,
            scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            itemCounting = "Each distinct item object is emitted as its own entry; stack quantity is recorded on that entry and entries are never merged.",
            groups = groups.Select(x => new { x.Key, x.Name, x.Status, itemCount = x.Count, topLevelItemCount = x.Items.Count, items = x.Items }).ToArray()
        };

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var tempPath = outputPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            WriteIndented = true,
            MaxDepth = 128,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, outputPath, true);
        return new { saved = true, outputPath, groups = groups.Select(x => new { x.Key, x.Status, itemCount = x.Count, topLevelItemCount = x.Items.Count }).ToArray(), totalItemEntries = groups.Sum(x => x.Count) };
    }

    private static CharacterLootingItemsContainerController? FindCharacterController()
    {
        CharacterLootingItemsContainerController? fallback = null;
        foreach (var gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (gameObject == null || !gameObject.scene.IsValid()) continue;
            var controller = gameObject.GetComponent<CharacterLootingItemsContainerController>();
            if (controller == null || controller._account == null) continue;
            if (controller.IsVisible) return controller;
            fallback ??= controller;
        }
        return fallback;
    }

    private static PageItemsContainerController? FindPageController(GameObject root)
    {
        var pending = new Stack<Transform>();
        pending.Push(root.transform);
        while (pending.Count > 0)
        {
            var transform = pending.Pop();
            if (transform == null) continue;
            var page = transform.gameObject.GetComponent<PageItemsContainerController>();
            if (page != null && page.ItemsContainer != null) return page;
            for (var i = 0; i < transform.childCount; i++) pending.Push(transform.GetChild(i));
        }
        return null;
    }

    private static InventoryGroup BuildEquipmentGroup(List<IItem> items)
    {
        var entries = new List<object>();
        var visited = new HashSet<long>();
        var budget = MaxItems;
        foreach (var item in items)
        {
            var entry = BuildItem(item, "equipment", null, 0, visited, ref budget);
            if (entry != null) entries.Add(entry);
        }
        return new InventoryGroup("equipment", "Экипировка игрока", "available", entries, CountEntries(entries), Array.Empty<long>());
    }

    private static InventoryGroup BuildGroup(string key, string name, IEnumerable<(IItemsContainer? Container, string Label)> sources)
    {
        var entries = new List<object>();
        var rootPointers = new List<long>();
        var visitedContainers = new HashSet<long>();
        var visitedItems = new HashSet<long>();
        var budget = MaxItems;
        var found = false;
        foreach (var (container, label) in sources)
        {
            if (container == null) continue;
            found = true;
            var pointer = container.Pointer.ToInt64();
            rootPointers.Add(pointer);
            if (!visitedContainers.Add(pointer)) continue;
            var count = Math.Clamp(container.ItemsCount, 0, MaxItems);
            for (var index = 0; index < count && budget > 0; index++)
            {
                var item = container.GetItemAtIndex(index);
                if (item == null) continue;
                var position = SafePosition(container, item);
                var entry = BuildItem(item, label, position, 0, visitedItems, ref budget, visitedContainers);
                if (entry != null) entries.Add(entry);
            }
        }
        var status = !found ? "notAvailable" : budget == 0 ? "truncatedAtItemLimit" : entries.Count == 0 ? "empty" : "available";
        return new InventoryGroup(key, name, status, entries, CountEntries(entries), rootPointers);
    }

    private static object? BuildItem(IItem item, string source, string? position, int depth, HashSet<long> visitedItems, ref int budget, HashSet<long>? visitedContainers = null)
    {
        if (item == null || budget <= 0) return null;
        var pointer = item.Pointer.ToInt64();
        if (!visitedItems.Add(pointer)) return null;
        budget--;
        var templateId = item.TemplateId.ToString();
        var quantity = 1;
        try
        {
            var stackable = item.GetComponent<StackableComponent>();
            if (stackable != null) quantity = Math.Max(1, stackable.Quantity);
            else
            {
                var commodity = item.GetComponent<CommodityComponent>();
                if (commodity != null) quantity = Math.Max(1, commodity.Count);
            }
        }
        catch { }

        var children = new List<object>();
        if (depth < MaxDepth)
        {
            var container = item.Container;
            if (container != null)
                AddContainerChildren(container, "contents", depth + 1, visitedItems, ref budget, visitedContainers, children);
            try
            {
                var slots = item.GetComponent<SlotItemsContainerComponent>()?._data?.GetItemSlots();
                if (slots != null)
                    for (var i = 0; i < slots.Count && budget > 0; i++)
                    {
                        var slot = slots[i];
                        if (slot == null) continue;
                        var child = slot.ContainedItem;
                        if (child == null) continue;
                        var entry = BuildItem(child, "module", "slot:" + slot.Type, depth + 1, visitedItems, ref budget, visitedContainers);
                        if (entry != null) children.Add(entry);
                    }
            }
            catch { }
            try
            {
                var cartridges = item.GetComponent<MagazineComponent>()?.GetCartridgesData();
                if (cartridges != null)
                    for (var i = 0; i < cartridges.Count && budget > 0; i++)
                    {
                        var cartridge = cartridges[i];
                        if (cartridge.Count <= 0) continue;
                        budget--;
                        var cartridgeId = cartridge.Id.ToString();
                        var cartridgeItem = itemManager?.FindTemplate(cartridge.Id);
                        children.Add(new
                        {
                            itemId = (string?)null, templateId = cartridgeId, name = (cartridgeItem == null ? null : ResolveName(cartridgeItem)) ?? "Патроны " + cartridgeId,
                            quantity = cartridge.Count, source = "loadedMagazine", position = (string?)null,
                            contents = Array.Empty<object>(), contentsTruncated = false
                        });
                    }
            }
            catch { }
        }
        return new
        {
            itemId = item.Id.ToString(), templateId, name = ResolveName(item) ?? "Предмет " + templateId,
            quantity, source, position, contents = children,
            contentsTruncated = depth >= MaxDepth || budget <= 0
        };
    }

    private static void AddContainerChildren(IItemsContainer container, string source, int depth, HashSet<long> visitedItems, ref int budget, HashSet<long>? visitedContainers, List<object> children)
    {
        if (budget <= 0 || depth > MaxDepth) return;
        var pointer = container.Pointer.ToInt64();
        if (visitedContainers != null && !visitedContainers.Add(pointer)) return;
        var count = Math.Clamp(container.ItemsCount, 0, MaxItems);
        for (var i = 0; i < count && budget > 0; i++)
        {
            var item = container.GetItemAtIndex(i);
            if (item == null) continue;
            var entry = BuildItem(item, source, SafePosition(container, item), depth, visitedItems, ref budget, visitedContainers);
            if (entry != null) children.Add(entry);
        }
    }

    private static string? ResolveName(IItem item)
    {
        try
        {
            var component = item.GetComponent<BaseComponent>();
            if (component == null) return null;
            var reference = component.LocalizedName;
            if (reference.IsEmpty) reference = component.LocalizedShortName;
            if (reference.IsEmpty) return null;
            TableReference table = reference.TableReference;
            TableEntryReference entry = reference.TableEntryReference;
            var localized = LocalizationSettings.StringDatabase.GetLocalizedString(table, entry, (UnityEngine.Localization.Locale)null!, FallbackBehavior.UseProjectSettings, (Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>)null!);
            return string.IsNullOrWhiteSpace(localized) ? null : localized;
        }
        catch { return null; }
    }

    private static string? SafePosition(IItemsContainer container, IItem item)
    {
        try { return container.GetItemPosition(item).ToString(); }
        catch { return null; }
    }

    private static int CountEntries(IEnumerable<object> entries) => entries.Sum(CountEntry);

    private static int CountEntry(object entry)
    {
        var count = 1;
        if (entry.GetType().GetProperty("contents")?.GetValue(entry) is System.Collections.IEnumerable children)
            foreach (var child in children)
                if (child != null) count += CountEntry(child);
        return count;
    }

    private sealed record InventoryGroup(string Key, string Name, string Status, IReadOnlyList<object> Items, int Count, IReadOnlyList<long> RootPointers);
}
