using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private readonly HashSet<string> _alertedModels = new HashSet<string>();
        private readonly HashSet<string> _closedModels = new HashSet<string>();
        private readonly Dictionary<string, string> _lastTitles = new Dictionary<string, string>();
        private volatile bool _connecting;
        private volatile bool _modelConnectionAttempted;
        private HashSet<uint> _teklaPids = new HashSet<uint>();
        private int _pidRefreshTick;


        public TeklaWatcher()
        {
            LogDiagnostic("=== TeklaWatcher INICIADO ===");

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
                    _alertedModels.Clear();
                    _closedModels.Clear();
                    _lastTitles.Clear();
                    UninstallWinEventHook();
                    return;
                }

                if (!_eventsRegistered && !_modelConnectionAttempted && _tickCount % 60 == 0)
                {
                    LogDiagnostic("Reintentando RegisterEvents...");
                    RegisterEvents();
                }

                if (_loadingDetected)
                {
                    _loadingDetected = false;
                    _titleChanged = false;
                    IntPtr hwnd = _pendingWinEventHwnd;
                    if (hwnd != IntPtr.Zero && IsHwndOwnedByTekla(hwnd))
                    {
                        var sb = new StringBuilder(512);
                        GetWindowText(hwnd, sb, sb.Capacity);
                        string title = sb.ToString();
                        string model = ExtractModelPath(title);
                        LogDiagnostic("Loading confirmado para: [" + model + "]");
                        if (!string.IsNullOrEmpty(model))
                            _alertedModels.Remove(model);
                    }
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
                return;

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
                        _alertedModels.Clear();
                        _closedModels.Clear();
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

        private bool IsHwndOwnedByTekla(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return _teklaPids.Contains(pid);
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

                var currentModels = new HashSet<string>();
                var currentTitles = new Dictionary<string, string>();

                foreach (uint pid in _teklaPids)
                {
                    try
                    {
                        string title = GetTeklaWindowTitle(pid);
                        if (string.IsNullOrEmpty(title)) continue;
                        if (title.IndexOf("Tekla", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        string model = ExtractModelPath(title);
                        if (!string.IsNullOrEmpty(model))
                        {
                            currentModels.Add(model);
                            currentTitles[model] = title;
                        }
                    }
                    catch (Exception) { }
                }

                foreach (string m in _alertedModels)
                {
                    if (!currentModels.Contains(m))
                    {
                        _closedModels.Add(m);
                        LogDiagnostic("Modelo CERRADO: " + m);
                    }
                }

                bool foundReload = false;
                var toRemoveFromClosed = new List<string>();
                foreach (string m in _closedModels)
                {
                    if (currentModels.Contains(m))
                    {
                        LogDiagnostic("Modelo REABIERTO: " + m);
                        _alertedModels.Remove(m);
                        toRemoveFromClosed.Add(m);
                        foundReload = true;
                    }
                }
                foreach (string m in toRemoveFromClosed)
                    _closedModels.Remove(m);

                bool foundNew = false;
                foreach (string m in currentModels)
                {
                    if (!_alertedModels.Contains(m))
                    {
                        LogDiagnostic("Modelo NUEVO: " + m);
                        foundNew = true;
                    }
                }

                if (foundNew || foundReload)
                {
                    LogDiagnostic("Sesiones: [" + string.Join("|", currentModels) + "]");
                    foreach (string m in currentModels)
                        LogDiagnostic("Titulo RAW: [" + currentTitles[m] + "]");

                    foreach (string m in currentModels)
                        _alertedModels.Add(m);

                    _lastTitles.Clear();
                    foreach (var kv in currentTitles)
                        _lastTitles[kv.Key] = kv.Value;

                    LogDiagnostic("Mostrando alerta...");
                    ShowReminder();
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

        private void OnModelLoad()
        {
            LogDiagnostic(">>> OnModelLoad disparado! <<<");

            try
            {
                if (_uiSync.InvokeRequired)
                {
                    _uiSync.Invoke(new Action(ShowReminder));
                }
                else
                {
                    ShowReminder();
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
                    _alertedModels.Clear();

                    }));
                }
                else
                {
                    _connected = false;
                    _alertedModels.Clear();

                }
            }
            catch (Exception ex)
            {
                LogDiagnostic("OnTeklaExit error: " + ex.Message);
            }
        }

        private void ShowReminder()
        {
            LogDiagnostic("ShowReminder() llamado.");

            try
            {
                if (_activeAlert != null && !_activeAlert.IsDisposed)
                {
                    _activeAlert.Activate();
                    _activeAlert.BringToFront();
                    LogDiagnostic("Alerta ya visible. Activada.");
                    return;
                }

                _activeAlert = new frmSaveAlert
                {
                    StartPosition = FormStartPosition.CenterScreen,
                    TopMost = true,
                    ShowInTaskbar = true
                };
                _activeAlert.FormClosed += (s, e) => _activeAlert = null;
                _activeAlert.Show();
                _activeAlert.BringToFront();

                LogDiagnostic("Alerta mostrada OK.");
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
