using System;
using System.IO;
using System.Reflection;

namespace CMOLocalizer
{
    public class CustomAppDomainManager : AppDomainManager
    {
        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            try
            {
                string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cmo_localizer_boot.log");
                File.AppendAllText(logFile, string.Format("[{0:yyyy-MM-dd HH:mm:ss}] CMOLocalizer AppDomainManager Initialized successfully in {1}!\r\n", DateTime.Now, AppDomain.CurrentDomain.FriendlyName));
            }
            catch { }
            base.InitializeNewDomain(appDomainInfo);
        }
    }
}
