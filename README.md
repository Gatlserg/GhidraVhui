# C# / .NET: Vhui

USB-сервер для Windows x64 на C# / .NET 10 с графическим интерфейсом
и поддержкой клиента VirtualHere. В репозитории находятся все исходники
библиотеки, GUI, CLI, тесты, скрипты сборки и документация.

**Один переносимый EXE 0.3.1 после сборки:** `artifacts/Vhui.Server.exe`.
`Build.ps1 -Publish` создаёт его со встроенными .NET и руководствами;
результат публикации — `artifacts/server/single-file/win-x64/Vhui.Server.exe`.
Дерево устройств, запуск/остановка, настройки, журнал и трей:
[руководство GUI](docs/GUI.ru.md). Сборка `Build.ps1 -Publish` публикует GUI и CLI.
В версии 0.3.0 добавлены русский/английский языки интерфейса и сохраняемые имена
устройств, которые отображаются и в штатном клиенте VirtualHere.

Текущее восстановление протокола, INI, TLS/reverse, mDNS и службы:
[COMPATIBILITY_STATUS.ru.md](docs/COMPATIBILITY_STATUS.ru.md).

Корень репозитория соответствует папке `dotnet` исходного рабочего каталога.
Откройте **`Vhui.sln`** в Visual Studio либо корень репозитория в VS Code.
Для сборки нужен .NET SDK 10. Готовый GUI содержит среду .NET;
для его запуска отдельная установка .NET не требуется. Для захвата USB нужны
права администратора. Ограничения совместимости описаны в документации ниже.

**Как пользоваться готовым EXE:** [руководство пользователя](docs/USER_GUIDE.ru.md).
В нём описаны команды `inspect`, `extract-payload`, `usb-inspect`,
`probe-server`, `serve-discovery`, примеры вывода,
поддерживаемый файл, ошибки и формат результата. При публикации копия
руководства сохраняется как `README.ru.md` рядом с EXE.

| Путь | Назначение |
| --- | --- |
| `src/Vhui.Core/` | LZMA, настройки, USB-дескрипторы, SetupAPI/IOCTL, TCP/LZ4 и информационный сервер |
| `src/Vhui.Recovery/` | CLI анализа EXE, USB и сетевого обмена |
| `src/Vhui.Server/` | Windows Forms: графический USB-сервер |
| `tests/Vhui.Recovery.Tests/` | Проверки и независимые эталоны |
| `scripts/` | Выбор SDK и генерация тестовых данных |
| `Build.ps1` | Сборка, проверки и публикация |
| `Start.ps1` | Запуск инструмента |
| `.tools/` | Локальный SDK и его установщик; исходники сюда не добавляются |
| `artifacts/` | Результаты публикации и извлечения данных |

Папки `bin` и `obj` внутри проектов создаются сборкой автоматически.
Материалы декомпиляции и оригинальные EXE не входят в этот репозиторий.
Для обычной сборки и тестов они не нужны. Ссылки и примеры с `analysis/` в
документации относятся к отдельному каталогу анализа рядом с проектом.
Если `../analysis/recovered/vhusbdwinw64.exe` доступен, `Build.ps1` дополнительно
проверяет декодирование его потока. Сравнительные скрипты с оригинальным клиентом
и сервером требуют соответствующих файлов в этом внешнем каталоге.

## Сборка

Команды ниже выполняются **из корня репозитория**. Нужен .NET SDK 10;
можно использовать установленный SDK либо установить его локально:

```powershell
git clone https://github.com/Gatlserg/GhidraVhui.git
cd GhidraVhui
# Если .NET SDK 10 ещё не установлен:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Setup-DotNet.ps1
```

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
# Собрать Windows x64 со своей средой .NET:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -Publish
```

Результат публикации GUI: **`artifacts/Vhui.Server.exe`** — достаточно перенести
этот файл. CLI: `artifacts/recovery/win-x64/Vhui.Recovery.exe`; для переноса CLI
нужна вся его папка `win-x64`. Сборки, SDK, рабочие настройки, приватные ключи
и журналы восстановления драйверов исключены из Git.

## Запуск

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Start.ps1 inspect ..\analysis\recovered\vhusbdwinw64.exe
# Сохранить подтверждённый префикс LZMA в новый файл:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Start.ps1 extract-payload ..\analysis\recovered\vhusbdwinw64.exe .\artifacts\payload.bin
```

`Start.ps1` принимает пути относительно текущего каталога, поэтому его можно
вызывать и из родительской папки. В VS Code сборка и F5 настроены
для открытия корня репозитория. Конфигурация отладки `inspect source image`
требует оригинальный EXE в отдельном каталоге `../analysis/recovered/`.

Режим `serve-usb` совместим с оригинальным клиентом 6.0.3 для проверенной вебкамеры:
захват через WinUSB, URB, MJPEG-кадр с виртуального устройства, освобождение
и восстановление физической камеры. [Запуск и ограничения](docs/USB_SERVER.ru.md).
Проверка: `scripts/Test-UsbClient.ps1 -VideoProbe` в повышенном PowerShell;
отчёт — `artifacts/usb-client-test/result.json`. `serve-discovery` остаётся
отдельным информационным режимом без захвата.
Описание реализованных возможностей и оставшихся частей — в
[статусе совместимости](docs/COMPATIBILITY_STATUS.ru.md).
