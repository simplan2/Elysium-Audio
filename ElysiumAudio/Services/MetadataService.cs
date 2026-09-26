using ATL;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ElysiumAudio.Services
{
    // P6: Servicio exclusivo de metadatos (tags + carátula).
    // Separado del motor de audio; aqui NO hay procesamiento de muestras.
    public static class MetadataService
    {
        // INTEROP NATIVO: Invoca la API de Windows que traduce rutas largas con tildes/eñes
        // a rutas cortas en formato MS-DOS 8.3 (ej: "D:\Música\Canción.wav" -> "D:\MSICA~1\CANCN~1.WAV")
        // Este formato ASCII puro es 100% digerible por los motores C++ de libsndfile y ATL
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

        private static string GetSafePathForNativeLibraries(string longPath)
        {
            if (string.IsNullOrWhiteSpace(longPath)) return longPath;

            // Si es una ruta de salida y el archivo aún no existe, creamos un cascarón vacío temporal
            // para que la API de Windows pueda resolver el mapa de caracteres del disco duro
            string directory = Path.GetDirectoryName(longPath) ?? "";
            if (!File.Exists(longPath) && Directory.Exists(directory))
            {
                File.WriteAllBytes(longPath, Array.Empty<byte>());
            }

            StringBuilder shortPath = new StringBuilder(255);
            uint result = GetShortPathName(longPath, shortPath, (uint)shortPath.Capacity);

            return result > 0 ? shortPath.ToString() : longPath;
        }

        // Copia los campos comunes del Track origen a un Track destino
        private static void CopyFieldsToTag(Track target, Track source)
        {
            target.Title = source.Title;
            target.Artist = source.Artist;
            target.AlbumArtist = source.AlbumArtist;
            target.Album = source.Album;
            target.Year = source.Year;
            target.Genre = source.Genre;
            target.Comment = source.Comment;
            target.TrackNumber = source.TrackNumber;
            target.DiscNumber = source.DiscNumber;
            target.Composer = source.Composer;
            target.ISRC = source.ISRC;
            target.Copyright = source.Copyright;
        }

        // Escribe tags básicos + carátula en un solo pase con ATL:
        // WAV -> ID3v2.3 (estándar compatible), FLAC -> Vorbis nativo.
        // ATL mantiene el chunk data intacto y no corrompe el RIFF.
        private static void WriteBasicTags(string outputPath, Track source)
        {
            bool isWav = Path.GetExtension(outputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase);
            string safePath = GetSafePathForNativeLibraries(outputPath);

            var target = new Track(safePath);
            CopyFieldsToTag(target, source);

            target.EmbeddedPictures.Clear();
            foreach (var pic in source.EmbeddedPictures)
            {
                pic.PicType = PictureInfo.PIC_TYPE.Front;
                target.EmbeddedPictures.Add(pic);
            }

            if (isWav)
            {
                Settings.ID3v2_tagSubVersion = 3;
                target.Save(ATL.AudioData.MetaDataIOFactory.TagType.ID3V2);
                System.Diagnostics.Debug.WriteLine($"[MetadataService] Tags ID3v2.3 guardados: {outputPath}");
            }
            else
            {
                target.Save();
                System.Diagnostics.Debug.WriteLine($"[MetadataService] Tags Vorbis guardados: {outputPath}");
            }
        }

        // Determina si el archivo tiene metadatos "significativos" que valga la pena clonar.
        // Un archivo sin ninguna etiqueta real (título vacío, sin año/carátula…) no debe forzar
        // la escritura de tags en la salida: se omite para no "inventar" metadatos.
        // Nota: ATL devuelve el NOMBRE DE ARCHIVO como Title cuando no existe tag de título;
        // se excluye ese caso comparando contra el nombre de archivo (original y 8.3 corto).
        private static bool HasMeaningfulTags(Track track, string inputPath, string safeInputPath)
        {
            string derivedTitleLong = Path.GetFileNameWithoutExtension(inputPath);
            string derivedTitleShort = Path.GetFileNameWithoutExtension(safeInputPath);
            bool hasTitle = !string.IsNullOrWhiteSpace(track.Title)
                && track.Title != derivedTitleLong
                && track.Title != derivedTitleShort;

            bool textField = hasTitle
                || !string.IsNullOrWhiteSpace(track.Artist)
                || !string.IsNullOrWhiteSpace(track.AlbumArtist)
                || !string.IsNullOrWhiteSpace(track.Album)
                || !string.IsNullOrWhiteSpace(track.Genre)
                || !string.IsNullOrWhiteSpace(track.Comment)
                || !string.IsNullOrWhiteSpace(track.Composer)
                || !string.IsNullOrWhiteSpace(track.ISRC)
                || !string.IsNullOrWhiteSpace(track.Copyright);

            bool numericField = track.Year != 0
                || track.TrackNumber != 0
                || track.DiscNumber != 0;

            return textField || numericField || track.EmbeddedPictures.Count > 0;
        }

        // Clonación de metadatos + carátula.
        // Un solo pase ATL con los tags básicos del origen. Si el archivo ORIGINAL
        // no tiene tags significativos, se omite la escritura por completo.
        public static void CloneMetadataAndCover(string inputPath, string outputPath)
        {
            if (!File.Exists(inputPath) || !File.Exists(outputPath)) return;

            try
            {
                string safeInputPath = GetSafePathForNativeLibraries(inputPath);
                Track source = new Track(safeInputPath);

                if (!HasMeaningfulTags(source, inputPath, safeInputPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[MetadataService] Origen sin tags significativos, omitiendo clonado de metadatos: {Path.GetFileName(inputPath)}");
                    return;
                }

                WriteBasicTags(outputPath, source);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error crítico en el módulo de metadatos: {ex.Message}");
                throw;
            }
        }
    }
}