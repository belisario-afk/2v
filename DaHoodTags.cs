/*
    Da Hood Gang Tags & Skinning v2.0.0
    - Premium Glass UI with background blur
    - Integrated Gang Management (Pirus, Vagos, Surenos, Disciples)
    - Automatic Item Renaming & Skinning
    - Individual Spray Image Previews in UI
    - Admin Testing & Management Commands
    - Gang Alert System - Notify gangs when enemies tag their territory
    - REP System Integration - Earn REP for tagging based on location
    - Territory Tagging Mechanics - Track tags and territory control
*/

using System;
using System.Collections.Generic;
using UnityEngine;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Plugins;
using Oxide.Core.Configuration;
using System.Linq;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("DaHoodTags", "Gemini + Copilot", "2.0.0")]
    [Description("Gang tag system with alerts, REP rewards, and territory mechanics - integrated with HoodWars")]
    public class DaHoodTags : RustPlugin
    {
        private const string permAdmin = "dahoodtags.admin";
        
        // Reference to HoodWars for REP system
        [PluginReference]
        private Plugin HoodWars;
        
        private enum GangType { None, Pirus, Vagos, Surenos, Disciples }

        #region HoodWars Data Types (for reading HoodWars_CoreData.json directly)

        // These mirror the data structures in HoodWars.cs
        private class HoodWarsStoredData
        {
            public Dictionary<ulong, HoodWarsPlayerInfo> Players = new Dictionary<ulong, HoodWarsPlayerInfo>();
        }

        private class HoodWarsPlayerInfo
        {
            public int HomeHood = 4; // 0=West (Pirus), 1=North (Vagos), 2=South (Surenos), 3=East (Disciples), 4=Neutral
        }

        #endregion

        #region Configuration & Data

        private ConfigData _config;
        private TagStoredData _tagData;
        private DynamicConfigFile _dataFile;

        // Track recent taggers for "caught tagging" mechanic
        private Dictionary<ulong, float> _recentTaggers = new Dictionary<ulong, float>();
        private const float CAUGHT_TAGGING_WINDOW = 30f; // seconds

        private class ConfigData
        {
            [JsonProperty("REP Rewards")]
            public REPSettings REP { get; set; } = new REPSettings();

            [JsonProperty("Alert Settings")]
            public AlertSettings Alerts { get; set; } = new AlertSettings();

            [JsonProperty("Territory Settings")]
            public TerritorySettings Territory { get; set; } = new TerritorySettings();

            public class REPSettings
            {
                [JsonProperty("REP for Tag in Neutral Territory")]
                public int NeutralTag { get; set; } = 5;

                [JsonProperty("REP for Tag in Own Territory")]
                public int OwnTerritoryTag { get; set; } = 2;

                [JsonProperty("REP for Tag in Enemy Territory")]
                public int EnemyTerritoryTag { get; set; } = 15;

                [JsonProperty("REP for Tag in Enemy HQ")]
                public int EnemyHQTag { get; set; } = 25;

                [JsonProperty("Stealth Bonus (No Enemies within 50m)")]
                public int StealthBonus { get; set; } = 10;

                [JsonProperty("REP Lost When Tag Destroyed")]
                public int TagDestroyedPenalty { get; set; } = 5;

                [JsonProperty("REP Lost When Caught Tagging (killed within 30s)")]
                public int CaughtTaggingPenalty { get; set; } = 10;
            }

            public class AlertSettings
            {
                [JsonProperty("Enable Gang Alerts for Enemy Tagging")]
                public bool EnableGangAlerts { get; set; } = true;

                [JsonProperty("Alert Message Color (Hex)")]
                public string AlertColor { get; set; } = "#FF4444";

                [JsonProperty("Show Grid Location in Alert")]
                public bool ShowGridLocation { get; set; } = true;
            }

            public class TerritorySettings
            {
                [JsonProperty("Enable Territory Tag Tracking")]
                public bool EnableTagTracking { get; set; } = true;

                [JsonProperty("Heat Level Threshold (Tags for High Alert)")]
                public int HeatThreshold { get; set; } = 10;

                [JsonProperty("Tag Decay Time (Hours)")]
                public float TagDecayHours { get; set; } = 24f;
            }
        }

        private class TagStoredData
        {
            public Dictionary<string, TerritoryTagData> TerritoryTags { get; set; } = new Dictionary<string, TerritoryTagData>();
            public Dictionary<ulong, List<TagRecord>> PlayerTags { get; set; } = new Dictionary<ulong, List<TagRecord>>();
        }

        private class TerritoryTagData
        {
            public Dictionary<string, int> TagCount { get; set; } = new Dictionary<string, int>();
            public float HeatLevel { get; set; } = 0f;
        }

        private class TagRecord
        {
            public float PosX { get; set; }
            public float PosY { get; set; }
            public float PosZ { get; set; }
            public string Timestamp { get; set; }
            public string TaggerGang { get; set; }
            public string TerritoryGang { get; set; }
            public uint EntityId { get; set; }
        }

        // Territory definitions (matching HoodWars)
        private class TerritoryDefinition
        {
            public string Name;
            public GangType Gang;
            public float MinX, MaxX, MinZ, MaxZ;
            public float HQCenterX, HQCenterZ, HQRadius;
        }

        private List<TerritoryDefinition> _territories = new List<TerritoryDefinition>
        {
            new TerritoryDefinition { Name = "Westside Pirus", Gang = GangType.Pirus, MinX = -1050, MaxX = 0, MinZ = 0, MaxZ = 1050, HQCenterX = -525, HQCenterZ = 525, HQRadius = 50 },
            new TerritoryDefinition { Name = "Northside Vagos", Gang = GangType.Vagos, MinX = 0, MaxX = 1050, MinZ = 0, MaxZ = 1050, HQCenterX = 525, HQCenterZ = 525, HQRadius = 50 },
            new TerritoryDefinition { Name = "Southside Sureños", Gang = GangType.Surenos, MinX = -1050, MaxX = 0, MinZ = -1050, MaxZ = 0, HQCenterX = -525, HQCenterZ = -525, HQRadius = 50 },
            new TerritoryDefinition { Name = "Eastside Disciples", Gang = GangType.Disciples, MinX = 0, MaxX = 1050, MinZ = -1050, MaxZ = 0, HQCenterX = 525, HQCenterZ = -525, HQRadius = 50 }
        };

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null) throw new Exception();
            }
            catch
            {
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig() => _config = new ConfigData();
        protected override void SaveConfig() => Config.WriteObject(_config);

        private void LoadData()
        {
            _dataFile = Interface.Oxide.DataFileSystem.GetFile("DaHoodTags_Data");
            _tagData = _dataFile.ReadObject<TagStoredData>() ?? new TagStoredData();
        }

        private void SaveTagData() => _dataFile.WriteObject(_tagData);

        #endregion

        private class SprayOption
        {
            public string Name;
            public ulong SkinID;
            public string ImageUrl;
        }

        private class GangConfig
        {
            public string Name;
            public string Color;      // RGBA format for UI accents
            public string ChatPrefix; // Color tag for chat
            public string Permission;
            public string LogoUrl;    // URL to the gang crest
            public List<SprayOption> Sprays;
        }

        private Dictionary<GangType, GangConfig> _gangs = new Dictionary<GangType, GangConfig>
        {
            [GangType.Pirus] = new GangConfig {
                Name = "WESTSIDE PIRUS", 
                Color = "0.7 0.1 0.1 0.85", ChatPrefix = "#FF3131", 
                Permission = "dahood.pirus",
                LogoUrl = "https://i.imgur.com/example_pirus_logo.png",
                Sprays = new List<SprayOption> {
                    new SprayOption { Name = "Piru Tag", SkinID = 3639835623, ImageUrl = "https://i.imgur.com/vupaESp.png" },
                    new SprayOption { Name = "Block Mural", SkinID = 3639835623, ImageUrl = "https://i.imgur.com/vupaESp.png" },
                    new SprayOption { Name = "Piru Emblem", SkinID = 3639835623, ImageUrl = "https://i.imgur.com/vupaESp.png" }
                }
            },
            [GangType.Vagos] = new GangConfig {
                Name = "NORTHSIDE VAGOS", 
                Color = "0.9 0.8 0.1 0.85", ChatPrefix = "#FFFF00",
                Permission = "dahood.vagos",
                LogoUrl = "https://i.imgur.com/example_vagos_logo.png",
                Sprays = new List<SprayOption> {
                    new SprayOption { Name = "Vago Tag", SkinID = 201, ImageUrl = "https://i.imgur.com/tag2.png" },
                    new SprayOption { Name = "Yellow Pride", SkinID = 202, ImageUrl = "https://i.imgur.com/mural2.png" },
                    new SprayOption { Name = "Vago Crest", SkinID = 203, ImageUrl = "https://i.imgur.com/emblem2.png" }
                }
            },
            [GangType.Surenos] = new GangConfig {
                Name = "SOUTHSIDE SUREÑOS", 
                Color = "0.1 0.2 0.7 0.85", ChatPrefix = "#3131FF",
                Permission = "dahood.surenos",
                LogoUrl = "https://i.imgur.com/example_surenos_logo.png",
                Sprays = new List<SprayOption> {
                    new SprayOption { Name = "Blue Tag", SkinID = 301, ImageUrl = "https://i.imgur.com/tag3.png" },
                    new SprayOption { Name = "South Side", SkinID = 302, ImageUrl = "https://i.imgur.com/mural3.png" },
                    new SprayOption { Name = "13 Crest", SkinID = 303, ImageUrl = "https://i.imgur.com/emblem3.png" }
                }
            },
            [GangType.Disciples] = new GangConfig {
                Name = "EASTSIDE DISCIPLES", 
                Color = "0.2 0.2 0.2 0.95", ChatPrefix = "#808080",
                Permission = "dahood.disciples",
                LogoUrl = "https://i.imgur.com/example_disciples_logo.png",
                Sprays = new List<SprayOption> {
                    new SprayOption { Name = "Void Tag", SkinID = 401, ImageUrl = "https://i.imgur.com/tag4.png" },
                    new SprayOption { Name = "East Mural", SkinID = 402, ImageUrl = "https://i.imgur.com/mural4.png" },
                    new SprayOption { Name = "Disciple Star", SkinID = 403, ImageUrl = "https://i.imgur.com/emblem4.png" }
                }
            }
        };

        #region Oxide Hooks
        private void Init()
        {
            permission.RegisterPermission(permAdmin, this);
            foreach (var gang in _gangs.Values) permission.RegisterPermission(gang.Permission, this);
            LoadData();
        }

        private void Unload()
        {
            SaveTagData();
        }

        private object OnSprayCreate(SprayCan sc, Vector3 vector, Quaternion quaternion)
        {
            BasePlayer player = sc.GetOwnerPlayer();
            
            if (sc.skinID != 0)
            {
                BaseEntity entity = GameManager.server.CreateEntity(sc.SprayDecalEntityRef.resourcePath, vector, quaternion, true);
                entity.skinID = sc.skinID;
                entity.OnDeployed(null, player, sc.GetItem());
                entity.Spawn();
                sc.GetItem().LoseCondition(sc.ConditionLossPerSpray);

                // Process the tag for alerts and REP
                if (player != null)
                {
                    ProcessTagPlacement(player, vector, entity.net?.ID.Value ?? 0);
                }

                return false;
            }
            return null;
        }

        // Track when spray decals are destroyed (for REP penalty)
        private void OnEntityKill(BaseEntity entity)
        {
            if (entity?.ShortPrefabName != "spraydecal.deployed") return;
            
            // Find the tag record and apply penalty
            foreach (var kvp in _tagData.PlayerTags)
            {
                var record = kvp.Value.FirstOrDefault(r => r.EntityId == entity.net?.ID.Value);
                if (record != null)
                {
                    // Apply REP penalty to the tagger
                    ModifyPlayerREP(kvp.Key, -_config.REP.TagDestroyedPenalty);
                    
                    // Notify the player if online
                    var player = BasePlayer.FindByID(kvp.Key);
                    if (player != null)
                    {
                        player.ChatMessage($"<color=#ff4444>[Da Hood]</color> Your tag was destroyed! You lost <color=#ff0000>{_config.REP.TagDestroyedPenalty}</color> REP.");
                    }
                    
                    kvp.Value.Remove(record);
                    break;
                }
            }
        }

        // Track player deaths for "caught tagging" mechanic
        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null) return;
            
            // Check if player recently tagged
            if (_recentTaggers.TryGetValue(player.userID, out float tagTime))
            {
                if (UnityEngine.Time.realtimeSinceStartup - tagTime <= CAUGHT_TAGGING_WINDOW)
                {
                    // Player was caught tagging!
                    ModifyPlayerREP(player.userID, -_config.REP.CaughtTaggingPenalty);
                    player.ChatMessage($"<color=#ff4444>[Da Hood]</color> You got caught tagging! Lost <color=#ff0000>{_config.REP.CaughtTaggingPenalty}</color> REP.");
                    
                    // Notify the killer
                    var attacker = info?.InitiatorPlayer;
                    if (attacker != null && attacker != player)
                    {
                        attacker.ChatMessage($"<color=#55ff55>[Da Hood]</color> You caught an enemy tagger! Nice work.");
                    }
                }
                _recentTaggers.Remove(player.userID);
            }
        }
        #endregion

        #region Tag Processing & Alerts

        private void ProcessTagPlacement(BasePlayer player, Vector3 position, uint entityId)
        {
            GangType playerGang = GetPlayerGang(player);
            if (playerGang == GangType.None) return;

            var territory = GetTerritoryAtPosition(position);
            GangType territoryGang = territory?.Gang ?? GangType.None;
            bool isInHQ = territory != null && IsInHQ(position, territory);

            // Calculate REP reward
            int repGain = CalculateREPReward(player, playerGang, territoryGang, isInHQ, position);
            
            if (repGain > 0)
            {
                ModifyPlayerREP(player.userID, repGain);
                player.ChatMessage($"<color=#55ff55>[Da Hood]</color> Tag placed! Earned <color=#00ff00>+{repGain}</color> REP.");
            }

            // Track the tag
            TrackTag(player, position, playerGang, territoryGang, entityId);
            
            // Mark player as recent tagger
            _recentTaggers[player.userID] = UnityEngine.Time.realtimeSinceStartup;

            // Send gang alerts if tagging in enemy territory
            if (_config.Alerts.EnableGangAlerts && territoryGang != GangType.None && territoryGang != playerGang)
            {
                SendGangAlert(playerGang, territoryGang, position, isInHQ);
            }

            // Update territory heat
            if (_config.Territory.EnableTagTracking && territory != null)
            {
                UpdateTerritoryHeat(territory.Name, playerGang);
            }
        }

        private int CalculateREPReward(BasePlayer player, GangType playerGang, GangType territoryGang, bool isInHQ, Vector3 position)
        {
            int baseREP = 0;

            if (territoryGang == GangType.None)
            {
                // Neutral territory
                baseREP = _config.REP.NeutralTag;
            }
            else if (territoryGang == playerGang)
            {
                // Own territory
                baseREP = _config.REP.OwnTerritoryTag;
            }
            else
            {
                // Enemy territory
                if (isInHQ)
                {
                    baseREP = _config.REP.EnemyHQTag;
                }
                else
                {
                    baseREP = _config.REP.EnemyTerritoryTag;
                }
            }

            // Check for stealth bonus (no enemies within 50m)
            bool hasStealthBonus = CheckStealthBonus(player, position);
            if (hasStealthBonus)
            {
                baseREP += _config.REP.StealthBonus;
            }

            return baseREP;
        }

        private bool CheckStealthBonus(BasePlayer tagger, Vector3 position)
        {
            GangType taggerGang = GetPlayerGang(tagger);
            
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == tagger) continue;
                
                GangType otherGang = GetPlayerGang(player);
                if (otherGang != GangType.None && otherGang != taggerGang)
                {
                    float distance = Vector3.Distance(player.transform.position, position);
                    if (distance <= 50f)
                    {
                        return false; // Enemy nearby, no stealth bonus
                    }
                }
            }
            return true;
        }

        private void SendGangAlert(GangType attackerGang, GangType defenderGang, Vector3 position, bool isInHQ)
        {
            string gridRef = _config.Alerts.ShowGridLocation ? GetGridReference(position) : "";
            string gangName = _gangs[defenderGang].Name;
            string attackerName = _gangs[attackerGang].Name;
            string locationDesc = isInHQ ? "YOUR HQ" : "your territory";
            
            string alertMsg = isInHQ 
                ? $"<color={_config.Alerts.AlertColor}>[GANG ALERT]</color> An enemy from {attackerName} is tagging {locationDesc}! {gridRef}"
                : $"<color={_config.Alerts.AlertColor}>[GANG ALERT]</color> Enemy activity detected in {locationDesc}! {gridRef}";

            // Send to all players of the defending gang
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (GetPlayerGang(player) == defenderGang)
                {
                    player.ChatMessage(alertMsg);
                }
            }
        }

        private void TrackTag(BasePlayer player, Vector3 position, GangType playerGang, GangType territoryGang, uint entityId)
        {
            if (!_tagData.PlayerTags.ContainsKey(player.userID))
                _tagData.PlayerTags[player.userID] = new List<TagRecord>();

            _tagData.PlayerTags[player.userID].Add(new TagRecord
            {
                PosX = position.x,
                PosY = position.y,
                PosZ = position.z,
                Timestamp = DateTime.UtcNow.ToString("o"),
                TaggerGang = playerGang.ToString(),
                TerritoryGang = territoryGang.ToString(),
                EntityId = entityId
            });

            SaveTagData();
        }

        private void UpdateTerritoryHeat(string territoryName, GangType taggerGang)
        {
            if (!_tagData.TerritoryTags.ContainsKey(territoryName))
                _tagData.TerritoryTags[territoryName] = new TerritoryTagData();

            var data = _tagData.TerritoryTags[territoryName];
            string gangKey = taggerGang.ToString();
            
            if (!data.TagCount.ContainsKey(gangKey))
                data.TagCount[gangKey] = 0;

            data.TagCount[gangKey]++;
            data.HeatLevel = data.TagCount.Values.Sum();

            // Check if territory is now "hot"
            if (data.HeatLevel >= _config.Territory.HeatThreshold)
            {
                var territory = _territories.FirstOrDefault(t => t.Name == territoryName);
                if (territory != null)
                {
                    BroadcastHotTerritory(territory.Gang, territoryName);
                }
            }

            SaveTagData();
        }

        private void BroadcastHotTerritory(GangType territoryGang, string territoryName)
        {
            string msg = $"<color=#ffaa00>[TERRITORY ALERT]</color> {territoryName} is under heavy enemy tagging activity!";
            
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (GetPlayerGang(player) == territoryGang)
                {
                    player.ChatMessage(msg);
                }
            }
        }

        #endregion

        #region Territory Helpers

        private TerritoryDefinition GetTerritoryAtPosition(Vector3 position)
        {
            return _territories.FirstOrDefault(t => 
                position.x >= t.MinX && position.x < t.MaxX && 
                position.z >= t.MinZ && position.z < t.MaxZ);
        }

        private bool IsInHQ(Vector3 position, TerritoryDefinition territory)
        {
            float dx = position.x - territory.HQCenterX;
            float dz = position.z - territory.HQCenterZ;
            return (dx * dx + dz * dz) <= (territory.HQRadius * territory.HQRadius);
        }

        private string GetGridReference(Vector3 position)
        {
            // Convert world position to grid reference (A1, B2, etc.)
            float worldSize = ConVar.Server.worldsize;
            float halfWorld = worldSize / 2f;
            
            int gridX = Mathf.FloorToInt((position.x + halfWorld) / (worldSize / 26f));
            int gridZ = Mathf.FloorToInt((position.z + halfWorld) / (worldSize / 26f));
            
            char letterX = (char)('A' + Mathf.Clamp(gridX, 0, 25));
            int numberZ = Mathf.Clamp(gridZ + 1, 1, 26);
            
            return $"({letterX}{numberZ})";
        }

        private void ModifyPlayerREP(ulong playerId, int amount)
        {
            // Call HoodWars to modify reputation if available
            if (HoodWars != null)
            {
                HoodWars.Call("API_ModifyReputation", playerId, amount);
            }
        }

        #endregion

        #region UI Implementation
        [ChatCommand("tag")]
        private void CmdTag(BasePlayer player)
        {
            GangType gang = GetPlayerGang(player);
            if (gang == GangType.None && !permission.UserHasPermission(player.UserIDString, permAdmin))
            {
                player.ChatMessage("<color=#ff0000>[Da Hood]</color> You must belong to a gang to use this.");
                return;
            }
            if (gang == GangType.None) gang = GangType.Pirus;
            OpenTagMenu(player, gang);
        }

        private void OpenTagMenu(BasePlayer player, GangType gangType)
        {
            CuiHelper.DestroyUi(player, "TagMenuBg");
            var config = _gangs[gangType];
            var container = new CuiElementContainer();

            // Background Overlay
            container.Add(new CuiPanel {
                Image = { Color = "0 0 0 0.8" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", "TagMenuBg");

            container.Add(new CuiButton {
                Button = { Command = "tag.close", Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, "TagMenuBg");

            // Main Glass Content Panel
            container.Add(new CuiPanel {
                Image = { Color = "0 0 0 0.95", Material = "assets/content/ui/uibackgroundblur-runway.mat" },
                RectTransform = { AnchorMin = "0.15 0.2", AnchorMax = "0.85 0.8" }
            }, "TagMenuBg", "TagMenu");

            // Header Accent Line
            container.Add(new CuiPanel {
                Image = { Color = config.Color },
                RectTransform = { AnchorMin = "0 0.98", AnchorMax = "1 1" }
            }, "TagMenu");

            // Gang Logo (Top Center)
            container.Add(new CuiElement {
                Parent = "TagMenu",
                Components = {
                    new CuiRawImageComponent { Url = config.LogoUrl, Sprite = "assets/content/textures/generic/fullwhite.tga" },
                    new CuiRectTransformComponent { AnchorMin = "0.44 0.82", AnchorMax = "0.56 0.96" }
                }
            });

            // Gang Title
            container.Add(new CuiLabel {
                Text = { Text = config.Name, FontSize = 24, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0.72", AnchorMax = "1 0.82" }
            }, "TagMenu");

            // Action Buttons with Image Previews
            int i = 0;
            foreach (var spray in config.Sprays)
            {
                float xMin = 0.05f + (i * 0.31f);
                float xMax = xMin + 0.28f;

                string btnName = $"btn_{i}";
                
                // Button Background
                container.Add(new CuiButton {
                    Button = { Command = $"tag.select {spray.SkinID} \"{spray.Name}\"", Color = "0.15 0.15 0.15 0.8", Material = "assets/content/ui/uibackgroundblur-runway.mat" },
                    RectTransform = { AnchorMin = $"{xMin} 0.1", AnchorMax = $"{xMax} 0.65" },
                    Text = { Text = "" } // Label handled separately
                }, "TagMenu", btnName);

                // Spray Preview Image
                container.Add(new CuiElement {
                    Parent = btnName,
                    Components = {
                        new CuiRawImageComponent { Url = spray.ImageUrl, Sprite = "assets/content/textures/generic/fullwhite.tga" },
                        new CuiRectTransformComponent { AnchorMin = "0.1 0.35", AnchorMax = "0.9 0.9" }
                    }
                });

                // Spray Name Label
                container.Add(new CuiLabel {
                    Text = { Text = $"<b>{spray.Name.ToUpper()}</b>", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "0.9 0.9 0.9 1" },
                    RectTransform = { AnchorMin = "0 0.1", AnchorMax = "1 0.3" }
                }, btnName);

                // Button Glow Underline
                container.Add(new CuiPanel {
                    Image = { Color = config.Color },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.04" }
                }, btnName);

                i++;
            }

            CuiHelper.AddUi(player, container);
        }

        [ConsoleCommand("tag.select")]
        private void CmdSelect(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length < 2) return;

            ulong skinID;
            if (!ulong.TryParse(arg.Args[0], out skinID)) return;
            string tagName = arg.Args[1];

            Item item = ItemManager.CreateByItemID(-596876839, 1, skinID);
            item.name = $"{tagName} Spray Can";
            player.GiveItem(item);
            
            CuiHelper.DestroyUi(player, "TagMenuBg");

            GangType gangType = GetPlayerGang(player);
            string colorCode = gangType != GangType.None ? _gangs[gangType].ChatPrefix : "#ffffff";
            player.ChatMessage($"<color={colorCode}>[Da Hood]</color> You have received the <b>{tagName}</b>.");
        }

        [ConsoleCommand("tag.close")]
        private void CmdClose(ConsoleSystem.Arg arg) => CuiHelper.DestroyUi(arg.Player(), "TagMenuBg");
        #endregion

        #region Helpers & Admin
        private GangType GetPlayerGang(BasePlayer player)
        {
            // First try to read from HoodWars data file (same approach as DriveBySedanGangs)
            try
            {
                var dataFile = Interface.Oxide.DataFileSystem.GetFile("HoodWars_CoreData");
                if (dataFile != null)
                {
                    var hoodWarsData = dataFile.ReadObject<HoodWarsStoredData>();
                    if (hoodWarsData?.Players != null && hoodWarsData.Players.TryGetValue(player.userID, out var playerInfo))
                    {
                        // HomeHood: 0=West (Pirus), 1=North (Vagos), 2=South (Surenos), 3=East (Disciples), 4=Neutral
                        switch (playerInfo.HomeHood)
                        {
                            case 0: return GangType.Pirus;
                            case 1: return GangType.Vagos;
                            case 2: return GangType.Surenos;
                            case 3: return GangType.Disciples;
                            default: break; // Fall through to permission check
                        }
                    }
                }
            }
            catch { /* Fall through to permission-based detection */ }

            // Fallback: Check permissions (for admin testing or manual gang assignment)
            foreach (var gang in _gangs)
                if (permission.UserHasPermission(player.UserIDString, gang.Value.Permission)) return gang.Key;
            return GangType.None;
        }

        [ChatCommand("tagtest")]
        private void CmdTagTest(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permAdmin)) return;
            if (args.Length == 0) {
                player.ChatMessage("Usage: /tagtest <pirus|vagos|surenos|disciples>");
                return;
            }

            switch (args[0].ToLower())
            {
                case "pirus": OpenTagMenu(player, GangType.Pirus); break;
                case "vagos": OpenTagMenu(player, GangType.Vagos); break;
                case "surenos": OpenTagMenu(player, GangType.Surenos); break;
                case "disciples": OpenTagMenu(player, GangType.Disciples); break;
                default: player.ChatMessage("Invalid Gang Name."); break;
            }
        }

        [ChatCommand("tagstats")]
        private void CmdTagStats(BasePlayer player, string command, string[] args)
        {
            GangType playerGang = GetPlayerGang(player);
            
            // Show player's tag stats
            int playerTags = 0;
            if (_tagData.PlayerTags.TryGetValue(player.userID, out var tags))
                playerTags = tags.Count;

            player.ChatMessage($"<color=#55aaee>=== YOUR TAG STATS ===</color>");
            player.ChatMessage($"Total Tags Placed: <color=#00ff00>{playerTags}</color>");
            
            // Show territory heat levels
            player.ChatMessage($"<color=#55aaee>=== TERRITORY HEAT ===</color>");
            foreach (var territory in _territories)
            {
                if (_tagData.TerritoryTags.TryGetValue(territory.Name, out var data))
                {
                    string heatColor = data.HeatLevel >= _config.Territory.HeatThreshold ? "#ff0000" : 
                                       data.HeatLevel >= _config.Territory.HeatThreshold / 2 ? "#ffaa00" : "#55ff55";
                    player.ChatMessage($"{territory.Name}: <color={heatColor}>{data.HeatLevel} tags</color>");
                }
                else
                {
                    player.ChatMessage($"{territory.Name}: <color=#55ff55>0 tags</color>");
                }
            }
        }

        [ChatCommand("covertag")]
        private void CmdCoverTag(BasePlayer player, string command, string[] args)
        {
            // Allow players to "cover" enemy tags they're looking at
            GangType playerGang = GetPlayerGang(player);
            if (playerGang == GangType.None)
            {
                player.ChatMessage("<color=#ff0000>[Da Hood]</color> You must belong to a gang to cover tags.");
                return;
            }

            // Raycast to find spray decal
            RaycastHit hit;
            if (Physics.Raycast(player.eyes.HeadRay(), out hit, 5f))
            {
                var entity = hit.GetEntity();
                if (entity != null && entity.ShortPrefabName == "spraydecal.deployed")
                {
                    // Check if it's an enemy tag
                    ulong tagOwnerId = entity.OwnerID;
                    var tagOwner = BasePlayer.FindByID(tagOwnerId);
                    GangType tagOwnerGang = GangType.None;
                    
                    if (tagOwner != null)
                        tagOwnerGang = GetPlayerGang(tagOwner);
                    else
                    {
                        // Try to find in stored data
                        foreach (var kvp in _tagData.PlayerTags)
                        {
                            var record = kvp.Value.FirstOrDefault(r => r.EntityId == entity.net?.ID.Value);
                            if (record != null)
                            {
                                Enum.TryParse(record.TaggerGang, out tagOwnerGang);
                                break;
                            }
                        }
                    }

                    if (tagOwnerGang != GangType.None && tagOwnerGang != playerGang)
                    {
                        // It's an enemy tag - destroy it!
                        entity.Kill();
                        player.ChatMessage($"<color=#55ff55>[Da Hood]</color> You covered an enemy tag! +{_config.REP.EnemyTerritoryTag / 2} REP");
                        ModifyPlayerREP(player.userID, _config.REP.EnemyTerritoryTag / 2);
                    }
                    else
                    {
                        player.ChatMessage("<color=#ffaa00>[Da Hood]</color> That's not an enemy tag.");
                    }
                }
                else
                {
                    player.ChatMessage("<color=#ff0000>[Da Hood]</color> No tag found. Look at a spray decal.");
                }
            }
            else
            {
                player.ChatMessage("<color=#ff0000>[Da Hood]</color> No tag found. Get closer.");
            }
        }
        #endregion
    }
}