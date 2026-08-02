using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SaveAlertShortcutPatcher
{
    internal static class Program
    {
        private const string TeklaExeName = "TeklaStructures.exe";
        private static readonly Regex MacroArgRegex = new Regex("-m\\s+(?:\"[^\"]*\"|\\S+)", RegexOptions.IgnoreCase);

        private static int Main(string[] args)
        {
            try
            {
                bool uninstall = args.Any(a => a.Equals("uninstall", StringComparison.OrdinalIgnoreCase));
                string macroPath = args.FirstOrDefault(a =>
                    !a.StartsWith("-", StringComparison.Ordinal) &&
                    !a.Equals("install", StringComparison.OrdinalIgnoreCase) &&
                    !a.Equals("uninstall", StringComparison.OrdinalIgnoreCase));

                if (uninstall)
                {
                    Console.WriteLine("SaveAlertShortcutPatcher: desinstalando (removiendo -m).");
                }
                else
                {
                    if (string.IsNullOrEmpty(macroPath))
                    {
                        Console.Error.WriteLine("Uso: SaveAlertShortcutPatcher install \"<ruta de SaveBridge.cs>\" | uninstall");
                        return 2;
                    }
                    macroPath = Path.GetFullPath(macroPath);
                    if (!File.Exists(macroPath))
                    {
                        Console.Error.WriteLine("La macro no existe: " + macroPath);
                        return 2;
                    }
                    Console.WriteLine("SaveAlertShortcutPatcher: instalando -m \"{0}\"", macroPath);
                }

                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                if (shell == null)
                {
                    Console.Error.WriteLine("No se pudo crear WScript.Shell.");
                    return 1;
                }

                var roots = new List<string>();
                Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
                Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
                Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
                Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + @"\Microsoft\Windows\Start Menu\Programs");
                Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + @"\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

                var lnkFiles = new List<string>();
                foreach (string root in roots)
                    CollectLnk(root, lnkFiles);

                int patched = 0;
                foreach (string lnk in lnkFiles)
                {
                    if (PatchShortcut(shell, lnk, macroPath, uninstall))
                        patched++;
                }

                Console.WriteLine("Accesos directos revisados: " + lnkFiles.Count + " | modificados: " + patched);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex);
                return 1;
            }
        }

        private static void Add(ICollection<string> list, string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                list.Add(path);
        }

        private static void CollectLnk(string dir, ICollection<string> result)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;

            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.lnk"))
                    result.Add(file);
            }
            catch
            {
            }

            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                    CollectLnk(sub, result);
            }
            catch
            {
            }
        }

        private static bool PatchShortcut(dynamic shell, string lnkPath, string macroPath, bool uninstall)
        {
            try
            {
                dynamic lnk = shell.CreateShortcut(lnkPath);
                string target = ((string)lnk.TargetPath ?? "").Trim().Trim('"');
                if (string.IsNullOrEmpty(target))
                    return false;

                if (!string.Equals(Path.GetFileName(target), TeklaExeName, StringComparison.OrdinalIgnoreCase))
                    return false;

                string currentArgs = (string)lnk.Arguments ?? "";
                string newArgs = uninstall ? RemoveMacroArg(currentArgs) : SetMacroArg(currentArgs, macroPath);

                if (string.Equals(newArgs, currentArgs, StringComparison.Ordinal))
                    return false;

                lnk.Arguments = newArgs;
                lnk.Save();

                Console.WriteLine((uninstall ? "Removido  : " : "Modificado: ") + lnkPath);
                Console.WriteLine("  Args -> " + (newArgs.Length > 0 ? newArgs : "(vacío)"));
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error en " + lnkPath + ": " + ex.Message);
                return false;
            }
        }

        private static string SetMacroArg(string currentArgs, string macroPath)
        {
            string withoutOld = MacroArgRegex.Replace(currentArgs, " ").Trim();
            string result = withoutOld;
            if (result.Length > 0)
                result += " ";
            result += "-m \"" + macroPath + "\"";
            return result;
        }

        private static string RemoveMacroArg(string currentArgs)
        {
            return MacroArgRegex.Replace(currentArgs, " ").Trim();
        }
    }
}
