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
        private volatile bool _forceDetect;
        private volatile bool _connecting;
        private volatile bool _modelConnectionAttempted;
        private HashSet<uint> _teklaPids = new HashSet<uint>();
        private int _pidRefreshTick;


        public TeklaWatcher()
        {
            LogDiagnostic("=== TeklaWatcher INICIADO ===");
            TeklaSaveService.CleanupStale();

            AppDomain.CurrentDomain.AssemblyResolve += HandleAssemblyResolve;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Salir", null, (s, e) => ExitApp());

            _trayIcon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                Text = "Tekla Save Watcher",
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
            string bestTitle = "";
            int bestLength = 0;

            EnumWindows((hWnd, lParam) =>
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);

                if (pid != processId)
                    return true;

                int length = GetWindowTextLength(hWnd);
                if (length > bestLength)
                {
                    var sb = new StringBuilder(length + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    bestTitle = sb.ToString();
                    bestLength = length;
                }

                return true;
            }, IntPtr.Zero);

            return bestTitle;
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
                        _connected = true;
                        _alertedPids.Clear();
                        _lastAlertedModelByPid.Clear();
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

                var gone = new List<uint>();
                foreach (uint pid in _alertedPids)
                    if (!current.ContainsKey(pid)) gone.Add(pid);
                foreach (uint pid in gone)
                {
                    LogDiagnostic("Sesion sin modelo o cerrada pid=" + pid);
                    _alertedPids.Remove(pid);
                    _lastAlertedModelByPid.Remove(pid);
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
                    targetPid = kv.Key;
                    targetModel = kv.Value;
                    anyNew = true;
                }

                if (anyNew)
                {
                    LogDiagnostic("Sesiones actuales: [" + string.Join(" | ", current.Select(kv => kv.Key + "=" + kv.Value)) + "]");
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

            int viewIdx = afterDash.IndexOf(" - View", StringComparison.OrdinalIgnoreCase);
            if (viewIdx > 0)
                afterDash = afterDash.Substring(0, viewIdx);

            afterDash = afterDash.Replace("(Loading)", "").Trim().TrimEnd(' ');

            return afterDash;
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

                        TryDeleteBridgeFile(file);

                        LogDiagnostic("Marcador de modelo pid=" + pid + " model=[" + model + "]");
                        _alertedPids.Add(pid);
                        _lastAlertedModelByPid[pid] = model;
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
                if (_uiSync.InvokeRequired)
                {
                    _uiSync.Invoke(new Action(() =>
                    {
                    _connected = false;
                    _alertedPids.Clear();
                    _lastAlertedModelByPid.Clear();

                    }));
                }
                else
                {
                    _connected = false;
                    _alertedPids.Clear();
                    _lastAlertedModelByPid.Clear();

                }
            }
            catch (Exception ex)
            {
                LogDiagnostic("OnTeklaExit error: " + ex.Message);
            }
        }

        private void ShowReminder(uint pid, string model)
        {
            LogDiagnostic("ShowReminder() pid=" + pid + " model=[" + model + "]");

            try
            {
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
                _activeAlert.FormClosed += (s, e) => _activeAlert = null;
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

                if (title.Length > 0 && title.IndexOf("Tekla", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _pendingWinEventHwnd = hwnd;
                    _titleChanged = true;
                    if (title.IndexOf("(Loading)", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _pendingWinEventHwnd = hwnd;
                        _loadingDetected = true;
                    }
                }
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
