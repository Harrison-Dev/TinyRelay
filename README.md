# TinyRelay

一个基于LiteNetLib的轻量级网络中继服务器，专为Unity多人游戏设计。

## 项目结构

- **TinyRelay.Server**: 中继服务器实现
- **TinyRelay.Client**: Unity客户端集成（NetworkTransport实现）
- **TinyRelay.Shared**: 共享代码（数据包定义等）
- **External/LiteNetLib**: LiteNetLib子模块

## 构建要求

- .NET 8.0 SDK
- Unity 2022.3 或更高版本（用于客户端）
- Unity Netcode for GameObjects包

## 如何使用

### 服务器

1. 克隆仓库并初始化子模块：
   ```bash
   git clone <repository-url>
   cd TinyRelay
   git submodule update --init --recursive
   ```

2. 构建并运行服务器：
   ```bash
   dotnet build
   cd TinyRelay.Server/bin/Debug/net8.0
   dotnet TinyRelay.Server.dll
   ```

### Unity客户端

1. 在Unity项目中添加以下包：
   - com.unity.netcode.gameobjects
   - com.unity.transport

2. 将以下DLL文件复制到Unity项目的Assets/Plugins目录：
   - TinyRelay.Client.dll
   - TinyRelay.Shared.dll
   - LiteNetLib.dll

3. 在NetworkManager组件上，将Transport设置为LiteNetLibTransport

4. 配置LiteNetLibTransport组件：
   - Address: 中继服务器IP地址
   - Port: 中继服务器端口（默认9050）
   - BaseKey: 连接密钥（可选）

## 开发说明

- 服务器使用.NET 8.0
- 客户端库使用.NET Standard 2.1以确保Unity兼容性
- 使用git submodule方式集成LiteNetLib以便于源码级调试和定制

## Docker支持

项目包含Dockerfile，可以使用以下命令构建和运行容器：

```bash
docker build -t tinyrelay .
docker run -p 9050:9050 tinyrelay
```

## 许可证

MIT License 