#!/usr/bin/env bash
# Gera o pacote .deb do Leno Shot (amd64) em dist/.
# Uso: packaging/build-deb.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/LenoShot/LenoShot.csproj"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT")"
ARCH="amd64"
PKG="leno-shot_${VERSION}_${ARCH}"
BUILD="$ROOT/dist/build/$PKG"
OUT="$ROOT/dist/$PKG.deb"

echo "==> Leno Shot $VERSION"
rm -rf "$BUILD"
mkdir -p "$BUILD/DEBIAN" "$BUILD/usr/lib/leno-shot" "$BUILD/usr/bin" \
         "$BUILD/usr/share/applications" "$BUILD/usr/share/icons/hicolor" "$BUILD/usr/share/doc/leno-shot"

echo "==> Compilando (Native AOT)"
dotnet publish "$PROJECT" -c Release -r linux-x64 -o "$ROOT/dist/publish" --nologo -v quiet

# Binário e bibliotecas nativas (sem os símbolos de depuração .dbg)
install -m 755 "$ROOT/dist/publish/leno-shot" "$BUILD/usr/lib/leno-shot/leno-shot"
install -m 644 "$ROOT/dist/publish/"*.so "$BUILD/usr/lib/leno-shot/"
ln -s ../lib/leno-shot/leno-shot "$BUILD/usr/bin/leno-shot"

echo "==> Gerando ícones"
"$ROOT/dist/publish/leno-shot" --export-icons "$BUILD/usr/share/icons/hicolor"

cat > "$BUILD/usr/share/applications/leno-shot.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Leno Shot
GenericName=Captura de tela
Comment=Capture, anote e compartilhe a tela
Exec=/usr/bin/leno-shot settings
Icon=leno-shot
Terminal=false
Categories=Utility;Graphics;
Keywords=screenshot;print;captura;tela;
StartupWMClass=leno-shot
Actions=capture;delay;

[Desktop Action capture]
Name=Capturar área
Exec=/usr/bin/leno-shot capture

[Desktop Action delay]
Name=Capturar em 5 segundos
Exec=/usr/bin/leno-shot capture --delay 5
DESKTOP

# Licença no formato de copyright do Debian
{
  echo "Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/"
  echo "Upstream-Name: Leno Shot"
  echo
  echo "Files: *"
  echo "Copyright: 2026 Renan Nunes"
  echo "License: MIT"
  sed -n '3,$p' "$ROOT/LICENSE" | sed 's/^$/./; s/^/ /'
} > "$BUILD/usr/share/doc/leno-shot/copyright"

SIZE_KB="$(du -sk "$BUILD/usr" | cut -f1)"
cat > "$BUILD/DEBIAN/control" <<CONTROL
Package: leno-shot
Version: $VERSION
Architecture: $ARCH
Maintainer: Renan Nunes <renan@leno-shot.invalid>
Installed-Size: $SIZE_KB
Depends: ffmpeg, pulseaudio-utils, libc6, libx11-6, libxtst6, libxinerama1, libxrandr2, libxi6, libxcursor1, libice6, libsm6, libfontconfig1, zlib1g
Recommends: libnotify-bin, libglib2.0-bin
Section: graphics
Priority: optional
Homepage: https://github.com
Description: Captura de tela minimalista para Linux
 Captura uma área da tela em um ou mais monitores, com anotações (caneta,
 setas, formas, texto, marcadores numerados, pixelização), captura com
 atraso e captura com rolagem para páginas longas.
CONTROL

# Permissões padrão do Debian (independente do umask): 755 diretórios/executáveis, 644 arquivos
chmod -R u=rwX,go=rX "$BUILD"
chmod 755 "$BUILD/usr/lib/leno-shot/leno-shot"

echo "==> Empacotando"
dpkg-deb --root-owner-group --build "$BUILD" "$OUT" >/dev/null
echo "==> Pronto: $OUT ($(du -h "$OUT" | cut -f1))"
