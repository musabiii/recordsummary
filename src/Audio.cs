using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;

namespace RecordSummary;

/// <summary>
/// Дорожки звонка: mic и system. Пока звонок не обработан — несжатый wav, после — mp3 (или m4a, если в Windows нет MP3-кодировщика).
/// </summary>
public static class Tracks
{
    public const string Mic = "mic", System = "system";
    static readonly string[] Extensions = [".wav", ".mp3", ".m4a"];

    /// <summary>wav, пока он есть (он исходник: сжатие могло не удалить его, если файл был занят), иначе сжатая копия.</summary>
    public static string? Find(string folder, string track) =>
        Extensions.Select(e => Path.Combine(folder, track + e)).FirstOrDefault(File.Exists);

    public static string Require(string folder, string track) =>
        Find(folder, track) ?? throw new FileNotFoundException($"В папке звонка нет дорожки {track}", Path.Combine(folder, track + ".wav"));

    /// <summary>
    /// Сжатые файлы читает Media Foundation. Его COM-объекты создаются в MTA-потоке, иначе их нельзя читать
    /// из потока звука (WASAPI) — поэтому открываем через Task.Run и из окна тоже.
    /// </summary>
    public static WaveStream Open(string path) => path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
        ? new WaveFileReader(path)
        : Task.Run(() => (WaveStream)new MediaFoundationReader(path)).GetAwaiter().GetResult();

    public static TimeSpan Duration(string folder, string track)
    {
        var path = Find(folder, track);
        if (path == null) return TimeSpan.Zero;
        try
        {
            using var r = Open(path);
            return r.TotalTime;
        }
        catch { return TimeSpan.Zero; }   // пишется прямо сейчас или повреждён
    }

    /// <summary>Байт в несжатых дорожках звонка — для подсказки в настройках.</summary>
    public static long WavBytes(string folder) =>
        new[] { Mic, System }.Select(t => new FileInfo(Path.Combine(folder, t + ".wav"))).Where(f => f.Exists).Sum(f => f.Length);
}

/// <summary>
/// Сжатие обработанного звонка: каждая дорожка → моно MP3 64 кбит/с (~29 МБ на час вместо 1,4–2,7 ГБ wav).
/// Для речи и повторного распознавания этого достаточно: whisper всё равно работает на 16 кГц.
/// Wav удаляется, только если сжатый файл открылся и совпал по длительности.
/// </summary>
public static class Compressor
{
    const int Mp3Bitrate = 64000, AacBitrate = 96000;   // у AAC-кодировщика Windows меньше 96 кбит/с не бывает

    /// <summary>Сколько байт освободилось.</summary>
    public static long Compress(string folder)
    {
        long saved = 0;
        foreach (var track in new[] { Tracks.Mic, Tracks.System })
        {
            var wav = Path.Combine(folder, track + ".wav");
            if (!File.Exists(wav)) continue;
            var size = new FileInfo(wav).Length;
            var compressed = Encode(wav, Path.Combine(folder, track));
            try
            {
                File.Delete(wav);
                saved += size - new FileInfo(compressed).Length;
            }
            catch (IOException e)
            {
                // Например, звонок открыт в плеере. Сжатая копия уже есть, wav удалится при следующем сжатии.
                Log.Write($"{wav} не удалён: {e.Message}");
            }
        }
        return saved;
    }

    /// <summary>Путь к готовому сжатому файлу. Кодировщик выбирает формат по расширению, поэтому временный файл — *.tmp.mp3.</summary>
    static string Encode(string wav, string basePath)
    {
        TimeSpan expected;
        using (var r = new WaveFileReader(wav)) expected = r.TotalTime;

        foreach (var (ext, encode) in new (string, Action<IWaveProvider, string>)[]
                 {
                     (".mp3", (p, f) => MediaFoundationEncoder.EncodeToMp3(p, f, Mp3Bitrate)),
                     (".m4a", (p, f) => MediaFoundationEncoder.EncodeToAac(p, f, AacBitrate)),
                 })
        {
            var tmp = basePath + ".tmp" + ext;
            try
            {
                using (var reader = new WaveFileReader(wav))
                    encode(Pcm16Mono(reader), tmp);

                TimeSpan actual;
                using (var check = new MediaFoundationReader(tmp)) actual = check.TotalTime;
                // Кодировщик добавляет десятки миллисекунд тишины в начало; секунда разницы — уже что-то не так.
                if ((actual - expected).Duration() > TimeSpan.FromSeconds(1))
                    throw new InvalidDataException($"сжатый файл {actual.TotalSeconds:F1} с вместо {expected.TotalSeconds:F1} с");

                var final = basePath + ext;
                File.Move(tmp, final, true);
                return final;
            }
            catch (Exception e) when (ext == ".mp3" && e is not InvalidDataException)
            {
                Log.Write($"MP3-кодировщика нет, сжимаю в AAC: {e.Message}");   // бывает в Windows N без Media Feature Pack
                File.Delete(tmp);
            }
            catch
            {
                File.Delete(tmp);
                throw;
            }
        }
        throw new InvalidOperationException("В Windows нет ни MP3-, ни AAC-кодировщика");
    }

    /// <summary>Кодировщики Windows принимают 16-битный PCM 44,1/48 кГц.</summary>
    static IWaveProvider Pcm16Mono(WaveStream reader)
    {
        ISampleProvider p = reader.ToSampleProvider();
        if (p.WaveFormat.Channels > 1) p = new DownmixSampleProvider(p);
        if (p.WaveFormat.SampleRate is not (44100 or 48000)) p = new WdlResamplingSampleProvider(p, 48000);
        return new SampleToWaveProvider16(p);
    }
}

/// <summary>Среднее по каналам. StereoToMono в NAudio умеет только 2 канала, а у микрофонных массивов их бывает 4.</summary>
public sealed class DownmixSampleProvider(ISampleProvider source) : ISampleProvider
{
    readonly int _channels = source.WaveFormat.Channels;
    float[] _buf = [];

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

    public int Read(float[] buffer, int offset, int count)
    {
        int need = count * _channels;
        if (_buf.Length < need) _buf = new float[need];
        int read = source.Read(_buf, 0, need) / _channels;
        for (int i = 0; i < read; i++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++) sum += _buf[i * _channels + c];
            buffer[offset + i] = sum / _channels;
        }
        return read;
    }
}

/// <summary>
/// Ускорение без "мультяшного" голоса: SoundTouch меняет темп, сохраняя высоту.
/// На обычной скорости звук идёт напрямую, без обработки.
/// </summary>
public sealed class TempoSampleProvider : ISampleProvider
{
    readonly ISampleProvider _source;
    readonly SoundTouchProcessor _st = new();
    readonly int _channels;
    readonly float[] _buf;
    double _tempo = 1;
    bool _eof;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public TempoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _st.SampleRate = source.WaveFormat.SampleRate;
        _st.Channels = _channels;
        _buf = new float[_channels * 2048];
    }

    /// <summary>Вызывающий отвечает за то, чтобы не менять темп посреди Read (см. CallPlayer).</summary>
    public double Tempo
    {
        get => _tempo;
        set { _tempo = value; _st.Tempo = value; Reset(); }
    }

    /// <summary>После перемотки: выбросить накопленный звук старой позиции.</summary>
    public void Reset()
    {
        _st.Clear();
        _eof = false;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_tempo == 1) return _source.Read(buffer, offset, count);

        int written = 0;
        while (written < count)
        {
            var span = buffer.AsSpan(offset + written, count - written);
            written += _st.ReceiveSamples(span, span.Length / _channels) * _channels;
            if (written >= count || _eof) break;
            int n = _source.Read(_buf, 0, _buf.Length);
            if (n == 0) { _st.Flush(); _eof = true; continue; }
            _st.PutSamples(_buf.AsSpan(0, n), n / _channels);
        }
        return written;
    }
}
