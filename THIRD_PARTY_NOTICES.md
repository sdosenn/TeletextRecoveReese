# Third-party notices

## vhs-teletext VHS pattern tables

TeletextRecoveReese includes the following trained VHS signal-pattern tables
from Alistair Buxton's `vhs-teletext` project:

- `TeletextRecoveReese.Core/Assets/VbiPatterns/vhs/full.dat`
- `TeletextRecoveReese.Core/Assets/VbiPatterns/vhs/parity.dat`
- `TeletextRecoveReese.Core/Assets/VbiPatterns/vhs/hamming.dat`

The `observed_crifc` reference waveform from `teletext/vbi/config.py` is also
included in `TeletextRecoveReese.Core/VbiPatternResources.cs` for signal
alignment.

The OpenCL correlation and staged minimum-reduction design in
`TeletextRecoveReese.Core/OpenClVhsPatternMatcher.cs` is adapted from
`teletext/vbi/patternopencl.py`, copyright © 2023 Dr. David Alan Gilbert,
which is based on Alistair Buxton's CUDA implementation.

Source: <https://github.com/ali1234/vhs-teletext>

Source revision: `f470629a3d5d3b0a577153832505e187beeb7204`

Copyright © 2016 Alistair Buxton and vhs-teletext contributors.

These files are distributed under the GNU General Public License, version 3
or, at your option, any later version. TeletextRecoveReese is distributed under
the GNU General Public License, version 3. See `LICENSE` in the repository root.

## Linux cx231xx and cx25840 drivers

The usermode August VGB100 driver in `TeletextRecoveReese.Core/Vgb100/` is
ported from the Linux kernel `cx231xx` USB bridge driver
(`drivers/media/usb/cx231xx`) and the `cx25840` A/V decoder driver
(`drivers/media/i2c/cx25840`), including the August VGB100 board profile.

Copyright © 2008 Srinivasa Deevi, Conexant Systems Inc., Mauro Carvalho
Chehab, Hans Verkuil and the Linux kernel contributors.

These sources are distributed under the GNU General Public License, version 2
or, at your option, any later version, which permits their use under version 3.

## libusb

Release packages for Windows and macOS include the `libusb-1.0` shared library,
used unmodified by the usermode VGB100 driver.

Source: <https://github.com/libusb/libusb>

libusb is distributed under the GNU Lesser General Public License, version 2.1
or, at your option, any later version.
