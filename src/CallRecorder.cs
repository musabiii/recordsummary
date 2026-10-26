using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RecordSummary;

/// <summary>
/// Две дорожки: mic.wav — микрофон (вы), system.wav — системный звук через WASAPI loopback (собеседники).
/// Раздельные дорожки дают разметку "я / они" без диаризации. Пишутся в родном формате устройств,
/// без пересэмплирования на лету — к 16 кГц для whisper приводим уже после звонка.
/// </summary>
public sealed class CallRecorder : IDisposable
{
    readonly Stopwatch _clock = new();
    readonly WasapiCapture _micCapture;
    readonly WasapiLoopbackCapture _systemCapture;
    readonly Track _mic, _system;
    bool _disposed;

    public string Folder { get; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public float MicLevel => _mic.Level;
    public float SystemLevel => _system.Level;

    public CallRecorder(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);

        // Приложения для звонков по умолчанию используют устройства связи, а не мультимедиа.
        using var devices = new MMDeviceEnumerator();
        var micDevice = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        var outDevice = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);

        _micCapture = new WasapiCapture(micDevice);
        _systemCapture = new WasapiLoopbackCapture(outDevice);
        _mic = new Track(Path.Combine(folder, "mic.wav"), _micCapture.WaveFormat, _clock);
        _system = new Track(Path.Combine(folder, "system.wav"), _systemCapture.WaveFormat, _clock);

        _micCapture.DataAvailable += (_, e) => _mic.Append(e.Buffer, e.BytesRecorded);
        _systemCapture.DataAvailable += (_, e) => _system.Append(e.Buffer, e.BytesRecorded);
        _micCapture.RecordingStopped += (_, e) => { if (e.Exception != null) Log.Write($"Микрофон: {e.Exception.Message}"); };
        _systemCapture.RecordingStopped += (_, e) => { if (e.Exception != null) Log.Write($"Системный звук: {e.Exception.Message}"); };

        Log.Write($"Запись в {folder}: микрофон \"{micDevice.FriendlyName}\" {_micCapture.WaveFormat}, " +
                  $"вывод \"{outDevice.FriendlyName}\" {_systemCapture.WaveFormat}");
        _clock.Start();
        _micCapture.StartRecording();
        _systemCapture.StartRecording();
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _micCapture.StopRecording(); } catch { }
        try { _systemCapture.StopRecording(); } catch { }
        var end = _clock.Elapsed;
        _mic.Close(end);
        _system.Close(end);
        _micCapture.Dispose();
        _systemCapture.Dispose();
        Log.Write($"Запись остановлена: {end:hh\\:mm\\:ss}");
    }

    /// <summary>
    /// WAV-файл, выровненный по общим часам. Loopback не присылает данные, пока ничего не играет,
    /// и без добивки тишиной дорожки разъедутся по времени.
    /// </summary>
    sealed class Track
    {
        readonly object _gate = new();
        readonly WaveFormat _format;
        readonly Stopwatch _clock;
        readonly bool _float;
        WaveFileWriter? _writer;
        long _written;

        public float Level { get; private set; }

        public Track(string path, WaveFormat format, Stopwatch clock)
        {
            _format = format;
            _clock = clock;
            _writer = new WaveFileWriter(path, format);
            _float = format.BitsPerSample == 32 && format.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible;
        }

        public void Append(byte[] buffer, int count)
        {
            lock (_gate)
            {
                if (_writer == null) return;
                // Буфер заканчивается "сейчас", значит начался на свою длительность раньше.
                PadTo(_clock.Elapsed - TimeSpan.FromSeconds(count / (double)_format.AverageBytesPerSecond));
                _writer.Write(buffer, 0, count);
                _written += count;
            }
            Level = Peak(buffer, count);
        }

        public void Close(TimeSpan end)
        {
            lock (_gate)
            {
                if (_writer == null) return;
                PadTo(end);
                _writer.Dispose();
                _writer = null;
                Level = 0;
            }
        }

        void PadTo(TimeSpan t)
        {
            long target = (long)(t.TotalSeconds * _format.AverageBytesPerSecond);
            target -= target % _format.BlockAlign;
            long gap = target - _written;
            if (gap < _format.AverageBytesPerSecond / 10) return;   // джиттер меньше 100 мс не трогаем
            var zeros = new byte[Math.Min(gap, _format.AverageBytesPerSecond)];
            while (gap > 0)
            {
                int n = (int)Math.Min(gap, zeros.Length);
                _writer!.Write(zeros, 0, n);
                _written += n;
                gap -= n;
            }
        }

        float Peak(byte[] buffer, int count)
        {
            float peak = 0;
            if (_float)
                for (int i = 0; i + 4 <= count; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
            else if (_format.BitsPerSample == 16)
                for (int i = 0; i + 2 <= count; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i) / 32768f));
            return Math.Min(peak, 1f);
        }
    }
}
