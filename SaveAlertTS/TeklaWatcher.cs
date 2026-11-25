using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tekla.Structures.Model;

namespace SaveAlertTS
{
    public class TeklaWatcher : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private readonly Control _uiSync;

        private readonly ArrayList _heldEvents = new ArrayList();
        private volatile bool _connected;
        private volatile bool _eventsRegistered;
        private volatile bool _titleChanged;
        private volatile bool _loadingDetected;
        private IntPtr _winEventHook;
        private frmSaveAlert _activeAlert;
        private int _tickCount;
        private string _teklaBinPath;
        private readonly HashSet<uint> _alertedPids = new HashSet<uint>();
        private readonly Dictionary<uint, string> _lastAlertedModelByPid = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> _lastMarkerLoadIdByPid = new Dictionary<uint, string>();
        private readonly Dictionary<uint, DateTime> _lastAlertTimeByPid = new Dictionary<uint, DateTime>();
        private volatile bool _forceDetect;
        private volatile bool _connecting;
        private volatile bool _modelConnectionAttempted;
        private HashSet<uint> _teklaPids = new HashSet<uint>();
        private int _pidRefreshTick;

        // Fase 1-4: anti-reaparicion y prioridad de marcadores del bridge.
        private const int TitleDebounceMs = 700;
        private const int DismissSuppressMs = 4000;
        private const int ConversionSuppressMs = 10000;
        private const int MarkerPriorityWindowMs = 1500;
        private const int MissingTitleGraceMs = 6000;
        private const int PostSaveSuppressMs = 12000;

        private readonly Dictionary<uint, DateTime> _suppressAlertsUntil = new Dictionary<uint, DateTime>();
        private readonly Dictionary<uint, DateTime> _postSaveSuppressUntil = new Dictionary<uint, DateTime>();
        private readonly Dictionary<uint, DateTime> _missingTitleSince = new Dictionary<uint, DateTime>();
        private readonly HashSet<string> _dismissedModels = new HashSet<string>();
        private readonly Dictionary<uint, bool> _lastSharedStateByPid = new Dictionary<uint, bool>();
        private DateTime _lastTitleChangeTime = DateTime.MinValue;
        private DateTime _titleDetectionPausedUntil = DateTime.MinValue;


        public TeklaWatcher()
        {
            LogDiagnostic("=== TeklaWatcher INICIADO ===");
            TeklaSaveService.CleanupStale();

            AppDomain.CurrentDomain.AssemblyResolve += HandleAssemblyResolve;

            var menu = new ContextMenuStrip();
            menu.Items.Add(Localization.TrayExit, null, (s, e) => ExitApp());

            _trayIcon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                Text = Localization.TrayTooltip,
                Visible = true,
                ContextMenuStrip = menu
            };

            _uiSync = new Control();
            _uiSync.CreateControl();
            var forceHandle = _uiSync.Handle;

            _pollTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _pollTimer.Tick += (s, e) => OnTick();
            _pollTimer.Start();

            LogDiagnostic("Timer iniciado.");
        }

        private System.Reflection.Assembly HandleAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = new System.Reflection.AssemblyName(args.Name);
                string dllName = name.Name + ".dll";

                if (name.Name.StartsWith("SaveAlertTS"))
                    return null;

                string executingDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string libDir = Path.GetFullPath(Path.Combine(executingDir, "..", "..", "..", "..", "Lib"));

                string libDll = Path.Combine(libDir, dllName);
                if (File.Exists(libDll))
                {
                    LogDiagnostic("Assembly resuelto (Lib): " + name.Name);
                    return System.Reflection.Assembly.LoadFrom(libDll);
                }

                if (_teklaBinPath == null)
                    _teklaBinPath = FindTeklaBinPath();

                if (_teklaBinPath != null)
                {
                    string teklaDll = Path.Combine(_teklaBinPath, dllName);
                    if (File.Exists(teklaDll))
                    {
                        LogDiagnostic("Assembly resuelto (Tekla bin): " + name.Name);
                        return System.Reflection.Assembly.LoadFrom(teklaDll);
                    }
                }

                LogDiagnostic("Assembly NO resuelto: " + name.Name);
                return null;
            }
            catch (Exception ex)
            {
                LogDiagnostic("AssemblyResolve error: " + ex.Message);
                return null;
            }
        }

        private string FindTeklaBinPath()
        {
            string[] candidates = {
                @"C:\Program Files\Tekla Structures\2025.0\bin",
                @"C:\Program Files (x86)\Tekla Structures\2025.0\bin",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tekla Structures", "2025.0", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tekla Structures", "2025.0", "bin"),
            };

            foreach (string dir in candidates)
            {
                if (File.Exists(Path.Combine(dir, "Trimble.Remoting.dll")))
                {
                    LogDiagnostic("Tekla bin encontrado: " + dir);
                    return dir;
                }
            }

            LogDiagnostic("Tekla bin NO encontrado.");
            return null;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsDelegate lpEnumFunc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsDelegate(IntPtr hWnd, IntPtr lParam);

        private string GetTeklaWindowTitle(uint processId)
        {
            // Solo la ventana principal: evita que los cambios de titulo de los
            // dibujos (ventanas hijas/secundarias) disparen una alerta falsa.
            try
            {
                using (var proc = Process.GetProcessById((int)processId))
                {
                    IntPtr hWnd = proc.MainWindowHandle;
                    if (hWnd == IntPtr.Zero)
                        return "";

                    int length = GetWindowTextLength(hWnd);
                    if (length == 0)
                        return "";

                    var sb = new StringBuilder(length + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    return sb.ToString();
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        private void OnTick()
        {
            _tickCount++;

            if (_connected)
            {
                RefreshTeklaPids();

                if (_teklaPids.Count == 0)
                {
                    LogDiagnostic("Tekla murio. Desconectando...");
                    _connected = false;
                    _eventsRegistered = false;
                    _connecting = false;
                    _modelConnectionAttempted = false;
                    _alertedPids.Clear();
                    _lastAlertedModelByPid.Clear();
                    _lastMarkerLoadIdByPid.Clear();
                    _lastAlertTimeByPid.Clear();
                    _suppressAlertsUntil.Clear();
                    _postSaveSuppressUntil.Clear();
                    _missingTitleSince.Clear();
                    _dismissedModels.Clear();
                    _lastSharedStateByPid.Clear();
                    UninstallWinEventHook();
                    return;
                }

                ProcessBridgeMarkers();

                if (!_eventsRegistered && !_modelConnectionAttempted && _tickCount % 60 == 0)
                {
                    LogDiagnostic("Reintentando RegisterEvents...");
                    RegisterEvents();
                }

                if (_forceDetect)
                {
                    _forceDetect = false;
                    DetectModelByWindowTitle();
                }

                if (_loadingDetected)
                {
                    _loadingDetected = false;
                    _titleChanged = false;
                    DetectModelByWindowTitle();
                    return;
                }

                if (_titleChanged)
                {
                    _titleChanged = false;
                }

                DetectModelByWindowTitle();

                if (_tickCount % 120 == 0)
                    LogDiagnostic("Conectado. Tick #" + _tickCount);
                return;
            }

            if (_connecting)
                return;

            if (_modelConnectionAttempted)
            {
                RefreshTeklaPids();
                ProcessBridgeMarkers();
                DetectModelByWindowTitle();
                return;
            }

            RefreshTeklaPids();
            if (_teklaPids.Count == 0)
                return;

            LogDiagnostic("Tick #" + _tickCount + ": Proceso encontrado. Intentando conexion en background...");
            _connecting = true;
            _modelConnectionAttempted = true;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var model = new Model();
                    LogDiagnostic("Model() creado OK.");

                    _uiSync.Invoke(new Action(() =>
                    {
                        RegisterEvents();
                        // La conexion exitosa tambien debe liberar el flag: si no,
                        // tras un OnTeklaExit el OnTick queda en `if (_connecting) return;`.
                        _connecting = false;
                        _connected = true;
                        _alertedPids.Clear();
                        _lastAlertedModelByPid.Clear();
                        _lastMarkerLoadIdByPid.Clear();
                        _lastAlertTimeByPid.Clear();
                        _suppressAlertsUntil.Clear();
                        _postSaveSuppressUntil.Clear();
                        _missingTitleSince.Clear();
                        _dismissedModels.Clear();
                        _lastSharedStateByPid.Clear();
                        _loadingDetected = false;
                        InstallWinEventHook();
                        LogDiagnostic("Conexion establecida. Monitoreando ventana...");
                    }));
                }
                catch (Exception ex)
                {
                    LogDiagnostic("EXCEPCION conexion: " + ex.GetType().Name + " - " + ex.Message);
                    if (ex.InnerException != null)
                        LogDiagnostic("Inner: " + ex.InnerException.GetType().Name + " - " + ex.InnerException.Message);
                    LogDiagnostic("Conexion API fallo. Continuando solo con monitoreo de ventana.");
                    _connecting = false;
                }
            });
        }

        private void RefreshTeklaPids()
        {
            if (_tickCount - _pidRefreshTick < 10) return;
            _pidRefreshTick = _tickCount;
            var pids = new HashSet<uint>();
            try
            {
                foreach (var proc in Process.GetProcessesByName("TeklaStructures"))
                {
                    pids.Add((uint)proc.Id);
                    proc.Dispose();
                }
            }
            catch (Exception) { }
            _teklaPids = pids;
        }

        private void RegisterEvents()
        {
            try
            {
                var events = new Tekla.Structures.Model.Events();
                events.ModelLoad += OnModelLoad;
                events.TeklaStructuresExit += OnTeklaExit;
                events.Register();

                lock (_heldEvents)
                {
                    _heldEvents.Add(events);
                }

                _eventsRegistered = true;
                LogDiagnostic("Events registrados OK. Held=" + _heldEvents.Count);
            }
            catch (Exception ex)
            {
                LogDiagnostic("Events fallo (no critico): " + ex.Message);
            }
        }

        private void DetectModelByWindowTitle()
        {
            try
            {
                if (_teklaPids.Count == 0) return;

                // Un marcador del bridge manda: no competir con el titulo.
                if (DateTime.Now < _titleDetectionPausedUntil) return;

                var current = new Dictionary<uint, string>();

                foreach (uint pid in _teklaPids)
                {
                    try
                    {
                        string title = GetTeklaWindowTitle(pid);
                        if (string.IsNullOrEmpty(title)) continue;
                        if (title.IndexOf("Tekla", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (title.IndexOf("(Loading)", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                        string model = ExtractModelPath(title);
                        if (string.IsNullOrEmpty(model)) continue;

                        current[pid] = model;
                    }
                    catch (Exception) { }
                }

                // El titulo de Tekla desaparece por fracciones de segundo al
                // guardar, numerar o mover ventanas. No damos la sesion por
                // cerrada hasta que el titulo falte de forma continua.
                DateTime now = DateTime.Now;
                foreach (uint pid in current.Keys)
                {
                    if (_missingTitleSince.ContainsKey(pid))
                        _missingTitleSince.Remove(pid);
                }

                var gone = new List<uint>();
                foreach (uint pid in _alertedPids)
                {
                    if (current.ContainsKey(pid))
                        continue;

                    DateTime since;
                    if (!_missingTitleSince.TryGetValue(pid, out since))
                    {
                        _missingTitleSince[pid] = now;
                        continue;
                    }
                    if ((now - since).TotalMilliseconds < MissingTitleGraceMs)
                        continue;

                    gone.Add(pid);
                }

                foreach (uint pid in gone)
                {
                    LogDiagnostic("Sesion sin modelo o cerrada pid=" + pid);
                    _alertedPids.Remove(pid);
                    _lastAlertedModelByPid.Remove(pid);
                    _lastMarkerLoadIdByPid.Remove(pid);
                    _lastAlertTimeByPid.Remove(pid);
                    _suppressAlertsUntil.Remove(pid);
                    _lastSharedStateByPid.Remove(pid);
                    _missingTitleSince.Remove(pid);
                }

                bool anyNew = false;
                uint targetPid = 0;
                string targetModel = null;

                foreach (var kv in current)
                {
                    string lastModel;
                    bool alreadyAlerted = _alertedPids.Contains(kv.Key)
                        && _lastAlertedModelByPid.TryGetValue(kv.Key, out lastModel)
                        && lastModel == kv.Value;

                    if (alreadyAlerted)
                        continue;

                    _alertedPids.Add(kv.Key);
                    _lastAlertedModelByPid[kv.Key] = kv.Value;
                    _lastAlertTimeByPid[kv.Key] = DateTime.Now;
                    targetPid = kv.Key;
                    targetModel = kv.Value;
                    anyNew = true;
                }

                if (anyNew)
                {
                    LogDiagnostic("Sesiones actuales: [" + string.Join(" | ", current.Select(kv => kv.Key + "=" + kv.Value)) + "]");
                    if (IsDismissed(targetPid, targetModel) || IsSuppressed(targetPid))
                    {
                        LogDiagnostic("Alerta por titulo ignorada (dismiss/suppress) pid=" + targetPid);
                        return;
                    }
                    ShowReminder(targetPid, targetModel);
                }
            }
            catch (Exception ex)
            {
                if (_tickCount % 60 == 0)
                    LogDiagnostic("DetectModel error: " + ex.Message);
            }
        }

        private static string ExtractModelPath(string title)
        {
            if (string.IsNullOrEmpty(title))
                return "";

            if (title.IndexOf("Tekla", StringComparison.OrdinalIgnoreCase) < 0)
                return "";

            int dashIdx = title.IndexOf(" - ");
            if (dashIdx < 0)
                return "";

            string afterDash = title.Substring(dashIdx + 3).Trim();

            int suffixIdx = afterDash.IndexOf("  - ", StringComparison.Ordinal);
            if (suffixIdx > 0)
            {
                afterDash = afterDash.Substring(0, suffixIdx);
            }
            else
            {
                int viewIdx = afterDash.IndexOf(" - View", StringComparison.OrdinalIgnoreCase);
                if (viewIdx > 0)
                    afterDash = afterDash.Substring(0, viewIdx);
            }

            afterDash = afterDash.Replace("(Loading)", "").Trim().TrimEnd(' ');

            if (!LooksLikeModelPath(afterDash))
                return "";

            return afterDash;
        }

        private static bool LooksLikeModelPath(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 3)
                return false;

            // Las rutas de modelo de Tekla siempre son absolutas (unidad o UNC).
            // Esto descarta titulos de dibujo y ventanas secundarias sin ruta.
            if (value.IndexOf(':') > 0)
                return true;
            if (value.StartsWith("\\\\", StringComparison.Ordinal))
                return true;

            return false;
        }

        private void ProcessBridgeMarkers()
        {
            try
            {
                string bridgeDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SaveAlertTS", "bridge");

                if (!Directory.Exists(bridgeDir))
                    return;

                string[] files;
                try
                {
                    files = Directory.GetFiles(bridgeDir, "model_*.txt");
                }
                catch
                {
                    return;
                }

                foreach (string file in files)
                {
                    try
                    {
                        string fileName = Path.GetFileName(file);
                        if (!fileName.StartsWith("model_", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string pidText = fileName.Substring("model_".Length);
                        if (pidText.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                            pidText = pidText.Substring(0, pidText.Length - 4);

                        uint pid;
                        if (!uint.TryParse(pidText, out pid))
                        {
                            TryDeleteBridgeFile(file);
                            continue;
                        }

                        if (!_teklaPids.Contains(pid))
                        {
                            LogDiagnostic("Marcador para sesion inexistente, borrado pid=" + pid);
                            TryDeleteBridgeFile(file);
                            continue;
                        }

                        string[] lines = File.ReadAllLines(file);
                        string model = GetBridgeValue(lines, "MODEL");
                        if (string.IsNullOrEmpty(model))
                        {
                            LogDiagnostic("Marcador sin MODEL, re-intentando pid=" + pid);
                            continue;
                        }

                        string loadId = GetBridgeValue(lines, "LOADEDAT");
                        if (string.IsNullOrEmpty(loadId))
                            loadId = model;

                        TryDeleteBridgeFile(file);

                        LogDiagnostic("Marcador de modelo pid=" + pid + " model=[" + model + "] loadId=[" + loadId + "]");

                        // Fase 4: transicion multiusuario -> usuario unico. Suprime la
                        // alerta mientras Tekla termina la conversion.
                        string sharedText = GetBridgeValue(lines, "SHARED");
                        bool shared;
                        bool prevShared;
                        if (bool.TryParse(sharedText, out shared)
                            && _lastSharedStateByPid.TryGetValue(pid, out prevShared)
                            && prevShared && !shared)
                        {
                            _suppressAlertsUntil[pid] = DateTime.Now.AddMilliseconds(ConversionSuppressMs);
                            LogDiagnostic("Transicion multiusuario -> usuario unico pid=" + pid
                                + ", suprimiendo alerta " + ConversionSuppressMs + "ms.");
                            _lastSharedStateByPid[pid] = shared;
                            continue;
                        }
                        if (bool.TryParse(sharedText, out shared))
                            _lastSharedStateByPid[pid] = shared;

                        string lastLoadId;
                        if (_lastMarkerLoadIdByPid.TryGetValue(pid, out lastLoadId)
                            && string.Equals(lastLoadId, loadId, StringComparison.OrdinalIgnoreCase))
                        {
                            LogDiagnostic("Marcador duplicado (misma carga) pid=" + pid + ", ignorando.");
                            continue;
                        }

                        string lastModelBridge;
                        DateTime lastTimeBridge;
                        bool isDuplicate =
                            _lastAlertedModelByPid.TryGetValue(pid, out lastModelBridge)
                            && string.Equals(lastModelBridge, model, StringComparison.OrdinalIgnoreCase)
                            && _lastAlertTimeByPid.TryGetValue(pid, out lastTimeBridge)
                            && (DateTime.Now - lastTimeBridge).TotalSeconds < 3;

                        _lastMarkerLoadIdByPid[pid] = loadId;

                        if (isDuplicate)
                        {
                            LogDiagnostic("Marcador repetido en ventana corta pid=" + pid + " model=[" + model + "], ignorando.");
                            continue;
                        }

                        // Un marcador nuevo (otra carga real del modelo) reinicia el
                        // dismiss. Guardar o cerrar dibujos no generan marcador nuevo,
                        // por lo que no lo reinician.
                        _dismissedModels.Remove(ModelKey(pid, model));

                        if (IsSuppressed(pid))
                        {
                            LogDiagnostic("Alerta ignorada (suppress) pid=" + pid + " model=[" + model + "]");
                            continue;
                        }

                        _alertedPids.Add(pid);
                        _lastAlertedModelByPid[pid] = model;
                        _lastAlertTimeByPid[pid] = DateTime.Now;

                        // El marcador del bridge es la fuente determinista: pausamos la
                        // deteccion por titulo para que no dispare una segunda alerta.
                        _titleDetectionPausedUntil = DateTime.Now.AddMilliseconds(MarkerPriorityWindowMs);

                        LogDiagnostic("Nueva carga detectada (marcador) pid=" + pid + " -> alerta.");
                        ShowReminder(pid, model);
                    }
                    catch (Exception ex)
                    {
                        LogDiagnostic("ProcessBridgeMarkers error: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic("ProcessBridgeMarkers: " + ex.Message);
            }
        }

        private static string GetBridgeValue(string[] lines, string key)
        {
            string prefix = key + "=";
            foreach (string line in lines)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(prefix.Length).Trim();
            }
            return null;
        }

        private static void TryDeleteBridgeFile(string file)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch
            {
            }
        }

        private void OnModelLoad()
        {
            LogDiagnostic(">>> OnModelLoad disparado! <<<");

            try
            {
                if (_uiSync.InvokeRequired)
                {
                    _uiSync.Invoke(new Action(() => { _forceDetect = true; }));
                }
                else
                {
                    _forceDetect = true;
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic("OnModelLoad error: " + ex.Message);
            }
        }

        private void OnTeklaExit()
        {
            LogDiagnostic(">>> OnTeklaExit disparado! <<<");

            try
            {
                Action reset = () =>
                {
                    _connected = false;
                    // Sin estos reseteos el OnTick queda atrapado en
                    // `if (_connecting) return;` y nunca se reconecta al
                    // siguiente proceso de Tekla que se abra.
                    _connecting = false;
                    _modelConnectionAttempted = false;
                    _eventsRegistered = false;
                    _alertedPids.Clear();
                    _lastAlertedModelByPid.Clear();
                    _lastMarkerLoadIdByPid.Clear();
                    _lastAlertTimeByPid.Clear();
                    _suppressAlertsUntil.Clear();
                    _postSaveSuppressUntil.Clear();
                    _missingTitleSince.Clear();
                    _dismissedModels.Clear();
                    _lastSharedStateByPid.Clear();
                    UninstallWinEventHook();
                };

                if (_uiSync.InvokeRequired)
                    _uiSync.Invoke(reset);
                else
                    reset();
            }
            catch (Exception ex)
            {
                LogDiagnostic("OnTeklaExit error: " + ex.Message);
            }
        }

        private static string ModelKey(uint pid, string model)
        {
            return pid + "|" + (model ?? "").Trim().ToLowerInvariant();
        }

        private bool IsSuppressed(uint pid)
        {
            DateTime until;
            if (_suppressAlertsUntil.TryGetValue(pid, out until) && DateTime.Now < until)
                return true;
            if (_postSaveSuppressUntil.TryGetValue(pid, out until) && DateTime.Now < until)
                return true;
            return false;
        }

        private bool IsDismissed(uint pid, string model)
        {
            return _dismissedModels.Contains(ModelKey(pid, model));
        }

        // Llamado desde el FormClosed cuando el bridge acepto el pedido de guardado.
        // Con MODE=dialog el guardado real lo confirma el usuario en el cuadro nativo;
        // aqui solo se arma la supresion para evitar alertas tardias.
        public void ModelSaved(uint pid, string model, string saveMode = null)
        {
            try
            {
                _postSaveSuppressUntil[pid] = DateTime.Now.AddMilliseconds(PostSaveSuppressMs);
                if (string.Equals(saveMode, "dialog", StringComparison.OrdinalIgnoreCase))
                    LogDiagnostic("Cuadro nativo iniciado (guardado manual pendiente) pid=" + pid + " model=[" + model
                        + "], suprimiendo alertas " + PostSaveSuppressMs + "ms.");
                else
                    LogDiagnostic("Guardado exitoso pid=" + pid + " model=[" + model
                        + "], suprimiendo alertas " + PostSaveSuppressMs + "ms.");
            }
            catch (Exception ex)
            {
                LogDiagnostic("ModelSaved error: " + ex.Message);
            }
        }

        // Llamado desde el FormClosed de la alerta cuando el usuario la cierra con la X.
        public void DismissModel(uint pid, string model)
        {
            try
            {
                _dismissedModels.Add(ModelKey(pid, model));
                _suppressAlertsUntil[pid] = DateTime.Now.AddMilliseconds(DismissSuppressMs);
                LogDiagnostic("Alerta descartada con X pid=" + pid + " model=[" + model + "]");
            }
            catch (Exception ex)
            {
                LogDiagnostic("DismissModel error: " + ex.Message);
            }
        }

        private void ShowReminder(uint pid, string model)
        {
            LogDiagnostic("ShowReminder() pid=" + pid + " model=[" + model + "]");

            try
            {
                if (IsDismissed(pid, model) || IsSuppressed(pid))
                {
                    LogDiagnostic("ShowReminder omitido (dismiss/suppress) pid=" + pid);
                    return;
                }

                if (_activeAlert != null && !_activeAlert.IsDisposed)
                {
                    _activeAlert.SetTarget(pid, model);
                    _activeAlert.Activate();
                    _activeAlert.BringToFront();
                    LogDiagnostic("Alerta ya visible. Retargeted a pid=" + pid);
                    return;
                }

                _activeAlert = new frmSaveAlert();
                _activeAlert.SetTarget(pid, model);
                _activeAlert.StartPosition = FormStartPosition.CenterScreen;
                _activeAlert.TopMost = true;
                _activeAlert.ShowInTaskbar = true;
                _activeAlert.FormClosed += (s, e) =>
                {
                    var form = s as frmSaveAlert;
                    if (form != null)
                    {
                        if (form.SavedSuccessfully)
                            ModelSaved(form.TargetPid, form.TargetModelPath, form.SaveResponseMode);
                        else
                            // Cierre con la X: no volver a mostrar para este modelo cargado.
                            DismissModel(form.TargetPid, form.TargetModelPath);
                    }
                    _activeAlert = null;
                };
                _activeAlert.Show();
                _activeAlert.BringToFront();

                LogDiagnostic("Alerta mostrada OK pid=" + pid);
            }
            catch (Exception ex)
            {
                LogDiagnostic("ShowReminder EXCEPCION: " + ex.Message);
            }
        }

        private static void ForceForeground(IntPtr handle)
        {
            try
            {
                uint foregroundThreadId = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
                uint thisThreadId = GetCurrentThreadId();

                if (foregroundThreadId != thisThreadId)
                    AttachThreadInput(foregroundThreadId, thisThreadId, true);

                SetForegroundWindow(handle);

                if (foregroundThreadId != thisThreadId)
                    AttachThreadInput(foregroundThreadId, thisThreadId, false);
            }
            catch (Exception) { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        private void InstallWinEventHook()
        {
            UninstallWinEventHook();
            _winEventHookCallback = OnWinEventCallback;
            _winEventHook = SetWinEventHook(
                EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero, _winEventHookCallback,
                0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            LogDiagnostic("WinEvent hook instalado: " + (_winEventHook != IntPtr.Zero));
        }

        private void UninstallWinEventHook()
        {
            if (_winEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }
        }

        private WinEventDelegate _winEventHookCallback;

        private volatile IntPtr _pendingWinEventHwnd;

        private void OnWinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                var sb = new StringBuilder(512);
                GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString();

                if (title.Length == 0 || title.IndexOf("Tekla", StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                // Ignorar ventanas secundarias (dibujos, cuadros sin titulo).
                uint evtPid;
                GetWindowThreadProcessId(hwnd, out evtPid);
                if (!_teklaPids.Contains(evtPid))
                    return;

                try
                {
                    using (var proc = Process.GetProcessById((int)evtPid))
                    {
                        if (proc.MainWindowHandle != hwnd)
                            return;
                    }
                }
                catch (Exception) { return; }

                bool loading = title.IndexOf("(Loading)", StringComparison.OrdinalIgnoreCase) >= 0;
                DateTime now = DateTime.Now;

                // Los movimientos rapidos de vistas cambian el titulo varias veces
                // por segundo: aplicamos debounce salvo durante la carga del modelo.
                if (!loading && (now - _lastTitleChangeTime).TotalMilliseconds < TitleDebounceMs)
                    return;

                _lastTitleChangeTime = now;
                _pendingWinEventHwnd = hwnd;
                _titleChanged = true;

                if (loading)
                    _loadingDetected = true;
            }
            catch (Exception) { }
        }

        private void ExitApp()
        {
            LogDiagnostic("=== TeklaWatcher CERRADO ===");
            UninstallWinEventHook();
            _pollTimer.Stop();
            _heldEvents.Clear();

            if (_activeAlert != null && !_activeAlert.IsDisposed)
            {
                _activeAlert.Close();
                _activeAlert.Dispose();
            }

            _trayIcon.Visible = false;
            _uiSync.Dispose();
            Application.Exit();
        }

        private static void LogDiagnostic(string message)
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TeklaSaveWatcher_debug.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " - " + message + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }
}

