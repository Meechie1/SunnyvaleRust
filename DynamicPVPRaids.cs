//Requires: DynamicPVP
//Requires: ZoneManager

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Dynamic PVP Raids", "zenmarev", "1.0.0")]
    [Description("Addon to Dynamic PVP plugin to create timed zones during player raid events.")]
    public class DynamicPVPRaids : RustPlugin
    {
        #region Plugin Dependencies

        [PluginReference]
        private readonly Plugin DynamicPVP, ZoneManager;

        #endregion

        #region Fields

        private PlayerRaidConfig playerRaidConfig;

        private const string EVENT_PLAYER_RAID = "PlayerRaid";

        private readonly Dictionary<string, List<PlayerRaid>> activePlayerRaids = new Dictionary<string, List<PlayerRaid>>();
        private readonly Dictionary<string, PlayerRaid> activeRaids = new Dictionary<string, PlayerRaid>();
        private readonly Dictionary<ulong, DateTime> raidEventTimes = new Dictionary<ulong, DateTime>();
        private object raidLock = new object();

        #endregion

        #region PlayerRaidConfig

        private class PlayerRaidConfig : DynamicPVP.TimedEventS
        {
            [JsonProperty(PropertyName = "Minimum Required Damage Percent", Order = 24)]
            public float damagePercentMinimum = 0.5f;

            [JsonProperty(PropertyName = "Raidable Entities (by Type name)", Order = 25)]
            public string[] damageTypes = new[]
            {
                nameof(BuildingBlock),
                nameof(BuildingPrivlidge),
                nameof(Door),
                nameof(SimpleBuildingBlock),
            };

            [JsonProperty(PropertyName = "Raidable Entities (by Prefab name)", Order = 26)]
            public string[] damagePrefabs = new[]
            {
                "refinery_small_deployed.prefab",
                "furnace.large.prefab",
            };
        }

        #endregion PlayerRaidConfig

        #region Plugin Overrides

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                playerRaidConfig = Config.ReadObject<PlayerRaidConfig>();
                if (playerRaidConfig == null)
                {
                    LoadDefaultConfig();
                }
            }
            catch (Exception ex)
            {
                PrintError($"The configuration file is corrupted. \n{ex}");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating a new configuration file");
            playerRaidConfig = new PlayerRaidConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(playerRaidConfig);

        #endregion

        #region Oxide Hooks

        #region System

        private void Init()
        {
            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(OnGetBaseEventS));
            Unsubscribe(nameof(OnHandleDeleteDynamicZone));
        }

        private void OnServerInitialized()
        {
            if (playerRaidConfig.enabled)
            {
                Subscribe(nameof(OnEntityTakeDamage));
                Subscribe(nameof(OnGetBaseEventS));
                Subscribe(nameof(OnHandleDeleteDynamicZone));
            }
        }

        #endregion System

        #region Entity

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            ValidateIsRaid(entity, info);
        }

        #endregion Entity

        #endregion Oxide Hooks

        #region Handle Player Raid

        private void ValidateIsRaid(BaseCombatEntity entity, HitInfo info)
        {
            if (!playerRaidConfig.enabled) return;
            if (entity == null || info.InitiatorPlayer == null || info.InitiatorPlayer.userID <= 0) return;

            if (!playerRaidConfig.damagePrefabs.Contains(entity.ShortPrefabName)
                && !playerRaidConfig.damageTypes.Contains(entity.GetType().Name)) return;

            var damageTotal = info.damageTypes.Total();
            if (damageTotal <= 0) return;

            if (entity.OwnerID <= 0
                || entity.OwnerID == info.InitiatorPlayer.userID
                || (info.InitiatorPlayer.Team?.members.Contains(entity.OwnerID) ?? false)) return;

            if (activePlayerRaids.ContainsKey(info.InitiatorPlayer.UserIDString))
            {
                bool inAny = false;
                foreach (var raid in activePlayerRaids[info.InitiatorPlayer.UserIDString])
                {
                    if (IsPlayerInZone(raid.zoneID, info.InitiatorPlayer))
                    {
                        raid.lastRaidEvent = DateTime.Now;
                        inAny = true;
                    }
                }
                if (inAny) return;
            }

            float newHealth = entity.Health() - damageTotal;
            if (newHealth <= 0 || 1 - (newHealth / entity.MaxHealth()) > playerRaidConfig.damagePercentMinimum)
            {
                HandleRaidEvent(info.InitiatorPlayer);
            }
        }

        private bool HandleRaidEvent(BasePlayer player)
        {
            lock (raidLock)
            {
                if (raidEventTimes.ContainsKey(player.userID) && (DateTime.Now - raidEventTimes[player.userID]).TotalSeconds <= 10) return false;
                raidEventTimes[player.userID] = DateTime.Now;
            }

            if (!CanCreateDynamicPVP(EVENT_PLAYER_RAID, player))
            {
                return false;
            }
            var position = player.transform.position;
            position.y = TerrainMeta.HeightMap.GetHeight(position);

            var raid = new PlayerRaid(player);
            activeRaids[raid.zoneID] = raid;
            if (activePlayerRaids.ContainsKey(raid.playerID))
            {
                activePlayerRaids[raid.playerID].Add(raid);
            }
            else
            {
                activePlayerRaids[raid.playerID] = new List<PlayerRaid> { raid };
            }
            return CreateDynamicZone(EVENT_PLAYER_RAID, position, raid.zoneID, playerRaidConfig.GetDynamicZoneS().ZoneSettings(player.transform));
        }

        public class PlayerRaid
        {
            public string playerID { get; }

            public string zoneID { get; }

            public DateTime startRaidEvent { get; set; }

            public DateTime lastRaidEvent { get; set; }

            public PlayerRaid(BasePlayer player)
            {
                playerID = player.UserIDString;
                zoneID = Guid.NewGuid().ToString();
                startRaidEvent = lastRaidEvent = DateTime.Now;
            }
        }

        #endregion Handle Player Raid

        #region DynamicPvp Integration

        private bool CanCreateDynamicPVP(string eventName, BaseEntity entity) => (bool)DynamicPVP.Call("CanCreateDynamicPVP", eventName, entity);

        private bool CreateDynamicZone(string eventName, Vector3 position, string zoneID = "", string[] zoneSettings = null, bool delay = true)
            => (bool)DynamicPVP.Call("CreateDynamicZone", eventName, position, zoneID, zoneSettings, delay);

        private object OnGetBaseEventS(string eventName) => eventName == EVENT_PLAYER_RAID ? playerRaidConfig : null;

        private object OnHandleDeleteDynamicZone(string zoneID)
        {
            if (activeRaids.ContainsKey(zoneID))
            {
                var continueRaidDuration = (activeRaids[zoneID].lastRaidEvent - activeRaids[zoneID].startRaidEvent).TotalSeconds;
                if (continueRaidDuration > 0)
                {
                    activeRaids[zoneID].lastRaidEvent = activeRaids[zoneID].startRaidEvent = DateTime.Now;

                    DynamicPVP.Call("TryRemoveEventTimer", zoneID);
                    DynamicPVP.Call("HandleDeleteDynamicZone", zoneID, (float)continueRaidDuration, EVENT_PLAYER_RAID);
                    return false;
                }
                else
                {
                    activePlayerRaids[activeRaids[zoneID].playerID].Remove(activeRaids[zoneID]);
                    activeRaids.Remove(zoneID);
                }
            }
            return null;
        }

        #endregion

        #region ZoneManager Integration

        private bool IsPlayerInZone(string zoneID, BasePlayer player) => (bool)ZoneManager.Call("IsPlayerInZone", zoneID, player);

        #endregion
    }
}