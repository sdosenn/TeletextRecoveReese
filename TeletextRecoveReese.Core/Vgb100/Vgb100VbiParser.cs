// SPDX-License-Identifier: GPL-2.0-or-later
namespace TeletextRecoveReese.Core.Vgb100;

/// <summary>
/// Assembles raw VBI frames from the CX23102 bulk stream. Each line follows a
/// BT.656-style FF 00 00 code; codes 0x2x and 0x6x mark VBI lines of the first
/// and second field. Lines beyond the 18 exposed per field are discarded, as
/// some firmwares emit a surplus VBI-tagged line.
/// </summary>
public sealed class Vgb100VbiParser
{
    public const int SamplesPerLine = 1440;
    public const int LinesPerField = 18;
    public const int FrameSize = SamplesPerLine * LinesPerField * 2;

    private enum State
    {
        SeekFf,
        SeekZero1,
        SeekZero2,
        ReadCode,
        ReadLine,
    }

    private readonly Action<byte[]> _frameCompleted;
    private byte[] _frame = new byte[FrameSize];
    private State _state;
    private int _field;
    private int _field1Lines;
    private int _field2Lines;
    private int _lineOffset;
    private bool _keepLine;

    public Vgb100VbiParser(Action<byte[]> frameCompleted) => _frameCompleted = frameCompleted;

    public void Reset()
    {
        _state = State.SeekFf;
        _field = 0;
        _field1Lines = 0;
        _field2Lines = 0;
        _lineOffset = 0;
        _keepLine = false;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        int index = 0;
        while (index < data.Length)
        {
            if (_state == State.ReadLine)
            {
                int count = Math.Min(SamplesPerLine - _lineOffset, data.Length - index);
                if (_keepLine)
                {
                    int fieldOffset = _field == 1 ? 0 : SamplesPerLine * LinesPerField;
                    int line = _field == 1 ? _field1Lines : _field2Lines;
                    data.Slice(index, count)
                        .CopyTo(_frame.AsSpan(fieldOffset + line * SamplesPerLine + _lineOffset));
                }
                index += count;
                _lineOffset += count;
                if (_lineOffset == SamplesPerLine) FinishLine();
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
            case 0x20:
                BeginLine(1);
                return State.ReadLine;
            case 0x60:
                BeginLine(2);
                return State.ReadLine;
            default:
                return code == 0xff ? State.SeekZero1 : State.SeekFf;
        }
    }

    private void BeginLine(int field)
    {
        if (field == 1 && _field != 1)
        {
            // A new first field is also a safe resynchronization point.
            _field1Lines = 0;
            _field2Lines = 0;
        }
        _field = field;
        _lineOffset = 0;
        _keepLine = (field == 1 ? _field1Lines : _field2Lines) < LinesPerField;
    }

    private void FinishLine()
    {
        if (_keepLine)
        {
            if (_field == 1) _field1Lines++;
            else _field2Lines++;

            if (_field == 2 && _field1Lines == LinesPerField && _field2Lines == LinesPerField)
            {
                byte[] completed = _frame;
                _frame = new byte[FrameSize];
                _frameCompleted(completed);
                // Further field-two lines are discarded until the next first field.
            }
        }
        _lineOffset = 0;
        _keepLine = false;
        _state = State.SeekFf;
    }
}
