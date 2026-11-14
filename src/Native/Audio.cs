using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Launcher.Core;

namespace Launcher.Native
{
    internal enum SoundEffect
    {
        Open = 0,
        Close = 1,
        Move = 2,
        Launch = 3
    }

    /// <summary>
    /// Low-latency UI sound player for Tizen 6.
    /// Preloads 48 kHz PCM buffers in RAM and plays them through PulseAudio (libpulse-simple.so.0)
    /// on a dedicated background thread. The PulseAudio stream is opened on demand and closed
    /// automatically after 1.5 s of idle time so no stream is ever left open across TV standby,
    /// HDMI/TDT source switches, or media app playback.
    /// </summary>
    internal static class SoundPlayer
    {
        private enum Backend { None, PulseSimple, WavPlayer, Cli }

        private const int PaStreamPlayback = 1;
        private const int PaSampleS16Le = 3;
        private const int IdleCloseMs = 1500;

        [StructLayout(LayoutKind.Sequential)]
        private struct PaSampleSpec
        {
            public int Format;
            public uint Rate;
            public byte Channels;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PaBufferAttr
        {
            public uint MaxLength;
            public uint TLength;
            public uint PreBuf;
            public uint MinReq;
            public uint FragSize;
        }

        [DllImport("libpulse-simple.so.0")]
        private static extern IntPtr pa_simple_new(
            string server, string name, int dir, string dev, string streamName,
            ref PaSampleSpec ss, IntPtr map, ref PaBufferAttr attr, out int error);

        [DllImport("libpulse-simple.so.0")]
        private static extern int pa_simple_write(IntPtr s, byte[] data, UIntPtr bytes, out int error);

        [DllImport("libpulse-simple.so.0")]
        private static extern int pa_simple_flush(IntPtr s, out int error);

        [DllImport("libpulse-simple.so.0")]
        private static extern void pa_simple_free(IntPtr s);

        [DllImport("libcapi-media-wav-player.so.0")]
        private static extern int wav_player_start_new(string path, IntPtr streamInfo, IntPtr callback, IntPtr userData, out int id);

        private sealed class SoundClip
        {
            public readonly string Path;
            public readonly byte[] Pcm;
            public readonly int SampleRate;
            public readonly int Channels;

            public SoundClip(string path, byte[] pcm, int sampleRate, int channels)
            {
                Path = path;
                Pcm = pcm;
                SampleRate = sampleRate;
                Channels = channels;
            }
        }

        private static readonly string[] FileNames = { "open.wav", "close.wav", "move.wav", "launch.wav" };
        private static readonly SoundClip[] Clips = new SoundClip[FileNames.Length];

        private static Backend _backend = Backend.None;
        private static IntPtr _pa = IntPtr.Zero;
        private static int _rate = 48000, _channels = 2;
        private static string _cliBin;
        private static Action<string> _log;

        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        private static volatile int _pending = -1;
        private static volatile bool _stopping;
        private static Thread _worker;
        private static bool _loggedPlayError;

        public static void Init(string soundsDir, Action<string> log, bool disabled)
        {
            _log = log ?? (_ => { });
            if (disabled) { _log("sound: disabled (--no-sound)"); return; }
            if (!Directory.Exists(soundsDir)) { _log("sound: directory not found (" + soundsDir + ")"); return; }

            int loaded = 0;
            for (int i = 0; i < FileNames.Length; i++)
            {
                string path = Path.Combine(soundsDir, FileNames[i]);
                if (!File.Exists(path)) continue;
                try
                {
                    byte[] raw = File.ReadAllBytes(path);
                    WavInfo info;
                    if (!WavParser.TryParse(raw, out info))
                    {
                        _log("sound: invalid wav " + FileNames[i]);
                        continue;
                    }
                    byte[] pcm = new byte[info.DataLength];
                    Buffer.BlockCopy(raw, info.DataOffset, pcm, 0, info.DataLength);
                    Clips[i] = new SoundClip(path, pcm, info.SampleRate, info.Channels);
                    _rate = info.SampleRate;
                    _channels = info.Channels;
                    loaded++;
                }
                catch (Exception ex) { _log("sound: load " + FileNames[i] + ": " + ex.Message); }
            }

            if (loaded == 0) { _log("sound: no clips loaded"); return; }

            if (TryOpenPulse())
            {
                ClosePulse();   // do not hold the sink open while idle
                _backend = Backend.PulseSimple;
                StartWorker();
                _log("sound: PulseAudio ready on-demand (" + loaded + " clips, " + _rate + " Hz)");
                return;
            }

            if (File.Exists("/usr/lib/libcapi-media-wav-player.so.0") || File.Exists("/usr/lib64/libcapi-media-wav-player.so.0"))
            {
                _backend = Backend.WavPlayer;
                StartWorker();
                _log("sound: wav_player ready (" + loaded + " clips)");
                return;
            }

            foreach (string bin in new[] { "/usr/bin/paplay", "/usr/bin/aplay" })
            {
                if (File.Exists(bin))
                {
                    _cliBin = bin;
                    _backend = Backend.Cli;
                    StartWorker();
                    _log("sound: cli fallback " + bin + " (" + loaded + " clips)");
                    return;
                }
            }

            _log("sound: no supported audio backend found");
        }

        private static bool TryOpenPulse()
        {
            if (_pa != IntPtr.Zero) return true;
            try
            {
                var ss = new PaSampleSpec
                {
                    Format = PaSampleS16Le,
                    Rate = (uint)_rate,
                    Channels = (byte)_channels
                };
                var attr = new PaBufferAttr
                {
                    MaxLength = uint.MaxValue,
                    TLength = 19200,
                    PreBuf = 2048,
                    MinReq = uint.MaxValue,
                    FragSize = uint.MaxValue
                };
                int err;
                IntPtr pa = pa_simple_new(null, "vetro-bar", PaStreamPlayback, null, "ui", ref ss, IntPtr.Zero, ref attr, out err);
                if (pa == IntPtr.Zero)
                {
                    if (!_loggedPlayError) { _loggedPlayError = true; _log("sound: pa_simple_new failed (err=" + err + ")"); }
                    return false;
                }
                _pa = pa;
                return true;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (Exception ex)
            {
                if (!_loggedPlayError) { _loggedPlayError = true; _log("sound: pulse open: " + ex.Message); }
                return false;
            }
        }

        private static void ClosePulse()
        {
            if (_pa == IntPtr.Zero) return;
            try { pa_simple_free(_pa); } catch (Exception) { }
            _pa = IntPtr.Zero;
        }

        private static void StartWorker()
        {
            _stopping = false;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "vetro-sound" };
            _worker.Start();
        }

        /// <summary>Non-blocking, zero-allocation trigger safe to call from the EFL main loop.</summary>
        public static void Play(SoundEffect effect)
        {
            if (_backend == Backend.None) return;
            int idx = (int)effect;
            if (idx < 0 || idx >= Clips.Length || Clips[idx] == null) return;
            _pending = idx;
            Wake.Set();
        }

        private static void WorkerLoop()
        {
            while (!_stopping)
            {
                bool signaled = Wake.WaitOne(_pa != IntPtr.Zero ? IdleCloseMs : Timeout.Infinite);
                if (_stopping) break;
                if (!signaled)
                {
                    // Idle timeout elapsed with no new sound: release the PulseAudio stream so tizenaudio-sink never wedges.
                    ClosePulse();
                    continue;
                }
                int idx = Interlocked.Exchange(ref _pending, -1);
                if (idx < 0) continue;
                SoundClip clip = Clips[idx];
                if (clip == null) continue;
                PlayClip(clip);
            }
            ClosePulse();
        }

        private static void PlayClip(SoundClip clip)
        {
            try
            {
                switch (_backend)
                {
                    case Backend.PulseSimple:
                        int err;
                        if (_pa != IntPtr.Zero)
                        {
                            pa_simple_flush(_pa, out err);
                            if (pa_simple_write(_pa, clip.Pcm, (UIntPtr)clip.Pcm.Length, out err) >= 0)
                                break;
                            // Reconnect once if PulseAudio restarted or suspended the stream after standby.
                            ClosePulse();
                        }
                        if (TryOpenPulse())
                        {
                            if (pa_simple_write(_pa, clip.Pcm, (UIntPtr)clip.Pcm.Length, out err) < 0)
                            {
                                if (!_loggedPlayError) { _loggedPlayError = true; _log("sound: pa_simple_write failed (err=" + err + ")"); }
                                ClosePulse();
                            }
                        }
                        break;

                    case Backend.WavPlayer:
                        int id;
                        int rc = wav_player_start_new(clip.Path, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out id);
                        if (rc != 0 && !_loggedPlayError)
                        {
                            _loggedPlayError = true;
                            _log("sound: wav_player_start_new rc=" + rc);
                        }
                        break;

                    case Backend.Cli:
                        using (var p = Process.Start(new ProcessStartInfo(_cliBin, "\"" + clip.Path + "\"")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }))
                        {
                            p?.WaitForExit(1500);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                if (!_loggedPlayError) { _loggedPlayError = true; _log("sound: play error: " + ex.Message); }
            }
        }

        public static void Shutdown()
        {
            _stopping = true;
            Wake.Set();
            if (_worker != null && _worker.IsAlive) _worker.Join(200);
            ClosePulse();
            _backend = Backend.None;
        }
    }
}
