// Macro desplegado automaticamente por SaveBridge.cs (opcion B).
// No ejecutar manualmente: solo actua si existe save_dialog_request_<pid>.txt.
// Version embebida en SaveBridge.cs como base64; mantener ambos sincronizados.

#pragma warning disable 1633 // Unrecognized #pragma directive
#pragma reference "Tekla.Macros.Akit"
#pragma reference "Tekla.Macros.Runtime"
#pragma warning restore 1633 // Unrecognized #pragma directive

using System;
using System.IO;
using System.Threading;
using Tekla.Macros.Akit;
using Tekla.Macros.Runtime;

namespace UserMacros
{
    public sealed class SaveAlertNativeSave
    {
        [Tekla.Macros.Runtime.MacroEntryPointAttribute()]
        public static void Run(IMacroRuntime runtime)
        {
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SaveAlertTS", "bridge");
            string request = Path.Combine(dir, "save_dialog_request_" + pid + ".txt");

            Log("=== SaveAlertNativeSave === pid=" + pid
                + " thread=" + Thread.CurrentThread.ManagedThreadId
                + " pool=" + Thread.CurrentThread.IsThreadPoolThread);

            if (!File.Exists(request))
            {
                Log("Sin pedido pendiente; saliendo sin guardar.");
                return;
            }

            TryDelete(request);

            try
            {
                IAkitScriptHost akit = runtime.Get<IAkitScriptHost>();
                Log("Akit host obtenido. Despachando RunUI...");

                runtime.RunUI(() =>
                {
                    Log("UI thread=" + Thread.CurrentThread.ManagedThreadId
                        + " pool=" + Thread.CurrentThread.IsThreadPoolThread);
                    Log("Llamando akit.Callback(\"acmd_save_model\", \"\", \"main_frame\")");
                    akit.Callback("acmd_save_model", "", "main_frame");
                    Log("Comando despachado.");
                });

                Log("RunUI finalizo. FIN.");
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.GetType().Name + " - " + ex.Message);
                if (ex.InnerException != null)
                    Log("  Inner: " + ex.InnerException.GetType().Name + " - " + ex.InnerException.Message);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        private static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SaveAlertTS", "bridge");
                Directory.CreateDirectory(dir);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    + " - " + message + Environment.NewLine;
                File.AppendAllText(Path.Combine(dir, "native_save.log"), line);
            }
            catch (Exception)
            {
            }
        }
    }
}
