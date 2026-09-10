using ATL;
using NAudio.SoundFile;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using TFile = TagLib.File;

namespace ElysiumAudio.Services
{

    public class AudioInfo
    {
        public string DurationFormatted { get; set; } = "00:00";
        public string PeakDbFormatted { get; set; } = "0.0 dBFS";
        public string LoudnessFormatted { get; set; } = "-0.0 LUFS";
        public float MaxPeakLinear { get; set; } = 0f;
        public float IntegratedLoudness { get; set; } = -70f; // Sonoridad estimada
    }

    public class AudioEngineService
    {

        // INTEROP NATIVO: Invoca la API de Windows que traduce rutas largas con tildes/eñes
        // a rutas cortas en formato MS-DOS 8.3 (ej: "D:\Música\Canción.wav" -> "D:\MSICA~1\CANCN~1.WAV")
        // Este formato ASCII puro es 100% digerible por los motores C++ de libsndfile y ATL
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

        private string GetSafePathForNativeLibraries(string longPath)
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
        private void WriteMetadataViaPropertyStore(string originalFilePath, Track source)
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
                    System.Diagnostics.Debug.WriteLine($"[AudioEngine] ATL ID3v2.3 guardado: {originalFilePath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioEngine] ATL ID3v2 falló: {ex.Message}");
                }

                // WAV: LIST/INFO nativo para Windows Explorer (best-effort, no debe romper el archivo)
                try
                {
                    var native = new Track(safePath);
                    CopyFieldsToTag(native, source);
                    native.Save(ATL.AudioData.MetaDataIOFactory.TagType.NATIVE);
                    System.Diagnostics.Debug.WriteLine($"[AudioEngine] ATL LIST/INFO guardado: {originalFilePath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioEngine] ATL LIST/INFO falló: {ex.Message}");
                }

                // Windows Explorer solo muestra los tags LIST/INFO de un WAV si el chunk data
                // aparece ANTES que el chunk LIST. ATL los ordena al revés y Explorer queda vacío.
                try
                {
                    ReorderWavForWindowsExplorer(originalFilePath, safePath);
                    System.Diagnostics.Debug.WriteLine($"[AudioEngine] WAV reordenado para Explorer: {originalFilePath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioEngine] Reorden WAV falló: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"[AudioEngine] ATL Vorbis guardado: {originalFilePath}");
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


        // Estructura para recalcular coeficientes dinámicos según el Sample Rate real del archivo
        private class KWeightingFilter
        {
            // Coeficientes del Filtro de Primer Paso: High Shelf (Pre-weighting)
            private double b0_sh, b1_sh, b2_sh, a1_sh, a2_sh;

            // Coeficientes del Filtro de Segundo Paso: High-Pass (RLB Filter)
            private double b0_hp, b1_hp, b2_hp, a1_hp, a2_hp;

            // Memorias de estado DF1 por canal (Direct Form I):
            //   x1/x2 = entradas retrasadas, y1/y2 = salidas retrasadas
            // La norma ITU-R BS.1770 requiere que los coeficientes a1/a2 operen
            // sobre las SALIDAS anteriores del mismo filtro.
            private double[] x1_sh, x2_sh, y1_sh, y2_sh;
            private double[] x1_hp, x2_hp, y1_hp, y2_hp;

            public KWeightingFilter(int sampleRate, int channels)
            {
                x1_sh = new double[channels];
                x2_sh = new double[channels];
                y1_sh = new double[channels];
                y2_sh = new double[channels];

                x1_hp = new double[channels];
                x2_hp = new double[channels];
                y1_hp = new double[channels];
                y2_hp = new double[channels];

                // CARGA DE COEFICIENTES OFICIALES SEGÚN NORMA ITU-R BS.1770-4
                if (sampleRate == 44100)
                {
                    // Etapa 1: High Shelf (Pre-weighting para simular la acústica de la cabeza humana)
                    b0_sh = 1.530841230050348;
                    b1_sh = -2.650979995154729;
                    b2_sh = 1.169079079921587;
                    a1_sh = -1.663655113256020;
                    a2_sh = 0.712595428073225;

                    // Etapa 2: High-Pass (RLB Filter)
                    // Coeficientes bilineales propios de 44.1 kHz (f0=38 Hz, Q=0.5), según la norma.
                    b0_hp = 1.0;
                    b1_hp = -2.0;
                    b2_hp = 1.0;
                    a1_hp = -1.9892010416922556;
                    a2_hp = 0.98923019606738871;
                }
                else if (sampleRate == 48000)
                {
                    // Etapa 1: High Shelf (Valores extraídos textualmente del estándar de la ITU)
                    b0_sh = 1.53512485958697;
                    b1_sh = -2.69169618940638;
                    b2_sh = 1.19839281085285;
                    a1_sh = -1.69065929318241;
                    a2_sh = 0.73248077421585;

                    // Etapa 2: High-Pass (RLB)
                    b0_hp = 1.0;
                    b1_hp = -2.0;
                    b2_hp = 1.0;
                    a1_hp = -1.99004745483398;
                    a2_hp = 0.99007225036621;
                }
                else
                {
                    // Fallback analítico aproximado para Sample Rates extraños (96kHz, 192kHz, etc.)
                    // Basado en el algoritmo de transformación bilineal de la norma
                    double dbGain = 4.0;
                    double f0 = 1500.0;
                    double Q = 1.0 / Math.Sqrt(2.0);
                    double v0 = Math.Pow(10, dbGain / 20.0);
                    double k = Math.Tan(Math.PI * f0 / sampleRate);

                    double norm = 1.0 + (1.0 / Q) * k + k * k;
                    b0_sh = (v0 + (Math.Sqrt(v0) / Q) * k + k * k) / norm;
                    b1_sh = 2.0 * (k * k - v0) / norm;
                    b2_sh = (v0 - (Math.Sqrt(v0) / Q) * k + k * k) / norm;
                    a1_sh = 2.0 * (k * k - 1.0) / norm;
                    a2_sh = (1.0 - (1.0 / Q) * k + k * k) / norm;

                    double f0_hp = 38.0;
                    double Q_hp = 0.5;
                    double k_hp = Math.Tan(Math.PI * f0_hp / sampleRate);

                    double norm_hp = 1.0 + (1.0 / Q_hp) * k_hp + k_hp * k_hp;
                    b0_hp = 1.0 / norm_hp;
                    b1_hp = -2.0 / norm_hp;
                    b2_hp = 1.0 / norm_hp;
                    a1_hp = 2.0 * (k_hp * k_hp - 1.0) / norm_hp;
                    a2_hp = (1.0 - (1.0 / Q_hp) * k_hp + k_hp * k_hp) / norm_hp;
                }
            }


            public float ProcessSample(float sample, int channel)
            {
                double x = sample;

                // ETAPA 1 PRIMERO: Filtro High-Shelf (Pre-weighting)
                // DF1 correcto: a1/a2 operan sobre las salidas anteriores del propio filtro.
                double y_sh = b0_sh * x
                            + b1_sh * x1_sh[channel] + b2_sh * x2_sh[channel]
                            - a1_sh * y1_sh[channel] - a2_sh * y2_sh[channel];

                x2_sh[channel] = x1_sh[channel];
                x1_sh[channel] = x;
                y2_sh[channel] = y1_sh[channel];
                y1_sh[channel] = y_sh;

                // ETAPA 2 DESPUÉS: Filtro High-Pass (RLB Filter) aplicando sobre el resultado de la etapa 1
                double y_hp = b0_hp * y_sh
                            + b1_hp * x1_hp[channel] + b2_hp * x2_hp[channel]
                            - a1_hp * y1_hp[channel] - a2_hp * y2_hp[channel];

                x2_hp[channel] = x1_hp[channel];
                x1_hp[channel] = y_sh;
                y2_hp[channel] = y1_hp[channel];
                y1_hp[channel] = y_hp;

                return (float)y_hp;
            }
        }


        // PASADA 1: Análisis indexado ITU-R BS.1770-4 (Gating Doble + 75% Overlap Exacto)
        // Diseñado para coincidir 1:1 con los valores de medición de Youlean Loudness Meter
        public AudioInfo AnalyzeAudioFile(string filePath)
        {
            try
            {
                if (!System.IO.File.Exists(filePath)) return new AudioInfo();

                string cleanPath = ExtractFlacStream(filePath);
                bool isTemp = cleanPath != filePath;
                try
                {
                    return AnalyzeFromPath(cleanPath);
                }
                finally
                {
                    if (isTemp && File.Exists(cleanPath))
                    {
                        try { File.Delete(cleanPath); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioEngine] Error analizando '{Path.GetFileName(filePath)}': {ex.Message}");
                throw;
            }
        }

        // Detecta si el archivo tiene ID3v2 al inicio y crea una copia temporal sin él.
        // Solo se busca el marcador fLaC si la cabecera del archivo es ID3v2 ("ID3"); el
        // tamaño del tag se lee de la propia cabecera para saltar exactamente al stream FLAC.
        // Los WAV (RIFF) y los FLAC ya limpios (fLaC al inicio) se devuelven sin tocar,
        // evitando falsos positivos del marcador en datos PCM u otros binarios.
        private static string ExtractFlacStream(string filePath)
        {
            byte[] head = new byte[10];
            int headLen;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                headLen = fs.Read(head, 0, head.Length);
            }

            // fLaC al inicio (FLAC puro) o RIFF (WAV): no hay nada que limpiar
            if (headLen >= 4)
            {
                if (head[0] == 0x66 && head[1] == 0x4C && head[2] == 0x61 && head[3] == 0x43)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioEngine] Archivo ya empieza con fLaC. Sin limpieza necesaria.");
                    return filePath;
                }
                if (head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioEngine] Archivo RIFF (WAV). Sin limpieza necesaria.");
                    return filePath;
                }
            }

            // Solo se busca el stream FLAC si la cabecera es un tag ID3v2 ("ID3")
            if (headLen < 10 || head[0] != 0x49 || head[1] != 0x44 || head[2] != 0x33) // "ID3"
            {
                System.Diagnostics.Debug.WriteLine($"[AudioEngine] Sin cabecera ID3v2 en '{Path.GetFileName(filePath)}'. Usando archivo original.");
                return filePath;
            }

            // Tamaño del tag ID3v2 = 4 bytes syncsafe (bit más alto de cada byte ignorado)
            int id3Size = ((head[6] & 0x7F) << 21) | ((head[7] & 0x7F) << 14) | ((head[8] & 0x7F) << 7) | (head[9] & 0x7F);
            long flacPos = 10L + id3Size;

            // Verificar que justo después del tag ID3v2 viene el marcador fLaC
            byte[] marker = new byte[4];
            int markerLen;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(flacPos, SeekOrigin.Begin);
                markerLen = fs.Read(marker, 0, 4);
            }
            if (markerLen != 4 || marker[0] != 0x66 || marker[1] != 0x4C || marker[2] != 0x61 || marker[3] != 0x43)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioEngine] ID3v2 sin fLaC tras el tag en '{Path.GetFileName(filePath)}'. Usando archivo original.");
                return filePath;
            }

            System.Diagnostics.Debug.WriteLine($"[AudioEngine] ID3v2 de {id3Size} bytes, fLaC en offset {flacPos} (0x{flacPos:X}) de '{Path.GetFileName(filePath)}'. Extrayendo stream limpio.");

            // Crear archivo temporal con solo el stream FLAC
            string tempPath = Path.Combine(Path.GetTempPath(), "ElysiumAudio",
                "clean_" + Guid.NewGuid().ToString("N") + Path.GetExtension(filePath));

            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            using (var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                input.Seek(flacPos, SeekOrigin.Begin);
                input.CopyTo(output);
            }

            // Recortar ID3v1 final si existe (128 bytes empezando con "TAG" = 0x54 0x41 0x47)
            // libsndfile intenta decodificarlo como frame FLAC y pierde la sync.
            const int Id3v1Size = 128;
            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                long len = fs.Length;
                if (len > Id3v1Size)
                {
                    fs.Seek(-Id3v1Size, SeekOrigin.End);
                    byte[] tail = new byte[Id3v1Size];
                    fs.ReadExactly(tail, 0, Id3v1Size);
                    if (tail[0] == 0x54 && tail[1] == 0x41 && tail[2] == 0x47) // "TAG"
                    {
                        fs.SetLength(len - Id3v1Size);
                        System.Diagnostics.Debug.WriteLine($"[AudioEngine] ID3v1 detectado al final, recortados {Id3v1Size} bytes.");
                    }
                }
            }

            // Verificar
            byte[] check = new byte[4];
            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.ReadExactly(check, 0, 4);
            }
            string hex = BitConverter.ToString(check).Replace("-", " ");
            System.Diagnostics.Debug.WriteLine($"[AudioEngine] Archivo limpio empieza con: {hex}");

            return tempPath;
        }

        // Core del análisis ITU-R BS.1770-4 extraído para reutilización con retry
        private AudioInfo AnalyzeFromPath(string filePath)
        {
            var info = new AudioInfo();

            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (var reader = new SoundFileReader(fileStream))
                {
                    var waveFormat = reader.WaveFormat;
                    info.DurationFormatted = reader.TotalTime.ToString(reader.TotalTime.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");

                    var sampleProvider = reader.ToSampleProvider();
                    var filter = new KWeightingFilter(waveFormat.SampleRate, waveFormat.Channels);

                    int channels = waveFormat.Channels;
                    int sampleRate = waveFormat.SampleRate;

                    // Definiciones estrictas de la norma ITU: Ventana de 400ms, Avance de 100ms (75% traslape)
                    int blockLengthSamples = (int)(0.4 * sampleRate);
                    int hopSizeSamples = (int)(0.1 * sampleRate);

                    // Listas de muestras filtradas por canal para gestionar la ventana deslizante
                    var filteredChannels = new List<float>[channels];
                    for (int c = 0; c < channels; c++)
                    {
                        filteredChannels[c] = new List<float>(blockLengthSamples * 2);
                    }

                    var blockEnergies = new List<double>();
                    float maxSample = 0f;

                    // Búfer de lectura óptimo de NAudio basado en bloques de sample rate
                    float[] readBuffer = new float[sampleRate * channels];
                    int samplesRead;

                    while ((samplesRead = sampleProvider.Read(readBuffer.AsSpan())) > 0)
                    {
                        for (int i = 0; i < samplesRead; i += channels)
                        {
                            // 1. Separar por canales, buscar picos y aplicar K-Weighting (Filtro en cascada)
                            for (int c = 0; c < channels; c++)
                            {
                                if (i + c >= samplesRead) break;

                                float rawSample = readBuffer[i + c];
                                float absSample = Math.Abs(rawSample);
                                if (absSample > maxSample) maxSample = absSample;

                                float filteredSample = filter.ProcessSample(rawSample, c);
                                filteredChannels[c].Add(filteredSample);
                            }

                            // 2. Evaluación de la Ventana Deslizante (Se mide sobre el historial de un canal)
                            if (filteredChannels[0].Count >= blockLengthSamples)
                            {
                                double blockEnergySum = 0.0;

                                // Calcular el RMS medio por canal de forma independiente
                                for (int c = 0; c < channels; c++)
                                {
                                    double channelEnergy = 0.0;
                                    // Tomamos las muestras desde el inicio (0) hasta completar los 400ms de audio
                                    for (int s = 0; s < blockLengthSamples; s++)
                                    {
                                        float sample = filteredChannels[c][s];
                                        channelEnergy += (sample * sample);
                                    }
                                    channelEnergy /= blockLengthSamples;

                                    // Ponderación estéreo oficial (Izquierdo = 1.0, Derecho = 1.0)
                                    double channelWeight = 1.0;
                                    blockEnergySum += (channelWeight * channelEnergy);
                                }

                                // COMPUERTA 1: Gate Absoluto a -70 LUFS (Offset oficial de calibración: -0.691)
                                double blockLoudness = blockEnergySum > 0.0 ? (-0.691 + 10.0 * Math.Log10(blockEnergySum)) : -70.0;

                                if (blockLoudness > -70.0)
                                {
                                    blockEnergies.Add(blockEnergySum);
                                }

                                // Desplazamiento de la ventana: Removemos únicamente el tamaño del salto (100ms)
                                // Esto provoca que las muestras remanentes (los otros 300ms) se mantengan para el siguiente ciclo
                                for (int c = 0; c < channels; c++)
                                {
                                    filteredChannels[c].RemoveRange(0, hopSizeSamples);
                                }
                            }
                        }
                    }

                    info.MaxPeakLinear = maxSample;
                    info.PeakDbFormatted = maxSample > 0f ? $"{(20f * Math.Log10(maxSample)):F2} dBFS" : "-oo dBFS";

                    // COMPUERTA 2: Gate Relativo (-10 dB por debajo de la energía media integrada)
                    if (blockEnergies.Count > 0)
                    {
                        double averageEnergyUnGated = 0.0;
                        foreach (var energy in blockEnergies)
                        {
                            averageEnergyUnGated += energy;
                        }
                        averageEnergyUnGated /= blockEnergies.Count;

                        // Restar 10 dB en escala logarítmica es equivalente a multiplicar por 0.1 en escala lineal
                        double relativeThresholdEnergy = averageEnergyUnGated * 0.1;

                        double finalEnergySum = 0.0;
                        int gatedBlockCount = 0;

                        foreach (var energy in blockEnergies)
                        {
                            if (energy >= relativeThresholdEnergy)
                            {
                                finalEnergySum += energy;
                                gatedBlockCount++;
                            }
                        }

                        if (gatedBlockCount > 0)
                        {
                            double integratedEnergy = finalEnergySum / gatedBlockCount;
                            float finalLufs = (float)(-0.691 + 10.0 * Math.Log10(integratedEnergy));

                            if (finalLufs < -70f) finalLufs = -70f;

                            info.IntegratedLoudness = finalLufs;
                            info.LoudnessFormatted = $"{finalLufs:F1} LUFS";
                        }
                        else
                        {
                            info.IntegratedLoudness = -70f;
                            info.LoudnessFormatted = "-70.0 LUFS";
                        }
                    }
                    else
                    {
                        info.IntegratedLoudness = -70f;
                        info.LoudnessFormatted = "-70.0 LUFS";
                    }
                }
            }
            return info;
        }


        // PASADA 2: Renderizado físico del limitador Soft-Limiter
        // Procesa en un directorio temporal con nombres ASCII puros (GUID) porque
        // libsndfile (SoundFileReader/SoundFileWriter) abre por ruta con fopen()
        // estilo C y falla con tildes/eñes si los nombres cortos 8.3 están
        // deshabilitados en el disco. El archivo final se copia con .NET (File.Copy),
        // que sí soporta rutas Unicode.
        public void ApplyNormalizationWithLimiter(string inputPath, string outputPath, float gainDb, float maxPeakLimitDb, float releaseMs = 40f, float lookAheadMs = 5f)
        {
            float linearGain = (float)Math.Pow(10, gainDb / 20.0);
            float ceilingLinear = (float)Math.Pow(10, maxPeakLimitDb / 20.0);

            string tempDir = Path.Combine(Path.GetTempPath(), "ElysiumAudio", Guid.NewGuid().ToString("N"));
            string tempInput = Path.Combine(tempDir, "input" + Path.GetExtension(inputPath));
            string tempOutput = Path.Combine(tempDir, "output" + Path.GetExtension(outputPath));

            try
            {
                Directory.CreateDirectory(tempDir);

                // Usar stream FLAC limpio (sin ID3v2/ID3v1) para que libsndfile no pierda sync
                string cleanInputPath = ExtractFlacStream(inputPath);
                bool inputIsTemp = cleanInputPath != inputPath;
                if (inputIsTemp)
                {
                    File.Copy(cleanInputPath, tempInput, true);
                }
                else
                {
                    File.Copy(inputPath, tempInput, true);
                }

                using (var reader = new SoundFileReader(tempInput))
                {
                    var waveFormat = reader.WaveFormat;
                    var sampleProvider = reader.ToSampleProvider();

                    // 1. Estructura del Búfer Circular Fijo (Reemplaza la cola lenta)
                    int lookAheadSize = (int)((lookAheadMs / 1000.0) * waveFormat.SampleRate) * waveFormat.Channels;
                    if (lookAheadSize < 1) lookAheadSize = 1;

                    float[] delayBuffer = new float[lookAheadSize];
                    int writeIndex = 0;
                    int readIndex = 0;
                    int count = 0;

                    // Variables para rastrear el pico de forma instantánea sin bucles foreach
                    float currentMaxInQueue = 0f;

                    float releaseFactor = (float)Math.Exp(-1.0 / (waveFormat.SampleRate * (releaseMs / 1000.0)));
                    float currentGain = 1.0f;

                    using (var writer = new SoundFileWriter(tempOutput, waveFormat,
                        Path.GetExtension(outputPath).ToLower() == ".flac" ? SoundFileMajorFormat.Flac : SoundFileMajorFormat.Wav))
                    {
                        float[] floatBuffer = new float[waveFormat.SampleRate * waveFormat.Channels];
                        int samplesRead;

                        while ((samplesRead = sampleProvider.Read(floatBuffer.AsSpan())) > 0)
                        {
                            for (int i = 0; i < samplesRead; i++)
                            {
                                float gainAppliedSample = floatBuffer[i] * linearGain;
                                float absInput = Math.Abs(gainAppliedSample);

                                float delayedSample = 0f;

                                // 2. Gestión del Búfer Circular Indexado
                                if (count >= lookAheadSize)
                                {
                                    // Extraemos la muestra retrasada 5ms de forma instantánea
                                    delayedSample = delayBuffer[readIndex];
                                    float absLeaving = Math.Abs(delayedSample);

                                    readIndex = (readIndex + 1) % lookAheadSize;
                                    count--;

                                    // Si la muestra que sale era el pico máximo, recalculamos el nuevo pico una sola vez
                                    if (absLeaving >= currentMaxInQueue)
                                    {
                                        currentMaxInQueue = 0f;
                                        for (int j = 0; j < lookAheadSize; j++)
                                        {
                                            float absVal = Math.Abs(delayBuffer[j]);
                                            if (absVal > currentMaxInQueue) currentMaxInQueue = absVal;
                                        }
                                    }
                                }

                                // Insertar la muestra nueva en el búfer circular
                                delayBuffer[writeIndex] = gainAppliedSample;
                                writeIndex = (writeIndex + 1) % lookAheadSize;
                                count++;

                                // Evaluar de inmediato si la muestra entrante es el nuevo pico máximo del futuro
                                if (absInput > currentMaxInQueue)
                                {
                                    currentMaxInQueue = absInput;
                                }

                                // 3. Cálculo de la Atenuación Predictiva Instantánea
                                float targetGain = 1.0f;
                                if (currentMaxInQueue > ceilingLinear)
                                {
                                    targetGain = ceilingLinear / currentMaxInQueue;
                                }

                                // Aplicar envolvente del limitador (Ataque predictivo / Liberación suave)
                                if (targetGain < currentGain)
                                {
                                    currentGain = targetGain;
                                }
                                else
                                {
                                    currentGain = releaseFactor * currentGain + (1.0f - releaseFactor) * targetGain;
                                }

                                // Mapear la muestra procesada al búfer de salida
                                floatBuffer[i] = delayedSample * currentGain;
                            }

                            writer.WriteSamples(floatBuffer.AsSpan(0, samplesRead));
                        }

                        // 4. Vaciado Rápido del Búfer Circular al terminar la canción
                        while (count > 0)
                        {
                            float delayedSample = delayBuffer[readIndex];
                            readIndex = (readIndex + 1) % lookAheadSize;
                            count--;

                            float[] flushBuffer = new float[] { delayedSample * currentGain };
                            writer.WriteSamples(flushBuffer.AsSpan());
                        }
                    }
                }

                // Limpiar el temp del stream extraído si lo creamos
                if (inputIsTemp && File.Exists(cleanInputPath))
                {
                    try { File.Delete(cleanInputPath); } catch { }
                }

                // Copiar el resultado final a la ruta destino (admite tildes/eñes)
                string outputDir = Path.GetDirectoryName(outputPath) ?? "";
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }
                File.Copy(tempOutput, outputPath, true);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { /* La limpieza falló; no bloquea el flujo principal */ }
            }
        }

        public float CalculateTargetGain(float currentLoudness, float targetLufs)
        {
            float gainNeeded = targetLufs - currentLoudness;
            if (Math.Abs(gainNeeded) < 0.1f) return 0f;
            return gainNeeded;
        }

        // PASO 3: Clonación de metadatos + carátula
        // Metadatos -> Windows Property System (IPropertyStore) nativo (WAV, FLAC, MP3, etc.)
        // Carátula -> ATL (única forma fiable de embeber Picture blocks en FLAC/WAV)
        public void CloneMetadataAndCover(string inputPath, string outputPath)
        {
            if (!File.Exists(inputPath) || !File.Exists(outputPath)) return;

            try
            {
                // 1. Leer metadatos origen con ATL (soporta todos los formatos)
                string safeInputPath = GetSafePathForNativeLibraries(inputPath);
                Track source = new Track(safeInputPath);

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
                    System.Diagnostics.Debug.WriteLine($"[AudioEngine] LIST/INFO re-codificado a ANSI: {outputPath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioEngine] Re-codificado LIST/INFO falló: {ex.Message}");
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