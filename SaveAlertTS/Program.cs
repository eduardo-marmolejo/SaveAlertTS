using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace SaveAlertTS
{
    internal static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        private static void Main()
        {
            bool created;
            _mutex = new Mutex(true, "Global\\SaveAlertTS_SingleInstance", out created);

            if (!created)
                return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Localization.Initialize();
            try
            {
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(Localization.Language);
            }
            catch
            {
            }

            Application.Run(new TeklaWatcher());

            GC.KeepAlive(_mutex);
        }
    }
}
