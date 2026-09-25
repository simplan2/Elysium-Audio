using ATL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        // =========================================================================
        // WINDOWS PROPERTY SYSTEM (IPropertyStore) — Escritor nativo de metadatos
        // Usa la misma API que el Explorador de Windows para leer/escribir tags.
        // Funciona en WAV, FLAC, MP3, etc. sin corromper el audio.
        // =========================================================================

        // IPropertyStore (propsys.h)
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PropertyKey pkey);
            int GetValue(ref PropertyKey key, out PropVariant pv);
            int SetValue(ref PropertyKey key, ref PropVariant pv);
            int Commit();
        }

        // PropertyKey (FMTID + PID)
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;

            public PropertyKey(string fmtid, uint pid)
            {
                this.fmtid = new Guid(fmtid);
                this.pid = pid;
            }
        }

        // PropVariant (VARIANT simplificado para strings)
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort vt;      // VarType (VT_LPWSTR = 31)
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr pwszVal; // puntero a string Unicode

            public static PropVariant FromString(string value)
            {
                var pv = new PropVariant();
                if (value != null)
                {
                    pv.vt = 31; // VT_LPWSTR
                    pv.pwszVal = Marshal.StringToCoTaskMemUni(value);
                }
                else
                {
                    pv.vt = 0; // VT_EMPTY
                    pv.pwszVal = IntPtr.Zero;
                }
                return pv;
            }

            public void Clear()
            {
                if (pwszVal != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pwszVal);
                    pwszVal = IntPtr.Zero;
                }
                vt = 0;
            }
        }

        // Property Keys estándar (de propkey.h)
        private static readonly PropertyKey PKEY_Title = new("{F29F85E0-4FF9-1068-AB91-08002B27B3D9}", 2);        // System.Title
        private static readonly PropertyKey PKEY_Music_AlbumTitle = new("{56A3372E-CE9C-11D2-9F0E-006097C686F6}", 4); // System.Music.AlbumTitle
        private static readonly PropertyKey PKEY_Music_Artist = new("{56A3372E-CE9C-11D2-9F0E-006097C686F6}", 13);   // System.Music.Artist
        private static readonly PropertyKey PKEY_Music_AlbumArtist = new("{56A3372E-CE9C-11D2-9F0E-006097C686F6}", 14); // System.Music.AlbumArtist
        private static readonly PropertyKey PKEY_Music_Genre = new("{56A3372E-CE9C-11D2-9F0E-006097C686F6}", 11);    // System.Music.Genre
        private static readonly PropertyKey PKEY_Music_TrackNumber = new("{56A3372E-CE9C-11D2-9F0E-006097C686F6}", 7);  // System.Music.TrackNumber
        private static readonly PropertyKey PKEY_Music_Year = new("{56A3372E-CE9C-11D2-9F0E-006097C686F6}", 5);       // System.Music.Year
        private static readonly PropertyKey PKEY_Comment = new("{F29F85E0-4FF9-1068-AB91-08002B27B3D9}", 6);          // System.Comment
        private static readonly PropertyKey PKEY_Authors = new("{F29F85E0-4FF9-1068-AB91-08002B27B3D9}", 4);         // System.Author (para Composer)
        private static readonly PropertyKey PKEY_ApplicationName = new("{F29F85E0-4FF9-1068-AB91-08002B27B3D9}", 18);  // System.ApplicationName (para Conductor/Publisher)

        // SHGetPropertyStoreFromParsingName (shell32.dll) - método más compatible
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHGetPropertyStoreFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc, // IBindCtx*, opcional
            int flags,  // GETPROPERTYSTOREFLAGS
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

        // IShellItem (shell32.dll) - para uso futuro si se necesita
        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(int sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        // SHCreateItemFromParsingName (shell32.dll)
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        // IShellItem.GetPropertyStore usa BHID_PropertyStore = {886d8eeb-8cf2-4446-8d02-cdba1dbdcf99}
        private static readonly Guid BHID_PropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

        private const int GPS_DEFAULT = 0;
        private const int GPS_HANDLERPROPERTIESONLY = 0x1;
        private const int GPS_READWRITE = 0x2;
        private const int GPS_TEMPORARY = 0x4;
        private const int GPS_FASTPROPERTIESONLY = 0x8;
        private const int GPS_OPENSLOWITEM = 0x10;
        private const int GPS_DELAYCREATION = 0x20;
        private const int GPS_BESTEFFORT = 0x40;
        private const int GPS_NO_OPLOCK = 0x80;
        private const int GPS_PREFERQUERYPROPERTIES = 0x100;
        private const int GPS_EXTRINSICPROPERTIES = 0x200;

// Escribe metadatos: WAV -> ATL (ID3v2.3 + LIST/INFO nativo), FLAC -> ATL (Vorbis nativo)
        // ATL es la librería probada: mantiene el chunk data intacto y no corrompe el RIFF.
        private static void WriteMetadataViaPropertyStore(string originalFilePath, Track source)
        {
            bool isWav = Path.GetExtension(originalFilePath).Equals(".wav", StringComparison.OrdinalIgnoreCase);
            string safePath = GetSafePathForNativeLibraries(originalFilePath);

            if (isWav)
            {
                // WAV: forzar ID3v2.3 (estándar conpatible)
                try
                {
                    Settings.ID3v2_tagSubVersion = 3;
                    var target = new Track(safePath);
                    CopyFieldsToTag(target, source);
                    target.Save(ATL.AudioData.MetaDataIOFactory.TagType.ID3V2);
                    System.Diagnostics.Debug.WriteLine($"[MetadataService] ATL ID3v2.3 guardado: {originalFilePath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MetadataService] ATL ID3v2 falló: {ex.Message}");
                }

                // WAV: LIST/INFO nativo para Windows Explorer (best-effort, no debe romper el archivo)
                try
                {
                    var native = new Track(safePath);
                    CopyFieldsToTag(native, source);
                    native.Save(ATL.AudioData.MetaDataIOFactory.TagType.NATIVE);
                    System.Diagnostics.Debug.WriteLine($"[MetadataService] ATL LIST/INFO guardado: {originalFilePath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MetadataService] ATL LIST/INFO falló: {ex.Message}");
                }

                // Windows Explorer solo muestra los tags LIST/INFO de un WAV si el chunk data
                // aparece ANTES que el chunk LIST. ATL los ordena al revés y Explorer queda vacío.
                try
                {
                    ReorderWavForWindowsExplorer(originalFilePath, safePath);
                    System.Diagnostics.Debug.WriteLine($"[MetadataService] WAV reordenado para Explorer: {originalFilePath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MetadataService] Reorden WAV falló: {ex.Message}");
                }
            }
            else
            {
                // FLAC: ATL escribe Vorbis nativo + Picture blocks
                var target = new Track(safePath);
                CopyFieldsToTag(target, source);

                target.EmbeddedPictures.Clear();
                foreach (var pic in source.EmbeddedPictures)
                {
                    pic.PicType = PictureInfo.PIC_TYPE.Front;
                    target.EmbeddedPictures.Add(pic);
                }
                target.Save();
                System.Diagnostics.Debug.WriteLine($"[MetadataService] ATL Vorbis guardado: {originalFilePath}");
            }
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
            target.Lyrics = source.Lyrics;
        }

        // Reordena los chunks de un WAV para que el chunk data quede ANTES de los chunks
        // de metadatos (LIST/INFO e id3). Windows Explorer exige este orden para mostrar
        // los tags en Propiedades -> Detalles; ATL inserta los tags antes de data.
        // Reconstruye el archivo en un temp y lo reemplaza si el reorden es necesario.
        private static void ReorderWavForWindowsExplorer(string originalFilePath, string safePath)
        {
            string tmpPath = safePath + ".reorder." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var outFs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write))
                {
                    byte[] riff = new byte[12];
                    if (fs.Read(riff, 0, 12) != 12) return;
                    if (Encoding.ASCII.GetString(riff, 0, 4) != "RIFF" ||
                        Encoding.ASCII.GetString(riff, 8, 4) != "WAVE") return;

                    var tagsBeforeData = new List<(string id, long offset, int size)>();
                    var dataChunk = (id: "", offset: 0L, size: 0);
                    var fmtChunk = (id: "", offset: 0L, size: 0);
                    bool hasFmt = false, hasData = false;
                    long pos = 12;

                    while (pos + 8 <= fs.Length)
                    {
                        fs.Position = pos;
                        byte[] h = new byte[8];
                        int r = fs.Read(h, 0, 8);
                        if (r != 8) break;
                        int sz = BitConverter.ToInt32(h, 4);
                        if (sz < 0 || pos + 8 + sz > fs.Length + 4) break;
                        string id = Encoding.ASCII.GetString(h, 0, 4);
                        if (id == "fmt ") { fmtChunk = (id, pos, sz); hasFmt = true; }
                        else if (id == "data") { dataChunk = (id, pos, sz); hasData = true; }
                        else if (!hasData && (id == "LIST" || id == "id3 " || id == "ID3 " || id == "ID3"))
                        {
                            tagsBeforeData.Add((id, pos, sz));
                        }
                        pos += 8 + sz + (sz & 1);
                    }

                    if (!hasFmt || !hasData) return;

                    // Solo reordenar si hay tags (LIST/id3) ANTES del chunk data, que es lo que
                    // ATL genera y lo que Windows Explorer no sabe leer en ese orden.
                    if (tagsBeforeData.Count == 0) return;

                    // Reconstruir: RIFF + fmt + data + resto (incluye los tags movidos al final)
                    byte[] riffHeader = new byte[12];
                    riffHeader[0] = (byte)'R'; riffHeader[1] = (byte)'I'; riffHeader[2] = (byte)'F'; riffHeader[3] = (byte)'F';
                    Array.Copy(BitConverter.GetBytes(0), 0, riffHeader, 4, 4); // tamaño en bytes se corrige al final
                    riffHeader[8] = (byte)'W'; riffHeader[9] = (byte)'A'; riffHeader[10] = (byte)'V'; riffHeader[11] = (byte)'E';
                    outFs.Write(riffHeader, 0, 12);

                    // fmt
                    fs.Position = fmtChunk.offset;
                    CopyChunk(fs, outFs, fmtChunk.size);

                    // data
                    fs.Position = dataChunk.offset;
                    CopyChunk(fs, outFs, dataChunk.size);

                    // resto de chunks en orden original (incluye LIST/INFO e id3 que ATL puso antes de data)
                    pos = 12;
                    while (pos + 8 <= fs.Length)
                    {
                        fs.Position = pos;
                        byte[] h = new byte[8];
                        int r = fs.Read(h, 0, 8);
                        if (r != 8) break;
                        int sz = BitConverter.ToInt32(h, 4);
                        if (sz < 0) break;
                        string id = Encoding.ASCII.GetString(h, 0, 4);
                        if (id == "fmt " || id == "data") { pos += 8 + sz + (sz & 1); continue; }
                        fs.Position = pos;
                        CopyChunk(fs, outFs, sz);
                        pos += 8 + sz + (sz & 1);
                    }

                    long total = outFs.Length;
                    byte[] sizeBytes = BitConverter.GetBytes((int)(total - 8));
                    outFs.Position = 4;
                    outFs.Write(sizeBytes, 0, 4);
                    outFs.Flush();
                }

                // Reemplazar el original por el reordenado, SIEMPRE volviendo al nombre largo
                // original (no a la ruta 8.3, que dejaría el archivo con nombre corto literal).
                if (File.Exists(originalFilePath))
                {
                    File.Delete(originalFilePath);
                }
                File.Move(tmpPath, originalFilePath);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }

        private static void CopyChunk(FileStream src, FileStream dst, int size)
        {
            byte[] h = new byte[8];
            int r = src.Read(h, 0, 8);
            if (r != 8) return;
            dst.Write(h, 0, 8);
            int remaining = size;
            byte[] buf = new byte[65536];
            while (remaining > 0)
            {
                int n = src.Read(buf, 0, Math.Min(buf.Length, remaining));
                if (n <= 0) break;
                dst.Write(buf, 0, n);
                remaining -= n;
            }
            if ((size & 1) == 1) dst.WriteByte(0); // padding para alinear a word
        }

        // ATL escribe los subchunks LIST/INFO de un WAV en UTF-8, pero Windows Explorer (y ATL
        // mismo al releer) los interpretan como ANSI (página de códigos 1252). Esto convierte el
        // texto de cada subchunk INFO a 1252, recorriendo todos los chunks y reconstruyendo el
        // archivo solo si algún valor cambió (los tamaños pueden variar al cambiar de encoding).
        private static void FixWavInfoToAnsi(string originalFilePath, string safePath)
        {
            string tmpPath = safePath + ".fixinfo." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var outFs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write))
                {
                    byte[] riff = new byte[12];
                    if (fs.Read(riff, 0, 12) != 12) return;
                    if (Encoding.ASCII.GetString(riff, 0, 4) != "RIFF" ||
                        Encoding.ASCII.GetString(riff, 8, 4) != "WAVE") return;

                    // Header RIFF: tamaño se corrige al final
                    byte[] riffHeader = new byte[12];
                    riffHeader[0] = (byte)'R'; riffHeader[1] = (byte)'I'; riffHeader[2] = (byte)'F'; riffHeader[3] = (byte)'F';
                    Array.Copy(BitConverter.GetBytes(0), 0, riffHeader, 4, 4);
                    riffHeader[8] = (byte)'W'; riffHeader[9] = (byte)'A'; riffHeader[10] = (byte)'V'; riffHeader[11] = (byte)'E';
                    outFs.Write(riffHeader, 0, 12);

                    bool anyChanged = false;
                    long pos = 12;
                    while (pos + 8 <= fs.Length)
                    {
                        fs.Position = pos;
                        byte[] h = new byte[8];
                        int r = fs.Read(h, 0, 8);
                        if (r != 8) break;
                        int sz = BitConverter.ToInt32(h, 4);
                        if (sz < 0 || pos + 8 + sz > fs.Length + 4) break;
                        string id = Encoding.ASCII.GetString(h, 0, 4);

                        if (id == "LIST" && sz >= 4)
                        {
                            // LIST: el payload arranca con un id de 4 bytes (p.ej. "INFO")
                            byte[] payload = new byte[sz];
                            fs.Position = pos + 8;
                            int read = 0;
                            while (read < payload.Length)
                            {
                                int n = fs.Read(payload, read, payload.Length - read);
                                if (n <= 0) break;
                                read += n;
                            }
                            if (read != payload.Length) break;

                            string listType = Encoding.ASCII.GetString(payload, 0, 4);
                            if (listType == "INFO")
                            {
                                byte[] newPayload = ReencodeInfoSubchunksToAnsi(payload, ref anyChanged);
                                if (newPayload != null)
                                {
                                    Array.Copy(Encoding.ASCII.GetBytes("LIST"), 0, h, 0, 4);
                                    Array.Copy(BitConverter.GetBytes(newPayload.Length), 0, h, 4, 4);
                                    outFs.Write(h, 0, 8);
                                    outFs.Write(newPayload, 0, newPayload.Length);
                                    if ((newPayload.Length & 1) == 1) outFs.WriteByte(0);
                                }
                                else
                                {
                                    Array.Copy(Encoding.ASCII.GetBytes("LIST"), 0, h, 0, 4);
                                    Array.Copy(BitConverter.GetBytes(payload.Length), 0, h, 4, 4);
                                    outFs.Write(h, 0, 8);
                                    outFs.Write(payload, 0, payload.Length);
                                    if ((payload.Length & 1) == 1) outFs.WriteByte(0);
                                }
                            }
                            else
                            {
                                fs.Position = pos;
                                CopyChunk(fs, outFs, sz);
                            }
                        }
                        else
                        {
                            fs.Position = pos;
                            CopyChunk(fs, outFs, sz);
                        }
                        pos += 8 + sz + (sz & 1);
                    }

                    long total = outFs.Length;
                    byte[] sizeBytes = BitConverter.GetBytes((int)(total - 8));
                    outFs.Position = 4;
                    outFs.Write(sizeBytes, 0, 4);
                    outFs.Flush();

                    if (anyChanged)
                    {
                        // Voltaje al nombre largo original
                        if (File.Exists(originalFilePath)) File.Delete(originalFilePath);
                        File.Move(tmpPath, originalFilePath);
                        tmpPath = null; // ya movido
                    }
                }
            }
            finally
            {
                if (tmpPath != null && File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }

        // Convierte los subchunks INFO de UTF-8 a ANSI/1252. Devuelve un nuevo payload si algo
        // cambió, o null si no hubo cambios (byte a byte idénticos). Mantiene el orden.
        private static byte[] ReencodeInfoSubchunksToAnsi(byte[] payload, ref bool anyChanged)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var strictUtf8 = new UTF8Encoding(false, true);
            var ansi = Encoding.GetEncoding(1252);

            var ms = new MemoryStream();
            ms.Write(payload, 0, 4); // "INFO"
            long pos = 4;
            bool localChanged = false;
            while (pos + 8 <= payload.Length)
            {
                int subSz = BitConverter.ToInt32(payload, (int)pos + 4);
                if (subSz < 0 || pos + 8 + subSz > payload.Length) break;
                byte[] value = new byte[subSz];
                Array.Copy(payload, (int)pos + 8, value, 0, subSz);

                // Intentar leer como UTF-8 estricto; si no es UTF-8 válido, se asume que ya es
                // ANSI/Latin-1 y se deja intacto.
                string text = null;
                try { text = strictUtf8.GetString(value); }
                catch (DecoderFallbackException) { }

                if (text != null && text.Length > 0)
                {
                    // Solo si todos los caracteres son representables en 1252 (accentos, ñ, ©…)
                    bool representable = text.All(c => c <= 0xFF);
                    if (representable)
                    {
                        try
                        {
                            byte[] reencoded = ansi.GetBytes(text);
                            if (!reencoded.SequenceEqual(value))
                            {
                                byte[] idBytes = new byte[4];
                                Array.Copy(payload, (int)pos, idBytes, 0, 4);
                                ms.Write(idBytes, 0, 4);
                                ms.Write(BitConverter.GetBytes(reencoded.Length), 0, 4);
                                ms.Write(reencoded, 0, reencoded.Length);
                                if ((reencoded.Length & 1) == 1) ms.WriteByte(0);
                                localChanged = true;
                                anyChanged = true;
                                pos += 8 + subSz + (subSz & 1);
                                continue;
                            }
                        }
                        catch (EncoderFallbackException) { }
                    }
                }

                // Sin cambios: copiar el subchunk original completo
                ms.Write(payload, (int)pos + 0, 8 + subSz);
                if ((subSz & 1) == 1) ms.WriteByte(0);
                pos += 8 + subSz + (subSz & 1);
            }
            return localChanged ? ms.ToArray() : null;
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
                || (track.Lyrics is { Count: > 0 })
                || !string.IsNullOrWhiteSpace(track.ISRC)
                || !string.IsNullOrWhiteSpace(track.Copyright);

            bool numericField = track.Year != 0
                || track.TrackNumber != 0
                || track.DiscNumber != 0;

            return textField || numericField || track.EmbeddedPictures.Count > 0;
        }

        // PASO 3: Clonación de metadatos + carátula
        // Metadatos -> Windows Property System (IPropertyStore) nativo (WAV, FLAC, MP3, etc.)
        // Carátula -> ATL (única forma fiable de embeber Picture blocks en FLAC/WAV)
        // Si el archivo ORIGINAL no tiene tags, se omite la escritura por completo
        public static void CloneMetadataAndCover(string inputPath, string outputPath)
        {
            if (!File.Exists(inputPath) || !File.Exists(outputPath)) return;

            try
            {
                // 1. Leer metadatos origen con ATL (soporta todos los formatos)
                string safeInputPath = GetSafePathForNativeLibraries(inputPath);
                Track source = new Track(safeInputPath);

                // 2. Si el origen no tiene metadatos significativos, no escribir nada
                if (!HasMeaningfulTags(source, inputPath, safeInputPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[MetadataService] Origen sin tags significativos, omitiendo clonado de metadatos: {Path.GetFileName(inputPath)}");
                    return;
                }

                // 2. Escribir metadatos básicos via Windows Property System (IPropertyStore)
                // Lo que lee el Explorador de Windows — usa ruta ORIGINAL (no 8.3)
                WriteMetadataViaPropertyStore(outputPath, source);

                // 3. Carátula -> ATL (Picture blocks en FLAC/WAV)
                // Windows Property System no maneja bien imágenes embebidas
                string safeOutputPath = GetSafePathForNativeLibraries(outputPath);
                var target = new Track(safeOutputPath);
                target.EmbeddedPictures.Clear();
                foreach (var pic in source.EmbeddedPictures)
                {
                    pic.PicType = PictureInfo.PIC_TYPE.Front;
                    target.EmbeddedPictures.Add(pic);
                }
                target.Save(); // Solo guarda la carátula (y mantiene los metadatos ya escritos)

                // 4. ATL escribe los subchunks LIST/INFO del WAV en UTF-8, pero Windows Explorer
                //    (y el propio ATL al releer) los interpretan como ANSI/Latin-1. Re-codificarlos
                //    a la página de códigos 1252 para que los acentos se muestren correctamente.
                try
                {
                    FixWavInfoToAnsi(outputPath, safeOutputPath);
                    System.Diagnostics.Debug.WriteLine($"[MetadataService] LIST/INFO re-codificado a ANSI: {outputPath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MetadataService] Re-codificado LIST/INFO falló: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error crítico en el módulo de metadatos: {ex.Message}");
                throw;
            }
        }
    }
}
