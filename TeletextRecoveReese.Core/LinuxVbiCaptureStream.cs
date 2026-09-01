using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace TeletextRecoveReese.Core;

public sealed class LinuxVbiCaptureStream : Stream
{
    private const uint VbiCaptureType = 4;
    private const ulong VideoGetFormat = 0xC0D05604;
    private const uint GreyFourCc = 0x59455247; // GREY
    private readonly FileStream _device;
    private readonly byte[] _frame;
    private int _frameOffset;
    private int _frameLength;

    public uint SamplingRate { get; }
    public int SamplesPerLine { get; }
    public int FirstFieldLines { get; }
    public int SecondFieldLines { get; }
    public int LinesPerFrame => FirstFieldLines + SecondFieldLines;
    public uint SampleFormat { get; }

    public LinuxVbiCaptureStream(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Raw V4L2 VBI capture is available on Linux.");
        _device = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1, FileOptions.Asynchronous);
        byte[] format = new byte[208];
        BitConverter.TryWriteBytes(format.AsSpan(0, 4), VbiCaptureType);
        GCHandle pinned = GCHandle.Alloc(format, GCHandleType.Pinned);
        try
        {
            int result = ioctl(
                _device.SafeFileHandle.DangerousGetHandle().ToInt32(),
                VideoGetFormat,
                pinned.AddrOfPinnedObject());
            if (result < 0)
                throw new IOException($"VIDIOC_G_FMT failed for {path} (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally { pinned.Free(); }

        // The v4l2_format union starts at offset 8 on Linux. The raw VBI member
        // contains sampling rate, samples/line, format, starts and field counts.
        SamplingRate = BitConverter.ToUInt32(format, 8);
        SamplesPerLine = checked((int)BitConverter.ToUInt32(format, 16));
        SampleFormat = BitConverter.ToUInt32(format, 20);
        FirstFieldLines = checked((int)BitConverter.ToUInt32(format, 32));
        SecondFieldLines = checked((int)BitConverter.ToUInt32(format, 36));
        if (SamplesPerLine <= 0 || LinesPerFrame <= 0)
            throw new InvalidDataException("The V4L2 device returned an invalid raw VBI format.");
        if (SampleFormat != GreyFourCc)
            throw new NotSupportedException(
                $"The VBI device uses unsupported sample format 0x{SampleFormat:X8}; GREY 8-bit samples are required.");
        _frame = new byte[checked(SamplesPerLine * LinesPerFrame)];
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_frameOffset >= _frameLength)
        {
            _frameOffset = 0;
            _frameLength = 0;
            while (_frameLength < _frame.Length)
            {
                int read = await _device.ReadAsync(
                    _frame.AsMemory(_frameLength), cancellationToken).ConfigureAwait(false);
                if (read == 0) return 0;
                _frameLength += read;
            }
        }
        int count = Math.Min(buffer.Length, _frameLength - _frameOffset);
        _frame.AsMemory(_frameOffset, count).CopyTo(buffer);
        _frameOffset += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _device.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, IntPtr argument);
}
