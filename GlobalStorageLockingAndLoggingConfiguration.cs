using Rocket.API;

namespace GlobalStorageLockingAndLogging
{
    public class GlobalStorageLockingAndLoggingConfiguration : IRocketPluginConfiguration
    {
        public bool EnableAdminOverride;
        public bool LockStorage;
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
            PreventHarvest = true;
            LogHarvest = false;
            PreventSalvage = true;
            LogSalvage = false;
            GroupCanAccess = true;
            BackgroundIDDictionarySaveIntervalInMinutes = 60;
        }
    }
}