# Aronium Bridge

Aronium Bridge is a Windows utility designed to bridge the **Aronium POS Customer Display** output to physical **PDLED8 Customer Displays** using virtual COM ports.

It simplifies the complex task of setting up `com0com` virtual port pairs, ensuring that data sent by Aronium to a virtual port is seamlessly forwarded to your physical hardware via RS232.

![System Check Screenshot](DiagnosticScreenshot.png) *(Placeholder for your screenshot)*

## 🚀 Features

-   **Automatic Virtual Port Management**: Handles `com0com` download, installation, and port pair creation directly from the app.
-   **Comprehensive Diagnostics**: Real-time checks for driver status, port visibility, and registry configuration to help you troubleshoot setup issues instantly.
-   **Modern System Tray Integration**: Runs quietly in the background with a status-aware tray icon.
-   **Hardware Compatibility**: Specifically optimized for PDLED8 displays with customizable baud rates and protocol options.
-   **Single Instance Guard**: Prevents multiple copies from running and clashing over COM ports.

## 🛠️ Prerequisites

-   **Operating System**: Windows 10 or 11.
-   **Runtime**: [.NET 10.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
-   **Hardware**: A physical COM port (or USB-to-Serial adapter) connected to a PDLED8 display.

## 📥 Installation

1.  **Download** the latest release from the [Releases](#) page.
2.  **Extract** the ZIP to a folder of your choice.
3.  **Run** `AroniumBridge.exe`.

## ⚙️ Configuration

1.  **System Check**: On the first run, the app will perform a diagnostic. If `com0com` is missing, click **Install com0com**.
2.  **Port Pair**: Create a virtual pair (e.g., `COM3` ↔ `COM4`). Aronium will send data to `COM4`, and the Bridge will read from `COM3`.
3.  **Settings**:
    -   **Virtual Port**: Select the Aronium-side port (e.g., `COM3`).
    -   **Physical Port**: Select the port where your display is connected (e.g., `COM1`).
    -   **Display Model**: Choose the PDLED8 protocol.
4.  **Save & Start**: Once configured, the tray icon will turn green (Connected).

## 📂 Project Structure

The codebase is organized following clean architecture principles for easy maintainability:

-   **/Models**: Data structures for `AppSettings` and `DisplayModel`.
-   **/Services**:
    -   `VirtualPortService`: Handles the virtual side of the bridge.
    -   `HardwareService`: Manages physical RS232 communication.
    -   `Com0ComService`: Handles the installation and management of virtual drivers.
-   **/UI**: WPF Windows, Dialogs, and custom Markup Extensions for the diagnostic engine.
-   **/Scripts**: Utility scripts and build helpers.

## 📜 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

-   Uses [com0com](http://com0com.sourceforge.net/) for virtual serial port emulation.
-   Built for the [Aronium POS](https://www.aronium.com/) community.
