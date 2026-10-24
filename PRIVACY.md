# Evania Privacy Policy

Evania is a local-first app. It has no account system, no analytics, no telemetry and no crash reporting. The developer does not receive any of your data.

## What stays on your device
- Notes, vault entries, bookmarks and settings are stored in `%APPDATA%\Evania` (`settings.json`, `Evania.db`). The database can be encrypted with a password you choose; that password is kept in Windows Credential Manager.
- Screenshots are saved to Pictures\Screenshots and screen recordings to Videos\Captures.
- Screen capture, microphone and camera are used only when you start a capture or recording. OCR runs on the device.
- Evania registers the PrintScreen key as a capture shortcut. It does not record other keystrokes.
- Passwords and one-time codes you copy from the vault are kept out of clipboard history and cloud clipboard, and the clipboard is cleared after 30 seconds.

## Network use
- When you add a web bookmark, or paste a link into a note, Evania requests that page to read its title. The site you choose can see that request.
- The Chat and Web Bookmarks pages show websites in an embedded Microsoft Edge WebView2 browser. Sites you visit there, including the Chat provider (Microsoft Copilot by default), handle your data under their own privacy policies. Cookies and sign-in are stored in `%APPDATA%\Evania\chat-webview`. You can erase them with Settings > Chat > Clear browsing data.

## Deleting your data
Use Clear browsing data in Settings, and delete `%APPDATA%\Evania` to remove everything Evania stores. Remove the "Evania" entries from Windows Credential Manager to delete the saved database password.

## Contact
Questions: adhartmain@live.com
