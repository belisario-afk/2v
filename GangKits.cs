using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("GangKits", "Gemini", "1.8.0")]
    [Description("Automatic permanent gang outfits and weapons. Includes admin testing tools.")]
    public class GangKits : RustPlugin
    {
        // Track gang kit weapons dropped on ground (to clean up)
        private HashSet<uint> _droppedKitItems = new HashSet<uint>();
        
        // Track which gang each player is blooded into (persists across deaths AND server restarts)
        private Dictionary<ulong, string> _playerGangs = new Dictionary<ulong, string>();
        
        // Track wounded players to prevent kit loss on DBNO (down but not out)
        private HashSet<ulong> _woundedPlayers = new HashSet<ulong>();
        
        // Track players currently getting kit (to prevent re-entry)
        private HashSet<ulong> _givingKit = new HashSet<ulong>();
        
        // Data file for persisting player gang assignments
        private const string DataFileName = "GangKits_PlayerGangs";
        
        [PluginReference]
        private Plugin HoodWars;

        private const string PermAdmin = "hoodwars.admin";

        private class GangKit
        {
            public List<string> Clothing;
            public Dictionary<string, ulong> Skins;
            public string Weapon;
            public ulong WeaponSkin;
        }

        private Dictionary<string, GangKit> _kits;

        #region Configuration

        protected override void LoadDefaultConfig()
        {
            Config["Kits"] = new Dictionary<string, object>
            {
                ["Westside Pirus"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.balaclava" },
                    ["Skins"] = new Dictionary<string, object> 
                    { 
                        ["hoodie"] = 3637124708, 
                        ["pants"] = 3637161289, 
                        ["mask.balaclava"] = 3637136628 
                    },
                    ["Weapon"] = "pistol.semiauto",
                    ["WeaponSkin"] = 3639125341 
                },
                ["Northside Vagos"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.bandana" },
                    ["Skins"] = new Dictionary<string, object> 
                    { 
                        ["hoodie"] = 3637132959, 
                        ["pants"] = 3637162032, 
                        ["mask.bandana"] = 3637144551 
                    },
                    ["Weapon"] = "pistol.semiauto",
                    ["WeaponSkin"] = 3639147460 
                },
                ["Southside Sureños"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.balaclava" },
                    ["Skins"] = new Dictionary<string, object> 
                    { 
                        ["hoodie"] = 3637133781, 
                        ["pants"] = 3637162360, 
                        ["mask.balaclava"] = 3637136303 
                    },
                    ["Weapon"] = "pistol.semiauto",
                    ["WeaponSkin"] = 3639138385 
                },
                ["Eastside Disciples"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.bandana" },
                    ["Skins"] = new Dictionary<string, object> 
                    { 
                        ["hoodie"] = 3637126631, 
                        ["pants"] = 3637163268, 
                        ["mask.bandana"] = 3637149926 
                    },
                    ["Weapon"] = "pistol.semiauto",
                    ["WeaponSkin"] = 3639144230 
                }
            };
            SaveConfig();
        }

        private void Init()
        {
            _kits = new Dictionary<string, GangKit>();
            var configKits = Config["Kits"] as Dictionary<string, object>;
            if (configKits == null) return;

            foreach (var kvp in configKits)
            {
                var data = kvp.Value as Dictionary<string, object>;
                _kits[kvp.Key] = new GangKit
                {
                    Clothing = (data["Clothing"] as List<object>).Select(x => x.ToString()).ToList(),
                    Skins = (data["Skins"] as Dictionary<string, object>).ToDictionary(x => x.Key, x => ulong.Parse(x.Value.ToString())),
                    Weapon = data["Weapon"].ToString(),
                    WeaponSkin = ulong.Parse(data["WeaponSkin"].ToString())
                };
            }
            
            // Load saved player gang data
            LoadPlayerGangData();
        }
        
        private void Unload()
        {
            // Save player gang data when plugin unloads
            SavePlayerGangData();
        }
        
        private void OnServerSave()
        {
            // Save player gang data periodically with server saves
            SavePlayerGangData();
        }
        
        private void LoadPlayerGangData()
        {
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, string>>(DataFileName);
                if (data != null)
                {
                    _playerGangs.Clear();
                    foreach (var kvp in data)
                    {
                        if (ulong.TryParse(kvp.Key, out ulong playerId))
                        {
                            _playerGangs[playerId] = kvp.Value;
                        }
                    }
                    Puts($"[GangKits] Loaded {_playerGangs.Count} player gang assignments from data file.");
                }
            }
            catch
            {
                Puts("[GangKits] No existing player gang data found, starting fresh.");
            }
        }
        
        private void SavePlayerGangData()
        {
            try
            {
                var data = _playerGangs.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);
                Interface.Oxide.DataFileSystem.WriteObject(DataFileName, data);
                Puts($"[GangKits] Saved {_playerGangs.Count} player gang assignments to data file.");
            }
            catch (System.Exception ex)
            {
                Puts($"[GangKits] ERROR saving player gang data: {ex.Message}");
            }
        }

        #endregion

        #region Core Logic

        // API method to register a player's gang (called when they blood in)
        private void API_RegisterPlayerGang(ulong playerId, string gangName)
        {
            Puts($"[DEBUG] API_RegisterPlayerGang: playerId={playerId}, gangName={gangName}");
            if (!string.IsNullOrEmpty(gangName) && gangName != "Neutral" && gangName != "Neutral Ground")
            {
                _playerGangs[playerId] = gangName;
                SavePlayerGangData(); // Persist immediately
                Puts($"[DEBUG] Player {playerId} registered to gang: {gangName}");
            }
        }
        
        // API method to get a player's registered gang
        private string API_GetPlayerGang(ulong playerId)
        {
            return _playerGangs.ContainsKey(playerId) ? _playerGangs[playerId] : null;
        }

        // API method for external plugins to give a player their gang kit
        private void API_GiveGangKit(BasePlayer player, string gangName = null)
        {
            Puts($"[DEBUG] API_GiveGangKit called for player: {player?.displayName ?? "null"}, gangName: {gangName ?? "null"}");
            
            // If gang name provided, also register them
            if (!string.IsNullOrEmpty(gangName) && gangName != "Neutral" && gangName != "Neutral Ground")
            {
                _playerGangs[player.userID] = gangName;
                SavePlayerGangData(); // Persist immediately
            }
            
            GiveGangKit(player, gangName);
        }

        private void GiveGangKit(BasePlayer player, string forcedGang = null)
        {
            if (player == null)
            {
                Puts("[DEBUG] GiveGangKit: player is null, aborting.");
                return;
            }
            
            // Prevent re-entry while giving kit
            if (_givingKit.Contains(player.userID)) return;
            _givingKit.Add(player.userID);

            string gangName = forcedGang ?? GetPlayerGang(player);
            Puts($"[DEBUG] GiveGangKit: player={player.displayName}, forcedGang={forcedGang ?? "null"}, resolvedGang={gangName}");
            
            if (string.IsNullOrEmpty(gangName) || gangName == "Neutral Ground" || gangName == "Neutral") 
            {
                Puts($"[DEBUG] GiveGangKit: Gang name is '{gangName}', not a valid gang - aborting.");
                _givingKit.Remove(player.userID);
                return;
            }

            if (!_kits.TryGetValue(gangName, out var kit)) 
            {
                Puts($"[DEBUG] GiveGangKit: No kit found for gang '{gangName}'. Available kits: {string.Join(", ", _kits.Keys)}");
                _givingKit.Remove(player.userID);
                return;
            }
            
            Puts($"[DEBUG] GiveGangKit: Found kit for '{gangName}', giving items...");
            int clothingGiven = 0;
            bool weaponGiven = false;

            // 1. Clothing - only give if slot is not occupied by a non-kit item
            foreach (var shortname in kit.Clothing)
            {
                // Skip if player has a non-kit item in this slot
                if (HasNonKitClothing(player, shortname)) 
                {
                    Puts($"[DEBUG] Skipping {shortname} - player has non-kit clothing equipped");
                    continue;
                }
                
                // Skip if player already has this kit item anywhere
                if (HasGangKitClothing(player, shortname))
                {
                    Puts($"[DEBUG] Skipping {shortname} - player already has gang kit clothing");
                    continue;
                }

                ulong skin = kit.Skins.ContainsKey(shortname) ? kit.Skins[shortname] : 0;
                Item item = ItemManager.CreateByName(shortname, 1, skin);
                if (item != null)
                {
                    item.name = "GANG_KIT_ITEM"; 
                    if (item.MoveToContainer(player.inventory.containerWear))
                    {
                        clothingGiven++;
                        Puts($"[DEBUG] Gave {shortname} (skin: {skin}) to wear container");
                    }
                    else 
                    {
                        item.Remove();
                        Puts($"[DEBUG] Failed to give {shortname} - removed item");
                    }
                }
                else
                {
                    Puts($"[DEBUG] Failed to create item: {shortname}");
                }
            }

            // 2. Weapon - only give if player doesn't already have it anywhere
            if (!HasGangKitWeaponAnywhere(player, kit.Weapon))
            {
                Item weapon = ItemManager.CreateByName(kit.Weapon, 1, kit.WeaponSkin);
                if (weapon != null)
                {
                    weapon.name = "GANG_KIT_WEAPON";
                    BaseProjectile proj = weapon.GetHeldEntity() as BaseProjectile;
                    if (proj != null)
                    {
                        proj.primaryMagazine.contents = 0;
                        proj.SendNetworkUpdate();
                    }

                    if (weapon.MoveToContainer(player.inventory.containerBelt))
                    {
                        weaponGiven = true;
                        Puts($"[DEBUG] Gave weapon {kit.Weapon} (skin: {kit.WeaponSkin}) to belt container");
                    }
                    else 
                    {
                        weapon.Remove();
                        Puts($"[DEBUG] Failed to give weapon {kit.Weapon} (skin: {kit.WeaponSkin}) - removed item");
                    }
                }
                else
                {
                    Puts($"[DEBUG] Failed to create weapon: {kit.Weapon} (skin: {kit.WeaponSkin})");
                }
            }
            else
            {
                Puts($"[DEBUG] Skipping weapon - player already has gang kit {kit.Weapon}");
            }
            
            Puts($"[DEBUG] GiveGangKit complete: {clothingGiven} clothing items, weapon: {weaponGiven}");
            _givingKit.Remove(player.userID);
        }

        // Check if player has a non-kit clothing item in a specific slot
        private bool HasNonKitClothing(BasePlayer player, string shortname)
        {
            return player.inventory.containerWear.itemList.Any(item => 
                item.info.shortname == shortname && item.name != "GANG_KIT_ITEM");
        }
        
        // Check if player has gang kit clothing anywhere (wear or main)
        private bool HasGangKitClothing(BasePlayer player, string shortname)
        {
            return player.inventory.containerWear.itemList.Any(item => 
                item.info.shortname == shortname && item.name == "GANG_KIT_ITEM") ||
                player.inventory.containerMain.itemList.Any(item =>
                item.info.shortname == shortname && item.name == "GANG_KIT_ITEM");
        }
        
        // Check if player has gang kit weapon anywhere (belt or main)
        private bool HasGangKitWeaponAnywhere(BasePlayer player, string shortname)
        {
            return player.inventory.containerBelt.itemList.Any(item => 
                item.info.shortname == shortname && item.name == "GANG_KIT_WEAPON") ||
                player.inventory.containerMain.itemList.Any(item =>
                item.info.shortname == shortname && item.name == "GANG_KIT_WEAPON");
        }

        private string GetPlayerGang(BasePlayer player)
        {
            // First check our internal tracking (persistent across deaths/locations)
            if (_playerGangs.ContainsKey(player.userID))
            {
                Puts($"[DEBUG] GetPlayerGang: Found cached gang for {player.displayName}: {_playerGangs[player.userID]}");
                return _playerGangs[player.userID];
            }
            
            // Fall back to HoodWars for fresh players
            if (HoodWars == null) 
            {
                Puts($"[DEBUG] GetPlayerGang: HoodWars not loaded, returning Neutral");
                return "Neutral";
            }
            
            object result = HoodWars.Call("GetPlayerGangName", player.userID);
            string gangName = result?.ToString() ?? "Neutral";
            Puts($"[DEBUG] GetPlayerGang: HoodWars returned '{gangName}' for {player.displayName}");
            
            // Cache the result if it's a valid gang
            if (!string.IsNullOrEmpty(gangName) && gangName != "Neutral" && gangName != "Neutral Ground")
            {
                _playerGangs[player.userID] = gangName;
            }
            
            return gangName;
        }

        #endregion

        #region Commands

        [ChatCommand("testallkits")]
        private void CmdTestAllKits(BasePlayer player)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                SendReply(player, "You do not have permission to use this command.");
                return;
            }

            player.inventory.Strip();
            SendReply(player, "<color=#ffff00>ADMIN:</color> Clearing inventory and spawning all 4 Gang Kits for testing...");

            // Give all kits directly to main inventory for testing (bypasses normal kit logic)
            int totalItems = 0;
            foreach (var kvp in _kits)
            {
                string gangName = kvp.Key;
                GangKit kit = kvp.Value;
                
                SendReply(player, $"<color=#aaaaaa>Adding {gangName} kit...</color>");
                
                // Add clothing items to main inventory
                foreach (var shortname in kit.Clothing)
                {
                    ulong skin = kit.Skins.ContainsKey(shortname) ? kit.Skins[shortname] : 0;
                    Item item = ItemManager.CreateByName(shortname, 1, skin);
                    if (item != null)
                    {
                        item.name = $"TEST_{gangName}"; // Mark as test item, not GANG_KIT_ITEM
                        if (item.MoveToContainer(player.inventory.containerMain))
                        {
                            totalItems++;
                        }
                        else
                        {
                            item.Remove();
                            Puts($"[DEBUG] CmdTestAllKits: Failed to give {shortname} for {gangName}");
                        }
                    }
                }
                
                // Add weapon to main inventory
                Item weapon = ItemManager.CreateByName(kit.Weapon, 1, kit.WeaponSkin);
                if (weapon != null)
                {
                    weapon.name = $"TEST_{gangName}"; // Mark as test item
                    BaseProjectile proj = weapon.GetHeldEntity() as BaseProjectile;
                    if (proj != null)
                    {
                        proj.primaryMagazine.contents = 0;
                        proj.SendNetworkUpdate();
                    }

                    if (weapon.MoveToContainer(player.inventory.containerMain))
                    {
                        totalItems++;
                    }
                    else if (weapon.MoveToContainer(player.inventory.containerBelt))
                    {
                        totalItems++;
                    }
                    else
                    {
                        weapon.Remove();
                        Puts($"[DEBUG] CmdTestAllKits: Failed to give weapon for {gangName}");
                    }
                }
            }
            
            SendReply(player, $"<color=#66ff66>Done!</color> Spawned {totalItems} items from all 4 gang kits. Check your main inventory.");
        }

        #endregion

        #region Hooks

        // When player connects to server (includes reconnects)
        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            Puts($"[DEBUG] OnPlayerConnected: {player.displayName}");
            
            // Give kit on connect with a delay to ensure player is fully loaded
            timer.Once(2f, () => {
                if (player == null || !player.IsConnected) return;
                
                string gangName = GetPlayerGang(player);
                if (string.IsNullOrEmpty(gangName) || gangName == "Neutral" || gangName == "Neutral Ground")
                {
                    Puts($"[DEBUG] OnPlayerConnected: Player {player.displayName} has no gang, skipping kit");
                    return;
                }
                Puts($"[DEBUG] OnPlayerConnected giving kit for gang: {gangName}");
                GiveGangKit(player);
            });
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            Puts($"[DEBUG] OnPlayerRespawned: {player.displayName}");
            
            // Always give kit on respawn - this is permanent kit behavior
            timer.Once(0.5f, () => {
                if (player == null || !player.IsConnected) return;
                
                // Give kit on respawn (true permanent kit behavior)
                string gangName = GetPlayerGang(player);
                if (string.IsNullOrEmpty(gangName) || gangName == "Neutral" || gangName == "Neutral Ground")
                {
                    Puts($"[DEBUG] OnPlayerRespawned: Player {player.displayName} has no gang, skipping kit");
                    return;
                }
                Puts($"[DEBUG] OnPlayerRespawned giving kit for gang: {gangName}");
                GiveGangKit(player);
            });
        }
        
        // When an item is added to a container - handle kit removal when equipping other clothing
        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            BasePlayer player = container.playerOwner;
            if (player == null || item == null) return;
            
            // Only care about wear container
            if (container != player.inventory.containerWear) return;
            
            // If a non-kit clothing item is being equipped, remove any gang kit item of the same type
            if (item.name != "GANG_KIT_ITEM")
            {
                string shortname = item.info.shortname;
                Puts($"[DEBUG] OnItemAddedToContainer: Non-kit item {shortname} added to wear");
                
                // Find and remove gang kit clothing of same type
                timer.Once(0.1f, () => {
                    if (player == null || !player.IsConnected) return;
                    
                    // Remove from wear
                    var kitItemInWear = player.inventory.containerWear.itemList
                        .FirstOrDefault(i => i.info.shortname == shortname && i.name == "GANG_KIT_ITEM" && i != item);
                    if (kitItemInWear != null)
                    {
                        Puts($"[DEBUG] Removing gang kit {shortname} from wear - replaced by non-kit item");
                        kitItemInWear.Remove();
                    }
                    
                    // Also remove from main if it's there
                    var kitItemInMain = player.inventory.containerMain.itemList
                        .FirstOrDefault(i => i.info.shortname == shortname && i.name == "GANG_KIT_ITEM");
                    if (kitItemInMain != null)
                    {
                        Puts($"[DEBUG] Removing gang kit {shortname} from main - player equipped non-kit item");
                        kitItemInMain.Remove();
                    }
                });
            }
        }

        private void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            BasePlayer player = container.playerOwner;
            if (player == null || item == null) return;

            // Only care about wear container for clothing restoration
            if (container != player.inventory.containerWear)
                return;
                
            // If it's a gang kit item being removed, don't re-trigger kit give
            if (item.name == "GANG_KIT_ITEM")
                return;
                
            Puts($"[DEBUG] OnItemRemovedFromContainer: Non-kit {item.info.shortname} removed from wear");
            
            // Non-kit clothing item removed - check if we need to restore gang kit in that slot
            timer.Once(0.5f, () => {
                if (player == null || !player.IsConnected) return;
                GiveGangKit(player); // Only give missing items
            });
        }
        
        // Prevent gang kit items from being moved to boxes/external storage
        private object CanMoveItem(Item item, PlayerInventory playerLoot, ItemContainerId targetContainerId, int targetSlot, int amount)
        {
            if (item == null) return null;
            
            // Check if this is a gang kit item
            if (item.name == "GANG_KIT_ITEM" || item.name == "GANG_KIT_WEAPON")
            {
                // Find the target container
                ItemContainer targetContainer = playerLoot?.FindContainer(targetContainerId);
                if (targetContainer == null) return null;
                
                BasePlayer player = playerLoot?.baseEntity as BasePlayer;
                if (player == null) return null;
                
                // Allow moving within player's own inventory (belt, main, wear)
                bool isPlayerContainer = targetContainer == player.inventory.containerMain ||
                                         targetContainer == player.inventory.containerBelt ||
                                         targetContainer == player.inventory.containerWear;
                
                if (!isPlayerContainer)
                {
                    Puts($"[DEBUG] CanMoveItem: Blocking gang kit item {item.info.shortname} from being moved to external storage");
                    // Send message to player
                    player.ChatMessage("<color=#ff6666>You cannot put gang kit items in storage.</color>");
                    return false;
                }
            }
            
            return null;
        }
        
        // Also block via CanLootEntity hook for dropping into world containers
        private object CanAcceptItem(ItemContainer container, Item item, int targetPos)
        {
            if (item == null || container == null) return null;
            
            // Check if this is a gang kit item
            if (item.name == "GANG_KIT_ITEM" || item.name == "GANG_KIT_WEAPON")
            {
                // Check if container belongs to a player's inventory
                BasePlayer player = container.playerOwner;
                if (player != null)
                {
                    // Allow moving within player's own inventory
                    bool isPlayerContainer = container == player.inventory.containerMain ||
                                             container == player.inventory.containerBelt ||
                                             container == player.inventory.containerWear;
                    
                    if (isPlayerContainer) return null;
                }
                
                // Block moving to any other container (boxes, etc)
                Puts($"[DEBUG] CanAcceptItem: Blocking gang kit item {item.info.shortname} from external container");
                return ItemContainer.CanAcceptResult.CannotAccept;
            }
            
            return null;
        }
        
        // Block recycling of gang kit items
        private object CanRecycle(Recycler recycler, Item item)
        {
            if (item == null) return null;
            
            if (item.name == "GANG_KIT_ITEM" || item.name == "GANG_KIT_WEAPON")
            {
                Puts($"[DEBUG] CanRecycle: Blocking gang kit item {item.info.shortname} from recycling");
                return false;
            }
            
            return null;
        }

        // Track when items are dropped on the ground
        private void OnItemDropped(Item item, BaseEntity entity)
        {
            if (item == null) return;
            
            // If this is a gang kit item dropped on ground
            if (item.name == "GANG_KIT_ITEM" || item.name == "GANG_KIT_WEAPON")
            {
                Puts($"[DEBUG] Gang kit item dropped on ground: {item.info.shortname}");
                
                // Find who dropped it
                BasePlayer dropper = item.GetOwnerPlayer();
                
                // If player is wounded/DBNO, don't destroy yet - they might recover
                if (dropper != null && _woundedPlayers.Contains(dropper.userID))
                {
                    Puts($"[DEBUG] Player is wounded - keeping dropped kit item for potential recovery");
                    return;
                }
                
                // Otherwise destroy it - gang kit items can't be dropped while alive
                timer.Once(0.1f, () => {
                    if (entity != null && !entity.IsDestroyed)
                    {
                        Puts($"[DEBUG] Destroying dropped gang kit item: {item.info.shortname}");
                        entity.Kill();
                    }
                });
                
                // Give back kit to the player who dropped it
                if (dropper != null && dropper.IsConnected)
                {
                    timer.Once(0.2f, () => {
                        if (dropper == null || !dropper.IsConnected) return;
                        GiveGangKit(dropper);
                    });
                }
            }
        }

        private void OnPlayerCorpseSpawned(BasePlayer player, PlayerCorpse corpse)
        {
            if (corpse == null) return;
            Puts($"[DEBUG] OnPlayerCorpseSpawned: {player?.displayName ?? "unknown"}");
            
            int removed = 0;
            foreach (var container in corpse.containers)
            {
                for (int i = container.itemList.Count - 1; i >= 0; i--)
                {
                    var item = container.itemList[i];
                    if (item.name == "GANG_KIT_ITEM" || item.name == "GANG_KIT_WEAPON")
                    {
                        Puts($"[DEBUG] Removing gang kit item from corpse: {item.info.shortname}");
                        item.Remove();
                        removed++;
                    }
                }
            }
            Puts($"[DEBUG] Removed {removed} gang kit items from corpse");
        }
        
        // When player dies, ensure we clean up any dropped gang kit items
        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null) return;
            Puts($"[DEBUG] OnPlayerDeath: {player.displayName}");
            
            // Remove from wounded tracking since they're fully dead now
            _woundedPlayers.Remove(player.userID);
            
            // Note: Corpse handling is done in OnPlayerCorpseSpawned
            // Respawn kit is handled in OnPlayerRespawned
        }
        
        // Player got downed/wounded (DBNO state) - NOT full death
        private void OnPlayerWound(BasePlayer player)
        {
            if (player == null) return;
            Puts($"[DEBUG] OnPlayerWound (DBNO): {player.displayName}");
            _woundedPlayers.Add(player.userID);
        }
        
        // Player recovered from wounded state (got back up)
        private void OnPlayerRecover(BasePlayer player)
        {
            if (player == null) return;
            Puts($"[DEBUG] OnPlayerRecover: {player.displayName}");
            _woundedPlayers.Remove(player.userID);
            
            // Give kit back since they recovered (weapon may have been dropped while wounded)
            timer.Once(0.5f, () => {
                if (player == null || !player.IsConnected) return;
                GiveGangKit(player); // Only give missing items
            });
        }

        #endregion
    }
}