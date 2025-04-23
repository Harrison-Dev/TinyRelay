# TinyRelay

一個基於 LiteNetLib 的輕量級網路中繼伺服器，專為 Unity 多人遊戲設計。

## 專案結構

- **TinyRelay.Server**: 中繼伺服器實現
- **TinyRelay.Client**: Unity 客戶端整合（NetworkTransport 實現）
- **TinyRelay.Shared**: 共享程式碼（資料包定義等）
- **External/LiteNetLib**: LiteNetLib 子模組

## 構建要求

- .NET 8.0 SDK
- Unity 2022.3 或更高版本（用於客戶端）
- Unity Netcode for GameObjects 套件

## 如何使用

### 伺服器

1. 複製儲存庫並初始化子模組：
   ```bash
   git clone <repository-url>
   cd TinyRelay
   git submodule update --init --recursive
   ```

2. 構建並執行伺服器：
   ```bash
   dotnet build
   cd TinyRelay.Server/bin/Debug/net8.0
   dotnet TinyRelay.Server.dll
   ```

### Unity 客戶端

1. 在 Unity 專案中添加以下套件：
   - com.unity.netcode.gameobjects
   - com.unity.transport

2. 將以下 DLL 檔案複製到 Unity 專案的 Assets/Plugins 目錄：
   - TinyRelay.Client.dll
   - TinyRelay.Shared.dll
   - LiteNetLib.dll

3. 在 NetworkManager 組件上，將 Transport 設定為 LiteNetLibTransport

4. 設定 LiteNetLibTransport 組件：
   - Address: 中繼伺服器 IP 位址
   - Port: 中繼伺服器連接埠（預設 9050）
   - BaseKey: 連接金鑰（可選）

## 開發說明

- 伺服器使用 .NET 8.0
- 客戶端函式庫使用 .NET Standard 2.1 以確保 Unity 相容性
- 使用 git submodule 方式整合 LiteNetLib 以便於原始碼級偵錯和客製化

## Docker 支援

專案包含 Dockerfile，可以使用以下指令建立和執行容器：

```bash
docker build -t tinyrelay .
docker run -p 9050:9050 tinyrelay
```

## 授權條款

MIT License