# Vhui USB Server

Version 0.3.1 is a single executable: copy only `Vhui.Server.exe`.
.NET and both user guides are embedded; no separate .NET installation is required.
Open the built-in guide using **Help → User guide**.

Select a device, click **Share device**, then **Start**. Windows may request
administrator privileges to bind the USB device to WinUSB temporarily.
Connect from the VirtualHere client using this computer's address and port 7575.
In **Settings → General**, use `0.0.0.0` as the listen address for other computers;
the default `127.0.0.1` accepts connections from this computer only.
Leave Allowed IPs / CIDR blank or enter `*` to allow all addresses. A specific IP
or subnet limits access, for example `192.168.0.190` or `192.168.0.0/24`.

Choose **Language / Язык → English** or **Русский** to change language immediately.
The choice is saved and does not restart the server.

To rename a USB device, select it and click **Rename…**, or use its right-click menu.
The new name is saved, shown in the server window and sent to VirtualHere clients.
A serial-numbered device keeps its alias when moved to another USB port.
Devices without a serial number are identified by their USB port and model.
Leave the name blank or use **Reset to original** to restore the default name.
Names support Unicode and must fit in 63 UTF-8 bytes. Hardware identifiers and USB
descriptors stay unchanged. A client-side nickname may override the server's name.

**Unshare device** removes the device at the next scan (normally within two seconds).
**Stop** releases clients and restores local device drivers. Closing the window
stops the server. **View → Minimize to tray** keeps it running; double-click the
tray icon to restore the window.

Settings include TCP/TLS ports, certificates, network discovery and reverse
connections. A server certificate enables TLS-only mode, normally on port 7574.
Saving connection settings restarts a running server. Renaming devices or changing
the UI language does not restart it.

Configuration is saved in `%LOCALAPPDATA%\Vhui\Server\server.ini`.
Use `--config C:\path\server.ini` for a different file, or `--start` to start the
server when opening the window. Device aliases use the `DeviceAliases` INI entry;
the language uses `DesktopLanguage=ru` or `en`.

The GUI hosts its own server. An installed Windows service is managed separately
through the CLI. EasyFind and license checks are excluded.
