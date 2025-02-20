using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json;
using Rocket.Core.Plugins;
using Rocket.Unturned;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Items;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace GlobalStorageLockingAndLogging
{
    [HarmonyPatch]
    public class GlobalStorageLockingAndLogging : RocketPlugin<GlobalStorageLockingAndLoggingConfiguration>
    {
        string filename = "Plugins/GlobalStorageLockingAndLogging/ID-Name.json";

        public static GlobalStorageLockingAndLogging instance;
        private int _dictionarySaveInterval = 60000;
        private Dictionary<string, string> _DisplayNames;

        protected override void Load()
        {
            Logger.Log("Starting GlobalStorageLockingAndLogging");
            instance = this;

            if (Configuration.Instance.LockStorage)
            {
                Harmony harmony = new Harmony("GlobalStorageLockingAndLogging");
                harmony.PatchAll();
            }

            
            BarricadeManager.onHarvestPlantRequested += (CSteamID steamid, byte x, byte y, ushort plant, ushort index,
                    ref bool shouldallow) =>
                OnHarvestOrSalvageRequested(steamid, x, y, plant, index, ref shouldallow, true);
            BarricadeManager.onSalvageBarricadeRequested += (CSteamID steamid, byte x, byte y, ushort plant,
                    ushort index,
                    ref bool shouldallow) =>
                OnHarvestOrSalvageRequested(steamid, x, y, plant, index, ref shouldallow, false);
            U.Events.OnPlayerConnected += EventsOnOnPlayerConnected;
            U.Events.OnPlayerDisconnected += EventsOnOnPlayerDisconnected;
            
            _dictionarySaveInterval = Configuration.Instance.BackgroundIDDictionarySaveIntervalInMinutes * 60000;
            LoadID2NameDictionary();

            if (_dictionarySaveInterval == 0)
            {
                Logger.Log(
                    "Background saving of the 'id to display name' dictionary disabled!  Set to a value > 0(minutes) to enable. (60 should be good)");
                return;
            }

            Thread backgroundSaveThread = new Thread(DisplayNameBackgroundThread);
            backgroundSaveThread.Start();
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(InteractableStorage), nameof(InteractableStorage.ReceiveInteractRequest))]
        static bool ReceiveInteractRequest(in ServerInvocationContext context, bool quickGrab,
            InteractableStorage __instance)
        {
            bool allowed = false;
            var player = UnturnedPlayer.FromPlayer(context.GetPlayer());

            if (__instance.owner == CSteamID.Nil || __instance.owner == player.CSteamID ||
                instance.Configuration.Instance.EnableAdminOverride && player.IsAdmin)
                allowed = true;

            if (!allowed)
            {
                UnturnedChat.Say(player, "You are not allowed to access this storage.", Color.red);
            }

            return allowed;
        }
        
        
        protected override void Unload()
        {
            SaveID2NameDictionary();
        }

        private void DisplayNameBackgroundThread()
        {
            for (;;)
            {
                Thread.Sleep(_dictionarySaveInterval);
                SaveID2NameDictionary();
            }
        }


        private void LoadID2NameDictionary()
        {
            _DisplayNames = new Dictionary<string, string>();

            if (!File.Exists(filename))
            {
                return;
            }

            string text = File.ReadAllText(filename);
            _DisplayNames = JsonConvert.DeserializeObject<Dictionary<string, string>>(text);

            Logger.Log("Loaded ID to DisplayName dictionary: " + _DisplayNames.Count + " Entries");
        }


        private void SaveID2NameDictionary()
        {
            string output = "";
            int count = 0;

            lock (_DisplayNames)
            {
                output = JsonConvert.SerializeObject(_DisplayNames);
                count = _DisplayNames.Count;
            }

            File.WriteAllText(filename, output);
            Logger.Log("Saved ID to DisplayName dictionary: " + count + " Entries");
        }


        private void EventsOnOnPlayerConnected(UnturnedPlayer player)
        {
            PlayerSubscribeToDropRequested(player);
            lock (_DisplayNames)
            {
                _DisplayNames[player.CSteamID.ToString()] = player.DisplayName;
            }
        }


        private void EventsOnOnPlayerDisconnected(UnturnedPlayer player)
        {
            PlayerUnsubscribeToDropRequested(player);
        }


        private void PlayerSubscribeToDropRequested(UnturnedPlayer player)
        {
            player.Player.inventory.onDropItemRequested +=
                (PlayerInventory inventory, Item item, ref bool allow) =>
                    OnDropItemRequested(inventory, item, ref allow, player);
        }


        private void PlayerUnsubscribeToDropRequested(UnturnedPlayer player)
        {
            player.Player.inventory.onDropItemRequested -=
                (PlayerInventory inventory, Item item, ref bool allow) =>
                    OnDropItemRequested(inventory, item, ref allow, player);
        }


        private void OnDropItemRequested(PlayerInventory inventory, Item item, ref bool shouldAllow,
            UnturnedPlayer player)
        {
            if (inventory.storage == null) // apparently personal storage is null
                return;

            if (!PlayersOwnsStorage(player, player.Inventory.storage.owner, player.Inventory.storage.group))
            {
                shouldAllow = false;
            }
        }


        private void LogPlayersAction(ushort id, UnturnedPlayer player, CSteamID owner, bool attempted)
        {
            string ownerStr = owner.ToString();
            if (_DisplayNames.ContainsKey(owner.ToString()))
                ownerStr = _DisplayNames[owner.ToString()];

            Logger.LogWarning(
                String.Format("{0} from {1} by: {2}, steam id:{3} item: {4}: {5}",
                    attempted ? "Attempted to take" : "Taken",
                    ownerStr,
                    player.DisplayName,
                    player.CSteamID,
                    UnturnedItems.GetItemAssetById(id).itemName,
                    id)
            );
        }


        private void OnHarvestOrSalvageRequested(CSteamID steamid, byte x, byte y, ushort plant, ushort index,
            ref bool shouldallow, bool isHarvest)
        {
            if (BarricadeManager.tryGetRegion(x, y, plant, out BarricadeRegion region))
            {
                BarricadeData data = region.barricades[index];

                UnturnedPlayer player = UnturnedPlayer.FromCSteamID(steamid);

                if (!PlayersOwnsStorage(player, (CSteamID)data.owner, (CSteamID)data.@group))
                {
                    if (isHarvest)
                    {
                        if (Configuration.Instance.LogHarvest)
                            LogPlayersAction(data.barricade.id, player, (CSteamID)data.owner,
                                Configuration.Instance.PreventHarvest);

                        if (Configuration.Instance.PreventHarvest)
                            shouldallow = false;
                    }
                    else //not harvest
                    {
                        if (Configuration.Instance.LogSalvage)
                            LogPlayersAction(data.barricade.id, player, (CSteamID)data.owner,
                                Configuration.Instance.PreventSalvage);

                        if (Configuration.Instance.PreventSalvage)
                            shouldallow = false;
                    }
                }
            }
        }


        private static bool PlayersOwnsStorage(UnturnedPlayer player, CSteamID owner, CSteamID group)
        {
            if ((player.CSteamID == owner
                 || player.Player.quests.groupID != CSteamID.Nil
                 && player.Player.quests.groupID == group
                 && instance.Configuration.Instance.GroupCanAccess)
                || owner == CSteamID.Nil) //an airdrop has no owner
                return true;

            return false;
        }
    }
}