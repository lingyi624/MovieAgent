using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace MovieAgent.Infrastructure.Services
{
    public class SharedMemoryManager : IDisposable
    {
        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _viewAccessor;
        private EventWaitHandle? _dataReadyEvent;
        private EventWaitHandle? _dataConsumedEvent;
        private readonly string _mmfName;
        private readonly string _readyEventName;
        private readonly string _consumedEventName;
        private bool _disposed;

        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }
        public int FrameSize => FrameWidth * FrameHeight * 3;

        public SharedMemoryManager(string baseName)
        {
            _mmfName = $"MovieAgent_FrameBuffer_{baseName}";
            _readyEventName = $"MovieAgent_DataReady_{baseName}";
            _consumedEventName = $"MovieAgent_DataConsumed_{baseName}";
        }

        public bool Create(int width, int height)
        {
            try
            {
                FrameWidth = width;
                FrameHeight = height;
                int bufferSize = FrameSize + 128;

                _mmf = MemoryMappedFile.CreateNew(_mmfName, bufferSize, MemoryMappedFileAccess.ReadWrite);
                _viewAccessor = _mmf.CreateViewAccessor(0, bufferSize);

                _dataReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _readyEventName);
                _dataConsumedEvent = new EventWaitHandle(true, EventResetMode.AutoReset, _consumedEventName);

                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public bool Open(int width, int height)
        {
            try
            {
                FrameWidth = width;
                FrameHeight = height;

                _mmf = MemoryMappedFile.OpenExisting(_mmfName, MemoryMappedFileRights.ReadWrite);
                _viewAccessor = _mmf.CreateViewAccessor();

                _dataReadyEvent = EventWaitHandle.OpenExisting(_readyEventName);
                _dataConsumedEvent = EventWaitHandle.OpenExisting(_consumedEventName);

                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public bool WriteFrame(byte[] frameData, long timestamp)
        {
            if (_disposed || _viewAccessor == null || _dataReadyEvent == null || _dataConsumedEvent == null)
                return false;

            try
            {
                if (!_dataConsumedEvent.WaitOne(100))
                {
                    return false;
                }

                _viewAccessor.Write(0, timestamp);
                _viewAccessor.Write(8, frameData.Length);
                _viewAccessor.WriteArray(16, frameData, 0, frameData.Length);

                _dataReadyEvent.Set();
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public bool ReadFrame(out byte[] frameData, out long timestamp, out long audioTimestamp, out long audioPlayPosition)
        {
            frameData = Array.Empty<byte>();
            timestamp = 0;
            audioTimestamp = 0;
            audioPlayPosition = 0;

            if (_disposed || _viewAccessor == null || _dataReadyEvent == null)
                return false;

            try
            {
                if (!_dataReadyEvent.WaitOne(100))
                {
                    return false;
                }

                timestamp = _viewAccessor.ReadInt64(0);
                audioTimestamp = _viewAccessor.ReadInt64(8);
                audioPlayPosition = _viewAccessor.ReadInt64(16);
                int dataLength = _viewAccessor.ReadInt32(24);

                if (dataLength > 0)
                {
                    frameData = new byte[dataLength];
                    _viewAccessor.ReadArray(32, frameData, 0, dataLength);
                }

                _dataConsumedEvent?.Set();
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            try { _viewAccessor?.Dispose(); } catch { }
            try { _mmf?.Dispose(); } catch { }
            try { _dataReadyEvent?.Dispose(); } catch { }
            try { _dataConsumedEvent?.Dispose(); } catch { }
        }
    }
}