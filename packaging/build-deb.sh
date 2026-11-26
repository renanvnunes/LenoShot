#!/usr/bin/env bash
# Gera o pacote .deb do LenoShot (amd64) em dist/.
# Uso: packaging/build-deb.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/LenoShot/LenoShot.csproj"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT")"
ARCH="amd64"
PKG="lenoshot_${VERSION}_${ARCH}"
BUILD="$ROOT/dist/build/$PKG"
OUT="$ROOT/dist/$PKG.deb"

echo "==> LenoShot $VERSION"
rm -rf "$BUILD"
mkdir -p "$BUILD/DEBIAN" "$BUILD/usr/lib/LenoShot" "$BUILD/usr/bin" \
         "$BUILD/usr/share/applications" "$BUILD/usr/share/icons/hicolor" "$BUILD/usr/share/doc/LenoShot"

echo "==> Compilando (Native AOT)"
dotnet publish "$PROJECT" -c Release -r linux-x64 -o "$ROOT/dist/publish" --nologo -v quiet

# Binário e bibliotecas nativas (sem os símbolos de depuração .dbg)
install -m 755 "$ROOT/dist/publish/LenoShot" "$BUILD/usr/lib/LenoShot/LenoShot"
install -m 644 "$ROOT/dist/publish/"*.so "$BUILD/usr/lib/LenoShot/"
ln -s ../lib/LenoShot/LenoShot "$BUILD/usr/bin/LenoShot"

echo "==> Gerando ícones"
"$ROOT/dist/publish/LenoShot" --export-icons "$BUILD/usr/share/icons/hicolor"

cat > "$BUILD/usr/share/applications/LenoShot.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=LenoShot
GenericName=Captura de tela
Comment=Capture, anote e compartilhe a tela
Exec=/usr/bin/LenoShot settings
Icon=LenoShot
Terminal=false
Categories=Utility;Graphics;
Keywords=screenshot;print;captura;tela;
StartupWMClass=LenoShot
Actions=capture;delay;

[Desktop Action capture]
Name=Capturar área
Exec=/usr/bin/LenoShot capture

[Desktop Action delay]
Name=Capturar em 5 segundos
Exec=/usr/bin/LenoShot capture --delay 5
DESKTOP

# Licença no formato de copyright do Debian
{
  echo "Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/"
  echo "Upstream-Name: LenoShot"
  echo
  echo "Files: *"
  echo "Copyright: 2026 Renan Nunes"
  echo "License: MIT"
  sed -n '3,$p' "$ROOT/LICENSE" | sed 's/^$/./; s/^/ /'
} > "$BUILD/usr/share/doc/LenoShot/copyright"

SIZE_KB="$(du -sk "$BUILD/usr" | cut -f1)"
cat > "$BUILD/DEBIAN/control" <<CONTROL
Package: lenoshot
Version: $VERSION
Architecture: $ARCH
Maintainer: Renan Nunes <renan@lenoshot.invalid>
Installed-Size: $SIZE_KB
Depends: ffmpeg, pulseaudio-utils, libc6, libx11-6, libxtst6, libxinerama1, libxrandr2, libxi6, libxcursor1, libice6, libsm6, libfontconfig1, zlib1g
Recommends: libnotify-bin, libglib2.0-bin
Section: graphics
Priority: optional
Homepage: https://github.com
Description: Captura de tela minimalista para Linux e Windows
 Captura uma área da tela em um ou mais monitores, com anotações (caneta,
 setas, formas, texto, marcadores numerados, pixelização), captura com
 atraso e captura com rolagem para páginas longas.
CONTROL

# Permissões padrão do Debian (independente do umask): 755 diretórios/executáveis, 644 arquivos
chmod -R u=rwX,go=rX "$BUILD"
chmod 755 "$BUILD/usr/lib/LenoShot/LenoShot"

echo "==> Empacotando"
dpkg-deb --root-owner-group --build "$BUILD" "$OUT" >/dev/null
echo "==> Pronto: $OUT ($(du -h "$OUT" | cut -f1))"
