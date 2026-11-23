using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Launcher.Core;
using Launcher.Native;

namespace Launcher.Host
{
    /// <summary>
    /// Watches the remote control's evdev nodes read-only through ecore fd handlers,
    /// using Linux inotify on /dev/input for instant (&lt;80 ms) Bluetooth remote reconnect
    /// detection plus a periodic rescan fallback.
    /// </summary>
    internal sealed class InputWatcher
    {
        private const double RescanSeconds = 1.5;
        public const double HomeDebounceSeconds = 0.55;
        private const double DuplicateEventSeconds = 0.05;
        private const int EAgain = 11;

        private readonly List<int> _fds = new List<int>();
        private readonly List<EvdevReader> _readers = new List<EvdevReader>();
        private readonly List<string> _paths = new List<string>();
        private readonly List<IntPtr> _handlers = new List<IntPtr>();
        private readonly HashSet<string> _openFailed = new HashSet<string>();
        private readonly byte[] _buf = new byte[16 * 32];
        private readonly List<int> _presses = new List<int>();

        private readonly Efl.FdCb _inputCb;
        private readonly Efl.FdCb _inotifyCb;
        private readonly Efl.TaskCb _rescanCb;
        private readonly Efl.TaskCb _quickRescanCb;
        private readonly Action _onHome;
        private readonly Action _onHomeBurst;
        private readonly Action<int> _onNav;

        private int _inotifyFd = -1;
        private IntPtr _inotifyHandler;
        private IntPtr _rescanTimer;
        private IntPtr _quickRescanTimer;
        private bool _listFailed;
        private int _homeCode = -1;
        private bool _learn;
        private double _lastHome = -100;

        public double LastRawHome { get; private set; } = -100;
        public double LastHome { get { return _lastHome; } set { _lastHome = value; } }
        public bool HasOpenNodes { get { return _fds.Exists(fd => fd >= 0); } }

        public void Rescan()
        {
            try { WatchNewNodes(); }
            catch (Exception ex) { Logger.Write("input: rescan: " + ex.Message); }
        }

        public InputWatcher(Action onHome, Action onHomeBurst, Action<int> onNav)
        {
            _onHome = onHome;
            _onHomeBurst = onHomeBurst;
            _onNav = onNav;
            _inputCb = OnInput;
            _inotifyCb = OnInotify;
            _rescanCb = OnRescan;
            _quickRescanCb = OnQuickRescan;
        }

        public void Start(string homeCodeArg, bool learn)
        {
            int code;
            if (homeCodeArg != null && int.TryParse(homeCodeArg, out code) && code > 0 && code < 768) _homeCode = code;
            _learn = learn;
            if (_homeCode < 0 && !_learn) return;

            WatchNewNodes();
            SetupInotify();
            Logger.Write("home key code " + (_homeCode < 0 ? "not set" : _homeCode.ToString()) + (_learn ? ", learn mode (every key press is logged)" : ""));
            _rescanTimer = Efl.ecore_timer_add(RescanSeconds, _rescanCb, IntPtr.Zero);
        }

        private void SetupInotify()
        {
            try
            {
                int fd = Libc.inotify_init1(Libc.IN_NONBLOCK);
                if (fd < 0) return;
                if (Libc.inotify_add_watch(fd, "/dev/input", Libc.IN_CREATE | Libc.IN_ATTRIB) < 0)
                {
                    Libc.close(fd);
                    return;
                }
                _inotifyFd = fd;
                _inotifyHandler = Efl.ecore_main_fd_handler_add(fd, Efl.EcoreFdRead, _inotifyCb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception) { }
        }

        private byte OnInotify(IntPtr data, IntPtr handler)
        {
            try
            {
                while ((long)Libc.read(_inotifyFd, _buf, (UIntPtr)_buf.Length) > 0) { }
                WatchNewNodes();
                if (_quickRescanTimer == IntPtr.Zero)
                    _quickRescanTimer = Efl.ecore_timer_add(0.08, _quickRescanCb, IntPtr.Zero);
            }
            catch (Exception) { }
            return 1;
        }

        private byte OnQuickRescan(IntPtr data)
        {
            _quickRescanTimer = IntPtr.Zero;
            try { WatchNewNodes(); }
            catch (Exception) { }
            return 0;
        }

        private int WatchNewNodes()
        {
            List<string> nodes;
            try { nodes = InputDevices.RemoteNodes(File.ReadAllText("/proc/bus/input/devices")); }
            catch (Exception ex) { if (!_listFailed) Logger.Write("input: cannot list devices: " + ex.Message); _listFailed = true; return 0; }
            _listFailed = false;

            int added = 0;
            foreach (string node in nodes)
            {
                if (_paths.Contains(node)) continue;
                int fd = Libc.open(node, Libc.O_RDONLY | Libc.O_NONBLOCK);
                if (fd < 0)
                {
                    if (_openFailed.Add(node)) Logger.Write("input: cannot open " + node + " errno=" + Marshal.GetLastWin32Error());
                    continue;
                }
                _openFailed.Remove(node);

                int index = _fds.IndexOf(-1);
                bool reuse = index >= 0;
                if (!reuse) index = _fds.Count;
                IntPtr handler = Efl.ecore_main_fd_handler_add(fd, Efl.EcoreFdRead, _inputCb, (IntPtr)index, IntPtr.Zero, IntPtr.Zero);
                if (handler == IntPtr.Zero) { Libc.close(fd); Logger.Write("input: cannot watch " + node); continue; }

                if (reuse) { _fds[index] = fd; _readers[index] = new EvdevReader(); _paths[index] = node; _handlers[index] = handler; }
                else { _fds.Add(fd); _readers.Add(new EvdevReader()); _paths.Add(node); _handlers.Add(handler); }
                added++;
                Logger.Write("input: watching " + node);
            }
            return added;
        }

        private byte OnRescan(IntPtr data)
        {
            try { WatchNewNodes(); }
            catch (Exception ex) { Logger.Write("input: rescan: " + ex.Message); }
            return 1;
        }

        private void DropNode(int index)
        {
            if (_fds[index] < 0) return;
            Logger.Write("input: lost " + _paths[index]);
            if (_handlers[index] != IntPtr.Zero) Efl.ecore_main_fd_handler_del(_handlers[index]);
            Libc.close(_fds[index]);
            _fds[index] = -1;
            _handlers[index] = IntPtr.Zero;
            _paths[index] = null;
        }

        private byte OnInput(IntPtr data, IntPtr handler)
        {
            try
            {
                int index = (int)data;
                if (index < 0 || index >= _fds.Count || _fds[index] < 0) return 1;
                for (int round = 0; round < 8; round++)
                {
                    long n = (long)Libc.read(_fds[index], _buf, (UIntPtr)_buf.Length);
                    if (n < 0 && Marshal.GetLastWin32Error() != EAgain) { DropNode(index); break; }
                    if (n <= 0) break;
                    _presses.Clear();
                    _readers[index].Feed(_buf, (int)n, _presses);
                    for (int k = 0; k < _presses.Count; k++)
                    {
                        int code = _presses[k];
                        if (_learn) Logger.Write("key code " + code);
                        if (code != _homeCode)
                        {
                            _onNav(code);
                            continue;
                        }
                        double now = Efl.ecore_time_get();
                        double dt = now - _lastHome;
                        LastRawHome = now;
                        if (dt < DuplicateEventSeconds) continue;
                        _lastHome = now;
                        if (dt < HomeDebounceSeconds)
                        {
                            if (_onHomeBurst != null) _onHomeBurst();
                            continue;
                        }
                        _onHome();
                    }
                }
            }
            catch (Exception ex) { Logger.Write("input: " + ex); }
            return 1;
        }
    }
}
