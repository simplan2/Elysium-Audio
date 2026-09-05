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

                    // Etapa 2: High-Pass (Filtro RLB para simular la insensibilidad a bajas frecuencias)
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
                var info = new AudioInfo();
                if (!System.IO.File.Exists(filePath)) return info;

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