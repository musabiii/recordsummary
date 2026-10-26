using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RecordSummary;

/// <summary>
/// Воспроизведение звонка: обе дорожки сводятся в одну, каждую можно приглушить ("только я" / "только собеседник"),
/// темп меняется без изменения высоты голоса.
/// </summary>
public sealed class CallPlayer : IDisposable
{
    const int Rate = 48000;

    readonly object _gate = new();
    readonly WaveStream _micReader, _systemReader;
    readonly VolumeSampleProvider _micVolume, _systemVolume;
    readonly TempoSampleProvider _tempo;
    readonly WasapiOut _out;

    public TimeSpan Duration { get; }
    public bool IsPlaying => _out.PlaybackState == PlaybackState.Playing;

    /// <summary>Воспроизведение дошло до конца или остановилось с ошибкой.</summary>
    public event Action? Stopped;

    public CallPlayer(string folder)
    {
        _micReader = Tracks.Open(Tracks.Require(folder, Tracks.Mic));
        _systemReader = Tracks.Open(Tracks.Require(folder, Tracks.System));
        Duration = _micReader.TotalTime > _systemReader.TotalTime ? _micReader.TotalTime : _systemReader.TotalTime;

        _micVolume = new VolumeSampleProvider(Mono(_micReader));
        _systemVolume = new VolumeSampleProvider(Mono(_systemReader));
        _tempo = new TempoSampleProvider(new Mix(_micVolume, _systemVolume));

        _out = new WasapiOut(AudioClientShareMode.Shared, 100);
        _out.Init(new Locked(this, new MonoToStereoSampleProvider(_tempo)));
        _out.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null) Log.Write($"Воспроизведение: {e.Exception.Message}");
            Stopped?.Invoke();
        };
    }

    /// <summary>Дорожка любого формата → моно 48 кГц, чтобы их можно было смешать.</summary>
    static ISampleProvider Mono(WaveStream reader)
    {
        ISampleProvider p = reader.ToSampleProvider();
        if (p.WaveFormat.Channels > 1) p = new DownmixSampleProvider(p);
        if (p.WaveFormat.SampleRate != Rate) p = new WdlResamplingSampleProvider(p, Rate);
        return p;
    }

    /// <summary>Позиция по дорожке, которая длиннее (по ней же считается Duration).</summary>
    public TimeSpan Position
    {
        get
        {
            lock (_gate) return Max(_micReader.CurrentTime, _systemReader.CurrentTime);
        }
        set
        {
            lock (_gate)
            {
                var t = value < TimeSpan.Zero ? TimeSpan.Zero : value > Duration ? Duration : value;
                _micReader.CurrentTime = t < _micReader.TotalTime ? t : _micReader.TotalTime;
                _systemReader.CurrentTime = t < _systemReader.TotalTime ? t : _systemReader.TotalTime;
                _tempo.Reset();
            }
        }
    }

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public double Tempo
    {
        get => _tempo.Tempo;
        set { lock (_gate) _tempo.Tempo = value; }
    }

    /// <summary>Какие дорожки слышно.</summary>
    public void SetTracks(bool me, bool them)
    {
        _micVolume.Volume = me ? 1 : 0;
        _systemVolume.Volume = them ? 1 : 0;
    }

    public void Play()
    {
        if (Position >= Duration - TimeSpan.FromMilliseconds(200)) Position = TimeSpan.Zero;   // с конца — заново
        _out.Play();
    }

    public void Pause() => _out.Pause();

    public void Dispose()
    {
        _out.Dispose();
        _micReader.Dispose();
        _systemReader.Dispose();
    }

    /// <summary>
    /// Сумма двух дорожек. MixingSampleProvider из NAudio навсегда выкидывает закончившийся вход,
    /// и после перемотки назад более короткая дорожка пропала бы.
    /// </summary>
    sealed class Mix(ISampleProvider a, ISampleProvider b) : ISampleProvider
    {
        float[] _buf = [];

        public WaveFormat WaveFormat => a.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_buf.Length < count) _buf = new float[count];
            int na = a.Read(buffer, offset, count);
            Array.Clear(buffer, offset + na, count - na);
            int nb = b.Read(_buf, 0, count);
            for (int i = 0; i < nb; i++) buffer[offset + i] += _buf[i];
            return Math.Max(na, nb);
        }
    }

    /// <summary>Чтение идёт в потоке звука, перемотка и смена темпа — из окна; замок не даёт им пересечься.</summary>
    sealed class Locked(CallPlayer player, ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            lock (player._gate) return source.Read(buffer, offset, count);
        }
    }
}
