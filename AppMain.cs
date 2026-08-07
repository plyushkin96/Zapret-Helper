using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using IOPath = System.IO.Path;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ZapretHelper
{
    public class AddrItem
    {
        public string IP { get; set; }
        public string IPPort { get; set; }
        public string Host { get; set; }
        public string Primary { get; set; }
        public string Secondary { get; set; }
        public string Proto { get; set; }
        public bool Child { get; set; }
    }

    internal class TcpRow
    {
        public string RemoteAddr;
        public int RemotePort;
        public int Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    public class UiLogic
    {
        private readonly Window _win;
        private readonly WebView2 _web;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        // state
        private string _selectedExe = "";
        private string _selectedName = "";
        private readonly Dictionary<int, bool> _watchPids = new Dictionary<int, bool>();
        private readonly HashSet<int> _discoveredPids = new HashSet<int>();
        private readonly HashSet<int> _childPids = new HashSet<int>();
        private readonly Dictionary<string, string> _hostCache = new Dictionary<string, string>();
        private readonly HashSet<string> _knownIps = new HashSet<string>();
        private readonly Dictionary<string, bool> _seenKeys = new Dictionary<string, bool>();
        private readonly HashSet<string> _appIps = new HashSet<string>();
        private readonly Dictionary<string, string> _appDomains = new Dictionary<string, string>();
        private string _appRoot = "";
        private readonly List<AddrItem> _items = new List<AddrItem>();
        private readonly Queue<string> _ptrQueue = new Queue<string>();
        private readonly HashSet<string> _ptrSeen = new HashSet<string>();
        private int _ptrRunning;
        private Dictionary<string, string> _dnsMap = new Dictionary<string, string>();
        private bool _dirty;
        private int _tick;

        private readonly DispatcherTimer _timer;
        private readonly bool _debug;
        private readonly string _dbgPath;

        public Action OnPing;

        public UiLogic(Window win, WebView2 web, bool debug)
        {
            _win = win;
            _web = web;
            _debug = debug;
            _dbgPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
            _web.WebMessageReceived += OnMessage;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (s, e) => Poll();
            _timer.Start();

            win.Closed += (s, e) => _timer.Stop();
        }

        private void Dbg(string msg)
        {
            if (!_debug) return;
            try { File.AppendAllText(_dbgPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n"); } catch { }
        }

        public void OnPageReady()
        {
            Dbg("onpageready: exe=" + _selectedExe + " items=" + _items.Count);
            bool ok = IsDnsHealthy();
            Push(new { type = "dns-status", ok = ok });
            if (!string.IsNullOrEmpty(_selectedExe))
            {
                string text = _watchPids.Count > 0
                    ? "Следим за: " + _selectedName + "  (PID " + string.Join(", ", _watchPids.Keys) + ")"
                    : "Ждём запуск: " + _selectedName;
                string color = _watchPids.Count > 0 ? "green" : "blue";
                Push(new { type = "status", text = text, exe = _selectedExe, color = color });
                if (_items.Count > 0)
                {
                    _dirty = true;
                    RebuildList();
                }
            }
        }

        public void SelectExe(string path)
        {
            SetExe(path);
        }

        public void AutoPick()
        {
            PickExe();
        }

        public string DumpItems()
        {
            var sb = new StringBuilder();
            foreach (var it in _items)
            {
                sb.Append((it.Child ? "C " : "P "));
                sb.Append(string.IsNullOrEmpty(it.Host) ? it.IPPort : it.Host);
                sb.Append("  ");
                sb.Append(it.IPPort);
                sb.Append("  ");
                sb.Append(it.Proto);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ----------------------------------------------------------- JS bridge
        private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                Action act = delegate { HandleMessage(json); };
                if (_win.Dispatcher.CheckAccess()) act();
                else _win.Dispatcher.Invoke(act);
            }
            catch { }
        }

        private void HandleMessage(string json)
        {
            var m = _json.DeserializeObject(json) as Dictionary<string, object>;
            if (m == null) return;
            object typeObj;
            string type = m.TryGetValue("type", out typeObj) ? Convert.ToString(typeObj) : "";
            Dbg("msg: " + type);
            switch (type)
            {
                case "pick": PickExe(); break;
                case "copy": DoCopy(m); break;
                case "clear": ClearList(); break;
                case "min": _win.WindowState = WindowState.Minimized; break;
                case "close": _win.Close(); break;
                case "drag": DoDrag(); break;
                case "smoke":
                    if (OnPing != null) OnPing();
                    break;
                case "fixdns": FixDns(); break;
                case "log":
                    {
                        object txt;
                        if (m.TryGetValue("text", out txt)) Dbg("js: " + Convert.ToString(txt));
                    }
                    break;
            }
        }

        private void DoDrag()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(_win).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    ReleaseCapture();
                    SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                }
            }
            catch (Exception ex)
            {
                Dbg("drag ERROR: " + ex.Message);
            }
        }

        private void Push(object obj)
        {
            try
            {
                if (_web.CoreWebView2 != null)
                {
                    string js = _json.Serialize(obj);
                    Dbg("push: " + (js.Length > 400 ? js.Substring(0, 400) + "..." : js));
                    _web.CoreWebView2.PostWebMessageAsJson(js);
                }
            }
            catch { }
        }

        private void SetStatus(string text, string color)
        {
            Push(new { type = "status", text = text, exe = _selectedExe, color = color });
        }

        private void PushFoot(string text)
        {
            Push(new { type = "foot", text = text });
        }

        private void DoCopy(Dictionary<string, object> m)
        {
            object itemsObj;
            if (!m.TryGetValue("items", out itemsObj)) return;
            var arr = itemsObj as object[];
            if (arr == null || arr.Length == 0)
            {
                PushFoot("Список пуст - нечего копировать.");
                return;
            }
            var lines = arr
                .Where(x => x != null)
                .Select(x => Convert.ToString(x))
                .Where(s => s.Length > 0)
                .Distinct()
                .ToList();
            if (lines.Count == 0)
            {
                PushFoot("Список пуст - нечего копировать.");
                return;
            }
            try
            {
                Clipboard.SetText(string.Join("\r\n", lines));
                PushFoot("Скопировано: " + lines.Count + " адрес(ов).");
            }
            catch
            {
                PushFoot("Не удалось записать в буфер обмена.");
            }
        }

        // ------------------------------------------------------------------ UI
        private void PickExe()
        {
            Dbg("PickExe: start");
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Выберите .exe приложения",
                    Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*",
                    CheckFileExists = true
                };
                bool? ok = dlg.ShowDialog(_win);
                Dbg("PickExe: returned " + ok);
                if (ok == true) SetExe(dlg.FileName);
            }
            catch (Exception ex)
            {
                Dbg("PickExe ERROR: " + ex);
            }
        }

        private void SetExe(string exe)
        {
            _selectedExe = exe;
            _selectedName = IOPath.GetFileNameWithoutExtension(exe);
            _appRoot = _selectedName.ToLowerInvariant();
            Dbg("SetExe: " + exe + " root=" + _appRoot);
            _watchPids.Clear();
            _childPids.Clear();
            _hostCache.Clear();
            _knownIps.Clear();
            _ptrQueue.Clear();
            _ptrSeen.Clear();
            _seenKeys.Clear();
            _appIps.Clear();
            _appDomains.Clear();
            _discoveredPids.Clear();
            _items.Clear();
            _dirty = true;
            RebuildList();
            CheckDnsHealth();
            SetStatus("Ждём запуск: " + _selectedName, "blue");
        }

        private bool IsDnsHealthy()
        {
            try
            {
                var q = new EventLogQuery("Microsoft-Windows-DNS-Client/Operational", PathType.LogName,
                    "*[System[EventID=1]]");
                using (var r = new EventLogReader(q)) { r.ReadEvent(); }
                return true;
            }
            catch { return false; }
        }

        private void CheckDnsHealth()
        {
            bool logOk = false;
            try
            {
                var q = new EventLogQuery("Microsoft-Windows-DNS-Client/Operational", PathType.LogName,
                    "*[System[EventID=3008 and TimeCreated[timediff(@SystemTime) <= 1000]]]");
                using (var r = new EventLogReader(q)) { logOk = true; r.ReadEvent(); }
            }
            catch { }
            int edgeBuiltIn = -1;
            try
            {
                object val = Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\Software\\Policies\\Microsoft\\Edge", "BuiltInDnsClientEnabled", -1);
                if (val is int) edgeBuiltIn = (int)val;
            }
            catch { }
            if (!logOk)
                PushFoot("DNS-лог выключен — имена хостов не видны. Кликните сюда, чтобы исправить (UAC).");
            else if (edgeBuiltIn != 0)
                PushFoot("DNS-лог включён. Кликните сюда для усиления (UAC — EdgeDNS=sys).");
            else
                PushFoot("DNS-лог включён, Edge использует системный DNS.");
        }

        public void FixDns()
        {
            PushFoot("Запрашиваю права администратора...");
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string bat = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zapret_fixdns.bat");
                    File.WriteAllText(bat,
                        "@echo off\r\n" +
                        "wevtutil set-log \"Microsoft-Windows-DNS-Client/Operational\" /enabled:true /quiet:true\r\n" +
                        "reg add \"HKLM\\Software\\Policies\\Microsoft\\Edge\" /v BuiltInDnsClientEnabled /t REG_DWORD /d 0 /f >nul 2>nul\r\n" +
                        "del \"%~f0\"\r\n");
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = bat,
                        Verb = "runas",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    if (p != null)
                    {
                        p.WaitForExit();
                        System.Threading.Thread.Sleep(1000);
                    }
                    _win.Dispatcher.Invoke(() =>
                    {
                        bool ok = IsDnsHealthy();
                        Push(new { type = "dns-status", ok = ok });
                        CheckDnsHealth();
                    });
                }
                catch (Exception ex)
                {
                    _win.Dispatcher.Invoke(() => PushFoot("Ошибка: " + ex.Message));
                }
            });
        }

        private void ClearList()
        {
            _seenKeys.Clear();
            _items.Clear();
            _knownIps.Clear();
            _hostCache.Clear();
            _ptrQueue.Clear();
            _ptrSeen.Clear();
            _appDomains.Clear();
            _appIps.Clear();
            _dirty = true;
            RebuildList();
            PushFoot("Список очищен.");
        }

        private static readonly HashSet<string> _noiseHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "substrate.office.com", "dns.google", "8.8.8.8", "8.8.4.4",
        };

        private static readonly string[] _noiseSuffixes = {
            ".1e100.net", ".office.com", ".office.net", ".googleapis.com",
            ".gstatic.com", ".microsoft.com", ".trafficmanager.net",
        };

        private static readonly string[] _noisePrefixes = {
            "clients", "msedge.", "edge.microsoft.",
        };

        private int GetCat(string ip, string host)
        {
            if (!string.IsNullOrEmpty(ip) && _appIps.Contains(ip))
                return 0;
            if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(_appRoot) && host.IndexOf(_appRoot, StringComparison.OrdinalIgnoreCase) >= 0)
                return 0;
            if (ip == "8.8.8.8" || ip == "8.8.4.4")
                return 2;
            if (!string.IsNullOrEmpty(host))
            {
                string h = host.ToLowerInvariant();
                if (_noiseHosts.Contains(h)) return 2;
                foreach (var s in _noiseSuffixes) { if (h.EndsWith(s)) return 2; }
                foreach (var p in _noisePrefixes) { if (h.StartsWith(p)) return 2; }
            }
            return 1;
        }

        private void RebuildList()
        {
            Dbg("rebuild: n=" + _items.Count);
            foreach (var it in _items)
            {
                if (string.IsNullOrEmpty(it.Host))
                {
                    string h;
                    if (_hostCache.TryGetValue(it.IP, out h) && !string.IsNullOrEmpty(h))
                    {
                        it.Host = h;
                        it.Primary = h;
                        it.Secondary = it.IPPort;
                    }
                }
            }
            var all = new List<AddrItem>(_items);
            var tcpIps = new HashSet<string>(all.Select(x => x.IP));
            foreach (var kv in _appDomains)
            {
                if (!tcpIps.Contains(kv.Key))
                {
                    string h = kv.Value;
                    all.Add(new AddrItem { IP = kv.Key, IPPort = kv.Key, Host = h, Primary = h, Secondary = kv.Key, Proto = "dns", Child = false });
                }
            }
            var grouped = all
                .GroupBy(x => string.IsNullOrEmpty(x.Host) ? x.IPPort : x.Host)
                .Select(g =>
                {
                    var first = g.First();
                    var ips = g.Select(x => string.IsNullOrEmpty(x.Host) ? x.IPPort : x.IPPort)
                              .Distinct().OrderBy(s => s).ToList();
                    var proto = g.Any(x => x.Proto == "TCP") ? "TCP" : "dns";
                    return new
                    {
                        p = string.IsNullOrEmpty(first.Host) ? first.IPPort : first.Host,
                        s = ips.Count == 1 ? ips[0] : string.Join("\n", ips),
                        pt = proto,
                        h = first.Host,
                        ipport = first.IPPort,
                        c = first.Child,
                        cat = GetCat(first.IP, first.Host),
                        ips = ips
                    };
                })
                .OrderBy(x => x.cat)
                .ThenBy(x => string.IsNullOrEmpty(x.h) ? 1 : 0)
                .ThenBy(x => x.h)
                .ThenBy(x => x.ipport)
                .ToList();
            Push(new { type = "list", items = grouped });
        }

        // ---------------------------------------------------------------- core
        private List<Process> MatchProcs()
        {
            var res = new List<Process>();
            string name = IOPath.GetFileNameWithoutExtension(_selectedExe);
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id <= 0) continue;
                string fp = null;
                try { fp = p.MainModule.FileName; } catch { }
                if (!string.IsNullOrEmpty(fp))
                {
                    if (string.Equals(fp.Trim(), _selectedExe, StringComparison.OrdinalIgnoreCase)) res.Add(p);
                }
                else
                {
                    res.Add(p);
                }
            }
            Dbg("match: " + name + " -> " + res.Count + " procs");
            return res;
        }

        private void Poll()
        {
            if (string.IsNullOrEmpty(_selectedExe)) return;
            _tick++;

            var dead = new List<int>(_watchPids.Keys);
            foreach (var pid in dead)
            {
                try { var p = Process.GetProcessById(pid); if (p.HasExited) _watchPids.Remove(pid); }
                catch { _watchPids.Remove(pid); }
            }

            foreach (var p in MatchProcs())
            {
                if (!_watchPids.ContainsKey(p.Id)) _watchPids[p.Id] = true;
                _discoveredPids.Add(p.Id);
            }

            if (_watchPids.Count == 0 && _childPids.Count == 0)
            {
                SetStatus("Ждём запуск: " + _selectedName, "blue");
            }
            else
            {
                string txt = "Следим за: " + _selectedName;
                if (_watchPids.Count > 0) txt += "  (PID " + string.Join(", ", _watchPids.Keys) + ")";
                if (_childPids.Count > 0) txt += "  дети: " + _childPids.Count;
                SetStatus(txt, "green");
            }

            _dnsMap = BuildDnsMap();
            var evt = BuildDnsEventMap();
            foreach (var kv in evt) { if (!_dnsMap.ContainsKey(kv.Key)) _dnsMap[kv.Key] = kv.Value; }
            if (!string.IsNullOrEmpty(_appRoot))
            {
                foreach (var kv in _dnsMap)
                {
                    if (kv.Value.IndexOf(_appRoot, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _appIps.Add(kv.Key);
                        _appDomains[kv.Key] = kv.Value;
                    }
                }
            }
            if (_tick % 8 == 0) Dbg("dns: cache=" + _dnsMap.Count + " appIPs=" + _appIps.Count + " appDomains=" + _appDomains.Count);

            if (_dnsMap.Count > 0)
            {
                foreach (var kv in _dnsMap)
                {
                    if (_knownIps.Contains(kv.Key))
                    {
                        string cur;
                        if (!_hostCache.TryGetValue(kv.Key, out cur) || string.IsNullOrEmpty(cur))
                        {
                            _hostCache[kv.Key] = kv.Value;
                            _dirty = true;
                            Dbg("dns-hit: " + kv.Key + " -> " + kv.Value);
                        }
                    }
                }
            }

            bool scanTcp = _watchPids.Count > 0 || _childPids.Count > 0;
            if (_discoveredPids.Count > 0)
            {
                ComputeChildPids();
                Dbg("child-bfs: " + _childPids.Count + " pids");
                var rows = GetTcpRows(2);
                rows.AddRange(GetTcpRows(23));
                Dbg("tcp: " + rows.Count + " rows");
                foreach (var r in rows)
                {
                    bool child;
                    if (_watchPids.ContainsKey(r.Pid)) child = false;
                    else if (_childPids.Contains(r.Pid)) child = true;
                    else continue;
                    string ra = r.RemoteAddr;
                    if (string.IsNullOrEmpty(ra)) continue;
                    if (ra == "0.0.0.0" || ra == "::" || ra == "127.0.0.1" || ra == "::1") continue;
                    if (ra.StartsWith("224.") || ra.StartsWith("239.") || ra.StartsWith("ff")) continue;
                    AddAddress(ra, r.RemotePort, "TCP", child);
                }
                Dbg("poll " + _tick + " watch=" + _watchPids.Count + " child=" + _childPids.Count + " rows=" + rows.Count + " items=" + _items.Count);
            }

            PumpPtr();

            if (_dirty)
            {
                _dirty = false;
                RebuildList();
            }
        }

        private void ComputeChildPids()
        {
            _childPids.Clear();
            if (_discoveredPids.Count == 0) return;

            var parent = new Dictionary<int, int>();
            foreach (var p in Process.GetProcesses())
            {
                int pid = -1, ppid = -1;
                try { pid = p.Id; ppid = GetParentPid(p.Handle); }
                catch { }
                try { p.Dispose(); } catch { }
                if (pid > 0 && ppid > 0) parent[pid] = ppid;
            }

            var found = new HashSet<int>(_discoveredPids);
            var frontier = new HashSet<int>(_discoveredPids);
            while (frontier.Count > 0)
            {
                var next = new HashSet<int>();
                foreach (var kv in parent)
                {
                    if (frontier.Contains(kv.Value) && !found.Contains(kv.Key))
                    {
                        found.Add(kv.Key);
                        next.Add(kv.Key);
                    }
                }
                frontier = next;
            }

            foreach (var pid in found)
            {
                if (!_discoveredPids.Contains(pid)) _childPids.Add(pid);
            }
        }

        private static int GetParentPid(IntPtr handle)
        {
            PROCESS_BASIC_INFORMATION pbi;
            int len;
            if (NtQueryInformationProcess(handle, 0, out pbi, Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out len) != 0)
                return -1;
            return pbi.InheritedFromUniqueProcessId.ToInt32();
        }

        private void AddAddress(string ip, int port, string proto, bool child)
        {
            string ipport = ip + ":" + port;
            string key = ipport + "|" + proto;
            if (_seenKeys.ContainsKey(key)) return;
            _seenKeys[key] = true;
            if (!_knownIps.Contains(ip)) _knownIps.Add(ip);

            string host = "";
            string fromMap;
            if (_dnsMap.TryGetValue(ip, out fromMap) && !string.IsNullOrEmpty(fromMap))
            {
                host = fromMap;
                _hostCache[ip] = host;
            }
            else if (!_hostCache.ContainsKey(ip))
            {
                EnqueuePtr(ip);
            }
            else if (!string.IsNullOrEmpty(_hostCache[ip]))
            {
                host = _hostCache[ip];
            }

            var it = new AddrItem
            {
                IP = ip,
                IPPort = ipport,
                Host = host,
                Primary = string.IsNullOrEmpty(host) ? ipport : host,
                Secondary = string.IsNullOrEmpty(host) ? "" : ipport,
                Proto = proto,
                Child = child
            };
            _items.Add(it);
            _dirty = true;
            Dbg("add: " + ipport + " proto=" + proto + " child=" + child);
        }

        private void EnqueuePtr(string ip)
        {
            if (_hostCache.ContainsKey(ip)) return;
            if (_ptrSeen.Contains(ip)) return;
            _ptrSeen.Add(ip);
            _ptrQueue.Enqueue(ip);
            PumpPtr();
        }

        private void PumpPtr()
        {
            while (_ptrRunning < 3 && _ptrQueue.Count > 0)
            {
                string ip = _ptrQueue.Dequeue();
                _ptrRunning++;
                Task.Run(() =>
                {
                    string h = "";
                    try
                    {
                        var t = Dns.GetHostEntryAsync(ip);
                        if (t.Wait(600)) h = t.Result.HostName;
                    }
                    catch { }
                    string captured = ip;
                    string name = h;
                    _win.Dispatcher.BeginInvoke((Action)(() =>
                    {
                        _ptrRunning--;
                        string cur;
                        if (!_hostCache.TryGetValue(captured, out cur))
                        {
                            _hostCache[captured] = name;
                        }
                        else if (string.IsNullOrEmpty(cur) && !string.IsNullOrEmpty(name))
                        {
                            _hostCache[captured] = name;
                            _dirty = true;
                        }
                        PumpPtr();
                    }));
                });
            }
        }

        // ---------------------------------------------------------------- TCP table
        private List<TcpRow> GetTcpRows(int af)
        {
            var list = new List<TcpRow>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, 5, 0);
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, af, 5, 0) != 0) return list;
                int count = Marshal.ReadInt32(buf);
                IntPtr p = IntPtr.Add(buf, 4);
                int rowSize = af == 2 ? 24 : 56;
                for (int i = 0; i < count; i++)
                {
                    if (af == 2)
                    {
                        uint la = (uint)Marshal.ReadInt32(p + 4);
                        ushort lp = (ushort)Marshal.ReadInt16(p + 8);
                        uint ra = (uint)Marshal.ReadInt32(p + 12);
                        ushort rp = (ushort)Marshal.ReadInt16(p + 16);
                        int pid = Marshal.ReadInt32(p + 20);
                        if (la != 0 || ra != 0)
                        {
                            list.Add(new TcpRow
                            {
                                RemoteAddr = Ip4(ra),
                                RemotePort = Ntohs(rp),
                                Pid = pid
                            });
                        }
                    }
                    else
                    {
                        IntPtr raPtr = p + 28; // state(4) + local addr(16) + scope(4) + port(4)
                        int pid = Marshal.ReadInt32(p + 52);
                        ushort rp = (ushort)Marshal.ReadInt16(p + 48);
                        byte[] b = new byte[16];
                        Marshal.Copy(raPtr, b, 0, 16);
                        string ra = Ip6(b);
                        if (!AllZero(b))
                        {
                            list.Add(new TcpRow { RemoteAddr = ra, RemotePort = Ntohs(rp), Pid = pid });
                        }
                    }
                    p = IntPtr.Add(p, rowSize);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            return list;
        }

        private static string Ip4(uint a)
        {
            return (a & 0xFF) + "." + ((a >> 8) & 0xFF) + "." + ((a >> 16) & 0xFF) + "." + ((a >> 24) & 0xFF);
        }

        private static int Ntohs(ushort v)
        {
            return ((v >> 8) & 0xFF) | ((v & 0xFF) << 8);
        }

        private static bool AllZero(byte[] b)
        {
            foreach (var x in b) if (x != 0) return false;
            return true;
        }

        private static string Ip6(byte[] b)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 8; i++)
            {
                if (i > 0) sb.Append(':');
                sb.Append(b[i * 2].ToString("x2"));
                sb.Append(b[i * 2 + 1].ToString("x2"));
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- DNS cache
        [StructLayout(LayoutKind.Sequential)]
        private struct DnsCacheEntry
        {
            public IntPtr pNext;
            public IntPtr pszName;
            public ushort wType;
            public ushort wDataLength;
            public uint dwFlags;
            public uint dwInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DnsCacheData
        {
            public IntPtr pHead;
        }

        [DllImport("dnsapi.dll", CharSet = CharSet.Ansi)]
        private static extern uint DnsGetCacheDataTable(ref DnsCacheData cacheData);

        private Dictionary<string, string> BuildDnsMap()
        {
            var map = new Dictionary<string, string>();
            try
            {
                var data = new DnsCacheData();
                if (DnsGetCacheDataTable(ref data) == 0)
                {
                    IntPtr p = data.pHead;
                    int dataOffset = Marshal.SizeOf(typeof(DnsCacheEntry));
                    while (p != IntPtr.Zero)
                    {
                        var e = (DnsCacheEntry)Marshal.PtrToStructure(p, typeof(DnsCacheEntry));
                        string name = Marshal.PtrToStringAnsi(e.pszName);
                        if (!string.IsNullOrEmpty(name))
                        {
                            name = name.TrimEnd('.');
                            IntPtr dp = IntPtr.Add(p, dataOffset);
                            if (e.wType == 1 && e.wDataLength >= 4)
                            {
                                byte[] b = new byte[4];
                                Marshal.Copy(dp, b, 0, 4);
                                string ip = b[0] + "." + b[1] + "." + b[2] + "." + b[3];
                                if (!map.ContainsKey(ip)) map[ip] = name;
                            }
                            else if (e.wType == 28 && e.wDataLength >= 16)
                            {
                                byte[] b = new byte[16];
                                Marshal.Copy(dp, b, 0, 16);
                                string ip = Ip6(b);
                                if (!map.ContainsKey(ip)) map[ip] = name;
                            }
                        }
                        p = e.pNext;
                    }
                }
            }
            catch { }
            return map;
        }

        private Dictionary<string, string> BuildDnsEventMap()
        {
            var map = new Dictionary<string, string>();
            try
            {
                var q = new EventLogQuery("Microsoft-Windows-DNS-Client/Operational", PathType.LogName,
                    "*[System[(EventID=3001 or EventID=3002 or EventID=3006 or EventID=3008 or EventID=3020) and TimeCreated[timediff(@SystemTime) <= 180000]]]");
                using (var reader = new EventLogReader(q))
                {
                    EventLogRecord r;
                    while ((r = reader.ReadEvent() as EventLogRecord) != null)
                    {
                        try
                        {
                            var props = r.Properties;
                            if (props.Count < 2) continue;
                            object nv = props[0].Value;
                            string name = nv != null ? nv.ToString() : null;
                            if (string.IsNullOrEmpty(name)) continue;
                            string results = null;
                            for (int i = props.Count - 1; i >= 1; i--)
                            {
                                object rv = props[i].Value;
                                string s = rv != null ? rv.ToString() : null;
                                if (!string.IsNullOrEmpty(s) && (s.IndexOf('.') >= 0 || s.IndexOf(':') >= 0))
                                {
                                    results = s;
                                    break;
                                }
                            }
                            if (string.IsNullOrEmpty(results)) continue;
                            foreach (var ip in results.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                string t = ip.Trim();
                                if (t.StartsWith("::ffff:")) t = t.Substring(7);
                                IPAddress dummy;
                                if (IPAddress.TryParse(t, out dummy))
                                    map[t] = name;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Dbg("dns-evlog ERR: " + ex.Message);
            }
            return map;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, out PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;
    }

    public static class Program
    {
        private static CoreWebView2 _coreRef;

        [STAThread]
        public static void Main(string[] args)
        {
            bool smoke = args != null && args.Length > 0 && args[0] == "-SmokeTest";
            bool test = args != null && args.Length >= 3 && args[0] == "-Test";
            bool debug = args != null && Array.Exists(args, a => a == "-Debug");
            bool autoPick = args != null && args.Length > 0 && args[0] == "-AutoPick";
            bool clickPick = args != null && args.Length > 0 && args[0] == "-ClickPick";
            string testExe = test ? args[1] : "";
            int testSecs = test ? 15 : 0;
            if (test) { int.TryParse(args[2], out testSecs); if (testSecs <= 0) testSecs = 15; }
            string logPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoketest.txt");
            try
            {
                var win = new Window
                {
                    Title = "Zapret Helper",
                    Width = 1080,
                    Height = 680,
                    MinWidth = 880,
                    MinHeight = 540,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.CanResize,
                    Background = new SolidColorBrush(Color.FromRgb(13, 15, 23)),
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 13, 15, 23) };
                win.Content = web;

                var ui = new UiLogic(win, web, debug);
                if (test) ui.SelectExe(testExe);
                if (smoke) File.WriteAllText(logPath, "START\n");

                bool uiError = false;
                web.Loaded += async (s, e) =>
                {
                    try
                    {
                        await web.EnsureCoreWebView2Async(null);
                        var core = web.CoreWebView2;
                        _coreRef = core;
                        core.Settings.AreDefaultContextMenusEnabled = false;
                        core.Settings.IsStatusBarEnabled = false;
                        core.Settings.IsZoomControlEnabled = false;
            core.NavigateToString(LoadHtml());
            core.NavigationCompleted += async (s2, e2) =>
            {
                try
                {
                    await Task.Delay(500);
                    win.Dispatcher.Invoke(() => ui.OnPageReady());
                }
                catch { }
            };
            if (smoke)
                        {
                            File.AppendAllText(logPath, "NAV_OK\n");
                            var pt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
                            pt.Tick += (s2, e2) =>
                            {
                                pt.Stop();
                                try { core.ExecuteScriptAsync("chrome.webview.postMessage({type:'smoke'});"); } catch { }
                            };
                            pt.Start();
                        }
                    }
                    catch (Exception ex)
                    {
                        uiError = true;
                        if (smoke) File.WriteAllText(logPath, "ERROR:\n" + ex);
                        else MessageBox.Show("Не удалось инициализировать WebView2:\n" + ex, "Zapret Helper");
                        win.Close();
                    }
                };

                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

                if (autoPick)
                {
                    var ap = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
                    ap.Tick += (s2, e2) => { ap.Stop(); ui.AutoPick(); };
                    ap.Start();
                }
                else if (clickPick)
                {
                    var cp = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
                    cp.Tick += (s2, e2) => { cp.Stop(); try { if (_coreRef != null) _coreRef.ExecuteScriptAsync("document.getElementById('pickBtn').click();"); } catch { } };
                    cp.Start();
                }

                if (smoke)
                {
                    ui.OnPing = delegate { try { File.AppendAllText(logPath, "BRIDGE_OK\n"); } catch { } };
                    var st = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(6000) };
                    st.Tick += (s2, e2) => { st.Stop(); win.Close(); };
                    st.Start();
                }
                else if (test)
                {
                    var tt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(testSecs * 1000) };
                    tt.Tick += (s2, e2) => { tt.Stop(); win.Close(); };
                    tt.Start();
                }

                app.Run(win);
                if (smoke && !uiError) File.AppendAllText(logPath, "UI_OK\n");
                if (test)
                {
                    File.WriteAllText(IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "testlog.txt"), ui.DumpItems());
                }
            }
            catch (Exception ex)
            {
                if (smoke) File.WriteAllText(logPath, "ERROR:\n" + ex);
                else MessageBox.Show(ex.ToString(), "Zapret Helper");
            }
        }

        private static string LoadHtml()
        {
            string file = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");
            if (File.Exists(file)) return File.ReadAllText(file, Encoding.UTF8);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZapretHelper.index.html"))
            {
                if (s == null) return "<html><body style='background:#0d0f17;color:#e8eaf2;font-family:Segoe UI'>index.html not found</body></html>";
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }
    }
}
