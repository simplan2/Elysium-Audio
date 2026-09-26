using NAudio.SoundFile;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ElysiumAudio.Services
{

    public class AudioInfo
    {
        public string DurationFormatted { get; set; } = "00:00";
        public string TruePeakDbFormatted { get; set; } = "-oo dBTP";
        public string LoudnessFormatted { get; set; } = "-0.0 LUFS";
        public float MaxTruePeakLinear { get; set; } = 0f;
        public float IntegratedLoudness { get; set; } = -70f; // Sonoridad estimada
        public int SampleRate { get; set; } = 0;
        public int Channels { get; set; } = 0;
    }

    public class AudioEngineService
    {

        // =====================================================================
        // SegmentStream: lee un segmento de un archivo sin crear copias.
        // Usado para analizar/normalizar FLAC con ID3v2 al inicio o ID3v1 al final,
        // saltando los tags sin tocar el disco.
        // =====================================================================
        private class SegmentStream : Stream
        {
            private readonly Stream _base;
            private readonly long _offset;
            private readonly long _length;
            private long _position;

            public SegmentStream(Stream baseStream, long offset, long length)
            {
                _base = baseStream;
                _offset = offset;
                _length = length;
                _position = 0;
            }

            public override long Length => _length;
            public override long Position { get => _position; set => _position = value; }
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _length) return 0;
                long remaining = _length - _position;
                if (count > remaining) count = (int)remaining;
                _base.Seek(_offset + _position, SeekOrigin.Begin);
                int read = _base.Read(buffer, offset, count);
                _position += read;
                return read;
            }

            public override int Read(Span<byte> buffer)
            {
                if (_position >= _length) return 0;
                long remaining = _length - _position;
                int count = buffer.Length;
                if (count > remaining) count = (int)remaining;
                _base.Seek(_offset + _position, SeekOrigin.Begin);
                int read = _base.Read(buffer.Slice(0, count));
                _position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long newPos = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => _length + offset,
                    _ => _position
                };
                _position = Math.Clamp(newPos, 0, _length);
                return _position;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        }

        // Abre un archivo de audio como Stream, saltando ID3v2 al inicio e ID3v1 al final
        // sin crear copias en disco. FLAC/WAV puros se abren directamente.
        private static Stream GetAudioStream(string filePath)
        {
            byte[] head = new byte[10];
            long fileLen;
            int headLen;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fileLen = fs.Length;
                int toRead = (int)Math.Min(head.Length, fs.Length);
                fs.ReadExactly(head, 0, toRead);
                headLen = toRead;
            }

            // fLaC al inicio (FLAC puro) o RIFF (WAV): sin tags que saltar
            if (headLen >= 4)
            {
                if ((head[0] == 0x66 && head[1] == 0x4C && head[2] == 0x61 && head[3] == 0x43) ||
                    (head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46))
                {
                    return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            }

            // No empieza con ID3: abrir tal cual
            if (headLen < 10 || head[0] != 0x49 || head[1] != 0x44 || head[2] != 0x33)
            {
                return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            // ID3v2 al inicio: calcular donde empieza el stream FLAC
            int id3Size = ((head[6] & 0x7F) << 21) | ((head[7] & 0x7F) << 14) | ((head[8] & 0x7F) << 7) | (head[9] & 0x7F);
            long start = 10L + id3Size;

            // Verificar fLaC justo después del tag
            byte[] marker = new byte[4];
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(start, SeekOrigin.Begin);
                int markerLen = fs.Read(marker, 0, 4);
                if (markerLen < 4 || marker[0] != 0x66 || marker[1] != 0x4C || marker[2] != 0x61 || marker[3] != 0x43)
                {
                    // ID3v2 sin fLaC después: abrir todo (libsndfile decidirá)
                    return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            }

            // Detectar ID3v1 al final ("TAG" en los últimos 128 bytes)
            long end = fileLen;
            if (fileLen > 128)
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Seek(-128, SeekOrigin.End);
                    byte[] tail = new byte[3];
                    fs.ReadExactly(tail, 0, 3);
                    if (tail[0] == 0x54 && tail[1] == 0x41 && tail[2] == 0x47) // "TAG"
                        end = fileLen - 128;
                }
            }

            System.Diagnostics.Debug.WriteLine($"[AudioEngine] SegmentStream: start={start} end={end} ({end - start} bytes) para '{Path.GetFileName(filePath)}'");
            var baseStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new SegmentStream(baseStream, start, end - start);
        }

        // Detector de TRUE PEAK según el Anexo de BS.1770-4/5: sobre-muestreo 4x con
        // filtro interpolador FIR de fase lineal (ventana Blackman) + máximo absoluto.
        // La salida en dBTP es el pico real de la onda reconstruida, superior al sample peak.
        private class TruePeakMeter
        {
            // 48 taps/fase (192 total): diseño blackman-sinc que deja la pasabanda plana <= 0.02 dB
            // hasta el borde (20 kHz a 44.1k -> 0.011 dB de error), minimo error de medicion dBTP.
            private const int TapsPerPhase = 48;
            private const int TotalTaps = TapsPerPhase * 4;

            private readonly float[][] _phaseTaps; // [fase][tap] filtro polifásico
            private readonly float[][] _history;   // [canal][retardo] línea de retardo
            private readonly int[] _head;          // [canal] índice de la muestra más reciente

            // Buffer de trabajo lineal para los lotes SIMD (se reutiliza entre canales y llamadas
            // para no asignar por bloque) y tamaño del vector de System.Numerics.Vector<float>.
            private float[] _work = Array.Empty<float>();
            private readonly int _vecSize = Vector<float>.Count;

            public TruePeakMeter(int channels)
            {
                _phaseTaps = new float[4][];
                for (int p = 0; p < 4; p++)
                {
                    _phaseTaps[p] = new float[TapsPerPhase];
                }

                // Filtro pasa-bajos de interpolación: blackman-sinc.
                // Al interpolar x4, el cutoff debe quedar en 0.125 ciclos/muestra (0.5 del rate original/4).
                // Con inserción de ceros, cada FASE polifásica debe sumar 1 para ganancia DC unitaria;
                // por eso la normalización se hace por fase (no sobre la suma total del filtro).
                int m = TotalTaps - 1;
                const double fc = 0.125;
                double[] taps = new double[TotalTaps];
                for (int n = 0; n < TotalTaps; n++)
                {
                    double window = 0.42 - 0.5 * Math.Cos(2.0 * Math.PI * n / m) + 0.08 * Math.Cos(4.0 * Math.PI * n / m);
                    double t = n - m / 2.0;
                    double sinc = Math.Abs(t) < 1e-12 ? 1.0 : Math.Sin(2.0 * Math.PI * fc * t) / (Math.PI * t);
                    taps[n] = (2.0 * fc) * sinc * window;
                }
                for (int p = 0; p < 4; p++)
                {
                    double phaseSum = 0.0;
                    for (int n = p; n < TotalTaps; n += 4)
                    {
                        phaseSum += taps[n];
                    }
                    for (int n = p, k = 0; n < TotalTaps; n += 4, k++)
                    {
                        _phaseTaps[p][k] = (float)(taps[n] / phaseSum);
                    }
                }

                _history = new float[channels][];
                _head = new int[channels];
                for (int c = 0; c < channels; c++)
                {
                    _history[c] = new float[TapsPerPhase];
                    _head[c] = 0;
                }
            }

            // Devuelve el mayor de los 4 valores interpolados (pico en la señal sobremuestreada x4)
            public float Process(float sample, int channel)
            {
                int head = (_head[channel] + 1) % TapsPerPhase;
                _head[channel] = head;
                _history[channel][head] = sample;

                float peak = 0f;
                for (int p = 0; p < 4; p++)
                {
                    float acc = 0f;
                    float[] taps = _phaseTaps[p];
                    float[] hist = _history[channel];
                    for (int k = 0; k < TapsPerPhase; k++)
                    {
                        int idx = head - k;
                        if (idx < 0) idx += TapsPerPhase;
                        acc += taps[k] * hist[idx];
                    }
                    float abs = Math.Abs(acc);
                    if (abs > peak) peak = abs;
                }
                return peak;
            }

            // Versión batch (bloque contiguo de un canal): devuelve el pico máximo del bloque.
            // Matemáticamente idéntica a llamar Process() por muestra. Los 4 dot products se
            // vectorizan (System.Numerics.Vector<float>) sobre una ventana lineal en orden de
            // retardo creciente, sin la indexación circular con % del bucle por muestra.
            public float ProcessBlock(ReadOnlySpan<float> samples, int channel)
            {
                int n = samples.Length;
                if (n == 0) return 0f;

                float[] hist = _history[channel];
                int head = _head[channel];

                // work = [x[n-1], x[n-2], ..., x[0], x[-1], x[-2], ..., x[-48]]:
                // muestras nuevas en orden INVERTIDO y el historial con la más reciente al frente.
                // Para la salida i la ventana empieza en work[n-1-i], así que window[k] = x[i-k],
                // la causalidad que esperan los taps polifásicos.
                // (OJO: copiar las muestras en orden forward produce correlación con muestras
                // FUTURAS y resultados distintos del detector escalar. Este orden es el correcto.)
                EnsureWork(n + TapsPerPhase);
                float[] work = _work;
                for (int j = 0; j < n; j++)
                {
                    work[j] = samples[n - 1 - j];
                }
                for (int k = 0; k < TapsPerPhase; k++)
                {
                    int idx = head - k;
                    if (idx < 0) idx += TapsPerPhase;
                    work[n + k] = hist[idx];
                }

                float maxTP = 0f;
                for (int i = 0; i < n; i++)
                {
                    int start = n - 1 - i;
                    float m = Math.Abs(DotProduct(_phaseTaps[0], work, start));
                    for (int p = 1; p < 4; p++)
                    {
                        float a = Math.Abs(DotProduct(_phaseTaps[p], work, start));
                        if (a > m) m = a;
                    }
                    if (m > maxTP) maxTP = m;
                }

                UpdateHistory(samples, n, hist, ref head);
                _head[channel] = head;
                return maxTP;
            }

            // Versión batch que escribe el TP por muestra (perfil feed-forward del limitador).
            // Misma causalidad que Process(): output[i] es el pico interpolado de la muestra i,
            // sin desplazamiento temporal. Idéntica a Process() por muestra.
            public void ProcessBlockToBuffer(ReadOnlySpan<float> samples, int channel, Span<float> output)
            {
                int n = samples.Length;
                if (n == 0) return;

                float[] hist = _history[channel];
                int head = _head[channel];

                EnsureWork(n + TapsPerPhase);
                float[] work = _work;
                for (int j = 0; j < n; j++)
                {
                    work[j] = samples[n - 1 - j];
                }
                for (int k = 0; k < TapsPerPhase; k++)
                {
                    int idx = head - k;
                    if (idx < 0) idx += TapsPerPhase;
                    work[n + k] = hist[idx];
                }

                for (int i = 0; i < n; i++)
                {
                    int start = n - 1 - i;
                    float m = Math.Abs(DotProduct(_phaseTaps[0], work, start));
                    for (int p = 1; p < 4; p++)
                    {
                        float a = Math.Abs(DotProduct(_phaseTaps[p], work, start));
                        if (a > m) m = a;
                    }
                    output[i] = m;
                }

                UpdateHistory(samples, n, hist, ref head);
                _head[channel] = head;
            }

            private void EnsureWork(int needed)
            {
                if (_work.Length < needed) _work = new float[needed];
            }

            // Refleja el estado que dejaría Process() por muestra: tras n muestras el anillo queda
            // con la más reciente en head y las n más recientes en head, head-1, ...; las posiciones
            // que no se sobrescriben conservan el historial anterior (semántica de ring buffer).
            private static void UpdateHistory(ReadOnlySpan<float> samples, int n, float[] hist, ref int head)
            {
                head = (head + n) % TapsPerPhase;
                int written = Math.Min(n, TapsPerPhase);
                for (int k = 0; k < written; k++)
                {
                    int idx = head - k;
                    if (idx < 0) idx += TapsPerPhase;
                    hist[idx] = samples[n - 1 - k];
                }
            }

            // Producto punto vectorizado: Σ taps[k] * work[start+k]. TapsPerPhase (48) es múltiplo
            // de Vector<float>.Count (4 en SSE2, 8 en AVX2), así que normalmente no hay cola escalar.
            private float DotProduct(float[] taps, float[] work, int start)
            {
                Vector<float> acc = Vector<float>.Zero;
                int i = 0;
                int limit = TapsPerPhase - (TapsPerPhase % _vecSize);
                for (; i < limit; i += _vecSize)
                {
                    acc += new Vector<float>(taps, i) * new Vector<float>(work, start + i);
                }
                float sum = Vector.Dot(acc, Vector<float>.One);
                for (; i < TapsPerPhase; i++)
                {
                    sum += taps[i] * work[start + i];
                }
                return sum;
            }
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

            // Versión batch: procesa un bloque contiguo de un canal escribiendo el resultado
            // en output. Carga el estado DF1 del canal en variables locales para eliminar el
            // acceso repetido a arrays en el bucle interno. Mismas ecuaciones, mismo orden.
            public void ProcessBlock(ReadOnlySpan<float> input, int channel, Span<float> output)
            {
                double lx1_sh = x1_sh[channel], lx2_sh = x2_sh[channel];
                double ly1_sh = y1_sh[channel], ly2_sh = y2_sh[channel];
                double lx1_hp = x1_hp[channel], lx2_hp = x2_hp[channel];
                double ly1_hp = y1_hp[channel], ly2_hp = y2_hp[channel];

                for (int i = 0; i < input.Length; i++)
                {
                    double x = input[i];

                    // ETAPA 1: High-Shelf (Pre-weighting)
                    double y_sh = b0_sh * x
                                + b1_sh * lx1_sh + b2_sh * lx2_sh
                                - a1_sh * ly1_sh - a2_sh * ly2_sh;

                    lx2_sh = lx1_sh;
                    lx1_sh = x;
                    ly2_sh = ly1_sh;
                    ly1_sh = y_sh;

                    // ETAPA 2: High-Pass (RLB) sobre el resultado de la etapa 1
                    double y_hp = b0_hp * y_sh
                                + b1_hp * lx1_hp + b2_hp * lx2_hp
                                - a1_hp * ly1_hp - a2_hp * ly2_hp;

                    lx2_hp = lx1_hp;
                    lx1_hp = y_sh;
                    ly2_hp = ly1_hp;
                    ly1_hp = y_hp;

                    output[i] = (float)y_hp;
                }

                x1_sh[channel] = lx1_sh; x2_sh[channel] = lx2_sh;
                y1_sh[channel] = ly1_sh; y2_sh[channel] = ly2_sh;
                x1_hp[channel] = lx1_hp; x2_hp[channel] = lx2_hp;
                y1_hp[channel] = ly1_hp; y2_hp[channel] = ly2_hp;
            }
        }


        // Búfer circular (ring buffer) para la ventana deslizante del análisis.
        // Reemplaza List<float> + RemoveRange: "quitar" la ventana = mover solo un
        // puntero (O(1) en vez de O(n) con desplazamiento de ~13K elementos por salto).
        private class RingBuffer
        {
            private readonly float[] _buffer;
            private readonly int _mask; // capacidad = potencia de 2 -> acceso O(1) con & mask
            private int _head;
            private int _count;

            public RingBuffer(int capacity)
            {
                int cap = 1;
                while (cap < capacity) cap <<= 1;
                _buffer = new float[cap];
                _mask = cap - 1;
                _head = 0;
                _count = 0;
            }

            public int Count => _count;

            public float this[int index] => _buffer[(_head + index) & _mask];

            public void Add(float value)
            {
                _buffer[(_head + _count) & _mask] = value;
                _count++;
            }

            public void RemoveFirst(int count)
            {
                _head = (_head + count) & _mask;
                _count -= count;
            }
        }

        // SampleProvider superficial sobre un float[] en memoria (para re-análisis del
        // resultado sin tocar disco en la PASADA 3 y en la convergencia LUFS).
        private sealed class FloatSampleProvider : ISampleProvider
        {
            private readonly float[] _data;
            private int _pos;
            public WaveFormat WaveFormat { get; }

            public FloatSampleProvider(float[] data, int sampleRate, int channels)
            {
                _data = data;
                _pos = 0;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            }

            public int Read(Span<float> buffer)
            {
                int available = _data.Length - _pos;
                int toRead = Math.Min(buffer.Length, available);
                if (toRead <= 0) return 0;
                _data.AsSpan(_pos, toRead).CopyTo(buffer.Slice(0, toRead));
                _pos += toRead;
                return toRead;
            }
        }

        // PASADA 1: Análisis indexado ITU-R BS.1770-5 (Gating Doble + 75% Overlap Exacto)
        // Diseñado para coincidir 1:1 con los valores de medición de Youlean Loudness Meter
        public AudioInfo AnalyzeAudioFile(string filePath, CancellationToken ct = default)
        {
            try
            {
                if (!System.IO.File.Exists(filePath)) return new AudioInfo();
                using var stream = GetAudioStream(filePath);
                return AnalyzeFromStream(stream, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioEngine] Error analizando '{Path.GetFileName(filePath)}': {ex.Message}");
                throw;
            }
        }

        // Pesos de canal según ITU-R BS.1770-5 (Tabla de ponderación multicanal).
        // G es LINEAL sobre la potencia del canal (L_K = -0.691 + 10·log10(Σ G_i·z_i)),
        // NO se normaliza por la suma de pesos ni por el número de canales.
        // El signo ±1.41 en la norma denota fase del canal surround; |G| se usa al sumar potencias.
        private static double[] GetChannelWeights(int channels)
        {
            if (channels == 1) return new[] { 1.0 };
            if (channels == 2) return new[] { 1.0, 1.0 };
            if (channels == 6) return new[] { 1.0, 1.0, 1.0, 0.0, 1.41, 1.41 };          // 5.1 (L,R,C,LFE,SL,SR)
            if (channels == 8) return new[] { 1.0, 1.0, 1.0, 0.0, 1.41, 1.41, 1.41, 1.41 }; // 7.1 (…SL,SR,BL,BR)

            var weights = new double[channels];
            for (int i = 0; i < channels; i++) weights[i] = 1.0;
            if (channels >= 6) weights[3] = 0.0; // Fallback: canal 3 = LFE por convención ITU
            return weights;
        }

        // Core del análisis ITU-R BS.1770-5 extraído para reutilización con retry
        private AudioInfo AnalyzeFromStream(Stream stream, CancellationToken ct = default)
        {
            using (var reader = new SoundFileReader(stream))
            {
                var waveFormat = reader.WaveFormat;
                var info = AnalyzeCore(reader.ToSampleProvider(), waveFormat.SampleRate, waveFormat.Channels, ct: ct);
                info.DurationFormatted = reader.TotalTime.ToString(reader.TotalTime.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                return info;
            }
        }

        // Re-análisis de un búfer float en memoria (PASADA 3 y convergencia LUFS sin
        // tocar disco). Misma matemática que el análisis por archivo.
        public AudioInfo AnalyzeFromFloats(float[] samples, int sampleRate, int channels, bool measureTruePeak = true,
            CancellationToken ct = default)
        {
            var provider = new FloatSampleProvider(samples, sampleRate, channels);
            return AnalyzeCore(provider, sampleRate, channels, measureTruePeak, ct);
        }

        // Análisis solo de sonoridad (sin la pasada de true peak) para la verificación de
        // convergencia de la ruta a disco; el TP de salida ya lo aporta RenderLimiter en línea.
        private AudioInfo AnalyzeLoudnessOnly(string filePath, CancellationToken ct = default)
        {
            if (!System.IO.File.Exists(filePath)) return new AudioInfo();
            using var stream = GetAudioStream(filePath);
            using (var reader = new SoundFileReader(stream))
            {
                var waveFormat = reader.WaveFormat;
                var info = AnalyzeCore(reader.ToSampleProvider(), waveFormat.SampleRate, waveFormat.Channels,
                    measureTruePeak: false, ct: ct);
                info.DurationFormatted = reader.TotalTime.ToString(reader.TotalTime.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                return info;
            }
        }

        private static void ApplyOutputTruePeak(AudioInfo info, float maxOutputTp)
        {
            info.MaxTruePeakLinear = Math.Max(info.MaxTruePeakLinear, maxOutputTp);
            info.TruePeakDbFormatted = maxOutputTp > 0f ? $"{(20f * Math.Log10(maxOutputTp)):F2}" : "-oo";
        }

        // Core del análisis ITU-R BS.1770-5. Procesa por lotes (batch) por canal:
        // deinterleave -> true peak -> K-weighting -> ventana deslizante (RingBuffer).
        // Mismas ecuaciones y mismo orden de bloques que el bucle original por muestra.
        private AudioInfo AnalyzeCore(ISampleProvider sampleProvider, int sampleRate, int channels,
            bool measureTruePeak = true, CancellationToken ct = default)
        {
            var info = new AudioInfo();
            info.SampleRate = sampleRate;
            info.Channels = channels;
            var filter = new KWeightingFilter(sampleRate, channels);
            double[] channelWeights = GetChannelWeights(channels);

            // Definiciones estrictas de la norma ITU: Ventana de 400ms, Avance de 100ms (75% traslape)
            int blockLengthSamples = (int)(0.4 * sampleRate);
            int hopSizeSamples = (int)(0.1 * sampleRate);

            // Energía de la ventana deslizante por canal. Vía rápida (tasas estándar, blockLength
            // múltiplo de hop): sumatorio por segmentos de 100 ms -> O(1) por salto, manteniendo los
            // mismos bloques de 400 ms alineados a los avances de la norma. Vía de respaldo (tasas
            // exóticas): re-suma los 400 ms por salto, igual que la versión original.
            int segsPerWindow = hopSizeSamples > 0 ? blockLengthSamples / hopSizeSamples : 0;
            bool fastEnergy = segsPerWindow > 0 && blockLengthSamples == hopSizeSamples * segsPerWindow;

            var segRing = new RingBuffer[channels];
            double[] windowAcc = new double[channels];
            double[] fragSum = new double[channels];
            int[] fragCount = new int[channels];
            int[] segCount = new int[channels];
            var energyRing = new RingBuffer[channels];
            for (int c = 0; c < channels; c++)
            {
                if (fastEnergy)
                {
                    segRing[c] = new RingBuffer(segsPerWindow + 2);
                }
                else
                {
                    energyRing[c] = new RingBuffer(blockLengthSamples + sampleRate * 2);
                }
            }

            var blockEnergies = new List<double>(sampleRate);
            var truePeakMeter = measureTruePeak ? new TruePeakMeter(channels) : null;
            float maxTruePeak = 0f;

            // Búfer de lectura: 2 segundos interleaved + buffers por canal (deinterleave)
            int chunkFrames = sampleRate * 2;
            float[] readBuffer = new float[chunkFrames * channels];
            var chSamples = new float[channels][];
            var filteredOut = new float[channels][];
            for (int c = 0; c < channels; c++)
            {
                chSamples[c] = new float[chunkFrames];
                filteredOut[c] = new float[chunkFrames];
            }

            int samplesRead;
            while ((samplesRead = sampleProvider.Read(readBuffer.AsSpan())) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int frames = samplesRead / channels;

                // 1-3. Deinterleave, TRUE PEAK (lote SIMD) y K-Weighting por canal.
                for (int c = 0; c < channels; c++)
                {
                    float[] ch = chSamples[c];
                    int idx = c;
                    for (int i = 0; i < frames; i++, idx += channels)
                    {
                        ch[i] = readBuffer[idx];
                    }

                    if (measureTruePeak)
                    {
                        float blockMax = truePeakMeter!.ProcessBlock(ch.AsSpan(0, frames), c);
                        if (blockMax > maxTruePeak)
                        {
                            maxTruePeak = blockMax;
                        }
                    }

                    filter.ProcessBlock(ch.AsSpan(0, frames), c, filteredOut[c]);
                }

                // 4. Evaluación de la ventana deslizante (mismo orden de bloques que el original).
                if (fastEnergy)
                {
                    // Cada bloque de 400 ms = los últimos `segsPerWindow` segmentos de 100 ms. Por cada
                    // muestra se añade su cuadrado al segmento en curso; al completar un segmento se
                    // evalúa una ventana completa y se descarta el segmento más antiguo (O(1) por salto).
                    for (int i = 0; i < frames; i++)
                    {
                        bool completed = false;
                        for (int c = 0; c < channels; c++)
                        {
                            float s = filteredOut[c][i];
                            fragSum[c] += s * s;
                            fragCount[c]++;
                            if (fragCount[c] < hopSizeSamples) continue;

                            double seg = fragSum[c];          // segmento de 100 ms completado
                            fragSum[c] = 0.0;
                            fragCount[c] = 0;
                            segCount[c]++;
                            segRing[c].Add((float)seg);
                            windowAcc[c] += seg;

                            if (segCount[c] > segsPerWindow)
                            {
                                windowAcc[c] -= segRing[c][0]; // descarta el segmento más antiguo
                                segRing[c].RemoveFirst(1);
                            }
                            completed = true;                  // todos los canales avanzan a la vez
                        }

                        if (completed)
                        {
                            double blockEnergySum = 0.0;
                            for (int c = 0; c < channels; c++)
                            {
                                double channelEnergy = windowAcc[c] / blockLengthSamples;
                                blockEnergySum += channelWeights[c] * channelEnergy;
                            }

                            double blockLoudness = blockEnergySum > 0.0
                                ? (-0.691 + 10.0 * Math.Log10(blockEnergySum)) : -70.0;

                            if (blockLoudness > -70.0)
                            {
                                blockEnergies.Add(blockEnergySum);
                            }
                        }
                    }
                }
                else
                {
                    // Respaldo: re-sumado de los 400 ms por salto (misma matemática que el original).
                    for (int c = 0; c < channels; c++)
                    {
                        var rb = energyRing[c];
                        for (int i = 0; i < frames; i++)
                        {
                            rb.Add(filteredOut[c][i]);
                        }
                    }

                    while (energyRing[0].Count >= blockLengthSamples)
                    {
                        double blockEnergySum = 0.0;
                        for (int c = 0; c < channels; c++)
                        {
                            double channelEnergy = 0.0;
                            var rb = energyRing[c];
                            for (int s = 0; s < blockLengthSamples; s++)
                            {
                                float sample = rb[s];
                                channelEnergy += sample * sample;
                            }
                            channelEnergy /= blockLengthSamples;
                            blockEnergySum += channelWeights[c] * channelEnergy;
                        }

                        double blockLoudness = blockEnergySum > 0.0
                            ? (-0.691 + 10.0 * Math.Log10(blockEnergySum)) : -70.0;

                        if (blockLoudness > -70.0)
                        {
                            blockEnergies.Add(blockEnergySum);
                        }

                        for (int c = 0; c < channels; c++)
                        {
                            energyRing[c].RemoveFirst(hopSizeSamples);
                        }
                    }
                }
            }

            info.MaxTruePeakLinear = maxTruePeak;
            info.TruePeakDbFormatted = maxTruePeak > 0f ? $"{(20f * Math.Log10(maxTruePeak)):F2}" : "-oo";

            // COMPUERTA 2: Gate Relativo (-10 LU) ITERATIVO hasta convergencia
            if (blockEnergies.Count > 0)
            {
                double referenceEnergy = 0.0;
                foreach (var energy in blockEnergies) referenceEnergy += energy;
                referenceEnergy /= blockEnergies.Count;

                double referenceLoudness = -0.691 + 10.0 * Math.Log10(referenceEnergy);
                if (referenceLoudness < -70.0) referenceLoudness = -70.0;

                double gatedLoudness = referenceLoudness;

                for (int iter = 0; iter < 10; iter++)
                {
                    double relativeThresholdEnergy = referenceEnergy * 0.1;

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

                    if (gatedBlockCount == 0)
                    {
                        gatedLoudness = -70.0;
                        break;
                    }

                    double gatedEnergy = finalEnergySum / gatedBlockCount;
                    gatedLoudness = -0.691 + 10.0 * Math.Log10(gatedEnergy);
                    if (gatedLoudness < -70.0) gatedLoudness = -70.0;

                    if (Math.Abs(gatedLoudness - referenceLoudness) < 0.01) break;

                    referenceEnergy = gatedEnergy;
                    referenceLoudness = gatedLoudness;
                }

                info.IntegratedLoudness = (float)gatedLoudness;
                info.LoudnessFormatted = $"{gatedLoudness:F1}";
            }
            else
            {
                info.IntegratedLoudness = -70f;
                info.LoudnessFormatted = "-70.0";
            }

            return info;
        }


        // PASADA 2: Renderizado físico del limitador Soft-Limiter
        // El output se escribe en un directorio temporal con nombres ASCII puros (GUID)
        // porque libsndfile (SoundFileWriter) abre por ruta con fopen() estilo C y falla
        // con tildes/eñes si los nombres cortos 8.3 están deshabilitados en el disco.
        // El input se lee por Stream (SoundFileReader(Stream), I/O virtual) saltando
        // ID3v2/ID3v1, sin copiar archivos. El resultado final se copia con .NET
        // (File.Copy), que sí soporta rutas Unicode.
        //
        // Archivos cortos (dur <= 10 min y RAM suficiente): la PASADA 3 y la convergencia
        // LUFS se ejecutan EN MEMORIA sobre el búfer de salida (sin re-leer ni re-escribir
        // disco). Archivos largos: flujo a disco equivalente al original.
        private const float MaxInMemoryRenderSeconds = 600f;
        private const long MaxInMemoryRenderBytes = 512L * 1024 * 1024;

        // Destino del renderizado del limitador: archivo (streaming) o búfer en memoria
        private interface ISampleSink
        {
            void WriteSamples(ReadOnlySpan<float> samples);
        }

        private sealed class FileSink : ISampleSink
        {
            private readonly SoundFileWriter _writer;
            public FileSink(SoundFileWriter writer) => _writer = writer;
            public void WriteSamples(ReadOnlySpan<float> samples) => _writer.WriteSamples(samples);
        }

        private sealed class BufferSink : ISampleSink
        {
            private readonly float[] _buffer;
            private int _pos;
            public BufferSink(float[] buffer) { _buffer = buffer; _pos = 0; }
            public void WriteSamples(ReadOnlySpan<float> samples)
            {
                samples.CopyTo(_buffer.AsSpan(_pos));
                _pos += samples.Length;
            }
        }

        // Núcleo del limitador soft-limiter (lookahead + envolvente + feed-forward true peak),
        // idéntico al bucle original. Difiere solo en dos cosas:
        //   1. El destino es un ISampleSink (archivo o memoria), no el writer directo.
        //   2. El true peak feed-forward se precomputa en LOTES por canal (ProcessBlockToBuffer)
        //      con la misma matemática muestra a muestra: cero cambio en el resultado.
        // Devuelve (engaged, maxOutputTp, maxInputTp): si el limitador llegó a atenuar en algún momento,
        // el true peak estimado de la señal de salida (TP × ganancia al emitir el frame retardado;
        // solo es orientativo en transiciones) y el true peak máximo de la entrada compensada (usado
        // como cota superior segura para decidir si PASADA 3 necesita medir con exactitud: la salida
        // nunca supera a la entrada, así que si maxInputTp <= techo no hay rebase posible).
        private static (bool engaged, float maxOutputTp, float maxInputTp) RenderLimiter(ISampleProvider sampleProvider, WaveFormat waveFormat,
            float linearGain, float ceilingLinear, float releaseMs, float lookAheadMs, ISampleSink sink,
            CancellationToken ct = default)
        {
            int channels = waveFormat.Channels;
            int sampleRate = waveFormat.SampleRate;
            bool limiterEngaged = false;
            float maxOutputTp = 0f;
            float maxInputTp = 0f;

            int lookAheadSize = (int)((lookAheadMs / 1000.0) * sampleRate) * channels;
            if (lookAheadSize < 1) lookAheadSize = 1;

            float[] delayBuffer = new float[lookAheadSize];
            int writeIndex = 0, readIndex = 0, count = 0;
            float currentMaxInQueue = 0f;
            float releaseFactor = (float)Math.Exp(-1.0 / (sampleRate * (releaseMs / 1000.0)));
            float currentGain = 1.0f;

            var tpMeter = new TruePeakMeter(channels);
            int lookAheadFrames = Math.Max(1, lookAheadSize / channels);
            int tpWindow = Math.Min(48, lookAheadFrames);
            float[] tpFuture = new float[lookAheadFrames];
            float[] trailingTpMax = new float[lookAheadFrames];
            // Historial TP por frame con tamaño mayor al retardo para que la lectura del frame a
            // emitir (readFrame % H) no colisione con las escrituras de frames posteriores.
            float[] outTpH = new float[lookAheadFrames + 1];
            int[] dequeW = new int[tpWindow * 2 + 2];
            int dequeHead = 0, dequeTail = 0;
            int writeFrame = 0, readFrame = 0;

            // Lotes de 2 s interleaved + TP precomputado por canal
            int chunkFrames = sampleRate * 2;
            float[] readBuffer = new float[chunkFrames * channels];
            float[] chunkOut = new float[chunkFrames * channels];
            var gainedCh = new float[channels][];
            var tpPerCh = new float[channels][];
            for (int c = 0; c < channels; c++)
            {
                gainedCh[c] = new float[chunkFrames];
                tpPerCh[c] = new float[chunkFrames];
            }

            int samplesRead;
            while ((samplesRead = sampleProvider.Read(readBuffer.AsSpan())) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int frames = samplesRead / channels;

                // Precomputo en lote: deinterleave + ganancia + true peak por canal
                for (int c = 0; c < channels; c++)
                {
                    float[] gch = gainedCh[c];
                    int idx = c;
                    for (int i = 0; i < frames; i++, idx += channels)
                    {
                        gch[i] = readBuffer[idx] * linearGain;
                    }
                    tpMeter.ProcessBlockToBuffer(gch.AsSpan(0, frames), c, tpPerCh[c]);
                }

                int emittedInChunk = 0;
                for (int i = 0; i + channels <= samplesRead; i += channels)
                {
                    int frameIdx = i / channels;
                    float frameTpMax = 0f;
                    for (int c = 0; c < channels; c++)
                    {
                        float gainAppliedSample = readBuffer[i + c] * linearGain;
                        float absInput = Math.Abs(gainAppliedSample);
                        if (absInput > currentMaxInQueue) currentMaxInQueue = absInput;

                        float tp = tpPerCh[c][frameIdx];
                        if (tp > frameTpMax) frameTpMax = tp;

                        delayBuffer[writeIndex] = gainAppliedSample;
                        writeIndex = (writeIndex + 1) % lookAheadSize;
                        count++;
                    }
                    tpFuture[writeFrame % lookAheadFrames] = frameTpMax;
                    if (frameTpMax > maxInputTp) maxInputTp = frameTpMax;
                    outTpH[writeFrame % outTpH.Length] = frameTpMax;

                    while (dequeTail > dequeHead && tpFuture[dequeW[dequeTail - 1] % lookAheadFrames] <= frameTpMax)
                    {
                        dequeTail--;
                    }
                    dequeW[dequeTail++] = writeFrame;
                    while (dequeTail > dequeHead && dequeW[dequeHead] < writeFrame - tpWindow + 1)
                    {
                        dequeHead++;
                    }
                    if (dequeHead > 0 && dequeHead * 2 >= tpWindow)
                    {
                        Array.Copy(dequeW, dequeHead, dequeW, 0, dequeTail - dequeHead);
                        dequeTail -= dequeHead;
                        dequeHead = 0;
                    }
                    trailingTpMax[writeFrame % lookAheadFrames] = tpFuture[dequeW[dequeHead] % lookAheadFrames];
                    writeFrame++;

                    if (count >= lookAheadSize)
                    {
                        float maxTpFuture = trailingTpMax[(readFrame + tpWindow - 1) % lookAheadFrames];

                        float targetGain = 1.0f;
                        if (currentMaxInQueue > ceilingLinear)
                        {
                            targetGain = ceilingLinear / currentMaxInQueue;
                        }
                        if (maxTpFuture > ceilingLinear)
                        {
                            float truePeakGain = ceilingLinear / maxTpFuture;
                            if (truePeakGain < targetGain) targetGain = truePeakGain;
                        }
                        if (targetGain < 1.0f) limiterEngaged = true;

                        if (targetGain < currentGain) currentGain = targetGain;
                        else currentGain = releaseFactor * currentGain + (1.0f - releaseFactor) * targetGain;

                        float outTp = outTpH[readFrame % outTpH.Length] * currentGain;
                        if (outTp > maxOutputTp) maxOutputTp = outTp;

                        for (int c = 0; c < channels; c++)
                        {
                            float delayedSample = delayBuffer[readIndex];
                            delayBuffer[readIndex] = 0f;
                            readIndex = (readIndex + 1) % lookAheadSize;
                            count--;

                            float absLeaving = Math.Abs(delayedSample);
                            if (absLeaving >= currentMaxInQueue)
                            {
                                currentMaxInQueue = 0f;
                                for (int j = 0; j < lookAheadSize; j++)
                                {
                                    float absVal = Math.Abs(delayBuffer[j]);
                                    if (absVal > currentMaxInQueue) currentMaxInQueue = absVal;
                                }
                            }

                            chunkOut[emittedInChunk + c] = delayedSample * currentGain;
                        }
                        emittedInChunk += channels;
                        readFrame++;
                    }
                }

                if (emittedInChunk > 0) sink.WriteSamples(chunkOut.AsSpan(0, emittedInChunk));
            }

            // Vaciado del búfer circular (con el mismo control de true peak)
            float[] flushOut = new float[channels];
            while (count > 0)
            {
                ct.ThrowIfCancellationRequested();
                float maxTpFuture = 0f;
                for (int w = 0; w < tpWindow; w++)
                {
                    int fa = readFrame + w;
                    if (fa >= writeFrame) break;
                    float v = tpFuture[fa % lookAheadFrames];
                    if (v > maxTpFuture) maxTpFuture = v;
                }

                float targetGain = 1.0f;
                if (currentMaxInQueue > ceilingLinear)
                {
                    targetGain = ceilingLinear / currentMaxInQueue;
                }
                if (maxTpFuture > ceilingLinear)
                {
                    float truePeakGain = ceilingLinear / maxTpFuture;
                    if (truePeakGain < targetGain) targetGain = truePeakGain;
                }
                if (targetGain < 1.0f) limiterEngaged = true;

                if (targetGain < currentGain) currentGain = targetGain;
                else currentGain = releaseFactor * currentGain + (1.0f - releaseFactor) * targetGain;

                float outTp = outTpH[readFrame % outTpH.Length] * currentGain;
                if (outTp > maxOutputTp) maxOutputTp = outTp;

                for (int c = 0; c < channels; c++)
                {
                    float delayedSample = delayBuffer[readIndex];
                    delayBuffer[readIndex] = 0f;
                    readIndex = (readIndex + 1) % lookAheadSize;
                    count--;

                    float absLeaving = Math.Abs(delayedSample);
                    if (absLeaving >= currentMaxInQueue)
                    {
                        currentMaxInQueue = 0f;
                        for (int j = 0; j < lookAheadSize; j++)
                        {
                            float absVal = Math.Abs(delayBuffer[j]);
                            if (absVal > currentMaxInQueue) currentMaxInQueue = absVal;
                        }
                    }

                    flushOut[c] = delayedSample * currentGain;
                }
                sink.WriteSamples(flushOut.AsSpan());
                readFrame++;
            }

            return (limiterEngaged, maxOutputTp, maxInputTp);
        }

        // Lee todas las muestras float disponibles de un provider hasta llenar el búfer.
        private static int ReadAllFloats(ISampleProvider provider, float[] buffer, CancellationToken ct = default)
        {
            int total = 0;
            int n;
            while (total < buffer.Length && (n = provider.Read(buffer.AsSpan(total, buffer.Length - total))) > 0)
            {
                ct.ThrowIfCancellationRequested();
                total += n;
            }
            return total;
        }

        // PASADA 3 en memoria: garantía de TRUE PEAK <= techo aplicando corrección al búfer.
        // Solo mide con exactitud si la entrada pudo rebasar el techo (maxInputTp es cota superior
        // segura del TP de salida); de lo contrario usa la estimación en línea. Devuelve el TP final.
        private static float ApplyPasadA3InMemory(float[] output, int sampleRate, int channels, float ceilingLinear,
            bool engaged, float maxInputTp, float maxOutputTp, CancellationToken ct = default)
        {
            if (!engaged || maxInputTp <= ceilingLinear * 1.0006f) return maxOutputTp;
            float tp = MeasureTruePeakLinear(output, sampleRate, channels, ct);
            if (tp <= ceilingLinear * 1.0006f) return tp;
            float correction = ceilingLinear / tp;
            if (correction < 0.5f) correction = 0.5f;
            for (int i = 0; i < output.Length; i++) output[i] *= correction;
            return ceilingLinear;
        }

        // PASADA 3 a disco: garantía de TRUE PEAK <= techo sobre el archivo temporal renderizado.
        // Misma regla de decisión: mide el TP real solo si la entrada pudo rebasar el techo (caso
        // poco frecuente); de lo contrario usa la estimación en línea. Devuelve el TP final.
        private float PasadA3Disk(string tempOutput, string outputPath, float ceilingLinear,
            bool engaged, float maxInputTp, float maxOutputTp, CancellationToken ct = default)
        {
            if (!engaged || maxInputTp <= ceilingLinear * 1.0006f) return maxOutputTp;
            float tp = MeasureTruePeakLinear(tempOutput, ct);
            if (tp <= ceilingLinear * 1.0006f) return tp;

            float correction = ceilingLinear / tp;
            if (correction < 0.5f) correction = 0.5f;

            string correctedOutput = Path.Combine(Path.GetDirectoryName(tempOutput) ?? "", "output_corrected" + Path.GetExtension(outputPath));
            using (var reader = new SoundFileReader(tempOutput))
            {
                var waveFormat = reader.WaveFormat;
                var sampleProvider = reader.ToSampleProvider();
                using (var writer = new SoundFileWriter(correctedOutput, waveFormat,
                    Path.GetExtension(outputPath).ToLower() == ".flac" ? SoundFileMajorFormat.Flac : SoundFileMajorFormat.Wav))
                {
                    float[] floatBuffer = new float[waveFormat.SampleRate * waveFormat.Channels];
                    int samplesRead;
                    while ((samplesRead = sampleProvider.Read(floatBuffer.AsSpan())) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        for (int i = 0; i < samplesRead; i++) floatBuffer[i] *= correction;
                        writer.WriteSamples(floatBuffer.AsSpan(0, samplesRead));
                    }
                }
            }
            File.Delete(tempOutput);
            File.Move(correctedOutput, tempOutput);
            return ceilingLinear;
        }

        // Ruta disco (archivos largos): renderiza a archivo temporal, PASADA 3 (TP inline),
        // copia a destino y, si targetLufs viene definido, corrige UNA vez re-renderizando desde
        // el input. La verificación de convergencia usa análisis solo-de-sonoridad (sin TP) porque
        // el true peak de salida ya viene estimado en línea por RenderLimiter. Devuelve el AudioInfo
        // final del output (mismo valor que antes medía el VM re-analizando el archivo).
        private AudioInfo NormalizeToDisk(string inputPath, string tempOutput, string outputPath,
            float linearGain, float ceilingLinear, float releaseMs, float lookAheadMs, float targetLufs,
            CancellationToken ct)
        {
            float RenderAndFinalize(float gainLinear)
            {
                (bool engaged, float maxOutputTp, float maxInputTp) rendered;
                using (var inStream = GetAudioStream(inputPath))
                using (var reader = new SoundFileReader(inStream))
                {
                    var fmt = reader.WaveFormat;
                    using (var writer = new SoundFileWriter(tempOutput, fmt,
                        Path.GetExtension(outputPath).ToLower() == ".flac" ? SoundFileMajorFormat.Flac : SoundFileMajorFormat.Wav))
                    {
                        rendered = RenderLimiter(reader.ToSampleProvider(), fmt, gainLinear, ceilingLinear, releaseMs, lookAheadMs, new FileSink(writer), ct);
                    }
                }
                float outputTp = PasadA3Disk(tempOutput, outputPath, ceilingLinear, rendered.engaged, rendered.maxInputTp, rendered.maxOutputTp, ct);
                ct.ThrowIfCancellationRequested();
                File.Copy(tempOutput, outputPath, true);
                return outputTp;
            }

            float bestOutputTp = RenderAndFinalize(linearGain);

            AudioInfo? post = null;
            if (!float.IsNaN(targetLufs))
            {
                const float tolerance = 0.3f;
                const float maxExtraGainDb = 1.5f;
                double gainDbNow = 20.0 * Math.Log10(linearGain);
                post = AnalyzeLoudnessOnly(outputPath, ct);
                float shortfall = targetLufs - post.IntegratedLoudness;
                if (shortfall > tolerance && shortfall >= 0.1f)
                {
                    float extraGainDb = Math.Min(shortfall, maxExtraGainDb);
                    float reTp = RenderAndFinalize((float)Math.Pow(10, (gainDbNow + extraGainDb) / 20.0));
                    if (reTp > bestOutputTp) bestOutputTp = reTp;
                    post = AnalyzeLoudnessOnly(outputPath, ct);
                }
            }
            if (post == null) post = AnalyzeLoudnessOnly(outputPath, ct);
            ApplyOutputTruePeak(post, bestOutputTp);
            return post;
        }

        public AudioInfo ApplyNormalizationWithLimiter(string inputPath, string outputPath, float gainDb, float maxPeakLimitDb,
            float releaseMs = 40f, float lookAheadMs = 5f, float targetLufs = float.NaN, CancellationToken ct = default)
        {
            float linearGain = (float)Math.Pow(10, gainDb / 20.0);
            float ceilingLinear = (float)Math.Pow(10, maxPeakLimitDb / 20.0);

            string tempDir = Path.Combine(Path.GetTempPath(), "ElysiumAudio", Guid.NewGuid().ToString("N"));
            string tempOutput = Path.Combine(tempDir, "output" + Path.GetExtension(outputPath));

            try
            {
                Directory.CreateDirectory(tempDir);

                using var inputStream = GetAudioStream(inputPath);
                using (var reader = new SoundFileReader(inputStream))
                {
                    var waveFormat = reader.WaveFormat;
                    int sampleRate = waveFormat.SampleRate;
                    int channels = waveFormat.Channels;
                    long totalFrames = (long)(reader.TotalTime.TotalSeconds * sampleRate);
                    double totalSeconds = reader.TotalTime.TotalSeconds;

                    bool useMemory = totalFrames > 0
                        && totalSeconds <= MaxInMemoryRenderSeconds
                        && (long)totalFrames * channels * 8L <= MaxInMemoryRenderBytes;

                    if (useMemory)
                    {
                        // ── ESTRATEGIA EN MEMORIA (cero re-lecturas de disco) ──
                        float[] raw = new float[(totalFrames + sampleRate) * channels];
                        int rawCount = ReadAllFloats(reader.ToSampleProvider(), raw, ct);
                        if (rawCount < raw.Length) Array.Resize(ref raw, rawCount);

                        float[] output = new float[rawCount];
                        var rawProvider = new FloatSampleProvider(raw, sampleRate, channels);

                        var (engaged0, maxOutputTp0, maxInputTp0) = RenderLimiter(rawProvider, waveFormat, linearGain, ceilingLinear,
                            releaseMs, lookAheadMs, new BufferSink(output), ct);
                        float bestOutputTp = ApplyPasadA3InMemory(output, sampleRate, channels, ceilingLinear,
                            engaged0, maxInputTp0, maxOutputTp0, ct);

                        AudioInfo? post = null;
                        if (!float.IsNaN(targetLufs))
                        {
                            const float tolerance = 0.3f;
                            const float maxExtraGainDb = 1.5f;
                            post = AnalyzeFromFloats(output, sampleRate, channels, measureTruePeak: false, ct: ct);
                            float shortfall = targetLufs - post.IntegratedLoudness;
                            if (shortfall > tolerance && shortfall >= 0.1f)
                            {
                                float extraGainDb = Math.Min(shortfall, maxExtraGainDb);
                                var (reEngaged, reOutTp, reInTp) = RenderLimiter(new FloatSampleProvider(raw, sampleRate, channels), waveFormat,
                                    (float)Math.Pow(10, (gainDb + extraGainDb) / 20.0), ceilingLinear,
                                    releaseMs, lookAheadMs, new BufferSink(output), ct);
                                float reTp = ApplyPasadA3InMemory(output, sampleRate, channels, ceilingLinear,
                                    reEngaged, reInTp, reOutTp, ct);
                                if (reTp > bestOutputTp) bestOutputTp = reTp;
                                post = AnalyzeFromFloats(output, sampleRate, channels, measureTruePeak: false, ct: ct);
                            }
                        }
                        if (post == null) post = AnalyzeFromFloats(output, sampleRate, channels, measureTruePeak: false, ct: ct);
                        ApplyOutputTruePeak(post, bestOutputTp);

                        using (var writer = new SoundFileWriter(tempOutput, waveFormat,
                            Path.GetExtension(outputPath).ToLower() == ".flac" ? SoundFileMajorFormat.Flac : SoundFileMajorFormat.Wav))
                        {
                            writer.WriteSamples(output.AsSpan());
                        }
                        ct.ThrowIfCancellationRequested();
                        File.Copy(tempOutput, outputPath, true);
                        return post;
                    }
                    else
                    {
                        // ── ESTRATEGIA A DISCO (archivos largos) ──
                        return NormalizeToDisk(inputPath, tempOutput, outputPath, linearGain, ceilingLinear,
                            releaseMs, lookAheadMs, targetLufs, ct);
                    }
                }
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

        // Limpieza de directorios temporales huérfanos (%TEMP%\ElysiumAudio\*) que quedan
        // tras matar la app a la fuerza (Task Manager, cierre del sistema, corte de luz).
        // Solo borra los que llevan más tiempo que maxAge sin tocar: así un directorio activo
        // de otra instancia en ejecución no se elimina por error. Se invoca al arrancar.
        public static void CleanupStaleTempDirectories(TimeSpan maxAge)
        {
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "ElysiumAudio");
                if (!Directory.Exists(root)) return;

                DateTime now = DateTime.UtcNow;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        if (now - Directory.GetLastWriteTimeUtc(dir) > maxAge)
                        {
                            Directory.Delete(dir, true);
                        }
                    }
                    catch
                    {
                        // El directorio está en uso o los permisos lo impiden; se ignora.
                    }
                }
            }
            catch
            {
                // La limpieza nunca debe impedir el arranque de la app.
            }
        }

        // Mide el true peak lineal (4x oversampling) del archivo: máximo absoluto de la
        // onda reconstruida sobre todos los canales (p. ej. 0.891 para -1.0 dBTP).
        private static float MeasureTruePeakLinear(string path, CancellationToken ct = default)
        {
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new SoundFileReader(fileStream);
            var waveFormat = reader.WaveFormat;
            var sampleProvider = reader.ToSampleProvider();
            var meter = new TruePeakMeter(waveFormat.Channels);

            float maxTruePeak = 0f;
            int channels = waveFormat.Channels;
            float[] buffer = new float[waveFormat.SampleRate * channels];
            int read;

            while ((read = sampleProvider.Read(buffer.AsSpan())) > 0)
            {
                ct.ThrowIfCancellationRequested();
                for (int i = 0; i + channels - 1 < read; i += channels)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        float tp = meter.Process(buffer[i + c], c);
                        if (tp > maxTruePeak) maxTruePeak = tp;
                    }
                }
            }
            return maxTruePeak;
        }

        // Igual medición sobre un búfer interleaved ya cargado en memoria (ruta PASADA 3
        // en memoria), procesado por lotes por canal para no asignar buffers por canal.
        private static float MeasureTruePeakLinear(float[] samples, int sampleRate, int channels, CancellationToken ct = default)
        {
            var meter = new TruePeakMeter(channels);
            float maxTruePeak = 0f;

            int chunkFrames = sampleRate * 2;
            var chBuf = new float[channels][];
            for (int c = 0; c < channels; c++) chBuf[c] = new float[chunkFrames];

            int totalSamples = samples.Length;
            int pos = 0;
            while (pos < totalSamples)
            {
                ct.ThrowIfCancellationRequested();
                int frames = Math.Min(chunkFrames, (totalSamples - pos) / channels);
                if (frames <= 0) break;
                for (int c = 0; c < channels; c++)
                {
                    float[] ch = chBuf[c];
                    int idx = pos + c;
                    for (int i = 0; i < frames; i++, idx += channels)
                    {
                        ch[i] = samples[idx];
                    }
                    float m = meter.ProcessBlock(ch.AsSpan(0, frames), c);
                    if (m > maxTruePeak) maxTruePeak = m;
                }
                pos += frames * channels;
            }
            return maxTruePeak;
        }

    }
}
