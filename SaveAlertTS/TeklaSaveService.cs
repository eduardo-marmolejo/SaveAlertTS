using System;
using System.IO;
using System.Threading;

namespace SaveAlertTS
{
    public static class TeklaSaveService
    {
        private const int TimeoutMs = 180000;
        private const int PollMs = 200;

        public static string BridgeDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SaveAlertTS", "bridge");
            }
        }

        public static bool SaveCurrentModel(uint pid, string modelPath)
        {
            try
            {
                Directory.CreateDirectory(BridgeDirectory);

                string requestId = Guid.NewGuid().ToString("N");
                string reqPath = Path.Combine(BridgeDirectory, "request_" + requestId + ".txt");
                string respPath = Path.Combine(BridgeDirectory, "response_" + requestId + ".txt");

                File.WriteAllText(reqPath,
                    "REQUESTID=" + requestId + Environment.NewLine +
                    "PID=" + pid + Environment.NewLine +
                    "MODEL=" + (modelPath ?? "") + Environment.NewLine +
                    "REQUESTEDAT=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);

                Log("Pedido enviado pid=" + pid + " rid=" + requestId + " model=[" + (modelPath ?? "") + "]");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < TimeoutMs)
                {
                    if (File.Exists(respPath))
                    {
                        string response = ReadAllTextSafe(respPath);
                        if (response != null)
                        {
                            string[] lines = response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                            bool ok = string.Equals(GetValue(lines, "OK"), "true", StringComparison.OrdinalIgnoreCase);
                            string message = GetValue(lines, "MESSAGE");
                            Log("Respuesta pid=" + pid + " ok=" + ok + " msg=[" + message + "]");
                            CleanupFiles(reqPath, respPath);
                            return ok;
                        }
                    }
                    else if (!File.Exists(reqPath))
                    {
                        Log("Pedido tomado por el puente (request removido), esperando respuesta...");
                    }

                    Thread.Sleep(PollMs);
                }

                Log("TIMEOUT (" + TimeoutMs + "ms) esperando respuesta pid=" + pid);
                CleanupFiles(reqPath, respPath);
                return false;
            }
            catch (Exception ex)
            {
                Log("EXCEPCION: " + ex.GetType().Name + " - " + ex.Message);
                if (ex.InnerException != null)
                    Log("Inner: " + ex.InnerException.GetType().Name + " - " + ex.InnerException.Message);
                return false;
            }
        }

        public static void CleanupStale()
        {
            try
            {
                if (!Directory.Exists(BridgeDirectory))
                    return;

                foreach (string file in Directory.GetFiles(BridgeDirectory))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if (DateTime.UtcNow - fi.LastWriteTimeUtc > TimeSpan.FromMinutes(10))
                        {
                            fi.Delete();
                            Log("Limpiado archivo viejo: " + fi.Name);
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        private static string ReadAllTextSafe(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch
            {
                return null;
            }
        }

        private static string GetValue(string[] lines, string key)
        {
            string prefix = key + "=";
            foreach (string line in lines)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(prefix.Length).Trim();
            }
            return null;
        }

        private static void CleanupFiles(params string[] paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception) { }
            }
        }

        private static void Log(string message)
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TeklaSaveWatcher_debug.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " - [TeklaSaveService] " + message + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }
}
