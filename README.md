# Drumless Play

Aplicación móvil de playlists mixtas para Android e iOS. Una misma cola puede
intercalar archivos de audio del teléfono y vídeos de YouTube.

## Qué incluye el MVP

- Varias playlists persistentes.
- Creación, renombrado y borrado de playlists.
- Importación múltiple de MP3, WAV, M4A, AAC, FLAC, OGG, OPUS y AIFF mediante
  el selector de archivos del sistema.
- Adición de enlaces `youtube.com`, `youtu.be`, Shorts, Live y YouTube Music.
- Importación completa de playlists de YouTube pegando su enlace: conserva el
  orden original, obtiene el título real de cada vídeo y omite vídeos que ya
  estén en la playlist activa.
- Modo de reproducción sin controles editoriales en las filas y modo de
  selección múltiple activable con **Seleccionar** o manteniendo pulsada una
  pista. En edición aparecen las casillas, **Seleccionar todos**, un único menú
  de acciones y **Hecho**, sin tapar los títulos.
- Modos de reproducción individual, secuencial y aleatorio sin repetición.
- Selector visible de modo: **Una pista** se detiene al finalizar, **En orden**
  avanza por la lista y **Aleatorio** recorre las pistas restantes sin repetir.
- Transporte anterior, reproducir/pausar y siguiente.
- Avance automático al finalizar una pista local o un vídeo.
- Detección automática del tempo de los archivos locales y ajuste manual del
  BPM y del primer pulso.
- Tempo de YouTube mediante pulsación rítmica (`tap tempo`) o entrada manual,
  manteniendo el vídeo en el reproductor oficial.
- Recepción de golpes desde una batería USB MIDI en Android y evaluación contra
  la rejilla de semicorcheas: adelantado, a tiempo o retrasado.
- Compensación configurable del retardo de escucha, pensada especialmente para
  auriculares o altavoces Bluetooth.
- Historial de evaluaciones guardado por pista.
- Persistencia JSON local y copia privada de los audios elegidos, para que la
  playlist siga funcionando aunque el proveedor de archivos retire el permiso
  temporal concedido por Android o iOS.
- Pausa automática al abandonar la aplicación.

## Arquitectura

- `Drumless.Mobile.Core`: modelo, edición de playlists, navegación, parser de
  YouTube y persistencia. No depende de MAUI.
- `Drumless.Mobile`: interfaz .NET MAUI y coordinación de los reproductores.
- `Platforms/Android` y `Platforms/iOS`: reproducción de audio local con las
  APIs nativas de cada sistema.
- `Resources/Raw/wwwroot`: reproductor oficial YouTube IFrame, comunicado con
  C# mediante `HybridWebView`.
- `Drumless.Mobile.Core.Tests`: pruebas del comportamiento portable.

No se descarga, extrae ni procesa el audio de YouTube. El vídeo se reproduce en
el reproductor oficial visible y se pausa cuando la aplicación deja de estar en
primer plano.

Para importar una playlist de YouTube, pulsa **YouTube vídeo/lista** y pega un
enlace que contenga `list=…`. El reproductor oficial obtiene los identificadores
de los vídeos y la aplicación consulta sus metadatos públicos en YouTube antes
de incorporarlos. Los títulos se conservan en la biblioteca para no repetir
consultas. Los vídeos privados, eliminados o sin metadatos disponibles se
identifican por su ID, sin impedir que se importe el resto de la playlist.

Al abrir una biblioteca creada con versiones anteriores, los nombres
automáticos del tipo `YouTube 01 · ID` se sustituyen en segundo plano por el
título real. Cualquier nombre escrito manualmente se conserva.

## Tempo y evaluación MIDI

En una pista local, pulsa **Analizar** para estimar el BPM y el primer pulso a
partir del audio. En un vídeo de YouTube, inicia la reproducción y pulsa
**Tap pulso** al menos cuatro veces siguiendo el ritmo. En ambos casos puedes
escribir el BPM o marcar manualmente el primer pulso para corregir la rejilla.

Conecta la batería al teléfono mediante USB y pulsa **Conectar MIDI**. Al iniciar
**Evaluar**, cada `Note On` se compara con la semicorchea más cercana. La
tolerancia para considerarlo a tiempo es de ±45 ms.

Si escuchas la pista por Bluetooth, configura una compensación positiva:
150 ms es un punto de partida razonable, pero conviene calibrarla para el
teléfono, los auriculares y el códec concretos. Esta compensación afecta a la
evaluación, no intenta eliminar el retardo físico de Bluetooth.

La captura USB MIDI está implementada actualmente en Android. La aplicación iOS
compila y puede analizar pistas, pero la conexión Core MIDI queda pendiente.

## Requisitos

- .NET SDK 10.
- Workload `android` para Android.
- Workload `ios` y un Mac conectado para firmar/instalar en un dispositivo iOS.
- Android SDK para generar o instalar el APK.

Comprobar los workloads:

```powershell
dotnet workload list
```

## Compilar Android

Desde la raíz del repositorio:

```powershell
dotnet restore mobile\Drumless.Mobile\Drumless.Mobile.csproj
dotnet build mobile\Drumless.Mobile\Drumless.Mobile.csproj `
  -f net10.0-android -c Debug
```

El APK de desarrollo firmado queda en:

```text
mobile\Drumless.Mobile\bin\Debug\net10.0-android\com.drumless.play-Signed.apk
```

Instalar con un teléfono conectado y depuración USB habilitada:

```powershell
adb install -r mobile\Drumless.Mobile\bin\Debug\net10.0-android\com.drumless.play-Signed.apk
```

## Compilar iOS

La comprobación de código puede ejecutarse desde Windows:

```powershell
dotnet build mobile\Drumless.Mobile\Drumless.Mobile.csproj `
  -f net10.0-ios -r iossimulator-x64
```

Para producir, firmar y distribuir una aplicación iOS hace falta Xcode en un Mac
y una identidad de Apple Developer.

## Pruebas

```powershell
dotnet test mobile\Drumless.Mobile.Core.Tests\Drumless.Mobile.Core.Tests.csproj
```

## Datos y privacidad

La biblioteca se guarda en `FileSystem.AppDataDirectory/library.json`. Los
archivos escogidos se copian al subdirectorio privado `audio`; los originales no
se mueven ni se borran. Al quitar la última referencia a una copia privada, la
aplicación elimina únicamente esa copia.

El reproductor de YouTube necesita conexión y puede mostrar anuncios,
consentimiento, restricciones regionales o peticiones de inicio de sesión de
YouTube. Algunos vídeos no permiten reproducción integrada. El inicio automático
puede ser bloqueado por el sistema; en ese caso la aplicación pide pulsar el
botón de reproducción del reproductor oficial.
