# UniCast

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/License-Proprietary-red)](LICENSE.txt)

**UniCast** is a professional multi-platform live streaming studio application that enables simultaneous broadcasting to YouTube, TikTok, Instagram, Facebook, and Twitch.

![UniCast Banner](docs/images/banner.png)

## ✨ Features

### 🎥 Multi-Platform Streaming
- **YouTube** - Full RTMP support with chat integration
- **TikTok** - Live streaming with real-time chat
- **Instagram** - Hybrid chat ingestion
- **Facebook** - Live broadcasting with comments
- **Twitch** - RTMP streaming with chat overlay

### 🎬 Professional Tools
- **Hardware Encoding** - NVIDIA NVENC, AMD AMF, Intel QSV support
- **Camera Capture** - Multiple camera sources with preview
- **Screen Capture** - Window and desktop capture
- **Audio Mixing** - Multi-source audio with level monitoring
- **Overlay System** - Custom overlays via Named Pipes

### 💬 Chat Aggregation
- Unified chat from all platforms
- Real-time message display
- Rate limiting and spam protection
- Platform-specific badges and emotes

### 🔒 Licensing
- Secure hardware-based licensing
- Trial and Lifetime license options
- Online validation with offline grace period

## 🏗️ Architecture

```
UniCast/
├── UniCast.App/           # WPF Application (MVVM)
├── UniCast.Core/          # Business Logic & Chat
├── UniCast.Encoder/       # FFmpeg & Hardware Encoding
├── UniCast.Capture/       # Camera & Screen Capture
├── UniCast.Licensing/     # License Management
├── UniCast.Config/        # Configuration
├── UniCast.Overlay/       # Overlay System
├── UniCast.Logging/       # Logging Infrastructure
├── UniCast.LicenseServer/ # License Server API
└── UniCast.Tests/         # Unit & Integration Tests
```

## 🚀 Getting Started

### Prerequisites

- Windows 10/11 (x64)
- .NET 8.0 SDK
- Visual Studio 2022 or JetBrains Rider
- FFmpeg (included in release)

### Building

```bash
# Clone the repository
git clone https://github.com/your-org/unicast.git
cd unicast

# Restore packages
dotnet restore

# Build
dotnet build -c Release

# Run tests
dotnet test
```

### Running

```bash
# Development
dotnet run --project UniCast.App

# Or open UniCast.sln in Visual Studio and press F5
```

## ⚙️ Configuration

### Environment Variables

```bash
# Required for OAuth token refresh
YOUTUBE_CLIENT_ID=your_client_id
YOUTUBE_CLIENT_SECRET=your_client_secret
TWITCH_CLIENT_ID=your_client_id
TWITCH_CLIENT_SECRET=your_client_secret
FACEBOOK_APP_ID=your_app_id
FACEBOOK_APP_SECRET=your_app_secret

# License Server (production)
LICENSE_SERVER_URL=https://your-license-server.com
```

### Settings Location

User settings are stored in:
```
%USERPROFILE%\Documents\UniCast\settings.json
```

Logs are stored in:
```
%USERPROFILE%\Documents\UniCast\Logs\
```

## 🧪 Testing

```bash
# Run all tests
dotnet test

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test class
dotnet test --filter "FullyQualifiedName~StreamControllerTests"
```

### Test Coverage

| Component | Coverage |
|-----------|----------|
| StreamController | ~85% |
| HardwareEncoder | ~80% |
| ChatBus | ~90% |
| LicenseModels | ~95% |
| FrameBufferPool | ~85% |
| SettingsData | ~90% |

## 📦 Dependencies

### Core
- **Serilog** - Structured logging
- **Microsoft.Extensions.DependencyInjection** - IoC container
- **Newtonsoft.Json** - JSON serialization

### Media
- **OpenCvSharp4** - Camera capture
- **NAudio** - Audio processing
- **FFmpeg** - Video encoding/streaming

### UI
- **WPF** - Windows Presentation Foundation
- **MaterialDesignThemes** - Modern UI components

### Testing
- **xUnit** - Test framework
- **Moq** - Mocking library
- **FluentAssertions** - Assertion library

## 🔧 Development

### Code Style

- Follow [C# Coding Conventions](https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Use nullable reference types (`#nullable enable`)
- XML documentation for public APIs
- Async suffix for async methods

### Branching Strategy

- `main` - Production-ready code
- `develop` - Integration branch
- `feature/*` - New features
- `bugfix/*` - Bug fixes
- `release/*` - Release preparation

### Commit Messages

```
type(scope): description

feat(chat): add TikTok chat integration
fix(encoder): resolve NVENC initialization crash
docs(readme): update installation instructions
```

## 📄 License

This software is proprietary. See [LICENSE.txt](LICENSE.txt) for details.

## 🤝 Support

- **Email**: support@unicast.app
- **Documentation**: [docs.unicast.app](https://docs.unicast.app)
- **Issues**: [GitHub Issues](https://github.com/your-org/unicast/issues)

---

Built with ❤️ by the UniCast Team
