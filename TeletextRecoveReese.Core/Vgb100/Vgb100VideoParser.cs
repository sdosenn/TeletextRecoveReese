// SPDX-License-Identifier: GPL-2.0-or-later
namespace TeletextRecoveReese.Core.Vgb100;

/// <summary>
/// Assembles interlaced 720-wide video frames from the CX23102 isochronous stream,
/// following cx231xx_isoc_copy(). Each active line follows an FF 00 00 code;
/// 0x8x and 0xCx mark lines of the first and second field. The bridge sends
/// UYVY, which is byte-swapped into YUYV as the Linux driver exposes it.
/// </summary>
public sealed class Vgb100VideoParser
{
    public const int Width = 720;
    public const int BytesPerLine = Width * 2;
    public const int PalHeight = 576;
    public const int NtscHeight = 480;

    public int Height { get; }
    public int LinesPerField => Height / 2;
    public int FrameSize => BytesPerLine * Height;

    private enum State
    {
        SeekFf,
        SeekZero1,
        SeekZero2,
        ReadCode,
        ReadLine,
    }

    private readonly Action<byte[]> _frameCompleted;
    private readonly byte[] _frame;
    private State _state;
    private int _field;
    private int _lines;
    private bool _field1Done;
    private int _lineOffset;
    private bool _keepLine;
    private int _pendingByte = -1;

    /// <param name="frameCompleted">
    /// Receives the parser's frame buffer, which is reused for the next frame.
    /// </param>
    public Vgb100VideoParser(Action<byte[]> frameCompleted, int height = PalHeight)
    {
        _frameCompleted = frameCompleted;
        Height = height;
        _frame = new byte[FrameSize];
    }

    public void Reset()
    {
        _state = State.SeekFf;
        _field = 0;
        _lines = 0;
        _field1Done = false;
        _lineOffset = 0;
        _keepLine = false;
        _pendingByte = -1;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        int index = 0;
        while (index < data.Length)
        {
            if (_state == State.ReadLine)
            {
                int count = Math.Min(BytesPerLine - _lineOffset, data.Length - index);
                if (_keepLine) CopySwapped(data.Slice(index, count));
                index += count;
                _lineOffset += count;
                if (_lineOffset == BytesPerLine) FinishLine();
                continue;
            }

            byte value = data[index++];
            _state = _state switch
            {
                State.SeekFf => value == 0xff ? State.SeekZero1 : State.SeekFf,
                State.SeekZero1 => value == 0x00 ? State.SeekZero2 : value == 0xff ? State.SeekZero1 : State.SeekFf,
                State.SeekZero2 => value == 0x00 ? State.ReadCode : value == 0xff ? State.SeekZero1 : State.SeekFf,
                State.ReadCode => ReadCode(value),
                _ => _state,
            };
        }
    }

    private State ReadCode(byte code)
    {
        switch (code & 0xf0)
        {
            case 0x80:
                BeginLine(1);
                return State.ReadLine;
            case 0xc0:
                BeginLine(2);
                return State.ReadLine;
            default:
                // Any other code ends the current run of active lines.
                ChangeField(0);
                return code == 0xff ? State.SeekZero1 : State.SeekFf;
        }
    }

    // cx231xx_reset_video_buffer(): a field change restarts the line count and
    // remembers whether the first field was complete.
    private void ChangeField(int field)
    {
        if (field == _field) return;
        if (_field == 1) _field1Done = _lines >= LinesPerField;
        _field = field;
        _lines = 0;
    }

    private void BeginLine(int field)
    {
        ChangeField(field);
        _lineOffset = 0;
        _pendingByte = -1;
        _keepLine = _lines < LinesPerField;
    }

    private void CopySwapped(ReadOnlySpan<byte> data)
    {
        int row = _lines * 2 + (_field == 2 ? 1 : 0);
        Span<byte> line = _frame.AsSpan(row * BytesPerLine, BytesPerLine);
        int position = _lineOffset;
        foreach (byte value in data)
        {
            // Swap each byte pair; a pair may straddle two USB packets.
            if ((position & 1) == 0)
            {
                _pendingByte = value;
            }
            else
            {
                line[position - 1] = value;
                line[position] = (byte)_pendingByte;
            }
            position++;
        }
    }

    private void FinishLine()
    {
        if (_keepLine)
        {
            _lines++;
            if (_field == 2 && _lines == LinesPerField && _field1Done)
            {
                _field1Done = false;
                _frameCompleted(_frame);
            }
        }
        _lineOffset = 0;
        _keepLine = false;
        _state = State.SeekFf;
    }
}
