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


        // Estructura para recalcular coeficientes dinámicos según el Sample Rate real del archivo
        private class KWeightingFilter
        {
            private double b0_hp, b1_hp, b2_hp, a1_hp, a2_hp;
            private double b0_sh, b1_sh, b2_sh, a1_sh, a2_sh;
            private double[,] w_hp;
            private double[,] w_sh;

            public KWeightingFilter(int sampleRate, int channels)
            {
                w_hp = new double[channels, 2];
                w_sh = new double[channels, 2];

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

            public float ProcessSample(float sample, int channel)
            {
                double x = sample;
                double y_hp = b0_hp * x + b1_hp * w_hp[channel, 0] + b2_hp * w_hp[channel, 1] - a1_hp * w_hp[channel, 0] - a2_hp * w_hp[channel, 1];
                w_hp[channel, 1] = w_hp[channel, 0]; w_hp[channel, 0] = x;

                double y_sh = b0_sh * y_hp + b1_sh * w_sh[channel, 0] + b2_sh * w_sh[channel, 1] - a1_sh * w_sh[channel, 0] - a2_sh * w_sh[channel, 1];
                w_sh[channel, 1] = w_sh[channel, 0]; w_sh[channel, 0] = y_hp;

                return (float)y_sh;
            }
        }


        // PASADA 1: Análisis lineal K-Weighting con cálculo de sonoridad integrada LUFS y pico máximo
        public AudioInfo AnalyzeAudioFile(string filePath)
        {
            try
            {
                var info = new AudioInfo();
                if (!System.IO.File.Exists(filePath)) return info;

                // Abrimos el archivo mediante un FileStream binario nativo de .NET (Blindado contra tildes/eñes)
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // Pasamos el Stream seguro al decodificador unificado de NAudio.SoundFile
                    using (var reader = new SoundFileReader(fileStream))
                    {
                        var waveFormat = reader.WaveFormat;
                        info.DurationFormatted = reader.TotalTime.ToString(reader.TotalTime.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");

                        var sampleProvider = reader.ToSampleProvider();
                        var filter = new KWeightingFilter(waveFormat.SampleRate, waveFormat.Channels);

                        int blockSizeSamples = (int)(0.4 * waveFormat.SampleRate) * waveFormat.Channels;

                        // CORRECCIÓN: Unificamos el nombre a floatBuffer para que compile correctamente
                        float[] floatBuffer = new float[waveFormat.SampleRate * waveFormat.Channels];

                        float maxSample = 0f;
                        double totalGatedEnergy = 0.0;
                        long totalGatedSamples = 0;

                        double currentBlockEnergy = 0.0;
                        int currentBlockSampleCount = 0;
                        int samplesRead;

                        // El bucle ahora lee correctamente usando la variable unificada
                        while ((samplesRead = sampleProvider.Read(floatBuffer.AsSpan())) > 0)
                        {
                            for (int i = 0; i < samplesRead; i++)
                            {
                                float rawSample = floatBuffer[i];
                                float absSample = Math.Abs(rawSample);

                                if (absSample > maxSample) maxSample = absSample;

                                int channel = i % waveFormat.Channels;
                                float filteredSample = filter.ProcessSample(rawSample, channel);

                                currentBlockEnergy += (filteredSample * filteredSample);
                                currentBlockSampleCount++;

                                if (currentBlockSampleCount >= blockSizeSamples)
                                {
                                    double rms = Math.Sqrt(currentBlockEnergy / currentBlockSampleCount);
                                    float blockLufs = rms > 0.0 ? (20f * (float)Math.Log10(rms) + 3.01f) : -70f;

                                    if (blockLufs > -70f)
                                    {
                                        totalGatedEnergy += currentBlockEnergy;
                                        totalGatedSamples += currentBlockSampleCount;
                                    }

                                    currentBlockEnergy = 0.0;
                                    currentBlockSampleCount = 0;
                                }
                            }
                        }

                        if (currentBlockSampleCount > 0)
                        {
                            double rms = Math.Sqrt(currentBlockEnergy / currentBlockSampleCount);
                            float blockLufs = rms > 0.0 ? (20f * (float)Math.Log10(rms) + 3.01f) : -70f;
                            if (blockLufs > -70f)
                            {
                                totalGatedEnergy += currentBlockEnergy;
                                totalGatedSamples += currentBlockSampleCount;
                            }
                        }

                        info.MaxPeakLinear = maxSample;
                        info.PeakDbFormatted = maxSample > 0f ? $"{(20f * Math.Log10(maxSample)):F2} dBFS" : "-oo dBFS";

                        if (totalGatedSamples > 0 && totalGatedEnergy > 0)
                        {
                            double rmsIntegrated = Math.Sqrt(totalGatedEnergy / totalGatedSamples);
                            float finalLufs = 20f * (float)Math.Log10(rmsIntegrated) + 3.01f;

                            info.IntegratedLoudness = finalLufs;
                            info.LoudnessFormatted = $"{finalLufs:F1} LUFS";
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error analítico del motor: {ex.Message}");
                throw;
            }
        }

        // PASADA 2: Renderizado físico del limitador Soft-Limiter utilizando firmas válidas de 1 solo argumento string
        public void ApplyNormalizationWithLimiter(string inputPath, string outputPath, float gainDb, float maxPeakLimitDb, float releaseMs = 40f, float lookAheadMs = 5f)
        {
            float linearGain = (float)Math.Pow(10, gainDb / 20.0);
            float ceilingLinear = (float)Math.Pow(10, maxPeakLimitDb / 20.0);

            string safeInputPath = GetSafePathForNativeLibraries(inputPath);
            string safeOutputPath = GetSafePathForNativeLibraries(outputPath);

            using (var reader = new SoundFileReader(safeInputPath))
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

                using (var writer = new SoundFileWriter(safeOutputPath, waveFormat,
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
        }

        public float CalculateTargetGain(float currentLoudness, float targetLufs)
        {
            float gainNeeded = targetLufs - currentLoudness;
            if (Math.Abs(gainNeeded) < 0.1f) return 0f;
            return gainNeeded;
        }

        // PASO 3: Clonación profunda automatizada mediante rutas cortas seguras de ATL
        public void CloneMetadataAndCover(string inputPath, string outputPath)
        {
            if (!File.Exists(inputPath) || !File.Exists(outputPath)) return;

            try
            {
                // Traducimos las rutas largas a cadenas de formato corto seguro 8.3
                string safeInputPath = GetSafePathForNativeLibraries(inputPath);
                string safeOutputPath = GetSafePathForNativeLibraries(outputPath);

                // Abrir archivo fuente de manera segura
                Track source = new Track(safeInputPath);

                // Abrir archivo destino de manera segura
                Track target = new Track(safeOutputPath);

                // Copiar campos estándar
                target.Title = source.Title;
                target.Artist = source.Artist;
                target.Album = source.Album;
                target.Year = source.Year;
                target.Genre = source.Genre;
                target.Comment = source.Comment;
                target.TrackNumber = source.TrackNumber;
                target.DiscNumber = source.DiscNumber;
                target.Composer = source.Composer;
                target.Conductor = source.Conductor;
                target.OriginalArtist = source.OriginalArtist;
                target.OriginalAlbum = source.OriginalAlbum;
                target.Publisher = source.Publisher;
                target.Copyright = source.Copyright;
                target.Lyrics = source.Lyrics;

                // Copiar carátulas forzando el tipo Front
                target.EmbeddedPictures.Clear();
                foreach (var pic in source.EmbeddedPictures)
                {
                    pic.PicType = PictureInfo.PIC_TYPE.Front; // Sincronización de tu enum correcto
                    target.EmbeddedPictures.Add(pic);
                }

                // Copiar cualquier campo personalizado extendido (ISRC, etc.)
                target.AdditionalFields.Clear();
                foreach (var kv in source.AdditionalFields)
                {
                    target.AdditionalFields[kv.Key] = kv.Value;
                }

                // Guardar cambios recalculando cabeceras de forma nativa
                target.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error crítico en el módulo de metadatos ATL: {ex.Message}");
                throw;
            }
        }
    }

}