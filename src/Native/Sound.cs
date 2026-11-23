using System;
using System.Runtime.InteropServices;

namespace Launcher.Native
{
    /// <summary>
    /// The TV's master volume and speaker selection (libcapi-media-sound-manager-tv).
    /// </summary>
    internal static class Sound
    {
        private const string Lib = "libcapi-media-sound-manager-tv.so.0";
        [DllImport(Lib, EntryPoint = "sound_manager_get_master_volume")] private static extern int GetVolume(out int value);
        [DllImport(Lib, EntryPoint = "sound_manager_set_master_volume")] private static extern int SetVolume(int value);
        [DllImport(Lib, EntryPoint = "sound_manager_get_speaker_selection")] private static extern int GetSpeaker(out int type);
        [DllImport(Lib, EntryPoint = "sound_manager_set_speaker_selection")] private static extern int SetSpeaker(int type);

        public const int SpeakerTv = 0, SpeakerOptical = 1, SpeakerReceiver = 2, SpeakerBluetooth = 5;

        public static bool TryGetSpeakerSelection(out int type)
        {
            type = -1;
            try { return GetSpeaker(out type) == 0; }
            catch (Exception) { type = -1; return false; }
        }

        public static bool TrySetSpeakerSelection(int type)
        {
            try { return SetSpeaker(type) == 0; }
            catch (Exception) { return false; }
        }

        public static bool TryGetVolume(out int volume)
        {
            volume = 0;
            try { return GetVolume(out volume) == 0 && volume >= 0 && volume <= 100; }
            catch (Exception) { return false; }
        }

        public static bool TrySetVolume(int volume)
        {
            try { return SetVolume(volume) == 0; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// True when the active output is an external HDMI-CEC receiver or Optical soundbar
        /// where Samsung's native key handler forwards the volume command over CEC / IR / Q-Symphony.
        /// </summary>
        public static bool IsExternalOutput(int selection)
        {
            switch (selection)
            {
                case 1:
                case 2:
                case 8:
                case 9:
                case 11:
                case 12:
                case 17:
                case 18:
                    return true;
                default:
                    return false;
            }
        }
    }
}
