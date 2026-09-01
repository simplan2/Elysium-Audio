using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ElysiumAudio.Helpers
{
    public static class DefaultValues
    {
        // Valor típico de LUFS objetivo para normalización de audio
        public const double MIN_TARGET_LUFS = -24.0;
        public const double MAX_TARGET_LUFS = -6.0;
        public const double DEFAULT_TARGET_LUFS = -14.0;

        // Valor típico de techo de pico verdadero en dBTP
        public const double MIN_TRUE_PEAK_CEILING = -3.0;
        public const double MAX_TRUE_PEAK_CEILING = -0.1;
        public const double DEFAULT_TRUE_PEAK_CEILING = -1.0;

        // Tiempo de liberación del limitador en milisegundos
        public const double MIN_RELEASE_TIME_MS = 20.0;
        public const double MAX_RELEASE_TIME_MS = 200.0;
        public const double DEFAULT_RELEASE_TIME_MS = 50.0;

        // Tiempo de anticipación del limitador en milisegundos
        public const double MIN_LOOK_AHEAD_TIME_MS = 1.0;
        public const double MAX_LOOK_AHEAD_TIME_MS = 10.0;
        public const double DEFAULT_LOOK_AHEAD_TIME_MS = 5.0;

        // Directorio de salida por defecto: subcarpeta "ElysiumAudio_Normalized" en el escritorio
        // Devuelve null si no se puede resolver (en cuyo caso se usa el directorio de entrada).
        public static string? GetDefaultOutputDirectory()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (string.IsNullOrEmpty(desktop)) return null;

                string dir = Path.Combine(desktop, "Elysium Audio Normalized");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                return null;
            }
        }
    }
}
