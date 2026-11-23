# Восстановление совместимости

Оригинал: VirtualHere USB Server 4.8.8. Аппаратные проверки: клиент 6.0.3,
Windows 10 x64. EasyFind и проверки лицензии исключены по заданию.

Добавлен [графический сервер](GUI.ru.md): `Vhui.Server.exe`, дерево устройств,
запуск/остановка, включение доступа, настройки, журнал и трей. Протокол общий с CLI.
В GUI 0.3.0 доступны русский/английский языки и сохраняемые пользовательские имена
USB-устройств. Имена передаются в перечислении, захвате и обновлениях состояния;
проверено отображение в штатном клиенте 6.0.3 после перезапуска обоих приложений.

## Состояние плана

| Этап | Реализовано | Проверка / границы |
| --- | --- | --- |
| USB | Настоящий port status, раздельные reset/clear-halt/abort, исключения ISO, составной reset, composite endpoint routing | 13 выбранных ответов совпадают с оригиналом побайтно, включая ошибки |
| Устройства | Общий реестр/владелец, уникальные адреса разных хабов, busy, рассылки, опрос hotplug, cleanup и recovery | Две протокольные сессии и моделирование hotplug; реальный аварийный выход и восстановление камеры |
| Конфигурация | INI, окружение, VID/PID/serial, IP/CIDR, hooks, ClientAuthorization/Deauthorization, пароль устройства 0x42 | Проверены разбор/сохранение, фильтры и нативные коды выхода авторизации; администрирование перенесено частично |
| Сеть | TCP, TLS, клиентские сертификаты, reverse TCP/TLS, Windows mDNS | Штатный клиент 6.0.3 получил устройства через TLS, mTLS, reverse TCP/TLS и обнаружил сервер через mDNS |
| Windows/backend | SCM-служба, recovery журнала, extended reset 0x33, нативные отказы allocate/free streams | SCM проверена под LocalSystem; vhfilter и часть дополнительных команд ещё не перенесены |

Это рабочий сервер для проверенных сценариев, но пока не полная замена всех
функций оригинала. Работа камеры не доказывает поддержку произвольного USB-класса.

## Запуск

Скопируйте server.example.ini в server.ini. Из терминала администратора:

```powershell
.\Vhui.Recovery.exe serve .\server.ini
```

Пример слушает 127.0.0.1 и публикует камеру 145F:02E7.
Для LAN измените NetworkInterface на 0.0.0.0, задайте свой AllowedClients
(например 192.168.1.0/24), при необходимости UseAVAHI=1.
Правила Firewall автоматически не создаются.

| Параметр | Назначение |
| --- | --- |
| ServerName, ServerId | Имя и UUID; по умолчанию ID зависит от имени ПК |
| TCPPort, NetworkInterface | TCP-порт / bind; default 7575 / 127.0.0.1 |
| AllowedClients | IP, CIDR или `*`, через запятую |
| AllowedDevices, IgnoredDevices | VID/PID, VID/*, VID/PID/SERIAL; VID:PID также допустим |
| IgnoredBuses | Сопоставление с Windows-путём хаба/порта; не нативная таблица числовых bus ID |
| ScanInterval | Опрос USB, default 2 секунды |
| PingInterval, PingTimeout | Keepalive, default 4 / 9 секунд |
| TraceMsgs | JSON; пароль и данные pipe/ISO не выводятся в Base64 |
| HideClientInfo | Скрытие IP владельца в информации об устройстве |
| RemoteAdmin, AdminAllowedClients | Ограниченный административный режим и отдельный IP/CIDR-список |
| DeviceAddress.* | Сохраняемые сетевые адреса; физический Port не подменяется |

INI читается при старте. Перед ручным редактированием остановите сервер.
При сохранении INI нормализуется, комментарии не сохраняются.
Форматы DeviceIdMap/DeviceNicknames и числовых IgnoredBuses оригинала полностью
не восстановлены. Виртуальные хабы клиента VirtualHere исключены из физической
инвентаризации, чтобы loopback не создавал рекурсивный экспорт.

## TLS и reverse

```ini
SSLCert=certs/server.pem
SSLKeyFile=certs/server.key
SSLPort=7574
SSLUseClientCerts=1
SSLCAFile=certs/ca.pem
```

PEM с отдельным/совмещённым ключом и PFX/P12 поддерживаются.
SSLCertPassword задаёт пароль. Пути считаются от каталога INI.
SSLCert включает **TLS-only**: обычный TCP listener отключён.
Так mTLS нельзя обойти через открытый plaintext-порт.

Без TLS: ReverseClients=host[:port], default клиентский порт 7573.
С TLS: SSLReverseClients=host[:port], default 7572.
ReverseClientsRetryPeriod задаёт повторы, default 15 секунд.
Роль TLS остаётся серверной при исходящем TCP-соединении.

В конфигурации штатного клиента используйте пути сертификатов с `/`, например
`SSLCAFile=C:/certs/ca.pem`: его INI-парсер обрабатывает обратные слеши как escape.
Настройка клиента описана в [официальной инструкции TLS](https://www.virtualhere.com/ssl_setup),
управление reverse — в [Client API](https://www.virtualhere.com/client_api).
В штатном клиенте CA и клиентский PEM задаются через SSLCAFile/SSLClientCert:
[официальная инструкция](https://www.virtualhere.com/ssl_setup).

CRL пока не реализован: SSLCRLFile и SSLUseCRL завершают запуск явной ошибкой.
RemoteAdminPassword также отвергается при включённом RemoteAdmin: соответствующий
нативный механизм ещё не восстановлен. Эти политики не игнорируются молча.

## Hooks и доступ

Подключены ClientAuthorization, ClientDeauthorization, onClientConnect,
onClientDisconnect, onBind, onUnbind, onReset, onResetEp, onClearHalt.
Приоритет device hook: event.vid.pid, встроенное исключение, event.vid, event.
Пустая настройка считается обработанным hook.

Нативные коды выхода ClientAuthorization: **0 — отказ (−4), 1 — разрешение**,
2/3 — ответы −7/−8 для предоставления учётных данных, прочее — ошибка.
Программе доступны VH_CLIENT_IP, VH_PASSWORD, VH_VENDOR_ID, VH_PRODUCT_ID,
VH_PRODUCT_SERIAL, VH_DEVPATH и другие переменные окружения.
Текстовые `$…$`-подстановки с удалёнными shell-метасимволами отвергаются;
для таких данных используйте переменные окружения в программе hook.
HookTimeout ограничивает выполнение, default 30 секунд.

CLIENT_ID/CLIENT_HOSTNAME сейчас основаны на UUID соединения/IP; полный разбор
идентификации клиента ещё не восстановлен. power_cycle_port возвращает −95,
вместо ложного успеха запуска несуществующей команды.
Система OnDeviceIgnore/OnDeviceKick/OnAddReverseClient перенесена не полностью.
Проверок лицензии перед hooks нет.

## Служба Windows

Из опубликованной папки, от администратора:

```powershell
.\Vhui.Recovery.exe service-install C:\vhui\server.ini
.\Vhui.Recovery.exe service-start
.\Vhui.Recovery.exe service-status
.\Vhui.Recovery.exe service-stop
.\Vhui.Recovery.exe service-uninstall
```

Имя — VhuiRecovery, учётная запись — LocalSystem, автозапуск.
Установка сама не запускает службу. LogFile задаёт журнал;
default vhui-service.log рядом с INI. Не перемещайте папку работающей службы.

До смены драйвера записывается usb-leases/*.json рядом с EXE.
При старте восстанавливаются записи, чей процесс-владелец завершился.
Живой владелец не перехватывается. Ручное восстановление: restore-usb <journal.json>.
Windows выбирает совместимый драйвер; точный возврат произвольного стороннего INF
для любого устройства пока не гарантируется.

## Проверки

В дереве исходников сохранены результаты с SHA-256 проверявшегося Vhui.Core.dll:

- dotnet/artifacts/protocol-compatibility/result.json: 13 совпавших ответов,
  включая 0x33, ошибки endpoint/descriptor и allocate/free streams;
- dotnet/artifacts/usb-client-test/result.json: 1490 кадров за 120.06 секунды,
  11 023 112 байт видео и 10 аудиосэмплов с камеры; драйвер восстановлен;
- dotnet/artifacts/service-discovery/result.json: SCM и AutoFind штатного клиента;
- dotnet/artifacts/native-tls/result.json: штатный клиент 6.0.3, TLS, обязательный
  клиентский сертификат, reverse TLS и reverse TCP; проверялось получение устройств,
  без их захвата и передачи USB по TLS;
- dotnet/artifacts/crash-recovery/result.json: принудительное завершение тестового
  сервера, восстановленный journal и кадр физической камеры после рестарта;
- dotnet/artifacts/republish-client/result.json: три полных цикла захвата камеры,
  чтения кадра через штатный клиент 6.0.3, снятия доступа и повторной публикации.
  Все три кадра получены с виртуального устройства; штатный драйвер восстановлен;
- dotnet/scripts/Test-AudioOutputClient.ps1: отдельная проверка нулевых PCM на
  USB-аудиоустройстве 4C4A:4155; аппаратный результат OUT пока не подтверждён.

Сборка/tests: powershell -File dotnet/Build.ps1 -Publish — 39 проверок.
Полный сценарий: `powershell -File dotnet/scripts/Test-AllCompatibility.ps1` из
PowerShell с правами администратора через обычный UAC. Он использует камеру
145F:02E7 и аудиоустройство 4C4A:4155; `-SkipAudioOutput` пропускает последнее.
Сценарий временно захватывает выбранные устройства и восстанавливает драйверы.
Есть тесты подменяемого backend, двух TCP-сессий, hotplug, TLS/mTLS, reverse,
endpoint routing и ошибок. Физическое выдёргивание кабеля, разные ПК и bulk OUT
пока не проверены. Опрос может пропустить отключение короче ScanInterval.

После неудачного возврата драйвера (`RestoreFailed`) повторная попытка выполняется
при следующем опросе, даже если процесс сервера продолжает работать. Смена драйверов
и восстановление сериализованы внутри процесса. Ошибка отсутствующего устройства
журналируется как `UsbRecoveryFailed` и не останавливает остальные экспорты.
Сценарий повторного появления проверен на подменяемом восстановителе; физическое
переподключение кабеля ещё требует аппаратной проверки.

## Оставшиеся возможности

Не перенесены vhfilter, физические USB-хабы, питание портов, COM Name Arbiter,
полный административный протокол и часть событий. Из администрирования есть имя
сервера, безопасная ветка custom event, чтение reverse-списка/исключений и время
привязки. Неизвестные команды журналируются и пропускаются, соединение сохраняется.
Непустой custom event нельзя установить по сети, как и в оригинале.

0x46/0x48 возвращают нативный Windows-отказ −5; bulk streams не реализованы.
LZ4 принимается, исходящие кадры пока несжатые. EasyFind и лицензирование исключены;
GUI/tray доступны в отдельном проекте Vhui.Server.
Основания дополнительных команд: analysis/EXTENDED_RESET.ru.md в исходниках.

API: [WinUSB control routing](https://learn.microsoft.com/en-us/windows/win32/api/winusb/nf-winusb-winusb_controltransfer),
[DNS-SD](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsserviceregister),
[SCM](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicectrldispatcherw).
