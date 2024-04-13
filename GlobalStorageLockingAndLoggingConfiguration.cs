using Rocket.API;

namespace Shauna.GlobalStorageLockingAndLogging
{
    public class GlobalStorageLockingAndLoggingConfiguration: IRocketPluginConfiguration
    {
        public bool EnableAdminOverride;
        public bool LockStorage;
        // public ushort UnlockedStorageItemId;
        public bool PreventHarvest;
        public bool LogHarvest;
        public bool PreventSalvage;
        public bool LogSalvage;
        public bool GroupCanAccess;
        public int BackgroundIDDictionarySaveIntervalInMinutes;

        public void LoadDefaults()
        {
            EnableAdminOverride = true;
            LockStorage = true;
            // UnlockedStorageItemId = 38;
            PreventHarvest = true;
            LogHarvest = false;
            PreventSalvage = true;
            LogSalvage = false;
            GroupCanAccess = true;
            BackgroundIDDictionarySaveIntervalInMinutes = 60;
            
        }
    }
}
