using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.XAudio2;

namespace MovieAgent.FFmpegDecoder
{
    /// <summary>
    /// XAudio2 音频输出（WaveOutEvent 兼容接口面）。
    /// 时钟核心：IXAudio2SourceVoice.SamplesPlayed 为采样级硬件播放指针（48kHz下精度≈0.02ms），
    /// 相比 WaveOutEvent 依赖 BufferedWaveProvider.BufferedDuration 估算（±100ms量化抖动），
    /// 音画同步的时钟噪声被根除。
    /// 接口语义：
    ///   GetPosition()      返回累计提交字节（含ClearBuffer平移基准，单调不减，兼容旧Seek基准逻辑）
    ///   BufferedDuration   当前队列未播放时长（= 提交字节 - 已播放字节，精确无抖动）
    ///   两者相减即得精确的实际播放位置。
    /// </summary>
    public sealed class XAudioPlayer : IDisposable
    {
        private IXAudio2? _engine;
        private IXAudio2MasteringVoice? _master;
        private IXAudio2SourceVoice? _voice;
        private readonly Queue<IntPtr> _pending = new();  // 已提交待消费的非托管缓冲
        private readonly WaveFormat _format;
        private long _positionOffset;    // ClearBuffer 平移基准：保证 GetPosition 单调不减
        private long _submittedBytes;    // 当前队列累计提交字节
        private volatile bool _started;
        private volatile bool _paused;
        private float _volume = 1f;
        private bool _disposed;
        private readonly object _lock = new();

        public XAudioPlayer(int sampleRate, int bits = 16, int channels = 2)
        {
            _format = new WaveFormat(sampleRate, bits, channels);
            _engine = XAudio2.XAudio2Create(ProcessorSpecifier.UseDefaultProcessor, false);
            _engine.StartEngine();
            _master = _engine.CreateMasteringVoice((uint)channels, (uint)sampleRate);
            _voice = _engine.CreateSourceVoice(new Vortice.Multimedia.WaveFormat(sampleRate, bits, channels));
            _voice.SetVolume(_volume);
        }

        // ==== WaveOutEvent 兼容面 ====

        public WaveFormat OutputWaveFormat => _format;
        public WaveFormat WaveFormat => _format;
        public int DeviceNumber => 0;

        public PlaybackState PlaybackState =>
            !_started ? PlaybackState.Stopped : (_paused ? PlaybackState.Paused : PlaybackState.Playing);

        public float Volume
        {
            get => _volume;
            set { _volume = value; _voice?.SetVolume(value); }
        }

        /// <summary>累计提交字节（单调不减）。与 BufferedDuration 相减即得精确播放位置。</summary>
        public long GetPosition() => _positionOffset + _submittedBytes;

        public long GetPositionMs() => (long)(GetPosition() * 1000L / _format.AverageBytesPerSecond);

        /// <summary>队列中未播放的字节数（提交 - 硬件已消费，采样级精确）</summary>
        public long BufferedBytes
        {
            get
            {
                if (_voice == null) return 0;
                var st = _voice.State;
                return _submittedBytes - PlayedBytes(st);
            }
        }

        /// <summary>队列中未播放时长</summary>
        public TimeSpan BufferedDuration =>
            TimeSpan.FromSeconds(BufferedBytes / (double)_format.AverageBytesPerSecond);

        /// <summary>已播放时长（基于SamplesPlayed，采样级硬件时钟）</summary>
        public long GetPlayedMs()
        {
            if (_voice == null) return 0;
            var st = _voice.State;
            return (long)st.SamplesPlayed * 1000L / _format.SampleRate;
        }

        /// <summary>提交PCM数据（推模式，内部复制到非托管内存）</summary>
        public void AddSamples(byte[] buffer, int offset, int count)
        {
            if (_voice == null || count <= 0) return;
            lock (_lock)
            {
                CleanupConsumed();
                IntPtr p = Marshal.AllocHGlobal(count);
                Marshal.Copy(buffer, offset, p, count);
                var ab = new AudioBuffer
                {
                    AudioBytes = (uint)count,
                    AudioDataPointer = p,
                    Flags = BufferFlags.None
                };
                _voice.SubmitSourceBuffer(ab, null);
                _pending.Enqueue(p);
                _submittedBytes += count;
                if (_started && !_paused) _voice.Start();
            }
        }

        /// <summary>清空队列（Seek用）。平移内部基准保证 GetPosition 单调。</summary>
        public void ClearBuffer()
        {
            if (_voice == null) return;
            lock (_lock)
            {
                try { _voice.Stop(PlayFlags.None, 0); _voice.FlushSourceBuffers(); } catch { }
                while (_pending.Count > 0) Marshal.FreeHGlobal(_pending.Dequeue());
                var st = _voice.State;
                long played = PlayedBytes(st);
                _positionOffset += _submittedBytes - played;
                _submittedBytes = played;
                if (_started && !_paused) _voice.Start();
            }
        }

        public void Play()
        {
            _started = true;
            _paused = false;
            _voice?.Start();
        }

        public void Pause()
        {
            _paused = true;
            _voice?.Stop(PlayFlags.None, 0);
        }

        public void Stop()
        {
            _started = false;
            _paused = false;
            if (_voice != null)
            {
                try { _voice.Stop(PlayFlags.None, 0); _voice.FlushSourceBuffers(); } catch { }
                while (_pending.Count > 0) Marshal.FreeHGlobal(_pending.Dequeue());
                var st = _voice.State;
                long played = PlayedBytes(st);
                _positionOffset += _submittedBytes - played;
                _submittedBytes = played;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_lock)
            {
                while (_pending.Count > 0) Marshal.FreeHGlobal(_pending.Dequeue());
                _voice?.Dispose();
                _master?.Dispose();
                _engine?.Dispose();
                _voice = null; _master = null; _engine = null;
            }
        }

        private void CleanupConsumed()
        {
            if (_voice == null) return;
            var st = _voice.State;
            // 队列中还有 st.BuffersQueued 块未消费，其余可安全释放
            while (_pending.Count > st.BuffersQueued)
                Marshal.FreeHGlobal(_pending.Dequeue());
        }

        private long PlayedBytes(VoiceState st) => (long)st.SamplesPlayed * _format.BlockAlign;
    }
}
