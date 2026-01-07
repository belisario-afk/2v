using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("DriveBySedanGangs", "belisario-afk + Gemini + Copilot", "2.9.0")]
    [Description("Spawn sedan gangs via command; sedans stalk players with 3 gang scientists that shoot from the car and on foot, then despawn when too far or dead.")]
    public class DriveBySedanGangs : RustPlugin
    {
        #region Plugin References

        // HoodWars plugin reference for gang territory integration
        [PluginReference]
        private Plugin HoodWars;

        #endregion

        #region HoodWars Data Types (for reading HoodWars_CoreData.json directly)

        // These mirror the data structures in HoodWars.cs
        private class HoodWarsStoredData
        {
            public Dictionary<ulong, HoodWarsPlayerInfo> Players = new Dictionary<ulong, HoodWarsPlayerInfo>();
        }

        private class HoodWarsPlayerInfo
        {
            public int HomeHood = 4; // 0=West, 1=North, 2=South, 3=East, 4=Neutral
        }

        // Gang names corresponding to HomeHood enum values
        private static readonly string[] GangNames = { "Westside Pirus", "Northside Vagos", "Southside Sureños", "Eastside Disciples", "Neutral" };

        #endregion

        #region Data Types

        private class GangVisuals
        {
            public List<string> Clothing;
            public Dictionary<string, ulong> Skins;
            public string Weapon = "pistol.semiauto";
            public ulong WeaponSkin = 0;
        }

        private class DriveByState
        {
            public ulong TargetID;
            public List<ScientistNPC> Shooters = new List<ScientistNPC>();
            public float LastShootTime;
            public int LastShooterIndex = -1;
            public string GangName;
        }

        #endregion

        #region Fields / Constants

        private readonly Dictionary<string, GangVisuals> _gangKits = new Dictionary<string, GangVisuals>();
        private readonly Dictionary<string, string> _borderSpawns = new Dictionary<string, string>();

        // Combat-oriented scientist prefab
        private const string PrefabScientist =
            "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_cargo.prefab";

        private readonly HashSet<ulong> _driveByNPCs = new HashSet<ulong>();

        private const string DefaultGangName = "Westside Pirus";
        private const string NeutralTerritory = "Neutral";
        private const string NeutralGround = "Neutral Ground";

        // Debug mode - set to true in config to see debug messages in console
        private bool _debugMode = false;

        private const string SedanPrefab = "assets/content/vehicles/sedan_a/sedantest.entity.prefab";

        private const int DefaultSedansPerPlayer = 1;
        private const float SpawnRadius = 35f;
        private const float FollowUpdateInterval = 0.1f;
        private const float MaxSpeed = 11f;
        private const float Acceleration = 45f;
        private const float BrakeForce = 50f;
        private const float TurnTorque = 14f;
        private const float MaxSteerAngleDeg = 55f;
        private const float MinDistanceToPlayer = 8f;
        private const float TeleportDistance = 300f;    // if car > this, we retire it
        private const float SpawnHeightCheck = 30f;
        private const float SpawnAboveGround = 1.0f;
        private const int GroundLayerMask = -1;

        private const float AttackDistance = 18f;       // car stops & deploys here
        private const float DeployDelaySeconds = 1f;  // slight delay before deploy

        private const float ScientistHealth = 50f;
        private const float ScientistMoveSpeed = 4.5f;

        // Manual drive-by shooting tuning
        private const float MinShootDistance = 10f;
        private const float MaxShootDistance = 60f;
        private const float ShootInterval = 0.4f;

        // playerID -> list of sedans
        private readonly Dictionary<ulong, List<BaseEntity>> _playerSedans =
            new Dictionary<ulong, List<BaseEntity>>();

        // sedan -> scientists owned by that sedan
        private readonly Dictionary<BaseEntity, List<ScientistNPC>> _sedanScientists =
            new Dictionary<BaseEntity, List<ScientistNPC>>();

        // scientist -> seat they are mounted in (for proper, player-like dismount)
        private readonly Dictionary<ScientistNPC, BaseMountable> _scientistSeats =
            new Dictionary<ScientistNPC, BaseMountable>();

        // sedan -> fully deployed (scientists have been dismounted)
        private readonly HashSet<BaseEntity> _deployedSedans =
            new HashSet<BaseEntity>();

        // sedan -> already scheduled deploy timer
        private readonly HashSet<BaseEntity> _deployScheduled =
            new HashSet<BaseEntity>();

        // sedan -> manual shooting state
        private readonly Dictionary<BaseEntity, DriveByState> _driveByStates =
            new Dictionary<BaseEntity, DriveByState>();

        // sedan -> flagged for safe retire (so timers/logic ignore it)
        private readonly HashSet<BaseEntity> _retiringSedans =
            new HashSet<BaseEntity>();

        // Territory tracking for automatic drive-by spawning
        private readonly Dictionary<ulong, string> _playerLastTerritory =
            new Dictionary<ulong, string>();
        
        // Cooldown tracking for territory-based spawns (prevents spam)
        private readonly Dictionary<ulong, float> _playerTerritorySpawnCooldown =
            new Dictionary<ulong, float>();
        
        private const float TerritoryCheckInterval = 2f;  // Check player territories every 2 seconds
        private const float TerritorySpawnCooldown = 300f; // 5 minute cooldown between territory spawns

        #endregion

        #region Config & Gang Loading

        protected override void LoadDefaultConfig()
        {
            Config["Visuals"] = new Dictionary<string, object>
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

            Config["Border Spawns"] = new Dictionary<string, object>
            {
                ["Westside Pirus"] = "west",
                ["Northside Vagos"] = "north",
                ["Southside Sureños"] = "south",
                ["Eastside Disciples"] = "east"
            };

            Config["DebugMode"] = false;

            SaveConfig();
        }

        private void LoadGangConfig()
        {
            _gangKits.Clear();

            var visualData = Config["Visuals"] as Dictionary<string, object>;
            if (visualData != null)
            {
                foreach (var kvp in visualData)
                {
                    var data = kvp.Value as Dictionary<string, object>;
                    if (data == null) continue;

                    var clothingList = data["Clothing"] as List<object>;
                    var skinDict = data["Skins"] as Dictionary<string, object>;

                    if (clothingList == null || skinDict == null)
                        continue;

                    // Read weapon config with defaults
                    string weapon = "pistol.semiauto";
                    ulong weaponSkin = 0;
                    
                    object weaponValue;
                    if (data.TryGetValue("Weapon", out weaponValue) && weaponValue != null)
                        weapon = weaponValue.ToString();
                    
                    object weaponSkinValue;
                    if (data.TryGetValue("WeaponSkin", out weaponSkinValue) && weaponSkinValue != null)
                        ulong.TryParse(weaponSkinValue.ToString(), out weaponSkin);

                    _gangKits[kvp.Key] = new GangVisuals
                    {
                        Clothing = clothingList.Select(x => x.ToString()).ToList(),
                        Skins = skinDict.ToDictionary(
                            x => x.Key,
                            x => ulong.Parse(x.Value.ToString())
                        ),
                        Weapon = weapon,
                        WeaponSkin = weaponSkin
                    };
                    
                    Puts($"[DriveBySedanGangs] Loaded gang kit: {kvp.Key} - {clothingList.Count} clothing items, weapon: {weapon}");
                }
            }
            else
            {
                Puts("[DriveBySedanGangs] WARNING: No 'Visuals' config found. Gang kits will not be loaded.");
            }
            
            Puts($"[DriveBySedanGangs] Loaded {_gangKits.Count} gang kits.");

            _borderSpawns.Clear();
            var borderCfg = Config["Border Spawns"] as Dictionary<string, object>;
            if (borderCfg != null)
            {
                foreach (var kvp in borderCfg)
                    _borderSpawns[kvp.Key] = kvp.Value.ToString().ToLower();
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Helper method to log debug messages only when debug mode is enabled
        /// </summary>
        private void DebugLog(string message)
        {
            if (_debugMode)
            {
                Puts($"[DriveBySedanGangs] DEBUG: {message}");
            }
        }

        private bool FindGroundPosition(Vector3 desired, out Vector3 groundPos)
        {
            groundPos = desired + Vector3.up * SpawnAboveGround;

            Vector3 rayStart = desired + Vector3.up * SpawnHeightCheck;
            RaycastHit hit;
            if (Physics.Raycast(
                    rayStart,
                    Vector3.down,
                    out hit,
                    SpawnHeightCheck * 2f,
                    GroundLayerMask,
                    QueryTriggerInteraction.Ignore))
            {
                groundPos = hit.point + Vector3.up * SpawnAboveGround;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Safely retire a sedan: stop plugin logic, dismount/kill NPCs, then call Kill() once.
        /// </summary>
        private void RetireSedan(BaseEntity car)
        {
            if (car == null) return;
            if (_retiringSedans.Contains(car)) return;
            _retiringSedans.Add(car);

            NextFrame(() =>
            {
                if (car == null || car.IsDestroyed) return;

                if (_sedanScientists.TryGetValue(car, out var sciList) && sciList != null)
                {
                    foreach (var npc in sciList.ToArray())
                    {
                        if (npc == null) continue;

                        if (npc.isMounted)
                        {
                            BaseMountable seat;
                            if (_scientistSeats.TryGetValue(npc, out seat) && seat != null && !seat.IsDestroyed)
                            {
                                seat.DismountAllPlayers();
                            }
                            else
                            {
                                var mountable = npc.GetMounted() as BaseMountable;
                                if (mountable != null && !mountable.IsDestroyed)
                                    mountable.DismountAllPlayers();
                            }
                        }

                        if (!npc.IsDestroyed)
                            npc.Kill();

                        _scientistSeats.Remove(npc);
                    }

                    _sedanScientists.Remove(car);
                }

                _deployedSedans.Remove(car);
                _deployScheduled.Remove(car);
                _driveByStates.Remove(car);
                _retiringSedans.Remove(car);

                if (!car.IsDestroyed)
                    car.Kill();
            });
        }

        /// <summary>
        /// Check if a player is wearing clothing with skin IDs that match a specific gang's kit.
        /// Returns true if the player has any item with a skin ID matching the gang's clothing skins.
        /// </summary>
        private bool IsPlayerWearingGangClothing(BasePlayer player, string gangName)
        {
            if (player == null || string.IsNullOrEmpty(gangName))
                return false;

            if (!_gangKits.TryGetValue(gangName, out var kit) || kit.Skins == null)
                return false;

            // Get all skin IDs used by this gang
            var gangSkinIds = new HashSet<ulong>(kit.Skins.Values);
            if (gangSkinIds.Count == 0)
                return false;

            // Check player's worn items for matching skin IDs
            if (player.inventory?.containerWear?.itemList == null)
                return false;

            foreach (var item in player.inventory.containerWear.itemList)
            {
                if (item != null && item.skin != 0 && gangSkinIds.Contains(item.skin))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Get a player's gang name by reading directly from HoodWars_CoreData.json.
        /// This bypasses Plugin.Call() which doesn't work with private methods.
        /// </summary>
        private string GetPlayerGangFromHoodWars(BasePlayer player)
        {
            if (player == null)
            {
                DebugLog("GetPlayerGangFromHoodWars - player is null");
                return null;
            }

            DebugLog($"Attempting to get gang for player {player.userID} ({player.displayName})");

            // Read directly from HoodWars data file
            try
            {
                var dataFile = Interface.Oxide.DataFileSystem.GetFile("HoodWars_CoreData");
                if (dataFile == null)
                {
                    DebugLog("HoodWars_CoreData.json not found");
                    return null;
                }

                var hoodWarsData = dataFile.ReadObject<HoodWarsStoredData>();
                if (hoodWarsData == null || hoodWarsData.Players == null)
                {
                    DebugLog("HoodWars data is null or has no Players dictionary");
                    return null;
                }

                DebugLog($"HoodWars data loaded, {hoodWarsData.Players.Count} players in database");

                if (!hoodWarsData.Players.TryGetValue(player.userID, out var playerInfo))
                {
                    DebugLog($"Player {player.userID} not found in HoodWars data");
                    return null;
                }

                DebugLog($"Player {player.userID} HomeHood = {playerInfo.HomeHood}");

                // HomeHood: 0=West, 1=North, 2=South, 3=East, 4=Neutral
                if (playerInfo.HomeHood < 0 || playerInfo.HomeHood >= GangNames.Length)
                {
                    DebugLog($"Invalid HomeHood value: {playerInfo.HomeHood}");
                    return null;
                }

                var gangName = GangNames[playerInfo.HomeHood];
                DebugLog($"Resolved gang name: '{gangName}'");

                // Check if it's a valid gang (not Neutral)
                if (gangName == NeutralTerritory || gangName == NeutralGround)
                {
                    DebugLog("Player is Neutral, returning null");
                    return null;
                }

                DebugLog($"SUCCESS! Player {player.userID} is in gang '{gangName}'");
                return gangName;
            }
            catch (Exception ex)
            {
                DebugLog($"Exception reading HoodWars data: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Scientist Creation & Death Handling

        private ScientistNPC CreateDressedGangScientist(Vector3 position, Quaternion rotation, string gangName)
        {
            var npcEntity = GameManager.server.CreateEntity(PrefabScientist, position, rotation);
            if (npcEntity == null)
            {
                Puts("[DriveBySedanGangs] ERROR: Failed to create scientist entity.");
                return null;
            }

            var npc = npcEntity as ScientistNPC;
            if (npc == null)
            {
                Puts($"[DriveBySedanGangs] ERROR: Scientist cast failed. Entity type: {npcEntity?.GetType().Name ?? "NULL"}");
                npcEntity.Kill();
                return null;
            }

            npc.Spawn();

            if (npc.net != null)
                _driveByNPCs.Add(npc.net.ID.Value);

            npc.InitializeHealth(ScientistHealth, ScientistHealth);
            npc.startHealth = ScientistHealth;
            npc.SetMaxHealth(ScientistHealth);
            npc.SetHealth(ScientistHealth);

            npc.inventory.Strip();

            if (_gangKits.TryGetValue(gangName, out var kit))
            {
                Puts($"[DriveBySedanGangs] Dressing NPC with gang kit: {gangName}");
                foreach (var itemShort in kit.Clothing)
                {
                    ulong skin = kit.Skins.ContainsKey(itemShort) ? kit.Skins[itemShort] : 0;
                    var item = ItemManager.CreateByName(itemShort, 1, skin);
                    if (item != null)
                        npc.inventory.GiveItem(item, npc.inventory.containerWear);
                }

                var weapon = ItemManager.CreateByName(kit.Weapon, 1, kit.WeaponSkin);
                if (weapon != null)
                {
                    npc.inventory.GiveItem(weapon, npc.inventory.containerBelt);
                    npc.UpdateActiveItem(weapon.uid);
                    Puts($"[DriveBySedanGangs] NPC equipped with weapon: {kit.Weapon}");
                }
                else
                {
                    Puts($"[DriveBySedanGangs] WARNING: Failed to create weapon '{kit.Weapon}' for NPC");
                }
            }
            else
            {
                Puts($"[DriveBySedanGangs] WARNING: No gang kit found for '{gangName}'. Available kits: {string.Join(", ", _gangKits.Keys)}");
            }

            npc.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);
            npc.SetPlayerFlag(BasePlayer.PlayerFlags.DisplaySash, false);

            var agent = npc.GetComponent<NavMeshAgent>();
            if (agent != null)
            {
                agent.enabled = true;
                agent.speed = 0.1f;
                agent.acceleration = 1f;
                agent.stoppingDistance = 0f;
                agent.autoBraking = true;
            }

            if (npc.Brain != null)
            {
                npc.Brain.SetEnabled(true);
                if (npc.Brain.Navigator != null)
                {
                    npc.Brain.Navigator.CanUseNavMesh = true;
                    npc.Brain.Navigator.CanUseAStar = true;
                    npc.Brain.Navigator.MaxRoamDistanceFromHome = 500f;
                }
            }

            return npc;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            var npc = entity as ScientistNPC;
            if (npc != null)
            {
                if (npc.net != null)
                    _driveByNPCs.Remove(npc.net.ID.Value);

                HandleScientistDeath(npc);
                return;
            }
        }

        private void HandleScientistDeath(ScientistNPC npc)
        {
            if (npc == null) return;

            _scientistSeats.Remove(npc);

            BaseEntity ownerCar = null;

            foreach (var kvp in _sedanScientists)
            {
                var car = kvp.Key;
                var list = kvp.Value;
                if (list == null) continue;

                if (list.Remove(npc))
                {
                    ownerCar = car;
                    break;
                }
            }

            if (ownerCar == null) return;

            if (_sedanScientists.TryGetValue(ownerCar, out var remaining))
            {
                if (remaining == null || remaining.Count == 0)
                {
                    _sedanScientists.Remove(ownerCar);
                    _deployedSedans.Remove(ownerCar);
                    _deployScheduled.Remove(ownerCar);
                    _driveByStates.Remove(ownerCar);
                    _retiringSedans.Remove(ownerCar);

                    if (ownerCar != null && !ownerCar.IsDestroyed)
                        ownerCar.Kill();
                }
            }
        }

        #endregion

        #region Lifecycle

        private void OnServerInitialized()
        {
            LoadGangConfig();
            
            // Load debug mode from config
            if (Config["DebugMode"] != null)
            {
                _debugMode = Convert.ToBoolean(Config["DebugMode"]);
            }
            
            timer.Every(FollowUpdateInterval, UpdateAllSedans);
            timer.Every(TerritoryCheckInterval, CheckPlayerTerritories);
        }

        private void Unload()
        {
            foreach (var list in _playerSedans.Values.ToArray())
            {
                foreach (var car in list.ToArray())
                {
                    if (car != null && !car.IsDestroyed)
                        car.Kill();
                }
            }

            _playerSedans.Clear();

            foreach (var kvp in _sedanScientists.ToArray())
            {
                var sciList = kvp.Value;
                if (sciList == null) continue;

                foreach (var npc in sciList.ToArray())
                {
                    if (npc != null && !npc.IsDestroyed)
                        npc.Kill();

                    if (npc != null)
                        _scientistSeats.Remove(npc);
                }
            }

            _sedanScientists.Clear();
            _deployedSedans.Clear();
            _deployScheduled.Clear();
            _driveByStates.Clear();
            _retiringSedans.Clear();
            _scientistSeats.Clear();
            _playerLastTerritory.Clear();
            _playerTerritorySpawnCooldown.Clear();
        }

        /// <summary>
        /// Check all players' territories and spawn drive-by gangs when they cross into enemy territory.
        /// </summary>
        private void CheckPlayerTerritories()
        {
            if (HoodWars == null || !HoodWars.IsLoaded)
                return;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsNpc || !player.IsConnected || player.IsDead())
                    continue;

                CheckPlayerTerritory(player);
            }
        }

        /// <summary>
        /// Check if a player has crossed into enemy territory and spawn a drive-by gang if so.
        /// </summary>
        private void CheckPlayerTerritory(BasePlayer player)
        {
            if (player == null || HoodWars == null || !HoodWars.IsLoaded)
                return;

            // Get player's gang from HoodWars using multiple fallback methods
            var playerGang = GetPlayerGangFromHoodWars(player);
            if (string.IsNullOrEmpty(playerGang))
                return; // Neutral players don't trigger territory spawns

            // Get the territory the player is currently in based on their world position (X, Z coordinates)
            // HoodWars divides the map into 4 quadrants based on X and Z coordinates
            var currentTerritory = HoodWars.Call("GetNeighborhoodNameAt", player.transform.position) as string;
            if (string.IsNullOrEmpty(currentTerritory) || currentTerritory == NeutralGround)
            {
                // Update last territory but don't spawn
                _playerLastTerritory[player.userID] = currentTerritory ?? NeutralGround;
                return;
            }

            // Get player's last known territory
            _playerLastTerritory.TryGetValue(player.userID, out var lastTerritory);

            // Debug logging to help test territory detection
            if (lastTerritory != currentTerritory)
            {
                DebugLog($"Player {player.userID} moved from '{lastTerritory ?? "null"}' to '{currentTerritory}' at position X:{player.transform.position.x:F0} Z:{player.transform.position.z:F0}");
                DebugLog($"Player's gang: {playerGang}, Current territory: {currentTerritory}, Is enemy territory: {currentTerritory != playerGang}");
            }

            // Update current territory
            _playerLastTerritory[player.userID] = currentTerritory;

            // Check if player crossed into a new enemy territory
            if (lastTerritory != currentTerritory && currentTerritory != playerGang)
            {
                // Player entered enemy territory
                // Check cooldown
                if (_playerTerritorySpawnCooldown.TryGetValue(player.userID, out var lastSpawnTime))
                {
                    var cooldownRemaining = TerritorySpawnCooldown - (Time.realtimeSinceStartup - lastSpawnTime);
                    if (cooldownRemaining > 0)
                    {
                        DebugLog($"Player {player.userID} on cooldown for {cooldownRemaining:F0} more seconds");
                        return; // Still on cooldown
                    }
                }

                // Check if player already has an active drive-by gang
                if (_playerSedans.TryGetValue(player.userID, out var existingGangs) && existingGangs.Count > 0)
                {
                    // Clean up destroyed sedans
                    existingGangs.RemoveAll(c => c == null || c.IsDestroyed);
                    if (existingGangs.Count > 0)
                    {
                        DebugLog($"Player {player.userID} already has {existingGangs.Count} active drive-by gang(s)");
                        return; // Already has active gang
                    }
                }

                // Spawn a drive-by gang from the territory they entered
                DebugLog($"TERRITORY SPAWN: Player {player.userID} ({playerGang}) crossed into {currentTerritory} territory at X:{player.transform.position.x:F0} Z:{player.transform.position.z:F0} - spawning drive-by!");
                
                // Set cooldown
                _playerTerritorySpawnCooldown[player.userID] = Time.realtimeSinceStartup;

                // Spawn the gang with the territory's gang name
                EnsureGangForPlayerWithGang(player, 1, currentTerritory);

                // Notify the player
                player.ChatMessage($"<color=#ff4444>WARNING:</color> You've entered {currentTerritory} territory! A drive-by gang has been dispatched!");
            }
        }

        #endregion

        #region Hooks

        // NOTE: no auto spawn on init/respawn/disconnect anymore.
        // Only /stalksedan and /destroysedan control gangs.

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            DestroyGangForPlayer(player);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var baseEntity = entity as BaseEntity;
            if (baseEntity == null)
                return;

            if (!IsSedan(baseEntity))
                return;

            ulong playerToClean = 0;
            bool needsCleanup = false;

            foreach (var kvp in _playerSedans.ToArray())
            {
                var list = kvp.Value;
                if (list == null)
                    continue;

                if (list.Remove(baseEntity))
                {
                    playerToClean = kvp.Key;
                    needsCleanup = true;
                    break;
                }
            }

            if (needsCleanup)
            {
                if (_playerSedans.TryGetValue(playerToClean, out var list) && (list == null || list.Count == 0))
                    _playerSedans.Remove(playerToClean);
            }

            if (_sedanScientists.TryGetValue(baseEntity, out var sciList))
            {
                foreach (var npc in sciList.ToArray())
                {
                    if (npc != null && !npc.IsDestroyed)
                        npc.Kill();

                    if (npc != null)
                        _scientistSeats.Remove(npc);
                }

                _sedanScientists.Remove(baseEntity);
            }

            _deployedSedans.Remove(baseEntity);
            _deployScheduled.Remove(baseEntity);
            _driveByStates.Remove(baseEntity);
            _retiringSedans.Remove(baseEntity);
        }

        #endregion

        #region Sedan / Gang Management

        private bool IsSedan(BaseEntity ent)
        {
            if (ent == null) return false;
            return ent.ShortPrefabName.Equals("sedantest.entity", StringComparison.OrdinalIgnoreCase)
                   || ent.PrefabName == SedanPrefab;
        }

        private void EnsureGangForPlayer(BasePlayer player, int desiredCount)
        {
            if (player == null || !player.IsConnected)
                return;

            if (!_playerSedans.TryGetValue(player.userID, out var list))
            {
                list = new List<BaseEntity>();
                _playerSedans[player.userID] = list;
            }

            // Clean invalid cars
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] == null || list[i].IsDestroyed)
                    list.RemoveAt(i);
            }

            int missing = desiredCount - list.Count;
            if (missing <= 0)
                return;

            for (int i = 0; i < missing; i++)
            {
                var car = SpawnSedanNearPlayer(player, i, desiredCount);
                if (car != null)
                    list.Add(car);
            }
        }

        private BaseEntity SpawnSedanNearPlayer(BasePlayer player, int indexInGang, int gangSize)
        {
            Vector3 playerPos = player.transform.position;

            float angle = (360f / Mathf.Max(gangSize, 1)) * indexInGang;
            float rad = angle * Mathf.Deg2Rad;

            Vector3 offset = new Vector3(
                Mathf.Cos(rad) * SpawnRadius,
                0f,
                Mathf.Sin(rad) * SpawnRadius
            );

            Vector3 samplePos = playerPos + offset;

            if (!FindGroundPosition(samplePos, out var finalPos))
            {
                PrintWarning($"[DriveBySedanGangs] Failed to find ground for sedan spawn near {player.displayName}.");
                return null;
            }

            Vector3 toPlayer = (playerPos - finalPos);
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.01f)
                toPlayer = -player.transform.forward;

            toPlayer.Normalize();
            Quaternion spawnRot = Quaternion.LookRotation(toPlayer, Vector3.up);

            BaseEntity car = GameManager.server.CreateEntity(SedanPrefab, finalPos, spawnRot, true);
            if (car == null)
            {
                PrintError("Failed to create sedan entity from prefab: " + SedanPrefab);
                return null;
            }

            car.enableSaving = false;
            car.Spawn();

            var rb = car.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = false;
            }

            car.SendNetworkUpdateImmediate();

            _deployedSedans.Remove(car);
            _deployScheduled.Remove(car);
            _retiringSedans.Remove(car);

            SeatGangScientistsInSedan(car, DefaultGangName, player);

            return car;
        }

        private void SeatGangScientistsInSedan(BaseEntity car, string gangName, BasePlayer target)
        {
            if (car == null || car.IsDestroyed) return;

            var seats = car.GetComponentsInChildren<BaseMountable>(true);
            if (seats == null || seats.Length == 0)
            {
                Puts("[DriveBySedanGangs] No seats (BaseMountable) found on sedan; cannot seat scientists.");
                return;
            }

            int needed = 3;
            var seated = new List<ScientistNPC>();

            foreach (var seat in seats)
            {
                if (needed <= 0)
                    break;

                if (seat == null || seat.IsDestroyed) continue;
                if (seat.AnyMounted()) continue;

                Vector3 spawnPos = seat.transform.position + Vector3.up * 0.1f;
                var npc = CreateDressedGangScientist(spawnPos, seat.transform.rotation, gangName);
                if (npc == null) continue;

                seat.AttemptMount(npc);

                _scientistSeats[npc] = seat;

                if (npc.Brain != null && target != null)
                {
                    if (npc.Brain.Senses?.Memory != null)
                        npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);

                    if (npc.Brain.Events?.Memory?.Entity != null)
                        npc.Brain.Events.Memory.Entity.Set(target, 0);
                }

                seated.Add(npc);
                needed--;
            }

            if (seated.Count > 0)
            {
                _sedanScientists[car] = seated;

                _driveByStates[car] = new DriveByState
                {
                    TargetID = target.userID,
                    Shooters = new List<ScientistNPC>(seated),
                    LastShootTime = 0f,
                    LastShooterIndex = -1,
                    GangName = gangName
                };
            }
        }

        private void DestroyGangForPlayer(BasePlayer player)
        {
            if (player == null)
                return;

            if (!_playerSedans.TryGetValue(player.userID, out var list))
                return;

            foreach (var car in list.ToArray())
            {
                if (car != null && !car.IsDestroyed)
                    car.Kill();

                if (car != null && _sedanScientists.TryGetValue(car, out var sciList))
                {
                    foreach (var npc in sciList.ToArray())
                    {
                        if (npc != null && !npc.IsDestroyed)
                            npc.Kill();

                        if (npc != null)
                            _scientistSeats.Remove(npc);
                    }

                    _sedanScientists.Remove(car);
                }

                _deployedSedans.Remove(car);
                _deployScheduled.Remove(car);
                _driveByStates.Remove(car);
                _retiringSedans.Remove(car);
            }

            _playerSedans.Remove(player.userID);
        }

        #endregion

        #region Driving + Deployment + Shooting

        private void UpdateAllSedans()
        {
            if (_playerSedans.Count == 0)
                return;

            var playerIds = new List<ulong>(_playerSedans.Keys);

            foreach (var playerId in playerIds)
            {
                var player = BasePlayer.FindByID(playerId) ?? BasePlayer.FindSleeping(playerId);
                if (player == null || !player.IsConnected || player.IsDead())
                {
                    if (_playerSedans.TryGetValue(playerId, out var listToClean))
                    {
                        foreach (var car in listToClean.ToArray())
                        {
                            if (car != null && !car.IsDestroyed)
                                car.Kill();

                            if (car != null && _sedanScientists.TryGetValue(car, out var sciList))
                            {
                                foreach (var npc in sciList.ToArray())
                                {
                                    if (npc != null && !npc.IsDestroyed)
                                        npc.Kill();

                                    if (npc != null)
                                        _scientistSeats.Remove(npc);
                                }

                                _sedanScientists.Remove(car);
                            }

                            _deployedSedans.Remove(car);
                            _deployScheduled.Remove(car);
                            _driveByStates.Remove(car);
                            _retiringSedans.Remove(car);
                        }
                    }

                    _playerSedans.Remove(playerId);
                    continue;
                }

                if (!_playerSedans.TryGetValue(playerId, out var sedans) || sedans == null)
                    continue;

                var sedanSnapshot = sedans.ToArray();
                var toRemoveFromPlayerList = new List<BaseEntity>();

                foreach (var car in sedanSnapshot)
                {
                    if (car == null || car.IsDestroyed)
                    {
                        toRemoveFromPlayerList.Add(car);
                        continue;
                    }

                    if (_retiringSedans.Contains(car))
                        continue;

                    // Manual shooting both mounted and on-foot
                    if (_driveByStates.TryGetValue(car, out var state))
                    {
                        MakeNPCsShootAtPlayer(car, state);
                    }

                    // If not yet deployed, drive car
                    if (!_deployedSedans.Contains(car))
                    {
                        DriveSedanTowardsPlayer(car, player);
                    }
                    else
                    {
                        // Deployed: keep chase nav updated
                        if (_sedanScientists.TryGetValue(car, out var sciList) && sciList != null)
                        {
                            foreach (var sci in sciList.ToArray())
                            {
                                if (sci == null || sci.IsDestroyed) continue;
                                if (sci.Brain == null || sci.Brain.Navigator == null) continue;

                                sci.Brain.Navigator.SetDestination(player.transform.position, BaseNavigator.NavigationSpeed.Normal);
                            }
                        }
                    }
                }

                foreach (var car in toRemoveFromPlayerList)
                {
                    sedans.Remove(car);
                }

                if (sedans.Count == 0)
                    _playerSedans.Remove(playerId);
            }
        }

        /// <summary>
        /// Manual shooting logic, used both while mounted and on foot.
        /// </summary>
        private void MakeNPCsShootAtPlayer(BaseEntity car, DriveByState ev)
        {
            if (car == null || car.IsDestroyed) return;
            if (_retiringSedans.Contains(car)) return;

            if (Time.realtimeSinceStartup - ev.LastShootTime < ShootInterval) return;

            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target == null || !target.IsAlive()) return;

            // Don't shoot gang members wearing the same gang's clothing
            if (!string.IsNullOrEmpty(ev.GangName) && IsPlayerWearingGangClothing(target, ev.GangName))
                return;

            var validShooters = new List<ScientistNPC>();
            for (int i = 0; i < ev.Shooters.Count; i++)
            {
                var npc = ev.Shooters[i];
                if (npc == null || npc.IsDestroyed) continue;
                // Still skip index 0 as "driver" when mounted,
                // but on foot it doesn't hurt – can adjust if needed.
                if (i == 0 && npc.isMounted) continue;
                validShooters.Add(npc);
            }
            if (validShooters.Count == 0) return;

            ev.LastShooterIndex = (ev.LastShooterIndex + 1) % validShooters.Count;
            var shooter = validShooters[ev.LastShooterIndex];
            if (shooter == null || shooter.IsDestroyed) return;

            float distToTarget = Vector3.Distance(shooter.transform.position, target.transform.position);
            if (distToTarget < MinShootDistance || distToTarget > MaxShootDistance) return;

            Vector3 npcEyes = shooter.eyes?.position ?? (shooter.transform.position + Vector3.up * 1.5f);
            Vector3 targetPos = target.transform.position + Vector3.up * 1.2f;
            int losMask = LayerMask.GetMask("World", "Construction", "Terrain");

            if (Physics.Linecast(npcEyes, targetPos, losMask))
                return;

            Vector3 lookDir = (targetPos - npcEyes).normalized;
            shooter.SetAimDirection(lookDir);

            var heldEntity = shooter.GetHeldEntity() as BaseProjectile;
            if (heldEntity != null)
            {
                if (heldEntity.primaryMagazine.contents <= 0)
                    heldEntity.primaryMagazine.contents = heldEntity.primaryMagazine.capacity;

                shooter.SignalBroadcast(BaseEntity.Signal.Attack, string.Empty);
                heldEntity.ServerUse();

                ev.LastShootTime = Time.realtimeSinceStartup;
            }
        }

        private void ScheduleDeploy(BaseEntity car, BasePlayer target)
        {
            if (car == null || car.IsDestroyed || target == null) return;
            if (_retiringSedans.Contains(car)) return;
            if (_deployScheduled.Contains(car)) return;

            _deployScheduled.Add(car);

            var rb = car.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }

            timer.Once(DeployDelaySeconds, () =>
            {
                if (car == null || car.IsDestroyed) return;
                if (_retiringSedans.Contains(car)) return;
                if (target == null || target.IsDead()) return;

                DeployScientistsFromSedan(car, target);
            });
        }

        private void DeployScientistsFromSedan(BaseEntity car, BasePlayer target)
        {
            if (car == null || car.IsDestroyed || target == null) return;
            if (_retiringSedans.Contains(car)) return;
            if (_deployedSedans.Contains(car)) return;

            _deployedSedans.Add(car);

            if (!_sedanScientists.TryGetValue(car, out var sciList) || sciList == null || sciList.Count == 0)
                return;

            foreach (var sci in sciList.ToArray())
            {
                if (sci == null || sci.IsDestroyed) continue;

                if (sci.isMounted)
                {
                    BaseMountable seat;
                    if (_scientistSeats.TryGetValue(sci, out seat) && seat != null && !seat.IsDestroyed)
                    {
                        seat.DismountAllPlayers();
                    }
                    else
                    {
                        var mountable = sci.GetMounted() as BaseMountable;
                        if (mountable != null && !mountable.IsDestroyed)
                            mountable.DismountAllPlayers();
                    }
                }

                _scientistSeats.Remove(sci);

                var agent = sci.GetComponent<NavMeshAgent>();
                if (agent != null)
                {
                    agent.enabled = true;
                    agent.stoppingDistance = 5f;
                    agent.speed = ScientistMoveSpeed;
                    agent.acceleration = 8f;
                    agent.autoBraking = true;
                }

                if (sci.Brain != null)
                {
                    sci.Brain.SetEnabled(true);

                    if (sci.Brain.Navigator != null)
                    {
                        sci.Brain.Navigator.CanUseNavMesh = true;
                        sci.Brain.Navigator.CanUseAStar = true;
                        sci.Brain.Navigator.MaxRoamDistanceFromHome = 500f;
                        sci.Brain.Navigator.SetDestination(target.transform.position, BaseNavigator.NavigationSpeed.Normal);
                    }

                    if (sci.Brain.Senses?.Memory != null)
                        sci.Brain.Senses.Memory.SetKnown(target, sci, sci.Brain.Senses);

                    if (sci.Brain.Events?.Memory?.Entity != null)
                        sci.Brain.Events.Memory.Entity.Set(target, 0);
                }

                sci.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);
                sci.SetPlayerFlag(BasePlayer.PlayerFlags.DisplaySash, false);
            }
        }

        private void DriveSedanTowardsPlayer(BaseEntity car, BasePlayer player)
        {
            if (car == null || car.IsDestroyed || player == null)
                return;

            if (_retiringSedans.Contains(car))
                return;

            Vector3 carPos = car.transform.position;
            Vector3 playerPos = player.transform.position;
            Vector3 toPlayer = playerPos - carPos;

            float distance = toPlayer.magnitude;

            if (distance <= AttackDistance)
            {
                ScheduleDeploy(car, player);
                return;
            }

            // If sedan falls too far behind, retire it (no teleport)
            if (distance > TeleportDistance)
            {
                RetireSedan(car);
                return;
            }

            Vector3 flatToPlayer = toPlayer;
            flatToPlayer.y = 0f;

            if (flatToPlayer.sqrMagnitude < 0.25f)
            {
                var rbStop = car.GetComponent<Rigidbody>();
                if (rbStop != null)
                    rbStop.velocity = Vector3.Lerp(rbStop.velocity, Vector3.zero, 0.15f);
                return;
            }

            flatToPlayer.Normalize();

            Vector3 forward = car.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
                forward = flatToPlayer;

            forward.Normalize();

            float angleToTarget = Vector3.SignedAngle(forward, flatToPlayer, Vector3.up);
            float steerSign = Mathf.Sign(angleToTarget);
            float steerAmount = Mathf.Clamp(Math.Abs(angleToTarget) / MaxSteerAngleDeg, 0f, 1f) * steerSign;

            var rbMove = car.GetComponent<Rigidbody>();
            if (rbMove != null)
            {
                rbMove.AddTorque(0f, steerAmount * TurnTorque, 0f, ForceMode.Acceleration);
            }

            float desiredSpeed = MaxSpeed;

            if (distance < MinDistanceToPlayer)
            {
                float t = Mathf.InverseLerp(0f, MinDistanceToPlayer, distance);
                desiredSpeed = Mathf.Lerp(MaxSpeed * 0.1f, MaxSpeed * 0.7f, t);
            }

            forward = car.transform.forward;
            forward.y = 0f;
            forward.Normalize();

            if (rbMove != null)
            {
                Vector3 currentVel = rbMove.velocity;
                Vector3 flatVel = currentVel;
                flatVel.y = 0f;
                float currentSpeed = Vector3.Dot(flatVel, forward);

                if (currentSpeed < desiredSpeed)
                {
                    rbMove.AddForce(forward * Acceleration, ForceMode.Acceleration);
                }
                else
                {
                    if (flatVel.sqrMagnitude > 0.01f)
                    {
                        Vector3 brakeDir = -flatVel.normalized;
                        rbMove.AddForce(brakeDir * BrakeForce, ForceMode.Acceleration);
                    }
                }

                rbMove.AddForce(Vector3.down * 25f, ForceMode.Acceleration);
            }
            else
            {
                car.transform.position += forward * desiredSpeed * FollowUpdateInterval;
            }

            car.SendNetworkUpdate();
        }

        #endregion

        #region API Methods

        /// <summary>
        /// API method for external plugins (like HoodWars) to spawn a drive-by gang targeting a player.
        /// </summary>
        /// <param name="playerId">The player ID to target</param>
        /// <param name="count">Number of sedans to spawn (default 1)</param>
        /// <param name="gangName">Optional gang name for visuals (uses default if not specified)</param>
        /// <returns>True if spawn succeeded</returns>
        private bool API_SpawnDriveByGang(ulong playerId, int count = 1, string gangName = null)
        {
            var player = BasePlayer.FindByID(playerId);
            if (player == null || !player.IsConnected || player.IsDead())
            {
                Puts($"[DriveBySedanGangs] API_SpawnDriveByGang: Player {playerId} not found or invalid.");
                return false;
            }

            if (count <= 0)
                count = DefaultSedansPerPlayer;

            // Use specified gang name or try to get rival gang from HoodWars
            if (string.IsNullOrEmpty(gangName))
            {
                gangName = GetRivalGangForPlayer(player);
            }

            EnsureGangForPlayerWithGang(player, count, gangName);
            Puts($"[DriveBySedanGangs] Spawned {count} drive-by sedan(s) targeting {player.displayName} (gang: {gangName}).");
            return true;
        }

        /// <summary>
        /// API method for external plugins to destroy a player's drive-by gang.
        /// </summary>
        /// <param name="playerId">The player ID whose gang to destroy</param>
        /// <returns>True if destruction succeeded</returns>
        private bool API_DestroyDriveByGang(ulong playerId)
        {
            var player = BasePlayer.FindByID(playerId);
            if (player == null)
            {
                // Player might be offline, try to clean up by player ID
                if (_playerSedans.TryGetValue(playerId, out var list))
                {
                    foreach (var car in list.ToArray())
                    {
                        if (car != null && !car.IsDestroyed)
                            car.Kill();
                    }
                    _playerSedans.Remove(playerId);
                    Puts($"[DriveBySedanGangs] Destroyed drive-by gang for offline player {playerId}.");
                    return true;
                }
                return false;
            }

            DestroyGangForPlayer(player);
            Puts($"[DriveBySedanGangs] Destroyed drive-by gang for {player.displayName}.");
            return true;
        }

        /// <summary>
        /// API method to check if a player has an active drive-by gang.
        /// </summary>
        /// <param name="playerId">The player ID to check</param>
        /// <returns>True if player has active drive-by gang</returns>
        private bool API_HasDriveByGang(ulong playerId)
        {
            if (!_playerSedans.TryGetValue(playerId, out var list))
                return false;

            // Clean up destroyed sedans
            list.RemoveAll(c => c == null || c.IsDestroyed);
            return list.Count > 0;
        }

        /// <summary>
        /// API method to get the count of sedans targeting a player.
        /// </summary>
        /// <param name="playerId">The player ID to check</param>
        /// <returns>Number of active sedans</returns>
        private int API_GetDriveByCount(ulong playerId)
        {
            if (!_playerSedans.TryGetValue(playerId, out var list))
                return 0;

            list.RemoveAll(c => c == null || c.IsDestroyed);
            return list.Count;
        }

        /// <summary>
        /// API method to check if an NPC is a drive-by gang member.
        /// </summary>
        /// <param name="npcNetId">The network ID of the NPC</param>
        /// <returns>True if NPC is part of a drive-by gang</returns>
        private bool API_IsDriveByNPC(ulong npcNetId)
        {
            return _driveByNPCs.Contains(npcNetId);
        }

        /// <summary>
        /// Get a rival gang name for a player based on HoodWars territory.
        /// </summary>
        private string GetRivalGangForPlayer(BasePlayer player)
        {
            if (HoodWars == null || !HoodWars.IsLoaded)
                return DefaultGangName;

            // Get player's gang name from HoodWars using multiple fallback methods
            var playerGang = GetPlayerGangFromHoodWars(player);
            
            // Get the neighborhood name at player's position
            var territoryGang = HoodWars.Call("GetNeighborhoodNameAt", player.transform.position) as string;

            // If player is in their own territory, spawn a rival gang
            // Otherwise, spawn the territory's gang
            if (!string.IsNullOrEmpty(territoryGang) && territoryGang != "Neutral" && territoryGang != "Neutral Ground")
            {
                // Player is in enemy territory - spawn that territory's gang
                if (playerGang != territoryGang)
                    return territoryGang;
            }

            // If player is in their own territory or neutral, pick a rival based on their gang
            if (!string.IsNullOrEmpty(playerGang) && playerGang != "Neutral")
            {
                // Return a rival gang (opposite territory)
                return GetOppositeGang(playerGang);
            }

            return DefaultGangName;
        }

        /// <summary>
        /// Get the opposite/rival gang based on gang name.
        /// </summary>
        private string GetOppositeGang(string gangName)
        {
            switch (gangName)
            {
                case "Westside Pirus": return "Eastside Disciples";
                case "Eastside Disciples": return "Westside Pirus";
                case "Northside Vagos": return "Southside Sureños";
                case "Southside Sureños": return "Northside Vagos";
                default: return DefaultGangName;
            }
        }

        /// <summary>
        /// Ensure gang for player with specific gang visuals.
        /// </summary>
        private void EnsureGangForPlayerWithGang(BasePlayer player, int desiredCount, string gangName)
        {
            if (player == null || !player.IsConnected)
                return;

            if (!_playerSedans.TryGetValue(player.userID, out var list))
            {
                list = new List<BaseEntity>();
                _playerSedans[player.userID] = list;
            }

            // Clean invalid cars
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] == null || list[i].IsDestroyed)
                    list.RemoveAt(i);
            }

            int missing = desiredCount - list.Count;
            if (missing <= 0)
                return;

            for (int i = 0; i < missing; i++)
            {
                var car = SpawnSedanNearPlayerWithGang(player, i, desiredCount, gangName);
                if (car != null)
                    list.Add(car);
            }
        }

        /// <summary>
        /// Spawn sedan near player with specific gang visuals.
        /// </summary>
        private BaseEntity SpawnSedanNearPlayerWithGang(BasePlayer player, int indexInGang, int gangSize, string gangName)
        {
            Vector3 playerPos = player.transform.position;

            float angle = (360f / Mathf.Max(gangSize, 1)) * indexInGang;
            float rad = angle * Mathf.Deg2Rad;

            Vector3 offset = new Vector3(
                Mathf.Cos(rad) * SpawnRadius,
                0f,
                Mathf.Sin(rad) * SpawnRadius
            );

            Vector3 samplePos = playerPos + offset;

            if (!FindGroundPosition(samplePos, out var finalPos))
            {
                PrintWarning($"[DriveBySedanGangs] Failed to find ground for sedan spawn near {player.displayName}.");
                return null;
            }

            Vector3 toPlayer = (playerPos - finalPos);
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.01f)
                toPlayer = -player.transform.forward;

            toPlayer.Normalize();
            Quaternion spawnRot = Quaternion.LookRotation(toPlayer, Vector3.up);

            BaseEntity car = GameManager.server.CreateEntity(SedanPrefab, finalPos, spawnRot, true);
            if (car == null)
            {
                PrintError("Failed to create sedan entity from prefab: " + SedanPrefab);
                return null;
            }

            car.enableSaving = false;
            car.Spawn();

            var rb = car.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = false;
            }

            car.SendNetworkUpdateImmediate();

            _deployedSedans.Remove(car);
            _deployScheduled.Remove(car);
            _retiringSedans.Remove(car);

            // Use the specified gang name instead of DefaultGangName
            SeatGangScientistsInSedanWithGang(car, gangName, player);

            return car;
        }

        /// <summary>
        /// Seat gang scientists in sedan with specific gang visuals.
        /// </summary>
        private void SeatGangScientistsInSedanWithGang(BaseEntity car, string gangName, BasePlayer target)
        {
            if (car == null || car.IsDestroyed) return;

            var seats = car.GetComponentsInChildren<BaseMountable>(true);
            if (seats == null || seats.Length == 0)
            {
                Puts("[DriveBySedanGangs] No seats (BaseMountable) found on sedan; cannot seat scientists.");
                return;
            }

            int needed = 3;
            var seated = new List<ScientistNPC>();

            foreach (var seat in seats)
            {
                if (needed <= 0)
                    break;

                if (seat == null || seat.IsDestroyed) continue;
                if (seat.AnyMounted()) continue;

                Vector3 spawnPos = seat.transform.position + Vector3.up * 0.1f;
                var npc = CreateDressedGangScientist(spawnPos, seat.transform.rotation, gangName);
                if (npc == null) continue;

                seat.AttemptMount(npc);

                _scientistSeats[npc] = seat;

                if (npc.Brain != null && target != null)
                {
                    if (npc.Brain.Senses?.Memory != null)
                        npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);

                    if (npc.Brain.Events?.Memory?.Entity != null)
                        npc.Brain.Events.Memory.Entity.Set(target, 0);
                }

                seated.Add(npc);
                needed--;
            }

            if (seated.Count > 0)
            {
                _sedanScientists[car] = seated;

                _driveByStates[car] = new DriveByState
                {
                    TargetID = target.userID,
                    Shooters = new List<ScientistNPC>(seated),
                    LastShootTime = 0f,
                    LastShooterIndex = -1,
                    GangName = gangName
                };
            }
        }

        #endregion

        #region Chat Commands

        [ChatCommand("sedandebug")]
        private void CmdSedanDebug(BasePlayer player, string command, string[] args)
        {
            // Show debug info about territory detection
            player.ChatMessage("<color=#55ff55>=== Drive-By Sedan Debug Info ===</color>");
            player.ChatMessage($"Your Position: X:{player.transform.position.x:F0} Z:{player.transform.position.z:F0}");
            player.ChatMessage($"Your Steam ID: {player.userID}");
            
            if (HoodWars == null || !HoodWars.IsLoaded)
            {
                player.ChatMessage("<color=#ff4444>HoodWars plugin is NOT loaded - territory detection disabled</color>");
                return;
            }
            
            // Get player's gang membership using multiple fallback methods
            player.ChatMessage("<color=#aaaaaa>Querying HoodWars for your gang... (check server console for debug)</color>");
            var playerGang = GetPlayerGangFromHoodWars(player);
            
            bool hasGang = !string.IsNullOrEmpty(playerGang);
            player.ChatMessage($"Your Gang Membership: <color={(hasGang ? "#55ff55" : "#ff4444")}>{playerGang ?? "None"}</color>");
            
            if (!hasGang)
            {
                player.ChatMessage("<color=#ffaa00>NOTE: To join a gang, place a Tool Cupboard (TC) in a gang's territory.</color>");
                player.ChatMessage("<color=#ffaa00>This is called 'blooding in' and makes you a member of that gang.</color>");
            }
            
            // Get current territory based on position (where you're standing RIGHT NOW)
            var currentTerritory = HoodWars.Call("GetNeighborhoodNameAt", player.transform.position) as string;
            player.ChatMessage($"Territory You're Standing In: <color=#ffaa00>{currentTerritory ?? "Unknown"}</color>");
            
            // Check if in enemy territory (only applies if you HAVE a gang)
            bool isEnemyTerritory = hasGang 
                && !string.IsNullOrEmpty(currentTerritory) 
                && currentTerritory != NeutralGround 
                && currentTerritory != playerGang;
            
            if (hasGang)
            {
                player.ChatMessage($"Is Enemy Territory: <color={(isEnemyTerritory ? "#ff4444>YES" : "#55ff55>NO")}</color>");
            }
            else
            {
                player.ChatMessage($"Is Enemy Territory: <color=#aaaaaa>N/A (you need to join a gang first)</color>");
            }
            
            // Show last known territory
            _playerLastTerritory.TryGetValue(player.userID, out var lastTerritory);
            player.ChatMessage($"Last Tracked Territory: <color=#aaaaaa>{lastTerritory ?? "None"}</color>");
            
            // Show cooldown status
            if (_playerTerritorySpawnCooldown.TryGetValue(player.userID, out var lastSpawnTime))
            {
                var cooldownRemaining = TerritorySpawnCooldown - (Time.realtimeSinceStartup - lastSpawnTime);
                if (cooldownRemaining > 0)
                    player.ChatMessage($"Spawn Cooldown: <color=#ff4444>{cooldownRemaining:F0} seconds remaining</color>");
                else
                    player.ChatMessage($"Spawn Cooldown: <color=#55ff55>Ready</color>");
            }
            else
            {
                player.ChatMessage($"Spawn Cooldown: <color=#55ff55>Ready (never triggered)</color>");
            }
            
            // Show active gangs
            if (_playerSedans.TryGetValue(player.userID, out var gangs))
            {
                gangs.RemoveAll(c => c == null || c.IsDestroyed);
                player.ChatMessage($"Active Drive-By Gangs: <color=#ffaa00>{gangs.Count}</color>");
            }
            else
            {
                player.ChatMessage($"Active Drive-By Gangs: <color=#aaaaaa>0</color>");
            }
            
            player.ChatMessage("<color=#55ff55>=================================</color>");
            
            // Explain spawn requirements
            player.ChatMessage("<color=#ffaa00>For territory spawns to trigger:</color>");
            player.ChatMessage("1. You must be a gang member (place TC in gang territory)");
            player.ChatMessage("2. You must cross into ENEMY territory");
            player.ChatMessage("3. You must not have active drive-by gangs");
            player.ChatMessage("4. Cooldown must be ready (5 min between spawns)");
        }

        [ChatCommand("stalksedan")]
        private void CmdStalkSedan(BasePlayer player, string command, string[] args)
        {
            int count = DefaultSedansPerPlayer;
            if (args != null && args.Length > 0)
            {
                int.TryParse(args[0], out count);
                if (count <= 0)
                    count = DefaultSedansPerPlayer;
            }

            EnsureGangForPlayer(player, count);
            player.ChatMessage($"Your drive-by sedan gang is now size {count}.");
        }

        [ChatCommand("destroysedan")]
        private void CmdDestroySedan(BasePlayer player, string command, string[] args)
        {
            DestroyGangForPlayer(player);
            player.ChatMessage("Your drive-by sedan gang has been destroyed.");
        }

        #endregion
    }
}