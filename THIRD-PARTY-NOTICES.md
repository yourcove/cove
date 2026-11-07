# Third-party notices

Cove is licensed under the GNU Affero General Public License v3.0 (see [LICENSE](LICENSE)). It includes the third-party software listed here, which keeps its own licence.

## Image processing: libvips

Cove decodes, resizes and encodes images with [libvips](https://github.com/libvips/libvips) through the [NetVips](https://github.com/kleisauke/net-vips) bindings. Every Cove release and Docker image contains the libvips native library and the libraries it is built with, as packaged by [NetVips.Native](https://github.com/kleisauke/libvips-packaging) 8.18.7.

- **NetVips** 3.2.0, copyright Kleis Auke Wolthuizen, is used under the MIT License.
- **libvips and the libraries below** are used under the licences shown. The libraries under the LGPLv3 are used under it through the "any later version" clause of the LGPLv2 or LGPLv2.1. The texts of the LGPLv3 and of the GPLv3, which it extends, are in [licenses/](licenses/).

| Library | Version | Used under the terms of |
|---|---|---|
| aom | 3.15.1 | BSD 2-Clause + [Alliance for Open Media Patent License 1.0](https://aomedia.org/license/patent-license/) |
| cairo | 1.18.6 | Mozilla Public License 1.1 |
| cgif | 0.5.4 | MIT License |
| expat | 2.8.5 | MIT License |
| fontconfig | 2.18.3 | [fontconfig License](https://gitlab.freedesktop.org/fontconfig/fontconfig/blob/main/COPYING) (BSD-like) |
| freetype | 2.14.3 | [FreeType License](https://git.savannah.gnu.org/cgit/freetype/freetype2.git/tree/docs/FTL.TXT) (BSD-like) |
| fribidi | 1.0.17 | LGPLv3 |
| glib | 2.90.0 | LGPLv3 |
| harfbuzz | 14.5.0 | MIT License |
| highway | 1.4.0 | BSD 3-Clause |
| lcms | 2.19.1 | MIT License |
| libarchive | 3.8.9 | BSD 2-Clause |
| libexif | 0.6.26 | LGPLv3 |
| libffi | 3.8.0 | MIT License |
| libheif | 1.23.5 | LGPLv3 |
| libimagequant | 2.4.1 | [BSD 2-Clause](https://github.com/lovell/libimagequant/blob/main/COPYRIGHT) |
| libnsgif | — | MIT License |
| libpng | 1.6.58 | [libpng License](https://github.com/pnggroup/libpng/blob/master/LICENSE) |
| librsvg | 2.63.2 | LGPLv3 |
| libtiff | 4.7.2 | [libtiff License](https://gitlab.com/libtiff/libtiff/blob/master/LICENSE.md) (BSD-like) |
| libultrahdr | 2.0.2 | MIT License |
| libvips | 8.18.7 | LGPLv3 |
| libwebp | 1.6.0 | New BSD License |
| libxml2 | 2.15.4 | MIT License |
| mozjpeg | 0826579 | [zlib License, IJG License, BSD 3-Clause](https://github.com/mozilla/mozjpeg/blob/master/LICENSE.md) |
| pango | 1.58.2 | LGPLv3 |
| pixman | 0.46.4 | MIT License |
| proxy-libintl | 0.5 | LGPLv3 |
| zlib-ng | 2.3.3 | [zlib License](https://github.com/zlib-ng/zlib-ng/blob/develop/LICENSE.md) |

libnsgif is built into libvips and has no separate version.

### Source code

The source of each library above, at the version listed, is available from its upstream project. The scripts that build these binaries from it, and the exact source each release uses, are published at <https://github.com/kleisauke/libvips-packaging> (tag `v8.18.7`) and <https://github.com/lovell/sharp-libvips>.

### Replacing libvips

libvips is loaded at run time as a shared library: `libvips.so.42` on Linux, `libvips.42.dylib` on macOS and `libvips-42.dll` on Windows. In the Docker images it is a separate file in `/opt/cove/runtimes/<runtime>/native/` and can be replaced with a compatible build. The single-file release executables carry it inside the executable and extract it when they start.
