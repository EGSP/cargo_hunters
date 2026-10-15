using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace CargoHunters.InventoryMod;

/// <summary>
/// Загружает локальный инструмент исследования сцены; команды Unity исполняются в Update.
/// </summary>
[BepInPlugin("local.cargohunters.inventory", "Cargo Hunters Inventory Mod", Version)]
public sealed class Plugin : BasePlugin
{
    public const string Version = "0.5.0";
    public override void Load()
    {
        InventorySettings.Load();
        AddComponent<BridgeBehaviour>();
        Log.LogInfo("CARGO_INVENTORY_MOD_READY: local development bridge loaded.");
    }

    public override bool Unload() { BridgeBehaviour.Stop(); return true; }
}
