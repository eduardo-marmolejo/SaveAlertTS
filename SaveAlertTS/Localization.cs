using System;
using System.Globalization;
using System.IO;

namespace SaveAlertTS
{
    internal static class Localization
    {
        private static string _lang = "en";

        public static string Language
        {
            get { return _lang; }
        }

        public static void Initialize()
        {
            string code = null;

            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang.txt");
                if (File.Exists(path))
                    code = File.ReadAllText(path).Trim();
            }
            catch
            {
            }

            if (string.IsNullOrEmpty(code))
            {
                try
                {
                    code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                }
                catch
                {
                    code = "en";
                }
            }

            code = (code ?? string.Empty).Trim().ToLowerInvariant();
            _lang = (code == "es" || code == "spanish") ? "es" : "en";
        }

        private static string Pick(string es, string en)
        {
            return _lang == "es" ? es : en;
        }

        public static string FormTitle
        {
            get { return Pick("Alerta de guardado", "Save Alert"); }
        }

        public static string Message
        {
            get { return Pick("No olvides guardar antes de trabajar.", "Don't forget to save before to work."); }
        }

        public static string SubMessage
        {
            get { return Pick("Guarda para hacer check-in.", "Save to check-in."); }
        }

        public static string OkSave
        {
            get { return Pick("OK, guardar", "OK, save"); }
        }

        public static string Saving
        {
            get { return Pick("Guardando...", "Saving..."); }
        }

        public static string SavingModel
        {
            get { return Pick("Guardando el modelo...", "Saving the model..."); }
        }

        public static string SaveError
        {
            get { return Pick("Error al guardar el modelo.", "Error saving the model."); }
        }

        public static string Retry
        {
            get { return Pick("Reintentar", "Retry"); }
        }

        public static string TrayExit
        {
            get { return Pick("Salir", "Exit"); }
        }

        public static string TrayTooltip
        {
            get { return Pick("Tekla Save Watcher", "Tekla Save Watcher"); }
        }
    }
}
