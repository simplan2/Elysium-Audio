using ATL;
using System;
using System.Collections.Generic;
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
        // Valida la estructura RIFF/WAVE leyendo SOLO cabeceras de chunk (8 bytes por
        // iteración), sin reservar memoria en proporción al tamaño declarado.
        //
        // Motivo: ATL (ATL.AudioData.IO.Helpers.ListTag.readInfoPurpose) reserva un
        // búfer del tamaño que el propio chunk declara. Medido con ATL 7.16: un
        // LIST/INFO que declara 512 MB en un archivo real de 90 KB provoca ~513 MB
        // al leer y ~3.5 GB al escribir. Por encima de ~2 GB el cálculo interno de
        // ATL desborda y lanza.
        //
        // No se intenta "reparar" recortando el tamaño declarado: cuando un chunk
        // miente sobre su longitud, el layout real es irrecuperable (no se sabe
        // dónde termina ese chunk ni dónde empieza el siguiente) y un recorte inventado
        // acabaría tragándose el chunk 'data'. Es más seguro no ejecutar ATL sobre un
        // WAV que declara una estructura que no tiene. La normalización de audio no
        // depende de esto: sigue adelante con el resto de la salida ya escrita.
        private static bool IsRiffStructurallySound(string path, out string reason)
        {
            reason = string.Empty;
            try
            {
                if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return true;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                long len = fs.Length;
                if (len < 12)
                {
                    reason = "archivo demasiado pequeño para contener un WAV";
                    return false;
                }

                var h = new byte[4];
                fs.Position = 0;
                if (fs.Read(h, 0, 4) != 4 || h[0] != 'R' || h[1] != 'I' || h[2] != 'F' || h[3] != 'F')
                {
                    reason = "firma RIFF ausente";
                    return false;
                }

                fs.Position = 8;
                if (fs.Read(h, 0, 4) != 4 || h[0] != 'W' || h[1] != 'A' || h[2] != 'V' || h[3] != 'E')
                {
                    reason = "firma WAVE ausente";
                    return false;
                }

                bool hasFmt = false;
                bool hasData = false;
                var sizeBuf = new byte[4];
                long pos = 12;
                while (pos + 8 <= len)
                {
                    fs.Position = pos;
                    if (fs.Read(h, 0, 4) != 4) break;
                    if (fs.Read(sizeBuf, 0, 4) != 4) break;

                    uint size = BitConverter.ToUInt32(sizeBuf, 0);
                    long dataStart = pos + 8;
                    long dataEnd = dataStart + (long)size;

                    if (dataEnd > len)
                    {
                        reason = $"chunk '{SafeFourCC(h)}' declara {size} bytes pero solo quedan {len - dataStart}";
                        return false;
                    }

                    if (h[0] == 'f' && h[1] == 'm' && h[2] == 't' && h[3] == ' ') hasFmt = true;
                    if (h[0] == 'd' && h[1] == 'a' && h[2] == 't' && h[3] == 'a') hasData = true;

                    pos = dataEnd + ((size & 1) == 1 ? 1 : 0);
                }

                if (!hasFmt)
                {
                    reason = "chunk 'fmt ' ausente";
                    return false;
                }
                if (!hasData)
                {
                    reason = "chunk 'data' ausente";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        // Imprime un FourCC conservando los caracteres imprimibles y sustituyendo el resto,
        // para que el motivo del descarte sea legible en el log de depuración.
        private static string SafeFourCC(byte[] h)
        {
            var sb = new System.Text.StringBuilder(4);
            foreach (byte b in h)
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '?');
            return sb.ToString();
        }

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

            // Cinturón de seguridad: la salida la genera libsndfile y es válida, pero si
            // algún día este método recibiera un WAV ya alterado, abortamos antes de que
            // ATL reserve en proporción a un tamaño declarado enorme.
            if (!IsRiffStructurallySound(outputPath, out string why))
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataService] Salida WAV con estructura RIFF no fiable, no se escriben tags: {Path.GetFileName(outputPath)} ({why})");
                return;
            }

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

                // ATL escribe el chunk LIST/INFO de RIFF en UTF-8, pero ese chunk no
                // tiene campo de codificación: por especificación es Latin-1. Un lector
                // conforme leería "Canción". Se corrige a Latin-1 aquí.
                NormalizeRiffInfoChunk(outputPath);
            }
            else
            {
                target.Save();
                System.Diagnostics.Debug.WriteLine($"[MetadataService] Tags Vorbis guardados: {outputPath}");
            }
        }

        // Decodificación estricta: si los bytes no son UTF-8 válido se rechaza el
        // campo en vez de sustituirlo por caracteres de reemplazo.
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        // Reescribe el chunk LIST/INFO de un WAV pasando sus valores de UTF-8 a Latin-1.
        // Los campos cuyo valor NO quepa en Latin-1 (japonés, cirílico…) se omiten del
        // chunk en lugar de emitir basura; la información correcta sigue intacta en ID3v2.
        // Nunca lanza: un fallo aquí solo deja los metadatos como estaban.
        private static void NormalizeRiffInfoChunk(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (fs.Length < 12) return;

                var header = new byte[12];
                fs.Position = 0;
                if (fs.Read(header, 0, 12) != 12) return;
                if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F') return;
                if (header[8] != 'W' || header[9] != 'A' || header[10] != 'V' || header[11] != 'E') return;

                long pos = 12;
                while (pos + 8 <= fs.Length)
                {
                    fs.Position = pos;
                    var chunkId = new byte[4];
                    if (fs.Read(chunkId, 0, 4) != 4) return;

                    var sizeBytes = new byte[4];
                    if (fs.Read(sizeBytes, 0, 4) != 4) return;
                    int size = BitConverter.ToInt32(sizeBytes, 0);
                    long dataPos = pos + 8;

                    // El tamaño declarado tiene que caber en el archivo. Sin esta comprobación
                    // un chunk corrupto que anuncie gigabytes provocaría una reserva enorme
                    // de memoria antes de fallar al leer.
                    if (size < 4 || dataPos + size > fs.Length) return;

                    bool isList = chunkId[0] == 'L' && chunkId[1] == 'I'
                               && chunkId[2] == 'S' && chunkId[3] == 'T';

                    if (isList)
                    {
                        var magic = new byte[4];
                        fs.Position = dataPos;
                        if (fs.Read(magic, 0, 4) == 4
                            && magic[0] == 'I' && magic[1] == 'N' && magic[2] == 'F' && magic[3] == 'O')
                        {
                            var payload = new byte[size - 4];
                            fs.Position = dataPos + 4;
                            if (fs.Read(payload, 0, payload.Length) != payload.Length) return;

                            byte[]? rebuilt = RebuildInfoSubChunks(payload);
                            if (rebuilt != null) RewriteListChunk(fs, pos, size, rebuilt);
                            return;
                        }
                    }

                    pos = dataPos + size + (size & 1);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataService] No se pudo normalizar LIST/INFO: {ex.Message}");
            }
        }

        // Reconstruye los sub-chunks INFO en Latin-1. Devuelve null si no hay nada que cambiar.
        private static byte[]? RebuildInfoSubChunks(byte[] payload)
        {
            var output = new List<byte>();
            int p = 0;
            bool changed = false;

            while (p + 8 <= payload.Length)
            {
                int subSize = BitConverter.ToInt32(payload, p + 4);
                if (subSize < 0) break;

                int dataStart = p + 8;
                if (dataStart + subSize > payload.Length) break;

                int textLen = subSize;
                while (textLen > 0 && payload[dataStart + textLen - 1] == 0) textLen--;

                string subId = Encoding.ASCII.GetString(payload, p, 4);
                string value;
                try
                {
                    value = StrictUtf8.GetString(payload, dataStart, textLen);
                }
                catch (DecoderFallbackException)
                {
                    // Datos que no son texto UTF-8: se copian tal cual, sin tocar.
                    int copyLen = Math.Min(8 + subSize + (subSize & 1), payload.Length - p);
                    for (int i = 0; i < copyLen; i++) output.Add(payload[p + i]);
                    p += copyLen;
                    continue;
                }

                bool fitsLatin1 = true;
                foreach (char c in value)
                {
                    if (c > 0xFF) { fitsLatin1 = false; break; }
                }

                if (fitsLatin1)
                {
                    byte[] encoded = Encoding.Latin1.GetBytes(value);
                    if (!SameBytes(encoded, payload, dataStart, textLen)) changed = true;

                    int newSize = encoded.Length + 1; // incluye el terminador nulo
                    output.AddRange(Encoding.ASCII.GetBytes(subId));
                    output.AddRange(BitConverter.GetBytes(newSize));
                    output.AddRange(encoded);
                    output.Add(0);
                    if ((newSize & 1) == 1) output.Add(0);
                }
                else
                {
                    // No representable en Latin-1: se omite del chunk RIFF.
                    changed = true;
                }

                p = dataStart + subSize + (subSize & 1);
            }

            return changed ? output.ToArray() : null;
        }

        private static bool SameBytes(byte[] a, byte[] buffer, int offset, int count)
        {
            if (a.Length != count) return false;
            for (int i = 0; i < count; i++)
                if (a[i] != buffer[offset + i]) return false;
            return true;
        }

        // Escribe el nuevo chunk LIST y arrastra el resto del archivo hacia atrás.
        // Latin-1 siempre ocupa <= que UTF-8, así que el archivo solo puede encogerse.
        private static void RewriteListChunk(FileStream fs, long chunkPos, int oldSize, byte[] newPayload)
        {
            var newData = new List<byte>();
            newData.AddRange(Encoding.ASCII.GetBytes("INFO"));
            newData.AddRange(newPayload);

            int newSize = newData.Count;
            int oldTotal = 8 + oldSize + (oldSize & 1);
            int newTotal = 8 + newSize + (newSize & 1);

            if (newTotal > oldTotal) return; // salvaguarda: no debería ocurrir

            // Primero el campo de tamaño (está en chunkPos+4, ANTES del payload),
            // después el payload. Escribir en otro orden sobrescribe la cabecera del chunk siguiente.
            fs.Position = chunkPos + 4;
            fs.Write(BitConverter.GetBytes(newSize), 0, 4);

            fs.Position = chunkPos + 8;
            fs.Write(newData.ToArray(), 0, newData.Count);
            if ((newSize & 1) == 1) fs.WriteByte(0);

            long delta = oldTotal - newTotal;
            if (delta > 0)
            {
                long tailFrom = chunkPos + oldTotal;
                long tailLength = fs.Length - tailFrom;
                if (tailLength > 0)
                {
                    var buffer = new byte[64 * 1024];
                    long readPos = tailFrom;
                    long writePos = tailFrom - delta;
                    long remaining = tailLength;
                    while (remaining > 0)
                    {
                        int want = (int)Math.Min(buffer.Length, remaining);
                        fs.Position = readPos;
                        int read = fs.Read(buffer, 0, want);
                        if (read <= 0) break;
                        fs.Position = writePos;
                        fs.Write(buffer, 0, read);
                        readPos += read;
                        writePos += read;
                        remaining -= read;
                    }
                }
                fs.SetLength(fs.Length - delta);
            }

            fs.Position = 4;
            fs.Write(BitConverter.GetBytes((int)(fs.Length - 8)), 0, 4);
            fs.Flush();
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
                if (!IsRiffStructurallySound(inputPath, out string why))
                {
                    System.Diagnostics.Debug.WriteLine($"[MetadataService] WAV con estructura RIFF no fiable, se omite el clonado de metadatos: {Path.GetFileName(inputPath)} ({why})");
                    return;
                }

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