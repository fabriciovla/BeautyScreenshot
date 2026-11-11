using System;
using System.Media;

namespace BeautyScreenshot
{
    /// <summary>
    /// The capture sound: shutter.wav, embedded in the exe (already trimmed and at its final, quiet level).
    /// Preloaded at startup, so playing it costs nothing.
    /// </summary>
    static class ShutterSound
    {
        static SoundPlayer player;

        public static void Prepare()
        {
            if (player != null) return;
            player = new SoundPlayer(typeof(ShutterSound).Assembly.GetManifestResourceStream("shutter.wav"));
            player.Load();
        }

        /// <param name="sample">Plays even with the sound turned off (the sample when switching it on).</param>
        public static void Play(bool sample = false)
        {
            if (!sample && !Settings.PlaySound) return;
            try
            {
                Prepare();
                player.Play();   // asynchronous
            }
            catch (Exception ex) { Log.Error(ex); }
        }
    }
}
