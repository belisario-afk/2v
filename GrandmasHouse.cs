using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("GrandmasHouse", "Gemini", "2.9.1")]
    [Description("Independent Matriarch System. Advanced clone mimicry including jumping and looking direction.")]
    public class GrandmasHouse : RustPlugin
    {
        [PluginReference]
        private Plugin HoodWars, TurfGraffiti;

        private Dictionary<string, MatriarchSet> _activeMatriarchs = new Dictionary<string, MatriarchSet>();
        private List<BaseEntity> _manualMatriarchs = new List<BaseEntity>();
        private Dictionary<BaseEntity, ulong> _captors = new Dictionary<BaseEntity, ulong>();
        private HashSet<BaseEntity> _surrenderedMatriarchs = new HashSet<BaseEntity>();
        
        private Dictionary<ulong, Queue<CaptorFrame>> _captorTrails = new Dictionary<ulong, Queue<CaptorFrame>>();
        private Dictionary<BaseEntity, float> _aimTimers = new Dictionary<BaseEntity, float>();
        
        private const string PrefabNPC = "assets/prefabs/player/player.prefab";
        private const string PermAdmin = "grandmashouse.admin";
        private const string ItemC4 = "explosive.timed";

        private class MatriarchSet
        {
            public BaseEntity Grandma;
            public BaseEntity Mom;
        }

        private struct CaptorFrame
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float Pitch;
            public float Yaw;
            public bool IsSprinting;
            public bool IsCrouching;
            public bool IsOnGround;
        }

        #region Configuration

        private ConfigData _config;

        private class MatriarchSettings
        {
            public float Health;
            public float InfluencePenalty;
            public string Name;
            public float Radius;
        }

        private class ConfigData
        {
            [JsonProperty("Grandma Settings")]
            public MatriarchSettings Grandma { get; set; } = new MatriarchSettings 
            { 
                Health = 1000f, 
                InfluencePenalty = 25.0f, 
                Name = "Grandma",
                Radius = 15f
            };

            [JsonProperty("Mom Settings")]
            public MatriarchSettings Mom { get; set; } = new MatriarchSettings 
            { 
                Health = 800f, 
                InfluencePenalty = 35.0f, 
                Name = "Mom",
                Radius = 12f
            };

            public int FoodGiftIntervalMinutes = 30;
            public float RepairIntervalSeconds = 10f;
            public int RespawnTimeSeconds = 3600;
            public float StickUpDistance = 5f;
            public float FollowDistance = 1.2f;
            public float RequiredAimTime = 14.0f;
            public bool InvulnerableWhileSurrendered = false; 
        }

        protected override void LoadDefaultConfig() => _config = new ConfigData();
        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<ConfigData>() ?? new ConfigData();
            SaveConfig();
        }
        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Core Logic

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
        }

        private void OnServerInitialized()
        {
            if (HoodWars != null)
            {
                SpawnAllMatriarchs();
            }

            timer.Every(5f, UpdateMatriarchAuras);
            timer.Every(0.03f, UpdateHostageLogic); 
            timer.Every(_config.FoodGiftIntervalMinutes * 60, DistributeGrandmaMeals);
        }

        private void Unload()
        {
            foreach (var set in _activeMatriarchs.Values)
            {
                if (set.Grandma != null && !set.Grandma.IsDestroyed) set.Grandma.Kill();
                if (set.Mom != null && !set.Mom.IsDestroyed) set.Mom.Kill();
            }

            foreach (var ent in _manualMatriarchs)
                if (ent != null && !ent.IsDestroyed) ent.Kill();
        }

        private void SpawnAllMatriarchs()
        {
            string[] gangs = { "Westside Pirus", "Northside Vagos", "Southside Sureños", "Eastside Disciples" };
            foreach (var gang in gangs)
            {
                if (!_activeMatriarchs.ContainsKey(gang))
                    _activeMatriarchs[gang] = new MatriarchSet();
                
                SpawnMatriarchAtHQ(gang, true); 
                SpawnMatriarchAtHQ(gang, false);
            }
        }

        private void SpawnMatriarchAtHQ(string gangName, bool isGrandma)
        {
            if (HoodWars == null) return;
            object hqPosObj = HoodWars.Call("GetHQLocation", gangName);
            if (hqPosObj == null) return;
            
            Vector3 hqPos = (Vector3)hqPosObj;
            Vector3 spawnPos = hqPos + new Vector3(8, 0, 8);

            BaseEntity ent = InternalSpawn(spawnPos, gangName, isGrandma);
            if (isGrandma) _activeMatriarchs[gangName].Grandma = ent;
            else _activeMatriarchs[gangName].Mom = ent;
        }

        private BaseEntity InternalSpawn(Vector3 pos, string gangName, bool isGrandma)
        {
            BaseEntity npcEnt = GameManager.server.CreateEntity(PrefabNPC, pos, Quaternion.identity);
            if (npcEnt == null) return null;

            BasePlayer npc = npcEnt as BasePlayer;
            if (npc != null)
            {
                npc.userID = (ulong)UnityEngine.Random.Range(1000000, 9999999);
                npc.UserIDString = npc.userID.ToString();
                npc.displayName = !string.IsNullOrEmpty(gangName) ? $"{gangName}'s {(isGrandma ? _config.Grandma.Name : _config.Mom.Name)}" : (isGrandma ? _config.Grandma.Name : _config.Mom.Name);
                
                npc.SetPlayerFlag((BasePlayer.PlayerFlags)16384, true); 
                npc.Spawn();

                npc.InitializeHealth(isGrandma ? _config.Grandma.Health : _config.Mom.Health, isGrandma ? _config.Grandma.Health : _config.Mom.Health);
                npc.inventory.Strip();

                npc.metabolism.calories.value = 500;
                npc.metabolism.hydration.value = 500;
                
                if (isGrandma)
                {
                    // FIXED: Applied new Grandma Kit with provided Skin IDs
                    npc.inventory.GiveItem(ItemManager.CreateByName("tshirt.long", 1, 3642580871), npc.inventory.containerWear);
                    npc.inventory.GiveItem(ItemManager.CreateByName("pants", 1, 3642597643), npc.inventory.containerWear);
                    npc.inventory.GiveItem(ItemManager.CreateByName("mask.balaclava", 1, 3642598176), npc.inventory.containerWear);
                }
                else
                {
                    npc.inventory.GiveItem(ItemManager.CreateByName("shirt.collared", 1, 0), npc.inventory.containerWear);
                    npc.inventory.GiveItem(ItemManager.CreateByName("pants", 1, 3637162360), npc.inventory.containerWear);
                }
            }
            return npcEnt;
        }

        #endregion

        #region Hostage System (Live Echo Mimicry)

        private void UpdateHostageLogic()
        {
            foreach (var kvp in _activeMatriarchs)
            {
                HandleMatriarchInteraction(kvp.Value.Grandma, kvp.Key);
                HandleMatriarchInteraction(kvp.Value.Mom, kvp.Key);
            }

            for (int i = _manualMatriarchs.Count - 1; i >= 0; i--)
            {
                var ent = _manualMatriarchs[i];
                if (ent == null || ent.IsDestroyed) { _manualMatriarchs.RemoveAt(i); continue; }
                HandleMatriarchInteraction(ent, "Manual_Test");
            }
        }

        private void HandleMatriarchInteraction(BaseEntity entity, string homeGang)
        {
            if (entity == null || entity.IsDestroyed) return;

            if (!_captors.ContainsKey(entity))
            {
                BasePlayer activeAimer = null;
                List<BasePlayer> nearby = new List<BasePlayer>();
                Vis.Entities(entity.transform.position, _config.StickUpDistance, nearby);

                foreach (var p in nearby)
                {
                    if (IsLookingAt(p, entity) && IsHoldingWeapon(p))
                    {
                        activeAimer = p;
                        break;
                    }
                }

                if (activeAimer != null) ProcessAimTimer(entity, activeAimer);
                else if (_aimTimers.ContainsKey(entity)) _aimTimers.Remove(entity);
            }
            else
            {
                BasePlayer captor = BasePlayer.FindByID(_captors[entity]);
                if (captor == null || !captor.IsAlive() || Vector3.Distance(entity.transform.position, captor.transform.position) > 25f)
                {
                    ReleaseHostage(entity);
                    return;
                }

                BasePlayer npc = entity as BasePlayer;
                if (npc != null && !npc.isMounted)
                {
                    UpdateCaptorTrail(captor);
                    MimicCaptorFrames(npc, captor);
                }
            }
        }

        private void ProcessAimTimer(BaseEntity entity, BasePlayer aimer)
        {
            if (!_aimTimers.ContainsKey(entity)) _aimTimers[entity] = 0f;

            _aimTimers[entity] += 0.03f; 
            float progress = _aimTimers[entity];

            string color = progress > (_config.RequiredAimTime * 0.75f) ? "<color=red>" : "<color=yellow>";
            string text = $"{color}STICK-UP: {(_config.RequiredAimTime - progress):F1}s</color>";
            
            aimer.SendConsoleCommand("ddraw.text", 0.04f, Color.white, entity.CenterPoint() + new Vector3(0, 0.45f, 0), text);

            if (progress >= _config.RequiredAimTime)
            {
                _aimTimers.Remove(entity);
                EnterSurrenderState(entity, aimer);
            }
        }

        private void UpdateCaptorTrail(BasePlayer captor)
        {
            if (!_captorTrails.ContainsKey(captor.userID))
                _captorTrails[captor.userID] = new Queue<CaptorFrame>();

            Queue<CaptorFrame> trail = _captorTrails[captor.userID];
            
            // Record frames with higher precision for movement/jumping/looking
            if (trail.Count == 0 || Vector3.Distance(trail.Last().Position, captor.transform.position) > 0.02f || Quaternion.Angle(trail.Last().Rotation, captor.transform.rotation) > 1.0f)
            {
                trail.Enqueue(new CaptorFrame
                {
                    Position = captor.transform.position,
                    Rotation = captor.transform.rotation,
                    Pitch = captor.eyes.rotation.eulerAngles.x,
                    Yaw = captor.eyes.rotation.eulerAngles.y,
                    IsSprinting = captor.IsRunning(),
                    IsCrouching = captor.IsDucked(),
                    IsOnGround = captor.IsOnGround()
                });
            }

            while (trail.Count > 60) trail.Dequeue();
        }

        private void MimicCaptorFrames(BasePlayer npc, BasePlayer captor)
        {
            if (!_captorTrails.ContainsKey(captor.userID) || _captorTrails[captor.userID].Count == 0) return;

            float distToCaptor = Vector3.Distance(npc.transform.position, captor.transform.position);
            
            if (distToCaptor > _config.FollowDistance || _captorTrails[captor.userID].Count > 20)
            {
                CaptorFrame targetFrame = _captorTrails[captor.userID].Peek();
                
                if (Vector3.Distance(npc.transform.position, targetFrame.Position) < 0.15f && _captorTrails[captor.userID].Count > 1)
                {
                    _captorTrails[captor.userID].Dequeue();
                    targetFrame = _captorTrails[captor.userID].Peek();
                }

                float baseSpeed = targetFrame.IsSprinting ? 6.5f : 3.5f;
                float catchUpMultiplier = Mathf.Clamp(distToCaptor / _config.FollowDistance, 1.0f, 2.5f);
                float finalSpeed = baseSpeed * catchUpMultiplier;

                // Position Sync (Handles jumping via Y-coord mimicry)
                Vector3 newPos = Vector3.MoveTowards(npc.transform.position, targetFrame.Position, Time.deltaTime * finalSpeed);
                npc.ServerPosition = newPos;
                
                // Body Rotation Sync
                npc.transform.rotation = Quaternion.Lerp(npc.transform.rotation, targetFrame.Rotation, Time.deltaTime * 20f);

                // Animation & State Sync
                npc.modelState.SetFlag(ModelState.Flag.OnGround, targetFrame.IsOnGround);
                npc.modelState.SetFlag(ModelState.Flag.Sprinting, targetFrame.IsSprinting || catchUpMultiplier > 1.5f);
                npc.modelState.SetFlag(ModelState.Flag.Ducked, targetFrame.IsCrouching);

                // Mirror head/eye angles (Look around exactly like player)
                TrySetViewAngles(npc, targetFrame.Pitch, targetFrame.Yaw);

                npc.TransformChanged();
                npc.SendNetworkUpdateImmediate();
            }
            else
            {
                // Idle state mimicry (Still look where player is looking)
                npc.modelState.SetFlag(ModelState.Flag.Sprinting, false);
                npc.modelState.SetFlag(ModelState.Flag.Ducked, captor.IsDucked());
                npc.modelState.SetFlag(ModelState.Flag.OnGround, captor.IsOnGround());
                TrySetViewAngles(npc, captor.eyes.rotation.eulerAngles.x, captor.eyes.rotation.eulerAngles.y);
                npc.SendNetworkUpdate();
            }
        }

        #endregion

        #region ViewAngle Reflection

        private static MethodInfo miSetViewAngles;

        internal void TrySetViewAngles(BasePlayer player, float pitch, float yaw)
        {
            if (player == null) return;
            var angles = new Vector3(pitch, yaw, 0f);
            try
            {
                var t = typeof(BasePlayer);
                if (miSetViewAngles == null)
                    miSetViewAngles = t.GetMethod("SetViewAngles", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector3) }, null);
                
                if (miSetViewAngles != null) miSetViewAngles.Invoke(player, new object[] { angles });
                
                if (player.eyes != null)
                    player.eyes.rotation = Quaternion.Euler(pitch, yaw, 0f);
            }
            catch { }
        }

        #endregion

        #region Surrender & Release

        private void EnterSurrenderState(BaseEntity entity, BasePlayer captor)
        {
            if (entity == null) return;
            _surrenderedMatriarchs.Add(entity);
            
            BasePlayer npc = entity as BasePlayer;
            if (npc != null)
            {
                npc.SetPlayerFlag(BasePlayer.PlayerFlags.Wounded, true);
                npc.SetFlag(BaseEntity.Flags.Reserved1, true); 
                npc.SendNetworkUpdate();
            }
            
            PrintToChat($"<color=#ff4444>[STREET NEWS]</color> {npc?.displayName} has surrendered to {captor.displayName}!");
            
            timer.Once(3.5f, () => {
                if (entity == null || entity.IsDestroyed || _captors.ContainsKey(entity)) return;
                StartHostageFollowing(entity, captor);
            });
        }

        private void StartHostageFollowing(BaseEntity entity, BasePlayer captor)
        {
            if (entity == null || captor == null) return;
            _captors[entity] = captor.userID;
            _surrenderedMatriarchs.Remove(entity);
            
            BasePlayer npc = entity as BasePlayer;
            if (npc != null)
            {
                npc.SetPlayerFlag(BasePlayer.PlayerFlags.Wounded, false);
                npc.SetFlag(BaseEntity.Flags.Reserved1, false);
                npc.SendNetworkUpdate();
            }
            
            captor.ChatMessage($"<color=#55ff55>RESTRAINED:</color> {npc?.displayName} is now mimicking your steps.");
        }

        private void ReleaseHostage(BaseEntity entity)
        {
            if (entity == null) return;
            ulong captorId;
            if (_captors.TryGetValue(entity, out captorId)) _captorTrails.Remove(captorId);

            _captors.Remove(entity);
            _surrenderedMatriarchs.Remove(entity);
            
            BasePlayer npc = entity as BasePlayer;
            if (npc != null)
            {
                npc.SetPlayerFlag(BasePlayer.PlayerFlags.Wounded, false);
                npc.SetFlag(BaseEntity.Flags.Reserved1, false);
                npc.modelState.SetFlag(ModelState.Flag.Sprinting, false);
                npc.modelState.SetFlag(ModelState.Flag.Ducked, false);
                npc.modelState.SetFlag(ModelState.Flag.OnGround, true);
                if (npc.isMounted) npc.DismountObject();
                npc.SendNetworkUpdate();
            }
            
            PrintToChat($"<color=#55ff55>[STREET NEWS]</color> {npc?.displayName} has been released.");
        }

        private bool IsLookingAt(BasePlayer player, BaseEntity target)
        {
            Vector3 directionToTarget = (target.transform.position - player.eyes.position).normalized;
            float dot = Vector3.Dot(player.eyes.HeadForward(), directionToTarget);
            return dot > 0.91f; 
        }

        private bool IsHoldingWeapon(BasePlayer player)
        {
            Item item = player.GetActiveItem();
            if (item == null) return false;
            return item.info.category == ItemCategory.Weapon;
        }

        #endregion

        #region Retaliation & Rewards

        private void TriggerRetaliation(string gangName, string matriarchName)
        {
            if (string.IsNullOrEmpty(gangName)) return;

            PrintToChat($"<color=#ffd700>★★★ ACHIEVEMENT UNLOCKED ★★★</color>");
            PrintToChat($"<color=#ff4444>STREET JUSTICE:</color> <color=#ffffff>{gangName}</color> is seeking revenge for the death of <color=#ffffff>{matriarchName}</color>!");
            PrintToChat($"<color=#ffd700>RETALIATION LOADOUT GRANTED TO ALL ONLINE MEMBERS.</color>");

            foreach (var player in BasePlayer.activePlayerList)
            {
                string pGang = HoodWars?.Call<string>("GetPlayerGangName", player.userID) ?? "Neutral";
                if (pGang == gangName)
                {
                    Item c4 = ItemManager.CreateByName(ItemC4, 2);
                    if (c4 != null)
                    {
                        player.GiveItem(c4);
                        player.ChatMessage("<color=#ff4444>[GANG LOADOUT]</color> You received 2 C4. Go get your revenge!");
                        Effect.server.Run("assets/prefabs/tools/timed.explosive.charge/effects/impact.prefab", player.transform.position);
                    }
                }
            }
        }

        #endregion

        #region Buffs & Admin

        private void UpdateMatriarchAuras()
        {
            foreach (var kvp in _activeMatriarchs)
            {
                HandleAuraProcessing(kvp.Value.Grandma, kvp.Key, true);
                HandleAuraProcessing(kvp.Value.Mom, kvp.Key, false);
            }

            foreach (var ent in _manualMatriarchs)
            {
                if (ent == null) continue;
                BasePlayer npc = ent as BasePlayer;
                if (npc == null) continue;
                bool isG = npc.displayName.Contains("Grandma");
                HandleAuraProcessing(ent, "Admin_Test", isG);
            }
        }

        private void HandleAuraProcessing(BaseEntity ent, string gang, bool isGrandma)
        {
            if (ent == null || ent.IsDestroyed || _captors.ContainsKey(ent) || _surrenderedMatriarchs.Contains(ent)) return;

            float radius = isGrandma ? _config.Grandma.Radius : _config.Mom.Radius;
            ApplyAura(ent, gang, radius, p => {
                if (isGrandma) {
                    p.metabolism.comfort.Add(0.5f);
                    if (p.health < 100) p.Heal(0.5f);
                } else {
                    foreach (var item in p.inventory.containerWear.itemList) {
                        if (item.hasCondition && item.condition < item.maxCondition) { item.condition += 1.0f; item.MarkDirty(); }
                    }
                }
            });
        }

        private void ApplyAura(BaseEntity source, string gangName, float radius, Action<BasePlayer> effect)
        {
            List<BasePlayer> nearby = new List<BasePlayer>();
            Vis.Entities(source.transform.position, radius, nearby);
            foreach (var player in nearby)
            {
                string pGang = HoodWars != null ? HoodWars.Call<string>("GetPlayerGangName", player.userID) : "Admin_Test";
                if (pGang == gangName || gangName == "Admin_Test") effect(player);
            }
        }

        private void DistributeGrandmaMeals()
        {
            foreach (var kvp in _activeMatriarchs)
            {
                BaseEntity grandma = kvp.Value.Grandma;
                if (grandma == null || grandma.IsDestroyed || _captors.ContainsKey(grandma)) continue;

                List<BasePlayer> nearby = new List<BasePlayer>();
                Vis.Entities(grandma.transform.position, _config.Grandma.Radius, nearby);
                
                BasePlayer luckyMember = nearby.FirstOrDefault(p => (HoodWars?.Call<string>("GetPlayerGangName", p.userID) ?? "Neutral") == kvp.Key);
                if (luckyMember != null)
                {
                    Item meal = ItemManager.CreateByName("porkbeans", 1);
                    if (meal != null)
                    {
                        luckyMember.GiveItem(meal);
                        luckyMember.ChatMessage($"<color=#55ff55>{_config.Grandma.Name}:</color> Here you go, baby. Eat something.");
                    }
                }
            }
        }

        [ChatCommand("gspawn")]
        private void CmdGSpawn(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermAdmin)) return;
            if (args.Length < 1) { player.ChatMessage("Usage: /gspawn <grandma|mom>"); return; }

            bool isGrandma = args[0].ToLower() == "grandma";
            BaseEntity ent = InternalSpawn(player.transform.position, "TestHood", isGrandma);
            if (ent != null)
            {
                _manualMatriarchs.Add(ent);
                player.ChatMessage($"Spawned {args[0]}. Aim for 14s. Look-around mimicry active.");
            }
        }

        [ChatCommand("gclear")]
        private void CmdGClear(BasePlayer player)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermAdmin)) return;
            foreach (var ent in _manualMatriarchs) if (ent != null) ent.Kill();
            _manualMatriarchs.Clear();
            player.ChatMessage("Cleared manual NPCs.");
        }

        #endregion

        #region Hooks

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return null;

            bool isMatriarch = false;
            foreach (var set in _activeMatriarchs.Values)
                if (set.Grandma == entity || set.Mom == entity) { isMatriarch = true; break; }
            if (!isMatriarch) isMatriarch = _manualMatriarchs.Contains(entity);

            if (isMatriarch)
            {
                if (info.damageTypes.Has(Rust.DamageType.Collision) || 
                    info.damageTypes.Has(Rust.DamageType.Fall) || 
                    info.damageTypes.Has(Rust.DamageType.Drowned)) 
                    return true;
                
                if (_config.InvulnerableWhileSurrendered && _surrenderedMatriarchs.Contains(entity)) 
                    return true;
            }
            return null;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;
            string gangOwner = "";
            bool wasG = false;

            foreach(var kvp in _activeMatriarchs)
            {
                if (kvp.Value.Grandma == entity) { gangOwner = kvp.Key; wasG = true; break; }
                if (kvp.Value.Mom == entity) { gangOwner = kvp.Key; wasG = false; break; }
            }

            if (string.IsNullOrEmpty(gangOwner)) return;

            TriggerRetaliation(gangOwner, wasG ? "Grandma" : "Mom");

            _captors.Remove(entity);
            _surrenderedMatriarchs.Remove(entity);
            
            float penalty = wasG ? _config.Grandma.InfluencePenalty : _config.Mom.InfluencePenalty;
            if (TurfGraffiti != null) TurfGraffiti.Call("ReduceInfluence", gangOwner, penalty);

            timer.Once(_config.RespawnTimeSeconds, () => SpawnMatriarchAtHQ(gangOwner, wasG));
        }

        #endregion
    }
}