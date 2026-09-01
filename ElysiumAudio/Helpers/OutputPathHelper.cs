using System.IO;

namespace ElysiumAudio.Helpers
{
    public static class OutputPathHelper
    {
        /// <summary>
        /// Genera una ruta de salida única para el archivo normalizado.
        /// Si ya existe un archivo con el mismo nombre en el directorio,
        /// se agrega un sufijo numérico (_1, _2, ...) al nombre base.
        /// </summary>
        /// <param name="basePath">El path de salida inicial (ej: "...\cancion.wav")</param>
        /// <returns>Un path que no colisiona con ningún archivo existente.</returns>
        public static string GetUniqueOutputPath(string basePath)
        {
            string directory = Path.GetDirectoryName(basePath) ?? "";
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(basePath);
            string ext = Path.GetExtension(basePath);

            string candidate = basePath;
            int counter = 1;

            while (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, $"{fileNameWithoutExt}_{counter}{ext}");
                counter++;
            }

            return candidate;
        }
    }
}
