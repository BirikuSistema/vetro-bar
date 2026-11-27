using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Core;

namespace Launcher.Host
{
    /// <summary>
    /// Discovers a Sonos home-theater soundbar on the local network via cached IP or SSDP M-SEARCH
    /// and reads/writes its real UPnP RenderingControl volume and mute state over SOAP.
    /// </summary>
    internal sealed class SonosClient
    {
        private const string ControlPath = "/MediaRenderer/RenderingControl/Control";
        private const string ServiceUrn = "urn:schemas-upnp-org:service:RenderingControl:1";
        private static readonly string[] SoundbarModels = { "Beam", "Arc", "Ray", "Playbar", "Playbase" };

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(1200)
        };

        private readonly object _writeLock = new object();
        private volatile string _ip;
        private int _wantVolume = -1;
        private int _wantMute = -1;   // -1 none, 0 unmute, 1 mute
        private int _wantNight = -1;  // -1 none, 0 off, 1 on
        private int _wantVoice = -1;  // -1 none, 0 off, 1 on
        private bool _workerRunning;

        public bool IsAvailable { get { return !string.IsNullOrEmpty(_ip); } }

        private static string CachePath
        {
            get { return Path.Combine(Weather.CacheDir ?? Path.GetTempPath(), "vetro-sonos.txt"); }
        }

        public void StartDiscovery()
        {
            Task.Run(() =>
            {
                try { Discover(); }
                catch (Exception ex) { Logger.Write("sonos: discovery error: " + ex.Message); }
            });
        }

        private void Discover()
        {
            string cached = ReadCachedIp();
            if (!string.IsNullOrEmpty(cached))
            {
                string model, room;
                if (ProbeDevice(cached, out model, out room))
                {
                    _ip = cached;
                    Logger.Write("sonos: discovered " + model + " (" + room + ") at " + cached + " (cached)");
                    return;
                }
            }

            List<string> candidates = SsdpSearch();
            string bestIp = null, bestModel = null, bestRoom = null;
            foreach (string ip in candidates)
            {
                string model, room;
                if (!ProbeDevice(ip, out model, out room)) continue;
                bool isSoundbar = IsSoundbarModel(model);
                if (bestIp == null || isSoundbar)
                {
                    bestIp = ip;
                    bestModel = model;
                    bestRoom = room;
                    if (isSoundbar) break;
                }
            }

            if (bestIp != null)
            {
                _ip = bestIp;
                SaveCachedIp(bestIp);
                Logger.Write("sonos: discovered " + (bestModel ?? "Sonos") + " (" + (bestRoom ?? "?") + ") at " + bestIp);
            }
            else
            {
                Logger.Write("sonos: no ZonePlayer found on LAN");
            }
        }

        private static string ReadCachedIp()
        {
            try
            {
                if (!File.Exists(CachePath)) return null;
                string ip = File.ReadAllText(CachePath).Trim();
                IPAddress parsed;
                return IPAddress.TryParse(ip, out parsed) ? ip : null;
            }
            catch (Exception) { return null; }
        }

        private static void SaveCachedIp(string ip)
        {
            try { File.WriteAllText(CachePath, ip + "\n"); }
            catch (Exception) { }
        }

        private static List<string> SsdpSearch()
        {
            var ips = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            byte[] query = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                "ST: urn:schemas-upnp-org:device:ZonePlayer:1\r\n\r\n");

            try
            {
                using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Bind(new IPEndPoint(IPAddress.Any, 0));
                    var mcast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                    udp.SendTo(query, mcast);
                    udp.SendTo(query, mcast);

                    byte[] buf = new byte[4096];
                    long deadline = Environment.TickCount64 + 2500;
                    while (true)
                    {
                        long left = deadline - Environment.TickCount64;
                        if (left <= 0) break;
                        udp.ReceiveTimeout = (int)Math.Max(50, left);
                        try
                        {
                            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                            int n = udp.ReceiveFrom(buf, ref remote);
                            if (n <= 0) continue;
                            string text = Encoding.UTF8.GetString(buf, 0, n);
                            string ip = ParseLocationIp(text) ?? ((IPEndPoint)remote).Address.ToString();
                            if (!string.IsNullOrEmpty(ip) && seen.Add(ip)) ips.Add(ip);
                        }
                        catch (SocketException) { break; }
                    }
                }
            }
            catch (Exception ex) { Logger.Write("sonos: ssdp: " + ex.Message); }
            return ips;
        }

        private static string ParseLocationIp(string ssdpResponse)
        {
            foreach (string rawLine in ssdpResponse.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)) continue;
                string urlText = line.Substring(9).Trim();
                Uri uri;
                if (Uri.TryCreate(urlText, UriKind.Absolute, out uri)) return uri.Host;
            }
            return null;
        }

        private static bool ProbeDevice(string ip, out string model, out string room)
        {
            model = null;
            room = null;
            try
            {
                using (var cts = new CancellationTokenSource(1500))
                {
                    HttpResponseMessage resp = Http.GetAsync("http://" + ip + ":1400/xml/device_description.xml", cts.Token).GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode) return false;
                    string xml = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (xml.IndexOf("ZonePlayer", StringComparison.OrdinalIgnoreCase) < 0 &&
                        xml.IndexOf("Sonos", StringComparison.OrdinalIgnoreCase) < 0) return false;
                    model = ExtractTag(xml, "modelName") ?? "Sonos";
                    room = ExtractTag(xml, "roomName") ?? "";
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        private static bool IsSoundbarModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            foreach (string m in SoundbarModels)
                if (model.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public bool TryGetVolume(out int volume)
        {
            volume = 0;
            string xml;
            if (!SendSoap("GetVolume", "<InstanceID>0</InstanceID><Channel>Master</Channel>", 800, out xml)) return false;
            string vText = ExtractTag(xml, "CurrentVolume");
            int parsed;
            if (vText == null || !int.TryParse(vText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return false;
            volume = Math.Max(0, Math.Min(100, parsed));
            return true;
        }

        public bool TryGetMute(out bool muted)
        {
            muted = false;
            string xml;
            if (!SendSoap("GetMute", "<InstanceID>0</InstanceID><Channel>Master</Channel>", 800, out xml)) return false;
            string mText = ExtractTag(xml, "CurrentMute");
            if (mText == null) return false;
            muted = mText.Trim() == "1" || string.Equals(mText.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        public bool TryGetNightMode(out bool enabled) { return TryGetEq("NightMode", out enabled); }

        public bool TryGetDialogLevel(out bool enabled) { return TryGetEq("DialogLevel", out enabled); }

        private bool TryGetEq(string eqType, out bool enabled)
        {
            enabled = false;
            string xml;
            if (!SendSoap("GetEQ", "<InstanceID>0</InstanceID><EQType>" + eqType + "</EQType>", 800, out xml)) return false;
            string vText = ExtractTag(xml, "CurrentValue");
            if (vText == null) return false;
            enabled = vText.Trim() == "1" || string.Equals(vText.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        public void SetVolumeAsync(int volume)
        {
            int clamped = Math.Max(0, Math.Min(100, volume));
            lock (_writeLock)
            {
                _wantVolume = clamped;
                EnsureWorkerLocked();
            }
        }

        public void SetMuteAsync(bool muted)
        {
            lock (_writeLock)
            {
                _wantMute = muted ? 1 : 0;
                EnsureWorkerLocked();
            }
        }

        public void SetNightModeAsync(bool enabled)
        {
            lock (_writeLock)
            {
                _wantNight = enabled ? 1 : 0;
                EnsureWorkerLocked();
            }
        }

        public void SetDialogLevelAsync(bool enabled)
        {
            lock (_writeLock)
            {
                _wantVoice = enabled ? 1 : 0;
                EnsureWorkerLocked();
            }
        }

        private void EnsureWorkerLocked()
        {
            if (_workerRunning) return;
            _workerRunning = true;
            Task.Run(PumpWrites);
        }

        private void PumpWrites()
        {
            while (true)
            {
                int vol, mute, night, voice;
                lock (_writeLock)
                {
                    mute = _wantMute;
                    _wantMute = -1;
                    vol = _wantVolume;
                    _wantVolume = -1;
                    night = _wantNight;
                    _wantNight = -1;
                    voice = _wantVoice;
                    _wantVoice = -1;
                    if (mute < 0 && vol < 0 && night < 0 && voice < 0)
                    {
                        _workerRunning = false;
                        return;
                    }
                }

                try
                {
                    string unused;
                    if (mute >= 0)
                    {
                        SendSoap("SetMute", "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredMute>" + mute + "</DesiredMute>", 1200, out unused);
                    }
                    if (vol >= 0)
                    {
                        SendSoap("SetVolume", "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>" + vol.ToString(CultureInfo.InvariantCulture) + "</DesiredVolume>", 1200, out unused);
                    }
                    if (night >= 0)
                    {
                        SendSoap("SetEQ", "<InstanceID>0</InstanceID><EQType>NightMode</EQType><DesiredValue>" + night + "</DesiredValue>", 1200, out unused);
                    }
                    if (voice >= 0)
                    {
                        SendSoap("SetEQ", "<InstanceID>0</InstanceID><EQType>DialogLevel</EQType><DesiredValue>" + voice + "</DesiredValue>", 1200, out unused);
                    }
                }
                catch (Exception ex) { Logger.Write("sonos: write: " + ex.Message); }
            }
        }

        private bool SendSoap(string action, string argsXml, int timeoutMs, out string responseXml)
        {
            responseXml = null;
            string ip = _ip;
            if (string.IsNullOrEmpty(ip)) return false;

            string body = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body><u:" + action + " xmlns:u=\"" + ServiceUrn + "\">" + argsXml + "</u:" + action + "></s:Body></s:Envelope>";

            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(HttpMethod.Post, "http://" + ip + ":1400" + ControlPath))
                {
                    req.Headers.Add("SOAPACTION", "\"" + ServiceUrn + "#" + action + "\"");
                    req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
                    HttpResponseMessage resp = Http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode) return false;
                    responseXml = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        private static string ExtractTag(string xml, string tag)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            string open = "<" + tag + ">";
            string close = "</" + tag + ">";
            int s = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (s < 0) return null;
            s += open.Length;
            int e = xml.IndexOf(close, s, StringComparison.OrdinalIgnoreCase);
            return e > s ? xml.Substring(s, e - s) : null;
        }
    }
}
