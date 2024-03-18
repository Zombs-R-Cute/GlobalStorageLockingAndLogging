using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Rocket.Core.Plugins;
using Rocket.Unturned;
using Rocket.Unturned.Items;
using Rocket.Unturned.Player;
using SDG.Framework.IO.Deserialization;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;


namespace Shauna.GlobalStorageLockingAndLogging
{
    public class PlayerItemState
    {
        public enum ItemState
        {
            Normal, // Nothing to undo
            UndoMove, // A thief trying to steal from a crate
        }

        public byte sPage;
        public byte sIndex;
        public ItemJar sJar;
        public ItemState itemState = ItemState.Normal;

        public void SetSource(byte page, byte index, ItemJar jar)
        {
            sPage = page;
            sIndex = index;
            sJar = jar;
        }

        public void ClearSource()
        {
            sJar = null;
        }

        public bool hasItem()
        {
            return sJar != null;
        }
    }

    public class GlobalStorageLockingAndLogging : RocketPlugin<GlobalStorageLockingAndLoggingConfiguration>
    {
        string filename = "Plugins/GlobalStorageLockingAndLogging/ID-Name.json";

        /// <summary>
        /// Maintains the state of items moved for preventing theft by essentially locking storage
        /// </summary>
        private Dictionary<CSteamID, PlayerItemState> _PlayerState = new Dictionary<CSteamID, PlayerItemState>(0);

        private int _dictionarySaveInterval = 60000;
        private Dictionary<string, string> _DisplayNames;

        protected override void Load()
        {
            Logger.Log("Starting GlobalStorageLockingAndLogging");
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
                Logger.Log("Background saving of the 'id to display name' dictionary disabled!  Set to a value > 0(minutes) to enable. (60 should be good)");
                return;
            }
            Thread backgroundSaveThread = new Thread(DisplayNameBackgroundThread);
            backgroundSaveThread.Start();
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
            _PlayerState[player.CSteamID] = new PlayerItemState();
            PlayerSubscribeToOnInventoryAdded(player);
            PlayerSubscribeToOnInventoryRemoved(player);
            PlayerSubscribeToDropRequested(player);
            lock (_DisplayNames)
            {
                _DisplayNames[player.CSteamID.ToString()] = player.DisplayName;
            }
        }


        private void EventsOnOnPlayerDisconnected(UnturnedPlayer player)
        {
            PlayerUnsubscribeToOnInventoryAdded(player);
            PlayerUnsubscribeToOnInventoryRemoved(player);
            PlayerUnsubscribeToDropRequested(player);
        }


        private void PlayerSubscribeToDropRequested(UnturnedPlayer player)
        {
            player.Player.inventory.onDropItemRequested +=
                (PlayerInventory inventory, Item item, ref bool allow) =>
                    ONDropItemRequested(inventory, item, ref allow, player);
        }


        private void PlayerUnsubscribeToDropRequested(UnturnedPlayer player)
        {
            player.Player.inventory.onDropItemRequested -=
                (PlayerInventory inventory, Item item, ref bool allow) =>
                    ONDropItemRequested(inventory, item, ref allow, player);
        }


        private void PlayerSubscribeToOnInventoryAdded(UnturnedPlayer player)
        {
            player.Player.inventory.onInventoryAdded +=
                (page, index, jar) => OnInventoryAdded(page, index, jar, player);
        }

        private void PlayerSubscribeToOnInventoryRemoved(UnturnedPlayer player)
        {
            player.Player.inventory.onInventoryRemoved +=
                (page, index, jar) => ONInventoryRemoved(page, index, jar, player);
        }

        private void PlayerUnsubscribeToOnInventoryAdded(UnturnedPlayer player)
        {
            player.Player.inventory.onInventoryAdded -=
                (page, index, jar) => OnInventoryAdded(page, index, jar, player);
        }

        private void PlayerUnsubscribeToOnInventoryRemoved(UnturnedPlayer player)
        {
            player.Player.inventory.onInventoryRemoved -=
                (page, index, jar) => ONInventoryRemoved(page, index, jar, player);
        }


        private void ONDropItemRequested(PlayerInventory inventory, Item item, ref bool shouldAllow,
            UnturnedPlayer player)
        {
            if (inventory.storage == null) // apparently personal storage is null
                return;

            if (!PlayersOwnsStorage(player, player.Inventory.storage.owner, player.Inventory.storage.group))
            {
                shouldAllow = false;
            }
        }


        private void ONInventoryRemoved(byte page, byte index, ItemJar jar, UnturnedPlayer player)
        {
            PlayerItemState playerItemState = _PlayerState[player.CSteamID];

            switch (playerItemState.itemState)
            {
                case PlayerItemState.ItemState.Normal:
                    playerItemState.SetSource(page, index, jar);
                    break;

                case PlayerItemState.ItemState.UndoMove:
                    playerItemState.ClearSource();
                    playerItemState.itemState = PlayerItemState.ItemState.Normal;
                    break;
            }
        }


        private void OnInventoryAdded(byte page, byte index, ItemJar jar, UnturnedPlayer player)
        {
            PlayerItemState playerItemState = _PlayerState[player.CSteamID]; // grab a local

            switch (playerItemState.itemState)
            {
                case PlayerItemState.ItemState.Normal:
                   
                    if (player.IsInVehicle) // ignore vehicle storage
                        return;
                    
                    // no vehicle, see if in own storage 
                    
                    if(player.Inventory.storage == null) //check if storage is opened.
                        return;
                    
                    if (PlayersOwnsStorage(player, player.Inventory.storage.owner, player.Inventory.storage.group)) // ignore own storage
                        return;

                    if (!playerItemState.hasItem())
                        return;

                    if (isFreeCrate(player) && playerItemState.sJar.item.id != Configuration.Instance.UnlockedStorageItemId)
                        return;

                    // page: 2 = Hands, 3 = Backpack, 4 = Vest, 5 = Top, 6 = Bottom, 7 = External
                    if (playerItemState.sJar.item.id == Configuration.Instance.UnlockedStorageItemId || playerItemState.sPage == 7)
                        playerItemState.itemState = PlayerItemState.ItemState.UndoMove;
                    else
                        return;

                    if (Configuration.Instance.LogStorageAction)
                    {
                        var ownerCSteamID = player.Inventory.storage.owner;

                        LogPlayersAction(jar.item.id, player, ownerCSteamID, Configuration.Instance.LockStorage);
                    }

                    if (Configuration.Instance.LockStorage)
                    {
                        player.Inventory.tryAddItem(playerItemState.sJar.item, playerItemState.sJar.x,
                            playerItemState.sJar.y, playerItemState.sPage,
                            playerItemState.sJar.rot); //restore the item to it's original position which triggers an recursive event

                        player.Inventory.removeItem(page, index);
                    }
                    else
                        playerItemState.itemState = PlayerItemState.ItemState.Normal;


                    break;
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


        private bool isFreeCrate(UnturnedPlayer player)
        {
            byte itemCount = player.Player.inventory.getItemCount(7);
            for (byte index = 0; index < itemCount; index++)
            {
                if (player.Player.inventory.getItem(7, index).item.id == Configuration.Instance.UnlockedStorageItemId)
                {
                    return true;
                }
            }

            return false;
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
                            LogPlayersAction(data.barricade.id, player, (CSteamID) data.owner,
                                Configuration.Instance.PreventHarvest);

                        if (Configuration.Instance.PreventHarvest)
                            shouldallow = false;
                    }
                    else//not harvest
                    {
                        if (Configuration.Instance.LogSalvage)
                            LogPlayersAction(data.barricade.id, player, (CSteamID) data.owner,
                                Configuration.Instance.PreventSalvage);

                        if (Configuration.Instance.PreventSalvage)
                            shouldallow = false;
                    }
                }
            }
        }


        private bool PlayersOwnsStorage(UnturnedPlayer player, CSteamID owner, CSteamID group)
        {
            if (player.CSteamID == owner || player.Player.quests.groupID != CSteamID.Nil && player.Player.quests.groupID == group && Configuration.Instance.GroupCanAccess)
                return true;
            
            return false;
        }
    }
}
